using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace TidyCircuit;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileRow> _rows = new();
    private string _selectedRoot = "";
    public MainWindow()
    {
        InitializeComponent(); FilesGrid.ItemsSource = _rows;
        FilesGrid.Sorting += (_, e) => { e.Handled = true; SortRows(); };
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose one folder for a read-only scan", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _selectedRoot = dialog.FolderName;
        await ScanFolderAsync(_selectedRoot);
    }
    private async Task ScanFolderAsync(string root)
    {
        ScanButton.IsEnabled = FolderButton.IsEnabled = false;
        _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "Scanning…";
        ProgressText.Text = "Reading file list";
        StatusText.Text = "Scanning selected folder · nothing is changed"; ScanProgress.IsIndeterminate = true;
        try
        {
            var (files, inventoryErrors) = await Task.Run(() => ReadFiles(root));
            foreach (var row in files.OrderByDescending(f => f.Size)) _rows.Add(row);
            FileCountText.Text = files.Count.ToString("N0"); FolderSizeText.Text = Format(files.Sum(f => f.Size));
            ProgressText.Text = "Comparing matching files";
            var (dupes, hashErrors) = await Task.Run(() => FindDuplicates(files));
            DuplicatesText.Text = dupes.ToString("N0");
            var errors = inventoryErrors + hashErrors;
            StatusText.Text = errors == 0
                ? $"Scan complete · {files.Count:N0} files · matching content checked; files over 256 MB skipped"
                : $"Scan complete with {errors:N0} read issue(s) · some files could not be read";
        }
        catch (Exception ex)
        {
            _rows.Clear(); FileCountText.Text = FolderSizeText.Text = DuplicatesText.Text = "—";
            StatusText.Text = "Scan failed · " + ex.Message;
        }
        finally { ProgressText.Text = ""; ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = true; }
    }
    private static (List<FileRow> Files, int Errors) ReadFiles(string root)
    {
        var files = new List<FileRow>(); var errors = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try { foreach (var child in Directory.EnumerateDirectories(dir, "*", options)) pending.Push(child); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*", options))
                {
                    try { var info = new FileInfo(path); files.Add(new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = Path.GetRelativePath(root, info.FullName), Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors++; }
        }
        return (files, errors);
    }
    private static (int Duplicates, int Errors) FindDuplicates(List<FileRow> files)
    {
        var count = 0; var errors = 0;
        foreach (var sizeGroup in files.Where(f => f.Size > 0 && f.Size <= 268_435_456).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<FileRow>>();
            foreach (var row in sizeGroup)
            {
                try { using var stream = File.OpenRead(row.FullPath); var hash = Convert.ToHexString(SHA256.HashData(stream)); if (!hashes.TryGetValue(hash, out var matches)) hashes[hash] = matches = new(); matches.Add(row); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { errors++; }
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
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (FilesGrid is null) return; CollectionViewSource.GetDefaultView(_rows).Filter = item => item is FileRow row && (string.IsNullOrWhiteSpace(SearchBox.Text) || row.FullPath.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)); }
    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SortRows();
    private void SortRows() { if (FilesGrid is null || SortBox.SelectedIndex < 0) return; var view = CollectionViewSource.GetDefaultView(_rows); view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(SortBox.SelectedIndex switch { 1 => nameof(FileRow.Name), 2 => nameof(FileRow.Modified), _ => nameof(FileRow.Size) }, SortBox.SelectedIndex == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending)); }
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
