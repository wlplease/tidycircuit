using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace TidyCircuit;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> ProtectedPathParts = new(StringComparer.OrdinalIgnoreCase) { "Windows", "Windows.old", "WindowsApps", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "EFI", "Boot", "WinSxS", "Installer", "$Recycle.Bin", "System Volume Information" };
    private static readonly HashSet<string> ProtectedFileTypes = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys", ".drv", ".ocx", ".msi", ".msp", ".cab", ".cat", ".inf", ".bat", ".cmd", ".ps1", ".psm1", ".reg", ".lnk", ".scr", ".com" };
    private readonly BulkObservableCollection<FileRow> _rows = new();
    private readonly BulkObservableCollection<FileRow> _visibleRows = new();
    private readonly ObservableCollection<GuideChatMessage> _chatMessages = new();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _selectedRoot = "";
    private Func<FileRow, bool> _smartFilter = _ => true;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _cleanupCancellation;
    private readonly ManualResetEventSlim _scanPauseGate = new(true);
    private readonly ManualResetEventSlim _filterPauseGate = new(true);
    private bool _isUpdatingMarks;
    private bool _scanPaused;
    private bool _filterPaused;
    private bool _automaticScan;
    private bool _hashingDuplicates;
    private bool _scanComplete;
    private long _scanBytes;
    private string _selectedFolderFilter = "";
    private string _selectedViewFilter = "All files";
    private readonly List<OrganizationMove> _lastOrganizationMoves = new();
    private string _lastGuideTopic = "";
    private int _visibleExtraCount;
    private bool _syncingSort;
    private bool _limitLargestResults;
    private string _sortProperty = nameof(FileRow.Size);
    private bool _sortDescending = true;
    private long _filterRequest;
    private string _assistantSummary = "Suggestions use file size, dates, names, and local content hashes. They never decide a file is safe to remove.";
    private const string AskHint = "Try: largest videos, duplicates, old files, or PDFs";
    private const string SearchHint = "Search names, folders, extensions…";
    public MainWindow()
    {
        InitializeComponent(); FilesGrid.ItemsSource = _visibleRows;
        ChatMessagesList.ItemsSource = _chatMessages;
        LoadOrganizationUndo();
        _chatMessages.Add(GuideChatMessage.Assistant("Hi! I can help with scans, file review, cleanup, duplicates, and privacy. I can’t see your files, but I can walk you through the app."));
        ContentRendered += (_, _) => ShowFirstRunGuide();
        FilesGrid.Sorting += FilesGrid_Sorting;
        UpdateSortIndicators();
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await ApplyCurrentFiltersAsync(); };
        MainTabs.SelectionChanged += (_, _) => SetActiveNavigation();
        Closing += (_, _) =>
        {
            _searchDebounce.Stop();
            _scanCancellation?.Cancel(); _scanPauseGate.Set();
            _filterCancellation?.Cancel(); _filterPauseGate.Set();
            _cleanupCancellation?.Cancel();
        };
        SetActiveNavigation();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose one folder for a local scan", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _automaticScan = false;
        _selectedRoot = dialog.FolderName;
        SelectedRootText.Text = _selectedRoot;
        await ScanFolderAsync(_selectedRoot);
    }
    private async void ScanAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_automaticScan) { await FindTemporaryFilesAsync(); return; }
        if (!string.IsNullOrWhiteSpace(_selectedRoot)) await ScanFolderAsync(_selectedRoot);
        else ChooseFolder_Click(sender, e);
    }
    private async void FindCaches_Click(object sender, RoutedEventArgs e) => await FindTemporaryFilesAsync();
    private async Task FindTemporaryFilesAsync()
    {
        _automaticScan = true; _selectedRoot = "";
        SelectedRootText.Text = "Finding recognized browser caches and older temporary files…";
        ScanButton.IsEnabled = FolderButton.IsEnabled = FindCachesButton.IsEnabled = false;
        CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = true;
        CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Visible;
        PauseScanButton.Content = "Pause"; _scanPaused = false; _scanPauseGate.Set();
        _scanCancellation = new CancellationTokenSource(); var token = _scanCancellation.Token;
        _filterCancellation?.Cancel(); _filterPauseGate.Set(); _visibleExtraCount = 0; _visibleRows.Clear(); _rows.Clear(); _scanBytes = 0;
        _scanComplete = false; _hashingDuplicates = false; VerifyDuplicatesButton.IsEnabled = false; UpdateMarkedSummary();
        FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "Searching…";
        StatusText.Text = "Checking common cache and temporary locations · nothing is changed"; ProgressText.Text = "Starting local search…";
        ScanHealthText.Text = "Local inventory · recognized browser cache and older user temp files · protected system/app data excluded";
        ScanProgress.IsIndeterminate = true;
        try
        {
            var progress = new Progress<ScanInventoryProgress>(update =>
            {
                if (!token.IsCancellationRequested && !_scanPaused) ProgressText.Text = $"Finding files · {update.Files:N0} checked · {update.CurrentFolder}";
                if (!token.IsCancellationRequested && !_scanPaused) ScanHealthText.Text = $"Live · {update.Files:N0} entries checked · {update.Folders:N0} locations · protected paths skipped";
            });
            var result = await Task.Run(() => FindSafeTemporaryFilesCore(token, progress, _scanPauseGate,
                batch => Dispatcher.Invoke(() => AppendScanBatch(batch))));
            token.ThrowIfCancellationRequested();
            _selectedRoot = "";
            SelectedRootText.Text = "Common locations · browser cache + older user temporary files";
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
            DuplicatesText.Text = "—";
            _smartFilter = _ => true; _limitLargestResults = false;
            _assistantSummary = "Found recognized browser cache and older user temporary files. Review each result; nothing is selected automatically.";
            SearchBox.Text = SearchHint; AskBox.Text = AskHint; _searchDebounce.Stop();
            MainTabs.SelectedIndex = 2; SetActiveNavigation();
            _scanComplete = true;
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = result.Errors == 0
                ? $"Found {result.Count:N0} files · browser cache + user temp files idle 7+ days · nothing changed"
                : $"Found {result.Count:N0} files · {result.Errors:N0} read issue(s) · nothing changed";
            ScanHealthText.Text = result.Errors == 0
                ? $"Scan complete · {result.Locations:N0} locations checked · no access issues"
                : $"Scan complete · {result.Locations:N0} locations checked · {result.Errors:N0} read/access issue(s) · protected paths skipped";
        }
        catch (OperationCanceledException)
        {
            MarkDuplicateCandidates(_rows);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
            DuplicatesText.Text = $"{_rows.Count(row => row.SizeCandidate):N0} candidates";
            SelectedRootText.Text = "Common locations · partial results";
            _assistantSummary = "Partial cache/temp results are shown for review. Finish a complete search to enable cleanup.";
            _scanComplete = false;
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = $"Search canceled · {_rows.Count:N0} partial files kept · nothing changed";
            ScanHealthText.Text = $"Partial results · {_rows.Count:N0} files kept · scan canceled safely · no files changed";
        }
        catch (Exception ex)
        {
            MarkDuplicateCandidates(_rows);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
            DuplicatesText.Text = $"{_rows.Count(row => row.SizeCandidate):N0} candidates";
            SelectedRootText.Text = "Common locations · partial results";
            _assistantSummary = "Partial cache/temp results are shown for review. Finish a complete search to enable cleanup.";
            _scanComplete = false;
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = $"Temporary file search stopped · {_rows.Count:N0} partial files kept · {ex.Message}";
            ScanHealthText.Text = $"Partial results kept · {ex.GetType().Name} · no files changed";
        }
        finally
        {
            ProgressText.Text = ""; ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = FindCachesButton.IsEnabled = true;
            CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = false;
            CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Collapsed;
            _scanPaused = false; _scanPauseGate.Set(); _scanCancellation?.Dispose(); _scanCancellation = null;
        }
    }
    private async Task ScanFolderAsync(string root)
    {
        ScanButton.IsEnabled = FolderButton.IsEnabled = FindCachesButton.IsEnabled = false;
        CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = true;
        CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Visible;
        PauseScanButton.Content = "Pause"; _scanPaused = false; _scanPauseGate.Set();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        _filterCancellation?.Cancel(); _filterPauseGate.Set(); _visibleExtraCount = 0; _visibleRows.Clear(); _rows.Clear();
        _scanBytes = 0; _scanComplete = false; _hashingDuplicates = false;
        _smartFilter = _ => true; _limitLargestResults = false;
        _searchDebounce.Stop(); SearchBox.Text = SearchHint; ViewFilterBox.SelectedIndex = 0; FolderFilterBox.SelectedIndex = 0;
        SearchBox.IsEnabled = ViewFilterBox.IsEnabled = FolderFilterBox.IsEnabled = SortBox.IsEnabled = false;
        VerifyDuplicatesButton.IsEnabled = false;
        MainTabs.SelectedIndex = 2; SetActiveNavigation(); UpdateMarkedSummary();
        FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "Scanning…";
        ProgressText.Text = "Reading file list";
        StatusText.Text = "Scanning selected folder · nothing is changed"; ScanProgress.IsIndeterminate = true;
        ScanHealthText.Text = "Live inventory · linked folders skipped · Windows and protected application locations are blocked from cleanup";
        try
        {
            var inventoryProgress = new Progress<ScanInventoryProgress>(update =>
            {
                if (!token.IsCancellationRequested && !_scanPaused)
                {
                    ProgressText.Text = $"Reading {update.Files:N0} files · {update.Folders:N0} folders · {update.CurrentFolder}";
                    StatusText.Text = $"Scanning · {_rows.Count:N0} files found · nothing is changed";
                    ScanHealthText.Text = $"Live · {update.Files:N0} entries checked · {update.Folders:N0} folders visited · partial results shown below";
                }
            });
            var (files, inventoryErrors) = await Task.Run(() => ReadFiles(root, token, inventoryProgress, _scanPauseGate,
                batch => Dispatcher.Invoke(() => AppendScanBatch(batch))));
            token.ThrowIfCancellationRequested();
            MarkDuplicateCandidates(files);
            _scanComplete = true;
            PopulateFolderFilter();
            FileCountText.Text = files.Count.ToString("N0"); FolderSizeText.Text = Format(files.Sum(f => f.Size));
            var candidates = files.Count(row => row.SizeCandidate);
            DuplicatesText.Text = $"{candidates:N0} candidates";
            VerifyDuplicatesButton.IsEnabled = files.Any(row => row.SizeCandidate && row.Size <= 268_435_456);
            StatusText.Text = inventoryErrors == 0
                ? $"Scan complete · {files.Count:N0} files · {candidates:N0} same-size candidates · contents not hashed"
                : $"Scan complete with {inventoryErrors:N0} read issue(s) · {files.Count:N0} files · some files could not be read";
            ScanHealthText.Text = inventoryErrors == 0
                ? $"Scan complete · {files.Count:N0} files · no access issues · Windows/system folders and linked paths are skipped"
                : $"Scan complete · {files.Count:N0} files · {inventoryErrors:N0} read/access issue(s) · protected paths skipped";
            await ApplyCurrentFiltersAsync();
            await ApplySmartQueryAsync(); UpdateMarkedSummary();
        }
        catch (OperationCanceledException)
        {
            MarkDuplicateCandidates(_rows);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
            DuplicatesText.Text = $"{_rows.Count(row => row.SizeCandidate):N0} candidates";
            _assistantSummary = "Scan stopped early. These partial files are kept for review; scan again to refresh the full folder.";
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            SelectedRootText.Text = root + " · partial results";
            StatusText.Text = $"Scan canceled · {_rows.Count:N0} partial files kept · no files changed";
            ScanHealthText.Text = $"Partial results · {_rows.Count:N0} files kept · scan canceled safely · no files changed";
        }
        catch (Exception ex)
        {
            MarkDuplicateCandidates(_rows);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
            DuplicatesText.Text = $"{_rows.Count(row => row.SizeCandidate):N0} candidates";
            _assistantSummary = "Scan stopped early. These partial files are kept for review; scan again to refresh the full folder.";
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            StatusText.Text = $"Scan stopped · {_rows.Count:N0} partial files kept · {ex.Message}";
            ScanHealthText.Text = $"Partial results kept · {ex.GetType().Name} · no files changed";
        }
        finally
        {
            ProgressText.Text = ""; ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = FindCachesButton.IsEnabled = true;
            CancelScanButton.IsEnabled = PauseScanButton.IsEnabled = false;
            CancelScanButton.Visibility = PauseScanButton.Visibility = ScanProgress.Visibility = Visibility.Collapsed;
            _scanPaused = false; _scanPauseGate.Set();
            _scanCancellation?.Dispose(); _scanCancellation = null;
            SearchBox.IsEnabled = ViewFilterBox.IsEnabled = FolderFilterBox.IsEnabled = SortBox.IsEnabled = true;
            SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = true;
            UpdateMarkedSummary();
        }
    }
    private void AppendScanBatch(IReadOnlyList<FileRow> batch)
    {
        if (batch.Count == 0) return;
        System.ComponentModel.PropertyChangedEventHandler markedChanged = FileRow_PropertyChanged;
        foreach (var row in batch) row.PropertyChanged += markedChanged;
        _rows.AddRange(batch); _visibleRows.AddRange(batch);
        _scanBytes += batch.Sum(row => row.Size);
        FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_scanBytes);
        UpdateMarkedSummary();
    }
    private void PauseScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null) return;
        _scanPaused = !_scanPaused;
        if (_scanPaused) { _scanPauseGate.Reset(); PauseScanButton.Content = "Resume"; ProgressText.Text = "Scan paused · resume to continue"; }
        else { _scanPauseGate.Set(); PauseScanButton.Content = "Pause"; ProgressText.Text = "Resuming scan…"; }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupCancellation is not null) { _cleanupCancellation.Cancel(); ProgressText.Text = "Stopping cleanup…"; return; }
        _scanCancellation?.Cancel(); _scanPauseGate.Set(); ProgressText.Text = "Stopping scan…";
    }
    private static (List<FileRow> Files, int Errors) ReadFiles(string root, CancellationToken token, IProgress<ScanInventoryProgress> progress, ManualResetEventSlim pauseGate, Action<IReadOnlyList<FileRow>>? onBatch = null, bool collectFiles = true)
    {
        var files = new List<FileRow>(); var batch = new List<FileRow>(128); var errors = 0; var folders = 0; var processed = 0; var currentFolder = root;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>(); pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                pauseGate.Wait(token);
                var dir = pending.Pop();
                currentFolder = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(currentFolder)) currentFolder = dir;
                progress.Report(new(processed, ++folders, currentFolder));
                try { foreach (var child in Directory.EnumerateDirectories(dir, "*", options)) { token.ThrowIfCancellationRequested(); pauseGate.Wait(token); pending.Push(child); } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
                try
                {
                    foreach (var path in Directory.EnumerateFiles(dir, "*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        pauseGate.Wait(token);
                        try
                        {
                            var info = new FileInfo(path);
                            var row = new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = Path.GetRelativePath(root, info.FullName), ScanRoot = root, Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension };
                            if (collectFiles) files.Add(row);
                            batch.Add(row); processed++;
                            if (batch.Count >= 128) { onBatch?.Invoke(batch.ToArray()); batch.Clear(); }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
                        if (processed % 500 == 0) progress.Report(new(processed, folders, currentFolder));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
            }
        }
        catch (OperationCanceledException)
        {
            if (batch.Count > 0) onBatch?.Invoke(batch.ToArray());
            throw;
        }
        if (batch.Count > 0) onBatch?.Invoke(batch.ToArray());
        if (!token.IsCancellationRequested) progress.Report(new(processed, folders, currentFolder));
        return (files, errors);
    }
    private static TemporaryFindResult FindSafeTemporaryFilesCore(CancellationToken token, IProgress<ScanInventoryProgress> progress, ManualResetEventSlim pauseGate, Action<IReadOnlyList<FileRow>>? onBatch = null)
    {
        var candidates = 0; var errors = 0; var locations = 0; var checkedFiles = 0;
        var cacheRoots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        void AddProfiles(string userDataRoot)
        {
            if (!Directory.Exists(userDataRoot)) return;
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(userDataRoot, "*", options))
                {
                    var name = Path.GetFileName(profile);
                    if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && !name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;
                    var cacheRoot = Path.Combine(profile, "Cache", "Cache_Data");
                    if (Directory.Exists(cacheRoot)) cacheRoots.Add(cacheRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
        }
        AddProfiles(Path.Combine(local, "Google", "Chrome", "User Data"));
        AddProfiles(Path.Combine(local, "Microsoft", "Edge", "User Data"));
        AddProfiles(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
        var firefoxProfiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var entries = Path.Combine(profile, "cache2", "entries");
                    if (Directory.Exists(entries)) cacheRoots.Add(entries);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
        }
        cacheRoots = cacheRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in cacheRoots)
        {
            token.ThrowIfCancellationRequested(); pauseGate.Wait(token); locations++;
            var offset = checkedFiles;
            var nestedProgress = new InlineProgress<ScanInventoryProgress>(update =>
                progress.Report(new(offset + update.Files, locations + update.Folders, "Browser cache · " + update.CurrentFolder)));
            var (files, readErrors) = ReadFiles(root, token, nestedProgress, pauseGate, batch =>
            {
                var accepted = new List<FileRow>();
                foreach (var row in batch)
                {
                    row.ScanRoot = root;
                    if (RecycleBlockReason(row, root) is null) { candidates++; accepted.Add(row); }
                    checkedFiles++;
                }
                if (accepted.Count > 0) onBatch?.Invoke(accepted);
                if (checkedFiles % 250 == 0) progress.Report(new(checkedFiles, locations, "Checking browser cache files"));
            }, collectFiles: false);
            errors += readErrors;
        }
        var tempRoot = Path.GetFullPath(Path.GetTempPath()); var tempBatch = new List<FileRow>(64);
        if (Directory.Exists(tempRoot))
        {
            locations++;
            try
            {
                foreach (var path in Directory.EnumerateFiles(tempRoot, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    token.ThrowIfCancellationRequested(); pauseGate.Wait(token); checkedFiles++;
                    try
                    {
                        var info = new FileInfo(path);
                        var row = new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = info.Name, ScanRoot = tempRoot, Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension };
                        if (IsRecognizedUserTempFile(row) && RecycleBlockReason(row, tempRoot) is null) { candidates++; tempBatch.Add(row); }
                        if (tempBatch.Count >= 64) { onBatch?.Invoke(tempBatch.ToArray()); tempBatch.Clear(); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
                    if (checkedFiles % 250 == 0) progress.Report(new(checkedFiles, locations, "Checking older temporary files"));
                }
            }
            catch (OperationCanceledException)
            {
                if (tempBatch.Count > 0) onBatch?.Invoke(tempBatch.ToArray());
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
        }
        if (tempBatch.Count > 0) onBatch?.Invoke(tempBatch.ToArray());
        progress.Report(new(checkedFiles, locations, "Search complete"));
        return new TemporaryFindResult(candidates, locations, errors);
    }
    private static (int Duplicates, int Errors) FindDuplicates(List<FileRow> files, CancellationToken token = default, IProgress<int>? progress = null, ManualResetEventSlim? pauseGate = null)
    {
        var count = 0; var errors = 0; var candidate = 0; var nextGroup = 0;
        foreach (var row in files) { row.Duplicate = false; row.DuplicateExtra = false; row.DuplicateGroup = 0; }
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
                var orderedGroup = hashGroup.OrderBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                var groupId = ++nextGroup;
                count += orderedGroup.Count - 1;
                for (var index = 0; index < orderedGroup.Count; index++)
                {
                    orderedGroup[index].DuplicateGroup = groupId;
                    orderedGroup[index].Duplicate = true;
                    orderedGroup[index].DuplicateExtra = index > 0;
                }
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
    internal static string Format(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    private void SearchBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text == SearchHint) { SearchBox.Text = ""; SearchBox.Foreground = System.Windows.Media.Brushes.DarkSlateGray; }
    }
    private void SearchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) { SearchBox.Text = SearchHint; SearchBox.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#829498")!; }
    }
    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingSort) return;
        switch (SortBox.SelectedIndex)
        {
            case 0: _sortProperty = nameof(FileRow.Size); _sortDescending = true; break;
            case 1: _sortProperty = nameof(FileRow.Name); _sortDescending = false; break;
            case 2: _sortProperty = nameof(FileRow.Modified); _sortDescending = false; break;
            default: return;
        }
        UpdateSortIndicators(); SortRows();
    }
    private void FilesGrid_Sorting(object? sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var property = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(property)) return;
        if (property == _sortProperty) _sortDescending = !_sortDescending;
        else { _sortProperty = property; _sortDescending = false; }
        _syncingSort = true;
        SortBox.SelectedIndex = property switch
        {
            nameof(FileRow.Size) when _sortDescending => 0,
            nameof(FileRow.Name) when !_sortDescending => 1,
            nameof(FileRow.Modified) when !_sortDescending => 2,
            _ => -1
        };
        _syncingSort = false;
        UpdateSortIndicators(); SortRows();
    }
    private void UpdateSortIndicators()
    {
        if (FilesGrid is null) return;
        foreach (var column in FilesGrid.Columns)
            column.SortDirection = column.SortMemberPath == _sortProperty ? (_sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }
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
        var sortProperty = _sortProperty;
        var sortDescending = _sortDescending;
        var selectedFolder = _selectedFolderFilter; var selectedView = _selectedViewFilter;
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
                    var viewMatches = selectedView switch
                    {
                        "Selected only" => row.Marked,
                        "Large · 100 MB+" => row.Size >= 100L * 1024 * 1024,
                        "Older · 6 months+" => row.AgeDays >= 183,
                        "Exact duplicates" => row.Duplicate,
                        "Same-size candidates" => row.SizeCandidate,
                        "Temporary files" => IsRecognizedBrowserCacheFile(row.FullPath) || IsRecognizedUserTempFile(row),
                        _ => true
                    };
                    var folderMatches = string.IsNullOrEmpty(selectedFolder) || Path.GetDirectoryName(row.RelativePath)?.StartsWith(selectedFolder, StringComparison.OrdinalIgnoreCase) == true;
                    if (viewMatches && folderMatches && smartFilter(row) && (term.Length == 0 || row.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase))) found.Add(row);
                    if ((index + 1) % 500 == 0 || index + 1 == snapshot.Length) searchProgress.Report((index + 1, snapshot.Length, found.Count));
                }
                IEnumerable<FileRow> ordered = sortProperty switch
                {
                    nameof(FileRow.Name) => sortDescending ? found.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase) : found.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
                    nameof(FileRow.FullPath) => sortDescending ? found.OrderByDescending(row => row.FullPath, StringComparer.OrdinalIgnoreCase) : found.OrderBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase),
                    nameof(FileRow.Type) => sortDescending ? found.OrderByDescending(row => row.Type, StringComparer.OrdinalIgnoreCase) : found.OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase),
                    nameof(FileRow.AgeDays) => sortDescending ? found.OrderByDescending(row => row.AgeDays) : found.OrderBy(row => row.AgeDays),
                    nameof(FileRow.DuplicateSort) => sortDescending ? found.OrderByDescending(row => row.DuplicateSort).ThenBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase) : found.OrderBy(row => row.DuplicateSort).ThenBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase),
                    nameof(FileRow.Modified) => sortDescending ? found.OrderByDescending(row => row.Modified) : found.OrderBy(row => row.Modified),
                    _ => sortDescending ? found.OrderByDescending(row => row.Size).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase) : found.OrderBy(row => row.Size).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                };
                return largestOnly ? ordered.Take(25).ToList() : ordered.ToList();
            }, token);
            if (token.IsCancellationRequested || request != _filterRequest) return null;
            _visibleRows.ReplaceAll(matches); _visibleExtraCount = matches.Count(row => row.DuplicateExtra);
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
    private void ViewFilterBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewFilterBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item) _selectedViewFilter = item.Content?.ToString() ?? "All files";
        if (PresetDescriptionText is not null) PresetDescriptionText.Text = _selectedViewFilter switch
        {
            "Large · 100 MB+" => "Large files · at least 100 MiB each, sorted by the current sort order.",
            "Older · 6 months+" => "Older files · last modified at least 183 days ago; age alone does not mean safe to remove.",
            "Exact duplicates" => "Exact duplicates · file contents match after you run Verify same-size; nothing is auto-selected.",
            "Same-size candidates" => "Duplicate candidates · same size only; contents have not been compared yet.",
            "Temporary files" => "Temporary files · recognized browser cache, or user Temp .tmp/.temp files unchanged for 7+ days.",
            "Selected only" => "Selected only · checked files across the full scan, even when other filters hid them.",
            _ => "All files · choose a review preset to see its exact rules."
        };
        if (IsLoaded) _ = ApplyCurrentFiltersAsync();
    }
    private void FolderFilterBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FolderFilterBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item) _selectedFolderFilter = item.Tag as string ?? "";
        if (IsLoaded) _ = ApplyCurrentFiltersAsync();
    }
    private void PopulateFolderFilter()
    {
        FolderFilterBox.SelectionChanged -= FolderFilterBox_SelectionChanged;
        FolderFilterBox.Items.Clear(); FolderFilterBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "All folders", Tag = "" });
        foreach (var folder in _rows.Select(row => Path.GetDirectoryName(row.RelativePath)?.Split(Path.DirectorySeparatorChar).FirstOrDefault() ?? "")
                     .Where(folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).Take(250))
            FolderFilterBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = folder, Tag = folder });
        FolderFilterBox.SelectedIndex = 0; _selectedFolderFilter = "";
        FolderFilterBox.SelectionChanged += FolderFilterBox_SelectionChanged;
    }
    private void MarkDuplicateCandidates(IEnumerable<FileRow> files)
    {
        var sizes = files.GroupBy(row => row.Size).Where(group => group.Key >= 0 && group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var row in files) row.SizeCandidate = sizes.Contains(row.Size) && row.Size <= 268_435_456;
    }
    private async void VerifyDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (!_scanComplete || _hashingDuplicates) return;
        var candidates = _rows.Where(row => row.SizeCandidate).ToList(); if (candidates.Count == 0) return;
        if (MessageBox.Show($"Compare {candidates.Count:N0} same-size candidates (up to 256 MB each) using local SHA-256 hashes? Files are only read; nothing is changed.", "Verify duplicate content", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        _hashingDuplicates = true; VerifyDuplicatesButton.IsEnabled = false; CancelScanButton.Visibility = Visibility.Visible; CancelScanButton.IsEnabled = true;
        _scanCancellation = new CancellationTokenSource(); var token = _scanCancellation.Token;
        ScanProgress.Visibility = Visibility.Visible; ScanProgress.IsIndeterminate = true; PauseScanButton.Visibility = Visibility.Visible; PauseScanButton.IsEnabled = true; _scanPaused = false; _scanPauseGate.Set();
        var progress = new Progress<int>(done => ProgressText.Text = $"Hashing candidates · {done:N0} / {candidates.Count:N0}");
        try
        {
            var (_, errors) = await Task.Run(() => FindDuplicates(candidates, token, progress, _scanPauseGate));
            DuplicatesText.Text = $"{_rows.Count(row => row.DuplicateExtra):N0} exact extras";
            StatusText.Text = errors == 0 ? "Duplicate verification complete · hashes stayed on this PC" : $"Verification complete with {errors:N0} unreadable or changed candidate(s)";
            await ApplyCurrentFiltersAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Duplicate verification canceled · partial hashing results were not labeled"; foreach (var row in candidates) { row.Duplicate = false; row.DuplicateExtra = false; row.DuplicateGroup = 0; } }
        finally
        {
            _hashingDuplicates = false; _scanCancellation?.Dispose(); _scanCancellation = null; _scanPaused = false; _scanPauseGate.Set();
            ScanProgress.Visibility = PauseScanButton.Visibility = CancelScanButton.Visibility = Visibility.Collapsed; ScanProgress.IsIndeterminate = false; ProgressText.Text = "";
            VerifyDuplicatesButton.IsEnabled = _rows.Any(row => row.SizeCandidate); UpdateMarkedSummary();
        }
    }
    private void LoadOrganizationUndo()
    {
        try { if (File.Exists(OrganizationLogPath) && new FileInfo(OrganizationLogPath).Length <= 1_048_576) _lastOrganizationMoves.AddRange(JsonSerializer.Deserialize<List<OrganizationMove>>(File.ReadAllText(OrganizationLogPath))?.Where(move => move.Bytes >= 0 && !string.IsNullOrWhiteSpace(move.Source) && !string.IsNullOrWhiteSpace(move.Destination) && Path.GetFileName(move.Source).Equals(Path.GetFileName(move.Destination), StringComparison.OrdinalIgnoreCase)).Take(5000) ?? []); }
        catch { _lastOrganizationMoves.Clear(); }
        if (UndoOrganizationButton is not null) UndoOrganizationButton.IsEnabled = _lastOrganizationMoves.Count > 0;
    }
    private static string OrganizationLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyCircuit", "organization-undo.json");
    private async void OrganizePreview_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Marked).ToList();
        if (selected.Count == 0) { MessageBox.Show("Select files to preview organization.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new OpenFolderDialog { Title = "Choose a destination folder for the organization preview" };
        if (dialog.ShowDialog(this) != true) return;
        var safe = selected.Where(row => RecycleBlockReason(row, string.IsNullOrWhiteSpace(row.ScanRoot) ? _selectedRoot : row.ScanRoot) is null).ToList();
        var plan = safe.Select(row => new OrganizationMove(row.FullPath, Path.Combine(dialog.FolderName, SafeOrganizationFolder(row), Path.GetFileName(row.FullPath)), row.Size, row.Modified.ToUniversalTime().Ticks)).ToList();
        var destinationBlock = OrganizationDestinationBlockReason(dialog.FolderName);
        if (destinationBlock is not null) { MessageBox.Show($"This destination is protected or unsupported: {destinationBlock}. Choose a normal local folder you own.", "Destination blocked", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (plan.Count == 0) { MessageBox.Show("No selected file passed the local-file safety checks. Protected, linked, changed, and unsupported paths cannot be organized.", "Nothing eligible", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var conflicts = plan.Count(move => File.Exists(move.Destination));
        var rowsByPath = safe.ToDictionary(row => row.FullPath, StringComparer.OrdinalIgnoreCase);
        var preview = string.Join(Environment.NewLine, plan.Take(8).Select(move => $"{Path.GetFileName(move.Source)}  →  {Path.GetRelativePath(dialog.FolderName, move.Destination)}"));
        if (plan.Count > 8) preview += $"{Environment.NewLine}… and {plan.Count - 8:N0} more";
        if (MessageBox.Show($"Preview: {plan.Count:N0} eligible files · {Format(plan.Sum(move => move.Bytes))} · {selected.Count - plan.Count:N0} protected/changed file(s) excluded · {conflicts:N0} destination conflict(s), which will be skipped.\n\n{preview}\n\nMoves are user-approved; exact source and destination paths are saved locally for Undo.", "Review organization plan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var moved = new List<OrganizationMove>(); var skipped = 0;
        await Task.Run(() => { foreach (var move in plan) { try { if (!rowsByPath.TryGetValue(move.Source, out var row) || RecycleBlockReason(row, string.IsNullOrWhiteSpace(row.ScanRoot) ? _selectedRoot : row.ScanRoot) is not null || OrganizationDestinationBlockReason(dialog.FolderName) is not null || File.Exists(move.Destination) || !File.Exists(move.Source)) { skipped++; continue; } Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!); File.Move(move.Source, move.Destination); moved.Add(move); } catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; } } });
        _lastOrganizationMoves.AddRange(moved);
        try { Directory.CreateDirectory(Path.GetDirectoryName(OrganizationLogPath)!); File.WriteAllText(OrganizationLogPath, JsonSerializer.Serialize(_lastOrganizationMoves)); } catch { }
        UndoOrganizationButton.IsEnabled = moved.Count > 0;
        _rows.RemoveRange(selected.Where(row => moved.Any(move => move.Source.Equals(row.FullPath, StringComparison.OrdinalIgnoreCase)))); await ApplyCurrentFiltersAsync();
        StatusText.Text = $"Organized {moved.Count:N0} files · {skipped:N0} skipped · Undo available";
    }
    private static string SafeOrganizationFolder(FileRow row)
    {
        var extension = row.Type.TrimStart('.').ToLowerInvariant();
        if (new[] { "jpg", "jpeg", "png", "gif", "webp", "heic" }.Contains(extension)) return "Photos";
        if (new[] { "mp4", "mov", "mkv", "avi" }.Contains(extension)) return "Videos";
        if (new[] { "pdf", "doc", "docx", "xls", "xlsx", "txt", "ppt", "pptx" }.Contains(extension)) return "Documents";
        if (new[] { "mp3", "wav", "flac", "m4a" }.Contains(extension)) return "Audio";
        if (new[] { "zip", "7z", "rar", "tar", "gz" }.Contains(extension)) return "Archives";
        return "Other files";
    }
    private static string? OrganizationDestinationBlockReason(string destination)
    {
        try
        {
            var full = Path.GetFullPath(destination); var volume = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(volume) || volume.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(volume).DriveType != DriveType.Fixed) return "not on a supported local fixed drive";
            if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ProtectedPathParts.Contains)) return "inside a protected Windows or app folder";
            if (HasPathFolder(full, "AppData")) return "inside protected app data";
            if (HasReparsePointInPath(Path.Combine(full, ".tidycircuit-check"))) return "passes through a linked folder";
            if ((File.GetAttributes(full) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0) return "hidden, system, linked, or read-only";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return "unavailable or not a normal writable folder"; }
    }
    private async void UndoOrganization_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOrganizationMoves.Count == 0) return;
        var examples = string.Join(Environment.NewLine, _lastOrganizationMoves.Take(6).Select(move => $"{Path.GetFileName(move.Destination)}  →  {move.Source}"));
        if (_lastOrganizationMoves.Count > 6) examples += $"{Environment.NewLine}… and {_lastOrganizationMoves.Count - 6:N0} more";
        if (MessageBox.Show($"Undo up to {_lastOrganizationMoves.Count:N0} saved move(s) ({Format(_lastOrganizationMoves.Sum(move => Math.Max(0, move.Bytes)))}). Existing source files and changed or linked paths will be skipped.\n\n{examples}", "Review Undo plan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var restored = 0; var conflicts = 0;
        foreach (var move in _lastOrganizationMoves.AsEnumerable().Reverse().ToList())
        {
            if (!File.Exists(move.Destination) || File.Exists(move.Source) || OrganizationDestinationBlockReason(Path.GetDirectoryName(move.Source)!) is not null || OrganizationDestinationBlockReason(Path.GetDirectoryName(move.Destination)!) is not null) { conflicts++; continue; }
            try { var info = new FileInfo(move.Destination); if (info.Length != move.Bytes || (move.ModifiedUtcTicks > 0 && info.LastWriteTimeUtc.Ticks != move.ModifiedUtcTicks)) { conflicts++; continue; } }
            catch (IOException) { conflicts++; continue; }
            try { Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!); File.Move(move.Destination, move.Source); restored++; _lastOrganizationMoves.Remove(move); } catch (IOException) { conflicts++; } catch (UnauthorizedAccessException) { conflicts++; }
        }
        try { File.WriteAllText(OrganizationLogPath, JsonSerializer.Serialize(_lastOrganizationMoves)); } catch { }
        UndoOrganizationButton.IsEnabled = _lastOrganizationMoves.Count > 0; StatusText.Text = $"Undo restored {restored:N0} files · {conflicts:N0} conflict(s)";
        if (!string.IsNullOrWhiteSpace(_selectedRoot)) await ScanFolderAsync(_selectedRoot);
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
    private void ShowFolderMap_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedRoot) || _automaticScan)
        {
            MessageBox.Show("Choose and scan one folder first. The folder map uses only that scan's file sizes.", "Folder map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var rootLabel = Path.GetFileName(Path.TrimEndingDirectorySeparator(_selectedRoot));
        if (string.IsNullOrWhiteSpace(rootLabel)) rootLabel = _selectedRoot;
        var groups = _rows.GroupBy(row =>
        {
            var relative = row.RelativePath;
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 1 ? $"Files directly in {rootLabel}" : parts[0];
        }, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Files = group.Count(), Bytes = group.Sum(row => Math.Max(0, row.Size)) })
            .OrderByDescending(group => group.Bytes).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var largest = groups.FirstOrDefault()?.Bytes ?? 0;
        var total = groups.Sum(group => group.Bytes);
        var partial = !_scanComplete ? " · partial scan" : "";
        var dialog = new Window
        {
            Owner = this, Title = "Space by folder", Width = 760, Height = 660, MinWidth = 600, MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F3F8F6")!,
            ShowInTaskbar = false
        };
        var layout = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Where your space is going", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#143E50")! });
        header.Children.Add(new TextBlock { Text = $"Top-level folders in {rootLabel} · {Format(total)} shown{partial} · read-only map", FontSize = 12, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#638287")!, Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        if (groups.Count == 0)
            layout.Children.Add(new TextBlock { Text = "No files were found in this scan yet.", VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#638287")! });
        else
        {
            var stack = new StackPanel();
            foreach (var group in groups)
            {
                var card = new Border { Background = System.Windows.Media.Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 10, 13, 9), Margin = new Thickness(0, 0, 0, 8) };
                var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#143E50")!, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = group.Name };
                var detail = new TextBlock { Text = $"{group.Files:N0} files", FontSize = 10, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#829498")!, Margin = new Thickness(0, 3, 0, 0) };
                var size = new TextBlock { Text = Format(group.Bytes), FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#087D86")!, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = largest <= 0 ? 0 : 100d * group.Bytes / largest, Height = 6, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#31B5A6")!, Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#E7F5F2")!, Margin = new Thickness(0, 8, 0, 0) };
                Grid.SetRow(name, 0); Grid.SetColumn(name, 0); grid.Children.Add(name); Grid.SetRow(detail, 1); grid.Children.Add(detail);
                Grid.SetRow(size, 0); Grid.SetColumn(size, 1); Grid.SetRowSpan(size, 2); grid.Children.Add(size);
                Grid.SetRow(bar, 2); Grid.SetColumnSpan(bar, 2); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.Children.Add(bar);
                card.Child = grid; stack.Children.Add(card);
            }
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
            layout.Children.Add(scroll);
        }
        dialog.Content = layout; dialog.ShowDialog();
    }
    private void CleanupHistory_Click(object sender, RoutedEventArgs e) => ShowCleanupHistory();
    private void ShowCleanupHistory()
    {
        var entries = new List<CleanupHistoryEntry>();
        try
        {
            if (File.Exists(OperationLogPath))
            {
                foreach (var line in File.ReadLines(OperationLogPath).TakeLast(5000))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line); var item = json.RootElement;
                        var action = item.TryGetProperty("action", out var actionNode) ? actionNode.GetString() : null;
                        var result = item.TryGetProperty("result", out var resultNode) ? resultNode.GetString() : null;
                        if (!string.Equals(action, "recycle", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result) || result == "requested") continue;
                        var path = item.TryGetProperty("path", out var pathNode) ? pathNode.GetString() ?? "" : "";
                        var bytes = item.TryGetProperty("bytes", out var bytesNode) && bytesNode.TryGetInt64(out var value) ? value : 0;
                        var time = item.TryGetProperty("timeUtc", out var timeNode) && DateTimeOffset.TryParse(timeNode.GetString(), out var parsed) ? parsed.ToLocalTime() : DateTimeOffset.MinValue;
                        entries.Add(new(time, path, bytes, HistoryResultLabel(result), result));
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not read the local cleanup history. {ex.Message}", "Cleanup history", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        entries = entries.OrderByDescending(item => item.Time).Take(1000).ToList();
        var dialog = new Window { Owner = this, Title = "Cleanup history", Width = 980, Height = 620, MinWidth = 700, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false };
        var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        var summary = new TextBlock { Text = entries.Count == 0 ? "No cleanup actions recorded yet." : $"Latest {entries.Count:N0} recorded cleanup results · logs stay on this PC", Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#638287")!, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(summary, Dock.Left); footer.Children.Add(summary);
        var openBin = new Button { Content = "Open Recycle Bin", Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#087D86")!, Foreground = System.Windows.Media.Brushes.White, Padding = new Thickness(14, 8, 14, 8) };
        openBin.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show($"Could not open the Recycle Bin. {ex.Message}", "Recycle Bin", MessageBoxButton.OK, MessageBoxImage.Information); } };
        footer.Children.Add(openBin); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new DataGrid { ItemsSource = entries, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        grid.Columns.Add(new DataGridTextColumn { Header = "WHEN", Binding = new Binding(nameof(CleanupHistoryEntry.WhenDisplay)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FILE", Binding = new Binding(nameof(CleanupHistoryEntry.FileName)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FULL PATH", Binding = new Binding(nameof(CleanupHistoryEntry.Path)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "SIZE", Binding = new Binding(nameof(CleanupHistoryEntry.SizeDisplay)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "RESULT", Binding = new Binding(nameof(CleanupHistoryEntry.Status)), Width = 250 });
        root.Children.Add(grid); dialog.Content = root; dialog.ShowDialog();
    }
    private static string HistoryResultLabel(string result)
    {
        if (result.StartsWith("moved", StringComparison.OrdinalIgnoreCase)) return "Moved to Recycle Bin";
        if (result.StartsWith("skipped:", StringComparison.OrdinalIgnoreCase)) return FriendlySkipReason(result[8..].Trim());
        if (result.StartsWith("failed:", StringComparison.OrdinalIgnoreCase)) return "Could not complete · see local error type";
        return result;
    }
    private void SetActiveNavigation()
    {
        if (OverviewNavButton is null) return;
        var buttons = new[] { OverviewNavButton, SmartFindNavButton, ReviewFilesNavButton, GuideNavButton, SystemNavButton };
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
    private void ChatSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string question }) AddChatExchange(question);
    }
    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendChatMessage();
    private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { SendChatMessage(); e.Handled = true; }
    }
    private void SendChatMessage()
    {
        var question = ChatInput.Text.Trim();
        if (question.Length == 0) return;
        AddChatExchange(question); ChatInput.Clear(); ChatInput.Focus();
    }
    private void AddChatExchange(string question)
    {
        _chatMessages.Add(GuideChatMessage.User(question));
        var normalized = question.ToLowerInvariant();
        var topic = normalized.Contains("privacy") || normalized.Contains("upload") || normalized.Contains("data") ? "privacy"
            : normalized.Contains("duplicate") || normalized.Contains("copy") || normalized.Contains("hash") ? "duplicates"
            : normalized.Contains("clean") || normalized.Contains("cache") || normalized.Contains("temporary") || normalized.Contains("delete") || normalized.Contains("remove") ? "cleanup"
            : normalized.Contains("virus") || normalized.Contains("malware") || normalized.Contains("defender") || normalized.Contains("security") ? "security"
            : normalized.Contains("ram") || normalized.Contains("battery") || normalized.Contains("health") || normalized.Contains("storage") || normalized.Contains("system details") ? "health"
            : normalized.Contains("sort") || normalized.Contains("organ") || normalized.Contains("folder") || normalized.Contains("file") ? "organize"
            : normalized.Contains("it") || normalized.Contains("that") || normalized.Contains("more") || normalized.Contains("how") ? _lastGuideTopic
            : "";
        var answer = topic switch
        {
            "privacy" => "Your scans stay on this PC. File names and hashes aren’t uploaded. If you export a report or move files, those details stay in the locations Windows shows you.",
            "duplicates" => "I first group files with the same size, then compare their contents with SHA-256 when you choose Verify. Exact matches are labeled for review; the app never picks files to remove on its own.",
            "cleanup" => "Find temporary files looks for supported browser cache files and older .tmp/.temp files in your user Temp folder. You can review the paths and confirm a move to Recycle Bin. It doesn’t clear Windows or general app caches.",
            "security" => "I can’t check for viruses. TidyCircuit can open Windows Security, where Microsoft Defender can run the scan.",
            "health" => "Open System details and choose Refresh for Windows-reported RAM, battery, and fixed-drive readings. They’re read-only snapshots, not a full PC health diagnosis.",
            "organize" => "In Review files, click a column title to sort, or use search and filters to narrow the list. Select the files you want, then preview an organization move before confirming it.",
            _ => "I can help with finding files, duplicates, cleanup, privacy, or Windows Security. What are you trying to do?"
        };
        if (!string.IsNullOrEmpty(topic)) _lastGuideTopic = topic;
        _chatMessages.Add(GuideChatMessage.Assistant(answer));
        if (_chatMessages.Count > 61) _chatMessages.RemoveAt(1);
        ChatMessagesList.ScrollIntoView(_chatMessages[^1]);
    }
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
    private static bool IsRecognizedUserTempFile(FileRow row)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(row.FullPath);
            var extension = Path.GetExtension(full);
            return Path.GetDirectoryName(full)?.Equals(tempRoot, StringComparison.OrdinalIgnoreCase) == true
                && (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".temp", StringComparison.OrdinalIgnoreCase))
                && row.Modified.ToUniversalTime() <= DateTime.UtcNow.AddDays(-7);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
    private void ShowFirstRunGuide()
    {
        var guideFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyCircuit", "welcome-seen-v1");
        try { if (File.Exists(guideFlag)) return; } catch (IOException) { }
        var dialog = new Window
        {
            Owner = this, Title = "Welcome to TidyCircuit", Width = 540, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false
        };
        var content = new StackPanel { Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { Text = "A calm, local file review", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#143E50")! });
        content.Children.Add(new TextBlock { Text = "Choose a folder to review large, old, or duplicate files, or scan recognized browser cache and older user temporary files.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#45616A")! });
        content.Children.Add(new TextBlock { Text = "Your scan stays on this PC. Windows and protected application folders, linked paths, and system files are blocked from cleanup. Nothing is selected or changed automatically. Confirmed file cleanup moves eligible files to the Recycle Bin.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#45616A")! });
        content.Children.Add(new TextBlock { Text = "TidyCircuit does not detect malware or tune PC settings. Use Windows Security for threat scans.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18), Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#8B5D32")! });
        var button = new Button { Content = "Got it · show me around", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#087D86")!, Foreground = System.Windows.Media.Brushes.White };
        button.Click += (_, _) => dialog.DialogResult = true; content.Children.Add(button); dialog.Content = content;
        if (dialog.ShowDialog() == true)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(guideFlag)!); File.WriteAllText(guideFlag, DateTime.UtcNow.ToString("O"), new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void UpdateMarkedSummary()
    {
        if (ReviewCountText is null) return;
        var marked = _rows.Count(row => row.Marked);
        var markedBytes = _rows.Where(row => row.Marked).Sum(row => Math.Max(0, row.Size));
        var shownMarked = FilesGrid.Items.OfType<FileRow>().Count(row => row.Marked);
        ReviewCountText.Text = $"{FilesGrid.Items.Count:N0} shown · {shownMarked:N0} selected here · {marked:N0} selected overall · {Format(markedBytes)}";
        if (OverviewMarkedText is not null) OverviewMarkedText.Text = $"{marked:N0} · {Format(markedBytes)}";
        if (ReviewTrayText is not null) ReviewTrayText.Text = $"Review tray · {marked:N0} file(s) · {Format(markedBytes)} · {shownMarked:N0} visible";
        if (ReviewTrayButton is not null) ReviewTrayButton.IsEnabled = marked > 0;
        if (RecycleButton is not null) RecycleButton.IsEnabled = marked > 0 && _scanComplete;
        if (SelectVisibleExtrasButton is not null) SelectVisibleExtrasButton.IsEnabled = _visibleExtraCount > 0;
        if (SelectAllButton is not null) SelectAllButton.Content = $"Select all shown ({FilesGrid.Items.Count:N0})";
    }
    private void ReviewSelected_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 2; SetActiveNavigation();
        if (ViewFilterBox is not null) ViewFilterBox.SelectedItem = ViewFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == "Selected only");
        StatusText.Text = $"Reviewing {_rows.Count(row => row.Marked):N0} selected file(s) across all filters · selection retained";
    }
    private void ClearAllSelection_Click(object sender, RoutedEventArgs e)
    {
        SetMarks(_rows, false);
        StatusText.Text = "Selection cleared across all results · no files changed";
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
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetMarks(FilesGrid.Items.OfType<FileRow>(), true);
    private void MarkVisible_Click(object sender, RoutedEventArgs e)
    {
        ReplaceSelectionWith(FilesGrid.Items.OfType<FileRow>());
    }
    private void SelectVisibleExtras_Click(object sender, RoutedEventArgs e)
    {
        var extras = FilesGrid.Items.OfType<FileRow>().Where(row => row.DuplicateExtra).ToList();
        ReplaceSelectionWith(extras);
        StatusText.Text = $"Selected {extras.Count:N0} exact duplicate copies for review · nothing moved";
    }
    private void ReplaceSelectionWith(IEnumerable<FileRow> rows)
    {
        var selected = rows.ToHashSet();
        _isUpdatingMarks = true;
        try { foreach (var row in _rows) row.Marked = selected.Contains(row); }
        finally { _isUpdatingMarks = false; }
        UpdateMarkedSummary();
    }
    private void ClearMarks_Click(object sender, RoutedEventArgs e) => SetMarks(FilesGrid.Items.OfType<FileRow>(), false);
    private static string OperationLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyCircuit", "operations.jsonl");
    private static void WriteOperation(StreamWriter writer, string action, FileRow row, string result)
    {
        var entry = JsonSerializer.Serialize(new { timeUtc = DateTime.UtcNow, action, path = row.FullPath, bytes = row.Size, result });
        writer.WriteLine(entry);
    }
    private static string? RecycleBlockReason(FileRow row, string root)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(row.FullPath); var relative = Path.GetRelativePath(rootFull, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return "outside the selected folder";
            var volume = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(volume) || volume.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(volume).DriveType != DriveType.Fixed) return "not on a local fixed drive with standard Recycle Bin recovery";
            if ((File.GetAttributes(rootFull) & FileAttributes.ReparsePoint) != 0) return "selected folder is a link";
            if (HasReparsePointInPath(full)) return "path passes through a linked folder";
            var recognizedBrowserCache = IsRecognizedBrowserCacheFile(full);
            var recognizedUserTemp = IsRecognizedUserTempFile(row);
            if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ProtectedPathParts.Contains)) return "inside a protected Windows or app folder";
            if (HasPathFolder(full, "AppData") && !recognizedBrowserCache && !recognizedUserTemp) return "inside a protected app-data folder; only recognized cache or older temporary files can be reviewed";
            if (ProtectedFileTypes.Contains(Path.GetExtension(full))) return "an app, system, script, or shortcut file";
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly)) != 0) return "hidden, system, linked, read-only, or not a regular file";
            var info = new FileInfo(full);
            if (info.Length != row.Size || info.LastWriteTime != row.Modified) return "changed since the scan";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unavailable or changed since scanning"; }
    }
    private static bool HasReparsePointInPath(string file)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(file));
        while (!string.IsNullOrWhiteSpace(current))
        {
            var normalized = Path.GetFullPath(current);
            if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(normalized);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(Path.GetFullPath(parent), normalized, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
        return false;
    }
    private static string FriendlySkipReason(string? reason)
    {
        var value = reason ?? "unavailable";
        if (value.Contains("inside a protected Windows or app folder", StringComparison.OrdinalIgnoreCase) || value.Contains("protected app-data", StringComparison.OrdinalIgnoreCase)) return "Protected Windows/app location · left untouched";
        if (value.Contains("changed since", StringComparison.OrdinalIgnoreCase)) return "Changed since scan · scan again before cleanup";
        if (value.Contains("linked folder", StringComparison.OrdinalIgnoreCase) || value.Contains("folder is a link", StringComparison.OrdinalIgnoreCase)) return "Linked folder · skipped for safety";
        if (value.Contains("local fixed drive", StringComparison.OrdinalIgnoreCase)) return "Drive does not support the app’s safe Recycle Bin workflow";
        if (value.Contains("outside the selected folder", StringComparison.OrdinalIgnoreCase)) return "Outside the selected folder · left untouched";
        if (value.Contains("app, system, script, or shortcut", StringComparison.OrdinalIgnoreCase)) return "Program/system file · protected";
        if (value.Contains("hidden, system", StringComparison.OrdinalIgnoreCase)) return "Hidden, system, or read-only item · protected";
        if (value.Contains("in use", StringComparison.OrdinalIgnoreCase) || value.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) return "Busy or unavailable · close the app using it, then scan again";
        if (value.Contains("access denied", StringComparison.OrdinalIgnoreCase)) return "Windows denied access · left untouched";
        if (value.Contains("failed:", StringComparison.OrdinalIgnoreCase)) return "Windows could not complete the move · left in place";
        return value;
    }
    private static string DescribeBlockedItems(IEnumerable<(FileRow Row, string? Reason)> blocked) => string.Join(Environment.NewLine,
        blocked.GroupBy(item => item.Reason ?? "unavailable")
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => $"• {group.Count():N0} · {FriendlySkipReason(group.Key)} (example: {Path.GetFileName(group.First().Row.FullPath)})"));
    private void ShowSkippedItemsWindow(IEnumerable<CleanupSkipItem> items)
    {
        var list = items.Take(2000).ToList(); if (list.Count == 0) return;
        var dialog = new Window { Owner = this, Title = "Skipped files · why they stayed", Width = 900, Height = 560, MinWidth = 650, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false };
        var root = new DockPanel { Margin = new Thickness(18) };
        var note = new TextBlock { Text = $"{list.Count:N0} item(s) were left in place. Close apps holding files, rescan changed files, or keep protected items untouched.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#638287")!, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var grid = new DataGrid { ItemsSource = list, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        grid.Columns.Add(new DataGridTextColumn { Header = "FILE", Binding = new Binding(nameof(CleanupSkipItem.Name)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FULL PATH", Binding = new Binding(nameof(CleanupSkipItem.FullPath)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "SIZE", Binding = new Binding(nameof(CleanupSkipItem.SizeDisplay)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "WHY IT WAS SKIPPED", Binding = new Binding(nameof(CleanupSkipItem.FriendlyReason)), Width = 280 });
        root.Children.Add(grid); dialog.Content = root; dialog.ShowDialog();
    }
    private bool ConfirmRecycleSelection(IReadOnlyList<FileRow> eligible, long totalBytes, string safetyNotes)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Review files before cleanup",
            Width = 820,
            Height = 580,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        summary.Children.Add(new TextBlock { Text = $"Move {eligible.Count:N0} files · {Format(totalBytes)}", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DarkSlateGray });
        summary.Children.Add(new TextBlock { Text = "Review every full path below. Nothing moves until you confirm.", Margin = new Thickness(0, 4, 0, 0), Foreground = System.Windows.Media.Brushes.DimGray });
        Grid.SetRow(summary, 0); layout.Children.Add(summary);
        var preview = new DataGrid
        {
            ItemsSource = eligible.OrderBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserSortColumns = true,
            EnableRowVirtualization = true,
            RowHeight = 34,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single
        };
        preview.Columns.Add(new DataGridTextColumn { Header = "FULL PATH", Binding = new Binding(nameof(FileRow.FullPath)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 350 });
        preview.Columns.Add(new DataGridTextColumn { Header = "SIZE", Binding = new Binding(nameof(FileRow.SizeDisplay)), Width = new DataGridLength(100), MinWidth = 80 });
        Grid.SetRow(preview, 1); layout.Children.Add(preview);
        if (!string.IsNullOrWhiteSpace(safetyNotes))
        {
            var notes = new TextBlock { Text = safetyNotes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.DimGray, MaxHeight = 76 };
            Grid.SetRow(notes, 2); layout.Children.Add(notes);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 92, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 8, 14, 8) };
        var confirm = new Button { Content = "Move to Recycle Bin", IsDefault = true, MinWidth = 160, Padding = new Thickness(14, 8, 14, 8), Background = System.Windows.Media.Brushes.DarkSlateGray, Foreground = System.Windows.Media.Brushes.White };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }
    private async void RecycleMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = _rows.Where(r => r.Marked).ToList(); if (marked.Count == 0) return;
        StatusText.Text = $"Checking {marked.Count:N0} selected files…";
        ScanProgress.Visibility = Visibility.Visible; ScanProgress.IsIndeterminate = true;
        ScanButton.IsEnabled = FolderButton.IsEnabled = RecycleButton.IsEnabled = false;
        MainTabs.IsEnabled = FilesGrid.IsEnabled = SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = false;
        List<(FileRow Row, string? Reason)> eligibility;
        try { eligibility = await Task.Run(() => marked.Select(row => (Row: row, Reason: RecycleBlockReason(row, string.IsNullOrWhiteSpace(row.ScanRoot) ? _selectedRoot : row.ScanRoot))).ToList()); }
        catch (Exception ex)
        {
            StatusText.Text = "Could not check the selected files";
            MessageBox.Show($"Could not check the selected files. No files were moved. {ex.Message}", "Safety check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            ScanProgress.IsIndeterminate = false; ScanProgress.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = FolderButton.IsEnabled = true;
            MainTabs.IsEnabled = FilesGrid.IsEnabled = SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = true;
            UpdateMarkedSummary();
        }
        var eligible = eligibility.Where(item => item.Reason is null).Select(item => item.Row).ToList();
        var blocked = eligibility.Where(item => item.Reason is not null).ToList();
        if (eligible.Count == 0)
        {
            StatusText.Text = "No selected files are eligible to move";
            ShowSkippedItemsWindow(blocked.Select(item => new CleanupSkipItem(item.Row.FullPath, item.Row.Size, FriendlySkipReason(item.Reason))));
            return;
        }
        var total = eligible.Sum(row => row.Size);
        var cacheOnly = eligible.All(row => IsRecognizedBrowserCacheFile(row.FullPath));
        var oldTempOnly = eligible.All(IsRecognizedUserTempFile);
        var notes = new List<string>();
        if (blocked.Count > 0) notes.Add($"{blocked.Count:N0} protected, changed, or unsupported file(s) will be skipped and stay selected: {DescribeBlockedItems(blocked)}");
        if (cacheOnly) notes.Add("Browser cache only. Pages may reload or rebuild cache; cookies, history, saved passwords, and bookmarks are not included.");
        if (oldTempOnly) notes.Add("Only .tmp/.temp files in your user Temp folder that have not changed for at least 7 days.");
        notes.Add("Windows Recycle Bin recovery depends on drive settings. Verify the files appear there before relying on recovery. Paths are recorded in the local operation log.");
        if (!ConfirmRecycleSelection(eligible, total, string.Join("\n", notes))) { StatusText.Text = "Cleanup canceled · no files changed"; return; }
        ScanButton.IsEnabled = FolderButton.IsEnabled = RecycleButton.IsEnabled = false;
        MainTabs.IsEnabled = FilesGrid.IsEnabled = SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = false;
        PauseScanButton.Visibility = Visibility.Collapsed;
        CancelScanButton.Content = "Cancel cleanup"; CancelScanButton.IsEnabled = true; CancelScanButton.Visibility = Visibility.Visible;
        ScanProgress.Visibility = Visibility.Visible; ScanProgress.IsIndeterminate = false; ScanProgress.Maximum = eligible.Count; ScanProgress.Value = 0;
        StatusText.Text = "Moving selected files to the Recycle Bin";
        _cleanupCancellation = new CancellationTokenSource();
        var cleanupToken = _cleanupCancellation.Token;
        var cleanupProgress = new Progress<(int Done, int Total, string Name)>(update =>
        {
            ProgressText.Text = $"Moving {update.Done:N0} of {update.Total:N0} · {update.Name}";
            ScanProgress.Value = update.Done;
        });
        try
        {
            var result = await Task.Run(() => RecycleEligibleFiles(eligible, _selectedRoot, cleanupToken, cleanupProgress));
            _rows.RemoveRange(result.Moved);
            FileCountText.Text = _rows.Count.ToString("N0"); FolderSizeText.Text = Format(_rows.Sum(r => r.Size));
            MarkDuplicateCandidates(_rows);
            DuplicatesText.Text = $"{_rows.Count(row => row.SizeCandidate):N0} candidates";
            await ApplyCurrentFiltersAsync(); UpdateMarkedSummary();
            var skipped = blocked.Count + result.Skipped;
            StatusText.Text = $"Moved {result.Moved.Count:N0} file(s) · {skipped:N0} skipped and remain selected · {(result.LogWarning ? "check operation log status" : "operation logged locally")}";
            ShowSkippedItemsWindow(blocked.Select(item => new CleanupSkipItem(item.Row.FullPath, item.Row.Size, FriendlySkipReason(item.Reason)))
                .Concat(result.SkippedItems));
            if (!string.IsNullOrWhiteSpace(result.StoppedOn))
                MessageBox.Show($"Cleanup stopped after handling {result.Moved.Count:N0} file(s). Could not move {result.StoppedOn}.\n\nCheck the Recycle Bin for files already moved.", "Cleanup stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (result.Cancelled)
                StatusText.Text = $"Cleanup canceled · moved {result.Moved.Count:N0} file(s) · remaining files were not changed";
            else if (result.Moved.Count > 0 && MessageBox.Show($"Moved {result.Moved.Count:N0} file(s) to the Recycle Bin. To recover one, open the Recycle Bin, right-click the file, and choose Restore.\n\nOpen the Recycle Bin now?", "Cleanup complete", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show($"Could not open the Recycle Bin. Open it from the desktop or File Explorer. {ex.Message}", "Recycle Bin", MessageBoxButton.OK, MessageBoxImage.Information); }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Cleanup stopped · check the Recycle Bin for files already moved";
            MessageBox.Show($"Cleanup stopped unexpectedly. {ex.Message}\n\nCheck the Recycle Bin for files already moved.", "Cleanup stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cleanupCancellation?.Dispose(); _cleanupCancellation = null;
            CancelScanButton.Content = "Cancel"; CancelScanButton.IsEnabled = false; CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false; ScanProgress.Visibility = Visibility.Collapsed; ScanProgress.Maximum = 100; ScanProgress.Value = 0;
            ScanButton.IsEnabled = FolderButton.IsEnabled = true;
            MainTabs.IsEnabled = FilesGrid.IsEnabled = SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = true;
            UpdateMarkedSummary();
        }
    }
    private const int RecycleBatchSize = 32;
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofNoConfirmMkdir = 0x0200;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShellFileOperation operation);
    private static int QueueRecycleBatch(IReadOnlyList<FileRow> rows, out bool aborted)
    {
        var multiString = string.Join('\0', rows.Select(row => Path.GetFullPath(row.FullPath))) + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(multiString);
        var source = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, source, bytes.Length);
            var operation = new ShellFileOperation
            {
                Function = FoDelete,
                From = source,
                Flags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofNoConfirmMkdir)
            };
            var result = SHFileOperationW(ref operation);
            aborted = operation.AnyOperationsAborted;
            return result;
        }
        finally { Marshal.FreeHGlobal(source); }
    }
    private static bool SourceStillExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }
    private static CleanupResult RecycleEligibleFiles(List<FileRow> eligible, string root, CancellationToken token, IProgress<(int Done, int Total, string Name)> progress)
    {
        var moved = new List<FileRow>(); var skipped = 0; var skippedItems = new List<CleanupSkipItem>(); var logWarning = false; string? stoppedOn = null; var completed = 0;
        var logPath = OperationLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024), new UTF8Encoding(false), 64 * 1024);
        void Report(FileRow row) => progress.Report((completed, eligible.Count, Path.GetFileName(row.FullPath)));
        void TryLog(string action, FileRow row, string result)
        {
            try { WriteOperation(log, action, row, result); }
            catch { logWarning = true; }
        }
        var offset = 0;
        while (offset < eligible.Count && !token.IsCancellationRequested && stoppedOn is null)
        {
            var batch = new List<FileRow>(RecycleBatchSize);
            while (offset < eligible.Count && batch.Count < RecycleBatchSize && !token.IsCancellationRequested)
            {
                var row = eligible[offset++];
                var changedReason = RecycleBlockReason(row, string.IsNullOrWhiteSpace(row.ScanRoot) ? root : row.ScanRoot);
                if (changedReason is null) { batch.Add(row); continue; }
                skipped++; skippedItems.Add(new(row.FullPath, row.Size, FriendlySkipReason(changedReason))); completed++; TryLog("recycle", row, "skipped: " + changedReason); Report(row);
            }
            if (batch.Count == 0 || token.IsCancellationRequested) continue;

            var volumeGroups = batch.GroupBy(row => Path.GetPathRoot(row.FullPath) ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var volumeGroup in volumeGroups)
            {
                if (token.IsCancellationRequested || stoppedOn is not null) break;
                var group = volumeGroup.ToList();
                try
                {
                    foreach (var row in group) WriteOperation(log, "recycle", row, "requested");
                    log.Flush();
                }
                catch (Exception)
                {
                    logWarning = true; skipped += group.Count; completed += group.Count;
                    stoppedOn = Path.GetFileName(group[0].FullPath);
                    foreach (var row in group) Report(row);
                    foreach (var row in group) skippedItems.Add(new(row.FullPath, row.Size, "Operation log unavailable · move stopped before changing files"));
                    return new CleanupResult(moved, skipped, skippedItems, logWarning, stoppedOn, token.IsCancellationRequested && moved.Count + skipped < eligible.Count);
                }

                var shellFailed = false;
                try
                {
                    var shellCode = QueueRecycleBatch(group, out var aborted);
                    shellFailed = shellCode != 0 || aborted;
                }
                catch { shellFailed = true; }

                foreach (var row in group)
                {
                    var fileStillThere = SourceStillExists(row.FullPath);
                    if (!fileStillThere)
                    {
                        moved.Add(row); completed++; TryLog("recycle", row, "moved"); Report(row);
                        continue;
                    }
                    if (token.IsCancellationRequested) continue;
                    try
                    {
                        FileSystem.DeleteFile(row.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        moved.Add(row); TryLog("recycle", row, shellFailed ? "moved by individual fallback" : "moved");
                    }
                    catch (IOException) { skipped++; skippedItems.Add(new(row.FullPath, row.Size, FriendlySkipReason("file unavailable or in use"))); TryLog("recycle", row, "skipped: file unavailable or in use"); }
                    catch (UnauthorizedAccessException) { skipped++; skippedItems.Add(new(row.FullPath, row.Size, FriendlySkipReason("access denied"))); TryLog("recycle", row, "skipped: access denied"); }
                    catch (Exception ex)
                    {
                        skipped++; skippedItems.Add(new(row.FullPath, row.Size, FriendlySkipReason("failed: " + ex.GetType().Name))); logWarning |= !TryLogFailure(log, row, ex);
                        stoppedOn ??= Path.GetFileName(row.FullPath);
                    }
                    completed++; Report(row);
                    if (stoppedOn is not null) break;
                }
                log.Flush();
            }
        }
        return new CleanupResult(moved, skipped, skippedItems, logWarning, stoppedOn, token.IsCancellationRequested && moved.Count + skipped < eligible.Count);
    }
    private static bool TryLogFailure(StreamWriter log, FileRow row, Exception exception)
    {
        try { WriteOperation(log, "recycle", row, "failed: " + exception.GetType().Name); return true; }
        catch { return false; }
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
    private async void RefreshSystem_Click(object sender, RoutedEventArgs e)
    {
        MemoryStatusText.Text = BatteryStatusText.Text = StorageStatusText.Text = "Reading…";
        var result = await Task.Run(() => ReadSystemSnapshot());
        MemoryStatusText.Text = result.MemoryHeadline; MemoryDetailText.Text = result.MemoryDetail;
        BatteryStatusText.Text = result.BatteryHeadline; BatteryDetailText.Text = result.BatteryDetail;
        StorageStatusText.Text = result.StorageHeadline; StorageDetailText.Text = result.StorageDetail;
        StatusText.Text = "Read-only Windows details refreshed · no settings changed";
    }
    private static SystemSnapshot ReadSystemSnapshot()
    {
        string memoryHeadline = "Unavailable", memoryDetail = "Windows did not return memory status.";
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            var used = memory.TotalPhysical - memory.AvailablePhysical;
            memoryHeadline = $"{Format((long)Math.Min(used, long.MaxValue))} / {Format((long)Math.Min(memory.TotalPhysical, long.MaxValue))}";
            memoryDetail = $"{memory.MemoryLoad}% in use · {Format((long)Math.Min(memory.AvailablePhysical, long.MaxValue))} available";
        }
        var power = new SystemPowerStatus();
        var batteryHeadline = "No battery reported"; var batteryDetail = "Desktop PCs may not have a battery.";
        if (GetSystemPowerStatus(ref power))
        {
            if (power.BatteryLifePercent <= 100) { batteryHeadline = $"{power.BatteryLifePercent}%"; batteryDetail = power.ACLineStatus == 1 ? "Charging or on AC power" : power.ACLineStatus == 0 ? "Running on battery" : "Power source unknown"; }
            else if (power.ACLineStatus == 1) { batteryHeadline = "On AC power"; batteryDetail = "Windows reports no battery percentage."; }
        }
        var drives = new List<string>(); long free = 0, total = 0; int count = 0;
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            try { if (!drive.IsReady) continue; free += drive.AvailableFreeSpace; total += drive.TotalSize; count++; drives.Add($"{drive.Name.TrimEnd('\\')} · {Format(drive.AvailableFreeSpace)} free of {Format(drive.TotalSize)}"); } catch (IOException) { }
        }
        var storageHeadline = count == 0 ? "Unavailable" : $"{Format(free)} free";
        var storageDetail = count == 0 ? "No ready local drives reported." : $"{count} drive(s) · {Format(total)} total\n" + string.Join("\n", drives.Take(5));
        return new(memoryHeadline, memoryDetail, batteryHeadline, batteryDetail, storageHeadline, storageDetail);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    private void Safety_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("TidyCircuit does not detect malware. Open Windows Security to start a scan yourself? Microsoft Defender may quarantine detected threats.", "Windows Security", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Windows Security could not be opened. Find it in the Start menu. {ex.Message}", "Could not open Windows Security", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
public readonly record struct ScanInventoryProgress(int Files, int Folders, string CurrentFolder);
public sealed record SystemSnapshot(string MemoryHeadline, string MemoryDetail, string BatteryHeadline, string BatteryDetail, string StorageHeadline, string StorageDetail);
public sealed record OrganizationMove(string Source, string Destination, long Bytes, long ModifiedUtcTicks = 0);
public sealed record GuideChatMessage(string Text, string BubbleBrush, string TextBrush, HorizontalAlignment Alignment)
{
    public static GuideChatMessage Assistant(string text) => new(text, "#EEF6F4", "#264D56", HorizontalAlignment.Left);
    public static GuideChatMessage User(string text) => new(text, "#087D86", "White", HorizontalAlignment.Right);
}
public sealed record TemporaryFindResult(int Count, int Locations, int Errors);
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
public sealed record CleanupResult(List<FileRow> Moved, int Skipped, List<CleanupSkipItem> SkippedItems, bool LogWarning, string? StoppedOn, bool Cancelled);
public sealed record CleanupHistoryEntry(DateTimeOffset Time, string Path, long Bytes, string Status, string RawResult)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeDisplay => MainWindow.Format(Bytes);
    public string WhenDisplay => Time == DateTimeOffset.MinValue ? "Unknown time" : Time.ToString("yyyy-MM-dd HH:mm");
}
public sealed record CleanupSkipItem(string FullPath, long Bytes, string FriendlyReason)
{
    public string Name => System.IO.Path.GetFileName(FullPath);
    public string SizeDisplay => MainWindow.Format(Bytes);
}
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
    private bool _sizeCandidate;
    public string Name { get; set; } = ""; public string FullPath { get; set; } = ""; public string RelativePath { get; set; } = ""; public string Type { get; set; } = ""; public string ScanRoot { get; set; } = ""; public long Size { get; set; } public DateTime Modified { get; set; }
    public bool Duplicate { get => _duplicate; set { if (_duplicate == value) return; _duplicate = value; PropertyChanged?.Invoke(this, new(nameof(Duplicate))); PropertyChanged?.Invoke(this, new(nameof(DuplicateDisplay))); PropertyChanged?.Invoke(this, new(nameof(DuplicateSort))); } }
    public bool SizeCandidate { get => _sizeCandidate; set { if (_sizeCandidate == value) return; _sizeCandidate = value; PropertyChanged?.Invoke(this, new(nameof(SizeCandidate))); PropertyChanged?.Invoke(this, new(nameof(DuplicateDisplay))); } }
    private bool _duplicateExtra;
    private int _duplicateGroup;
    public bool DuplicateExtra { get => _duplicateExtra; set { if (_duplicateExtra == value) return; _duplicateExtra = value; PropertyChanged?.Invoke(this, new(nameof(DuplicateExtra))); PropertyChanged?.Invoke(this, new(nameof(DuplicateDisplay))); PropertyChanged?.Invoke(this, new(nameof(DuplicateSort))); } }
    public int DuplicateGroup { get => _duplicateGroup; set { if (_duplicateGroup == value) return; _duplicateGroup = value; PropertyChanged?.Invoke(this, new(nameof(DuplicateGroup))); PropertyChanged?.Invoke(this, new(nameof(DuplicateDisplay))); } }
    public int DuplicateSort => !Duplicate ? 0 : DuplicateExtra ? 2 : 1;
    public string DuplicateDisplay => !Duplicate ? SizeCandidate ? "Same-size candidate · not verified" : "—" : DuplicateExtra ? $"Exact extra · group {DuplicateGroup}" : $"Keep candidate · group {DuplicateGroup}";
    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "No extension" : Type.TrimStart('.').ToUpperInvariant();
    public int AgeDays => (DateTime.Today - Modified.Date).Days;
    public string AgeDisplay
    {
        get
        {
            var days = AgeDays;
            if (days < 0) return "Future";
            if (days == 0) return "Today";
            if (days < 14) return $"{days}d";
            if (days < 60) return $"{days / 7}w";
            if (days < 365) return $"{days / 30}mo";
            var years = days / 365; var months = days % 365 / 30;
            return months == 0 ? $"{years}y" : $"{years}y {months}mo";
        }
    }
    public string SizeDisplay => Size < 0 ? "—" : Format(Size); public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");
    public bool Marked { get => _marked; set { _marked = value; PropertyChanged?.Invoke(this, new(nameof(Marked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string Format(long bytes) { string[] u=["B","KB","MB","GB","TB"]; double n=bytes;int i=0;while(n>=1024&&i<4){n/=1024;i++;}return $"{n:0.#} {u[i]}"; }
}
