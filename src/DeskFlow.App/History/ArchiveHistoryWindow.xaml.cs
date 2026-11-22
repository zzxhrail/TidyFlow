using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure.Services;

namespace DeskFlow.App.History;

/// <summary>归档记录窗口：看每条归档的来源与去向，并可把文件放回原位置（撤销归档）。</summary>
public partial class ArchiveHistoryWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private List<Row> _rows = [];

    public ArchiveHistoryWindow(PanelViewModel viewModel)
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        SourceInitialized += (_, _) => WindowEffects.Apply(this, BackdropKind.None);
        _viewModel = viewModel;
        RecordList.MouseRightButtonUp += RecordList_RightClick;
        Reload();
    }

    private void Reload()
    {
        var keyword = SearchBox?.Text?.Trim();
        var records = _viewModel.GetUndoableRecords(string.IsNullOrWhiteSpace(keyword) ? null : keyword);

        _rows = records.Select(record => new Row(
            record,
            (record.ArchivedAt ?? record.DetectedAt).LocalDateTime.ToString("MM-dd HH:mm"),
            record.FileName,
            record.Category,
            record.Source.DisplayName(),
            Path.GetDirectoryName(record.SourcePath) ?? record.SourcePath)).ToList();

        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(Row.IsChecked))
                {
                    UpdateUndoButton();
                }
            };
        }

        RecordList.ItemsSource = _rows;
        UpdateUndoButton();

        // 整批撤销：列出"同类别 + 15 秒内"的归档批次
        var selectedBatch = BatchBox?.SelectedItem as DeskFlow.Infrastructure.History.ArchiveBatch;
        var batches = _viewModel.GetUndoableBatches();
        BatchBox.ItemsSource = batches;
        BatchBox.SelectedItem = batches.FirstOrDefault(b => b.Key == selectedBatch?.Key) ?? batches.FirstOrDefault();
        SummaryText.Text = _rows.Count == 0
            ? "没有可撤销的归档记录"
            : $"共 {_rows.Count} 条可撤销记录，选中后点「撤销归档」把文件放回原位置";
        StatusText.Text = string.Empty;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>按钮文案实时反映勾选了多少条。</summary>
    private void UpdateUndoButton()
    {
        var checkedCount = _rows.Count(static r => r.IsChecked);
        UndoButton.Content = checkedCount > 0 ? $"撤销选中（{checkedCount}）" : "撤销选中";
        UndoButton.IsEnabled = checkedCount > 0 || RecordList.SelectedItems.Count > 0;

        if (SelectAllButton is not null)
        {
            SelectAllButton.Content = _rows.Count > 0 && _rows.All(static r => r.IsChecked) ? "取消全选" : "全选";
        }
    }

    /// <summary>全选 / 取消全选：按钮文案跟着状态走。</summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var select = _rows.Count > 0 && !_rows.All(static r => r.IsChecked);
        foreach (var row in _rows)
        {
            row.IsChecked = select;
        }

        SelectAllButton.Content = select ? "取消全选" : "全选";
        UpdateUndoButton();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Reload();

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(static r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            selected = RecordList.SelectedItems.Cast<Row>().ToList();
        }

        if (selected.Count == 0)
        {
            StatusText.Text = "先勾选要撤销的记录（或按住 Ctrl 点选）";
            return;
        }

        var confirm = MessageBox.Show(
            $"确定把选中的 {selected.Count} 个文件放回原位置吗？\n\n" +
            "文件会从归档目录移走，回到当初被发现的位置。",
            "撤销归档",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var done = 0;
        foreach (var row in selected)
        {
            if (await _viewModel.UndoAsync(row.Record))
            {
                done++;
            }
        }

        StatusText.Text = $"已撤销 {done}/{selected.Count} 个文件";
        SelectAllButton.Content = "全选";
        Reload();
    }

    /// <summary>撤销整批：把选中批次里的文件全部放回原位置。</summary>
    private async void UndoBatch_Click(object sender, RoutedEventArgs e)
    {
        if (BatchBox.SelectedItem is not DeskFlow.Infrastructure.History.ArchiveBatch batch)
        {
            StatusText.Text = "没有可撤销的批次";
            return;
        }

        var confirm = MessageBox.Show(
            $"确定撤销这一批吗？\n\n{batch.Display}\n\n这 {batch.Count} 个文件会从归档目录搬回各自的原位置。",
            "整批撤销",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var done = await _viewModel.UndoBatchAsync(batch);
        StatusText.Text = $"已撤销 {done}/{batch.Count} 个文件";
        Reload();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RecordList.SelectedItem is Row row)
        {
            ShellLauncher.Open(row.Record.ArchivedPath);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (RecordList.SelectedItem is Row row)
        {
            ShellLauncher.RevealInExplorer(row.Record.ArchivedPath);
        }
    }

    /// <summary>右键某一行：撤销单个 / 撤销这一批 / 打开 / 定位。</summary>
    private void RecordList_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecordList.SelectedItem is not Row row)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(NewMenuItem("打开", () => ShellLauncher.Open(row.Record.ArchivedPath)));
        menu.Items.Add(NewMenuItem("在资源管理器中显示", () => ShellLauncher.RevealInExplorer(row.Record.ArchivedPath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewMenuItem("删除（回收站）", () =>
        {
            var confirm = MessageBox.Show(
                $"删除「{row.Record.FileName}」？会移到回收站。",
                "删除归档文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (confirm == MessageBoxResult.OK && _viewModel.DeleteById(row.Record.Id))
            {
                StatusText.Text = $"已删除 {row.Record.FileName}";
                Reload();
            }
        }));
        menu.Items.Add(NewMenuItem("撤销这条", async () =>
        {
            if (await _viewModel.UndoAsync(row.Record))
            {
                StatusText.Text = $"已撤销 {row.Record.FileName}";
                Reload();
            }
        }));

        // 同一批次的全部文件
        var batch = _viewModel.GetUndoableBatches()
            .FirstOrDefault(b => b.Records.Any(r => r.Id == row.Record.Id));
        if (batch is { Count: > 1 })
        {
            menu.Items.Add(NewMenuItem($"撤销这一批（{batch.Count} 个）", async () =>
            {
                var done = await _viewModel.UndoBatchAsync(batch);
                StatusText.Text = $"已撤销 {done}/{batch.Count} 个文件";
                Reload();
            }));
        }

        menu.PlacementTarget = RecordList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem NewMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>列表行：带勾选状态，原始 FileRecord 留着给撤销用。</summary>
    public sealed class Row : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isChecked;

        public Row(FileRecord record, string timeText, string name, string category, string source, string originalDirectory)
        {
            Record = record;
            TimeText = timeText;
            Name = name;
            Category = category;
            Source = source;
            OriginalDirectory = originalDirectory;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public FileRecord Record { get; }

        public string TimeText { get; }

        public string Name { get; }

        public string Category { get; }

        public string Source { get; }

        public string OriginalDirectory { get; }

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
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }
}