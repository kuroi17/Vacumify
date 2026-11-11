using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private FileEntry? _inspectedFile;
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
        DrawDonutChart(0, 0, 0, 0);
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
        ResultsTabs.SelectedIndex = 0;
        SetScanning(true);
        CloseInspector();
        ErrorSummary.Text = "";
        ScanTimingText.Visibility = Visibility.Collapsed;
        ScanStageText.Text = "Starting scan…";
        ScanProgressCountText.Text = "0 files · 0 B";
        ScanCurrentPathText.Text = "";
        var sw = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanStageText.Text = $"{p.Stage}…";
                ScanProgressCountText.Text = $"{p.Files:N0} files · {FileEntry.FormatBytes(p.Bytes)}";
                ScanCurrentPathText.Text = p.CurrentPath;
            });

            var rootsCopy = roots.ToArray();
            var token = _scanCts.Token;
            _lastResult = await Task.Run(() => Scanner.ScanAsync(rootsCopy, options, progress, token), token);
            sw.Stop();

            SubscribeEvents(_lastResult.Files);
            PopulateResults(_lastResult.Files);

            int failures = _lastResult.Errors.Count;
            ScanTimingText.Text = failures == 0
                ? $"Scanned in {sw.Elapsed.TotalSeconds:0.0}s"
                : $"Scanned in {sw.Elapsed.TotalSeconds:0.0}s · {failures:N0} skipped";
            ScanTimingText.Visibility = Visibility.Visible;
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
            ScanTimingText.Text = "Scan cancelled";
            ScanTimingText.Visibility = Visibility.Visible;
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
        LargeCount.Text = _large.Count.ToString("N0");

        ForgottenTab.Header = $"Forgotten ({_forgotten.Count:N0})";
        DuplicatesTab.Header = $"Duplicates ({_duplicates.Count:N0})";
        LargeTab.Header = $"Large ({_large.Count:N0})";

        UpdateOverviewVisuals(files, _lastResult?.BytesScanned ?? 0, _lastResult?.ReclaimableDuplicateBytes ?? 0);
        UpdateSelectionSummary();
    }

    private void UpdateOverviewVisuals(IReadOnlyList<FileEntry> files, long totalBytes, long reclaimableDupBytes)
    {
        if (totalBytes <= 0 || files.Count == 0)
        {
            DrawDonutChart(0, 0, 0, 0);
            LegendDupText.Text = "Duplicates";
            LegendLargeText.Text = "Large";
            LegendForgottenText.Text = "Forgotten";
            return;
        }

        int days = ReadSelectedTag(AgeFilter, 30);
        long sizeThreshold = ReadSelectedTag(SizeFilter, 100L * 1024 * 1024);
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);

        long largeOtherBytes = 0;
        long forgottenOtherBytes = 0;
        foreach (var f in files)
        {
            if (f.DuplicateGroupCount > 1) continue;
            if (f.Size >= sizeThreshold) largeOtherBytes += f.Size;
            else if (f.LastAccessUtc <= cutoff) forgottenOtherBytes += f.Size;
        }

        long dupBytes = Math.Min(reclaimableDupBytes, totalBytes);
        long otherBytes = Math.Max(0, totalBytes - dupBytes - largeOtherBytes - forgottenOtherBytes);

        LegendDupText.Text = $"Duplicates ({FileEntry.FormatBytes(dupBytes)})";
        LegendLargeText.Text = $"Large ({FileEntry.FormatBytes(largeOtherBytes)})";
        LegendForgottenText.Text = $"Forgotten ({FileEntry.FormatBytes(forgottenOtherBytes)})";

        DrawDonutChart(dupBytes, largeOtherBytes, forgottenOtherBytes, otherBytes);
    }

    private void DrawDonutChart(long dupBytes, long largeBytes, long forgottenBytes, long otherBytes)
    {
        DonutCanvas.Children.Clear();
        const double cx = 100, cy = 100, r = 78, stroke = 22;
        long total = dupBytes + largeBytes + forgottenBytes + otherBytes;

        if (total <= 0)
        {
            var track = new System.Windows.Shapes.Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(0xDC, 0xEC, 0xFB)),
                StrokeThickness = stroke
            };
            Canvas.SetLeft(track, cx - r);
            Canvas.SetTop(track, cy - r);
            DonutCanvas.Children.Add(track);
            DonutCenterText.Text = "—";
            return;
        }

        int pct = (int)Math.Round(dupBytes * 100.0 / total);
        DonutCenterText.Text = $"{pct}%";

        (long Bytes, Color Color)[] slices =
        [
            (dupBytes, Color.FromRgb(0x02, 0x84, 0xC7)),
            (largeBytes, Color.FromRgb(0x38, 0xBD, 0xF8)),
            (forgottenBytes, Color.FromRgb(0xBA, 0xE6, 0xFD)),
            (otherBytes, Color.FromRgb(0xE2, 0xEE, 0xFC))
        ];

        double startAngle = 0;
        foreach (var (bytes, color) in slices)
        {
            if (bytes <= 0) continue;
            double sweep = bytes * 360.0 / total;
            var brush = new SolidColorBrush(color);

            if (sweep >= 359.8)
            {
                var full = new System.Windows.Shapes.Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Stroke = brush,
                    StrokeThickness = stroke
                };
                Canvas.SetLeft(full, cx - r);
                Canvas.SetTop(full, cy - r);
                DonutCanvas.Children.Add(full);
                break;
            }

            double a1 = (startAngle - 90.0) * Math.PI / 180.0;
            double a2 = (startAngle + sweep - 90.0) * Math.PI / 180.0;
            var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
            var p2 = new Point(cx + r * Math.Cos(a2), cy + r * Math.Sin(a2));

            var fig = new PathFigure { StartPoint = p1, IsClosed = false };
            fig.Segments.Add(new ArcSegment(p2, new Size(r, r), 0, sweep > 180.0, SweepDirection.Clockwise, true));

            var geom = new PathGeometry();
            geom.Figures.Add(fig);

            DonutCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geom,
                Stroke = brush,
                StrokeThickness = stroke,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            });

            startAngle += sweep;
        }
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
        AddFolderButton.IsEnabled = !scanning;
        IncludeCachesCheck.IsEnabled = !scanning;
        MinDupSizeCombo.IsEnabled = !scanning;
        FastSampleCheck.IsEnabled = !scanning;
        ReviewDuplicatesBtn.IsEnabled = !scanning;

        var resultVis = scanning ? Visibility.Collapsed : Visibility.Visible;
        var spinnerVis = scanning ? Visibility.Visible : Visibility.Collapsed;

        HeroResultPanel.Visibility = resultVis;
        HeroLoadingPanel.Visibility = spinnerVis;

        FilesCount.Visibility = resultVis;
        FilesSpinner.Visibility = spinnerVis;

        TotalScannedSize.Visibility = resultVis;
        StorageSpinner.Visibility = spinnerVis;

        ForgottenCount.Visibility = resultVis;
        ForgottenSpinner.Visibility = spinnerVis;

        LargeCount.Visibility = resultVis;
        LargeSpinner.Visibility = spinnerVis;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void QuickTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null || sender is not Button btn || btn.Tag is not string key) return;

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = key switch
        {
            "UserHome" => userHome,
            "Downloads" => Path.Combine(userHome, "Downloads"),
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => userHome
        };

        if (Directory.Exists(path))
        {
            RootsBox.Text = path;
        }
    }

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
        ExtFilterPanel.Visibility = onFileTab ? Visibility.Visible : Visibility.Collapsed;

        if (!onFileTab)
        {
            CloseInspector();
        }
        else if (CurrentGrid?.SelectedItem is FileEntry selected)
        {
            ShowInspector(selected);
        }
        else
        {
            CloseInspector();
        }

        UpdateSelectionSummary();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not DataGrid grid || !ReferenceEquals(grid, CurrentGrid)) return;

        if (grid.SelectedItem is FileEntry item)
        {
            ShowInspector(item);
        }
        else
        {
            CloseInspector();
        }
    }

    private void ShowInspector(FileEntry item)
    {
        _inspectedFile = item;
        InspectorFileName.Text = item.Name;
        InspectorFileSize.Text = item.SizeText;
        InspectorFilePath.Text = item.Path;
        InspectorAccessed.Text = item.AccessedText;
        InspectorModified.Text = item.ModifiedText;

        // Clean borderless vector SVG file icon
        InspectorFileIcon.Source = item.IconImage;

        // Dynamic category pill
        InspectorExtText.Text = item.ExtensionText;
        if (ColorConverter.ConvertFromString(item.TypeBadgeBg) is Color bgCol)
            InspectorExtBadge.Background = new SolidColorBrush(bgCol);
        if (ColorConverter.ConvertFromString(item.TypeBadgeBorder) is Color borderCol)
            InspectorExtBadge.BorderBrush = new SolidColorBrush(borderCol);
        if (ColorConverter.ConvertFromString(item.TypeBadgeFg) is Color fgCol)
            InspectorExtText.Foreground = new SolidColorBrush(fgCol);

        if (item.DuplicateGroupCount > 1 && _lastResult is not null)
        {
            var siblings = _lastResult.Files
                .Where(f => f.DuplicateGroupId == item.DuplicateGroupId && !string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            long groupReclaimable = (item.DuplicateGroupCount - 1) * item.Size;
            InspectorDupSummary.Text = $"Group #{item.DuplicateGroupId} · {item.DuplicateGroupCount} copies · {FileEntry.FormatBytes(groupReclaimable)} reclaimable";
            InspectorSiblingsList.ItemsSource = siblings;
            InspectorDupSection.Visibility = Visibility.Visible;
        }
        else
        {
            InspectorSiblingsList.ItemsSource = null;
            InspectorDupSection.Visibility = Visibility.Collapsed;
        }

        InspectorPanel.Visibility = Visibility.Visible;
    }

    private void CloseInspector()
    {
        _inspectedFile = null;
        InspectorPanel.Visibility = Visibility.Collapsed;
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGrid is not null) CurrentGrid.SelectedItem = null;
        CloseInspector();
    }

    private void InspectorDeleteSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectedFile is null) return;
        MoveFilesToRecycleBin([_inspectedFile]);
    }

    private void UpdateSelectionSummary()
    {
        var grid = CurrentGrid;
        if (grid is null)
        {
            ActionBar.Visibility = Visibility.Collapsed;
            DeleteButton.IsEnabled = false;
            return;
        }

        var items = grid.ItemsSource as IReadOnlyList<FileEntry> ?? [];
        int count = items.Count(f => f.Selected);
        long size = items.Where(f => f.Selected).Sum(f => f.Size);

        // Action bar ONLY displays when files are selected (eliminates bottom-bar redundancy when 0 selected)
        ActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (count > 0)
        {
            SelectedSummaryText.Text = $"{count:N0} selected · {FileEntry.FormatBytes(size)}";
            DeleteButton.Content = $"Move Selected ({count:N0}) to Recycle Bin";
            DeleteButton.IsEnabled = true;
        }

        UpdateToggleButtonsState();
    }

    private void UpdateToggleButtonsState()
    {
        if (ForgottenToggleSelectBtn is not null && _forgotten.Count > 0)
            ForgottenToggleSelectBtn.Content = _forgotten.All(f => f.Selected) ? "Deselect all" : "Select all";
        if (DuplicatesToggleSelectBtn is not null && _duplicates.Count > 0)
            DuplicatesToggleSelectBtn.Content = _duplicates.All(f => f.Selected) ? "Deselect all" : "Select all";
        if (LargeToggleSelectBtn is not null && _large.Count > 0)
            LargeToggleSelectBtn.Content = _large.All(f => f.Selected) ? "Deselect all" : "Select all";
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

    private void ToggleSelectAll(List<FileEntry> list, Button? btn)
    {
        if (list.Count == 0) return;
        bool allSelected = list.All(f => f.Selected);
        bool newState = !allSelected;
        foreach (var f in list) f.Selected = newState;
        if (btn is not null) btn.Content = newState ? "Deselect all" : "Select all";
        UpdateSelectionSummary();
    }

    private void KeepNewest_Click(object sender, RoutedEventArgs e) => SelectDuplicatesByAge(keepNewest: true);
    private void KeepOldest_Click(object sender, RoutedEventArgs e) => SelectDuplicatesByAge(keepNewest: false);
    private void DuplicatesToggleSelect_Click(object sender, RoutedEventArgs e) => ToggleSelectAll(_duplicates, DuplicatesToggleSelectBtn);
    private void ForgottenToggleSelect_Click(object sender, RoutedEventArgs e) => ToggleSelectAll(_forgotten, ForgottenToggleSelectBtn);
    private void LargeToggleSelect_Click(object sender, RoutedEventArgs e) => ToggleSelectAll(_large, LargeToggleSelectBtn);

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var item = _inspectedFile ?? CurrentGrid?.SelectedItem as FileEntry;
        if (item is null)
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
        if (selected.Count > 0) MoveFilesToRecycleBin(selected);
    }

    private void MoveFilesToRecycleBin(IReadOnlyList<FileEntry> targets)
    {
        long total = targets.Sum(f => f.Size);
        string preview = string.Join(Environment.NewLine, targets.Take(6).Select(f => f.Path));
        if (targets.Count > 6) preview += Environment.NewLine + $"…and {targets.Count - 6:N0} more.";

        var answer = MessageBox.Show(
            $"Move {targets.Count:N0} file(s) ({FileEntry.FormatBytes(total)}) to the Windows Recycle Bin?\n\n{preview}",
            "Move to Recycle Bin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        int moved = 0, failed = 0;
        long movedBytes = 0;
        foreach (var item in targets)
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

        if (_inspectedFile is not null && !File.Exists(_inspectedFile.Path))
        {
            CloseInspector();
        }

        ScanTimingText.Text = $"Recycled {moved:N0} ({FileEntry.FormatBytes(movedBytes)})";
        ScanTimingText.Visibility = Visibility.Visible;

        if (failed > 0)
        {
            MessageBox.Show($"Moved: {moved:N0}\nSkipped or failed: {failed:N0}\nFiles in use or protected paths may fail.", "Cleanup summary", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateSelectionSummary();
    }
}
