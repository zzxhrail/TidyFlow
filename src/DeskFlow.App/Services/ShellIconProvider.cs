using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskFlow.App.Services;

/// <summary>
/// 取出系统真实文件图标（Word/Excel/PDF 各自的图标），面板才能像资源管理器那样一眼分辨。
/// 按"路径或扩展名"缓存，避免每次刷新都打一次 shell。
/// </summary>
public static class ShellIconProvider
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ImageSource? Empty = null;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Directory.Exists(path) ? ForFolder(path) : ForFile(path);
    }

    public static ImageSource? ForFolder(string path)
    {
        var key = "dir:" + path;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = Load(path, FileAttributeDirectory, useAttributes: !Directory.Exists(path));
        Cache[key] = icon;
        return icon;
    }

    public static ImageSource? ForFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        // .exe/.lnk 每个程序图标都不一样，必须按完整路径缓存；其余按扩展名缓存即可。
        var perPath = extension is ".exe" or ".lnk" or ".ico" or ".url" or ".msi";
        var key = perPath ? "path:" + path : "ext:" + extension;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = Load(File.Exists(path) ? path : extension, FileAttributeNormal, useAttributes: !File.Exists(path));
        Cache[key] = icon;
        return icon;
    }

    public static ImageSource? ForGlyphFallback() => Empty;

    private static ImageSource? Load(string path, uint attributes, bool useAttributes)
    {
        try
        {
            var info = new ShFileInfo();
            var flags = ShgfiIcon | ShgfiLargeIcon | (useAttributes ? ShgfiUseFileAttributes : 0);
            var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}