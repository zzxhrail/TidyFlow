using System.Runtime.InteropServices;

namespace DeskFlow.App.Services;

/// <summary>
/// Windows 11 的 DWM 圆角：非分层窗口（AllowsTransparency=False）需要它才能保持圆角外观。
/// Win10 上该属性被忽略，窗口就是直角 —— 可以接受，至少不会再出现"最小化后空白"。
/// </summary>
public static class RoundedCorners
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr handle)
    {
        try
        {
            var preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
