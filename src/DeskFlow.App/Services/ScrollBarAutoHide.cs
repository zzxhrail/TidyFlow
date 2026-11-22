using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeskFlow.App.Services;

/// <summary>
/// 滚动条自动隐藏：平时隐藏，鼠标放进滚动区域 / 正在滚动 / 正在拖动时才浮现。
/// 作用在 ScrollViewer 上，通过模板里的 PART_VerticalScrollBar 找到真正的滚动条。
/// </summary>
public static class ScrollBarAutoHide
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(260);
    private const int HideDelayMs = 500;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ScrollBarAutoHide),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer || e.NewValue is not true)
        {
            return;
        }

        Diagnostics.Log($"scrollbar auto-hide attached: {scrollViewer.Name}");

        scrollViewer.Loaded += OnLoaded;
        scrollViewer.MouseEnter += (_, _) => Show(scrollViewer, keepVisible: true);
        scrollViewer.MouseLeave += (_, _) =>
        {
            scrollViewer.Tag = null;
            ScheduleHide(scrollViewer);
        };
        scrollViewer.ScrollChanged += (_, _) => Show(scrollViewer, keepVisible: false);
        scrollViewer.PreviewMouseWheel += (_, _) => Show(scrollViewer, keepVisible: false);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.ApplyTemplate();
        var bar = scrollViewer.Template?.FindName("PART_VerticalScrollBar", scrollViewer) as ScrollBar;
        Diagnostics.Log($"scrollbar loaded: {scrollViewer.Name} bar={(bar is null ? "null" : "ok")}");
        if (bar is not null)
        {
            bar.Opacity = 0;
            bar.IsHitTestVisible = true;
        }
    }

    private static ScrollBar? FindBar(ScrollViewer scrollViewer)
    {
        scrollViewer.ApplyTemplate();
        return scrollViewer.Template?.FindName("PART_VerticalScrollBar", scrollViewer) as ScrollBar;
    }

    private static void LogScroll(string message) => Diagnostics.Log(message);

    private static void Show(ScrollViewer scrollViewer, bool keepVisible)
    {
        var bar = FindBar(scrollViewer);
        if (bar is null)
        {
            return;
        }

        scrollViewer.Tag = keepVisible ? "hover" : scrollViewer.Tag;
        bar.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, FadeIn) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);

        if (!keepVisible)
        {
            ScheduleHide(scrollViewer);
        }
    }

    private static void ScheduleHide(ScrollViewer scrollViewer)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (scrollViewer.IsMouseOver)
            {
                return;   // 鼠标还在里面就不收
            }

            var bar = FindBar(scrollViewer);
            if (bar is null)
            {
                return;
            }

            if (bar.IsMouseOver || Mouse.Captured is Thumb)
            {
                return;   // 正在悬停或拖动
            }

            LogScroll($"scrollbar hide ({scrollViewer.Name})");
            bar.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, FadeOut) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } },
                HandoffBehavior.SnapshotAndReplace);
        };

        timer.Start();
    }
}
