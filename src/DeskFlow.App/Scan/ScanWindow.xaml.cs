using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Scan;
using DeskFlow.Infrastructure.Services;

namespace DeskFlow.App.Scan;

/// <summary>扫描列表的一行（带勾选状态）。</summary>
public sealed class ScanRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public ScanRow(ScanItem item)
    {
        Path = item.Path;
        Name = System.IO.Path.GetFileName(item.Path);
        SizeText = item.SizeText;
        ModifiedText = item.Modified.ToString("MM-dd HH:mm");
        Directory = item.Directory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    public string Directory { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>按盘符扫盘：选类型 / 时间范围，扫出后可以直接归档。</summary>
public partial class ScanWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private readonly ObservableCollection<ScanRow> _rows = [];
    private CancellationTokenSource? _cts;

    public ScanWindow(PanelViewModel viewModel)
    {
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        _viewModel = viewModel;

        ResultList.ItemsSource = _rows;
        BuildDrives();
        SummaryText.Text = "先选范围和类型，再点「开始扫描」";
    }

    private void BuildDrives()
    {
        DrivePanel.Children.Clear();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            var label = $"{drive.Name.TrimEnd('\\')}  {drive.DriveFormat}  " +
                        $"{drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.#} GB 可用";
            var box = new System.Windows.Controls.CheckBox
            {
                Content = label,
                Tag = drive.RootDirectory.FullName,
                IsChecked = drive.Name.StartsWith("D", StringComparison.OrdinalIgnoreCase) || DrivePanel.Children.Count == 0,
                Margin = new Thickness(0, 4, 20, 4),
            };

            DrivePanel.Children.Add(box);
        }
    }

    private IReadOnlyList<string> SelectedRoots() =>
        DrivePanel.Children.OfType<System.Windows.Controls.CheckBox>()
            .Where(static c => c.IsChecked == true)
            .Select(static c => (string)c.Tag)
            .ToList();

    private static IReadOnlyList<string> ParseExtensions(string raw) =>
        raw.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private DateTime? SelectedAfter() => RangeBox.SelectedIndex switch
    {
        1 => DateTime.Now.AddDays(-1),
        2 => DateTime.Now.AddDays(-7),
        3 => DateTime.Now.AddDays(-30),
        4 => DateTime.Now.AddDays(-90),
        5 => DateTime.Now.AddYears(-1),
        _ => null,
    };

    private long SelectedMinimumBytes() => SizeBox.SelectedIndex switch
    {
        1 => 100 * 1024,
        2 => 1024 * 1024,
        3 => 10 * 1024 * 1024,
        _ => 1024,
    };

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = SelectedRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show("先勾一个盘", "扫描", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _rows.Clear();
        ScanButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        var options = new ScanOptions
        {
            Roots = roots,
            Extensions = ParseExtensions(ExtensionBox.Text),
            ModifiedAfter = SelectedAfter(),
            MinimumBytes = SelectedMinimumBytes(),
            ExcludedPaths = [_viewModel.ArchiveRoot, AppPaths.RootDirectory],
        };

        var progress = new Progress<ScanProgress>(p =>
            StatusText.Text = $"扫描中… 已进 {p.ScannedDirectories} 个目录，匹配 {p.Matched} 个文件");

        try
        {
            var items = await FileScanner.ScanAsync(options, progress, _cts.Token);
            foreach (var item in items)
            {
                _rows.Add(new ScanRow(item));
            }

            var totalBytes = items.Sum(static i => i.SizeBytes);
            SummaryText.Text = $"扫到 {items.Count} 个文件，合计 {new ScanItem("x", totalBytes, DateTime.Now).SizeText}";
            StatusText.Text = items.Count >= options.MaxResults
                ? $"结果已截断到 {options.MaxResults} 条，可缩小时间范围再扫"
                : "扫描完成";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"已停止，当前 {_rows.Count} 条结果";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var select = _rows.Count > 0 && !_rows.All(static r => r.IsChecked);
        foreach (var row in _rows)
        {
            row.IsChecked = select;
        }
    }

    private async void ArchiveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(static r => r.IsChecked).Select(static r => r.Path).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "先勾选要归档的文件";
            return;
        }

        var confirm = MessageBox.Show(
            $"把选中的 {selected.Count} 个文件归档到「{_viewModel.ArchiveRoot}」？\n\n默认复制（原件保留），按内容自动分类。",
            "归档选中", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var done = await _viewModel.ImportDroppedAsync(selected, null, move: false);
        StatusText.Text = $"已归档 {done}/{selected.Count} 个文件";

        foreach (var row in _rows.Where(static r => r.IsChecked).ToList())
        {
            _rows.Remove(row);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is ScanRow row)
        {
            ShellLauncher.RevealInExplorer(row.Path);
        }
    }

    private void PresetDocs_Click(object sender, RoutedEventArgs e) =>
        ExtensionBox.Text = ".docx .doc .xlsx .xls .pptx .ppt .pdf .txt .md .csv";

    private void PresetImages_Click(object sender, RoutedEventArgs e) =>
        ExtensionBox.Text = ".png .jpg .jpeg .gif .bmp .webp .heic";

    private void PresetAll_Click(object sender, RoutedEventArgs e) => ExtensionBox.Text = string.Empty;
}
