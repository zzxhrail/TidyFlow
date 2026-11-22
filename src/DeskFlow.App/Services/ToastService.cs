using System.Windows;

namespace DeskFlow.App.Services;

/// <summary>同一时间只留一张提示卡片，新的顶掉旧的，避免整屏弹窗。</summary>
public static class ToastService
{
    private static ToastWindow? _current;

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
