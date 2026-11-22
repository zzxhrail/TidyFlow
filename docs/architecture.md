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
