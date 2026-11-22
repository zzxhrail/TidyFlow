using System.Windows;

namespace DeskFlow.App.Services;

/// <summary>同一时间只留一张提示卡片，新的顶掉旧的，避免整屏弹窗。</summary>
public static class ToastService
{
    private static ToastWindow? _current;

    /// <summary>系统通知通道：由 TrayIconService 注入（Windows 通知中心的气球提示）。</summary>
    public static Action<string, string>? SystemNotifier { get; set; }

    public static void Show(string title, string detail, string? path = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(title, detail, path));
            return;
        }

        // 优先走 Windows 系统通知：在通知中心留痕，且不遮挡当前操作
        var notifier = SystemNotifier;
        if (notifier is not null)
        {
            try
            {
                notifier(title, detail);
                Diagnostics.Log($"system toast: {title} / {detail}");
                return;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("system toast failed " + ex.Message);
            }
        }

        try
        {
            _current?.Close();

            var toast = new ToastWindow(title, detail, path);
            _current = toast;
            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, toast))
                {
                    _current = null;
                }
            };

            toast.Show();
            Diagnostics.Log($"toast shown at {toast.Left:F0},{toast.Top:F0} title={title}");
        }
        catch (Exception ex)
        {
            // 弹窗失败不能影响归档本身
            Diagnostics.Log("toast failed " + ex.Message);
        }
    }
}
