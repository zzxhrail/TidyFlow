using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskFlow.App.Services;

/// <summary>窗口背景材质：云母（Mica）/ 亚克力（Acrylic）/ 无。</summary>
public enum BackdropKind
{
    None = 1,
    Mica = 2,
    Acrylic = 3,
    Tabbed = 4,
}

/// <summary>
/// Windows 11 的 DWM 材质与暗色标题栏。
/// 走 DWMWA_SYSTEMBACKDROP_TYPE，比 Win10 的 SetWindowCompositionAttribute 稳定，
/// 也不会引入分层窗口（那是之前"最小化后空白"的根源）。
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool Apply(Window window, BackdropKind kind = BackdropKind.Mica, bool darkMode = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (darkMode)
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        // Win11 圆角；Win10 上该调用返回失败但不影响运行
        var corner = 2;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var backdrop = (int)kind;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }
}
