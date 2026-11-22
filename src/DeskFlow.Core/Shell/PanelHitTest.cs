namespace DeskFlow.Core.Shell;

/// <summary>面板上一点落在哪个区域。平台无关，方便单测；WPF 层把它翻译成 HT* 常量。</summary>
public enum PanelHitArea
{
    None = 0,
    Caption,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 无边框窗口的非客户区命中计算：
/// 1. 边角 14 像素内 → 缩放；
/// 2. 标题栏区域（顶部 captionHeight、左右留 reservedRightWidth 给按钮）→ 拖动；
/// 3. 顶住（pinned）时标题栏不再可拖，只保留边角缩放；
/// 4. 其余 → 交给 WPF 正常命中（点按钮、点格子都能用）。
/// </summary>
public static class PanelHitTest
{
    public const double EdgeThickness = 14;

    public static PanelHitArea Hit(
        double x,
        double y,
        double width,
        double height,
        bool pinned,
        double captionHeight = 52,
        double reservedRightWidth = 148)
    {
        if (width <= 0 || height <= 0)
        {
            return PanelHitArea.None;
        }

        var nearLeft = x <= EdgeThickness;
        var nearRight = x >= width - EdgeThickness;
        var nearTop = y <= EdgeThickness;
        var nearBottom = y >= height - EdgeThickness;

        if (nearTop && nearLeft)
        {
            return PanelHitArea.TopLeft;
        }

        if (nearTop && nearRight)
        {
            return PanelHitArea.TopRight;
        }

        if (nearBottom && nearLeft)
        {
            return PanelHitArea.BottomLeft;
        }

        if (nearBottom && nearRight)
        {
            return PanelHitArea.BottomRight;
        }

        if (nearLeft)
        {
            return PanelHitArea.Left;
        }

        if (nearRight)
        {
            return PanelHitArea.Right;
        }

        if (nearTop)
        {
            return PanelHitArea.Top;
        }

        if (nearBottom)
        {
            return PanelHitArea.Bottom;
        }

        var inCaption = !pinned
            && y >= 0 && y <= captionHeight
            && x >= 0 && x <= width - reservedRightWidth;

        return inCaption ? PanelHitArea.Caption : PanelHitArea.None;
    }

    /// <summary>能否拖动（顶住时不行）。</summary>
    public static bool CanDrag(bool pinned) => !pinned;
}
