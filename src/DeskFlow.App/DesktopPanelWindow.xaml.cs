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
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));

        // 待接收列表变化时刷新「共 N 个，已选 M 个」与按钮状态
        _viewModel.Confirmed += (text, success) => ShowConfirm(text, success);

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PanelViewModel.PendingCount) or nameof(PanelViewModel.HasPending))
            {
                UpdatePendingSummary();
            }
        };

        SizeChanged += OnSizeChanged;
        _resizeTimer.Tick += OnResizeSettled;

        // 空格快速预览（类似 macOS Quick Look）：鼠标悬停哪个文件，就预览哪个
        PreviewKeyDown += OnPanelKeyDown;
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseDown;

        // 拖放：用 handledEventsToo 挂到窗口上。
        // 之前挂在 XAML 属性上，拖到 ListBox / ScrollViewer 这些"自己会处理拖放"的子控件上时
        // 事件被吞掉，表现就是"拖到中间那片区域没反应"。
        AddHandler(DragDrop.DragOverEvent, new DragEventHandler(Panel_DragOver), handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler(Panel_DragLeave), handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, new DragEventHandler(Panel_Drop), handledEventsToo: true);
        Loaded += (_, _) =>
        {
            InstallSpacePreviewHook();

            // 自动化测试钩子：按拖放分流的同一套规则处理给定路径（仅回归验证用）
            var autoDrop = Environment.GetEnvironmentVariable("DESKFLOW_AUTODROP");
            if (!string.IsNullOrWhiteSpace(autoDrop))
            {
                var dropped = autoDrop.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var autoShortcuts = dropped.Where(PanelViewModel.IsShortcutFile).ToArray();
                var autoFiles = dropped.Where(p => !PanelViewModel.IsShortcutFile(p)).ToArray();
                if (autoShortcuts.Length > 0)
                {
                    _viewModel.AddShortcuts(autoShortcuts);
                }

                if (autoFiles.Length > 0)
                {
                    _ = _viewModel.ImportDroppedAsync(autoFiles, null, false);
                }
            }
            Animations.FadeSlideIn(Root, offsetY: 10, milliseconds: 200);
        };
        Closed += (_, _) => UninstallSpacePreviewHook();
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

        if (WindowState == WindowState.Normal && IsVisible && _heightBeforeMinimize > 0)
        {
            Height = Math.Clamp(_heightBeforeMinimize, MinHeight, _maxPanelHeight);
            ForceRepaint();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        _maxPanelHeight = Math.Max(320, area.Height - 24);
        MaxHeight = _maxPanelHeight;
        MaxWidth = Math.Max(320, area.Width - 24);

        // 默认尺寸：960×800（两栏布局：左侧导航 140 + 右侧内容），窗口比例接近 6:5
        const double defaultWidth = 960;
        const double defaultHeight = 800;

        var savedWidth = _settings.PanelWidth;
        var savedHeight = _settings.PanelHeight;

        // 老版本存下来的窗口偏窄（< 800），一次性按新默认迁移；用户自己拉大过的不动
        if (!_settings.PanelLayoutMigrated && savedWidth < 800)
        {
            savedWidth = defaultWidth;
            savedHeight = defaultHeight;
            _settings.PanelWidth = defaultWidth;
            _settings.PanelHeight = defaultHeight;
            _settings.PanelLayoutMigrated = true;
            _settings.Save();
        }

        var width = savedWidth > 0 ? savedWidth : defaultWidth;
        Width = Math.Clamp(width, MinWidth, MaxWidth);

        if (savedHeight > 0)
        {
            Height = Math.Clamp(savedHeight, MinHeight, _maxPanelHeight);
        }
        else
        {
            Height = Math.Clamp(defaultHeight, MinHeight, _maxPanelHeight);
        }

        _expandedHeight = Height;
        _viewModel.ApplyPanelWidth(Width);
        ApplyPinVisual();
        Services.Diagnostics.Log($"render tier={System.Windows.Media.RenderCapability.Tier >> 16} (2=硬件加速)");

        var left = _settings.PanelX >= 0 ? _settings.PanelX : area.Right - Width - 24;
        var top = _settings.PanelY >= 0 ? _settings.PanelY : area.Top + 60;

        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(90),
    };

    /// <summary>
    /// 拖拽缩放时 SizeChanged 每帧都会来，直接重排会让画面掉帧。
    /// 用 90ms 防抖：松手（或停顿）后再统一重排一次。
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        if (!_collapsed)
        {
            _expandedHeight = ActualHeight;
        }

        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 2)
        {
            ScheduleSave();
            return;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
        ScheduleSave();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        _viewModel.ApplyPanelWidth(ActualWidth);
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

    private const double CollapsedHeight = 92;   // 收起条默认高度（用户可拖拽调整，会记住）

    private bool _topmostBeforeCollapse;

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    /// <summary>收起状态下的双击：点到图标就启动应用，点到空白就展开。</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_collapsed || e.ClickCount < 2)
        {
            return;
        }

        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button)
            {
                return;   // 交给图标：双击启动
            }
        }

        Services.Diagnostics.Log("collapsed dblclick → expand");
        e.Handled = true;
        SetCollapsed(false);
    }

    private void CollapsedBar_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DependencyObject source)
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Button)
                {
                    return;
                }

                if (ReferenceEquals(node, source))
                {
                    break;
                }
            }
        }

        Services.Diagnostics.Log("collapsed bar double-click → expand");
        e.Handled = true;
        SetCollapsed(false);
    }

    private void CollapsedBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 收起条上双击空白处 = 展开
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            SetCollapsed(false);
        }
    }

    private void CollapsedTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostVisual();
    }

    private void UpdateTopmostVisual()
    {
        if (CollapsedTopmostButton is null)
        {
            return;
        }

        CollapsedTopmostButton.Content = Topmost ? "\uE840" : "\uE718";
        CollapsedTopmostButton.ToolTip = Topmost ? "已置顶（点一下取消）" : "置顶（总在最前）";
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;

        if (collapsed)
        {
            _expandedHeight = Height;
            _topmostBeforeCollapse = Topmost;

            MinHeight = 60;   // 收起态允许拖拽调整高度
            BodyScroller.Visibility = Visibility.Collapsed;
            CollapsedBar.Visibility = Visibility.Visible;
            Height = Math.Max(60, _settings.PanelCollapsedHeight);

            // 收起后置顶，随时能点到（用户也可以再点按钮取消）
            Topmost = true;
            UpdateTopmostVisual();
        }
        else
        {
            Height = Math.Clamp(_expandedHeight, 380, _maxPanelHeight);
            MinHeight = 380;
            BodyScroller.Visibility = Visibility.Visible;
            CollapsedBar.Visibility = Visibility.Collapsed;

            Topmost = _topmostBeforeCollapse;
        }

        CollapseButton.Content = collapsed ? "\uE922" : "\uE921";

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Root.InvalidateMeasure();
            Root.InvalidateArrange();
            Root.InvalidateVisual();
            Root.UpdateLayout();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>收/展时只切换标题行以外的分区，标题栏（Logo、按钮）始终可见。</summary>
    private void SetBodyRowsVisible(bool visible)
    {
        if (BodyScroller is null)
        {
            return;
        }

        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in BodyScroller.Children)
        {
            if (child is FrameworkElement element && Grid.GetRow(element) > 0)
            {
                element.Visibility = target;
            }
        }
    }

    private void ScheduleForceRepaint()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ForceRepaint();
            Root.Opacity = 1;
            Root.RenderTransform = Transform.Identity;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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

        Services.Diagnostics.Log($"tile click: kind={tile.Kind} title={tile.Title}");
        switch (tile.Kind)
        {
            case TileKind.Pending:
                OpenPreview(tile);
                break;
            case TileKind.Browse:
                _viewModel.OpenCategory(tile.Path ?? string.Empty, tile.Title);
                break;
            case TileKind.Add:
                AddShortcutInteractive();
                break;
            case TileKind.Batch:
                break;
            case TileKind.Settings:
                OpenSettingsWindow();
                break;
            case TileKind.Shortcut:
            case TileKind.Category:
                // 应用 / 文件夹快捷方式：双击才启动，单击不动作（与资源管理器一致）
                break;
            default:
                LaunchTile(tile);
                break;
        }
    }

    /// <summary>双击瓦片：应用 / 文件夹快捷方式在这里启动。</summary>
    private void Tile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PanelTile tile)
        {
            return;
        }

        if (tile.Kind is TileKind.Shortcut or TileKind.Category)
        {
            e.Handled = true;
            LaunchTile(tile);
        }
    }

    private void LaunchTile(PanelTile tile)
    {
        if (string.IsNullOrWhiteSpace(tile.Path))
        {
            return;
        }

        if (!ShellLauncher.Open(tile.Path))
        {
            MessageBox.Show(this, "打不开：" + tile.Path, "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>待接收文件：先看内容再决定归档，别直接吞进分类目录。</summary>
    // ---- 空格快速预览：全局低级键盘钩子（只在鼠标位于面板上时生效）----
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkSpace = 0x20;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static LowLevelKeyboardProc? _hookProc;   // 保持引用，防止被 GC
    private IntPtr _hook;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private void InstallSpacePreviewHook()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hookProc = SpacePreviewHook;
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, IntPtr.Zero, 0);
        Services.Diagnostics.Log($"space preview hook={_hook}");
    }

    private void UninstallSpacePreviewHook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _hookProc = null;
        }
    }

    /// <summary>
    /// 鼠标压在面板上时按下空格 → 预览悬停的文件。
    /// 用全局钩子是故意的：这样不用先点一下面板拿焦点（和 macOS Quick Look 一样）；
    /// 钩子里只做判断和投递，真正的窗口弹在 UI 线程上做。
    /// </summary>
    private IntPtr SpacePreviewHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            if (vk == VkSpace && _settings.SpacePreviewEnabled && IsCursorOverPanel())
            {
                var hovered = _hoverTile;
                var row = _hoverBrowseItem;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (hovered is { Kind: TileKind.Pending or TileKind.File } tile && !string.IsNullOrWhiteSpace(tile.Path))
                    {
                        OpenPreview(tile, tile.Kind == TileKind.Pending);
                    }
                    else if (row is not null)
                    {
                        var fileTile = new PanelTile
                        {
                            Kind = TileKind.File,
                            Title = row.Name,
                            Path = row.FullPath,
                            Icon = row.Icon,
                        };

                        OpenPreview(fileTile, isPending: false);
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);

                return (IntPtr)1;   // 吃掉这个空格，别让面板滚动/按钮触发
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsCursorOverPanel()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return false;
        }

        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(hwnd, 2);   // GA_ROOT
        var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return root == self || hwnd == self;
    }

    private bool _dragging;

    private PanelTile? _hoverTile;
    private BrowseItem? _hoverBrowseItem;

    /// <summary>勾选框：只切选中状态，不要顺带触发瓦片的"打开预览"。</summary>
    private void TilePicker_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UpdatePendingSummary();
    }

    private void PendingSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var select = !_viewModel.PendingFiles.All(static t => t.IsSelected);
        foreach (var tile in _viewModel.PendingFiles)
        {
            tile.IsSelected = select;
        }

        UpdatePendingSummary();
    }

    private async void PendingAcceptSelected_Click(object sender, RoutedEventArgs e)
    {
        var count = await _viewModel.AcceptSelectedPendingAsync();
        if (count == 0)
        {
            PendingSummary.Text = "先勾选要接收的文件";
        }

        UpdatePendingSummary();
    }

    private void PendingIgnoreAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _viewModel.IgnoreAllPending();
        UpdatePendingSummary(count == 0 ? "暂无待接收文件" : null);
    }

    /// <summary>切换分区/分类时给内容区一个 8px 的滑入，观感更连贯（不做布局动画，只动合成层）。</summary>
    private System.Windows.Threading.DispatcherTimer? _confirmTimer;

    /// <summary>确认动画：卡片在内容区上方缩放淡入，成功是 ✓、失败是 !，1.2 秒后淡出。</summary>
    private void ShowConfirm(string text, bool success)
    {
        if (ConfirmCard is null)
        {
            return;
        }

        ConfirmText.Text = text;
        ConfirmGlyph.Text = success ? "\uE73E" : "\uE711";
        ConfirmGlyph.Foreground = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        ConfirmCard.Visibility = Visibility.Visible;
        Animations.PopIn(ConfirmCard, 0.9, 160);

        _confirmTimer?.Stop();
        _confirmTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer?.Stop();
            Animations.FadeOut(ConfirmCard, 180, () => ConfirmCard.Visibility = Visibility.Collapsed);
        };
        _confirmTimer.Start();
    }

    private void AnimateContent(int direction = 0)
    {
        if (ContentHost is null)
        {
            return;
        }

        // direction: +1 进入更深一层（从右侧滑入）、-1 返回（从左侧滑入）、0 同级切换（上浮淡入）
        if (direction == 0)
        {
            Animations.FadeSlideIn(ContentHost, offsetY: 8, milliseconds: 170);
        }
        else
        {
            Animations.SlideIn(ContentHost, direction * 26, 190);
        }
    }

    private void UpdatePendingSummary(string? hint = null)
    {
        if (PendingSummary is null)
        {
            return;
        }

        var selected = _viewModel.PendingSelectedCount;
        var total = _viewModel.PendingFiles.Count;
        PendingSummary.Text = hint ?? (total == 0 ? string.Empty : $"共 {total} 个，已选 {selected} 个");
        PendingSelectAllButton.Content = total > 0 && _viewModel.PendingFiles.All(static t => t.IsSelected) ? "取消全选" : "全选";
        PendingAcceptButton.IsEnabled = selected > 0;
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e) =>
        _hoverTile = (sender as FrameworkElement)?.Tag as PanelTile;

    private void Tile_MouseLeave(object sender, MouseEventArgs e) => _hoverTile = null;

    private void BrowseRow_MouseEnter(object sender, MouseEventArgs e) =>
        _hoverBrowseItem = (sender as FrameworkElement)?.Tag as BrowseItem;

    private void BrowseRow_MouseLeave(object sender, MouseEventArgs e) => _hoverBrowseItem = null;

    /// <summary>空格：预览鼠标正悬停的那个文件（待接收用完整预览，已归档的只读预览）。</summary>
    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_settings.SpacePreviewEnabled)
        {
            return;
        }

        if (_hoverTile is { Kind: TileKind.Pending or TileKind.File } tile && !string.IsNullOrWhiteSpace(tile.Path))
        {
            e.Handled = true;
            OpenPreview(tile, tile.Kind == TileKind.Pending);
            return;
        }

        if (_hoverBrowseItem is not null)
        {
            e.Handled = true;
            var tileForFile = new PanelTile
            {
                Kind = TileKind.File,
                Title = _hoverBrowseItem.Name,
                Path = _hoverBrowseItem.FullPath,
                Icon = _hoverBrowseItem.Icon,
            };

            OpenPreview(tileForFile, isPending: false);
        }
    }

    private void OpenPreview(PanelTile tile, bool isPending = true)
    {
        try
        {
            Services.Diagnostics.Log($"open preview: {tile.Title} path={tile.Path}");
            var window = new Preview.PreviewWindow(_viewModel, tile, isPending) { Owner = this };
            window.ShowDialog();
            Services.Diagnostics.Log("preview closed");
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Log("preview failed: " + ex);
            MessageBox.Show(ex.Message, "预览打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseBack_Click(object sender, RoutedEventArgs e) => _viewModel.CloseBrowse();

    /// <summary>一级导航右键：新建分类、把文件夹固定到导航、打开归档目录。</summary>
    private void NavList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var entry = FindNavEntry(e.OriginalSource as DependencyObject);
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor("新建分类…", NewCategory));
        menu.Items.Add(MenuItemFor("添加导航（选文件夹）…", AddNavFromFolder));
        if (entry is { Kind: "user" })
        {
            menu.Items.Add(MenuItemFor($"移除导航「{entry.Title}」", () =>
            {
                _viewModel.RemoveNavItem(entry.Key);
                AnimateContent();
            }));
        }

        if (entry is { Kind: "group" })
        {
            menu.Items.Add(MenuItemFor("进入分类列表", () =>
            {
                _viewModel.OpenCategoryNav();
                AnimateContent();
            }));
        }

        menu.Items.Add(MenuItemFor("在资源管理器中打开归档目录", () =>
        {
            try
            {
                Directory.CreateDirectory(_viewModel.ArchiveRoot);
                System.Diagnostics.Process.Start("explorer.exe", _viewModel.ArchiveRoot);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
            }
        }));

        menu.PlacementTarget = NavList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>二级（分类）右键：新建 / 重命名 / 删除自定义分类。</summary>
    private void CategoryNav_RightClick(object sender, MouseButtonEventArgs e)
    {
        var entry = FindNavEntry(e.OriginalSource as DependencyObject);
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor("新建分类…", NewCategory));

        if (entry is { Kind: "category" })
        {
            menu.Items.Add(MenuItemFor($"重命名「{entry.Title}」…", () => RenameCategory(entry)));
            menu.Items.Add(MenuItemFor($"删除「{entry.Title}」", () => DeleteCategory(entry)));
            menu.Items.Add(MenuItemFor("打开这个分类的文件夹", () =>
            {
                var dir = string.IsNullOrWhiteSpace(entry.Key) ? Path.Combine(_viewModel.ArchiveRoot, entry.Title) : entry.Key;
                try
                {
                    if (Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                }
            }));
        }

        menu.PlacementTarget = CategoryNav;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                return item;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static PanelViewModel.NavEntry? FindNavEntry(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem { DataContext: PanelViewModel.NavEntry entry })
            {
                return entry;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void NewCategory()
    {
        var name = Dialogs.TextPromptWindow.Ask(this, "新建分类", "分类名称（会在归档目录下建立同名文件夹）");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_viewModel.CreateCategory(name, out var message))
        {
            _viewModel.OpenCategoryNav();
            AnimateContent();
        }
        else
        {
            MessageBox.Show(this, message, "新建分类", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RenameCategory(PanelViewModel.NavEntry entry)
    {
        var name = Dialogs.TextPromptWindow.Ask(this, "重命名分类", "新的分类名称", entry.Title);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_viewModel.RenameCategory(entry.Title, name, out var message))
        {
            MessageBox.Show(this, message, "重命名分类", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        AnimateContent();
    }

    private void DeleteCategory(PanelViewModel.NavEntry entry)
    {
        var confirm = MessageBox.Show(this, $"删除分类「{entry.Title}」？\n\n只删除分类规则，磁盘上已经归档的文件不会动。",
            "删除分类", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_viewModel.RemoveCategory(entry.Title, out var message))
        {
            MessageBox.Show(this, message, "删除分类", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        AnimateContent();
    }

    private void AddNavFromFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要放到导航栏的文件夹" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!_viewModel.AddNavItem(string.Empty, dialog.FolderName, out var message))
        {
            MessageBox.Show(this, message, "添加导航", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        AnimateContent();
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BackToRootNav();
        _viewModel.SelectShortcuts();
        NavList.SelectedItem = null;
    }

    /// <summary>二级菜单「+」：新建分类（和右键菜单同一个入口）。</summary>
    private void CategoryAdd_Click(object sender, RoutedEventArgs e) => NewCategory();

    /// <summary>二级菜单「删除」：删除选中的分类（Ctrl/Shift 可多选；内置分类跳过）。</summary>
    private void CategoryDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = CategoryNav.SelectedItems
            .OfType<PanelViewModel.NavEntry>()
            .Where(static entry => entry.Kind == "category")
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "先在列表里选中要删除的分类（Ctrl / Shift 可多选）。", "删除分类",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "删除选中的 " + selected.Count + " 个分类？\n\n只删除分类规则，磁盘上已归档的文件不会动；内置分类会自动跳过。",
            "删除分类", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = 0;
        var skipped = 0;
        foreach (var entry in selected)
        {
            if (_viewModel.RemoveCategory(entry.Title, out _))
            {
                removed++;
            }
            else
            {
                skipped++;
            }
        }

        UpdateCategorySelectionHint();
        AnimateContent();

        var extra = skipped > 0 ? "，" + skipped + " 个内置分类未删除" : string.Empty;
        MessageBox.Show(this, "已删除 " + removed + " 个分类规则" + extra + "。\n\n磁盘上的归档文件保持原样。",
            "删除分类", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateCategorySelectionHint()
    {
        if (CategorySelectionHint is null)
        {
            return;
        }

        var count = CategoryNav?.SelectedItems?.Count ?? 0;
        CategorySelectionHint.Text = count > 1 ? "已选 " + count : string.Empty;
        if (CategoryDeleteButton is not null)
        {
            CategoryDeleteButton.IsEnabled = count > 0;
        }
    }

    private void CategoryNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryNav.SelectedItems.Count > 1)
        {
            return;   // 多选是在准备批量删除，不跳进某个分类
        }

        if (CategoryNav.SelectedItem is PanelViewModel.NavEntry { Kind: "category" } entry)
        {
            _viewModel.OpenCategory(entry.Key, entry.Title);
            AnimateContent();
        }
    }

    /// <summary>左侧导航：点一级切分区，点二级（分类）进该分类的文件列表。</summary>
    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not PanelViewModel.NavEntry entry)
        {
            return;
        }

        switch (entry.Kind)
        {
            case "section" when entry.Key == "shortcuts":
                _viewModel.SelectShortcuts();
                break;
            case "section" when entry.Key == "recent":
                _viewModel.SelectRecent();
                break;
            case "group" when entry.Key == "categories":
                // 「分类」点了不是展开，而是进二级页
                _viewModel.OpenCategoryNav();
        AnimateContent();
                CategoryNav.SelectedItem = null;
                break;
            case "category":
                _viewModel.OpenCategory(entry.Key, entry.Title);
                break;
        }
    }

    private void BrowseOpenFolder_Click(object sender, RoutedEventArgs e) =>
        ShellLauncher.RevealInExplorer(_viewModel.BrowsePath);

    private void BrowseCheck_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 只切换选中，不要顺带打开文件
        UpdateBrowseSelection();
    }

    private void BrowseSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var select = !_viewModel.BrowseItems.All(static item => item.IsSelected);
        foreach (var item in _viewModel.BrowseItems)
        {
            item.IsSelected = select;
        }

        UpdateBrowseSelection();
    }

    private void BrowseDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.BrowseItems.Where(static item => item.IsSelected).Select(static item => item.FullPath).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "先勾选要删除的文件。", "删除文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "删除选中的 " + selected.Count + " 个文件？它们会进回收站，可以从回收站还原。",
            "删除文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _viewModel.DeleteArchivedFiles(selected);
        UpdateBrowseSelection();
        if (removed == 0)
        {
            MessageBox.Show(this, "没能删除（文件可能已被移动或占用）。", "删除文件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateBrowseSelection()
    {
        if (BrowseSelectAllButton is null || BrowseDeleteButton is null)
        {
            return;
        }

        var total = _viewModel.BrowseItems.Count;
        var selected = _viewModel.BrowseItems.Count(static item => item.IsSelected);
        BrowseSelectAllButton.Content = total > 0 && selected == total ? "取消全选" : "全选";
        BrowseDeleteButton.Content = selected > 0 ? "删除选中（" + selected + "）" : "删除选中";
        BrowseDeleteButton.IsEnabled = selected > 0;
    }

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
            menu.Items.Add(MenuItemFor("预览", () => OpenPreview(tile)));
            menu.Items.Add(MenuItemFor("立即归档", () => _ = _viewModel.AcceptPendingAsync(tile)));
            menu.Items.Add(MenuItemFor("忽略", () => _viewModel.IgnorePending(tile)));
        }
        else
        {
            if (tile.Kind == TileKind.Browse)
            {
                menu.Items.Add(MenuItemFor("打开分类", () => { _viewModel.OpenCategory(tile.Path ?? string.Empty, tile.Title); AnimateContent(1); }));
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

    // 拖拽入面板：落到哪个分类就归档进哪个分类
    private Button? _dropTarget;
    private Brush? _dropTargetBrush;

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 和资源管理器一致：默认复制，按住 Shift 才是移动
        e.Effects = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0
            ? DragDropEffects.Move
            : DragDropEffects.Copy;

        if (!_dragging)
        {
            _dragging = true;
            Root.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }

        var tileButton = FindTileButton(e.OriginalSource as DependencyObject);
        if (!ReferenceEquals(tileButton, _dropTarget))
        {
            RestoreDropTarget();
            _dropTarget = tileButton;
            if (_dropTarget is not null)
            {
                _dropTargetBrush = _dropTarget.Background;
                _dropTarget.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xCD, 0xFF));
            }
        }

        e.Handled = true;
    }

    private void Panel_DragLeave(object sender, DragEventArgs e)
    {
        RestoreDropTarget();
        ResetDragVisual();
        e.Handled = true;
    }

    private void ResetDragVisual()
    {
        if (_dragging)
        {
            _dragging = false;
            Root.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        }
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        RestoreDropTarget();
        ResetDragVisual();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var tile = FindTileButton(e.OriginalSource as DependencyObject)?.Tag as PanelTile;
        var category = tile is { Kind: TileKind.Browse } ? tile.Title : null;
        var move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        Services.Diagnostics.Log($"drop: paths={paths.Length} tile={tile?.Kind.ToString() ?? "none"} category={category ?? "(auto)"} move={move}");

        // .lnk / .url 拖进来 = 添加面板快捷方式，不归档、不分类
        var shortcutPaths = paths.Where(PanelViewModel.IsShortcutFile).ToArray();
        var filePaths = paths.Where(p => !PanelViewModel.IsShortcutFile(p)).ToArray();

        if (shortcutPaths.Length > 0)
        {
            _viewModel.AddShortcuts(shortcutPaths);
        }

        if (filePaths.Length > 0)
        {
            _ = _viewModel.ImportDroppedAsync(filePaths, category, move);
        }
        e.Handled = true;
    }

    private void RestoreDropTarget()
    {
        if (_dropTarget is not null)
        {
            _dropTarget.Background = _dropTargetBrush;
            _dropTarget = null;
            _dropTargetBrush = null;
        }
    }

    private static Button? FindTileButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button && button.Tag is PanelTile)
            {
                return button;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
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

    /// <summary>标题栏「更多」：把归档记录 / 设置 / 扫描磁盘收进一个菜单，给标题腾地方。</summary>
    /// <summary>"⋯"菜单顶部显示版本号：用户能一眼确认自己跑的是哪一版（避免"我明明是新的"却看到旧界面）。</summary>
    private void OpenMoreMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        // 顶部一行版本号（不可点）：排查"怎么还是旧界面"时第一眼就能确认跑的哪一版
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        menu.Items.Add(new MenuItem
        {
            Header = $"TidyFlow {version?.ToString(3) ?? "?"}",
            IsEnabled = false,
            FontWeight = System.Windows.FontWeights.SemiBold,
        });

        // 顺序：设置 → 归档记录 →（细线）→ 扫描磁盘
        menu.Items.Add(MenuItemFor("设置", () => OpenSettingsWindow()));
        menu.Items.Add(MenuItemFor("归档记录 · 可撤销", () => OpenHistory_Click(sender, e)));
        menu.Items.Add(MenuItemFor("扫描磁盘…", OpenScanInteractive));

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void OpenScanInteractive()
    {
        var window = new Scan.ScanWindow(_viewModel) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshAll();
    }

    /// <summary>归档记录：查看每条归档的来源，并可把文件放回原位置。</summary>
    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = new DeskFlow.App.History.ArchiveHistoryWindow(_viewModel) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshAll();
    }

    private void OpenSettingsWindow()
    {
        var window = new SettingsWindow(_viewModel, _settings) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshAll();
    }


    /// <summary>面板右上角的 × 只是"收起来"：程序继续留在托盘里，右键托盘图标才有真正的退出。</summary>
    private void Exit_Click(object sender, RoutedEventArgs e) => HideToTray();

    /// <summary>隐藏到托盘（不退出进程）。第一次会弹一个气泡提示怎么回来。</summary>
    public void HideToTray()
    {
        PersistGeometry();
        _heightBeforeMinimize = Height > 100 ? Height : _heightBeforeMinimize;

        ShowInTaskbar = false;
        Hide();
        Services.Diagnostics.Log($"hidden to tray, IsVisible={IsVisible}");

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            App.Tray?.ShowBalloon("TidyFlow 仍在运行", "已收起到托盘。右键图标可打开设置、重新启动或退出。");
        }
    }

    /// <summary>从托盘回到桌面：还原 + 激活。</summary>
    public void ShowFromTray()
    {
        ShowInTaskbar = true;

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (_heightBeforeMinimize > 0)
        {
            Height = Math.Clamp(_heightBeforeMinimize, MinHeight, _maxPanelHeight);
        }

        Activate();
        Topmost = true;
        Topmost = false;
        ForceRepaint();
    }

    /// <summary>
    /// 最小化 / 托盘恢复后强制重建画面：WPF 在这种情况下不会自动重绘，
    /// 只靠 InvalidateVisual 不够，得把宽度挪 1px 再挪回来触发重新布局与合成。
    /// </summary>
    /// <summary>
    /// 最小化→还原之后 WPF 经常不再画内容（留一块空白卡片）。
    /// 可靠做法：把窗口在同一位置"藏一下再显示"，让 DWM 重建渲染面；
    /// 只调 InvalidateVisual / RedrawWindow 在还原场景里常常无效。
    /// </summary>
    private void ForceRepaint()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowState != WindowState.Normal)
            {
                return;
            }

            var left = Left;
            var top = Top;
            var width = Width;
            var height = Height;

            Hide();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                Show();
                Activate();

                _viewModel.ApplyPanelWidth(width);
                UpdateLayout();
                InvalidateVisual();
                Root.InvalidateVisual();
                Services.WindowEffects.Apply(this, Services.BackdropKind.Mica);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow | RdwFrame);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwFrame = 0x0400;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRgn, uint flags);
}