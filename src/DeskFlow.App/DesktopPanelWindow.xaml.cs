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
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int HtCaption = 2;

    /// <summary>标题栏可拖区域高度（DIP）：从窗口顶到标题行底。</summary>
    private const double CaptionHeight = 52;

    /// <summary>右侧按钮区宽度（DIP），这块要留给按钮，不能被当成标题栏吃掉点击。</summary>
    private const double CaptionRightReserved = 148;

    private bool _collapsed;
    private bool _pinned;
    private double _heightBeforeMinimize;
    private bool _trayHintShown;

    /// <summary>由 App 在"真正退出/重启"前置为 true，允许窗口被关掉；平时点 × 只是收起。</summary>
    public bool AllowClose { get; set; }
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

        _pinned = settings.PanelPinned;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        // 云母材质：成功就让背景透出系统材质，失败（Win10/关闭特效）退回深色实色
        if (!WindowEffects.Apply(this, BackdropKind.Mica))
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x1A, 0x1F));
        }
    }

    /// <summary>
    /// 最小化后还原时，WPF 偶尔不重绘（历史 bug：还原后窗口一片空白）。
    /// 这里显式恢复尺寸并强制整树重绘，同时避免把 0 尺寸写进配置。
    /// </summary>
    /// <summary>Alt+F4 / 任务栏关闭也会走到这里：默认不退出，收起到托盘。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            _heightBeforeMinimize = Height;
            return;
        }

        if (WindowState == WindowState.Normal && _heightBeforeMinimize > 0)
        {
            Height = Math.Clamp(_heightBeforeMinimize, MinHeight, _maxPanelHeight);
            var width = ActualWidth;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _viewModel.ApplyPanelWidth(width);
                InvalidateVisual();
                Root.InvalidateVisual();
                BodyScroller.InvalidateMeasure();
                UpdateLayout();
            }), DispatcherPriority.Loaded);
        }
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
        ApplyPinVisual();

        var left = _settings.PanelX >= 0 ? _settings.PanelX : area.Right - Width - 24;
        var top = _settings.PanelY >= 0 ? _settings.PanelY : area.Top + 60;

        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

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
        if (WindowState != WindowState.Normal)
        {
            return;
        }

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
        if (_pinned)
        {
            return;
        }

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
        if (msg == WmNcLButtonDblClk)
        {
            // 双击标题栏默认会最大化：这个面板不需要最大化，直接吞掉
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt32();
        var screenPoint = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var point = PointFromScreen(screenPoint);

        var area = DeskFlow.Core.Shell.PanelHitTest.Hit(point.X, point.Y, ActualWidth, ActualHeight, _pinned);
        var hit = ToHitCode(area);
        DeskFlow.App.Services.Diagnostics.Log($"area={area} x={point.X:F0} y={point.Y:F0} pinned={_pinned}");
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

    /// <summary>顶住 = 锁位置：点一下不可拖动，再点恢复拖动。状态写进 settings.json。</summary>
    private static int ToHitCode(DeskFlow.Core.Shell.PanelHitArea area) => area switch
    {
        DeskFlow.Core.Shell.PanelHitArea.Caption => HtCaption,
        DeskFlow.Core.Shell.PanelHitArea.Left => HtLeft,
        DeskFlow.Core.Shell.PanelHitArea.Right => HtRight,
        DeskFlow.Core.Shell.PanelHitArea.Top => HtTop,
        DeskFlow.Core.Shell.PanelHitArea.Bottom => HtBottom,
        DeskFlow.Core.Shell.PanelHitArea.TopLeft => HtTopLeft,
        DeskFlow.Core.Shell.PanelHitArea.TopRight => HtTopRight,
        DeskFlow.Core.Shell.PanelHitArea.BottomLeft => HtBottomLeft,
        DeskFlow.Core.Shell.PanelHitArea.BottomRight => HtBottomRight,
        _ => HtClient,
    };

    private void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        DeskFlow.App.Services.Diagnostics.Log($"pin toggled → {_pinned}");
        _settings.PanelPinned = _pinned;
        _settings.Save();
        ApplyPinVisual();
    }

    private void ApplyPinVisual()
    {
        if (PinButton is null)
        {
            return;
        }

        PinButton.Content = _pinned ? "\uE72E" : "\uE718";
        PinButton.ToolTip = _pinned ? "已顶住：点一下解除锁定" : "顶住：点一下锁住位置，再点解除";
        PinButton.Foreground = _pinned
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xCE, 0x6B))
            : new SolidColorBrush(Color.FromRgb(0xB9, 0xBF, 0xC7));
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
            case TileKind.Browse:
                _viewModel.OpenCategory(tile.Path ?? string.Empty, tile.Title);
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

    private void BrowseBack_Click(object sender, RoutedEventArgs e) => _viewModel.CloseBrowse();

    private void BrowseOpenFolder_Click(object sender, RoutedEventArgs e) =>
        ShellLauncher.RevealInExplorer(_viewModel.BrowsePath);

    private void BrowseItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BrowseItem item)
        {
            ShellLauncher.Open(item.FullPath);
        }
    }

    private void BrowseItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BrowseItem item)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("打开", () => ShellLauncher.Open(item.FullPath)));
        menu.Items.Add(MenuItemFor("在资源管理器中显示", () => ShellLauncher.RevealInExplorer(item.FullPath)));
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
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
            if (tile.Kind == TileKind.Browse)
            {
                menu.Items.Add(MenuItemFor("打开分类", () => _viewModel.OpenCategory(tile.Path ?? string.Empty, tile.Title)));
            }
            else
            {
                menu.Items.Add(MenuItemFor("打开", () => ShellLauncher.Open(tile.Path)));
            }

            menu.Items.Add(MenuItemFor("在资源管理器中显示", () => ShellLauncher.RevealInExplorer(tile.Path)));

            // 归档过的文件：可以撤销（放回原位置）或删除（进回收站）
            if (tile.Kind == TileKind.File && !string.IsNullOrWhiteSpace(tile.RecordId))
            {
                var recordId = tile.RecordId;
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItemFor("撤销回原位置", () => _ = UndoFromPanelAsync(recordId, tile.Title)));
                menu.Items.Add(MenuItemFor("删除（回收站）", () => DeleteFromPanel(recordId, tile.Title)));
            }

            if (tile.Path is not null && _settings.Shortcuts.Any(s => string.Equals(s.Path, tile.Path, StringComparison.OrdinalIgnoreCase)))
            {
                menu.Items.Add(MenuItemFor("从面板移除", () => _viewModel.RemoveShortcut(tile.Path)));
            }
        }

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async Task UndoFromPanelAsync(string recordId, string fileName)
    {
        var confirm = MessageBox.Show(
            $"把「{fileName}」放回原来的位置？\n\n文件会从归档目录移走。",
            "撤销归档", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        await _viewModel.UndoByIdAsync(recordId);
    }

    private void DeleteFromPanel(string recordId, string fileName)
    {
        var confirm = MessageBox.Show(
            $"删除「{fileName}」？\n\n会移到回收站，误删还能捞回来；原位置不会恢复。",
            "删除归档文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (confirm == MessageBoxResult.OK)
        {
            _viewModel.DeleteById(recordId);
        }
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

    /// <summary>归档记录：查看每条归档的来源，并可把文件放回原位置。</summary>
    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = new DeskFlow.App.History.ArchiveHistoryWindow(_viewModel) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshAll();
    }

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

    /// <summary>面板右上角的 × 只是"收起来"：程序继续留在托盘里，右键托盘图标才有真正的退出。</summary>
    private void Exit_Click(object sender, RoutedEventArgs e) => HideToTray();

    /// <summary>隐藏到托盘（不退出进程）。第一次会弹一个气泡提示怎么回来。</summary>
    public void HideToTray()
    {
        PersistGeometry();
        Hide();
        Services.Diagnostics.Log($"hidden to tray, IsVisible={IsVisible}");

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            App.Tray?.ShowBalloon("DeskFlow 仍在运行", "已收起到托盘。右键图标可打开设置、重新启动或退出。");
        }
    }

    /// <summary>从托盘回到桌面：还原 + 激活。</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }
}