using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskFlow.App.ViewModels;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure.Services;
using DeskFlow.Infrastructure.Settings;
using Microsoft.Win32;

namespace DeskFlow.App;

/// <summary>设置窗口：来源自动下载、归档策略、自定义分类、面板快捷方式。全部写进 settings.json。</summary>
public partial class SettingsWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private readonly ArchiveSettings _settings;

    public SettingsWindow(PanelViewModel viewModel, ArchiveSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;

        SourceList.ItemsSource = viewModel.Sources;
        ArchiveRootBox.Text = settings.ArchiveRoot;
        MoveCheck.IsChecked = settings.MoveInsteadOfCopy;

        RefreshWatchList();
        RefreshCategories();
        RefreshShortcuts();
    }

    private void RefreshWatchList()
    {
        WatchList.ItemsSource = _viewModel.BuildTargets()
            .Select(t => new ListRow($"{t.Name}  —  {t.Directory}", t.Directory))
            .ToList();
    }

    private void RefreshCategories()
    {
        var rows = new List<ListRow>();
        foreach (var category in App.Categories.All)
        {
            var extensions = category.Extensions.Count == 0 ? "（兜底）" : string.Join(" ", category.Extensions);
            var tag = category.IsBuiltIn ? "内置" : "自定义";
            rows.Add(new ListRow($"[{tag}] {category.Name}  —  {extensions}", category.Name));
        }

        CategoryList.ItemsSource = rows;
    }

    private void RefreshShortcuts()
    {
        ShortcutList.ItemsSource = _settings.Shortcuts
            .Select(s => new ListRow($"{s.Name}  —  {s.Path}", s.Path))
            .ToList();
    }

    private void AutoDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceToggle toggle })
        {
            _viewModel.SetAutoDownload(toggle, toggle.AutoDownload);
        }
    }

    private void AddWatchDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要监听的目录（聊天工具收文件目录）" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddWatchDirectory(dialog.FolderName);
            RefreshWatchList();
        }
    }

    private void RemoveWatchDir_Click(object sender, RoutedEventArgs e)
    {
        if (WatchList.SelectedItem is ListRow row)
        {
            _viewModel.RemoveWatchDirectory(row.Value);
            RefreshWatchList();
        }
    }

    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ImportRecent(TimeSpan.FromMinutes(30));
        _viewModel.RefreshAll();
        RefreshWatchList();
    }

    private void BrowseArchiveRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择归档根目录" };
        if (dialog.ShowDialog(this) == true)
        {
            ArchiveRootBox.Text = dialog.FolderName;
        }
    }

    private void ApplyArchiveRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = ArchiveRootBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _viewModel.UpdateArchiveRoot(path);
        _viewModel.RefreshAll();
    }

    private void OpenArchive_Click(object sender, RoutedEventArgs e) =>
        ShellLauncher.RevealInExplorer(_settings.ArchiveRoot);

    private void ClearIgnored_Click(object sender, RoutedEventArgs e)
    {
        App.Index.ClearHistory();
        _viewModel.RefreshAll();
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = CategoryNameBox.Text?.Trim();
        var raw = CategoryExtBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("先填类别名", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extensions = raw.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        App.Categories.UpsertUserCategory(name, extensions);
        _settings.AddUserCategory(name, extensions);
        CategoryNameBox.Text = string.Empty;
        RefreshCategories();
        _viewModel.RefreshAll();
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not ListRow row)
        {
            return;
        }

        if (!App.Categories.UserCategories.Any(c => string.Equals(c.Name, row.Value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("内置类别不能删除，可以用同名自定义类别覆盖它的扩展名。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.Categories.RemoveUserCategory(row.Value);
        _settings.RemoveUserCategory(row.Value);
        RefreshCategories();
        _viewModel.RefreshAll();
    }

    private void AddShortcutFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择程序或文件", Filter = "程序与文件|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddShortcut(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, false);
            RefreshShortcuts();
        }
    }

    private void AddShortcutFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddShortcut(new DirectoryInfo(dialog.FolderName).Name, dialog.FolderName, true);
            RefreshShortcuts();
        }
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutList.SelectedItem is ListRow row)
        {
            _viewModel.RemoveShortcut(row.Value);
            RefreshShortcuts();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.MoveInsteadOfCopy = MoveCheck.IsChecked == true;
        _settings.Save();
        base.OnClosed(e);
    }

    /// <summary>ListBox 里一行文本 + 隐藏的真实值，避免界面上解析字符串。</summary>
    private sealed record ListRow(string Display, string Value)
    {
        public override string ToString() => Display;
    }
}
