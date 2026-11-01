# BatchRename 架构说明

> 配套文档：[SPEC.md](SPEC.md) 定义**要做什么**（契约与验收），本文说明**为什么这样做**与**怎么排障**。
> 最后更新：2026-11-01（v1.5：系统级撤销 + journal 降级为单文件事务日志 + CaseOnly 两步作废。本轮实测与问题清单见 [REVIEW-2026-11-01.md](REVIEW-2026-11-01.md)）

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
| `App/MainWindow` | 预览 UI | **两层防抖**（150ms 纯内存 + 400ms 磁盘）；执行/撤销后保留用户已输入的规则；`Esc` 有未执行改动时二次确认；「全选」三态与列表同步；DataTemplate 用 `{Binding}`（`Window` 根不支持 `x:Bind`） |
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

任务栏这条还有个坑：只给默认 logo 时，系统会垫一块**强调色底板**（plating），圆角外露出 accent color。必须额外提供 `*.targetsize-*_altform-unplated.png` 变体。

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
撤销的"刚刚那一下"由 Shell 撤销栈兜住（`FOFX_ADDUNDORECORD`，与进程死活无关）；journal 剩下的不可替代职能只有两个——write-ahead 崩溃标记、软件内撤销最近一批。7 天 / 20 批既没有 UI 入口，也没有需求，实测 20 个文件 85KB 里有 10 个 `Applied` 记录永远撤不到。降级后 `UndoJournal` 只剩一个覆盖写的 `last-batch.json`，状态机从三个分支收敛到两个。详见 [REVIEW-2026-11-01.md](REVIEW-2026-11-01.md) §7.6。

**为什么正则优先 `NonBacktracking`？**
灾难性回溯（`(a+)+$` 这类）会让 UI 卡死几十秒。`NonBacktracking` 引擎天然免疫，只在用到 lookaround/backreference 时才回退到带 250ms 超时的引擎。

## 7. 构建与产物

```
源码                           产物
─────────────────────────────────────────────────────────
src/BatchRename.Shell/   ──▶   bin/publish/BatchRename.Shell.dll
src/BatchRename.App/     ──▶   bin/publish/BatchRename.App/  (整个目录)
packaging/Assets/        ──▶   bin/publish/Assets/
```

`bin/publish` 是**外部位置根**，`Add-AppxPackage -Register ... -ExternalLocation` 指向它。清单里所有 `Path` 都相对这个根，**不是**相对清单文件——写成 `..\bin\publish\...` 会直接失败。

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

## 9. 扩展点（M3 及以后）

| 里程碑 | 落点 | 需要新增 |
|---|---|---|
| ~~M3 执行+撤销~~ ✅ | `Core/FileOperationRunner`、`Core/UndoJournal` | 已完成（含磁盘冲突检测、write-ahead、Shell 撤销栈登记）。v1.5 修正：CaseOnly 两步作废、journal 降级为单文件 |
| M4 单实例 | `App/ShellPayload` | 命名 Mutex + 命名管道转发；`SetForegroundWindow` 需先 `AllowSetForegroundWindow` |
| M4 观感 | `MainWindow` | Mica（`SystemBackdrop`）+ 自定义标题栏（`ExtendsContentIntoTitleBar`） |
| M4 键盘 | `MainWindow` | `Enter` 执行 / `Esc` 关闭 / 上下键移动 / `Space` 切勾选 |
| M5 兜底 | `packaging/` | 传统 `ContextMenuHandlers` 注册作为 `Shift+右键` 入口 |
| **明确不做** | — | 右键菜单第二个 verb「撤销上次重命名」（用户判定没有必要：软件内撤销 + 资源管理器 Ctrl+Z 已覆盖） |
| **下一轮** | `Core` | 失败项重试（需先定 journal 语义：重试批次会覆盖上一批撤销记录） |
| M3 遗留 | — | 已清空：逐项进度/取消（v1.5）、失败清单对话框、表头吸顶（v1.6）都已落地 |

M3 期间引擎侧**并非**"只需消费"——实际追加了磁盘冲突检测（`MarkDiskConflicts` + `ExistsExact`）。教训是：能不能安全执行，取决于预览阶段拦不拦得住冲突；把冲突检测推给执行器就晚了，文件已经被覆盖。
