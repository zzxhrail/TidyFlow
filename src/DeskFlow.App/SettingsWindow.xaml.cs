using DeskFlow.App.Services;
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

    /// <summary>分类规则里的一个后缀（下拉框选项）。</summary>
    private sealed record KnownExtension(string Extension, string Category)
    {
        public override string ToString() => $"{Extension} — {Category}";
    }

    /// <summary>全局监听的文件类型（界面上的小标签列表）。</summary>

    public SettingsWindow(PanelViewModel viewModel, ArchiveSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.Mica.Apply(this);
        Loaded += (_, _) => LoadTheme();

        Loaded += (_, _) => Services.Animations.FadeSlideIn(this, offsetY: 6, milliseconds: 160);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        SourceInitialized += (_, _) => WindowEffects.Apply(this, BackdropKind.None);
        _viewModel = viewModel;
        _settings = settings;

        SourceList.ItemsSource = viewModel.Sources;
        ArchiveRootBox.Text = settings.ArchiveRoot;
        MoveCheck.IsChecked = settings.MoveInsteadOfCopy;
        ContentAiCheck.IsChecked = settings.ContentClassification;
        SpacePreviewCheck.IsChecked = settings.SpacePreviewEnabled;
        UnrecognizedCheck.IsChecked = settings.UnrecognizedBucket;
        AnimationsCheck.IsChecked = settings.AnimationsEnabled;

        RefreshWatchList();
        RefreshCategories();
        RefreshShortcuts();
    }


    private static IEnumerable<string> NormalizeExtensions(IEnumerable<string> raw) =>
        raw.Select(static e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Select(static e => e.Length == 0 ? e : (e[0] == '.' ? e : "." + e))
            .Where(static e => e.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase);



    /// <summary>把若干后缀加进列表，返回实际新增数量（已存在的跳过）。</summary>


    /// <summary>「按分类添加」：弹一个分类菜单，点哪个就把该分类下所有后缀加进来。</summary>








    private async void RefreshWatchList()
    {
        if (WatchList is null)
        {
            return;
        }

        WatchList.ItemsSource = new List<ListRow> { new("正在统计…", string.Empty) };
        var rows = await _viewModel.DiagnoseAsync();
        WatchList.ItemsSource = rows
            .Select(r => new ListRow(r.Display, r.Path))
            .ToList();
    }

    private void Diagnose_Click(object sender, RoutedEventArgs e) => RefreshWatchList();

    private async void ManualImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要归档的文件",
            Multiselect = true,
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var count = await _viewModel.ImportFilesAsync(dialog.FileNames);
        MessageBox.Show($"已归档 {count} 个文件。", "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void OpenWatchDir_Click(object sender, RoutedEventArgs e)
    {
        if (WatchList.SelectedItem is ListRow row)
        {
            ShellLauncher.RevealInExplorer(row.Value);
        }
    }

    private async void AutoScan_Click(object sender, RoutedEventArgs e)
    {
        AutoScanButton.IsEnabled = false;
        AutoScanButton.Content = "扫描中…";
        try
        {
            var message = await _viewModel.ScanForDataRootsAsync();
            RefreshWatchList();
            MessageBox.Show(message, "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            AutoScanButton.Content = "自动查找数据目录…";
            AutoScanButton.IsEnabled = true;
        }
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
            MessageBox.Show("先填类别名", "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("内置类别不能删除，可以用同名自定义类别覆盖它的扩展名。", "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _settings.ContentClassification = ContentAiCheck.IsChecked == true;
        _settings.SpacePreviewEnabled = SpacePreviewCheck.IsChecked == true;
        _settings.UnrecognizedBucket = UnrecognizedCheck.IsChecked == true;
        _settings.AnimationsEnabled = AnimationsCheck.IsChecked == true;
        Services.Animations.Enabled = _settings.AnimationsEnabled;

        _settings.Save();

        // 全局监听配置变了就立即按新配置重开，不用重启程序

        base.OnClosed(e);
    }



    /// <summary>ListBox 里一行文本 + 隐藏的真实值，避免界面上解析字符串。</summary>
    private sealed record ListRow(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    /// <summary>按关键字过滤左侧导航：只留标题匹配的页。</summary>
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (Nav is null)
        {
            return;
        }

        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        if (SearchPlaceholder is not null)
        {
            SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (System.Windows.Controls.TabItem item in Nav.Items)
        {
            var title = (item.Header as System.Windows.FrameworkElement)?.TemplatedParent is null
                ? ExtractText(item)
                : ExtractText(item);
            item.Visibility = query.Length == 0 || title.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
    }

    private static string ExtractText(System.Windows.Controls.TabItem item)
    {
        var builder = new System.Text.StringBuilder();
        if (item.Header is System.Windows.DependencyObject node)
        {
            Collect(node, builder);
        }

        return builder.ToString();
    }

    private static void Collect(System.Windows.DependencyObject node, System.Text.StringBuilder builder)
    {
        if (node is System.Windows.Controls.TextBlock text)
        {
            builder.Append(text.Text);
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i), builder);
        }
    }

    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => Close();

    private bool _themeReady;

    /// <summary>初始化时把下拉框同步到当前设置（避免构造期误触发保存）。</summary>
    private void LoadTheme()
    {
        if (ThemeCombo is null)
        {
            return;
        }

        ThemeCombo.SelectedIndex = Services.ThemeService.Parse(_settings.Theme) switch
        {
            Services.ThemeMode.Dark => 1,
            Services.ThemeMode.Light => 2,
            _ => 0,
        };

        _themeReady = true;
    }

    private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeCombo is null)
        {
            return;
        }

        var mode = ThemeCombo.SelectedIndex switch
        {
            1 => Services.ThemeMode.Dark,
            2 => Services.ThemeMode.Light,
            _ => Services.ThemeMode.System,
        };

        Services.ThemeService.Apply(mode);
        _settings.Theme = Services.ThemeService.ToSetting(mode);
        _settings.Save();
    }
}