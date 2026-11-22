using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskFlow.App.Services;

/// <summary>
/// 轻量动画：只动 Opacity / Translate / Scale 这类合成层属性，
/// 不碰 Width/Height/Margin（会触发布局重算），也不加 Effect（会让窗口退回软件渲染）。
/// 全部受设置项「界面动画」控制，关掉后立即变成瞬切。
/// </summary>
public static class Animations
{
    public static bool Enabled { get; set; } = true;

    /// <summary>淡入 + 轻微上移，用于窗口打开、内容区切换。</summary>
    public static void FadeSlideIn(UIElement element, double offsetY = 8, int milliseconds = 180)
    {
        if (element is null)
        {
            return;
        }

        if (!Enabled || !SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var transform = new TranslateTransform(0, offsetY);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
    }

    /// <summary>淡出，用于收起面板。</summary>
    public static void FadeOut(UIElement element, int milliseconds = 120, Action? completed = null)
    {
        if (element is null)
        {
            return;
        }

        if (!Enabled)
        {
            element.Opacity = 0;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>弹出：缩放 0.94→1 + 淡入，用于对话框 / 卡片。</summary>
    public static void PopIn(FrameworkElement element, double fromScale = 0.94, int milliseconds = 180)
    {
        if (element is null)
        {
            return;
        }

        if (!Enabled || !SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var transform = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);

        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            transform.BeginAnimation(
                property,
                new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        }
    }

    /// <summary>横向滑入：进入下一层从右侧来，返回从左侧来。</summary>
    public static void SlideIn(UIElement element, double fromX, int milliseconds = 190)
    {
        if (element is null)
        {
            return;
        }

        if (!Enabled || !SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var transform = new TranslateTransform(fromX, 0);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);

        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
    }

    /// <summary>只淡入，用于局部刷新（分类列表、日志行）。</summary>
    public static void FadeIn(UIElement element, int milliseconds = 140)
    {
        if (element is null)
        {
            return;
        }

        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);
    }
}
