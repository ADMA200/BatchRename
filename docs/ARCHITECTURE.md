# BatchRename 架构说明

> 配套文档：[SPEC.md](SPEC.md) 定义**要做什么**（契约与验收），本文说明**为什么这样做**与**怎么排障**。
> 最后更新：2026-10-31（对应提交 `v0.1-M1M2`）

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
  └─ MainWindow → RenameEngine.Run()
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
     └───────▶ (M3) FileOperationRunner ──▶ UndoJournal

ShellPayload ◀── App（只在 App.OnLaunched 里被调用一次）
Shell 扩展（C++）──▶ Launcher / Log（互不依赖）
```

**硬规则：`Core/` 与 `Models/` 不引用任何 WinUI 类型。**

这不是洁癖，是为了让 `BatchRename.ShellTest`（控制台）能**直接链接源码编译**，把引擎逻辑跑成断言，完全不启动 UI。当前 33 项断言就是这么跑起来的——改规则逻辑不需要开窗口、不需要注册右键菜单。

| 模块 | 职责 | 关键实现约束 |
|---|---|---|
| `Core/NameSplitter` | 主名/扩展名切分 | 只切最后一层；目录永不切；`i==0` 视为 dotfile |
| `Core/RenameRule` | 文本/正则替换 + 四种大小写 | 大小写一律 `Invariant`；正则优先 `NonBacktracking`，回退带 250ms 超时 |
| `Core/RenameEngine` | 编排 + 九条校验 + 批内互撞 | 替换**先**、大小写**后**；作用域「两者」绝不拼串 |
| `Models/RenameItem` | 单项数据 | `INotifyPropertyChanged`，`State` 驱动 UI 配色 |
| `App/ShellPayload` | stdin 解析 | `Encoding.Unicode` + `'\0'` 切分 |
| `App/MainWindow` | 预览 UI | 150ms 防抖；DataTemplate 用 `{Binding}`（`Window` 根不支持 `x:Bind`） |
| `Shell/Launcher` | 定位 exe + 管道启动 | 路径靠 `GetModuleFileNameW` 推导，零 IO |
| `Shell/Log` | 双写日志 | 1MB 循环；**只能在 Invoke 路径调用** |

## 6. 关键决策

**为什么用稀疏包而不是传统注册表 COM 注册？**
传统方式要写 `HKCR\CLSID\{...}`（需管理员）、还要给 explorer 加 `ContextMenuHandlers` 键。稀疏包把 COM 注册和菜单注册都放进一个可撤销的包，`Add-AppxPackage -Register` / `Remove-AppxPackage` 就是安装/卸载，不污染注册表，也不需要管理员。

**为什么 C++ 用 WRL 不用 ATL？**
只需实现 `IExplorerCommand` + `IObjectWithSite` 两个接口，WRL 的 `RuntimeClass` 模板够用，省掉 ATL 依赖（少一个 SDK 组件要求）。

**为什么 `GetState` 用 `ECS_HIDDEN` 而不是 `ECS_DISABLED`？**
灰掉的菜单项会让用户怀疑程序坏了。在"此电脑""网络""回收站"上直接不显示更干净。

**为什么引擎要做"仅大小写变化"单独标记？**
`a.txt` → `A.txt` 在 NTFS 上是合法改名，但 `IFileOperation` 直接改会被判"目标已存在"。必须走两步：先改成临时名，再改成目标名。引擎提前标记出来，执行器才知道哪些项要特殊处理（M3 实现）。

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

**日志位置有个坑**：dllhost 在包容器身份下运行，`%LOCALAPPDATA%\BatchRename\logs\` 会被文件虚拟化重定向，真实路径是：

```
C:\Users\<user>\AppData\Local\Packages\BatchRename.Shell_<hash>\LocalCache\Local\BatchRename\logs\shell.log
```

去前者找不到日志不代表没写，先看后者。

## 9. 扩展点（M3 及以后）

| 里程碑 | 落点 | 需要新增 |
|---|---|---|
| M3 执行+撤销 | `Core/FileOperationRunner` | STA 线程 + CsWin32 `IFileOperation`；两步改名处理 CaseOnly；WAL 边执行边落盘 |
| M3 撤销 | `Core/UndoJournal` | `%LOCALAPPDATA%` 下 journal 文件；启动时检测未完成批次 |
| M4 单实例 | `App/ShellPayload` | 命名 Mutex + 命名管道转发；`SetForegroundWindow` 需先 `AllowSetForegroundWindow` |
| M4 观感 | `MainWindow` | Mica（`SystemBackdrop`）+ 自定义标题栏（`ExtendsContentIntoTitleBar`） |
| M5 兜底 | `packaging/` | 传统 `ContextMenuHandlers` 注册作为 `Shift+右键` 入口 |

引擎侧无需改动即可支撑 M3——`RenameEngine.Run` 已经把 `CaseOnly`、`State`、`NewFullPath` 准备好，执行器只需消费。
