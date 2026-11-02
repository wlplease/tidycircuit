using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace TidyCircuit;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileRow> _rows = new();
    private string _selectedRoot = "";
    private Func<FileRow, bool> _smartFilter = _ => true;
    private CancellationTokenSource? _scanCancellation;
    private string _assistantSummary = "Suggestions use file size, dates, names, and local content hashes. They never decide a file is safe to remove.";
    private const string AskHint = "Try: largest videos, duplicates, old files, or PDFs";
    public MainWindow()
    {
        InitializeComponent(); FilesGrid.ItemsSource = _rows;
        FilesGrid.Sorting += (_, e) => { e.Handled = true; SortRows(); };
        MainTabs.SelectionChanged += (_, _) => SetActiveNavigation();
        SetActiveNavigation();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose one folder for a read-only scan", Multiselect = false };
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
        CancelScanButton.IsEnabled = true;
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "Scanning…";
        ProgressText.Text = "Reading file list";
        StatusText.Text = "Scanning selected folder · nothing is changed"; ScanProgress.IsIndeterminate = true;
        try
        {
            var inventoryProgress = new Progress<int>(count => ProgressText.Text = $"Reading file {count:N0}");
            var (files, inventoryErrors) = await Task.Run(() => ReadFiles(root, token, inventoryProgress));
            token.ThrowIfCancellationRequested();
            foreach (var row in files.OrderByDescending(f => f.Size))
            {
                row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(FileRow.Marked)) UpdateMarkedSummary(); };
                _rows.Add(row);
            }
            FileCountText.Text = files.Count.ToString("N0"); FolderSizeText.Text = Format(files.Sum(f => f.Size));
            ProgressText.Text = "Comparing matching files";
            var hashProgress = new Progress<int>(count => ProgressText.Text = $"Hashing candidate {count:N0}");
            var (dupes, hashErrors) = await Task.Run(() => FindDuplicates(files, token, hashProgress));
            DuplicatesText.Text = dupes.ToString("N0");
            var errors = inventoryErrors + hashErrors;
            StatusText.Text = errors == 0
                ? $"Scan complete · {files.Count:N0} files · matching content checked; files over 256 MB skipped"
                : $"Scan complete with {errors:N0} read issue(s) · some files could not be read";
            ApplySmartQuery(); UpdateMarkedSummary();
        }
        catch (OperationCanceledException)
        {
            _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "—";
            StatusText.Text = "Scan canceled · no files were changed";
        }
        catch (Exception ex)
        {
            _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "—";
            StatusText.Text = "Scan failed · " + ex.Message;
        }
        finally
        {
            ProgressText.Text = ""; ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = true;
            CancelScanButton.IsEnabled = false; _scanCancellation?.Dispose(); _scanCancellation = null;
        }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e) { _scanCancellation?.Cancel(); ProgressText.Text = "Stopping after current file…"; }
    private static (List<FileRow> Files, int Errors) ReadFiles(string root, CancellationToken token, IProgress<int> progress)
    {
        var files = new List<FileRow>(); var errors = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try { foreach (var child in Directory.EnumerateDirectories(dir, "*", options)) pending.Push(child); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try { var info = new FileInfo(path); files.Add(new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = Path.GetRelativePath(root, info.FullName), Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
                    if (files.Count % 250 == 0) progress.Report(files.Count);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
        }
        return (files, errors);
    }
    private static (int Duplicates, int Errors) FindDuplicates(List<FileRow> files, CancellationToken token = default, IProgress<int>? progress = null)
    {
        var count = 0; var errors = 0;
        foreach (var sizeGroup in files.Where(f => f.Size >= 0 && f.Size <= 268_435_456).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<FileRow>>();
            var candidate = 0;
            foreach (var row in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                try { using var stream = File.OpenRead(row.FullPath); var hash = Convert.ToHexString(SHA256.HashData(stream)); if (!hashes.TryGetValue(hash, out var matches)) hashes[hash] = matches = new(); matches.Add(row); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { errors++; }
                progress?.Report(++candidate);
            }
            foreach (var hashGroup in hashes.Values.Where(g => g.Count() > 1))
            {
                foreach (var row in hashGroup) row.Duplicate = true;
                count += hashGroup.Count() - 1;
            }
        }
        return (count, errors);
    }
    private static string Format(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyCurrentFilters();
    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SortRows();
    private void SortRows() { if (FilesGrid is null || SortBox.SelectedIndex < 0) return; var view = CollectionViewSource.GetDefaultView(_rows); view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(SortBox.SelectedIndex switch { 1 => nameof(FileRow.Name), 2 => nameof(FileRow.Modified), _ => nameof(FileRow.Size) }, SortBox.SelectedIndex == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending)); }
    private void ApplyCurrentFilters()
    {
        if (FilesGrid is null || SearchBox is null) return;
        var term = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_rows).Filter = item => item is FileRow row && _smartFilter(row) &&
            (term.Length == 0 || row.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (AssistantStatus is not null)
            AssistantStatus.Text = _assistantSummary + (term.Length > 0 ? $" · Search narrows results to {FilesGrid.Items.Count:N0}" : "");
        UpdateMarkedSummary();
    }
    private void AskBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (AskBox.Text == AskHint) { AskBox.Text = ""; AskBox.Foreground = System.Windows.Media.Brushes.DarkSlateGray; }
    }
    private void AskBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AskBox.Text)) { AskBox.Text = AskHint; AskBox.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#829498")!; }
    }
    private void AskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) { ApplySmartQuery(); e.Handled = true; } }
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        AskBox.Text = query; ApplySmartQuery();
    }
    private void Ask_Click(object sender, RoutedEventArgs e) => ApplySmartQuery();
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
    private void QuickQuery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        MainTabs.SelectedIndex = 1;
        SetActiveNavigation();
        AskBox.Text = query;
        if (_rows.Count == 0)
        {
            _assistantSummary = "Choose a folder first. Then this question will search that scan locally.";
            ApplyCurrentFilters(); return;
        }
        ApplySmartQuery();
    }
    private void ReviewMatches_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 2; SetActiveNavigation(); }
    private void ApplySmartQuery()
    {
        var query = AskBox.Text.Trim();
        if (query.Length == 0 || query == AskHint) { _smartFilter = _ => true; _assistantSummary = "Ask about duplicates, size, age, file types, or common folders."; ApplyCurrentFilters(); return; }
        var q = query.ToLowerInvariant();
        if (q.Contains("safe to delete") || q.Contains("should i delete") || q.Contains("what can i delete"))
        {
            _smartFilter = _ => true;
            _assistantSummary = "I can find review candidates, but age, size, and duplicates do not prove a file is safe to remove. You choose each item.";
            ApplyCurrentFilters(); return;
        }
        var rules = new List<Func<FileRow, bool>>(); var labels = new List<string>();
        if (q.Contains("duplicate") || q.Contains("same content")) { rules.Add(r => r.Duplicate); labels.Add("matching contents"); }
        if (q.Contains("video") || q.Contains("movie")) { rules.Add(r => new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v" }.Contains(r.Type, StringComparer.OrdinalIgnoreCase)); labels.Add("videos"); }
        else if (q.Contains("photo") || q.Contains("picture") || q.Contains("image")) { rules.Add(r => new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".tif", ".tiff", ".bmp" }.Contains(r.Type, StringComparer.OrdinalIgnoreCase)); labels.Add("photos"); }
        else if (q.Contains("document") || q.Contains("pdf")) { rules.Add(r => new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt" }.Contains(r.Type, StringComparer.OrdinalIgnoreCase)); labels.Add("documents"); }
        else if (q.Contains("audio") || q.Contains("music") || q.Contains("song")) { rules.Add(r => new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma" }.Contains(r.Type, StringComparer.OrdinalIgnoreCase)); labels.Add("audio"); }
        else if (q.Contains("archive") || q.Contains("zip")) { rules.Add(r => new[] { ".zip", ".7z", ".rar", ".tar", ".gz" }.Contains(r.Type, StringComparer.OrdinalIgnoreCase)); labels.Add("archives"); }
        if (q.Contains("download")) { rules.Add(r => r.FullPath.Contains("Downloads", StringComparison.OrdinalIgnoreCase)); labels.Add("Downloads"); }
        if (q.Contains("desktop")) { rules.Add(r => r.FullPath.Contains("Desktop", StringComparison.OrdinalIgnoreCase)); labels.Add("Desktop"); }
        if (q.Contains("large") || q.Contains("big") || q.Contains("space") || q.Contains("largest") || q.Contains("biggest"))
        {
            if (q.Contains("largest") || q.Contains("biggest"))
            {
                var top = _rows.OrderByDescending(r => r.Size).Take(25).ToHashSet(); rules.Add(top.Contains); labels.Add("25 largest files");
            }
            else { rules.Add(r => r.Size >= 100L * 1024 * 1024); labels.Add("files at least 100 MB"); }
        }
        var ageMatch = Regex.Match(q, @"(?:older than|over)\s+(\d+)\s+(day|week|month|year)s?");
        if (q.Contains("old") || ageMatch.Success)
        {
            var days = ageMatch.Success ? int.Parse(ageMatch.Groups[1].Value) * ageMatch.Groups[2].Value switch { "day" => 1, "week" => 7, "month" => 30, "year" => 365, _ => 1 } : 180;
            var cutoff = DateTime.Now.AddDays(-days); rules.Add(r => r.Modified < cutoff); labels.Add($"older than {days} days");
        }
        var extension = Regex.Match(q, @"\.(\w{1,8})\b");
        if (extension.Success && !new[] { "pdf", "zip" }.Contains(extension.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
        { var ext = "." + extension.Groups[1].Value; rules.Add(r => r.Type.Equals(ext, StringComparison.OrdinalIgnoreCase)); labels.Add(ext + " files"); }
        if (rules.Count == 0)
        {
            _smartFilter = _ => true;
            _assistantSummary = "I don’t recognize that yet. Try duplicates, largest files, old files, photos, videos, PDFs, Downloads, or an extension like .zip.";
            ApplyCurrentFilters(); return;
        }
        _smartFilter = row => rules.All(rule => rule(row));
        if ((q.Contains("largest") || q.Contains("biggest")) && SortBox.SelectedIndex != 0) SortBox.SelectedIndex = 0;
        ApplyCurrentFilters();
        var matches = FilesGrid.Items.OfType<FileRow>().ToList();
        var summary = $"Showing {matches.Count:N0} {string.Join(" + ", labels)} · {Format(matches.Sum(r => r.Size))}. Review each result; nothing is selected automatically.";
        if (q.Contains("folder") || q.Contains("directory") || q.Contains("space") || q.Contains("storage"))
        {
            var largestFolders = _rows.GroupBy(row =>
            {
                var relative = row.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                return separator < 0 ? "(selected folder root)" : relative[..separator];
            }).Select(group => new { Name = group.Key, Bytes = group.Sum(row => row.Size) })
              .OrderByDescending(folder => folder.Bytes).Take(3).ToList();
            if (largestFolders.Count > 0) summary += " · Largest folders: " + string.Join("; ", largestFolders.Select(folder => $"{folder.Name} ({Format(folder.Bytes)})"));
        }
        _assistantSummary = summary;
        ApplyCurrentFilters();
    }
    private void UpdateMarkedSummary()
    {
        if (ReviewCountText is null) return;
        var marked = _rows.Count(row => row.Marked);
        ReviewCountText.Text = $"{FilesGrid.Items.Count:N0} shown · {marked:N0} selected";
        if (OverviewMarkedText is not null) OverviewMarkedText.Text = marked.ToString("N0");
        if (RecycleButton is not null) RecycleButton.IsEnabled = marked > 0;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var row in _rows) row.Marked = true; UpdateMarkedSummary(); }
    private void MarkVisible_Click(object sender, RoutedEventArgs e) { foreach (var row in FilesGrid.Items.OfType<FileRow>()) row.Marked = true; UpdateMarkedSummary(); }
    private void ClearMarks_Click(object sender, RoutedEventArgs e) { foreach (var row in _rows) row.Marked = false; UpdateMarkedSummary(); }
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
            var protectedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Windows", "Windows.old", "WindowsApps", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Recovery", "EFI", "Boot", "WinSxS", "Installer", "$Recycle.Bin", "System Volume Information" };
            if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(protectedParts.Contains)) return "inside a protected Windows or app folder";
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
        var answer = MessageBox.Show($"Move {marked.Count:N0} marked file(s) ({Format(total)}) to the Windows Recycle Bin?\n\nTidyCircuit asks Windows to recycle them. Recovery depends on your Windows and drive settings; verify the files appear in Recycle Bin before relying on recovery. Their paths will be recorded locally in the operation log.", "Confirm recoverable cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            foreach (var row in moved) _rows.Remove(row);
            foreach (var row in _rows) row.Marked = false;
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_rows.Sum(r => r.Size));
            ApplyCurrentFilters(); UpdateMarkedSummary();
            var (duplicates, errors) = await Task.Run(() => FindDuplicates(_rows.ToList())); DuplicatesText.Text = duplicates.ToString("N0");
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
