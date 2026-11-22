# ADR 0004：面板窗口不用分层窗口（AllowsTransparency）

- 状态：已采纳
- 日期：2026-11-22

## 问题

v0.4 的面板用 `AllowsTransparency="True"` + `WindowStyle="None"` 做半透明与圆角。
实机反馈：**最小化后还原，面板内容整块消失**（标题栏还在，正文空白，透出桌面）。

## 根因

`AllowsTransparency=True` 会让 WPF 使用**分层窗口**（WS_EX_LAYERED + UpdateLayeredWindow 路径）。
在 DWM 合成下，窗口最小化时渲染表面会被释放，还原时 WPF 不一定重画整棵树 —— 表现就是"内容消失"。
这是 WPF 长期存在的已知缺陷，与我们的布局代码无关（用 PrintWindow 抓图看不出来，因为那会强制重绘；
只有真实屏幕截图才看得见）。

## 决策

改成**非分层窗口**：

1. `AllowsTransparency="False"`，`Background` 用纯色 `#181A1F`；
2. 用 `WindowChrome`（`GlassFrameThickness=-1`）拿系统窗口阴影与边框；
3. 圆角交给 Windows 11 的 DWM：`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)`，
   Win10 上自动退化为直角（可接受）；
4. 双保险：`OnStateChanged` 里还原时强制 `InvalidateVisual` / `UpdateLayout`，
   并且最小化期间不把 0 尺寸写进配置。

## 代价

- 失去图层级半透明：面板背景从 95% 不透明变成完全不透明。视觉差异极小（本来就不透桌面）。
- 依赖 Win11 的 DWM 圆角；Win10 上是直角。

## 验证

用**真实屏幕截图**（CopyFromScreen）而不是 PrintWindow 做回归：最小化 → 还原后截图仍能看到完整内容。
以后所有"渲染类"验证都要用屏幕截图，PrintWindow 会掩盖这一类 bug。
