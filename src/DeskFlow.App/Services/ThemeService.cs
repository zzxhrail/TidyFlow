using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DeskFlow.App.Services;

public enum ThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2,
}

/// <summary>
/// 深色 / 浅色 / 跟随系统。做法是就地改主题画刷的 Color——
/// XAML 里用 StaticResource 引用的都是同一个 SolidColorBrush 实例，
/// 所以改一次颜色，所有已经绑定的控件立刻跟着变（不用重建窗口）。
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["WindowBaseBrush"] = Color.FromArgb(0xB8, 0x14, 0x14, 0x14),
        ["CardSurfaceBrush"] = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
        ["HairlineBrush"] = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        ["DividerBrush"] = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
        ["SurfaceBrush"] = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
        ["SurfaceStrongBrush"] = Color.FromRgb(0x2C, 0x2C, 0x2C),
        ["StrokeBrush"] = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
        ["StrokeStrongBrush"] = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
        ["TextPrimaryBrush"] = Colors.White,
        ["TextSecondaryBrush"] = Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF),
        ["TextTertiaryBrush"] = Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF),
        ["GlassBrush"] = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
        ["GlassHoverBrush"] = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF),
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["WindowBaseBrush"] = Color.FromArgb(0xE6, 0xF3, 0xF3, 0xF3),
        ["CardSurfaceBrush"] = Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFC),
        ["HairlineBrush"] = Color.FromArgb(0x18, 0x00, 0x00, 0x00),
        ["DividerBrush"] = Color.FromArgb(0x10, 0x00, 0x00, 0x00),
        ["SurfaceBrush"] = Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
        ["SurfaceStrongBrush"] = Color.FromRgb(0xFB, 0xFB, 0xFB),
        ["StrokeBrush"] = Color.FromArgb(0x16, 0x00, 0x00, 0x00),
        ["StrokeStrongBrush"] = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
        ["TextPrimaryBrush"] = Color.FromRgb(0x1A, 0x1A, 0x1A),
        ["TextSecondaryBrush"] = Color.FromArgb(0xC8, 0x00, 0x00, 0x00),
        ["TextTertiaryBrush"] = Color.FromArgb(0x8A, 0x00, 0x00, 0x00),
        ["GlassBrush"] = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2),
        ["GlassHoverBrush"] = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8),
    };

    public static ThemeMode Current { get; private set; } = ThemeMode.System;

    public static bool IsLight => Current switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => IsSystemLight(),
    };

    /// <summary>启动时套用当前主题。</summary>
    public static void Initialize(ThemeMode mode) => Apply(mode);

    public static void Apply(ThemeMode mode)
    {
        Current = mode;
        var palette = IsLight ? LightPalette : DarkPalette;

        if (Application.Current is null)
        {
            return;
        }

        foreach (var (key, color) in palette)
        {
            // 直接换资源实例：XAML 里这些键都用 DynamicResource，会立刻跟着刷新
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }

        Diagnostics.Log($"theme applied: {mode} (light={IsLight})");
    }

    public static ThemeMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" or "深色" => ThemeMode.Dark,
        "light" or "浅色" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    public static string ToSetting(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "Dark",
        ThemeMode.Light => "Light",
        _ => "System",
    };

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
