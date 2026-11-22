using System.Runtime.InteropServices;

namespace DeskFlow.Infrastructure.Services;

/// <summary>
/// 删除文件时优先走回收站，用户误删还能捞回来。
/// 用 SHFileOperation（FOF_ALLOWUNDO），失败再退回直接删除。
/// </summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    public static bool TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var op = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = path + '\0' + '\0',
                fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };

            var result = SHFileOperation(ref op);
            if (result == 0 && !File.Exists(path))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
