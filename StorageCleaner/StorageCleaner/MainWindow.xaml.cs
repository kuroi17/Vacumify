using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _filterTimer;

    public MainWindow()
    {
        InitializeComponent();

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            if (IsLoaded && _lastResult is not null) ApplyFilters(_lastResult.Files);
        };

        string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(defaultPath) || !Directory.Exists(defaultPath))
        {
            defaultPath = "C:\\Users";
        }
        RootsBox.Text = defaultPath;
        UpdateTabContext();
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

        var options = new ScanOptions(
            IncludeHiddenCaches: IncludeCachesCheck.IsChecked == true,
            MinDuplicateBytes: ReadSelectedTag(MinDupSizeCombo, 100L * 1024),
            FastLargeFileSampling: FastSampleCheck.IsChecked == true
        );

        _scanCts = new CancellationTokenSource();
        SetScanning(true);
        ScanBadge.Visibility = Visibility.Collapsed;
        ErrorSummary.Text = "";
        StatusText.Text = "Scanning selected locations…";
        var sw = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                StatusText.Text = $"{p.Stage} · {p.Files:N0} files · {FileEntry.FormatBytes(p.Bytes)} · {p.CurrentPath}";
            });

            var rootsCopy = roots.ToArray();
            var token = _scanCts.Token;
            _lastResult = await Task.Run(() => Scanner.ScanAsync(rootsCopy, options, progress, token), token);
            sw.Stop();

            SubscribeEvents(_lastResult.Files);
            PopulateResults(_lastResult.Files);

            int failures = _lastResult.Errors.Count;
            ScanBadgeText.Text = failures == 0
                ? $"Done in {sw.Elapsed.TotalSeconds:0.0}s"
                : $"Done in {sw.Elapsed.TotalSeconds:0.0}s · {failures:N0} skipped";
            ScanBadge.Visibility = Visibility.Visible;
            ErrorSummary.Text = failures == 0 ? "" : $"{failures:N0} skipped";

            if (failures > 0)
            {
                var details = string.Join(Environment.NewLine, _lastResult.Errors.Take(30));
                if (failures > 30) details += Environment.NewLine + $"…and {failures - 30:N0} more.";
                MessageBox.Show(details, "Skipped files and folders", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            ScanBadgeText.Text = "Cancelled";
            ScanBadge.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
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

        ForgottenGrid.ItemsSource = null;
        ForgottenGrid.ItemsSource = _forgotten;

        DuplicatesGrid.ItemsSource = null;
        DuplicatesGrid.ItemsSource = _duplicates;

        LargeGrid.ItemsSource = null;
        LargeGrid.ItemsSource = _large;

        ForgottenCount.Text = _forgotten.Count.ToString("N0");
        DuplicateCount.Text = _duplicates.Count.ToString("N0");
        LargeCount.Text = _large.Count.ToString("N0");

        ForgottenTab.Header = $"Forgotten ({_forgotten.Count:N0})";
        DuplicatesTab.Header = $"Duplicates ({_duplicates.Count:N0})";
        LargeTab.Header = $"Large ({_large.Count:N0})";

        UpdateSelectionSummary();
    }

    private void SubscribeEvents(IReadOnlyList<FileEntry> items)
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
        if (!IsLoaded || _lastResult is null) return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void SetScanning(bool scanning)
    {
        ScanButton.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        RootsBox.IsEnabled = !scanning;
        IncludeCachesCheck.IsEnabled = !scanning;
        MinDupSizeCombo.IsEnabled = !scanning;
        FastSampleCheck.IsEnabled = !scanning;
        ProgressPanel.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
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
        UpdateTabContext();
    }

    private DataGrid? CurrentGrid => ResultsTabs.SelectedIndex switch
    {
        1 => ForgottenGrid,
        2 => DuplicatesGrid,
        3 => LargeGrid,
        _ => null
    };

    private void UpdateTabContext()
    {
        bool onFileTab = CurrentGrid is not null;
        ActionBar.Visibility = onFileTab ? Visibility.Visible : Visibility.Collapsed;
        ExtFilterPanel.Visibility = onFileTab ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var grid = CurrentGrid;
        if (grid is null)
        {
            DeleteButton.IsEnabled = false;
            return;
        }

        var items = grid.ItemsSource as IReadOnlyList<FileEntry> ?? [];
        int count = items.Count(f => f.Selected);
        long size = items.Where(f => f.Selected).Sum(f => f.Size);

        SelectedSummaryText.Text = count > 0
            ? $"{count:N0} selected · {FileEntry.FormatBytes(size)}"
            : "0 selected";

        DeleteButton.IsEnabled = count > 0;
    }

    private void JumpForgotten_Click(object sender, RoutedEventArgs e) => ResultsTabs.SelectedIndex = 1;
    private void JumpDuplicates_Click(object sender, RoutedEventArgs e) => ResultsTabs.SelectedIndex = 2;
    private void JumpLarge_Click(object sender, RoutedEventArgs e) => ResultsTabs.SelectedIndex = 3;

    // --- Selection Helpers ---
    private void SelectDuplicatesByAge(bool keepNewest)
    {
        foreach (var group in _duplicates.GroupBy(f => f.DuplicateGroupId))
        {
            var ordered = (keepNewest
                ? group.OrderByDescending(f => f.LastWriteUtc)
                : group.OrderBy(f => f.LastWriteUtc)).ToList();

            if (ordered.Count > 0) ordered[0].Selected = false;
            for (int i = 1; i < ordered.Count; i++) ordered[i].Selected = true;
        }
        UpdateSelectionSummary();
    }

    private void SetAllSelected(List<FileEntry> list, bool selected)
    {
        foreach (var f in list) f.Selected = selected;
        UpdateSelectionSummary();
    }

    private void KeepNewest_Click(object sender, RoutedEventArgs e) => SelectDuplicatesByAge(keepNewest: true);
    private void KeepOldest_Click(object sender, RoutedEventArgs e) => SelectDuplicatesByAge(keepNewest: false);
    private void DuplicatesSelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_duplicates, true);
    private void DuplicatesDeselectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_duplicates, false);
    private void ForgottenSelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_forgotten, true);
    private void ForgottenDeselectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_forgotten, false);
    private void LargeSelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_large, true);
    private void LargeDeselectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(_large, false);

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var grid = CurrentGrid;
        if (grid?.SelectedItem is not FileEntry item)
        {
            MessageBox.Show("Select a file row first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (grid is null) return;

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var items = grid.ItemsSource as IReadOnlyList<FileEntry> ?? [];
        var selected = items.Where(f => f.Selected).ToList();
        if (selected.Count == 0) return;

        long total = selected.Sum(f => f.Size);
        string preview = string.Join(Environment.NewLine, selected.Take(6).Select(f => f.Path));
        if (selected.Count > 6) preview += Environment.NewLine + $"…and {selected.Count - 6:N0} more.";

        var answer = MessageBox.Show(
            $"Move {selected.Count:N0} file(s) ({FileEntry.FormatBytes(total)}) to the Windows Recycle Bin?\n\n{preview}",
            "Move to Recycle Bin",
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
            long reclaimableBytes = Scanner.AssignDuplicateGroups(remaining);

            _lastResult = _lastResult with
            {
                Files = remaining,
                BytesScanned = remaining.Sum(f => f.Size),
                ReclaimableDuplicateBytes = reclaimableBytes
            };
            PopulateResults(remaining);
        }

        ScanBadgeText.Text = $"Recycled {moved:N0} ({FileEntry.FormatBytes(movedBytes)})";
        ScanBadge.Visibility = Visibility.Visible;

        if (failed > 0)
        {
            MessageBox.Show($"Moved: {moved:N0}\nSkipped or failed: {failed:N0}\nFiles in use or protected paths may fail.", "Cleanup summary", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateSelectionSummary();
    }
}
