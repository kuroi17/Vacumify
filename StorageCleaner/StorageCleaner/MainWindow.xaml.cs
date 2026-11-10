using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Vacumify;

public partial class MainWindow : Window
{
    private List<FileEntry> _forgotten = [];
    private List<FileEntry> _duplicates = [];
    private List<FileEntry> _large = [];
    private CancellationTokenSource? _scanCts;
    private ScanResult? _lastResult;

    public MainWindow()
    {
        InitializeComponent();

        string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(defaultPath) || !Directory.Exists(defaultPath))
        {
            defaultPath = "C:\\Users";
        }
        RootsBox.Text = defaultPath;
        UpdateSelectionSummary();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = RootsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0 || roots.Any(path => !Directory.Exists(path)))
        {
            MessageBox.Show("Please enter one or more valid, existing folder or drive paths (one per line).", "Check scan locations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scanCts = new CancellationTokenSource();
        SetScanning(true);
        ErrorSummary.Text = "";
        StatusText.Text = "Scanning selected locations…";

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                StatusText.Text = $"{p.Stage} · {p.Files:N0} files · {FileEntry.FormatBytes(p.Bytes)} · {p.CurrentPath}";
            });

            // Run scan completely off the UI thread to ensure 100% UI responsiveness
            var rootsCopy = roots.ToArray();
            var token = _scanCts.Token;
            _lastResult = await Task.Run(() => Scanner.ScanAsync(rootsCopy, progress, token), token);

            PopulateResults(_lastResult.Files);

            var failures = _lastResult.Errors.Count;
            ErrorSummary.Text = failures == 0 ? "" : $"{failures:N0} path(s) skipped due to permissions or errors.";
            StatusText.Text = $"Scan complete · {_lastResult.Files.Count:N0} files · {FileEntry.FormatBytes(_lastResult.BytesScanned)} scanned · {FileEntry.FormatBytes(_lastResult.ReclaimableDuplicateBytes)} reclaimable duplicates.";

            if (failures > 0)
            {
                var details = string.Join(Environment.NewLine, _lastResult.Errors.Take(30));
                if (failures > 30) details += Environment.NewLine + $"…and {failures - 30:N0} more.";
                MessageBox.Show(details, "Skipped files and folders", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled. Results were not applied.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan could not be completed.";
            MessageBox.Show(ex.Message, "Scan error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanning(false);
            UpdateSelectionSummary();
        }
    }

    private void PopulateResults(IReadOnlyList<FileEntry> files)
    {
        ApplyFilters(files);
        FilesCount.Text = files.Count.ToString("N0");
        TotalScannedSize.Text = FileEntry.FormatBytes(_lastResult?.BytesScanned ?? 0);
        ReclaimableSpaceText.Text = FileEntry.FormatBytes(_lastResult?.ReclaimableDuplicateBytes ?? 0);
    }

    private void ApplyFilters(IReadOnlyList<FileEntry> files)
    {
        int days = ReadSelectedTag(AgeFilter, 30);
        long sizeThreshold = ReadSelectedTag(SizeFilter, 100L * 1024 * 1024);
        var extensions = ExtensionFilter.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x : "." + x)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool allTypes = ExtensionFilter.Text.Trim() is "" or "*";
        bool TypeMatches(FileEntry file) => allTypes || extensions.Contains(Path.GetExtension(file.Path));

        DateTime cutoff = DateTime.UtcNow.AddDays(-days);

        _forgotten = files.Where(f => TypeMatches(f) && f.LastAccessUtc <= cutoff)
                          .OrderBy(f => f.LastAccessUtc)
                          .ToList();

        _duplicates = files.Where(f => TypeMatches(f) && f.DuplicateGroupCount > 1)
                           .OrderBy(f => f.DuplicateGroupId)
                           .ThenByDescending(f => f.LastWriteUtc)
                           .ToList();

        _large = files.Where(f => TypeMatches(f) && f.Size >= sizeThreshold)
                      .OrderByDescending(f => f.Size)
                      .ToList();

        // Subscribe to PropertyChanged for instant footer reactivity
        SubscribeEvents(_forgotten);
        SubscribeEvents(_duplicates);
        SubscribeEvents(_large);

        // Assign directly to trigger single virtualized UI layout pass
        ForgottenGrid.ItemsSource = null;
        ForgottenGrid.ItemsSource = _forgotten;

        DuplicatesGrid.ItemsSource = null;
        DuplicatesGrid.ItemsSource = _duplicates;

        LargeGrid.ItemsSource = null;
        LargeGrid.ItemsSource = _large;

        ForgottenCount.Text = _forgotten.Count.ToString("N0");
        DuplicateCount.Text = _duplicates.Count.ToString("N0");
        LargeCount.Text = _large.Count.ToString("N0");

        UpdateSelectionSummary();
    }

    private void SubscribeEvents(List<FileEntry> items)
    {
        foreach (var item in items)
        {
            item.PropertyChanged -= FileItem_PropertyChanged;
            item.PropertyChanged += FileItem_PropertyChanged;
        }
    }

    private void FileItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileEntry.Selected))
        {
            UpdateSelectionSummary();
        }
    }

    private static T ReadSelectedTag<T>(ComboBox combo, T fallback) where T : IParsable<T>
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && T.TryParse(tag, null, out var value))
        {
            return value;
        }
        return fallback;
    }

    private void Filters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _lastResult is not null) ApplyFilters(_lastResult.Files);
    }

    private void ExtensionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && _lastResult is not null) ApplyFilters(_lastResult.Files);
    }

    private void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        CancelButton.IsEnabled = scanning;
        RootsBox.IsEnabled = !scanning;
        ScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to scan", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        string current = RootsBox.Text.TrimEnd();
        RootsBox.Text = string.IsNullOrWhiteSpace(current) ? dialog.FolderName : current + Environment.NewLine + dialog.FolderName;
    }

    private void ResultsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, ResultsTabs)) return;
        UpdateSelectionSummary();
    }

    private DataGrid? CurrentGrid => ResultsTabs.SelectedIndex switch
    {
        1 => ForgottenGrid,
        2 => DuplicatesGrid,
        3 => LargeGrid,
        _ => null
    };

    private void UpdateSelectionSummary()
    {
        var grid = CurrentGrid;
        if (grid is null)
        {
            SelectedSummaryText.Text = "Select a results tab to review files";
            DeleteButton.IsEnabled = false;
            return;
        }

        var items = grid.ItemsSource as IReadOnlyList<FileEntry> ?? [];
        int count = items.Count(f => f.Selected);
        long size = items.Where(f => f.Selected).Sum(f => f.Size);

        SelectedSummaryText.Text = count > 0 
            ? $"{count:N0} file(s) selected ({FileEntry.FormatBytes(size)})" 
            : "0 files selected";

        DeleteButton.IsEnabled = count > 0;
    }

    // --- Smart Selection Handlers for Duplicates ---
    private void KeepNewest_Click(object sender, RoutedEventArgs e)
    {
        var groups = _duplicates.GroupBy(f => f.DuplicateGroupId);
        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(f => f.LastWriteUtc).ToList();
            if (ordered.Count > 0) ordered[0].Selected = false; // Keep newest
            for (int i = 1; i < ordered.Count; i++) ordered[i].Selected = true;
        }
        UpdateSelectionSummary();
    }

    private void KeepOldest_Click(object sender, RoutedEventArgs e)
    {
        var groups = _duplicates.GroupBy(f => f.DuplicateGroupId);
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(f => f.LastWriteUtc).ToList();
            if (ordered.Count > 0) ordered[0].Selected = false; // Keep oldest
            for (int i = 1; i < ordered.Count; i++) ordered[i].Selected = true;
        }
        UpdateSelectionSummary();
    }

    private void DuplicatesSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _duplicates) f.Selected = true;
        UpdateSelectionSummary();
    }

    private void DuplicatesDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _duplicates) f.Selected = false;
        UpdateSelectionSummary();
    }

    // --- Forgotten Files Selection Handlers ---
    private void ForgottenSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _forgotten) f.Selected = true;
        UpdateSelectionSummary();
    }

    private void ForgottenDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _forgotten) f.Selected = false;
        UpdateSelectionSummary();
    }

    // --- Large Files Selection Handlers ---
    private void LargeSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _large) f.Selected = true;
        UpdateSelectionSummary();
    }

    private void LargeDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _large) f.Selected = false;
        UpdateSelectionSummary();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var grid = CurrentGrid;
        if (grid?.SelectedItem is not FileEntry item)
        {
            MessageBox.Show("Please select a file row first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (File.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(Path.GetDirectoryName(item.Path)))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(item.Path)}\"") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("This file or its folder no longer exists.", "Location unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var grid = CurrentGrid;
        if (grid is null)
        {
            MessageBox.Show("Open one of the results tabs before choosing files.", "No file list", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var items = grid.ItemsSource as IReadOnlyList<FileEntry> ?? [];
        var selected = items.Where(f => f.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select files using the checkboxes or smart selection buttons before deleting.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long total = selected.Sum(f => f.Size);
        string preview = string.Join(Environment.NewLine, selected.Take(6).Select(f => f.Path));
        if (selected.Count > 6) preview += Environment.NewLine + $"…and {selected.Count - 6:N0} more.";

        var answer = MessageBox.Show(
            $"Move {selected.Count:N0} selected file(s) ({FileEntry.FormatBytes(total)}) to the Windows Recycle Bin?\n\n{preview}\n\nFiles are sent to the Recycle Bin and can be restored if needed.",
            "Confirm move to Recycle Bin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        int moved = 0, failed = 0;
        long movedBytes = 0;
        foreach (var item in selected)
        {
            try
            {
                if (!File.Exists(item.Path) || Scanner.IsProtectedPath(item.Path))
                {
                    failed++;
                    continue;
                }

                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved++;
                movedBytes += item.Size;
            }
            catch
            {
                failed++;
            }
        }

        if (_lastResult is not null)
        {
            var remaining = _lastResult.Files.Where(f => File.Exists(f.Path)).ToList();
            
            // Recompute duplicate groups and reclaimable bytes on remaining
            int groupId = 1;
            long reclaimableBytes = 0;
            var duplicateGroups = remaining.Where(f => f.Hash is not null)
                                           .GroupBy(f => f.Hash!, StringComparer.Ordinal)
                                           .Where(g => g.Count() > 1)
                                           .ToList();

            foreach (var f in remaining)
            {
                f.DuplicateGroupId = 0;
                f.DuplicateGroupCount = 0;
            }

            foreach (var group in duplicateGroups)
            {
                int groupCount = group.Count();
                long groupFileSize = group.First().Size;
                reclaimableBytes += (groupCount - 1) * groupFileSize;
                foreach (var copy in group)
                {
                    copy.DuplicateGroupId = groupId;
                    copy.DuplicateGroupCount = groupCount;
                }
                groupId++;
            }

            _lastResult = _lastResult with { 
                Files = remaining, 
                BytesScanned = remaining.Sum(f => f.Size),
                ReclaimableDuplicateBytes = reclaimableBytes 
            };
            PopulateResults(remaining);
        }

        StatusText.Text = $"Moved {moved:N0} file(s) ({FileEntry.FormatBytes(movedBytes)}) to the Recycle Bin; {failed:N0} could not be moved.";
        if (failed > 0)
        {
            MessageBox.Show($"Moved: {moved:N0}\nSkipped or failed: {failed:N0}\nFiles in use, protected paths, or changed files may fail.", "Cleanup summary", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateSelectionSummary();
    }
}
