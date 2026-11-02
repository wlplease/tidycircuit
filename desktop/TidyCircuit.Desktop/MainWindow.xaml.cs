using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace TidyCircuit;

public partial class MainWindow : Window
{
    private readonly BulkObservableCollection<FileRow> _rows = new();
    private readonly BulkObservableCollection<FileRow> _visibleRows = new();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _selectedRoot = "";
    private Func<FileRow, bool> _smartFilter = _ => true;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _filterCancellation;
    private readonly ManualResetEventSlim _scanPauseGate = new(true);
    private readonly ManualResetEventSlim _filterPauseGate = new(true);
    private bool _isUpdatingMarks;
    private bool _scanPaused;
    private bool _filterPaused;
    private bool _limitLargestResults;
    private long _filterRequest;
    private string _assistantSummary = "Suggestions use file size, dates, names, and local content hashes. They never decide a file is safe to remove.";
    private const string AskHint = "Try: largest videos, duplicates, old files, or PDFs";
    private const string SearchHint = "Search names, folders, extensions…";
    public MainWindow()
    {
        InitializeComponent(); FilesGrid.ItemsSource = _visibleRows;
        FilesGrid.Sorting += (_, e) => { e.Handled = true; SortRows(); };
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await ApplyCurrentFiltersAsync(); };
        MainTabs.SelectionChanged += (_, _) => SetActiveNavigation();
        Closing += (_, _) =>
        {
            _searchDebounce.Stop();
            _scanCancellation?.Cancel(); _scanPauseGate.Set();
            _filterCancellation?.Cancel(); _filterPauseGate.Set();
        };
        SetActiveNavigation();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose one folder for a local scan", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _selectedRoot = dialog.FolderName;
        SelectedRootText.Text = _selectedRoot;
        await ScanFolderAsync(_selectedRoot);
    }
    private async void ScanAgain_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedRoot)) await ScanFolderAsync(_selectedRoot);
        else ChooseFolder_Click(sender, e);
    }
    private async Task ScanFolderAsync(string root)
    {
        ScanButton.IsEnabled = FolderButton.IsEnabled = false;
        CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = true;
        CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Visible;
        PauseScanButton.Content = "Pause"; _scanPaused = false; _scanPauseGate.Set();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        _filterCancellation?.Cancel(); _visibleRows.Clear(); _rows.Clear(); UpdateMarkedSummary();
        FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "Scanning…";
        UpdateMarkedSummary();
        ProgressText.Text = "Reading file list";
        StatusText.Text = "Scanning selected folder · nothing is changed"; ScanProgress.IsIndeterminate = true;
        try
        {
            var inventoryProgress = new Progress<ScanInventoryProgress>(update =>
            {
                if (!token.IsCancellationRequested && !_scanPaused) ProgressText.Text = $"Reading {update.Files:N0} files · {update.Folders:N0} folders · {update.CurrentFolder}";
            });
            var (files, inventoryErrors) = await Task.Run(() => ReadFiles(root, token, inventoryProgress, _scanPauseGate));
            token.ThrowIfCancellationRequested();
            System.ComponentModel.PropertyChangedEventHandler markedChanged = FileRow_PropertyChanged;
            foreach (var row in files) row.PropertyChanged += markedChanged;
            _rows.AddRange(files.OrderByDescending(f => f.Size));
            FileCountText.Text = files.Count.ToString("N0"); FolderSizeText.Text = Format(files.Sum(f => f.Size));
            ProgressText.Text = "Comparing matching files";
            var hashProgress = new Progress<int>(count => { if (!token.IsCancellationRequested && !_scanPaused) ProgressText.Text = $"Checking files · {count:N0}"; });
            var (dupes, hashErrors) = await Task.Run(() => FindDuplicates(files, token, hashProgress, _scanPauseGate));
            DuplicatesText.Text = dupes.ToString("N0");
            var errors = inventoryErrors + hashErrors;
            StatusText.Text = errors == 0
                ? $"Scan complete · {files.Count:N0} files · matching content checked; files over 256 MB skipped"
                : $"Scan complete with {errors:N0} read issue(s) · some files could not be read";
            await ApplySmartQueryAsync(); UpdateMarkedSummary();
        }
        catch (OperationCanceledException)
        {
            _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "—";
            _smartFilter = _ => true; _limitLargestResults = false; await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = "Scan canceled · no files were changed";
        }
        catch (Exception ex)
        {
            _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "—";
            _smartFilter = _ => true; _limitLargestResults = false; await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = "Scan failed · " + ex.Message;
        }
        finally
        {
            ProgressText.Text = ""; ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = true;
            CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = false;
            CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Collapsed;
            _scanPaused = false; _scanPauseGate.Set();
            _scanCancellation?.Dispose(); _scanCancellation = null;
        }
    }
    private void PauseScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null) return;
        _scanPaused = !_scanPaused;
        if (_scanPaused) { _scanPauseGate.Reset(); PauseScanButton.Content = "Resume"; ProgressText.Text = "Scan paused · resume to continue"; }
        else { _scanPauseGate.Set(); PauseScanButton.Content = "Pause"; ProgressText.Text = "Resuming scan…"; }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e) { _scanCancellation?.Cancel(); _scanPauseGate.Set(); ProgressText.Text = "Stopping scan…"; }
    private static (List<FileRow> Files, int Errors) ReadFiles(string root, CancellationToken token, IProgress<ScanInventoryProgress> progress, ManualResetEventSlim pauseGate)
    {
        var files = new List<FileRow>(); var errors = 0; var folders = 0; var currentFolder = root;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            pauseGate.Wait(token);
            var dir = pending.Pop();
            currentFolder = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(currentFolder)) currentFolder = dir;
            progress.Report(new(files.Count, ++folders, currentFolder));
            try { foreach (var child in Directory.EnumerateDirectories(dir, "*", options)) { token.ThrowIfCancellationRequested(); pauseGate.Wait(token); pending.Push(child); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    pauseGate.Wait(token);
                    try { var info = new FileInfo(path); files.Add(new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = Path.GetRelativePath(root, info.FullName), Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
                    if (files.Count % 500 == 0) progress.Report(new(files.Count, folders, currentFolder));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
        }
        if (!token.IsCancellationRequested) progress.Report(new(files.Count, folders, currentFolder));
        return (files, errors);
    }
    private static (int Duplicates, int Errors) FindDuplicates(List<FileRow> files, CancellationToken token = default, IProgress<int>? progress = null, ManualResetEventSlim? pauseGate = null)
    {
        var count = 0; var errors = 0; var candidate = 0;
        foreach (var sizeGroup in files.Where(f => f.Size >= 0 && f.Size <= 268_435_456).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<FileRow>>();
            foreach (var row in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                try { var hash = HashFile(row.FullPath, token, pauseGate); if (!hashes.TryGetValue(hash, out var matches)) hashes[hash] = matches = new(); matches.Add(row); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { errors++; }
                candidate++;
                if (candidate % 128 == 0) progress?.Report(candidate);
            }
            foreach (var hashGroup in hashes.Values.Where(g => g.Count() > 1))
            {
                foreach (var row in hashGroup) row.Duplicate = true;
                count += hashGroup.Count() - 1;
            }
        }
        progress?.Report(candidate);
        return (count, errors);
    }
    private static string HashFile(string path, CancellationToken token, ManualResetEventSlim? pauseGate)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested(); pauseGate?.Wait(token); hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Format(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    private void SearchBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text == SearchHint) { SearchBox.Text = ""; SearchBox.Foreground = System.Windows.Media.Brushes.DarkSlateGray; }
    }
    private void SearchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) { SearchBox.Text = SearchHint; SearchBox.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#829498")!; }
    }
    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SortRows();
    private void SortRows() => _ = ApplyCurrentFiltersAsync();
    private async Task<List<FileRow>?> ApplyCurrentFiltersAsync()
    {
        if (FilesGrid is null || SearchBox is null) return null;
        _filterCancellation?.Cancel(); _filterPauseGate.Set();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        _filterPaused = false;
        var request = ++_filterRequest;
        var token = cancellation.Token;
        var term = SearchBox.Text.Trim();
        if (term == SearchHint) term = "";
        var smartFilter = _smartFilter;
        var largestOnly = _limitLargestResults;
        var sortIndex = SortBox.SelectedIndex;
        var snapshot = _rows.ToArray();
        if (ReviewCountText is not null) ReviewCountText.Text = "Searching…";
        if (SearchProgressRow is not null) SearchProgressRow.Visibility = Visibility.Visible;
        if (SearchPauseButton is not null) { SearchPauseButton.Content = "Pause"; SearchPauseButton.IsEnabled = true; }
        if (SearchProgressBar is not null) { SearchProgressBar.Value = 0; }
        if (SearchProgressText is not null) SearchProgressText.Text = $"Searching 0 / {snapshot.Length:N0}";
        IProgress<(int Processed, int Total, int Matches)> searchProgress = new Progress<(int Processed, int Total, int Matches)>(progress =>
        {
            if (request != _filterRequest || token.IsCancellationRequested || _filterPaused) return;
            if (SearchProgressBar is not null) SearchProgressBar.Value = progress.Total == 0 ? 100 : progress.Processed * 100d / progress.Total;
            if (SearchProgressText is not null) SearchProgressText.Text = $"Searching {progress.Processed:N0} / {progress.Total:N0} · {progress.Matches:N0} matches";
        });
        try
        {
            var matches = await Task.Run(() =>
            {
                var found = new List<FileRow>();
                for (var index = 0; index < snapshot.Length; index++)
                {
                    token.ThrowIfCancellationRequested(); _filterPauseGate.Wait(token);
                    var row = snapshot[index];
                    if (smartFilter(row) && (term.Length == 0 || row.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase))) found.Add(row);
                    if ((index + 1) % 500 == 0 || index + 1 == snapshot.Length) searchProgress.Report((index + 1, snapshot.Length, found.Count));
                }
                IEnumerable<FileRow> ordered = sortIndex switch
                {
                    1 => found.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
                    2 => found.OrderBy(row => row.Modified),
                    _ => found.OrderByDescending(row => row.Size)
                };
                return largestOnly ? ordered.Take(25).ToList() : ordered.ToList();
            }, token);
            if (token.IsCancellationRequested || request != _filterRequest) return null;
            _visibleRows.ReplaceAll(matches);
            if (SearchProgressBar is not null) SearchProgressBar.Value = 100;
            if (AssistantStatus is not null)
                AssistantStatus.Text = _assistantSummary + (term.Length > 0 ? $" · Search narrows results to {matches.Count:N0}" : "");
            UpdateMarkedSummary();
            return matches;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception)
        {
            if (request == _filterRequest)
            {
                if (AssistantStatus is not null) AssistantStatus.Text = "Search could not finish. Try another term or scan a smaller folder.";
                if (ReviewCountText is not null) ReviewCountText.Text = "Search failed · previous results kept";
            }
            return null;
        }
        finally
        {
            cancellation.Dispose();
            if (request == _filterRequest && ReferenceEquals(_filterCancellation, cancellation))
            {
                _filterCancellation = null; _filterPaused = false; _filterPauseGate.Set();
                if (SearchPauseButton is not null) SearchPauseButton.IsEnabled = false;
                if (SearchProgressRow is not null) SearchProgressRow.Visibility = Visibility.Collapsed;
            }
        }
    }
    private void PauseSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_filterCancellation is null) return;
        _filterPaused = !_filterPaused;
        if (_filterPaused)
        {
            _filterPauseGate.Reset(); SearchPauseButton.Content = "Resume";
            SearchProgressText.Text = "Paused · resume to continue at the same point";
        }
        else { _filterPauseGate.Set(); SearchPauseButton.Content = "Pause"; SearchProgressText.Text = "Resuming search…"; }
    }
    private void AskBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (AskBox.Text == AskHint) { AskBox.Text = ""; AskBox.Foreground = System.Windows.Media.Brushes.DarkSlateGray; }
    }
    private void AskBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AskBox.Text)) { AskBox.Text = AskHint; AskBox.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#829498")!; }
    }
    private async void AskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) { await ApplySmartQueryAsync(); e.Handled = true; } }
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        AskBox.Text = query; _ = ApplySmartQueryAsync();
    }
    private async void Ask_Click(object sender, RoutedEventArgs e) => await ApplySmartQueryAsync();
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string index } && int.TryParse(index, out var tabIndex)) MainTabs.SelectedIndex = tabIndex;
        SetActiveNavigation();
    }
    private void SetActiveNavigation()
    {
        if (OverviewNavButton is null) return;
        var buttons = new[] { OverviewNavButton, SmartFindNavButton, ReviewFilesNavButton };
        for (var i = 0; i < buttons.Length; i++)
        {
            var active = MainTabs.SelectedIndex == i;
            buttons[i].Background = active
                ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#DDF4EF")!
                : System.Windows.Media.Brushes.Transparent;
            buttons[i].Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(active ? "#087D86" : "#45616A")!;
        }
    }
    private async void QuickQuery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        MainTabs.SelectedIndex = 1;
        SetActiveNavigation();
        AskBox.Text = query;
        if (_rows.Count == 0)
        {
            _assistantSummary = "Choose a folder first. Then this question will search that scan locally.";
            await ApplyCurrentFiltersAsync(); return;
        }
        await ApplySmartQueryAsync();
    }
    private void ReviewMatches_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 2; SetActiveNavigation(); }
    private async Task ApplySmartQueryAsync()
    {
        var query = AskBox.Text.Trim();
        if (query.Length == 0 || query == AskHint) { _smartFilter = _ => true; _limitLargestResults = false; _assistantSummary = "Ask about duplicates, size, age, file types, or common folders."; await ApplyCurrentFiltersAsync(); return; }
        var q = query.ToLowerInvariant();
        if (q.Contains("safe to delete") || q.Contains("should i delete") || q.Contains("what can i delete"))
        {
            _smartFilter = _ => true; _limitLargestResults = false;
            _assistantSummary = "I can find review candidates, but age, size, and duplicates do not prove a file is safe to remove. You choose each item.";
            await ApplyCurrentFiltersAsync(); return;
        }
        if (q.Contains("app cache") || q.Contains("windows cache") || q.Contains("system cache"))
        {
            _smartFilter = _ => false; _limitLargestResults = false;
            _assistantSummary = "TidyCircuit reviews recognized browser cache files only. It does not clear general app or Windows caches.";
            await ApplyCurrentFiltersAsync(); return;
        }
        var rules = new List<Func<FileRow, bool>>(); var labels = new List<string>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consume(params string[] words) { foreach (var word in words) consumed.Add(word); }
        void AddType(string label, params string[] extensions)
        {
            rules.Add(row => extensions.Contains(row.Type, StringComparer.OrdinalIgnoreCase)); labels.Add(label); Consume(label);
        }
        if (q.Contains("duplicate") || q.Contains("same content")) { rules.Add(row => row.Duplicate); labels.Add("matching contents"); Consume("duplicate", "duplicates", "same", "content", "contents"); }
        if (q.Contains("cache"))
        {
            rules.Add(row => IsRecognizedBrowserCacheFile(row.FullPath)); labels.Add("recognized browser cache files");
            Consume("browser", "browsers", "cache", "caches", "files", "temporary", "temp", "clear", "clearing");
        }
        if (q.Contains("video") || q.Contains("movie")) { AddType("videos", ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v"); Consume("video", "videos", "movie", "movies"); }
        else if (q.Contains("photo") || q.Contains("picture") || q.Contains("image")) { AddType("photos", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".tif", ".tiff", ".bmp"); Consume("photo", "photos", "picture", "pictures", "image", "images"); }
        else if (q.Contains("document") || q.Contains("pdf")) { AddType("documents", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt"); Consume("document", "documents", "pdf", "pdfs"); }
        else if (q.Contains("audio") || q.Contains("music") || q.Contains("song")) { AddType("audio", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"); Consume("audio", "music", "song", "songs"); }
        else if (q.Contains("archive") || q.Contains("zip")) { AddType("archives", ".zip", ".7z", ".rar", ".tar", ".gz"); Consume("archive", "archives", "zip"); }

        foreach (var folder in new[] { "Downloads", "Desktop", "Documents", "Pictures", "Videos", "Music" })
        {
            if (!q.Contains(folder, StringComparison.OrdinalIgnoreCase)) continue;
            rules.Add(row => HasPathFolder(row.FullPath, folder)); labels.Add(folder); Consume(folder, folder.TrimEnd('s').ToLowerInvariant());
        }

        var folderOverview = q.Contains("folder") || q.Contains("directory");
        if (folderOverview) { rules.Add(_ => true); labels.Add("files grouped by folder"); Consume("folder", "folders", "directory", "directories"); }
        var largest = !folderOverview && (q.Contains("largest") || q.Contains("biggest"));
        var sizeMatch = Regex.Match(q, @"\b(over|above|more than|larger than|under|below|less than|at least|at most)\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)\b");
        if (sizeMatch.Success)
        {
            var amount = double.Parse(sizeMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var scale = sizeMatch.Groups[3].Value switch { "kb" => 1024d, "mb" => 1024d * 1024, "gb" => 1024d * 1024 * 1024, _ => 1024d * 1024 * 1024 * 1024 };
            var scaledAmount = amount * scale;
            var threshold = scaledAmount >= long.MaxValue ? long.MaxValue : (long)scaledAmount;
            var op = sizeMatch.Groups[1].Value;
            rules.Add(op is "under" or "below" or "less than" ? row => row.Size < threshold : op is "at most" ? row => row.Size <= threshold : row => row.Size >= threshold);
            labels.Add($"{op} {sizeMatch.Groups[2].Value} {sizeMatch.Groups[3].Value.ToUpperInvariant()}");
            Consume("over", "above", "more", "than", "larger", "under", "below", "less", "at", "least", "most", sizeMatch.Groups[2].Value, sizeMatch.Groups[3].Value);
        }
        else if (!folderOverview && !largest && (q.Contains("large") || q.Contains("big") || q.Contains("space") || q.Contains("storage")))
        { rules.Add(row => row.Size >= 100L * 1024 * 1024); labels.Add("files at least 100 MB"); }
        if (largest)
        { rules.Add(_ => true); labels.Add("25 largest"); Consume("largest", "biggest"); }
        if (q.Contains("large") || q.Contains("largest") || q.Contains("big") || q.Contains("biggest") || q.Contains("space") || q.Contains("storage")) Consume("large", "largest", "big", "biggest", "space", "storage", "files", "file");

        var ageMatch = Regex.Match(q, @"(?:older than|over|more than)\s+(\d+)\s+(day|week|month|year)s?");
        if (q.Contains("old") || ageMatch.Success)
        {
            var amount = ageMatch.Success && int.TryParse(ageMatch.Groups[1].Value, out var parsedDays) ? Math.Clamp(parsedDays, 1, 365_000) : 180;
            var days = Math.Min(365_000, amount * (ageMatch.Success ? ageMatch.Groups[2].Value switch { "day" => 1, "week" => 7, "month" => 30, "year" => 365, _ => 1 } : 1));
            var cutoff = DateTime.Now.AddDays(-days); rules.Add(row => row.Modified < cutoff); labels.Add($"older than {days} days");
            Consume("old", "older", "than", "over", "more", "day", "days", "week", "weeks", "month", "months", "year", "years", ageMatch.Groups[1].Value);
        }

        var extension = Regex.Match(q, @"\.(\w{1,8})\b");
        if (extension.Success)
        { var ext = "." + extension.Groups[1].Value; rules.Add(row => row.Type.Equals(ext, StringComparison.OrdinalIgnoreCase)); labels.Add(ext + " files"); Consume(extension.Value, extension.Groups[1].Value); }

        var searchTerms = Regex.Matches(q, @"[\p{L}\p{N}][\p{L}\p{N}_.-]*").Select(match => match.Value.Trim('.', '-', '_'))
            .Where(term => term.Length > 1 && !consumed.Contains(term) && !new[] { "find", "show", "list", "search", "me", "my", "the", "this", "that", "all", "any", "file", "files", "in", "on", "from", "with", "and", "or", "by", "type", "folder", "folders", "directory", "directories", "please" }.Contains(term, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var term in searchTerms)
        { rules.Add(row => row.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase)); labels.Add($"‘{term}’"); }

        if (rules.Count == 0)
        {
            _smartFilter = _ => true;
            _limitLargestResults = false;
            _assistantSummary = "Type a file or folder name, extension, size, age, type, or duplicates to search this scan.";
            await ApplyCurrentFiltersAsync(); return;
        }

        _smartFilter = row => rules.All(rule => rule(row));
        _limitLargestResults = largest;
        if (largest && SortBox.SelectedIndex != 0) SortBox.SelectedIndex = 0;
        var matches = await ApplyCurrentFiltersAsync();
        if (matches is null) return;
        var request = _filterRequest;
        var summary = $"Showing {matches.Count:N0} {string.Join(" + ", labels)} · {Format(matches.Sum(r => r.Size))}. Review each result; nothing is selected automatically.";
        if (q.Contains("folder") || q.Contains("directory") || q.Contains("space") || q.Contains("storage"))
        {
            var allRows = _rows.ToArray();
            var largestFolders = await Task.Run(() => allRows.GroupBy(row =>
            {
                var relative = row.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                return separator < 0 ? "(selected folder root)" : relative[..separator];
            }).Select(group => new { Name = group.Key, Bytes = group.Sum(row => row.Size) })
              .OrderByDescending(folder => folder.Bytes).Take(3).ToList());
            if (request != _filterRequest) return;
            if (largestFolders.Count > 0) summary += " · Largest folders: " + string.Join("; ", largestFolders.Select(folder => $"{folder.Name} ({Format(folder.Bytes)})"));
        }
        if (request != _filterRequest) return;
        _assistantSummary = summary;
        if (AssistantStatus is not null)
        {
            var term = SearchBox.Text.Trim();
            AssistantStatus.Text = _assistantSummary + (term.Length > 0 && term != SearchHint ? $" · Search narrows results to {matches.Count:N0}" : "");
        }
    }
    private static bool HasPathFolder(string path, string folder) => path
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Equals(folder, StringComparison.OrdinalIgnoreCase));
    private static bool IsRecognizedBrowserCacheFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return false;
            var parts = Path.GetRelativePath(local, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            static bool IsChromium(string[] p, string vendor, string product) => p.Length >= 7
                && p[0].Equals(vendor, StringComparison.OrdinalIgnoreCase)
                && p[1].Equals(product, StringComparison.OrdinalIgnoreCase)
                && p[2].Equals("User Data", StringComparison.OrdinalIgnoreCase)
                && (p[3].Equals("Default", StringComparison.OrdinalIgnoreCase) || p[3].StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) || p[3].Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                && p[4].Equals("Cache", StringComparison.OrdinalIgnoreCase)
                && p[5].Equals("Cache_Data", StringComparison.OrdinalIgnoreCase);
            var chromium = IsChromium(parts, "Google", "Chrome") || IsChromium(parts, "Microsoft", "Edge") || IsChromium(parts, "BraveSoftware", "Brave-Browser");
            var firefox = parts.Length >= 6
                && parts[0].Equals("Mozilla", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("Firefox", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("Profiles", StringComparison.OrdinalIgnoreCase)
                && parts[3].Length > 0
                && parts[4].Equals("cache2", StringComparison.OrdinalIgnoreCase)
                && parts[5].Equals("entries", StringComparison.OrdinalIgnoreCase);
            return (chromium && parts.Length >= 7 || firefox && parts.Length == 7) && !string.IsNullOrWhiteSpace(Path.GetFileName(full));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
    private void UpdateMarkedSummary()
    {
        if (ReviewCountText is null) return;
        var marked = _rows.Count(row => row.Marked);
        ReviewCountText.Text = $"{FilesGrid.Items.Count:N0} shown · {marked:N0} selected";
        if (OverviewMarkedText is not null) OverviewMarkedText.Text = marked.ToString("N0");
        if (RecycleButton is not null) RecycleButton.IsEnabled = marked > 0;
    }
    private void FileRow_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_isUpdatingMarks && args.PropertyName == nameof(FileRow.Marked)) UpdateMarkedSummary();
    }
    private void SetMarks(IEnumerable<FileRow> rows, bool marked)
    {
        _isUpdatingMarks = true;
        try { foreach (var row in rows) row.Marked = marked; }
        finally { _isUpdatingMarks = false; }
        UpdateMarkedSummary();
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetMarks(_rows, true);
    private void MarkVisible_Click(object sender, RoutedEventArgs e) => SetMarks(FilesGrid.Items.OfType<FileRow>().ToList(), true);
    private void ClearMarks_Click(object sender, RoutedEventArgs e) => SetMarks(_rows, false);
    private static string OperationLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyCircuit", "operations.jsonl");
    private static void WriteOperation(string action, FileRow row, string result)
    {
        var path = OperationLogPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entry = JsonSerializer.Serialize(new { timeUtc = DateTime.UtcNow, action, path = row.FullPath, bytes = row.Size, result });
        File.AppendAllText(path, entry + Environment.NewLine, new UTF8Encoding(false));
    }
    private static string? RecycleBlockReason(FileRow row, string root)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            if ((File.GetAttributes(rootFull) & FileAttributes.ReparsePoint) != 0) return "selected folder is a link";
            var full = Path.GetFullPath(row.FullPath); var relative = Path.GetRelativePath(rootFull, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return "outside the selected folder";
            var volume = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(volume) || volume.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(volume).DriveType != DriveType.Fixed) return "not on a local fixed drive with standard Recycle Bin recovery";
            var recognizedBrowserCache = IsRecognizedBrowserCacheFile(full);
            var protectedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Windows", "Windows.old", "WindowsApps", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "EFI", "Boot", "WinSxS", "Installer", "$Recycle.Bin", "System Volume Information" };
            if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(protectedParts.Contains)) return "inside a protected Windows or app folder";
            if (HasPathFolder(full, "AppData") && !recognizedBrowserCache) return "inside a protected app-data folder; only recognized browser cache files can be reviewed";
            var protectedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys", ".drv", ".ocx", ".msi", ".msp", ".cab", ".cat", ".inf", ".bat", ".cmd", ".ps1", ".psm1", ".reg", ".lnk", ".scr", ".com" };
            if (protectedTypes.Contains(Path.GetExtension(full))) return "an app, system, script, or shortcut file";
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly)) != 0) return "hidden, system, linked, read-only, or not a regular file";
            var info = new FileInfo(full);
            if (info.Length != row.Size || info.LastWriteTime != row.Modified) return "changed since the scan";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unavailable or changed since scanning"; }
    }
    private async void RecycleMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = _rows.Where(r => r.Marked).ToList(); if (marked.Count == 0 || string.IsNullOrWhiteSpace(_selectedRoot)) return;
        var blocked = marked.Select(row => (Row: row, Reason: RecycleBlockReason(row, _selectedRoot))).Where(x => x.Reason is not null).ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show($"No files were moved. {blocked.Count} marked file(s) are protected or changed. Clear marks and select only regular, visible personal files inside the chosen folder.", "Protected items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var total = marked.Sum(row => row.Size);
        var cacheOnly = marked.All(row => IsRecognizedBrowserCacheFile(row.FullPath));
        var cacheNote = cacheOnly ? "\n\nThese are recognized temporary browser cache files only. Browser pages may reload or cache may be rebuilt. Cookies, history, saved passwords, and bookmarks are not included." : "";
        var answer = MessageBox.Show($"Move {marked.Count:N0} marked file(s) ({Format(total)}) to the Windows Recycle Bin?{cacheNote}\n\nTidyCircuit asks Windows to recycle them. Recovery depends on your Windows and drive settings; verify the files appear in Recycle Bin before relying on recovery. Their paths will be recorded locally in the operation log.", "Confirm recoverable cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        ScanButton.IsEnabled = FolderButton.IsEnabled = false;
        var moved = new List<FileRow>();
        var logWarning = false;
        try
        {
            foreach (var row in marked)
            {
                try
                {
                    WriteOperation("recycle", row, "requested");
                    FileSystem.DeleteFile(row.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    moved.Add(row);
                    try { WriteOperation("recycle", row, "moved"); } catch { logWarning = true; }
                }
                catch (Exception ex)
                {
                    try { WriteOperation("recycle", row, "failed: " + ex.GetType().Name); } catch { logWarning = true; }
                    MessageBox.Show($"Stopped after handling {moved.Count} file(s). Could not move {Path.GetFileName(row.FullPath)}. {ex.Message}\n\nCheck the Recycle Bin for any files already moved.", "Cleanup stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }
            }
            _rows.RemoveRange(moved);
            SetMarks(_rows, false);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_rows.Sum(r => r.Size));
            var (duplicates, errors) = await Task.Run(() => FindDuplicates(_rows.ToList())); DuplicatesText.Text = duplicates.ToString("N0");
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = $"Moved {moved.Count:N0} file(s) to Recycle Bin · {(errors > 0 ? $"{errors} file(s) could not be rechecked" : logWarning ? "check operation log status" : "operation logged locally")}";
        }
        finally { ScanButton.IsEnabled = FolderButton.IsEnabled = true; }
    }
    private static string TsvField(string value)
    {
        var safe = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var firstNonSpace = safe.AsSpan().TrimStart();
        return firstNonSpace.Length > 0 && firstNonSpace[0] is '=' or '+' or '-' or '@' ? "'" + safe : safe;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = _rows.Where(r => r.Marked).ToList();
        if (rows.Count == 0) { MessageBox.Show("Mark files in the REVIEW column first.", "Nothing marked", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new SaveFileDialog { FileName = "tidycircuit-review.tsv", Filter = "TSV review list|*.tsv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var lines = new[] { "Name\tFull path\tBytes\tModified" }.Concat(rows.Select(r => $"{TsvField(r.Name)}\t{TsvField(r.FullPath)}\t{r.Size}\t{r.Modified:o}"));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText.Text = $"Review list exported · {rows.Count} files · no files changed";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not export the review list. {ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void System_Click(object sender, RoutedEventArgs e) { MessageBox.Show("TidyCircuit does not read live PC health data. Open Windows Settings or Task Manager for RAM, battery, and drive details.", "System details", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void Safety_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("TidyCircuit does not detect malware. Open Windows Security to start a scan yourself? Microsoft Defender may quarantine detected threats.", "Windows Security", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Windows Security could not be opened. Find it in the Start menu. {ex.Message}", "Could not open Windows Security", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
public readonly record struct ScanInventoryProgress(int Files, int Folders, string CurrentFolder);
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
    public void RemoveRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var changed = false;
        foreach (var item in items) changed |= Items.Remove(item);
        if (!changed) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
public sealed class FileRow : INotifyPropertyChanged
{
    private bool _marked;
    private bool _duplicate;
    public string Name { get; set; } = ""; public string FullPath { get; set; } = ""; public string RelativePath { get; set; } = ""; public string Type { get; set; } = ""; public long Size { get; set; } public DateTime Modified { get; set; }
    public bool Duplicate { get => _duplicate; set { if (_duplicate == value) return; _duplicate = value; PropertyChanged?.Invoke(this, new(nameof(Duplicate))); PropertyChanged?.Invoke(this, new(nameof(DuplicateDisplay))); } }
    public string DuplicateDisplay => Duplicate ? "Same content" : "—";
    public string SizeDisplay => Size < 0 ? "—" : Format(Size); public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");
    public bool Marked { get => _marked; set { _marked = value; PropertyChanged?.Invoke(this, new(nameof(Marked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string Format(long bytes) { string[] u=["B","KB","MB","GB","TB"]; double n=bytes;int i=0;while(n>=1024&&i<4){n/=1024;i++;}return $"{n:0.#} {u[i]}"; }
}
