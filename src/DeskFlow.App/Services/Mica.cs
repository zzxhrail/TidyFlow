using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskFlow.App.Services;

/// <summary>
/// Windows 11 云母（Mica）背景：DWM 系统背景材质 + 深色模式 + 把边框延伸到客户区，
/// 配合窗口透明背景与半透明底色，就能透出桌面的云母效果。
/// 系统不支持（Win10 / 关闭透明效果）时静默失败，窗口仍是普通深色。
/// </summary>
public static class Mica
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    // 1 = None, 2 = Mica, 3 = Acrylic, 4 = Tabbed
    private const int DwmsbtMainWindow = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>给窗口套上云母背景。必须在 SourceInitialized 之后调用。</summary>
    public static bool Apply(Window window, byte tintAlpha = 0xCC)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var dark = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var rounded = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));

            var backdrop = DwmsbtMainWindow;
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

            // 把边框延伸进客户区，否则没有系统背景材质
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);

            // 窗口本身必须透明，底色交给 Root 的半透明层
            window.Background = Brushes.Transparent;

            Diagnostics.Log($"mica: backdrop={result} (0 表示成功)");
            return result == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Diagnostics.Log($"mica 不可用：{ex.Message}");
            return false;
        }
    }
}
