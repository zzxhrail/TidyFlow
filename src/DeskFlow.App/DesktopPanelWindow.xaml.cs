using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Infrastructure.Services;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App;

/// <summary>
/// 桌面面板窗口：无边框、半透明、贴在桌面右侧。拖动标题区可移动，位置记忆在 settings.json。
/// </summary>
public partial class DesktopPanelWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private readonly ArchiveSettings _settings;
    private bool _collapsed;
    private double _maxPanelHeight = 640;

    public DesktopPanelWindow(PanelViewModel viewModel, ArchiveSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        _maxPanelHeight = Math.Max(260, area.Height - 40);
        ApplyAutoHeight();

        var left = _settings.PanelX >= 0 ? _settings.PanelX : area.Right - Width - 24;
        var top = _settings.PanelY >= 0 ? _settings.PanelY : area.Top + 60;

        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.PanelX = Left;
        _settings.PanelY = Top;
    }

    /// <summary>
    /// 面板高度 = 内容真实高度，但不超过屏幕工作区；超了就由内部 ScrollViewer 滚动，
    /// 避免出现"面板比屏幕还高、下面的分类永远点不到"。
    /// </summary>
    private void ApplyAutoHeight()
    {
        BodyScroller.MaxHeight = Math.Max(140, _maxPanelHeight - 92);
        Measure(new Size(Width, double.PositiveInfinity));
        var desired = DesiredSize.Height;
        Height = Math.Max(120, Math.Min(desired, _maxPanelHeight));
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                _settings.Save();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        Body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _collapsed ? "\uE922" : "\uE921";

        if (_collapsed)
        {
            Height = 60;
        }
        else
        {
            ApplyAutoHeight();
        }
    }

    private void AutoDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceToggle toggle })
        {
            _viewModel.SetAutoDownload(toggle, toggle.AutoDownload);
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PanelTile tile)
        {
            return;
        }

        switch (tile.Kind)
        {
            case TileKind.Pending:
                _ = _viewModel.AcceptPendingAsync(tile);
                break;
            case TileKind.Add:
                AddShortcutInteractive();
                break;
            case TileKind.Batch:
                OpenBatchWindow();
                break;
            case TileKind.Settings:
                OpenSettingsWindow();
                break;
            default:
                if (!ShellLauncher.Open(tile.Path))
                {
                    MessageBox.Show($"打不开：{tile.Path}", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                break;
        }
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PanelTile tile)
        {
            return;
        }

        var menu = new ContextMenu();
        if (tile.Kind == TileKind.Pending)
        {
            menu.Items.Add(MenuItemFor("立即归档", () => _ = _viewModel.AcceptPendingAsync(tile)));
            menu.Items.Add(MenuItemFor("忽略", () => _viewModel.IgnorePending(tile)));
        }
        else
        {
            menu.Items.Add(MenuItemFor("打开", () => ShellLauncher.Open(tile.Path)));
            menu.Items.Add(MenuItemFor("在资源管理器中显示", () => ShellLauncher.RevealInExplorer(tile.Path)));
            if (tile.Path is not null && _settings.Shortcuts.Any(s => string.Equals(s.Path, tile.Path, StringComparison.OrdinalIgnoreCase)))
            {
                menu.Items.Add(MenuItemFor("从面板移除", () => _viewModel.RemoveShortcut(tile.Path)));
            }
        }

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void AddShortcutInteractive()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要放到面板上的程序或文件",
            Filter = "程序与文件|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddShortcut(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, false);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void OpenBatch_Click(object sender, RoutedEventArgs e) => OpenBatchWindow();

    private void OpenSettingsWindow()
    {
        var window = new SettingsWindow(_viewModel, _settings) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshAll();
    }

    private void OpenBatchWindow()
    {
        var window = new MainWindow { Owner = this };
        window.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        Application.Current.Shutdown(0);
    }
}