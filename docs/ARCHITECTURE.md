# BatchRename 架构说明

> 配套文档：[SPEC.md](SPEC.md) 定义**要做什么**（契约与验收），本文说明**为什么这样做**与**怎么排障**。
> 最后更新：2026-11-02（补齐构建/测试/打包、里程碑状态、踩坑清单、PowerRename 对照。此前一轮代码评审的发现已并入本文 §10 与 SPEC 各版修订记录）

## 1. 设计约束

四条硬约束决定了后面所有结构选择：

| 约束 | 含义 | 直接后果 |
|---|---|---|
| **不能以管理员常驻** | 用户不该为一个低频工具装服务或开机自启 | 无常驻进程；右键动作触发才拉起 exe，关窗即退 |
| **不能注入 explorer** | DLL 一旦注入，崩溃会连带资源管理器重启 | 用 `SurrogateServer`，DLL 跑在 `dllhost.exe` 里 |
| **必须免签可装** | 个人自用，不买证书、不做签名流程 | 稀疏包 + 开发者模式；`Add-AppxPackage -Register` |
| **菜单路径零 IO** | 右键菜单是高频交互，任何慢盘/杀软扫描都会被感知为卡顿 | `GetState` 只读属性位；路径解析靠 `GetModuleFileNameW` 推导，不查注册表 |

## 2. 进程与线程拓扑

```
┌─────────────────┐  右键          ┌──────────────────────┐
│  explorer.exe   │───────────────▶│ dllhost.exe          │
│  (Shell, STA)   │  CoCreateInstance│ (COM Surrogate, STA) │
└─────────────────┘                │  ├─ BatchRename.Shell.dll │
                                   │  └─ BatchRenameCommand    │
                                   └──────────┬───────────┘
                                              │ CreateProcess + 匿名管道
                                              │ (stdin 传路径)
                                              ▼
                                   ┌──────────────────────┐
                                   │ BatchRename.App.exe  │
                                   │ (WinUI 3, 无常驻)     │
                                   │  ├─ ShellPayload     │
                                   │  ├─ RenameEngine     │
                                   │  └─ MainWindow       │
                                   └──────────────────────┘
```

三个关键点：

1. **Shell 宿主不加载我们的代码**。`com:SurrogateServer` 让 COM 把 DLL 加载到 `dllhost.exe`，explorer 只通过 RPC 调用。DLL 崩了最多 dllhost 重启，不会出现"资源管理器闪一下"。
2. **dllhost 会缓存 DLL 约 5 分钟**。改完 DLL 想立刻生效，要么等，要么重启 explorer（`taskkill /f /im dllhost.exe` 会连带杀掉其他 COM 代理，慎用）。
3. **App 是独立进程且无常驻**。窗口关闭即进程退出，没有托盘图标、没有后台轮询。

## 3. 启动时序

```
用户右键
  └─ GetState(psia, fOkToBeSlow)
       只对前 8 项（slow 路径则全量）调 GetAttributes(SFGAO_FILESYSTEM)
       命中 → ECS_ENABLED；全不命中 → ECS_HIDDEN
       ★ 绝不调 GetDisplayName，绝不读磁盘

用户点击菜单项
  └─ GetTitle → "批量重命名 (N)"        [纯内存，SHStrDupW]
  └─ Invoke(psia)
       ├─ CollectPaths：枚举 → 过滤 SFGAO_FILESYSTEM → GetDisplayName(SIGDN_FILESYSPATH)
       ├─ LaunchUI：CreatePipe → CreateProcess(EXTENDED_STARTUPINFO_PRESENT)
       │     └─ detached 线程写管道 → 关写端(EOF) → DecrementObjectCount
       └─ 失败 → AppendLog + 临时线程 MessageBox（绝不在 STA 线程弹）

App 启动
  └─ ShellPayload.ReadFromStdIn()   ← 先读，再建窗口，避免闪空窗
  └─ MainWindow → RenameEngine.Run() → ConflictResolver（互撞/循环/磁盘/编号）
```

弹框必须开在临时线程上：`MessageBox` 阻塞 dllhost 的 STA 线程后，下一次右键的 `GetState` 会排队，现象是"菜单转圈"。

## 4. 进程间数据契约

单向、无协议头、UTF-16LE：

```
[路径1 的 wchar_t 字节][L'\0'][路径2 的 wchar_t 字节][L'\0'] ... 然后关闭写端
```

- 接收端用 `Encoding.Unicode` 读、按 `'\0'` 切分（**不是**按换行）。
- 每条路径独立 `WriteFile`，写端关闭即 EOF，子进程 `ReadToEnd` 返回。
- 句柄白名单 `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` 里只放 `readEnd`，且**它必须保留 `HANDLE_FLAG_INHERIT`**；要清继承标志的是父进程持有的 `writeEnd`。这一条搞反会让 `CreateProcess` 直接返回 87。

## 5. 模块职责与依赖

```
MainWindow ──▶ RenameEngine ──▶ (NameSplitter, RenameRule)
     │
     └───────▶ FileOperationRunner ──▶ UndoJournal

ShellPayload ◀── App（只在 App.OnLaunched 里被调用一次）
Shell 扩展（C++）──▶ Launcher / Log（互不依赖）
```

**硬规则：`Core/` 与 `Models/` 不引用任何 WinUI 类型。**

这不是洁癖，是为了让 `BatchRename.ShellTest`（控制台）能**直接链接源码编译**，把引擎逻辑跑成断言，完全不启动 UI。当前 124 项断言就是这么跑起来的——改规则逻辑不需要开窗口、不需要注册右键菜单。

| 模块 | 职责 | 关键实现约束 |
|---|---|---|
| `Core/NameSplitter` | 主名/扩展名切分 | 只切最后一层；目录永不切；`i==0` 视为 dotfile |
| `Core/RenameRule` | 文本/正则替换 + 四种大小写 | 大小写一律 `Invariant`；正则优先 `NonBacktracking`，回退带 250ms 超时 |
| `Core/RenameEngine` | 编排 + 九条校验 + 名字计算 | 替换**先**、大小写**后**；作用域「两者」绝不拼串；**冲突判定已移到 `ConflictResolver`**（v1.6） |
| `Core/ConflictResolver` | 批内互撞 / 批内循环 / 磁盘已存在 / 冲突自动编号 | 只读写 `State`/`Message`/`NewName`，不碰规则、不写磁盘；顺序固定：互撞 → 循环 → 磁盘 → 编号 |
| `Core/FileOperationRunner` | STA + IFileOperation 执行 | 所有项统一进一个队列（**无 CaseOnly 两步**）；入队前按依赖排序；正向加 `FOFX_ADDUNDORECORD`、反向不加；`SetOwnerWindow`；逐项结果来自 `RenameProgressSink`，**不再事后扫目录** |
| `Core/RenameProgressSink` | `IFileOperationProgressSink` 的托管实现 | 实现 CsWin32 的 `[ComImport]` 接口（CLR 自动建 CCW）；`PostRenameItem` 只记录不抛，取消只在 `PreRenameItem` 抛 |
| `Core/UndoJournal` | 单文件事务日志 + 失效判定 + 反向执行 | 只有 `last-batch.json`（覆盖写）；先落盘再动手，`Flush(true)` 强制刷盘；`RollbackPendingAsync` 专管崩溃恢复 |
| `Models/RenameItem` | 单项数据 | `INotifyPropertyChanged`，`State` 驱动 UI 配色；`IsRenamed` **只是显示标记，不是规则计算的闸门**（改过的项照常参与下一轮计算） |
| `App/ShellPayload` | stdin 解析 | `Encoding.Unicode` + `'\0'` 切分 |
| `App/MainWindow` | 预览 UI | **两层防抖**（150ms 纯内存 + 400ms 磁盘）；执行/撤销后保留用户已输入的规则；`Esc` 有未执行改动时二次确认；「全选」三态与列表同步；DataTemplate 用 `{Binding}`（`Window` 根不支持 `x:Bind`）。**实测：7 处绑定全换成静态文本只省 2MB**，所以不必为用 `x:Bind` 去抽 `UserControl`——那是 CPU 优化不是内存优化（§7.8.7 v1.7） |
| `Shell/Launcher` | 定位 exe + 管道启动 | 路径靠 `GetModuleFileNameW` 推导，零 IO |
| `App/Services/AppLog` | App 侧日志（1MB 循环） | 只写异常与启动信息；日志失败绝不影响主流程。位置 `%LOCALAPPDATA%\BatchRename\logs\app.log` |
| `Shell/Log` | 双写日志 | 1MB 循环；**只能在 Invoke 路径调用** |

## 5.1 执行与撤销数据流

```
点击「执行」
  ├─ _debounce.Stop(); RefreshPreview()        [先吃掉防抖窗口里的最后一次输入，且这次是带磁盘检查的完整校验]
  ├─ UndoJournal.Create(...) → Save()          [write-ahead: state=Pending, Flush(true)]
  ├─ FileOperationRunner.RunAsync(items, RenameRunOptions(hwnd, AddUndoRecord: true))
  │    ├─ 新开 STA 线程 + CoInitializeEx(APARTMENTTHREADED)
  │    ├─ OrderByDependency：目标是别人的源名时，别人先动（链式改名不覆盖）
  │    ├─ 所有项（含仅大小写）→ 同一个 IFileOperation 队列，每项挂 RenameProgressSink
  │    │    SetOperationFlags(NOCONFIRMATION | NOERRORUI | SILENT |
  │    │                    FOFX_SHOWELEVATIONPROMPT | FOFX_ADDUNDORECORD)
  │    │    SetOwnerWindow(hwnd) → RenameItem × N → PerformOperations
  │    └─ 逐项结果取自 sink 的 PostRenameItem（HRESULT）；CaseOnly 额外用 ExistsExact 复核
  │       sink 同时按 ~100 次/批 上报进度 → UI 画进度条；「取消」→ PreRenameItem 抛 → Shell 停后续
  └─ 回写 state=Applied → Save()（覆盖 last-batch.json）

点击「撤销」/ Ctrl+Z（软件内）
  ├─ IsUndoable：已撤销过 / Pending → 拒绝
  ├─ 逐项失效判定：to 不存在（from 在 → 记「已是原名」）/ from 被真实占用（都用 ExistsExact）
  ├─ 反向构造 RenameItem → 复用同一个 Runner，但 AddUndoRecord: false
  └─ 全部成功 → state=Undone；部分失败 → 保留 Applied，可重试

启动时
  ├─ LoadLast() is Applied → 点亮「撤销」按钮
  └─ FindPending() 非空 → 弹「上次操作可能未完成」→ RollbackPendingAsync()

资源管理器里 Ctrl+Z（与上面完全独立）
  └─ Shell 撤销栈，一次一批，进程死活无关；受 explorer 重启影响
```

三个硬约束：

1. **write-ahead**：journal 必须在动手**之前**落盘并 `Flush(true)`。攒到执行完再写，崩溃后就无法知道改过哪些文件。
2. **`FOFX_ADDUNDORECORD` 是系统级撤销的唯一途径**：它把整批登记进 Shell 撤销栈，所以关掉本程序后资源管理器 `Ctrl+Z` 依然有效。反向执行**必须**关掉它，否则会多出一条"撤销的撤销"记录。
3. **不要在清单之外假设 Shell 处理不了大小写**：实测 `IFileOperation::RenameItem` 对文件和目录的仅大小写改名都直接成功，v1.2 的"两步中转"是误判（真正的病因是同一队列引用尚未生成的临时名）。

## 5.2 单实例激活

```
首实例
  ├─ new Mutex(true, "Local\BatchRename.SingleInstance", out createdNew) → true
  ├─ 写 %LOCALAPPDATA%\BatchRename\active.pid
  └─ 后台线程循环 WaitForConnectionAsync("BatchRename.Activate.{pid}")

第二实例
  ├─ createdNew == false
  ├─ 读 active.pid → 连命名管道（1500ms 超时，超时则降级为新开窗口）
  ├─ AllowSetForegroundWindow(首实例PID)   ← 缺这步，首实例的 SetForegroundWindow 会被前台锁静默拒绝
  ├─ 写 int32 count + count × string（BinaryWriter，长度前缀 + UTF-8）
  └─ Environment.Exit(0)

首实例收到
  └─ DispatcherQueue.TryEnqueue → 替换列表 → Activate() + SetForegroundWindow(hwnd)
```

无常驻形态下，单实例**只解决"连点两次出两个窗口"，不提供热复用**——窗口一关进程就没了。

## 5.3 图标的三条独立链路

| 位置 | 来源 | 换图后怎么生效 |
|---|---|---|
| 右键菜单 | `GetIcon` 返回 `Assets\menu.ico` | 替换文件即可，不碰 exe、不重新发布 |
| exe / 窗口 | `app.ico`（csproj 的 `ApplicationIcon`） | 需重新 build，且受 Shell 图标缓存影响，可能要重启 explorer |
| 任务栏 / 开始菜单 | 包的 `VisualElements` logo | 需 `Add-AppxPackage -Register` 重新注册（注册前要停净 App 与 dllhost，否则 `0x80073D02`） |

任务栏这条还有个坑：只给默认 logo 时，系统会垫一块**强调色底板**（plating），圆角外露出 accent color。

> 🔧 **v1.9 实测更正：补 `altform-unplated` 变体没用**
>
> 生成 `*.targetsize-44_altform-unplated.png` / `altform-lightunplated` 全套并重新注册后，任务栏**仍然带底板**——系统不会因为变体存在就改用无底板渲染。
>
> **真正有效的解法是设窗口图标**：
>
> ```csharp
> var ico = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Assets", "menu.ico"));
> AppWindow.SetIcon(ico);
> ```
>
> 任务栏按钮和 Alt+Tab 优先用**窗口图标**，只有窗口从未设过图标时才回落到包 logo。走了后者才会 plating。
>
> ⚠ **不要传 exe 路径**：`SetIcon(Environment.ProcessPath)` 会让 Shell 在 exe 的图标资源里自己挑，实测挑到了 .NET 的默认文档图标（因为 exe 里除了嵌入的 `app.ico` 还有运行时资源）。必须传独立的 ico 文件，没有索引歧义。

## 6. 关键决策

**为什么用稀疏包而不是传统注册表 COM 注册？**
传统方式要写 `HKCR\CLSID\{...}`（需管理员）、还要给 explorer 加 `ContextMenuHandlers` 键。稀疏包把 COM 注册和菜单注册都放进一个可撤销的包，`Add-AppxPackage -Register` / `Remove-AppxPackage` 就是安装/卸载，不污染注册表，也不需要管理员。

**为什么 C++ 用 WRL 不用 ATL？**
只需实现 `IExplorerCommand` + `IObjectWithSite` 两个接口，WRL 的 `RuntimeClass` 模板够用，省掉 ATL 依赖（少一个 SDK 组件要求）。

**为什么 `GetState` 用 `ECS_HIDDEN` 而不是 `ECS_DISABLED`？**
灰掉的菜单项会让用户怀疑程序坏了。在"此电脑""网络""回收站"上直接不显示更干净。

**为什么引擎要做"仅大小写变化"单独标记？**
v1.2 的理由（"`IFileOperation` 直接改会被判目标已存在，必须两步中转"）**已被实测否定**：`IFileOperation::RenameItem` 与 `File.Move` 都能直接完成仅大小写改名（文件与目录皆可）。现在 `CaseOnly` 标记只剩两个用途：① 判定"这是合法改名"而不是"未改变"；② 撤销时区分"目标名与源名只差大小写"，避免 `ExistsExact` 误判。**执行器不再对它做任何特殊处理。**

**为什么拦"循环改名"而不是用两阶段临时名？**
`a→b` + `b→a` 这一类环无法靠排序解决，工程上有两条路：整批先改临时名再改目标名（两阶段），或预览阶段直接拦掉。选后者：① 环在真实使用里几乎不存在（一条全局规则造不出互换）；② 两阶段会让"一批"变成两次 `PerformOperations`、两条撤销记录、失败回滚还要回滚两步；③ 拦掉是**可解释**的——用户看到红字知道为什么，而不是文件名的中间态在闪。链（`a→b` + `b→c`）不是环，由执行器的依赖排序保证安全。

**为什么行内勾选不用 `CheckBox`？**
5000 项实测把结论钉死了：空窗 110MB；只显示 10 项 133MB；显示 200 / 1000 / 5000 项分别是 179.6 / 177 / 180MB。**项数翻 25 倍内存几乎不变**，说明不存在"每项一个容器"的成本（虚拟化本身是好的）；把行内 `CheckBox` 换成 `Border` + `FontIcon` 后直接降到 **137MB**（再按官方模板像素级复刻成双层 Border + 勾字形，视觉 1:1，代价 139MB）。所以那 43MB 不是"行太多"，而是每个已实现行的模板化控件在摊模板、状态动画与合成资源。行模板的规则因此收紧成：只允许叶子元素。
状态列颜色同理不能自己"取"：WinUI 3 的主题字典按启动主题固化，代码侧查 `Light` 拿到的是深色值。最终方案是**颜色在 XAML 里用 `{ThemeResource}` 声明成画刷**（`Renamed` 用 `StaticResource` 别名指向 `AccentTextFillColorPrimaryBrush`，因为对应的 Color 键不存在），转换器只负责按状态挑对象、不做缓存——主题切换时 XAML 换的是同一实例的 `Color`，已绑定行自动更新。

交互方面踩了两个坑：`ListViewItem` 会吃掉子元素的 `Tapped`；`ItemClick` 又不给点击坐标。最终做法是 `ItemClick` 负责命中、`PointerPressed`（`handledEventsToo`）记下 x，只有落在左侧 56px 勾选列才切换，点其它位置保持原来的"选中"。

**为什么 journal 只留一批、不再有 7 天 / 20 条历史？**
撤销的"刚刚那一下"由 Shell 撤销栈兜住（`FOFX_ADDUNDORECORD`，与进程死活无关）；journal 剩下的不可替代职能只有两个——write-ahead 崩溃标记、软件内撤销最近一批。7 天 / 20 批既没有 UI 入口，也没有需求：实测 20 个文件 85KB 里有 10 个 `Applied` 记录永远撤不到。降级后 `UndoJournal` 只剩一个覆盖写的 `last-batch.json`，状态机从三个分支收敛到两个。

**为什么正则优先 `NonBacktracking`？**
灾难性回溯（`(a+)+$` 这类）会让 UI 卡死几十秒。`NonBacktracking` 引擎天然免疫，只在用到 lookaround/backreference 时才回退到带 250ms 超时的引擎。

### 6.1 与 PowerRename 的对照

PowerToys 的 PowerRename 是同功能参照物：同样是 WinUI 3 UI + `IFileOperation` 执行。源码级对比后值得记下的差异：

| 维度 | PowerRename | 本项目 |
|---|---|---|
| 菜单注册 | 双注册（传统注册表 + 稀疏包） | 稀疏包 + SendTo 兜底 |
| 进程拓扑 | Shell 扩展 + UI + 核心库 | dllhost 壳 + WinUI 3 App（更薄） |
| 传参 | COM 接口 | stdin UTF-16 + `\0` 分隔 |
| 执行顺序 | 按目录深度倒序 | `OrderByDependency` 拓扑排序 |
| 撤销 | Shell 撤销栈 | 双通路：自建 journal + Shell 撤销栈 |
| 冲突 | 交给 Shell 自动编号（`FOF_RENAMEONCOLLISION`） | 预览红字阻止 + 可选自动编号 |
| 逐项结果 | 不校验，失败交给 Shell UI | `PostRenameItem` 取逐项 HRESULT 并翻成中文 |

四条结论：

1. **跨进程 `Ctrl+Z` 从一开始就能做**——`FOFX_ADDUNDORECORD` 正是为此存在。v1.0 判断"无法撤销"是错的
2. **它不做逐项校验**，把失败留给 Shell 的进度窗口；本项目自己拿 `PostRenameItem` 的 HRESULT，换来准确的中文错误提示
3. **它的冲突策略是"让 Shell 自动编号"**，本项目默认红字阻止（更保守），编号作为可选
4. **它没有 CaseOnly 特殊处理**——因为它从不误用 `File.Move`

## 7. 构建与产物

### 7.1 环境要求

| 组件 | 版本 | 说明 |
|---|---|---|
| Windows | 11 26H2 / Build 26300+ | 清单的 `MaxVersionTested` 按此填 |
| Visual Studio | 2026 | 需勾选「.NET 桌面开发」+ **「使用 C++ 的桌面开发」**（缺 MSVC 工具集就没有 `include` 目录） |
| Windows SDK | 10.0.26100.0 | 提供 `makeappx` / `signtool` / 头文件 |
| .NET | 10 SDK | `dotnet --list-sdks` 应能看到 10.x |
| Windows App SDK | 2.6.4 | 走 NuGet，无需单独安装 |
| **开发者模式** | **必须开启** | 稀疏包免签注册的前置条件，未开报 `0x80073CFF` |

```powershell
# 管理员 PowerShell 开启开发者模式
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
  -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -Type DWord
```

### 7.2 产物

```
源码                           产物
─────────────────────────────────────────────────────────
src/BatchRename.Shell/   ──▶   bin/publish/BatchRename.Shell.dll
src/BatchRename.App/     ──▶   bin/publish/BatchRename.App/  (整个目录)
packaging/Assets/        ──▶   bin/publish/Assets/
```

`bin/publish` 是**外部位置根**，`Add-AppxPackage -Register ... -ExternalLocation` 指向它。清单里所有 `Path` 都相对这个根，**不是**相对清单文件——写成 `..\bin\publish\...` 会直接失败。

> 🔧 **`packaging/Assets/` → `bin\publish\Assets/` 这一步是 `tools/Install-Sparse.ps1` 在注册前拷的**（`Copy-Item -Recurse -Force`，日志打 `[ok] Assets synced -> … (N files)`）。
>
> 不拷的后果：右键菜单的 `GetIcon` 返回的是 `ModuleDir()\Assets\menu.ico`，而 `ModuleDir()` 是 **DLL 所在目录**（即 `bin\publish`），不是 `packaging`。以前脚本只 `Add-AppxPackage -Register`，从不拷 Assets，于是**换掉 `packaging/Assets` 里的图标后菜单永远是旧的**，重启 explorer 也没用——这是查了很久才定位的坑。
>
> 换完图标还要注意进程内缓存：菜单的 COM 跑在 `dllhost.exe` 里，只重启 explorer 不会换掉它，必须 `Get-Process dllhost | Stop-Process -Force` 之后再重启 explorer。

三个产物必须同时到位，缺一个就出问题：

| 缺什么 | 现象 |
|---|---|
| DLL 导出表为空 | `CoCreateInstance` 报 `0x80040154`，事件日志无任何记录 |
| `resources.pri` | App 启动即 `0xC000027B`（崩在 `Microsoft.UI.Xaml.dll`），表现为"加载一会儿什么都没有" |
| `bin\publish\BatchRename.App\` 目录 | 点菜单弹"启动失败"，`shell.log` 记 `launch failed` |

**受限环境下的手工编译**（`MSBuild.exe` 被安全策略拦截时）：

```powershell
$msvc = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$sdk  = 'C:\Program Files (x86)\Windows Kits\10'; $v = '10.0.26100.0'
$env:PATH    = "$msvc\bin\Hostx64\x64;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdk\Include\$v\ucrt;$sdk\Include\$v\um;$sdk\Include\$v\shared;$sdk\Include\$v\winrt"
$env:LIB     = "$msvc\lib\x64;$sdk\Lib\$v\ucrt\x64;$sdk\Lib\$v\um\x64"

cd src\BatchRename.Shell
cl /nologo /c /std:c++20 /EHsc /MD /utf-8 /DNDEBUG /D_WINDOWS /D_USRDLL /Yc"pch.h" /Fp"..\..\obj\BatchRename.Shell\Release\BatchRename.pch" pch.cpp
cl /nologo /c /std:c++20 /EHsc /MD /utf-8 /DNDEBUG /D_WINDOWS /D_USRDLL /Yu"pch.h" /Fp"..\..\obj\BatchRename.Shell\Release\BatchRename.pch" dllmain.cpp ExplorerCommand.cpp Launcher.cpp Log.cpp
link /nologo /DLL /MACHINE:X64 /DEF:BatchRename.Shell.def /OUT:"..\..\bin\publish\BatchRename.Shell.dll" *.obj runtimeobject.lib shlwapi.lib shell32.lib ole32.lib oleaut32.lib user32.lib
```

注意 `user32.lib` 不能省——`MessageBoxW` 在里面。

### 7.3 测试

124 项断言，统一实现在 `tests/BatchRename.Core.Tests/CoreSuite.cs`，两个入口共用同一份代码（避免各写一遍后漂移）：

```powershell
# xUnit（CI 用；一条 Fact 覆盖全部，同时守卫断言条数 == 124）
dotnet test tests\BatchRename.Core.Tests\BatchRename.Core.Tests.csproj -c Release

# 控制台（人肉看逐条输出）
dotnet build src\BatchRename.ShellTest\BatchRename.ShellTest.csproj -c Release
dotnet src\BatchRename.ShellTest\bin\Release\net10.0-windows10.0.26100.0\BatchRename.ShellTest.dll
```

覆盖：NameSplitter / 作用域语义 / 大小写 Invariant / 正则 / 九条校验 / 批内互撞 / 磁盘冲突 / 自动编号 / 性能预算 / 执行器与 journal（真实文件）/ Pending 回滚 / 文件与目录的仅大小写改名 / 执行中断回写 / 二次改名 / 链式排序 / 循环检测 / 逐项失败 / 两层防抖 / 执行期进度与取消。

安全边界：

- 第 10–16 组在 `%TEMP%\BatchRenameTest\` 下建临时文件并**真实改名**，用完清理
- journal 被指向 `%TEMP%\BatchRenameTestJournal\`，不会覆盖你正在用的 `last-batch.json`
- 测试调执行器时 `AddUndoRecord` 恒为 `false`，不会往资源管理器撤销栈里塞临时文件

### 7.4 打包

```powershell
.\tools\Package-Portable.ps1    # → dist\BatchRename-<日期>.zip（约 60MB）
.\tools\Build-Setup.ps1         # → dist\BatchRename-Setup-<日期>.exe（约 97MB）
```

**zip**：顶层直接是 `bin/ packaging/ tools/ 安装.cmd 安装说明.txt`，不带 `<日期>` 目录层。解压到任意目录后双击「安装.cmd」。

**exe**：`installer/BatchRenameSetup` 把该 zip 作为 `EmbeddedResource` 内嵌，运行时自解压到 `<所选目录>\BatchRename`，再调 `Install-Sparse.ps1` 完成注册。支持 `/S` 静默与 `--dir <父目录>`。

### 7.5 文档校验

```powershell
.\tools\Verify-Docs.ps1    # 全文镜像 + 断言条数一致性，失败返回 1
```

SPEC §6.2/§6.4 内嵌了 `AppxManifest.xml` 与 `Install-Sparse.ps1` 的**逐字全文**，这种镜像必然漂移（v1.5 就漂过：脚本 159 行、文档还停在 79 行）。脚本做三件事：

1. 从 SPEC 抽出这两个代码块，与真实文件逐行比对（忽略 BOM/CRLF）；
2. 读 `CoreSuiteTests.ExpectedChecks` 的断言条数，检查 README / ARCHITECTURE / SPEC 里出现的「N 项断言」都是同一个数；
3. 检查文档里不再引用已删除的 `REVIEW-*.md`。

CI 的 `build-and-test` 会在跑测试前先执行它——文档和代码不一致时构建直接红。

### 7.6 工程约定

三个根目录配置文件，各自只解决一件事：

| 文件 | 作用 | 注意 |
|---|---|---|
| `Directory.Build.props` | C# 公共编译选项（`Nullable` / `ImplicitUsings` / `LangVersion`）+ 版本号真源（`<Version>`）。安装器读程序集 `InformationalVersion` 写「应用和功能」条目 | `PropertyGroup` 带 `Condition="'$(MSBuildProjectExtension)' == '.csproj'"`——vcxproj 也 import 这个文件，不加条件会把 C# 属性塞进 C++ 工程 |
| `global.json` | 钉 SDK `10.0.100`，`rollForward: latestMinor`（本机与 CI 用的都是 10.0.4xx，允许同 major 内更高的功能带） | 写太死会让别人机器上 `dotnet build` 直接报「SDK 不匹配」 |
| `.editorconfig` | 换行/缩进与 `.gitattributes` 对齐（统一 CRLF）；`*.ps1` 声明 `charset = utf-8-bom` | PowerShell 5.1 按 BOM 判编码，无 BOM 的中文脚本会被按 GBK 解码、吃掉后续代码行 |

CI 在 `windows-latest` 上依次跑：`Verify-Docs.ps1` → `dotnet test` → 编 App → 编控制台壳 → **编原生 Shell（msbuild vcxproj）**。最后一步是 v1.13 补的：此前改 `dllmain.cpp` / `ExplorerCommand.cpp` 时前三步都绿，问题要等装到机器上才发现。

> 两个脚本都依赖 `bin\publish` 已由 `dotnet publish` 生成。
> csproj 里有 `EnsurePayload` 目标：缺 `payload.zip` 时 publish 会**直接报错**，而不是静默产出一个没有 payload 的 exe（踩过——体积从 97MB 掉到 36MB，看起来像"优化成功"）。

## 8. 排障地图

按现象直接跳转，别从头猜：

| 现象 | 先查什么 | 常见根因 |
|---|---|---|
| 菜单项**完全不出现** | `dumpbin /exports` 有没有三个导出；`Get-AppxPackage` 的 `Status` | 三处 CLSID 不一致 / 没重启 explorer / 导出表为空 |
| 点了弹"启动失败" | `shell.log` 里的错误码 | `0x57` → 句柄继承标志清错对象（见 §4） |
| 点了**没反应也没报错** | 事件查看器 Application 日志 | App 崩在 `Microsoft.UI.Xaml.dll`，多半是 `resources.pri` 缺失 |
| 右键**转圈** | `GetState` 是否调了 `GetDisplayName` 或读盘 | 菜单路径上做了 IO |
| 改完 DLL 不生效 | dllhost 是否还持有旧 DLL | COM 代理缓存约 5 分钟 |
| 仅大小写改名后，撤销报「所有项均已失效」 | 判断"源名是否被占用"用的是不是 `File.Exists` | CaseOnly 的 `from`/`to` 只差大小写，`File.Exists(from)` 恒为 true → 整批作废。必须用 `ExistsExact` |
| 改名把同名文件**覆盖**了 | 预览阶段有没有做磁盘冲突检测 | 缺 §8.8 的磁盘检测。执行带 `FOF_NOCONFIRMATION`，覆盖时连提示都没有 |
| 两个文件**互换名字**后内容串了 | 预览里两项是否标「批内循环改名」 | v1.5 前没有环检测：`a→b`+`b→a` 被"源名会让出"放行，执行时静默覆盖 |
| 链式改名（`a→b`、`b→c`）丢了中间那份 | 执行器有没有按依赖排序入队 | 错序时 `a→b` 会覆盖 `b`；v1.5 起 `OrderByDependency` 保证别人先动 |
| 改名失败只看到一句"改名后目标不存在" | 是不是 v1.5 之前的版本 | 旧实现靠事后扫目录推断结果；v1.5 起用 `PostRenameItem` 的 HRESULT，并翻译成中文（占用/权限/目标已存在…） |
| 资源管理器里 `Ctrl+Z` **没反应** | `SetOperationFlags` 里有没有 `FOFX_ADDUNDORECORD`；该项是不是走 `File.Move` | 旧版有意不加这个标志 → 操作从未进 Shell 撤销栈。整批必须走 `IFileOperation` |
| 文件夹名只改大小写**改不动** | 是否还在走 `File.Move` 两步 | `File.Move` 对目录抛 `FileNotFoundException`；应交给 `IFileOperation`（v1.5 已改） |
| 弹了「上次操作可能未完成」但回滚**失败** | 调的是 `UndoAsync` 还是 `RollbackPendingAsync` | `UndoAsync` 明确拒绝 `Pending`（v1.5 前的 P0-2） |
| 结果条一闪而过 | 底栏文案是否被预览统计覆盖 | `TextBox.TextChanged` 是异步派发的，清空输入框会触发预览刷新 |

**日志位置有个坑**：dllhost 在包容器身份下运行，`%LOCALAPPDATA%\BatchRename\logs\` 会被文件虚拟化重定向，真实路径是：

```
C:\Users\<user>\AppData\Local\Packages\BatchRename.Shell_<hash>\LocalCache\Local\BatchRename\logs\shell.log
```

去前者找不到日志不代表没写，先看后者。

App 进程**没有**容器身份，日志就在 `%LOCALAPPDATA%\BatchRename\logs\app.log`（1MB 循环，含启动信息与三个异常兜底钩子的记录）。执行中断时 `last-batch.json` 里还会带一个 `Error` 字段。

## 9. 里程碑状态与后续方向

| 阶段 | 内容 | 状态 |
|---|---|---|
| M1 | 右键菜单 + 拉起窗口 + 路径透传 | ✅ |
| M2 | 重命名引擎 + 实时预览 | ✅ 124 项断言 |
| M3 | 执行 + 撤销（`IFileOperation` + 单文件事务日志 + Shell 撤销栈） | ✅ |
| M4 | Mica / 自定义标题栏 / 键盘 / 单实例 | ✅ |
| M5 | 安装闭环 + SendTo 兜底入口 | ✅ |
| 分发 | zip + 单文件 exe（自解压 + 自包含运行时） | ✅ |

基线标签：`v0.1-M1M2` → `v0.2-M3` → `v0.3-M4` → `v0.4-M5`

### 后续可选方向

| 项 | 价值 | 成本 |
|---|---|---|
| `ItemsRepeater` 替代 `ListView` | 最多省 21MB（列表首屏的固定开销） | 需重写行选中与键盘交互 |
| 第二个 verb「撤销上次重命名」 | 少开一次窗口 | Shell 侧多一个 `IExplorerCommand`；`GetState` 要查 journal 是否存在，注意别放进慢路径 |
| 拖放启动 | 更直观 | 窗口 `AllowDrop` + `StorageItems` |
| ~~`x:Bind` + `UserControl`~~ | **实测只省 2MB**（绑定层总开销上限），是 CPU 优化不是内存优化 | 要迁移 767 行窗口逻辑 —— 性价比为零，已放弃 |

**明确不做**：

- 传统 verb（`MultiSelectModel` 默认 `Single`，多选只传第一个文件，对批量工具等于废掉）
- 递归子文件夹
- 云盘占位文件、回收站等虚拟项

M3 期间引擎侧**并非**"只需消费"——实际追加了磁盘冲突检测（`MarkDiskConflicts` + `ExistsExact`）。教训：能不能安全执行，取决于预览阶段拦不拦得住冲突；把冲突检测推给执行器就晚了，文件已经被覆盖。

## 10. 踩坑清单

按"踩到的顺序"记，每条都是实际调试出来的，不是推测。

### 文件系统与 Shell

- **`File.Exists` 在 NTFS 上忽略大小写**：不能校验「仅大小写改名」是否生效，也不能判断撤销时源名是否被占用。两处都必须用 `ExistsExact`（枚举目录 + `StringComparison.Ordinal`）
- **`File.Exists` 对目录返回 false**：判断目录存在必须用 `Directory.Exists`
- **`File.Move` 动不了目录**（抛 `FileNotFoundException`）；目录改名交给 `IFileOperation`
- **`File.Move` 与 `IFileOperation::RenameItem` 都能直接做仅大小写改名**（文件与目录皆可）。v1.2 的"必须两步中转"是误判，真正病因是"同一队列引用尚未生成的临时名"
- **系统级 `Ctrl+Z` 是可行的**：`FOFX_ADDUNDORECORD` 把整批登记进 Shell 撤销栈，进程退出后依然有效。旧版"有意不加 `FOF_ALLOWUNDO`"导致资源管理器撤销完全无反应
- **`IFileOperation` 改名失败的 HRESULT 常来自复制引擎**（`0x8027xxxx`，如 `0x80270027` = 源文件被占用），不是 `0x8007xxxx`；只映射 Win32 那族会把错误显示成原始码
- **磁盘冲突检测必须在预览阶段做**：执行带 `FOF_NOCONFIRMATION`，覆盖同名文件连提示都没有
- **"源名会让出"的豁免会把环放过去**：`a→b` + `b→a` 两项互相把对方当空位 → 静默覆盖。由 `MarkBatchCycles` 在预览阶段拦掉
- **链式改名的入队顺序决定会不会丢数据**：`a→b` + `b→c` 错序时 `a→b` 会先覆盖 `b` 的原内容。执行器按 `OrderByDependency` 拓扑排序入队

### 稀疏包与安装

- **稀疏包绑死绝对路径**：从 `A\packaging` 装过的包不能直接从 `B\packaging` 重注册，报 `0x80073D0B`。安装前必须比较 `Get-AppxPackage.InstallLocation` 与清单目录
- **`Remove-AppxPackage` 会被运行中的 dllhost 挡住**（`0x80073D02`）。卸载前要先停 App 进程 + 按模块名过滤停 dllhost
- **重复注册时 `0x80073D02` 也出现在 `Add-AppxPackage`**：先停 explorer 释放 COM 代理，再注册
- **`Get-Content -Raw` 必须加 `-Encoding UTF8`**：清单是无 BOM 的 UTF-8，PowerShell 5.1 默认按 ANSI 解码会把中文 `DisplayName` 读成乱码并吃掉引号，`[xml]` 转换直接抛异常
- **`param` 默认值里不能用 `$PSScriptRoot`**：求值时机早于自动变量初始化，会得到空串并在 `Split-Path` 上抛错。挪到体外计算
- **`Directory.Delete(recursive: true)` 不进回收站**：安装器绝不能把"用户选中的目录"当安装目录删除。正确做法是装到其下的子目录，且删前校验标记文件

### 窗口与图标

- **包图标在任务栏会被垫「强调色底板」（plating）**：`altform-unplated` 变体**实测无效**，系统不会因为变体存在就换渲染。真正解法是 `AppWindow.SetIcon(ico 绝对路径)`——窗口图标一旦显式设置，任务栏就不再回落到包 logo
- **`SetIcon` 不能传 exe 路径**：Shell 会在 exe 的图标资源里自己挑，实测挑到了 .NET 的默认文档图标。必须传独立 ico 文件
- **`LaunchUriAsync` 对无效 URI 返回 `true`**：`ms-settings:personalization-contextmenu` 不存在，但调用返回成功并打开设置首页，导致"逐个 URI 尝试、成功即返回"的写法永远走不到回退分支
- **`AppWindow.Resize` 吃物理像素**：150% 缩放下按 DIP 传会显得窗口偏小，要乘 `GetDpiForWindow(hwnd)/96`

### WinUI 3 与性能

- **`ListView` 行模板里放 `CheckBox`，5000 项多占 46–49MB**：模板化控件会按已实现行摊模板与动画资源。换 `Border` + `FontIcon` 后 180MB → 137MB。行内只允许叶子元素
- **内存几乎与项数无关**：实测 500 项 135MB、5000 项 143MB（私有提交）。真正的构成是 114MB 框架基线 + 21MB 列表首屏固定开销 + 每项 1.8KB
- **绑定层总开销只有 2MB**：把行模板 7 处 `{Binding}` 全换成静态文本，186MB → 184MB。所以 `x:Bind` 是 CPU 优化，不是内存优化
- **`XamlControlsResources.ThemeDictionaries` 按启动时主题固化**：代码里取 `"Light"` 字典拿到的可能仍是深色值。不要用代码查主题色——把 `{ThemeResource}` 写在 XAML 里，转换器只取现成 brush 对象
- **`IsThreeState="True"` 的点击循环会经过 `null`**：只在 `Checked` 里处理会让全选框卡在中间态。用 `Click` + 从列表真实状态推断意图
- **`ListViewItem` 会吃掉子元素的 `Tapped`/`PointerPressed`**：行内交互只能走 `ListView.ItemClick`；要判"点的是哪一列"，得另外用 `AddHandler(PointerPressedEvent, …, handledEventsToo: true)` 记下 x
- **`ListView.Header` 会跟着内容滚走**：表头要移出 `ListView`，放进它上方的独立行
