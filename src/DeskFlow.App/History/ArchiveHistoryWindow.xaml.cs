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
        SourceInitialized += (_, _) => WindowEffects.Apply(this, BackdropKind.None);
        _viewModel = viewModel;
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

        RecordList.ItemsSource = _rows;
        SummaryText.Text = _rows.Count == 0
            ? "没有可撤销的归档记录"
            : $"共 {_rows.Count} 条可撤销记录，选中后点「撤销归档」把文件放回原位置";
        StatusText.Text = string.Empty;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Search_Changed(object sender, TextChangedEventArgs e) => Reload();

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var selected = RecordList.SelectedItems.Cast<Row>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选一条记录";
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

    /// <summary>列表行：只暴露界面需要的字段，原始 FileRecord 留着给撤销用。</summary>
    public sealed record Row(FileRecord Record, string TimeText, string Name, string Category, string Source, string OriginalDirectory);
}
