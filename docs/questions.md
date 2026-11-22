# 需求问答卡（目标 / 输入 / 输出 / 交付）

这份卡片是给"还没定细节"的阶段用的：先回答四个维度，再把答案映射到改动面。
当前仓库已按 **默认列** 实现并跑通，所以不必等答案就能先看到东西。

## Q1 目标：这个软件到底替谁干什么？

| 选项 | 典型场景 | 落地形态 |
| --- | --- | --- |
| A 自动化重复操作 | 批量改名、批量转格式、每天整理下载目录 | 步骤组合 + 计划任务 |
| B 数据采集与整理 | 网页/接口/日志 → 一张表 | 换输入源为采集器，输出仍是报表 |
| C 带界面的桌面小工具 | 同事双击就用 | 现有 WPF 界面 |
| D 后台常驻/系统增强 | 托盘、热键、右键菜单、监控 | 加托盘与开机自启，引擎不变 |
| **默认（当前实现）** | A + C：目录盘点出报表 | 已可运行 |

## Q2 输入：数据从哪来？

| 选项 | 需要的适配器 | 工作量 |
| --- | --- | --- |
| 本地文件/文件夹 | `FileSystemInputSource`（已实现） | 0 |
| 手工输入/剪贴板/拖拽 | 界面加 `AllowDrop` + 一个 `IInputSource` | 半天 |
| 网页/HTTP API（可能带登录态） | `IInputSource` + `HttpClient` + 凭据存储（DPAPI） | 2-4 天，登录态是主要不确定性 |
| 数据库/其他程序导出 | `IInputSource` + ADO.NET/CSV 读取 | 1-2 天 |
| 串口/USB/扫码枪/摄像头 | `IInputSource` + `System.IO.Ports`/厂商 SDK | 3 天起，看设备文档 |

## Q3 输出：结果变成什么？

| 选项 | 需要的输出目标 | 工作量 |
| --- | --- | --- |
| 生成文件（CSV/JSON） | `ReportSink`（已实现） | 0 |
| 生成 Excel/Word/PDF | 新 `IOutputSink`（ClosedXML / OpenXML / QuestPDF） | 1-2 天，需引 NuGet |
| 界面里看结果 | 现有日志区 + 一个 `DataGrid` 绑定 `JobReport` | 半天 |
| 写数据库/调接口/发邮件 | 新 `IOutputSink` | 1 天 |
| 系统动作（托盘、打印、操作其他窗口） | `NotifyIcon` / `System.Drawing.Printing` / UIAutomation | 2 天起 |

## Q4 交付方式？

| 选项 | 做法 | 代价 |
| --- | --- | --- |
| 绿色单文件 EXE | `build/pack.ps1`（已实现，59 MB） | 体积大、首启解压 |
| 框架依赖薄壳 | `pack.ps1 -FrameworkDependent`（约 2 MB） | 目标机要有 .NET 10 桌面运行时 |
| 安装包 + 自动更新 | Inno Setup / MSIX + 版本检查 | 多一套发布流程 |
| 先跑原型 | 当前仓库就是原型 | 后续补测试与签名 |

## 答案 → 改动面 速查

- 改**目标**：只影响"注册哪些步骤/输入/输出"，引擎、界面骨架、打包方式全部不变。
- 改**输入**：新增一个 `IInputSource` + `DefaultJobCatalog` 一行。
- 改**输出**：新增一个 `IOutputSink` + `DefaultJobCatalog` 一行。
- 改**交付**：只动 `build/pack.ps1` 与项目属性，代码不动。
