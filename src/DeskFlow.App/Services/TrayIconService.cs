using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DeskFlow.App.ViewModels;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App.Services;

/// <summary>
/// 系统托盘：常驻图标 + 右键菜单（打开面板 / 设置 / 自动下载开关 / 重新启动 / 退出）。
/// 用 WinForms 的 NotifyIcon —— WPF 没有原生托盘 API，自己 P/Invoke Shell_NotifyIcon 维护量更大。
/// 所有回调都切回 WPF 的 Dispatcher，避免在 WinForms 消息泵里直接操作 WPF 窗口。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly PanelViewModel _viewModel;
    private readonly ArchiveSettings _settings;

    public TrayIconService(
        PanelViewModel viewModel,
        ArchiveSettings settings,
        Action showPanel,
        Action openSettings,
        Action openHistory,
        Action restart,
        Action exit)
    {
        _viewModel = viewModel;
        _settings = settings;

        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var openItem = new ToolStripMenuItem("打开面板", null, (_, _) => OnUi(showPanel)) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("归档记录 · 可撤销…", null, (_, _) => OnUi(openHistory)));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OnUi(openSettings)));
        menu.Items.Add(BuildAutoDownloadMenu());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("重新启动", null, (_, _) => OnUi(restart)));
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => OnUi(exit)));

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "DeskFlow 文件管家",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 双击托盘图标 = 打开面板（用户最常见的操作）
        _notifyIcon.DoubleClick += (_, _) => OnUi(showPanel);

        Diagnostics.Log("tray icon created");
    }

    /// <summary>四个来源的"自动下载"开关，跟面板顶部的勾选是同一份配置。</summary>
    private ToolStripMenuItem BuildAutoDownloadMenu()
    {
        var root = new ToolStripMenuItem("自动下载");
        foreach (var source in new[] { FileSource.Qq, FileSource.WeChat, FileSource.DingTalk, FileSource.Downloads })
        {
            var item = new ToolStripMenuItem(source.DisplayName())
            {
                CheckOnClick = true,
                Checked = _settings.IsAutoDownload(source),
            };

            var captured = source;
            item.CheckedChanged += (_, _) => OnUi(() => _viewModel.SetAutoDownload(captured, item.Checked));
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem("扫描一次（近 30 分钟）", null, (_, _) => OnUi(() => _viewModel.ImportRecent(TimeSpan.FromMinutes(30)))));
        return root;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // 托盘气泡在部分精简系统里会抛异常，不影响主流程。
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>运行时画一个 32×32 的品牌图标（蓝底白字 D），省掉一个 .ico 资源文件与打包步骤。</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(0x2F, 0x6B, 0xFF));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(background, path);

            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var size = g.MeasureString("D", font);
            g.DrawString("D", font, text, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 1);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}