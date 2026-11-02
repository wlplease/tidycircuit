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
        StatusText.Text = "Scanning selected folder · nothing is changed"; ScanProgress.IsIndeterminate = true;
        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(path => { try { var info = new FileInfo(path); return new FileRow { Name = info.Name, FullPath = info.FullName, RelativePath = Path.GetRelativePath(root, info.FullName), Size = info.Length, Modified = info.LastWriteTime, Type = info.Extension }; } catch { return null; } }).Where(x => x is not null).Cast<FileRow>().ToList());
            foreach (var row in files.OrderByDescending(f => f.Size)) _rows.Add(row);
            FileCountText.Text = files.Count.ToString("N0"); FolderSizeText.Text = Format(files.Sum(f => f.Size));
            var dupes = await Task.Run(() => FindDuplicates(files));
            DuplicatesText.Text = dupes.ToString("N0");
            StatusText.Text = $"Scan complete · {files.Count:N0} files · same-content duplicates verified";
        }
        catch (Exception ex) { StatusText.Text = "Scan stopped · " + ex.Message; }
        finally { ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = FolderButton.IsEnabled = true; }
    }
    private static int FindDuplicates(List<FileRow> files)
    {
        var count = 0;
        foreach (var sizeGroup in files.Where(f => f.Size > 0 && f.Size <= 268_435_456).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            foreach (var hashGroup in sizeGroup.GroupBy(f => { try { using var stream = File.OpenRead(f.FullPath); return Convert.ToHexString(SHA256.HashData(stream)); } catch { return Guid.NewGuid().ToString(); } }).Where(g => g.Count() > 1))
            {
                foreach (var row in hashGroup) row.Duplicate = true;
                count += hashGroup.Count() - 1;
            }
        }
        return count;
    }
    private static string Format(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (FilesGrid is null) return; CollectionViewSource.GetDefaultView(_rows).Filter = item => item is FileRow row && (string.IsNullOrWhiteSpace(SearchBox.Text) || row.FullPath.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)); }
    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SortRows();
    private void SortRows() { if (FilesGrid is null || SortBox.SelectedIndex < 0) return; var view = CollectionViewSource.GetDefaultView(_rows); view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(SortBox.SelectedIndex switch { 1 => nameof(FileRow.Name), 2 => nameof(FileRow.Modified), _ => nameof(FileRow.Size) }, SortBox.SelectedIndex == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending)); }
    private void Export_Click(object sender, RoutedEventArgs e) { var rows = _rows.Where(r => r.Marked).ToList(); if (rows.Count == 0) { MessageBox.Show("Mark files in the REVIEW column first.", "Nothing marked", MessageBoxButton.OK, MessageBoxImage.Information); return; } var dialog = new SaveFileDialog { FileName = "tidycircuit-review.tsv", Filter = "TSV review list|*.tsv" }; if (dialog.ShowDialog() != true) return; File.WriteAllLines(dialog.FileName, new[] { "Name\tFull path\tBytes\tModified" }.Concat(rows.Select(r => $"{r.Name}\t{r.FullPath}\t{r.Size}\t{r.Modified:o}")), Encoding.UTF8); StatusText.Text = $"Review list exported · {rows.Count} files · no files changed"; }
    private void System_Click(object sender, RoutedEventArgs e) { var memory = GC.GetGCMemoryInfo(); MessageBox.Show("For live RAM and battery readings, open Windows Task Manager or Settings. This first release keeps system-level changes out of the scan.", "Windows system details", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void Safety_Click(object sender, RoutedEventArgs e) { var result = MessageBox.Show("TidyCircuit does not identify malware. Ask Microsoft Defender to scan a chosen file? Defender may quarantine a detected threat.", "Windows Security", MessageBoxButton.YesNo, MessageBoxImage.Warning); if (result == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); }
}
public sealed class FileRow : INotifyPropertyChanged
{
    private bool _marked;
    public string Name { get; set; } = ""; public string FullPath { get; set; } = ""; public string RelativePath { get; set; } = ""; public string Type { get; set; } = ""; public long Size { get; set; } public DateTime Modified { get; set; } public bool Duplicate { get; set; }
    public string SizeDisplay => Size < 0 ? "—" : Format(Size); public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");
    public bool Marked { get => _marked; set { _marked = value; PropertyChanged?.Invoke(this, new(nameof(Marked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string Format(long bytes) { string[] u=["B","KB","MB","GB","TB"]; double n=bytes;int i=0;while(n>=1024&&i<4){n/=1024;i++;}return $"{n:0.#} {u[i]}"; }
}
