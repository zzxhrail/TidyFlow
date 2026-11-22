# ADR 0001：用 .NET 10 + WPF，出单文件 EXE

- 状态：已采纳
- 日期：2026-11-22
- 决策者：软件架构师

## 背景

需求是"一个 Windows 上的软件"，细节未定；已知约束：优先绿色免安装、双击即用、目标机器不一定有开发环境。
候选：C#/.NET + WPF、Python + PySide6 + PyInstaller、Electron、C++/Qt。

## 决策

**C# / .NET 10 / WPF，`PublishSingleFile` 自包含发布，win-x64。**

## 理由

1. 本机已有 .NET SDK 10.0.401 与 WindowsDesktop 运行时，零安装成本即可编译、调试、发布。
2. WPF 是 Windows 原生 UI，启动快（冷启 < 1s），无浏览器内核，内存占用低。
3. 自包含单文件把运行时打进 exe，目标机器不需要装任何东西；体积 59 MB 可接受（U 盘/内网分发都行）。
4. 需要 Excel、串口、注册表、托盘、计划任务时，.NET 生态是一等公民，不用跨语言桥接。
5. 同一个 exe 可以做 GUI + CLI 双入口，脚本化与人工操作共用一套引擎。

## 备选方案与放弃原因

| 方案 | 优点 | 放弃原因 |
| --- | --- | --- |
| Python + PySide6 + PyInstaller | 写起来最快，库多 | 单文件 100 MB+、启动 2-4s、PyInstaller 引导器被杀软误报率高 |
| Electron | 界面自由度最高 | 体积 150 MB+、内存 300 MB+，为一个工具类软件过重 |
| C++ / Qt | 体积与性能最优 | 开发效率低、Qt 部署与许可成本，团队维护门槛高 |
| .NET + WinForms | 更简单 | 高 DPI 与样式能力弱，长期演进不如 WPF |

## 影响

- 必须用 Windows 构建（WPF 不能跨平台编译）。
- 体积换便利：59 MB 自包含 vs 2 MB 框架依赖，默认选自包含，`pack.ps1 -FrameworkDependent` 可切换。
- 单文件首次启动有解压延迟（`%TEMP%\.net`），已用压缩开关权衡。
- 不引入第三方 NuGet 包（连测试框架都不用），保证离线可构建、供应链面积最小。
