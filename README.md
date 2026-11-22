# DeskFlow

常驻 Windows 桌面的**文件管家**：自动接住 QQ / 微信 / 钉钉收到和浏览器下载的文件，按类型分门别类归档；桌面上一个面板就能打开最近的文件、进入分类目录、启动常用程序。

![桌面面板](docs/screenshot-panel.png)

---

## 一、四个任务，现在做到哪一步

| 任务 | 现状 | 怎么用 |
| --- | --- | --- |
| **1. 监听 QQ / 微信 / 钉钉的文件并自动下载** | 已实现（目录监听 + 写完检测）。每个来源独立「自动下载」勾选：勾上 = 发现即归档；不勾 = 只进面板「待接收」，点一下才归档 | 面板顶部四个勾选框；设置 →「监听与自动下载」可加自定义监听目录 |
| **2. 自动归档 + 自动建类别 + 自定义类别** | 已实现。内置 6 类（办公文档/代码/图片/音视频/压缩包/安装包）+ 兜底「其他」，按扩展名自动建 <归档根>/<类别>/；设置里可新建自定义类别，同名会**并集**内置扩展名而不是覆盖丢弃 | 设置 →「分类规则」；归档位置在「归档位置」页 |
| **3. 在软件里打开文件** | 已实现。点图标 = 用系统默认程序打开；右键 = 打开 / 在资源管理器中显示 / 从面板移除 | 面板「最近归档」「待接收」「分类」 |
| **4. 常驻桌面，像截图那样** | 已实现。无边框半透明面板，图标取自系统真实图标；**拖边角可改宽高，列数/图标/字号跟着自适应**，尺寸与位置都记住 | 面板右上角：设置 / 批处理 / 收起 / 退出；边缘拖动 = 缩放 |

> 明确边界：**没有**去 hook QQ/微信/钉钉客户端进程、也没有逆协议。
> 取舍见 [ADR 0003](docs/adr/0003-file-interception.md)：目录监听对客户端升级免疫、不需要注入、不会被杀软按注入行为拦截。

### 面板可以拖大拖小（内容自适应）

抓住面板任意边或角拖动即可改尺寸，松开后立刻重排并存进配置：

| 宽度 | 列数 | 效果 |
| --- | --- | --- |
| 280–360 | 3 列 | 窄栏贴边，标题自动折两行，副标题按需隐藏 |
| 376（默认） | 4 列 | 与参考图一致 |
| 560 以上 | 6–8 列 | 变成横向看板，一屏扫完最近文件 |

![窄面板](docs/screenshot-panel-narrow.png)
![宽面板](docs/screenshot-panel-wide.png)

> 文字不会出现"半个字"：格子按当前宽度算好字数预算（中文按全角、ASCII 按 0.56）再截断加省略号，
> 换 DPI、换字体都不会错位；完整名字在鼠标悬停提示里。

---

## 二、快速开始

```
dist\DeskFlow.exe
```

1. 双击启动，桌面出现面板（可拖到任意位置，下次记住坐标）。
2. 顶部勾选要「自动下载」的来源；检测不到的来源置灰并显示「未检测到目录」。
3. 对方发来文件后：
   - 勾了自动下载 → 很快出现在「最近归档」，文件已按类别放进归档目录；
   - 没勾 → 出现在「待接收」（黄色标题区），点图标归档，右键可忽略。
4. 点「分类」里的文件夹直接打开对应归档目录。

归档根目录默认 `文档\DeskFlow\output\archive`，可在设置里改。

### 命令行（同一个 exe）

```powershell
.\dist\DeskFlow.exe --run "D:\待整理" --out "D:\报表"   # 批量盘点，出 report.csv / report.json
.\dist\DeskFlow.exe --help
```

退出码：0 成功 / 1 有失败条目 / 2 参数或运行错误，可挂计划任务或 CI。

---

## 三、数据放在哪

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\DeskFlow\settings.json` | 监听目录、自动下载开关、归档根、自定义类别、面板快捷方式、面板坐标 |
| `%LOCALAPPDATA%\DeskFlow\archive-index.json` | 归档索引（来源 / 类别 / 落盘路径 / 状态），面板四块列表都读它 |
| `%LOCALAPPDATA%\DeskFlow\logs\*.log` | 运行日志 |
| `文档\DeskFlow\output\archive\<类别>\` | 归档后的文件本体 |

便携/测试模式：设置环境变量 `DESKFLOW_HOME` 指到任意目录，配置与索引整体搬过去，不动真实用户配置。

---

## 四、架构

```
DeskFlow.App (WPF 面板 + 设置 + 批处理窗口 + CLI)
      ↓ 引用
DeskFlow.Infrastructure   Watch(目录探测/轮询监听) · Archive(归档器) · Index(JSON 索引)
                          · Steps/Sinks(批处理能力) · Settings · Services(Shell)
      ↓ 引用
DeskFlow.Core             Models · Categorization(类别目录) · Abstractions · Runtime(JobEngine)
```

两条主线：

1. **监听归档线**：`SourceDirectoryResolver` 探出 QQ/微信/钉钉/下载目录 → `IncomingWatcher` 每 2 秒轮询、连续两轮大小不变才算写完 → `PanelViewModel.HandleDetected` 判断该来源是否自动下载 → `FileArchiver` 按 `CategoryCatalog` 落盘 → `JsonArchiveIndex` 记账 → 面板刷新。
2. **批处理线**（原有能力，保留）：`JobEngine` = 输入源 → 步骤 × N → 输出目标 → `JobReport`，命令行与「批处理」窗口共用。

硬规则：归档器只搬文件不删原件（除非显式选「移动」）；类别解析永远有兜底「其他」；索引写坏只丢历史、不崩程序。

详见 [docs/architecture.md](docs/architecture.md) 与三份 ADR。

---

## 五、加东西要改哪里

| 想要的 | 动作 |
| --- | --- |
| 支持新的聊天工具 | `SourceDirectoryResolver.Discover()` 加一条候选路径 |
| 新的内置类别 | `DefaultCategories.Create()` 加一行（用户也能在设置里自建，不必改代码） |
| 归档后动作（上传网盘、发通知） | 在 `PanelViewModel.ArchiveAndRecordAsync` 后加一步，或写新的 `IOutputSink` |
| 新的批处理步骤 | 实现 `IPipelineStep`，在 `DefaultJobCatalog.Build()` 注册一行 |
| 面板多一块（如「今天收了多少」） | `PanelViewModel` 加集合，XAML 复制一段 `ItemsControl`（模板 `TileTemplate` 与列数自适应已复用） |
| 调整密度手感 | `PanelViewModel.ApplyPanelWidth`：改 `minTileWidth`（每格最小宽）、列数上下限、字号缩放区间 |

---

## 六、构建与自检

需要 .NET SDK 10（本机 10.0.401）。

```powershell
dotnet build DeskFlow.sln
dotnet run --project tests\DeskFlow.Checks      # 56 项零依赖自检
powershell -File build\pack.ps1                # 单文件 EXE → dist\（约 59 MB，自包含）
```

自检覆盖：类别解析与同名并集、归档落盘/判重/改名/移动、监听「写完才报」与去重、索引持久化、设置读写，以及原有批处理链路。

---

## 七、已知边界

- 监听依赖**客户端默认收文件目录**；若你把文件另存到了别处，请在设置里手动添加那个目录。
- 未安装对应客户端时，面板显示「未检测到目录」，勾选框置灰（不假装能监听）。
- 判重用「长度 + 首 1 MB SHA-256」，不是全文件哈希：几万文件量级够快；同名、同首块但尾部不同的极端情况会走改名 `(2)` 而不是判重。
- 面板按内容自适应高度，超过屏幕工作区后内部滚动；不抢焦点、默认不置顶。
- `dist\` 不进 Git（59 MB 二进制），发版走 Releases。

## 八、下一步候选

- [ ] 托盘图标 + 开机自启 + 全局热键唤起
- [ ] 归档后清理重复文件、按月份/来源建二级目录
- [ ] 分类内文件列表页、面板中键滚动
- [ ] 归档动作插件化（webhook / 网盘 / 备份盘）
