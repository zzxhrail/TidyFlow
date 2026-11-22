using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Infrastructure.Services;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App;

/// <summary>
/// 桌面面板：无边框、半透明、可拖动、可拉边角改大小。
/// 密度自适应：宽度一变就重算列数/格子尺寸/字号（见 PanelViewModel.ApplyPanelWidth），
/// 尺寸写进 settings.json，下次启动原样还原。
/// </summary>
public partial class DesktopPanelWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private readonly PanelViewModel _viewModel;
    private readonly ArchiveSettings _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _collapsed;
    private double _expandedHeight = 640;
    private double _maxPanelHeight = 640;

    public DesktopPanelWindow(PanelViewModel viewModel, ArchiveSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            PersistGeometry();
        };

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        _maxPanelHeight = Math.Max(260, area.Height - 24);
        MaxHeight = _maxPanelHeight;
        MaxWidth = Math.Max(320, area.Width - 24);

        var width = _settings.PanelWidth > 0 ? _settings.PanelWidth : 376;
        Width = Math.Clamp(width, MinWidth, MaxWidth);

        if (_settings.PanelHeight > 0)
        {
            Height = Math.Clamp(_settings.PanelHeight, MinHeight, _maxPanelHeight);
        }
        else
        {
            ApplyAutoHeight();
        }

        _expandedHeight = Height;
        _viewModel.ApplyPanelWidth(Width);

        var left = _settings.PanelX >= 0 ? _settings.PanelX : area.Right - Width - 24;
        var top = _settings.PanelY >= 0 ? _settings.PanelY : area.Top + 60;

        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.ApplyPanelWidth(ActualWidth);
        if (!_collapsed)
        {
            _expandedHeight = ActualHeight;
        }

        ScheduleSave();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ScheduleSave();
    }

    /// <summary>拖动/缩放时不要每像素写一次磁盘，停手 450ms 再落盘。</summary>
    private void ScheduleSave()
    {
        if (!IsLoaded)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistGeometry()
    {
        _settings.PanelX = Left;
        _settings.PanelY = Top;
        _settings.PanelWidth = Width;
        _settings.PanelHeight = _collapsed ? _expandedHeight : Height;
        _settings.Save();
    }

    /// <summary>首次启动没有历史尺寸时，按内容定一次高度。</summary>
    private void ApplyAutoHeight()
    {
        Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Max(200, Math.Min(DesiredSize.Height, _maxPanelHeight));
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                PersistGeometry();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// 无边框窗口自己算非客户区命中：离边 14 像素内算"拉边框"，返回对应的 HT 代码，
    /// 系统就会把这次鼠标拖动当成缩放；窗口内部仍返回 HTCLIENT，交给 DragMove 拖动。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt32();
        var screenPoint = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var point = PointFromScreen(screenPoint);

        const double edge = 14;
        var nearLeft = point.X <= edge;
        var nearRight = point.X >= ActualWidth - edge;
        var nearTop = point.Y <= edge;
        var nearBottom = point.Y >= ActualHeight - edge;

        var hit = HtClient;
        if (nearTop && nearLeft)
        {
            hit = HtTopLeft;
        }
        else if (nearTop && nearRight)
        {
            hit = HtTopRight;
        }
        else if (nearBottom && nearLeft)
        {
            hit = HtBottomLeft;
        }
        else if (nearBottom && nearRight)
        {
            hit = HtBottomRight;
        }
        else if (nearLeft)
        {
            hit = HtLeft;
        }
        else if (nearRight)
        {
            hit = HtRight;
        }
        else if (nearTop)
        {
            hit = HtTop;
        }
        else if (nearBottom)
        {
            hit = HtBottom;
        }

        handled = true;
        return new IntPtr(hit);
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        Body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _collapsed ? "\uE922" : "\uE921";

        if (_collapsed)
        {
            _expandedHeight = Height;
            Height = 60;
        }
        else
        {
            Height = Math.Clamp(_expandedHeight, MinHeight, _maxPanelHeight);
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
        PersistGeometry();
        Application.Current.Shutdown(0);
    }
}
