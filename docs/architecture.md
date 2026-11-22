# 架构说明

## 1. 一句话

一个**单向管道**：`输入源 → (步骤 × N) → 输出目标`，中间用 `ProcessedItem` 承载数据，用 `JobContext` 承载环境，用 `JobReport` 交付结果。
引擎不认识任何具体业务类型，所有能力都从 `JobCatalog` 注入。

## 2. 分层与依赖规则

```
DeskFlow.App (WPF + CLI)          ← 只做编排与呈现
      ↓ 允许引用
DeskFlow.Infrastructure           ← 具体实现（文件/哈希/文本/报表/日志/配置）
      ↓ 允许引用
DeskFlow.Core                     ← 模型 + 抽象 + 引擎，不依赖任何上层，也不依赖 Windows
```

硬规则：

1. `Core` 里不出现 `System.Windows`、不出现 `MessageBox`、不出现具体文件格式代码。
2. 步骤（`IPipelineStep`）**不改磁盘**，只写 `ProcessedItem.Fields`；落盘统一走 `IOutputSink`。
   这样任何一步失败都不会留下半成品，管道可以重放。
3. 输出目标拿到的是**整批**结果，方便做表头、汇总、分批上传。
4. 组件之间只通过 `JobCatalog` 的字符串名字通信，界面与命令行看到的能力集合自动一致。

## 3. 数据流

```
JobDefinition + JobContext
        │
        ├─ IInputSource.CollectAsync      → InputItem[]（相对路径已排序，结果可复现）
        │
        ├─ foreach item:  foreach step:   → ProcessedItem（失败则标记并跳出该条的后续步骤）
        │
        ├─ foreach sink:  WriteAsync      → Artifact[]（csv/json/...）
        │
        └─ JobReport                       → 界面 / 命令行 / 计划任务退出码
```

进度是 `IJobObserver.ReportProgress(JobProgress)`，日志是 `IJobObserver.Log(level, msg)`；
WPF 用 `DelegateObserver` 切回 UI 线程，命令行用 `TextWriterObserver`，文件日志再叠一层 `CompositeObserver`。

## 4. 关键类型

| 类型 | 职责 | 稳定性 |
| --- | --- | --- |
| `JobEngine` | 唯一主流程：收集 → 处理 → 写出 → 报告；含取消与逐条容错 | 稳定，尽量不要改 |
| `JobCatalog` | 名字 → 实现的注册表（组合根） | 稳定 |
| `JobContext` | 运行期环境：RunId、输入根、输出目录、观察者、配置、是否递归 | 可加字段 |
| `JobDefinition` | 任务的**声明**（可序列化进配置/命令行） | 可加字段 |
| `ProcessedItem` | 单条数据的可变载体（Fields/Notes/Failed/Error） | 稳定 |
| `JobReport` | 结果单一事实来源 | 可加字段 |
| `IInputSource` / `IPipelineStep` / `IOutputSink` | 三个扩展点 | 稳定 |

> 路径归属：**运行时以 `JobContext` 为准**，`JobDefinition` 里的路径只用于持久化与界面回显，
> 两者不一致时引擎会打一条警告而不是悄悄挑一个执行。

## 5. 线程与取消

- 引擎是纯异步、无 `Thread`、无 `Task.Run` 内部嵌套；UI 层用一次 `Task.Run` 把整批任务挪出 UI 线程。
- 每处理一条检查一次 `CancellationToken`；取消后已经写出的产物保留（不做事后删除，避免误删）。
- 观察者异常被 `CompositeObserver` 吞掉：日志坏了不能拖垮任务。

## 6. 运行形态

- 单文件自包含 EXE：`PublishSingleFile + IncludeNativeLibrariesForSelfExtract + EnableCompressionInSingleFile`，约 59 MB，首启解压到 `%TEMP%\.net`，二次启动 1 秒内。
- 同一个 exe 双入口：无参数 → 窗口；带 `--run` → 控制台批处理（`AttachConsole` 附加父进程控制台）。
- 不做插件加载（`Assembly.LoadFrom` + 版本地狱），扩展点用源码级注册，够用且可控。

## 7. 已知风险与对策

| 风险 | 现状 / 对策 |
| --- | --- |
| 杀软误报（单文件自解压） | 优先内部签名分发；必要时改用 `-FrameworkDependent` 薄壳 + 官方运行时 |
| Excel 处理依赖 Office | 当前输出 CSV（Excel 原生可开），不引入 COM，不需要装 Office |
| 登录态抓取失效 | 未涉及；做采集时把凭据放 `settings.json` 之外的 DPAPI 存储，别硬编码 |
| 长任务 UI 卡死 | 整批在后台线程，进度经 Dispatcher 回传；列表上限 2000 行防内存涨 |
| 中文/空格路径 | 全程 `Path` API + 参数加引号；CSV 用 UTF-8 BOM |
| 权限 | 不要求管理员；输出目录默认在"文档"，不碰 Program Files |
| 卸载残留 | 只写 `%LOCALAPPDATA%\DeskFlow`，删除即干净 |

---

# 附：桌面面板与监听归档（v0.3 新增）

## 1. 新增分层

```
Core
  Models            FileSource / CategoryDefinition / FileRecord / IncomingFile / WatchTarget / ArchiveOptions
  Categorization    DefaultCategories(6 类 + 兜底) / CategoryCatalog(解析 + 同名并集 + 用户类别)
Infrastructure
  Watch             SourceDirectoryResolver(探测 QQ/微信/钉钉/下载目录) / IncomingWatcher(轮询 + 写完检测)
  Archive           FileArchiver(按类别落盘 / 判重 / 改名 (2) / 复制或移动)
  Index             JsonArchiveIndex(archive-index.json，原子写)
  Settings          ArchiveSettings(自动下载开关 / 归档根 / 自定义类别 / 快捷方式 / 面板坐标)
  Services          ShellLauncher(打开 / 定位)
App
  DesktopPanelWindow   桌面面板（无边框、4 列图标、可拖动、收起、限高滚动）
  SettingsWindow       来源与自动下载 / 归档位置 / 分类规则 / 快捷方式
  PanelViewModel       监听事件 → 自动归档或待接收 → 刷新四块列表
  Services             ShellIconProvider(SHGetFileInfo 取系统真实图标) / ShortcutSeeder
```

## 2. 一次"收到文件"的完整时序

```
IncomingWatcher.ScanOnce()       每 2s；文件大小连续两轮相同才上报
  → FileDetected 事件
  → PanelViewModel.HandleDetected
      → CategoryCatalog.Resolve(扩展名)
      → ArchiveSettings.IsAutoDownload(source)?
          是 → FileArchiver.ArchiveAsync → JsonArchiveIndex.Upsert → BuildRecent/BuildCategories
          否 → 记录 Status=Pending → 落到面板"待接收"
  → 面板刷新（全部集合只在 Dispatcher 线程改）
```

## 3. 不变量

1. 归档只搬不删（除非 `MoveInsteadOfCopy`），原件路径始终留在记录里（`SourcePath`）。
2. 类别永远有兜底：解析不到 → 「其他」，不会出现无类别落盘。
3. 同名判重是"长度 + 首 1MB 哈希"，判非同则改名 `name (2).ext`，绝不覆盖已有文件。
4. 索引写失败只影响历史展示，不影响归档本身（先落盘后记账）。
5. 面板中所有跨线程更新经 `Dispatcher.BeginInvoke`，不在后台线程碰 UI 集合。

## 4. 面板窗口与命中（v0.4）

```
DesktopPanelWindow (WndProc hook, WM_NCHITTEST)
  → DeskFlow.Core.Shell.PanelHitTest.Hit(x, y, w, h, pinned)
      edge 14px        → 四边四角，HTLEFT/HTBOTTOMRIGHT...
      caption 区域      → HTCAPTION（原生拖动），顶住时降级为 HTCLIENT
      其余              → HTCLIENT（按钮/格子正常点击）
```

- 命中计算放在 Core 而不是窗口里：纯函数、可单测（9 个用例），窗口只做 HT 码映射。
- 拖动用原生 HTCAPTION 而不是 `DragMove()`：贴边吸附、高 DPI 不抖、不吞点击。
- 顶住（`panel.pinned`）= 关掉 caption 判定，只保留边角缩放；状态持久化，重启保持。
