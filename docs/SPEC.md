# BatchRename 设计与实施规格书

> 目标：Windows 11 26H2 上一个对标 PowerRename 的批量重命名工具（个人自用，非商业）
> 基线：Windows 11 26H2（Build 26300 系列）/ Visual Studio 2026 / .NET 10 LTS / Windows App SDK 2.6.4
> 文档版本：**v1.12 · 2026-11-02**（v1.0 · 2026-10-30）
> 状态：**规格书（Spec）+ 落地修订**。本文件不含可直接编译的工程，只含契约、清单全文、脚本全文、关键片段与验收标准。


## 目录

- [0. 文档说明](#0-文档说明)
- [1. 需求与范围](#1-需求与范围)
- [2. 技术选型](#2-技术选型)
- [3. 系统架构](#3-系统架构)
- [4. 工程结构](#4-工程结构)
- [5. Shell 扩展规格](#5-shell-扩展规格)
- [6. 打包与注册](#6-打包与注册)
- [7. 主程序规格](#7-主程序规格)
- [8. 重命名引擎规格](#8-重命名引擎规格)
- [9. 里程碑与验收](#9-里程碑与验收)
- [10. 26H2 适配](#10-26h2-适配)
- [11. 风险清单与排障](#11-风险清单与排障)
- [12. 附录](#12-附录)
- [修订历史](#修订历史)

---


## 0. 文档说明

### 0.1 这份文档怎么用

在 Mac 上写完，带到 Windows 上用。三种用法：

| 用法 | 做法 |
|---|---|
| 喂给编码 AI | 整份文档作为唯一上下文，按 §9 的 M1→M5 逐阶段下指令，一次只让 AI 做一个里程碑 |
| 自己照敲 | 从 §6 开始（清单 + 安装脚本），先让菜单出现，再补业务逻辑 |
| 当 review 清单 | 每个里程碑结束时对照 §9 的验收项逐条打勾，别跳步 |

**关键顺序**：M1（菜单出现 + 拉起空窗口）必须最先做完并验证通过，再写任何业务代码。历史上这类项目的失败案例八成死在清单/CLSID/位数上，而这三样与业务逻辑完全正交——晚发现等于白写。

### 0.2 标注约定

本文所有技术结论分两类，阅读时请区分：

- `[官方]` —— Microsoft 官方文档、官方示例、官方发行说明可查证，可信度最高。
- `[判断]` —— 我基于 Win32 Shell / WinAppSDK 实现经验的推断，**必须在 Windows 上实测确认**。这类结论在文档里都用这个标记点出来。

### 0.3 事实核实清单

| # | 结论 | 标注 | 依据 |
|---|---|---|---|
| 1 | 进 Win11 一级右键菜单的唯一受支持路径是 `IExplorerCommand` + 包身份 | `[官方]` | Learn: Add a File Explorer context menu command to a packaged desktop app |
| 2 | 包身份可以由 MSIX 提供，也可以由稀疏包（external location）提供 | `[官方]` | 同上 + Sparse packages |
| 3 | 官方清单示例用的是 `com:SurrogateServer`，即 DLL 由 dllhost 代理承载，而非注入 explorer.exe | `[官方]` | 同上（清单示例） |
| 4 | `com:Extension` 与 `desktop4:Extension` 都声明在 `<Application><Extensions>` 下 | `[官方]` | 原文："Under the application Extensions element…" |
| 5 | 微软**不建议**把 shell 扩展写成托管代码，且不视其为受支持场景 | `[官方]` | Guidance for Implementing In-Process Extensions |
| 6 | 只实现 `IContextMenu` 的扩展会掉进"显示更多选项"里的旧菜单 | `[官方]` | Make apps great for Windows |
| 7 | 26H2 重设计了右键菜单：第三方扩展默认收进「应用扩展」子菜单，可在设置里提升/隐藏 | `[官方]` | 26H2 Release Preview 发行说明（KB5127936） |
| 8 | Explorer 的系统级 Ctrl+Z 无法撤销**其它进程**发起的批量重命名 | `[实测否定]` v1.5 | 旧判断**错误**。`FOFX_ADDUNDORECORD` 让 Shell 撤销栈接管本次操作；实测改名进程退出后 Ctrl+Z 仍有效。详见 §8.9.2 |
| 9 | 注册表直写 `*\shell` 在 24H2 之后不能再拿到一级菜单 | `[判断]` | 社区一致结论；24H2 起该行为被移除 |
| 10 | 仅改大小写（`a.txt`→`A.txt`）在大小写不敏感卷上直接改名会失败 | `[实测否定]` v1.5 | 旧判断**错误**。`File.Move` 与 `IFileOperation::RenameItem` 都能直接完成仅大小写改名（文件与目录皆可）；v1.2 那次失败的原因是"同一队列引用未生成的临时名"，与大小写无关 |

### 0.4 版本基线

| 组件 | 版本 | 说明 |
|---|---|---|
| Windows | 11 26H2 / Build 26300+ | `MaxVersionTested` 按此填 |
| Visual Studio | 2026 | 需勾选：`.NET 桌面开发`、`使用 C++ 的桌面开发`、`Windows 11 SDK` |
| Windows SDK | 10.0.26100 或 10.0.28000 系列 | `TargetPlatformVersion` 按机器上实际装的填 |
| .NET | 10（LTS） | 当前 LTS；.NET 11 仍是 RC，不用 |
| Windows App SDK | **2.6.4** | 走 NuGet `Microsoft.WindowsAppSDK`。🔧 v1.1：v1.0 写「2.5.x 或更新」，实测 2026-10 时 NuGet 最新稳定版为 **2.6.4**，项目已采用 |

---

## 1. 需求与范围

### 1.1 必须实现

| # | 功能 | 交互定义 | 优先级 |
|---|---|---|---|
| F1 | 多选文件/文件夹 → 右键进入 | 一次右键带 N 个选中项，全部进同一批 | P0 |
| F2 | 匹配 + 替换 | 查找词 + 替换词，默认纯文本；可选正则 | P0 |
| F3 | 区分大小写开关 | 关（默认）时 `abc` 能命中 `ABC` | P0 |
| F4 | 作用域 | 仅文件名 / 仅扩展名 / 两者 | P0 |
| F5 | 大小写转换 | 不转换 / 全大写 / 全小写 / 每词首字母大写 | P0 |
| F6 | 实时预览 | 原名 → 新名两栏，逐行勾选启用，冲突红字，无变化灰字 | P0 |
| F7 | 执行 + 撤销 | 逐项结果、可取消；`Ctrl+Z` 撤销上一批；journal 跨重启有效 | P1 |

### 1.2 明确不做

| 不做 | 理由 |
|---|---|
| 随机字符串填充 | 用户明确不要 |
| 日期/时间填充 | 用户明确不要 |
| 元数据（EXIF/ID3/PDF）替换 | 用户明确不要；且要引入一堆解析库，与"轻量"冲突 |
| 计数器 `$n` 枚举 | 用户明确不要；真要做会牵扯排序稳定性与位数补零，是新的一块设计 |
| 上次输入记忆（MRU） | 需要持久化 + 下拉 UI，性价比最低 |
| 递归子文件夹 | PowerRename 也不做；且"父目录先改名、子项路径要跟着变"是另一套排序逻辑 |
| 云盘/回收站/库等虚拟项改名 | `IShellItemArray` 里可能存在，统一过滤掉（见 §5.4） |

### 1.3 默认取舍表

以下是我在没拿到明确指示时按经验定的默认值。任何一条要改，直接说，改动成本都不大。

| 项 | 默认 | 理由 |
|---|---|---|
| 正则 | **做**，默认关闭的开关 | .NET 自带，成本极低，但要防灾难性回溯（§8.6） |
| 冲突策略 | **默认红字阻止**；可选"自动编号"开关（默认关） | 误操作成本不对称：自动编号错了要一个个手工改回，红字只是多点一次 |
| 撤销入口 | 主程序 `Ctrl+Z` / 底栏「撤销」+ **资源管理器 `Ctrl+Z`** | 🔧 v1.5：系统级撤销不是死路（`FOFX_ADDUNDORECORD`），两套入口并存；右键第二个 verb 仍未做 |
| 撤销有效期 | **只保留最近一批**（`last-batch.json`，覆盖写），无时间上限 | 🔧 v1.5：7 天 / 20 批是没有入口的伪需求（实测 20 个文件 85KB，其中 10 个 Applied 永远撤不到）；跨会话靠这一条 + Shell 撤销栈 |
| 执行权限 | 沿用当前进程权限，UAC 用 `FOFX_SHOWELEVATIONPROMPT` 逐次弹 | 不做"整批提权重跑"，那要另起进程 + 重新枚举，v1 不要 |
| 界面语言 | 中英双语资源（resw），跟随系统 | 菜单标题与窗口文案走同一套资源 |
| **运行形态** | **无常驻**：窗口关闭即退出，**无托盘图标、无开机自启** | 低频工具常驻是负价值；右键是用户动作触发，不需要预先有进程。详见 §7.7 |

---

## 2. 技术选型

### 2.1 结论

**两段式**：

1. **`BatchRename.Shell.dll`**（C++ / WRL，约 300 行）—— 只做五件事：画菜单项、判断能不能改名、从选中项取路径、把路径传给主程序、启动失败时说一声（弹框 + 日志）。
2. **`BatchRename.App.exe`**（C# / .NET 10 / WinUI 3，单项目）—— 所有业务逻辑、UI、执行、撤销。

### 2.2 四方案对比

| 方案 | 改 UI 要重启 Explorer | 崩溃是否带走 Explorer | 语言数 | 迭代速度 | 风险 | 结论 |
|---|---|---|---|---|---|---|
| **C# 主程序 + 极薄 C++ 壳** | 否 | 否 | 2（C++ 只写一次） | 快 | 低 | **采用** |
| 全 C++/WinRT（照抄 PowerToys） | 是，每次 | 是 | 1 | 慢 | 中 | 淘汰 |
| 纯 C# + NativeAOT 壳 | 否 | 否 | 1 | 中 | 高 | 淘汰 |
| Rust 壳 + C# UI | 否 | 否 | 2 | 中 | 中 | 淘汰 |

淘汰理由：

- **全 C++**：正则只有 `std::regex`（慢且语法残）、XAML 绑定无 C# 便捷、每次改完要 `taskkill /f /im explorer.exe`、一处 bug 崩掉整个资源管理器。单语言省下的钱，全赔进调试时间。
- **NativeAOT 纯 C# 壳**：理论上最优雅（无 CLR 宿主、单语言），实际要手写 `DllGetClassObject` 导出 + `ComWrappers` 的 vtable 映射，且进程是 dllhost——**挂不上断点**，崩了只知道"资源管理器重启了"。等于给自己造一个黑盒。
- **Rust 壳**：也是好方案，但多一套 cargo 工具链和 MSVC 链接配置，对你（iOS/Swift 背景）不划算。

### 2.3 两条已证死路（别再试）

**死路一：把重命名放进 shell DLL，靠 Explorer 的 Ctrl+Z 撤销**

放弃，两个原因：

1. 微软明确要求 `Invoke` 必须立刻返回，几百个文件的文件操作会把资源管理器 UI 卡死；
2. 撤销栈是 Explorer 进程内的，即使你就在 explorer.exe 里操作，一旦 DLL 崩溃就带走整个 shell。

正确做法：撤销自己做（§8.10），并额外提供一个"撤销上次重命名"的右键菜单项。

**死路二：注册表直写 `*\shell\` 拿一级菜单**

这个技巧在 24H2 之后被移除。稀疏包是唯一受支持路径。部分教程还在教 `HKCU\Software\Classes\CLSID\{86ca1aa0-...}\InprocServer32` 空值去**关掉**新版菜单（从而让旧菜单变一级），那是全局副作用，绝对不要在安装脚本里碰。

---

## 3. 系统架构

### 3.1 进程与线程拓扑

```
Explorer.exe (用户右键)
   │  Shell 构建菜单 → 激活 COM 类
   ▼
dllhost.exe  [STA, mediumIL, 由 com:SurrogateServer 承载]
   └─ BatchRename.Shell.dll
        ├─ GetTitle / GetIcon / GetCanonicalName   → 静态返回，零 IO
        ├─ GetState                                → 只读 SFGAO 属性位，零路径解析
        └─ Invoke
             ├─ 枚举 IShellItemArray → vector<wstring>
             ├─ CreatePipe(1MB) + STARTUPINFOEX(HANDLE_LIST 白名单)
             └─ CreateProcess(BatchRename.App.exe) + 后台线程写 stdin
   │  UTF-16 路径 + \0 分隔符，EOF 结束
   ▼
BatchRename.App.exe  [WinUI 3, unpackaged, 稀疏包身份]
   ├─ 解析 stdin → RenameItem[]
   ├─ 单实例判定（Mutex 抢不到 → 命名管道转发给首实例 → 退出）
   ├─ 引擎：切分 → 替换/正则 → 大小写 → 校验 → 冲突
   ├─ 预览 ListView（虚拟化 + 200ms 防抖）
   └─ 执行：专用 STA 线程 + IFileOperation
        └─ %LOCALAPPDATA%\BatchRename\journal\*.json（WAL）
```

### 3.2 启动时序与耗时预算

| 阶段 | 目标耗时 | 说明 |
|---|---|---|
| 右键 → 菜单出现 | ≤ 30ms | 我们的 `GetState` 只读属性位，不碰磁盘 |
| 点击 → 进程启动 | 冷启 0.8–1.5s / 热复用 ≤ 100ms | 热复用时窗口已存在，只换数据源 |
| stdin 读取 + 建模 | 200 项 ≤ 20ms | 一次读全，不逐行 await |
| 首屏可交互 | 5000 项 ≤ 1.5s | 分批追加，先显示前 200 项 |

### 3.3 三个关键决策

**决策 1：传参用匿名管道，路径用 UTF-16 + `\0` 分隔**

| 候选 | 否决理由 |
|---|---|
| 命令行参数 | 上限 32767 字符（且实际受环境影响）；引号转义对含 `"` 的路径要命 |
| 临时文件 | 要处理清理、并发、权限；崩溃会留垃圾 |
| 按行分隔（PowerRename 的早期做法） | 文件名**合法**可含 `\n`。`a\nb.txt` 会被撕成两行 |
| **UTF-16 + `\0`** | `\0` 是唯一不能出现在文件名里的字符，天然安全 |

**决策 2：单实例判定放在 C# 侧，shim 永远只 CreateProcess**

shim 保持无状态：不连 IPC、不判断"是否已有实例"、不等待。理由：shim 跑在 dllhost 里，任何阻塞都可能连带影响 shell 的菜单体验；而 C# 侧有完整的进程控制能力，转发/置前逻辑写在那里更简单也更可控。

**决策 3：执行器用 `IFileOperation`，不用 `MoveFileEx`**

| 需求 | MoveFileEx | IFileOperation |
|---|---|---|
| 仅改大小写（NTFS） | 直接报 `ERROR_ALREADY_EXISTS`？ | 🔧 v1.5 实测否定：`IFileOperation::RenameItem` **直接就能改**（文件与目录皆可），不需要临时名中转 |
| 长路径 > 260 | 需 `\\?\` 前缀手工拼，处处小心 | 原生支持 |
| 逐项错误 | 只有一个 `GetLastError` | `IFileOperationProgressSink::PostRename` 逐项回调 |
| 进度 / 取消 | 自己写 | 内建 |
| OneDrive 占位符 / 云文件 | 会触发非预期下载或失败 | 走 shell 协议，正确 |
| UAC | 不支持 | `FOFX_SHOWELEVATIONPROMPT` |
| 幂等/顺序语义 | 自己控制 | `PerformOperations` 按入队顺序执行 |

结论：IFileOperation 多花的代价只是"必须有个带消息泵的 STA 线程"，而这个线程本来就需要（进度回调）。

---

## 4. 工程结构

### 4.1 目录树

```
<REPO>\
├─ BatchRename.sln
├─ docs\
│  └─ SPEC.md                          # 本文档
├─ src\
│  ├─ BatchRename.Shell\               # C++ / x64 / 无 ATL 无 Boost
│  │  ├─ pch.h
│  │  ├─ dllmain.cpp
│  │  ├─ ExplorerCommand.h             # ★ CLSID 唯一真源
│  │  ├─ ExplorerCommand.cpp
│  │  ├─ Launcher.h
│  │  ├─ Launcher.cpp
│  │  ├─ Log.h                          # AppendLog / OutputDebugString 双写，1MB 循环
│  │  ├─ Log.cpp
│  │  └─ BatchRename.Shell.vcxproj
│  ├─ BatchRename.App\                 # C# / WinUI 3
│  │  ├─ app.manifest
│  │  ├─ BatchRename.App.csproj
│  │  ├─ App.xaml / App.xaml.cs
│  │  ├─ MainWindow.xaml / MainWindow.xaml.cs
│  │  ├─ ShellPayload.cs
│  │  ├─ Models\RenameItem.cs
│  │  ├─ Core\NameSplitter.cs
│  │  ├─ Core\RenameRule.cs
│  │  ├─ Core\RenameEngine.cs
│  │  ├─ Core\ConflictResolver.cs
│  │  ├─ Core\FileOperationRunner.cs
│  │  ├─ Core\UndoJournal.cs
│  │  └─ Core\NativeMethods.txt        # CsWin32 输入清单
│  └─ BatchRename.ShellTest\           # 控制台：绕过 Shell 直接调 COM
│     └─ Program.cs
├─ packaging\
│  ├─ AppxManifest.xml
│  └─ Assets\
│     ├─ StoreLogo.png                 # 50x50
│     ├─ Square44x44Logo.png
│     └─ Square150x150Logo.png
└─ tools\
   ├─ Install-Sparse.ps1
   ├─ Uninstall.ps1
   └─ New-DevCert.ps1                  # 可选，仅在不开发者模式时装
```

### 4.2 文件职责（21 个源文件）

| 文件 | 职责 | 依赖 |
|---|---|---|
| `pch.h` | 预编译头：`windows.h` / `wrl/client.h` / `wrl/module.h` / `shobjidl_core.h` / `shlwapi.h` | — |
| `dllmain.cpp` | `DllMain` 里 `Module<InProc>::Create()/Terminate()`；`DisableThreadLibraryCalls` | pch |
| `ExplorerCommand.h` | `DECLSPEC_UUID` 宏定义 CLSID（**唯一真源**）、类声明 | pch |
| `ExplorerCommand.cpp` | 8 个 IExplorerCommand 方法 + `IObjectWithSite` | Launcher |
| `Launcher.h/.cpp` | 定位 exe 路径、CreatePipe、STARTUPINFOEX、CreateProcess、后台写线程 | pch |
| `Log.h/.cpp` | `AppendLog(msg, code)`：写 `%LOCALAPPDATA%\BatchRename\logs\shell.log`（1MB 循环）+ `OutputDebugStringW` 双写；**只能在 Invoke 路径调用** | pch |
| `app.manifest` | `PerMonitorV2` DPI、`longPathAware`、` requestedExecutionLevel=asInvoker` | — |
| `BatchRename.App.csproj` | 属性组合见 §7.1 | — |
| `App.xaml.cs` | **先**解析 stdin / 追加激活参数，**再**建窗口（避免闪一个空窗） | ShellPayload |
| `MainWindow.xaml` | 全部 UI，单窗口，无 Page/Navigation | — |
| `MainWindow.xaml.cs` | 输入事件 → 防抖 → 调引擎 → 绑定；执行/撤销命令 | Core.* |
| `ShellPayload.cs` | stdin 解析、Mutex 抢锁、命名管道转发、`SetForegroundWindow` | — |
| `RenameItem.cs` | 单项数据模型（§8.1） | — |
| `NameSplitter.cs` | 主名/扩展名切分（§8.2） | — |
| `RenameRule.cs` | 替换（文本/正则）+ 大小写转换（§8.5/8.6） | — |
| `RenameEngine.cs` | 编排：对每项应用规则 → 校验 → 产出结果（§8.7） | Splitter, Rule |
| `ConflictResolver.cs` | 批内互撞 + 磁盘存在 + 自动编号（§8.8） | — |
| `FileOperationRunner.cs` | STA 线程 + CsWin32 IFileOperation + 依赖排序 + 逐项结果（§8.9） | NativeMethods |
| `UndoJournal.cs` | WAL 写读、失效判定、反向执行（§8.10） | FileOperationRunner |
| `NativeMethods.txt` | CsWin32 生成清单（内容见 §8.9.3） | — |
| `ShellTest\Program.cs` | `CoCreateInstance` 直调 DLL 的 `Invoke`，绕过 Explorer 排障 | — |

### 4.3 依赖方向

```
MainWindow → RenameEngine → (NameSplitter, RenameRule)
MainWindow → ConflictResolver
MainWindow → FileOperationRunner → UndoJournal
ShellPayload ← App
```

硬规则：`Core\*` 不引用任何 WinUI 类型（`DispatcherQueue` 除外，且只通过回调注入）。这样引擎可以在 `ShellTest` 里无 UI 跑单元测试。

### 4.4 工程属性

| 工程 | 关键属性 |
|---|---|
| `BatchRename.Shell` | `ConfigurationType=DynamicLibrary`、`Platform=x64`（只编 x64）、`CharacterSet=Unicode`、`LanguageStandard=stdcpp20`、`PrecompiledHeader=Use`、`RuntimeLibrary=/MD`、附加依赖 `runtimeobject.lib shlwapi.lib shell32.lib ole32.lib oleaut32.lib **user32.lib**`、`ModuleDefinitionFile=BatchRename.Shell.def` |
| 禁止 | `AnyCPU` / `neutral` / `Win32`(x86) 平台。x86 的 explorer 在 11 上不存在（ARM64 版走 x64 仿真，仍加载 x64 DLL）`[判断]` |

> 🔧 v1.1：v1.0 的依赖清单**漏了 `user32.lib`**。`ReportLaunchFailure` 用到的 `MessageBoxW` 在 user32 里，缺了链接阶段直接 `LNK2019: 无法解析的外部符号 __imp_MessageBoxW`。
>
> 另：`ModuleDefinitionFile` 也是必需的，原因见 §5.1 的 v1.1 说明。

---

## 5. Shell 扩展规格

### 5.1 类定义（WRL，不用 ATL）

```cpp
// ExplorerCommand.h
#pragma once
// ★ 唯一真源：改这里，然后手动同步 packaging\AppxManifest.xml 的两处
#define BATCHRENAME_CLSID_STR  L"A1B2C3D4-1111-4A2B-9C3D-000000000001"
#define BATCHRENAME_VERB_ID    L"BatchRenameFiles"

class __declspec(uuid("A1B2C3D4-1111-4A2B-9C3D-000000000001"))
    BatchRenameCommand final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IExplorerCommand, IObjectWithSite>
{
public:
    // IExplorerCommand
    // 🔧 v1.1：全部方法必须带 noexcept。v1.0 的声明没写而 §5.3 的实现写了，
    //           C++17 起 noexcept 是函数类型的一部分，声明/定义不一致直接 C2382。
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR*) noexcept override;
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR*) noexcept override;
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR*) noexcept override;
    IFACEMETHODIMP GetCanonicalName(GUID*) noexcept override;
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE*) noexcept override;
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) noexcept override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS*) noexcept override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand**) noexcept override;
    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown*) noexcept override;
    IFACEMETHODIMP GetSite(REFIID, void**) noexcept override;

private:
    Microsoft::WRL::ComPtr<IUnknown> m_site;
};

// ExplorerCommand.cpp 末尾：
CoCreatableClass(BatchRenameCommand);
```

`dllmain.cpp`：

```cpp
BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hInst);
        Microsoft::WRL::Module<Microsoft::WRL::InProc>::Create();
    } else if (reason == DLL_PROCESS_DETACH) {
        Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Terminate();
    }
    return TRUE;
}
```

> 🔧 **v1.1 更正（重要）**：v1.0 原文称「`wrl/module.h` 会自动导出 `DllGetClassObject` / `DllCanUnloadNow` / `DllGetActivationFactory`，不用手写、不用 .def」——**这是错的**。
>
> WRL 的 `Module<InProc>` 只提供成员函数，**不做任何导出**。不显式导出的话 DLL 导出表是空的，`CoCreateInstance` 报 `0x80040154`（REGDB_E_CLASSNOTREG），而且事件日志里什么都不会有。
>
> 正确做法：写一个 `.def`（推荐），或者加 `__declspec(dllexport)`。若选择手写导出函数，签名**必须用 `STDAPI`**，不能用自己拼的 `extern "C" __declspec(dllexport) HRESULT WINAPI` —— `combaseapi.h` 里已有 `STDAPI` 形式的声明，两者链接属性不一致会报 `C2375: 重定义；不同的链接`。
>
> `dllmain.cpp` 的实际写法：
>
> ```cpp
> #include "pch.h"
> #include "ExplorerCommand.h"
>
> using Microsoft::WRL::InProc;
> using Microsoft::WRL::Module;
>
> BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
> {
>     if (reason == DLL_PROCESS_ATTACH) {
>         DisableThreadLibraryCalls(hInst);
>         Module<InProc>::Create();
>     } else if (reason == DLL_PROCESS_DETACH) {
>         Module<InProc>::GetModule().Terminate();
>     }
>     return TRUE;
> }
>
> // 导出交给 BatchRename.Shell.def，这里只负责实现
> STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
> {
>     return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
> }
>
> STDAPI DllCanUnloadNow()
> {
>     return Module<InProc>::GetModule().GetObjectCount() > 0 ? S_FALSE : S_OK;
> }
>
> STDAPI DllGetActivationFactory(HSTRING, void** factory)
> {
>     if (factory) *factory = nullptr;
>     return E_NOTIMPL;   // 纯 COM 组件，不提供 WinRT 激活
> }
> ```
>
> 对应的 `BatchRename.Shell.def`：
>
> ```
> LIBRARY BatchRename.Shell
> EXPORTS
>     DllGetClassObject       PRIVATE
>     DllCanUnloadNow         PRIVATE
>     DllGetActivationFactory PRIVATE
> ```
>
> 验证见 §5.7，`dumpbin /exports` 必须看到这三项。

### 5.2 方法契约表

| 方法 | 返回值策略 | 内存 | 禁止事项 |
|---|---|---|---|
| `GetTitle(psia, ppwsz)` | 有选中项时 `L"批量重命名 (N)"`（N=`psia->GetCount()`，内存操作）；否则 `L"批量重命名"` | `SHStrDupW` | 不查磁盘、不读配置 |
| `GetIcon(psia, ppwsz)` | 返回 `L"C:\\…\\BatchRename.App\\BatchRename.App.exe,0"`（显式带 `,0` 索引更稳；只给路径多数情况也能抽到 index 0 图标） | `SHStrDupW` | 不 `PathFileExists`（返回 `E_NOTIMPL` 会让图标位空白） |
| `GetToolTip` | `*ppwsz = nullptr; return E_NOTIMPL;` | — | 别返回空串 |
| `GetCanonicalName(GUID*)` | ⚠️ **返回 `GUID` 值，不是字符串**。`*pguid = __uuidof(BatchRenameCommand)` | — | 别当成 `LPWSTR` 去 `StringFromGUID2`（那是错的） |
| `GetState(psia, fOkToBeSlow, pstate)` | 见 §5.3。**任何失败路径都落到 `ECS_HIDDEN`** | — | 不解析路径、不 QI 耗时接口 |
| `Invoke(psia, pbc)` | 成功 `S_OK`；无有效项 `S_FALSE`（只写日志）；启动失败 `E_FAIL` + **弹框告知** | — | 不做文件操作、不等待子进程退出、不在 STA 线程上弹框 |
| `GetFlags(pflags)` | `*pflags = ECF_DEFAULT` | — | 不加 `ECF_HASSUBCOMMANDS`（否则必须实现 EnumSubCommands） |
| `EnumSubCommands()` | `*pp = nullptr; return E_NOTIMPL;` | — | — |
| `SetSite/GetSite` | 直接存/取 `m_site` | — | — |

枚举值备忘（`shobjidl_core.h`）：

```
EXPCMDSTATE: ECS_ENABLED=0  ECS_DISABLED=0x1  ECS_HIDDEN=0x2  ECS_CHECKBOX=0x4  ECS_CHECKED=0x8
EXPCMDFLAGS: ECF_DEFAULT=0  ECF_HASSUBCOMMANDS=0x1  ECF_HASSPLITBUTTON=0x2  ECF_HIDELABEL=0x4
             ECF_ISSEPARATOR=0x8  ECF_HASLUASHIELD=0x10  ECF_SEPARATORBEFORE=0x20
             ECF_SEPARATORAFTER=0x40  ECF_ISDROPDOWN=0x80
```

### 5.3 GetState：只读属性位

这是"右键转圈"的头号来源。规则：**只调 `GetAttributes(SFGAO_FILESYSTEM)`，绝不调 `GetDisplayName`**。

```cpp
IFACEMETHODIMP BatchRenameCommand::GetState(
    IShellItemArray* psia, BOOL fOkToBeSlow, EXPCMDSTATE* pstate) noexcept
{
    if (!pstate) return E_POINTER;
    *pstate = ECS_HIDDEN;
    if (!psia) return S_OK;                    // 背景右键：无选中项

    DWORD total = 0;
    if (FAILED(psia->GetCount(&total)) || total == 0) return S_OK;

    // 非 slow 路径只探前 8 项，够了：混选时前几项一定能代表"有没有文件系统对象"
    DWORD probe = fOkToBeSlow ? total : min(total, 8u);

    Microsoft::WRL::ComPtr<IEnumShellItems> items;
    if (FAILED(psia->EnumItems(&items))) return S_OK;

    for (DWORD i = 0; i < probe; ++i) {
        Microsoft::WRL::ComPtr<IShellItem> item;
        if (items->Next(1, &item, nullptr) != S_OK) break;
        SFGAOF attrs = SFGAO_FILESYSTEM;
        if (SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM, &attrs)) &&
            (attrs & SFGAO_FILESYSTEM)) {
            *pstate = ECS_ENABLED;             // 命中即返回，不做全量扫描
            return S_OK;
        }
    }
    return S_OK;
}
```

为什么用 `ECS_HIDDEN` 而不是 `ECS_DISABLED`：用户不想在"此电脑""网络""回收站""桌面空白"上看到一个灰掉的项。灰项只会让人怀疑程序坏了。

为什么不做"选中项里有不可改名的就禁用"：`GetState` 里逐项判断必然要碰 `GetDisplayName` 或属性递归，性能不可控。真正的过滤放在 `Invoke` 和主程序里做（§5.4 / §8.7），那里慢一点没关系。

### 5.4 Invoke：取路径 + 拉起主程序

```cpp
IFACEMETHODIMP BatchRenameCommand::Invoke(IShellItemArray* psia, IBindCtx*) noexcept
try {
    std::vector<std::wstring> paths;
    if (psia) CollectPaths(psia, paths);

    if (paths.empty()) {
        // 能走到这里说明 GetState 误判了（选中项全是虚拟对象）。只写日志不弹框：
        // 对"库/网络位置"右键弹一个错误框只会让用户困惑。
        AppendLog(L"empty selection", 0);
        return S_FALSE;
    }

    DWORD err = 0;
    if (!LaunchUI(paths, &err)) {
        ReportLaunchFailure(err);       // ★ 必须让用户看见，否则表现为"点了没反应"
        return E_FAIL;
    }
    return S_OK;
} catch (...) {
    ReportLaunchFailure(E_UNEXPECTED);
    return E_FAIL;                      // dllhost 里绝不能有未捕获异常
}
```

**失败必须可见**：主程序起不来时（exe 被 AV 拦、路径错、依赖缺失），用户看到的是"点了菜单什么都没发生"，会认定程序坏了。所以 `Invoke` 失败时同时做两件事：

```cpp
void ReportLaunchFailure(DWORD code) {
    AppendLog(L"launch failed", code);          // ① 落盘，便于事后排查

    // ② 弹框，但绝不能在 STA 线程上弹
    //    MessageBox 会阻塞 dllhost 的 STA，导致后续右键调用一直排队（菜单看起来"卡死"）
    std::thread([code]() {
        wchar_t msg[512];
        swprintf_s(msg,
            L"批量重命名启动失败。\n\n错误码：0x%08X\n"
            L"日志：%%LOCALAPPDATA%%\\BatchRename\\logs\\shell.log\n\n"
            L"常见原因：主程序被安全软件拦截、bin\\publish 被移动或删除。", code);
        MessageBoxW(nullptr, msg, L"批量重命名",
                    MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
    }).detach();
}
```

两个细节，都是踩过才知道的：

1. **弹框要开在临时线程上**。`MessageBox` 在 dllhost 的 STA 线程上会阻塞整个 STA，用户不点掉，下一次右键的 `GetState` 就得排队——现象是"菜单转圈"。
2. **`AppendLog` 只能是尽力而为**：`%LOCALAPPDATA%\BatchRename\logs\shell.log` 追加一行 UTF-8（时间 + 代码 + `HRESULT`），写失败就 `OutputDebugString` 后忽略。日志 IO 放在 `Invoke` 里可以，**绝不可**放进 `GetState`。

`LaunchUI` 的签名相应改为 `bool LaunchUI(const std::vector<std::wstring>& paths, DWORD* lastError)`，`CreateProcessW` 失败时把 `GetLastError()` 带出来。

**CollectPaths：过滤 + 取物理路径**

```cpp
void CollectPaths(IShellItemArray* psia, std::vector<std::wstring>& out) {
    Microsoft::WRL::ComPtr<IEnumShellItems> items;
    if (FAILED(psia->EnumItems(&items))) return;

    DWORD total = 0; psia->GetCount(&total);
    out.reserve(total);

    Microsoft::WRL::ComPtr<IShellItem> item;
    while (items->Next(1, &item, nullptr) == S_OK) {
        SFGAOF attrs = SFGAO_FILESYSTEM;
        if (SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM, &attrs)) &&
            (attrs & SFGAO_FILESYSTEM)) {
            PWSTR p = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p) {
                out.emplace_back(p);
                CoTaskMemFree(p);
            }
        }
        item.Reset();
    }
}
```

要跳过的：库（`SFGAO_FILESYSTEM` 不命中）、OneDrive 云端占位符（可能命中但 `GetDisplayName` 失败）、`\\?\` 前缀路径（可保留，`SHCreateItemFromParsingName` 能吃）、zip 内条目（不命中）。**一律静默跳过，不弹任何 UI**——菜单路径上弹框是灾难。

**LaunchUI：管道 + 句柄白名单 + 后台写线程**

```cpp
bool LaunchUI(const std::vector<std::wstring>& paths) {
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, TRUE };   // 可继承
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    if (!CreatePipe(&readEnd, &writeEnd, &sa, 1u << 20)) return false;  // 1MB 缓冲
    // 🔧 v1.1 更正（致命）：v1.0 写的是 SetHandleInformation(readEnd, ...)，这是错的。
    //   PROC_THREAD_ATTRIBUTE_HANDLE_LIST 白名单里的句柄必须保留 HANDLE_FLAG_INHERIT，
    //   而 readEnd 正是要给子进程的那一端。清掉它的继承标志再放进白名单，
    //   CreateProcess 必然返回 ERROR_INVALID_PARAMETER(87) → 用户看到"启动失败"弹框。
    //   要清的是父进程自己持有的 writeEnd。
    SetHandleInformation(writeEnd, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOEXW si{};
    si.StartupInfo.cb = sizeof(si);
    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    si.StartupInfo.hStdInput  = readEnd;
    si.StartupInfo.hStdOutput = nullptr;
    si.StartupInfo.hStdError  = nullptr;

    SIZE_T attrSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attrSize);
    std::vector<BYTE> attrBuf(attrSize);
    auto attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrBuf.data());
    if (!InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize)) { /* cleanup; return false */ }
    // ★ 白名单：子进程只继承 readEnd，杜绝句柄泄漏到长命进程
    if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                   &readEnd, sizeof(HANDLE), nullptr, nullptr)) { /* cleanup */ }
    si.lpAttributeList = attrs;

    std::wstring exe = ResolveAppPath();
    std::wstring cmd = L"\"" + exe + L"\"";
    PROCESS_INFORMATION pi{};
    BOOL ok = CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr,
                             TRUE,                                  // bInheritHandles
                             EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                             nullptr, nullptr, &si.StartupInfo, &pi);
    DeleteProcThreadAttributeList(attrs);
    CloseHandle(readEnd);          // 父进程不再需要
    if (!ok) { CloseHandle(writeEnd); return false; }

    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);

    // 写端交给 detached 线程：万级文件时绝不能阻塞 STA 线程
    auto payload = std::make_shared<std::vector<std::wstring>>(paths);
    Module<InProc>::GetModule().IncrementObjectCount();   // 线程在跑，DLL 不许卸载
    std::thread([payload, writeEnd]() {
        for (const auto& p : *payload) {
            size_t bytes = p.size() * sizeof(wchar_t);
            DWORD written = 0;
            WriteFile(writeEnd, p.data(), (DWORD)bytes, &written, nullptr);
            wchar_t nul = L'\0';
            WriteFile(writeEnd, &nul, sizeof(wchar_t), &written, nullptr);
        }
        CloseHandle(writeEnd);     // 关闭即 EOF，子进程结束读取
        Module<InProc>::GetModule().DecrementObjectCount();
    }).detach();

    return true;
}
```

> 🔧 **v1.1 补记：日志的真实落点**
>
> v1.0 说 `AppendLog` 写 `%LOCALAPPDATA%\BatchRename\logs\shell.log`。但 dllhost 在**包容器**身份下运行，`LOCALAPPDATA` 写入会被文件虚拟化重定向，实际落在：
>
> ```
> C:\Users\<user>\AppData\Local\Packages\BatchRename.Shell_<publisherhash>\LocalCache\Local\BatchRename\logs\shell.log
> ```
>
> 排查时如果去 `%LOCALAPPDATA%\BatchRename` 找不到日志，会误判成"日志没写"。稳妥实现：`GetEnvironmentVariableW(L"LOCALAPPDATA")` 取不到时回退 `GetTempPathW`。

**ResolveAppPath：零 IO 定位 exe**

```cpp
static std::wstring ResolveAppPath() {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ResolveAppPath), &self);
    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW(self, buf, MAX_PATH);
    std::wstring dir(buf);
    dir = dir.substr(0, dir.find_last_of(L"\\/") + 1);
    return dir + L"BatchRename.App\\BatchRename.App.exe";
}
```

为什么用路径推导而不是注册表/环境变量：菜单路径上任何 IO（含注册表）都可能被 Defender 或慢盘拖到几百毫秒。

### 5.5 生命周期

| 事项 | 规则 |
|---|---|
| 线程模型 | 清单里 `ThreadingModel="STA"`，**不要自己 `CoInitializeEx`**（会 `RPC_E_CHANGED_MODE`，且 dllhost 已初始化） |
| 并发 | 同一 STA 线程串行调用，成员变量（`m_site`）不需要锁 |
| DLL 卸载 | 写线程运行时必须 `IncrementObjectCount()`；`DllCanUnloadNow` 会返回 `S_FALSE` |
| 进程存活 | dllhost 会缓存我们一段时间（约 5 分钟），期间改 DLL 文件会被占用 → 见 §11 排障 |

### 5.6 异常与调试边界

- `Invoke` 外层 `try/catch(...)`：dllhost 里的未捕获异常不会给你弹框，只会让资源管理器假死或重启，且没有任何提示。
- 调试手段优先级：
  1. `BatchRename.ShellTest.exe` 直接 `CoCreateInstance` 调 `Invoke`（能断点、能改代码即可重编，不用重启 explorer）；
  2. `OutputDebugStringW(L"[BatchRename] ...")` + DebugView/DebugView++；
  3. `tasklist /m BatchRename.Shell.dll` 确认 DLL 到底有没有被加载；
  4. 最后才是附加到 `dllhost.exe`（要挑对那个 `-Embedding` 的实例）。

### 5.7 验证清单（M1 必做）

```powershell
dumpbin /exports .\bin\publish\BatchRename.Shell.dll | Select-String 'DllGetClassObject|DllCanUnloadNow'
dumpbin /headers .\bin\publish\BatchRename.Shell.dll | Select-String 'machine \('
# 期望：machine (x64)
```

- [ ] 三个导出都在：`DllGetClassObject`、`DllCanUnloadNow`、`DllGetActivationFactory`
- [ ] machine = x64
- [ ] `ShellTest.exe` 能 `CoCreateInstance` 成功并打出 3 条路径
- [ ] 右键单个文件，菜单项出现
- [ ] 右键"此电脑"/"网络"/桌面空白，菜单项**不**出现

---

## 6. 打包与注册

### 6.1 外部位置布局

稀疏包不含二进制，只含清单与身份。外部位置根目录必须让三个东西并列：

```
bin\publish\                     ← 外部位置根（-ExternalLocation 指向这里）
├─ BatchRename.Shell.dll         ← com:Class/@Path 相对此根
├─ Assets\
│  ├─ StoreLogo.png
│  ├─ Square44x44Logo.png
│  └─ Square150x150Logo.png
└─ BatchRename.App\
   ├─ BatchRename.App.exe
   ├─ Microsoft.ui.xaml.dll
   ├─ Bootstrap.dll
   ├─ resources.pri
   └─ ...（self-contained 的其余文件）
```

`packaging\AppxManifest.xml` 可以留在源码目录，`-Register` 时给绝对路径。**清单里的所有 Path 都相对外部位置根，不是相对清单文件。**

### 6.2 AppxManifest.xml 全文

> 🔧 **本段与 `packaging/AppxManifest.xml` 逐字一致，由 `tools/Verify-Docs.ps1` 校验。**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
    xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
    xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
    xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
    xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
    xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5"
    xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
    xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
    IgnorableNamespaces="uap uap10 desktop4 desktop5 com rescap">

  <Identity Name="BatchRename.Shell"
            Publisher="CN=BatchRenameDev"
            Version="1.0.0.0"
            ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>批量重命名</DisplayName>
    <PublisherDisplayName>BatchRename</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop"
                        MinVersion="10.0.22621.0"
                        MaxVersionTested="10.0.26300.0" />
  </Dependencies>

  <Resources>
    <Resource Language="zh-CN" />
    <Resource Language="en-US" />
  </Resources>

  <Applications>
    <Application Id="App"
                 Executable="BatchRename.App\BatchRename.App.exe"
                 uap10:TrustLevel="mediumIL"
                 uap10:RuntimeBehavior="win32App">
      <uap:VisualElements
          DisplayName="批量重命名"
          Description="批量重命名文件与文件夹（PowerRename 风格）"
          BackgroundColor="transparent"
          AppListEntry="none"
          Square150x150Logo="Assets\Square150x150Logo.png"
          Square44x44Logo="Assets\Square44x44Logo.png" />

      <Extensions>
        <!-- ① COM 服务器：SurrogateServer = 由 dllhost 代理承载，不注入 explorer -->
        <com:Extension Category="windows.comServer">
          <com:ComServer>
            <com:SurrogateServer DisplayName="BatchRename Context Menu">
              <com:Class Id="A1B2C3D4-1111-4A2B-9C3D-000000000001"
                         Path="BatchRename.Shell.dll"
                         ThreadingModel="STA" />
            </com:SurrogateServer>
          </com:ComServer>
        </com:Extension>

        <!-- ② 命令注册：文件 与 选中文件夹 各一条 -->
        <desktop4:Extension Category="windows.fileExplorerContextMenus">
          <desktop4:FileExplorerContextMenus>
            <desktop5:ItemType Type="*">
              <desktop5:Verb Id="BatchRenameFiles"
                             Clsid="A1B2C3D4-1111-4A2B-9C3D-000000000001" />
            </desktop5:ItemType>
            <desktop5:ItemType Type="Directory">
              <desktop5:Verb Id="BatchRenameFolders"
                             Clsid="A1B2C3D4-1111-4A2B-9C3D-000000000001" />
            </desktop5:ItemType>
          </desktop4:FileExplorerContextMenus>
        </desktop4:Extension>
      </Extensions>
    </Application>
  </Applications>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <rescap:Capability Name="unvirtualizedResources" />
  </Capabilities>
</Package>
```

### 6.3 字段规则表

| 字段 | 规则 | 踩错的现象 |
|---|---|---|
| **CLSID 三处一致** | `com:Class/@Id` = `desktop5:Verb/@Clsid` = C++ `DECLSPEC_UUID` | 菜单**静默不出现**，零报错，事件日志也没有。这是本项目第一大坑 |
| `Identity/@Name` | 建议含点号（`BatchRename.Shell`） | 格式不合规报 `0x80080204` |
| `Identity/@Publisher` | 参与包身份计算，必须与签名证书 Subject 一致（免签时任意值但**不能改**） | 改了再注册 → `0x80073CFB`（已存在不同包） |
| `Identity/@Version` | 四段数字，升级必须递增 | 同版本重注册可能被拒 |
| `Identity/@ProcessorArchitecture` | `x64` | 与 DLL 位数不一致 → `0x800700C1` |
| `com:Class/@Path` | 相对**外部位置根** | 写成 `..\bin\publish\...` 直接失败 |
| `ThreadingModel` | `STA` | 写成 `Both` 会与 dllhost 的 STA 冲突 `[判断]` |
| `TargetDeviceFamily/@MinVersion` | `10.0.22621.0`（Win11 22H2） | 太高会让旧机装不上；写 `19041` 会让 Win10 也"装得上"但没有新式菜单，实测坑。新式菜单项本身要 26H2（26300），安装器对这档只警告、不拦 |
| `MaxVersionTested` | 填当前系统 build，如 `10.0.26300.0` | 低于系统版本时某些扩展点可能不激活 |
| `uap10:AllowExternalContent` | 必须在 `<Properties>` 下 | 放错位置报 `0xC00CE508`（XML schema 校验失败） |
| `uap10:TrustLevel` / `RuntimeBehavior` | 必须挂在 `<Application>` 上 | 缺了 `RuntimeBehavior` 会当 UWP 应用处理，启动失败 |
| `VisualElements/@Description` | 必须写 | 漏写报 `0x80080204` |
| `AppListEntry` | 建议 `none` | 否则开始菜单多一个空壳条目（主程序是"无参启动=拖放模式"，其实留着也行） |
| `Directory\Background` | **不注册** | 背景右键时 `psia` 为空，命令永远 `ECS_HIDDEN`，注册了纯误导 |

### 6.4 安装脚本 `tools\Install-Sparse.ps1` 全文

> 🔧 **本段与 `tools/Install-Sparse.ps1` 逐字一致，由 `tools/Verify-Docs.ps1` 校验**（本地 `powershell -File tools/Verify-Docs.ps1`，CI 每次构建都跑）。
> 改脚本后不同步本段，校验会直接失败并打印差异——v1.5 时就出现过「脚本 159 行、文档还停在 79 行」的漂移。

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
    注册 BatchRename 稀疏包（免签 / 开发者模式路线）。
.NOTES
    必须以普通用户运行；不要用管理员（包注册在任何 IL 都可以）。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$PackageName = 'BatchRename.Shell',
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = 'Stop'

# 注意：不能在 param 默认值里写 (Split-Path -Parent $PSScriptRoot) ——
# PowerShell 5.1 求值默认值时自动变量尚未初始化，会得到空串并在 Split-Path 上抛错。
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$manifestPath = Join-Path $RepoRoot 'packaging\AppxManifest.xml'
$externalPath = Join-Path $RepoRoot 'bin\publish'

if (-not (Test-Path $manifestPath)) { throw "Manifest not found: $manifestPath" }
if (-not (Test-Path $externalPath)) { throw "Publish folder not found: $externalPath (run dotnet publish first)" }

# -ExternalLocation 必须是绝对路径，且不支持 UNC
$externalPath = (Resolve-Path $externalPath).Path
$manifestPath = (Resolve-Path $manifestPath).Path

# --- 0. 三处 CLSID 一致性自检（省掉 80% 的"菜单不出现"排障） ---
# 注意：AppxManifest.xml 是无 BOM 的 UTF-8。Windows PowerShell 5.1 的 Get-Content
# 默认按 ANSI（本机 GBK）解码，会把中文 DisplayName 读成乱码并吃掉引号，
# 导致 [xml] 转换直接抛异常、脚本停在第 28 行。必须显式指定 -Encoding UTF8。
[xml]$manifest = Get-Content $manifestPath -Raw -Encoding UTF8
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('f',  'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('com','http://schemas.microsoft.com/appx/manifest/com/windows10')
$ns.AddNamespace('d5', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/5')

$classIds = @($manifest.SelectNodes('//com:Class', $ns) | ForEach-Object { $_.Id })
$verbIds = @($manifest.SelectNodes('//d5:Verb', $ns)  | ForEach-Object { $_.Clsid })

if ($classIds.Count -eq 0) { throw 'Manifest has no com:Class' }
foreach ($v in $verbIds) {
    if ($classIds -notcontains $v) { throw "Verb Clsid '$v' does not match any com:Class Id" }
}

$headerPath = Join-Path $RepoRoot 'src\BatchRename.Shell\ExplorerCommand.h'
if (Test-Path $headerPath) {
    $header = Get-Content $headerPath -Raw -Encoding UTF8
    foreach ($id in $classIds) {
        if ($header -notmatch [regex]::Escape($id)) {
            throw "CLSID $id not found in ExplorerCommand.h (DECLSPEC_UUID must match manifest)"
        }
    }
    Write-Host "[ok] CLSID consistent across manifest and header" -ForegroundColor Green
}

# --- 1. 旧包处理：身份或**安装位置**变了都必须先卸载（否则 0x80073D0B / 0x80073CFB） ---
$newPublisher = $manifest.Package.Identity.Publisher
$newName      = $manifest.Package.Identity.Name
$manifestDir  = [IO.Path]::GetFullPath((Split-Path -Parent $manifestPath)).TrimEnd('\')

# 按前缀找历史安装：Identity 改名后旧包的名字不同，只按当前名找会漏掉
$existing = @(Get-AppxPackage -Name 'BatchRename*' -ErrorAction SilentlyContinue)
foreach ($old in $existing) {
    $needRemove = $false
    $reason = ''

    if ($old.Publisher -ne $newPublisher -or $old.Name -ne $newName) {
        $needRemove = $true
        $reason = "身份不一致 (Name: $($old.Name) -> $newName ; Publisher: $($old.Publisher) -> $newPublisher)"
    }
    elseif ($old.InstallLocation) {
        # 稀疏包绑死绝对路径：从 A 目录装过的包不能直接从 B 目录重注册（0x80073D0B）
        $oldDir = [IO.Path]::GetFullPath($old.InstallLocation).TrimEnd('\')
        if ($oldDir -ne $manifestDir) {
            $needRemove = $true
            $reason = "安装位置变了 ($oldDir -> $manifestDir)"
        }
    }

    if (-not $needRemove) { continue }

    Write-Host "[warn] 先卸载旧包：$reason" -ForegroundColor Yellow

    # 卸载前必须释放占用：App 进程 + 加载了 Shell DLL 的 dllhost，
    # 否则 Remove-AppxPackage 报 0x80073D02（包正在使用中）
    Get-Process -Name 'BatchRename.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name dllhost -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Modules | Where-Object { $_.ModuleName -like 'BatchRename*' }) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 500

    Remove-AppxPackage -Package $old.PackageFullName -ErrorAction Stop
    Write-Host "[ok] 已卸载 $($old.PackageFullName)" -ForegroundColor Green
}

# --- 1.5 同步图标资产到运行时目录 ---
# 右键菜单的图标由 Shell DLL 按 ModuleDir()\Assets\menu.ico 读取，
# 而 ModuleDir 是 -ExternalLocation 指向的 bin\publish，不是 packaging\。
# 以前这里没有拷贝步骤，所以换了 packaging\Assets 里的图标后菜单一直是旧的。
$assetSource = Join-Path $RepoRoot 'packaging\Assets'
$assetTarget = Join-Path $externalPath 'Assets'
if (Test-Path $assetSource) {
    New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
    Copy-Item -Path (Join-Path $assetSource '*') -Destination $assetTarget -Recurse -Force
    $count = @(Get-ChildItem $assetTarget -File).Count
    Write-Host "[ok] Assets synced -> $assetTarget ($count files)" -ForegroundColor Green
} else {
    Write-Host "[warn] packaging\Assets not found, menu icon may stay stale" -ForegroundColor Yellow
}

# --- 2. 注册 ---
Add-AppxPackage -Register $manifestPath -ExternalLocation $externalPath
$pkg = Get-AppxPackage -Name $PackageName
Write-Host "[ok] Registered $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host "     Status: $($pkg.Status)"

# --- 3. SendTo 快捷方式（§10.2 入口②：不受 26H2 新菜单折叠影响，多选也能一次传入全部路径） ---
$exe = Join-Path $externalPath 'BatchRename.App\BatchRename.App.exe'
if (Test-Path $exe) {
    $sendTo = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\SendTo'
    if (-not (Test-Path $sendTo)) { New-Item -ItemType Directory -Path $sendTo -Force | Out-Null }
    $lnkPath = Join-Path $sendTo '批量重命名.lnk'

    $wsh = New-Object -ComObject WScript.Shell
    $sc  = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath       = $exe
    $sc.WorkingDirectory = Split-Path $exe -Parent
    $sc.IconLocation     = "$exe,0"
    $sc.Description      = '批量重命名（多选可一次传入全部路径）'
    $sc.Save()
    Write-Host "[ok] SendTo: $lnkPath" -ForegroundColor Green
} else {
    Write-Host "[warn] 找不到 $exe，跳过 SendTo 快捷方式" -ForegroundColor Yellow
}

# --- 4. 重启 Explorer（唯一可靠的让 Shell 重新加载 Verb 表的办法） ---
if (-not $SkipExplorerRestart) {
    Write-Host "[..] Restarting explorer.exe"
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
}

Write-Host ''
Write-Host 'Done. Right-click a file in Explorer.' -ForegroundColor Cyan
Write-Host 'If the item is missing, check:' -ForegroundColor DarkGray
Write-Host '  - 26H2 puts app extensions under "App extensions" submenu by default' -ForegroundColor DarkGray
Write-Host '  - Settings > Personalization > Context menu to promote it' -ForegroundColor DarkGray
Write-Host '  - 兜底入口：右键 > 发送到 > 批量重命名（不受新菜单折叠影响）' -ForegroundColor DarkGray
```

> 注意读取清单做自检时，`Get-Content -Raw` **必须加 `-Encoding UTF8`**，否则中文会被按 ANSI 解码，导致 `[xml]` 转换报「开始标记与结束标记不匹配」。
### 6.5 卸载脚本 `tools\Uninstall.ps1`

```powershell
param([string]$PackageName = 'BatchRename.Shell', [switch]$SkipExplorerRestart)

$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if (-not $pkg) { Write-Host 'Not installed.'; return }

# 注意：dllhost.exe 可能还持着 BatchRename.Shell.dll 的句柄，导致 bin\publish 删不掉。
# 不要为了解锁去 kill 所有 dllhost（会连带杀掉其它 COM 代理）。正确做法：
# 先彻底重启 explorer（下面的步骤），再关掉我们自己的 App 进程，最后再删目录。
Get-Process -Name BatchRename.App -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-AppxPackage -Package $pkg.PackageFullName
Write-Host "[ok] Removed $($pkg.PackageFullName)"

if (-not $SkipExplorerRestart) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
}
Write-Host 'Note: bin\publish is NOT deleted. Remove it manually if you want a clean slate.'
```

### 6.6 免签 vs 自签名证书

| 路线 | 前提 | 命令 | 适用 |
|---|---|---|---|
| **免签（推荐开发期）** | 设置 > 系统 > 开发者选项 > 开发人员模式 = 开 | `Add-AppxPackage -Register ... -ExternalLocation ...` | 自己的机器，迭代用 |
| 自签名证书 | 生成 .pfx + .cer；.cer 必须同时进 `LocalMachine\Root` 和 `LocalMachine\TrustedPeople` | `signtool sign /fd SHA256 /f dev.pfx /p <pwd> BatchRename.msix` 后 `Add-AppxPackage` | 关掉开发者模式仍要用、或装到第二台机器 |
| 商用证书 | 需要 EV/OV 代码签名证书 | 同上 | 本项目不需要 |

`New-DevCert.ps1` 要点（不展开全文）：

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=BatchRenameDev" `
    -KeyUsage DigitalSignature -FriendlyName "BatchRename Dev" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
# 导出 pfx/cer 后：Import-Certificate -FilePath dev.cer -CertStoreLocation Cert:\LocalMachine\Root
#                                                                 Cert:\LocalMachine\TrustedPeople
```

⚠️ `Subject` 必须与清单的 `Publisher` **逐字符一致**（含大小写与空格）。

### 6.7 什么时候必须重启 Explorer

| 改动 | 要重启吗 | 原因 |
|---|---|---|
| 改了 DLL 里的代码（路径/CLSID 不变） | 不一定要，但建议 | dllhost 会缓存已加载的 DLL；改文件前先 `Stop-Process -Name dllhost`（会连带杀掉别的 COM 代理）`[判断]` |
| 新增/删除 Verb、改 Verb Id | **必须** | Shell 在启动时把 Verb 表载入内存 |
| 改 CLSID | **必须** | 旧的 CLSID→DLL 映射被缓存 |
| 改主程序 exe（换图标/改标题） | 不必 | 每次都是新进程 |
| 只改 Assets 图片 | 不必（但要清图标缓存 `ie4uinit.exe -show`） | — |

---

## 7. 主程序规格

### 7.1 csproj 关键属性

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <RootNamespace>BatchRename.App</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>

    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>false</EnableMsixTooling>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <SelfContained>false</SelfContained>

    <Nullable>enable</Nullable>
    <PublishSingleFile>false</PublishSingleFile>
    <PublishTrimmed>false</PublishTrimmed>
    <PublishAot>false</PublishAot>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.6.4" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.*" />
    <PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <!-- 🔧 v1.2 更正：CsWin32 的输入清单必须挂 AdditionalFiles。
         写成 <None Include> 源生成器读不到，等于没配，编译时不报错但 API 全部缺失。 -->
    <AdditionalFiles Include="Core\NativeMethods.txt" />
  </ItemGroup>

  <!--
    🔧 v1.1 新增（必需）：WinUI 3 非打包应用运行时在 exe 同目录固定查找 resources.pri。
    WinAppSDK 2.6.4 的 publish 只产出 <AssemblyName>.pri 且不会重命名，
    缺了 resources.pri 会在 Microsoft.UI.Xaml.dll 里崩成 0xC000027B
    —— 现象是"点了菜单，加载一会儿，什么窗口都没有"。
  -->
  <Target Name="EnsureResourcesPri" AfterTargets="Publish"
          Condition="Exists('$(TargetDir)$(AssemblyName).pri')">
    <Copy SourceFiles="$(TargetDir)$(AssemblyName).pri"
          DestinationFiles="$(PublishDir)resources.pri"
          OverwriteReadOnlyFiles="true" />
    <Message Importance="high" Text="[PRI] $(AssemblyName).pri -> $(PublishDir)resources.pri" />
  </Target>
</Project>
```

**每个反直觉属性的反面后果**：

| 属性 | 不这么设的后果 |
|---|---|
| `WindowsPackageType=None` | 稀疏包给的 exe 没有 `Package.Current`，WinAppSDK 不注入 bootstrap → 首启 `REGDB_E_CLASSNOTREG (0x80040154)` |
| `EnableMsixTooling=false` | 启用单项目 MSIX 工具链，`dotnet publish` 报 `APPX0104/APPX0108`，或生成自己的清单覆盖你的 |
| `WindowsAppSDKSelfContained=true` | 机器上必须装 WinAppSDK Runtime，否则启动即闪退 |
| `SelfContained=false` | 若设 true，输出从 ~30MB 涨到 ~150MB+.NET 部分框架依赖更划算 |
| `RuntimeIdentifier` 必须显式 | 缺了它 self-contained 的 WinAppSDK 会找不到 `Microsoft.ui.xaml.dll` |
| `PublishSingleFile=false` | WinUI 3 不支持单文件，`resources.pri` / `Microsoft.ui.xaml.dll` 必须是真实文件 → `0xc000027b` 启动即崩 |
| `PublishTrimmed=false` | XAML 靠反射解析类型，裁剪切掉 UI 类型 → `XamlParseException` |
| `PublishAot=false` | CsWinRT 的 COM 互操作与 AOT 不兼容 |
| **`resources.pri` 必须与 exe 同目录** | 🔧 v1.1：WinAppSDK 2.6.4 的 publish **不生成** `resources.pri`（只产出 `<AssemblyName>.pri`），缺它启动即 `0xC000027B`。用上面 `EnsureResourcesPri` Target 兜住 |

### 7.2 体积与依赖三档

| 组合 | 输出体积 | 目标机依赖 | 建议 |
|---|---|---|---|
| .NET 自包含 + WinAppSDK 自包含 | ~120–180MB | 无 | 最省事，但违背"轻量" |
| **.NET 框架依赖 + WinAppSDK 自包含** | **~30MB** | 一次装 .NET 10 Desktop Runtime | ✅ 采用（你自己机器本来就有） |
| 全框架依赖 | ~8MB | .NET Runtime + WinAppSDK Runtime | 最轻，但缺 Runtime 时闪退且提示不友好 |

内存实话说：WinUI 3 常驻 ~80–120MB，比原生方案（~20MB）重得多。这是换平台观感的代价。体积用上面的组合压；启动速度靠"少控件 + 分批渲染 + 首屏只渲染前 200 行"，**不靠抠框架，也不靠常驻预热**（无常驻形态下没有热复用，见 §7.7）。

### 7.3 启动链路

```csharp
// App.xaml.cs 的 OnLaunched 顺序不能变
protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    var payload = ShellPayload.ReadFromStandardInput();   // ① 先读数据（同步，几十 ms）

    if (!ShellPayload.TryAcquireSingleInstance())          // ② 抢锁
    {
        ShellPayload.ForwardToExistingInstance(payload);   // ③ 转发 + 置前 + AllowSetForegroundWindow
        Environment.Exit(0);                               // ④ 立刻退出，不要建窗口
    }

    _window = new MainWindow(payload);                     // ⑤ 才有窗口
    _window.Activate();
    ShellPayload.StartActivationServer(_window);           // ⑥ 起命名管道监听
}
```

顺序错了的后果：先建窗口再读 stdin → 用户看到一个空白窗口闪一下；先建窗口再判单实例 → 每次右键多一个窗口。

**stdin 解析**：

```csharp
public static List<string> ReadFromStandardInput()
{
    var result = new List<string>();
    var stdin = Console.OpenStandardInput();
    using var reader = new StreamReader(
        stdin,
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        detectEncodingFromByteOrderMarks: false);

    var sb = new StringBuilder(260);
    int ch;
    while ((ch = reader.Read()) >= 0)
    {
        if (ch == 0) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
        else sb.Append((char)ch);
    }
    if (sb.Length > 0) result.Add(sb.ToString());
    return result;
}
```

要点：`UnicodeEncoding(false, false)` —— 不能让它尝试读 BOM，否则第一条路径首字符可能被吞。

**转发 + 置前**（第二实例要做的事）：

```csharp
public static void ForwardToExistingInstance(IReadOnlyList<string> paths)
{
    // 关键：让**我们**（刚被用户点击启动的前台进程）把置前权转交给首实例
    AllowSetForegroundWindow(firstInstancePid);   // Win32: user32.dll
    using var pipe = new NamedPipeClientStream(".", "BatchRename.Activate", PipeDirection.Out);
    pipe.Connect(1500);
    using var w = new BinaryWriter(pipe, Encoding.Unicode);
    w.Write(paths.Count);
    foreach (var p in paths) w.Write(p);
}
```

不调 `AllowSetForegroundWindow` 的话，`SetForegroundWindow` 在首实例里大概率被系统拒绝，窗口只在任务栏闪一下。

### 7.4 UI 布局

单窗口，纵向四段：

| 区域 | 控件 | 备注 |
|---|---|---|
| 标题栏 | `ExtendsContentIntoTitleBar=true` + 自定义 `Grid` + `SetTitleBar()` | 与 Mica 融合，不留白条 |
| 规则栏（2 行） | 行 1：查找 `TextBox` + 替换 `TextBox` + 大小写敏感 `ToggleButton` + 正则 `ToggleSwitch`；行 2：作用域 `ComboBox`（文件名/扩展名/两者）+ 大小写转换 `ComboBox`（不转换/全大写/全小写/每词首字母大写） | 控件全部 `x:Bind` 双向到 code-behind 属性 |
| 预览区 | 表头 `Grid`（4 列：勾选 24 / 原名 * / 新名 * / 状态 110）+ `ListView`（`ItemsSource`，`ItemTemplate` 用相同列宽比例） | 表头与行模板列宽必须用**同样的固定像素 + 同样的星号比例**，WinUI 无 SharedSizeGroup |
| 底栏 | 左：`共 N 项 · 将改名 M 项 · K 项冲突`；右：`全选/全不选` `撤销上次` `执行` | 有冲突时左侧变红 + 执行按钮禁用 |

背景：

```xml
<Window.SystemBackdrop><MicaBackdrop Kind="BaseAlt" /></Window.SystemBackdrop>
```

`ListView` 的性能红线：

- 行模板只用**叶子元素**（`TextBlock`/`FontIcon`/`Border`），**禁止**任何模板化控件（`CheckBox`/`Button`/`ToggleSwitch`）——实测 5000 项时一个行内 `CheckBox` 独占 46–49MB（§7.8.7）；
- **绝不能**把 `ListView` 塞进 `ScrollViewer`（会立刻关掉虚拟化，5000 项直接卡死）；
- **行内要能点，就走 `ListView.ItemClick` + `PointerPressed` 记 x**：`ListViewItem` 会吃掉子元素的 `Tapped`/`PointerPressed`（必须 `handledEventsToo: true` 注册），`ItemClick` 又不给坐标；两者配合才能做"只有勾选列可切换，其它位置只选中"；
- **不给每行绑 `ToolTip`**（会创建 5000 个 flyout）；
- **不做逐行文件图标**：每项要走一次 `SHGetFileInfo`（1–3ms），5000 项多 5–15 秒，还要多占几十 MB 图标缓存。行内只用文字状态（§7.8.3）。

### 7.4.1 两条 WinUI 3 工程约束（🔧 v1.1 新增）

v1.0 未提及，实测踩到：

**① `Window` 作为 XAML 根时不支持 `x:Bind`**

`Window` 不是 `FrameworkElement`，XAML 编译器生成的 `MainWindow.g.cs` 会报：

```
error CS1503: 参数 1: 无法从 "BatchRename.App.MainWindow" 转换为 "Microsoft.UI.Xaml.FrameworkElement"
```

`DataTemplate` 里用 `x:Bind` 还会连带触发 `WMC0909 Cannot resolve DataType` 和 `WMC1111 DataTemplates containing x:Bind need a DataType`。

三种解法，按成本从低到高：

| 方案 | 做法 | 代价 |
|---|---|---|
| **改用 `{Binding}`**（项目当前采用） | `DataTemplate` 去掉 `x:DataType`，绑定全部换成 `{Binding}` | ListView 有 UI 虚拟化，只渲染可见项，运行时绑定开销可忽略 |
| 抽 `UserControl` | `Window` 内容只放一个 `UserControl`，UI 全部搬进去 | 结构更正统，`x:Bind` 编译期检查保留，但要拆文件 |
| 用 `Page` 承载 | 同上，`Frame` + `Page` | 单窗口工具属于过度设计 |

**② `Color` 在 `Windows.UI` 命名空间**

```csharp
using Windows.UI;              // Color
using Microsoft.UI.Xaml.Media; // SolidColorBrush
```

只写 `using Microsoft.UI;` 会得到 `CS0103: 当前上下文中不存在名称 "Color"`（`Microsoft.UI` 里是 `Colors` 静态类，不是 `Color` 结构）。

### 7.5 键盘

| 键 | 行为 |
|---|---|
| `Enter` | 执行（有冲突时无效） |
| `Esc` | 关闭窗口（有未执行改动时二次确认） |
| `Ctrl+Z` | 撤销上一批 |
| `Ctrl+A` | 全选行 |
| `Space` | 切换当前行勾选 |
| `Up/Down` | 移动选中行（默认 ListView 行为，不要拦） |

### 7.6 单实例与激活协议

| 项 | 值 |
|---|---|
| Mutex 名 | `Local\BatchRename.SingleInstance.{PackageFamilyName}` —— 带包身份避免和别的实例串 |
| 管道名 | `BatchRename.Activate.{ProcessId}`，名称写入 `%LOCALAPPDATA%\BatchRename\active.pid` |
| 协议 | `int32 count` + `count × (string)`（`BinaryWriter` 默认：长度前缀 + UTF-8）。第二实例用 `BinaryReader` 读 |
| 超时 | 连接 1500ms，读写 3000ms；超时则退化为"新开窗口"而不是失败 |
| 首实例收到数据 | `DispatcherQueue.TryEnqueue` → 替换 `ItemsSource` → `Activate()` + `SetForegroundWindow` |
| 进程残留 | 窗口关闭即 `Environment.Exit(0)`，无常驻（见 §7.7） |

**必须说清一件事**：无常驻形态下，单实例**只解决"连点两次出两个窗口"**，**不提供热复用**——窗口一关进程就没了，下次右键仍是冷启动。别指望靠它省启动时间。

所以单实例的规则收紧成：抢到锁 → 正常开窗；抢不到 → 转发数据后立刻退出（说明另一个窗口正开着，用户刚点了第二批文件，应该替换而不是并排两个窗口）。

### 7.7 运行形态与可见性

**结论：不做托盘图标，不做开机自启。**

| 决策 | 选择 | 理由 |
|---|---|---|
| 常驻 | 否。窗口关闭 → `Environment.Exit(0)` | 批量改名是低频操作（一天 0–3 次），为省 1 秒启动搭 ~100MB 常驻内存 + 一整套托盘生命周期管理，不划算 |
| 托盘图标 | 否 | Win11 默认把新托盘图标收进溢出区（`^`），用户不点开根本看不见——它给的是"以为在后台常驻"的错觉，不是确定性 |
| 开机自启 | 否 | 右键是用户动作触发，进程不需要预先存在。自启的唯一收益是"热复用省 1 秒"，代价是开机多背一个后台进程 |

**"它在运行吗"这个问题的正解不是托盘，而是每次操作都有明确反馈**：

| 场景 | 反馈 | 实现 |
|---|---|---|
| 点了菜单，正常启动 | 窗口出现（本身就是反馈） | — |
| 点了菜单，主程序起不来 | **错误框 + 日志** | §5.4 `ReportLaunchFailure` |
| 菜单项压根看不到 | 安装脚本末尾打印"26H2 可能折叠，去个性化设置"；应用内也有深链引导 | §6.4 / §10.3 |
| 名字改完了 | 底部结果条：`已改名 96 项 · 2 项失败 · [撤销]`，保持到窗口关闭 | §8.9.5 |
| 需要排查 | `%LOCALAPPDATA%\BatchRename\logs\shell.log` 与 `journal\*.json` | §5.4 / §8.10 |

任务管理器里的 `BatchRename.App.exe` 只在窗口开着时存在——这是**正确**行为，不是缺陷。

如果以后改主意要加托盘，两条技术路线留档：

| 方案 | 要点 |
|---|---|
| `Shell_NotifyIcon` P/Invoke（~150 行） | 必须用**不可见的顶层窗口**（`WS_POPUP`）收回调消息，**不能用 `HWND_MESSAGE`**——消息窗收不到 `TaskbarCreated` 广播，explorer 重启后图标会永久消失 |
| `H.NotifyIcon.WinUI` 库 | 免掉上述坑，代价是多一个第三方依赖 |

要加开机自启时，正确做法是清单里声明 `windows.startupTask` 扩展 + `StartupTask` API，**不是**往 `HKCU\…\Run` 写注册表——前者会出现在任务管理器的"启动"页，用户能自己禁用。

### 7.8 资源占用预估与实测方法

> 以下数字是按 WinUI 3 + .NET 10 + WinAppSDK 自包含的典型值推算的 `[判断]`，**不是实测**。落地后按 §7.8.6 的方法校准，并把实测值回填到 §7.8.7 的红线表里。

#### 7.8.1 分场景预算（🔧 v1.7：已全部替换为实测值）

| 场景 | 实测（私有提交） | 工作集 | 备注 |
|---|---|---|---|
| 平时没在用 | **0** | 0 | 无进程、无服务、无自启 |
| 右键菜单弹出瞬间 | Shell DLL 2–4MB（计入 `dllhost`） | — | 菜单出现 ≤30ms |
| 空窗口刚启动 | **114MB** | 156MB | 冷启 ~200ms |
| 打开 500 项 | **135MB** | 181MB | 列表首屏固定开销 **+21MB** |
| 打开 2000 项 | **138MB** | 182MB | |
| 打开 5000 项 | **143MB** | 187MB | 每项约 **1.8KB** |
| 关闭窗口后 | **0** | 0 | 进程真退出 |

> 🔧 **v1.7：内存几乎与项数无关**（这是本项目最容易搞错的一个直觉）
>
> | 项数 | 私有提交 | 相对 500 项 |
> |---|---|---|
> | 500 | 135MB | — |
> | 2000 | 138MB | +3MB |
> | 5000 | 143MB | **+8MB** |
>
> 项数翻 10 倍，内存只涨 8MB。内存的真实构成是：
>
> ```
> 114 MB  ← WinUI 3 + WinAppSDK 自包含基线（空窗就有）
>  21 MB  ← 列表首屏的固定开销（ListViewItem 容器池 / ItemsStackPanel / 文本布局引擎）
>   8 MB  ← 5000 个 RenameItem 数据对象（每项约 1.8KB）
> ```
>
> **推论**：优化"每项成本"是没有意义的，这里没有可挖的空间。要降只能动基线（换框架/去掉自包含）或那 21MB 的列表固定开销（换 `ItemsRepeater`，但要重写行选中与键盘交互）。

#### 7.8.2 磁盘占用

| 项 | 大小 |
|---|---|
| `BatchRename.App\`（WinAppSDK 自包含 + .NET 框架依赖） | 32–40MB |
| `BatchRename.Shell.dll` | 120–180KB |
| `Assets\` | 约 10KB |
| **安装总占用** | **约 35MB** |
| 运行期数据 | journal ≤20 个约 2MB；日志各 1MB 上限 |

不要把 .NET 也改成自包含：40MB 会变成 130–180MB，而内存几乎不变（差 5–10MB）。纯亏。

#### 7.8.3 会显著改变数字的三个变量

| 变量 | 影响 | 本项目选择 |
|---|---|---|
| WinAppSDK 自包含 vs 框架依赖 | 自包含让每个应用独占一份 XAML 运行时，比共享框架多占 5–15MB 私有内存 | **仍选自包含**：省掉"缺运行时直接闪退"，15MB 值这个保险 |
| 每行是否带文件图标 | 每项一次 `SHGetFileInfo`，1–3ms；5000 项多 **5–15 秒**，再加几十 MB 图标缓存 | **不做逐行图标**（§7.4 红线） |
| 每行是否绑 `ToolTip` | 5000 个 flyout，内存 +100MB 以上，滚动明显掉帧 | **不绑**（§7.4 红线） |

#### 7.8.4 两个会被误读的现象

**① 任务管理器里会残留一个 `dllhost.exe`**

右键点过一次后，COM 代理进程会存活约 5 分钟才被回收，`tasklist /m BatchRename.Shell.dll` 能看到它。这是正常行为，不是泄漏，也不违背"无常驻"承诺（那条承诺指的是主程序不在后台跑）。代价是那 5 分钟里多约 8–15MB 内存。

**② VS 调试时的内存会翻倍**

调试器附加后 WinUI 3 的私有工作集能从 60MB 涨到 150–200MB。用调试时的数字评估会高估 2–3 倍。要测真实值，跑 Release 版直接看「任务管理器 → 详细信息 → 内存（专用工作集）」。

#### 7.8.5 横向参照

| 程序 | 内存 | 说明 |
|---|---|---|
| 本工具（开窗时） | 60–95MB | 关窗即 0 |
| PowerToys PowerRename | 80–140MB | 同为 WinUI 3，同一量级 |
| 7-Zip 图形界面 | 约 15MB | 原生 Win32 |
| 记事本 | 约 10MB | — |

比"轻量"这个词给人的预期重 3 倍，与 PowerRename 持平。但这是**开窗期间的 90MB**，不是常驻的 90MB——体感完全不同。要再压只能换 WPF（约 60MB）或原生 Win32 自绘（约 20MB，等于自己写一套控件），都不值得为这 40MB 付。

#### 7.8.6 实测方法

```powershell
# 真实内存（Release 版跑起来后）
Get-Process BatchRename.App | Select-Object WorkingSet64,PrivateMemorySize64

# 进程数：应该只有 1 个 App（+ 可能 1 个 dllhost）
Get-Process BatchRename.App,dllhost -ErrorAction SilentlyContinue |
    Where-Object { $_.Modules.ModuleName -contains 'BatchRename.Shell.dll' -or $_.ProcessName -eq 'BatchRename.App' }
```

首帧与载入耗时靠**代码内打点**，不要靠外部工具（测不准）：

```csharp
// App.OnLaunched 开头
var sw = Stopwatch.StartNew();
// ... 首帧渲染完成后
Log.Info($"[startup] first-frame={sw.ElapsedMilliseconds}ms items={_items.Count}");

// 载入与首屏分开打
Log.Info($"[load] parse-done={t1.ElapsedMilliseconds}ms first-render={t2.ElapsedMilliseconds}ms");
```

#### 7.8.7 红线（实测超过即说明某处做错了）

| 指标 | 实测（2026-11-01） | 上限 | 超了通常意味着 |
|---|---|---|---|
| 空窗口私有工作集 | **110MB** | 120MB | 加载了多余 XAML 资源、或行模板里塞了重型容器 |
| 5000 项时 | **139MB**（v1.5 前 180MB；同状态 PowerRename 123MB，差在 C# 基座） | 150MB | 行模板里出现**模板化控件**（`CheckBox` 曾独占 46–49MB）、绑了 ToolTip、或重建了 `ObservableCollection` |
| 5000 项窗口出现耗时 | **174ms** | 1.5s | 没有分批 `TryEnqueue`，或一次 `Add` 一条 |
| 同时存在的 `BatchRename.App.exe` | 1 个（已验证） | 1 个 | 单实例失效（§7.6） |
| 关窗后残留进程 | 0 | 0 | 有后台线程未退出（多为 STA 线程未释放） |
| 改规则到预览刷新 | 0–5ms（引擎） | 200ms | 防抖没生效，或每次都全量重建列表 |

> 🔧 **v1.3 → v1.7 修正**：v1.3 曾判断「5000 项多出的 ~94MB 来自 5000 个 `RenameItem` 与 ListView 的绑定/布局对象，优化方向是改 `x:Bind`」——**v1.7 实测证伪**。
>
> | 实验（Release，真机） | 结果 | 结论 |
> |---|---|---|
> | 行模板 7 处 `{Binding}` **全部换成静态文本** | 186MB → **184MB** | 绑定层总开销**仅 2MB**。这就是 `x:Bind` 能省的**上限**，实际约 1MB |
> | 项数 500 → 2000 → 5000 | 135 → 138 → 143MB | 每项约 1.8KB，翻 10 倍只涨 8MB |
> | 空窗 → 显示列表（500 项） | 114 → 135MB | **+21MB 是"有没有列表"的固定开销**，与项数无关 |
>
> 三条结论：
> 1. **`x:Bind` / 抽 `UserControl` 是 CPU 优化（省反射），不是内存优化**。为 1MB 去搬 767 行窗口逻辑，性价比为零，已从待办移除。
> 2. **优化"每项成本"没有意义**——那块总共只有 8MB。真正的大头是 114MB 框架基线和 21MB 列表首屏开销。
> 3. 想再降只有两条路，代价都不小：换 `ItemsRepeater`（重写行选中与键盘交互，收益上限 21MB）、或去掉 WinAppSDK 自包含（换"缺运行时就闪退"的风险，收益约 10MB）。

---

## 8. 重命名引擎规格

### 8.1 数据模型

```csharp
sealed class RenameItem : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";        // 原始完整路径
    public string Directory { get; init; } = "";        // 所在目录
    public string OriginalName { get; init; } = "";     // 含扩展名
    public string Stem { get; init; } = "";             // 主名
    public string Ext { get; init; } = "";              // 含前导点，可能为空串
    public bool   IsDirectory { get; init; }
    public FileAttributes Attributes { get; init; }

    public bool   IsEnabled { get; set; } = true;       // 用户勾选
    public string NewName { get; set; } = "";           // 引擎产出，含扩展名
    public string NewFullPath => Path.Combine(Directory, NewName);

    public ItemState State { get; set; }                // 见下表
    public string?   Message { get; set; }              // 红/黄字文案
    public bool      CaseOnly { get; set; }             // 仅大小写变化

    public event PropertyChangedEventHandler? PropertyChanged;
}

enum ItemState { Unchanged, Ok, Warning, Invalid, ConflictInBatch, ConflictOnDisk }
```

### 8.2 主名 / 扩展名切分

```csharp
static (string Stem, string Ext) Split(string name, bool isDirectory)
{
    if (isDirectory) return (name, "");            // 目录永不切
    int i = name.LastIndexOf('.');
    if (i <= 0) return (name, "");                 // 无点；或 i==0 是 dotfile（.gitignore）
    return (name[..i], name[i..]);                 // Ext 含前导点
}
```

| 输入 | Stem | Ext | 说明 |
|---|---|---|---|
| `report.txt` | `report` | `.txt` | — |
| `archive.tar.gz` | `archive.tar` | `.gz` | 只切最后一层，与资源管理器"类型"列一致 |
| `.gitignore` | `.gitignore` | `` | `i==0` 视为 dotfile，整体是主名 |
| `a.` | `a` | `.` | 校验阶段判"结尾点"非法 |
| `noext` | `noext` | `` | — |
| `文件夹.v2`（目录） | `文件夹.v2` | `` | 目录作用域里"仅扩展名"必须**灰掉**并提示 |

### 8.3 作用域语义

**核心约束：「两者」绝不能把 Stem+Ext 拼成一整串再替换。**

反例：文件 `a.txt`，规则"查找 `a.t` → 替换 `x`"。拼串后 `a.txt` 命中出 `xxxt`（跨过了点）。这是 PowerRename 早期被吐槽过的行为。

| 作用域 | 作用对象 | 拼回方式 |
|---|---|---|
| 仅文件名 | `Stem` | `newStem + Ext` |
| 仅扩展名 | `Ext` **去掉前导点后**的部分（`gz`） | `Stem + "." + newExtBody`；`newExtBody` 为空则结果无扩展名（校验时会提示） |
| 两者 | `Stem` 与扩展名主体**各跑一次同一条规则** | 分别拼回 |

⚠️ 扩展名作用域的匹配目标**不含点**。否则用户输入 `.txt` 永远匹配不到，这是第一次使用最常见的挫败点。

### 8.4 执行顺序

```
原始段 S
  → ① 匹配替换（文本或正则）
  → ② 大小写转换（作用于 ① 的整段输出）
  → ③ 与另一段拼回完整文件名
  → ④ 逐项校验 → ⑤ 冲突检测
```

为什么大小写转换放在替换**之后**：

1. 替换词里用户可能手写了混合大小写（`IMG_` → `Pic_`），先转换会把用户意图一起"污染"；
2. 正则替换时 `$1` 捕获组带出来的原文大小写，如果先转换就"转了但没转全"（捕获组部分转不到），前后不一致更难解释。

转换**不**作用于路径、**不**作用于扩展名前的点。

### 8.5 大小写转换

| 模式 | 实现 | 陷阱 |
|---|---|---|
| 不转换 | 原样 | — |
| 全大写 | `s.ToUpperInvariant()` | 用 `ToUpper()` 会因当前区域出错：土耳其语 `i` → `İ`，`fileIndex` 变 `FİLEİNDEX` |
| 全小写 | `s.ToLowerInvariant()` | 同上，土耳其语 `I` → `ı` |
| 每词首字母大写 | 先 `ToLowerInvariant()`，再按词边界升首字母 | **不要用 `TextInfo.ToTitleCase`**：它会把 `fileName` 变成 `Filename`，把 `don't` 变成 `Don'T` |

词边界定义（`RenameRule` 里做常量）：

```csharp
// 🔧 v1.1 更正：单引号已从 WordSeps 移除。
//   v1.0 一边把它写进词边界、一边要求"don't 不能变 Don'T"，按原文实现必然是 Don'T。
static readonly char[] WordSeps = " \t-_.[](){}+&".ToCharArray();

static string TitleCase(string s)
{
    var chars = s.ToLowerInvariant().ToCharArray();
    bool atWordStart = true;

    for (int i = 0; i < chars.Length; i++)
    {
        var c = chars[i];

        if (Array.IndexOf(WordSeps, c) >= 0) { atWordStart = true; continue; }

        if (char.IsLetterOrDigit(c))
        {
            if (atWordStart)
            {
                chars[i] = char.ToUpperInvariant(c);
                atWordStart = false;
            }
        }
        // 🔧 v1.1：其它符号（含 '）不改变词状态。
        //   v1.0 这里写的是 atWordStart = true，于是 don't 的 t 被当成词首 → Don'T
    }
    return new string(chars);
}
```

> 🔧 **v1.1 说明**：`don't` 是这条规则唯一的试金石。
> 正确输出 `Don't`；v1.0 的两处写法（`WordSeps` 含 `'`、非字母数字一律重置词首标志）叠加后必然输出 `Don'T`。
> 断言已固化在 `ShellTest` 里（`don't 不变 Don'T`）。

中文与数字不算词边界（`第1章` 不会变成 `第1章` 的变形），连续数字不打断词。

### 8.6 匹配替换

**文本模式 + 区分大小写开关**

`string.Replace` 没有带比较器的重载，直接用它会让"区分大小写"开关形同虚设。必须自己写：

```csharp
static string ReplaceText(string input, string needle, string replacement, bool caseSensitive)
{
    if (string.IsNullOrEmpty(needle)) return input;
    var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    var sb = new StringBuilder(input.Length);
    int pos = 0;
    while (true)
    {
        int hit = input.IndexOf(needle, pos, cmp);
        if (hit < 0) { sb.Append(input, pos, input.Length - pos); break; }
        sb.Append(input, pos, hit - pos).Append(replacement);
        pos = hit + needle.Length;              // ★ 按命中长度前进，避免替换词含查找词时死循环
        if (pos > input.Length) break;
    }
    return sb.ToString();
}
```

**正则模式**

```csharp
Regex Build(string pattern, bool caseSensitive)
{
    var opts = RegexOptions.CultureInvariant
             | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

    // 首选非回溯引擎：天然免疫灾难性回溯，性能可预测
    try { return new Regex(pattern, opts | RegexOptions.NonBacktracking); }
    catch (NotSupportedException) { /* 用到 lookaround/backreference，回退 */ }
    catch (ArgumentException) { throw; }

    // 回退到回溯引擎，必须带超时
    return new Regex(pattern, opts, TimeSpan.FromMilliseconds(250));
}
```

三条规则：

1. `NonBacktracking` 不支持 lookaround、backreference、`RegexOptions.RightToLeft`，遇到就 `NotSupportedException` —— 静默回退，别打扰用户。
2. 回退路径必须带 250ms 超时，单次匹配超时把**该项**标红（`Message="正则匹配超时"`），不中止整批。
3. 非法正则（`ArgumentException`）在**输入框防抖回调里**捕获，顶部显示横幅"正则无效：xxx"，执行按钮禁用 —— 绝不能冒泡成未处理异常。

替换串直接交给 `Regex.Replace(input, replacement)`，支持 `$1` / `${name}` / `$$`。

### 8.7 逐项校验

按顺序执行，**首个失败即返回**：

| # | 规则 | 状态 | 文案 |
|---|---|---|---|
| 1 | `NewName` 为空 | Invalid | 新名为空 |
| 2 | 含 `Path.GetInvalidFileNameChars()` 任一（`" < > \| : * ? \ /` 与控制符 0–31） | Invalid | 含非法字符 |
| 3 | 主名（去尾随空格与点后 `ToUpperInvariant`）属于保留设备名：`CON PRN AUX NUL COM1-9 LPT1-9` | Invalid | 保留设备名 |
| 4 | 以空格或点结尾 | Invalid | 不能以空格或点结尾 |
| 5 | `NewName.Length > 255` | Invalid | 单段名超过 255 字符 |
| 6 | `NewFullPath.Length` > 32767 | Invalid | 路径过长 |
| 7 | `NewFullPath.Length` 在 260–32767 之间 | Warning（不阻止） | 超出传统路径长度，依赖长路径支持 |
| 8 | `IsEnabled && NewName == OriginalName` | Unchanged（灰字，自动跳过） | 未改变 |
| 9 | `OrdinalIgnoreCase` 相等但 `Ordinal` 不等 | Ok + `CaseOnly=true` | 仅大小写变化 |

附加提示（不阻止）：只读属性 / 系统属性 → Warning「目标为只读/系统文件」。

为什么 #7 只警告不阻止：`app.manifest` 声明了 `longPathAware`，系统也开了长路径策略时能成功；但网络盘与部分 archiver 场景会失败。给提示比硬拦合理。

> 🔧 **v1.2 陷阱：校验「有没有改名成功」不能用 `File.Exists`**
>
> NTFS 默认不区分大小写，`File.Exists(@"C:\dir\upper.txt")` 在文件实际叫 `UPPER.TXT` 时**照样返回 true**。后果：
>
> | 场景 | 错误后果 |
> |---|---|
> | 用 `File.Exists` 校验执行结果 | 「仅大小写改名」没生效也判成功，用户看到"已改名 N 项"但文件名没变 |
> | 用 `File.Exists` 判断撤销时源名是否被占用 | CaseOnly 项的 `from` 与 `to` 只差大小写 → 永远判"已被占用" → 整批标记"所有项均已失效"，撤销按钮直接废掉 |
>
> 正确做法：枚举目录后逐项 `StringComparison.Ordinal` 比较。
>
> ```csharp
> public static bool ExistsExact(string path)
> {
>     var dir = Path.GetDirectoryName(path);
>     if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
>     var name = Path.GetFileName(path);
>     return Directory.EnumerateFileSystemEntries(dir)
>         .Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));
> }
> ```
>
> 两处（执行结果校验、撤销占用检查）都必须用它。这是 M3 期间最隐蔽的一个 bug，单靠肉眼看代码发现不了，必须靠「执行 CaseOnly → 撤销」的端到端测试才能暴露。

### 8.8 冲突检测

> 🔧 **v1.2 警告（这条会造成数据丢失）**：本节**不是** M3 的事，M2 预览阶段就必须完整实现。
>
> v1.1 把「磁盘已存在」推迟到 M3，真机后果是：用户把 `中文文件.txt` 改成已存在的 `点.点.点.txt`，预览没拦，执行时 IFileOperation **直接覆盖**，原文件消失且无任何提示（因为执行带了 `FOF_NOCONFIRMATION | FOF_NOERRORUI`）。
>
> 只要「执行」这个按钮可以点，磁盘冲突检测就必须在它之前生效。

统一 key = `(Directory, NewName)`。

```csharp
sealed class ConflictKey : IEquatable<ConflictKey>
{
    public string Directory { get; init; }
    public string Name { get; init; }
    public static IEqualityComparer<ConflictKey> Comparer(bool caseSensitiveVolume) => ...
}
```

卷是否大小写敏感：`GetVolumeInformationW` 的 `FILE_CASE_SENSITIVE_SEARCH` 标志；调用失败则**按不敏感处理**（Windows 默认）。注意这是每卷属性，同一批可能横跨多个卷 —— 按 `Path.GetPathRoot` 分组后各用各的比较器。

| 检查 | 算法 | 复杂度 |
|---|---|---|
| 批内互撞 | `Dictionary<ConflictKey, List<RenameItem>>` 一次扫描，桶内多于 1 项则全标 `ConflictInBatch` | O(n) |
| **批内循环**（🔧 v1.5） | 沿"目标名 = 另一项的源名"这条边一路走，走回本轮起点即成环 → 整环标 `ConflictInBatch` / 「批内循环改名」 | O(n) |
| 磁盘已存在 | **按目录**分组 → 每目录一次 `Directory.EnumerateFileSystemEntries(dir)` 建 `HashSet` → 逐项查 | O(目录数 + n)，比逐项 `File.Exists` 快 10–50 倍 |
| 排除自身 | `CaseOnly` 项跳过（目标就是它自己） | — |
| 排除本批将移走的源 | 把本批所有 `OriginalName` 也放进"占用集合"，命中时若该源项也在本批且将改名，则不算冲突 | — |

两种策略：

| 策略 | 行为 | 数据结构 |
|---|---|---|
| 阻止（默认） | 有任一冲突 → 顶部红条 + 执行按钮禁用 + 冲突行红字 | 只读标记 |
| 自动编号（可选开关） | 循环加后缀直到不冲突 | 每加一次把新名塞回占用集合重判 |

自动编号规则：加在 **Stem 尾部**，不是文件名尾部。

```
a.txt       → a (1).txt        ✅
a.txt       → a.txt (1)        ❌
```

最多尝试 999 次，仍冲突则标红。`(1)` 里的空格用半角，括号用半角。

### 8.9 执行器

#### 8.9.1 线程模型

IFileOperation 必须在 **STA 线程**上创建，且该线程要泵消息（进度回调需要）。用专用线程，不要用 UI 线程（长操作会冻结界面）。

```csharp
sealed class FileOperationRunner
{
    public Task<RenameBatchResult> RunAsync(IReadOnlyList<RenameItem> items, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RenameBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => StaWorker(items, tcs, ct))
        {
            IsBackground = true,
            Name = "RenameSta"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    static void StaWorker(...)
    {
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);   // 自己初始化，因为不是 UI 线程
        try { /* 见 8.9.2 */ }
        finally { CoUninitialize(); tcs.TrySetResult(result); }
    }
}
```

#### 8.9.2 队列构建（🔧 v1.5：两步改名已作废）

```csharp
foreach (var (item, _) in OrderByDependency(planned))
{
    var src = CreateShellItem(item.FullPath);
    fixed (char* pName = item.NewName)
    {
        op.RenameItem(src, new PCWSTR(pName), sink);   // sink 见 §8.9.4
    }
}
```

**所有项一律进同一个 `IFileOperation` 队列，不再为 CaseOnly 单独开分支。**

> 🔧 **v1.5 更正（推翻 v1.2 的结论）**
>
> v1.2 断言「仅大小写改名必须经临时名中转」，因此改用 `File.Move` 走两步。实测两点都错：
>
> 1. `File.Move` 与 `IFileOperation::RenameItem` 在 NTFS 上**都能直接完成仅大小写改名**，文件与目录都可以（真机探针验证，精确大小写校验通过）；
> 2. `File.Move` **动不了目录** —— 旧实现让「文件夹名只改大小写」必然失败（`FileNotFoundException`）。
>
> v1.2 那次 `0x80070002` 的真实病因是「在同一队列里引用尚未生成的临时名」，**与大小写无关**。把两个互不相关的现象归到了同一个原因上。
>
> **依赖顺序**改用拓扑排序解决：`a→b`、`b→c` 这类链必须让 `b→c` 先执行（`b` 先让出名字），实现为 `OrderByDependency`（Kahn 算法，O(V+E)）。
> 环（`a↔b`）由预览阶段的 `MarkBatchCycles` 拦成 `ConflictInBatch`，不进入执行器 —— 环靠排序解决不了，只能拒绝。

标志位：

```csharp
// 🔧 v1.5：加 FOFX_ADDUNDORECORD —— 整批登记到资源管理器撤销栈，
//   关掉本程序后 Ctrl+Z 依然有效（旧版有意不加，导致系统级撤销完全不可用）。
//   journal 驱动的反向执行传 AddUndoRecord: false，避免出现"撤销的撤销"记录。
op.SetOperationFlags(
    FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI |
    FOFX_SHOWELEVATIONPROMPT | FOF_SILENT |
    (addUndoRecord ? FOFX_ADDUNDORECORD : 0));

// 🔧 v1.5：必须给 owner 窗口，UAC 提示框与 Shell 错误框才会挂在我们窗口上
if (ownerHwnd != IntPtr.Zero) op.SetOwnerWindow(ownerHwnd);
```

> 🔧 **v1.5：本节上面的「CaseOnly 两步改名」整段作废。** 实测 `IFileOperation::RenameItem` 直接就能做仅大小写改名（文件与目录都行），`File.Move` 也能（但对目录会抛 `FileNotFoundException`）。v1.2 那次 `0x80070002` 的病根是"同一队列引用尚未生成的临时名"，与大小写无关。现在所有项一律进同一个队列，顺带获得两个好处：整批 = **一条**撤销记录；目录名只改大小写不再是死路。

有意不加的：`FOF_ALLOWUNDO`（撤销我们自己记，走 shell 的回收站撤销栈对 rename 不可靠 `[判断]`）、`FOF_RENAMEONCOLLISION`（冲突已在 UI 阶段解决，静默改名比报错更坏）。

#### 8.9.3 CsWin32 清单 `NativeMethods.txt`

```
IFileOperation
IShellItem
IFileOperationProgressSink
SHCreateItemFromParsingName
CoCreateInstance
CoInitializeEx
CoUninitialize
GetVolumeInformationW
GetFileInformationByHandleEx
SetForegroundWindow
AllowSetForegroundWindow
```

为什么用 CsWin32 源生成器而不是手写 `[ComImport]`：`IFileOperation` 有 20 多个方法，`RenameItem` 在中间某个位置，vtable 错一位就是随机内存破坏/静默失败，且极难定位。源生成的接口顺序由 SDK 元数据保证。

#### 8.9.4 逐项结果回收

> 🔧 **v1.5：改为实现 sink。** v1.2 曾决定"不实现 15 个回调，执行后用文件系统校验"，真机代价是：
> 逐项 `ExistsExact` 每次整目录枚举（4000 项 ≈ 2.9s，撤销翻倍），且链式改名会把成功的项误报成失败。
> 现在由 Shell 直接给逐项 HRESULT。

```csharp
// Core/RenameProgressSink.cs —— CsWin32 生成的就是托管 [ComImport] 接口，
// 直接实现即可；把它当参数传给 RenameItem 时 CLR 自动建 CCW，不用手工碰 vtable。
internal sealed class RenameProgressSink : IFileOperationProgressSink
{
    public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
    {
        if (_ct.IsCancellationRequested) throw new OperationCanceledException();  // → 失败 HRESULT → Shell 停止后续
    }

    public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName,
                              HRESULT hrRename, IShellItem psiNewlyCreated)
    {
        _results[GetPath(psiItem)] = (hrRename.Value, pszNewName.ToString());
        // 不抛：单条失败不该让后面几百项都不执行
    }
}
```

三条约定：

1. `PostRenameItem` **只记录、绝不抛**，否则一件文件被占用就会连带中止整批；
2. 取消只在 `PreRenameItem` 里做（抛 → CLR 返回失败 HRESULT → Shell 停止后续，已完成的保持不变）；
3. `CaseOnly` 项额外用 `ExistsExact` 复核一次——"Shell 报成功但大小写没变"历史上真出现过。

`DescribeHr` 负责把 HRESULT 翻成人话。注意改名失败时 Shell 常返回**复制引擎**错误（`0x8027xxxx`，如 `0x80270027` = 源文件被占用），而不是普通 `0x8007xxxx`，两族都要映射。

UI 每 100 条 `TryEnqueue` 刷一次进度，不要逐条。**v1.5 起这条已接线**：sink 的 `(done,total)` 回调 → `MainWindow.ReportProgress` → `DispatcherQueue.TryEnqueue` → `BusyBar`（确定态）+"正在改名 n / N"+"取消"按钮（取消走 `PreRenameItem` 抛）。

#### 8.9.5 执行后

1. 全部成功 → 选项：改名后选中新文件（`ShellExecuteW` 在父目录打开并选中，可选）。
2. 部分失败 → 弹 `ContentDialog` 列出失败项 + HRESULT 翻译（`ERROR_ACCESS_DENIED` → "权限不足或被占用"）。
3. 关闭窗口后 journal 仍在。

**结果条**（无常驻形态下这是"改完了"的唯一可见凭证，别省）：

| 状态 | 底栏文案 | 附加 |
|---|---|---|
| 执行中 | `正在改名 34 / 96 …  [取消]` | 进度条替代按钮区 |
| 全部成功 | `已改名 96 项 · 撤销(Ctrl+Z)` | 「撤销」按钮点击后变 `已撤销 96 项` |
| 部分失败 | `已改名 94 项，2 项失败 · 查看详情` | 「查看详情」打开 ContentDialog；结果条左侧变黄 |
| 撤销成功 | `已撤销 96 项` | 结果条变灰，`Ctrl+Z` 失效 |
| 撤销部分失效 | `已撤销 93 项，3 项已失效` | 列表里失效行标灰并写明原因 |

结果条停留到窗口关闭为止，不做 3 秒自动消失——用户可能正切到资源管理器核对，回来时不该发现"提示早没了"。

### 8.10 撤销 journal

#### 8.10.1 文件结构

> 🔧 **v1.5：降级为单次操作事务日志。** 只有一个文件：`%LOCALAPPDATA%\BatchRename\journal\last-batch.json`，每次执行**覆盖写**。已删除：7 天有效期、`Prune(20)`、多批次历史、时间戳 `Id`。理由：历史批次既没有 UI 入口也没有需求。

实际落盘形态（System.Text.Json 默认不改名，所以是 **PascalCase**；`'` 会被转义成 `\u0027`）：

```jsonc
{
  "Schema": 2,
  "CreatedUtc": "2026-10-31T17:04:11.95Z",
  "State": "Pending",                    // Pending | Applied | Undone
  "RuleSummary": "find=\u0027IMG_\u0027 → \u0027PIC_\u0027 | scope=Stem | case=Upper | sensitive=True",
  "Entries": [
    { "From": "C:\\Photos\\IMG_001.jpg", "To": "C:\\Photos\\PIC_001.JPG", "CaseOnly": false, "Ok": true },
    { "From": "C:\\Photos\\a.txt",       "To": "C:\\Photos\\A.txt",       "CaseOnly": true,  "Ok": true }
  ]
}
```

`RuleSummary` 不再用于列表展示，只留给事后人工排查。

#### 8.10.2 时序（write-ahead）

```
点击执行
  ├─ 1. 写 journal，state=Pending，Flush 到磁盘（FileStream + Flush(true) → 临时文件 + Move 原子替换）
  ├─ 2. PerformOperations（此时才真正动文件）
  ├─ 3. 回写 state=Applied（含逐项 ok 结果），覆盖同一个文件
  └─ 4. 撤销时：反向执行 → 全部成功才写 state=Undone；部分失败保留原状态，可重试
```

崩溃恢复：下次启动读 `last-batch.json`，若 `State == Pending` 就弹「上次操作可能未完成，是否回滚 N 项？」，
回滚走 **`RollbackPendingAsync`**（🔧 v1.5 新增；旧版误用了 `UndoAsync`，而它对 `Pending` 明确拒绝 → 按钮必然失败）。

#### 8.10.3 失效判定（逐项）

| 条件 | 处理 |
|---|---|
| `to` 与 `from` 都不存在 | 该项跳过，计入"已失效"（文件被挪走或删除） |
| `from` 已被别的文件占用 | 该项跳过（不覆盖） |
| `to` 已不存在但 `from` 在 | 计入"已是原名"（上一次部分撤销的残留），跳过 |
| 所在卷 `IsReady == false`（U 盘拔了） | 整批失效，提示"目标卷不可用" |
| `state == Undone` | 拒绝（不做"撤销的撤销"） |
| `state == Pending` | `UndoAsync` 拒绝；崩溃恢复走 `RollbackPendingAsync` |
| 部分失效 | 其余正常回滚，结束时汇总"回滚 96 项，3 项失效" |
| 部分**失败**（回滚时抛错） | 保留 `Applied` 状态不写 `Undone`，可以再点一次「撤销」（已回滚项会被跳过） |

#### 8.10.4 反向执行

同样走 `FileOperationRunner`，方向对调。🔧 v1.5：`CaseOnly` 项**不需要**任何特殊处理，与正向一样直接入队——`IFileOperation::RenameItem` 本来就能做仅大小写改名，两侧步骤因此天然对称。

### 8.11 性能预算

> 本节只讲**时间**。内存、磁盘、CPU 与"实测超过即算做错"的红线见 §7.8。

| 环节 | 目标 | 做法 |
|---|---|---|
| 首次载入 5000 项 | ≤ 1.5s 可交互 | 后台线程 `Task.Run`，每 200 条批量 `DispatcherQueue.TryEnqueue` 追加；先渲染前 200 条 |
| 规则变更响应 | ≤ 200ms 出结果 | `DispatcherQueueTimer` 200ms 防抖（连打 6 个字符只算 1 次计算） |
| 引擎计算 | 5000 项 ≤ 120ms | `Task.Run` + `CancellationTokenSource`，新输入到来立即取消旧任务；>2000 项才 `Parallel.For` |
| UI 回写 | 不重建集合 | 逐项只对 `NewName`/`State` 变化项触发 `PropertyChanged`；**不要** `new ObservableCollection` |
| 磁盘存在性检查 | 用户停手 400ms 后 | 第二档防抖，按目录批量枚举（§8.8）。🔧 v1.5 已实现：150ms 纯内存层 + 400ms 磁盘层 |
| 执行期进度 | 每 100 项一次 | sink 里计数，到 100 才 Post 到 UI。🔧 v1.5 已实现（`PostRenameItem` 上报，UI 切线程画进度条 + 取消按钮） |
| 列表渲染 | 5000 行滚动 60fps | `ListView` 默认 `ItemsStackPanel` 虚拟化；行模板只放 4 个叶子控件；不塞 `ScrollViewer`；不绑 `ToolTip`；不逐行取图标（§7.4） |

---

## 9. 里程碑与验收

### M1 —— 菜单能点、窗口能开（**风险最高，必须最先做**）

范围：稀疏包清单 + `Install-Sparse.ps1` + 空壳 shim（`GetTitle`/`GetState`/`Invoke` 能拉起空窗口并打印收到的路径）。

验收（**2026-10-31 实测结果**）：

- [x] `Install-Sparse.ps1` 跑通，末尾无报错，`Get-AppxPackage -Name BatchRename.Shell` 有输出且 `Status` 为 `Ok`
      → 实测 `BatchRename.Shell_1.0.0.0_x64__81759h8xb5n4c`，`Status: Ok`
- [x] 单文件右键 → 26H2 菜单出现「批量重命名」（可能在「应用扩展」子菜单下）
- [x] ~~`Shift + 右键` → 经典菜单里也能看到~~ → **有意不做**。传统 verb 的 `MultiSelectModel` 默认 `Single`，多选只传第一个文件，对批量工具等于废掉；改用 SendTo 作为兜底入口（§10.2 入口②）
- [x] 选 3 个文件 → 窗口显示「收到 3 条路径」，中文路径、含空格路径、含 `&` 路径全部正确
- [x] 选 1 个文件夹 → 同样能进
- [x] 「此电脑」「回收站」「桌面空白」右键 → 菜单项**不出现**
- [x] `ShellTest.exe` 能直接 `CoCreateInstance` 调通（此条是排障基础设施，必须先有）
      → 当前实现为**引擎断言测试**（124 项），未做 COM 直调；COM 激活已用 `[Activator]::CreateInstance` 验证过
- [ ] **把 `BatchRename.App.exe` 改名藏起来**再右键 → 弹出错误框（不是静默无反应），且 `logs\shell.log` 有记录；随后下一次右键菜单**不卡**（验证弹框没阻塞 STA）
      → 错误框路径已验证（曾因 `SetHandleInformation` bug 真实触发过，日志有 `launch failed` 记录），**登录日志位置见 §5.4 的 v1.1 补记**
- [x] 关窗后 `tasklist /fi "imagename eq BatchRename.App.exe"` 无输出（无常驻，见 §7.7）

> **M1 期间的三个真实故障**（v1.0 未能预见，均已修复，详见 §5.1 / §5.4 / §7.1 的 v1.1 标注）：
> ① `CreateProcess` 返回 87（句柄继承标志清错对象）→ 弹「启动失败」；
> ② `resources.pri` 缺失 → App 启动即 `0xC000027B`，表现为"加载一会儿什么都没有"；
> ③ WRL 不自动导出 → 若不写 `.def` 会得到一个空导出表的 DLL。

M1 卡住时的排障顺序（别乱猜）：

1. `dumpbin /exports` 看三个导出 → 没有？WRL `Module` 没实例化或编成了 static lib
2. `dumpbin /headers` 看 machine → 不是 x64？改平台
3. 清单三处 CLSID 逐字符 diff → 用脚本的自检（§6.4 第 0 步）
4. `tasklist /m BatchRename.Shell.dll` → 没加载说明注册层面就没通
5. `Get-WinEvent -LogName 'Microsoft-Windows-AppModel-Runtime/Admin' -MaxEvents 20` → 看包激活错误
6. 以上全过但没有菜单项 → 26H2 的「应用扩展」折叠（§10），去设置里找

### M2 —— 引擎 + 实时预览（**引擎部分已完成**，UI 待人工验收）

范围：`NameSplitter` / `RenameRule` / `RenameEngine` + 预览列表。

验收（用固定样本集，建议放在 `docs\samples\`）。

**状态说明**：下表打 ✅ 的项由 `tests/BatchRename.Core.Tests` 的 **124 项自动断言**验证（不依赖 UI）；标 ⏳ 的项需要人工在界面上确认。

- [x] 200 个混合样本（中文 / 空格 / 多扩展名 / dotfile / 无扩展名 / 文件夹）载入后产出正确
      → 断言通过（引擎层，0ms）；⏳ 两栏**显示**效果待人工确认
- [x] 改查找词，200ms 内预览刷新
      → 引擎单次 0–3ms，UI 侧另有 150ms 防抖
- [x] 输入 `?` → 该行立刻红字 + 执行按钮变灰
      → 断言：`State=Invalid`、`Message="含非法字符"`、`HasBlockingError=true`
- [x] 正则 `(\d+)` → `#$1` 正确；输入 `(` → 顶部横幅"正则无效"，不崩
      → 断言通过（`file12` → `file#12`；`(` 返回 `RegexError` 而非抛异常）
- [x] 作用域=仅扩展名时，查找框写 `txt` 能命中 `.txt`（不要求用户写点）
      → 断言通过（`a.txt` → `a.md`）
- [x] 作用域=仅文件名时，`a.txt` + 查找 `a.t` **不**命中
      → 断言通过（**Both 模式同样不命中**，绝不拼串）
- [x] `a.txt` → `A.txt` 显示"仅大小写变化"，不是错误
      → 断言：`CaseOnly=true`、`Message="仅大小写变化"`
      🔧 v1.1 实现差异：状态用 `Ok`（**绿色**）而不是 v1.0 写的黄色。理由是该状态可正常执行，用黄色会与"只读/长路径"这类需注意的警告混淆
- [x] 全大写模式下 `fileIndex` → `FILEINDEX`（不是 `FİLEİNDEX`）
      → 断言通过（`ToUpperInvariant`）
- [x] 5000 项载入到可交互 ≤ 1.5s
      → 引擎实测 **3ms**（预算 1500ms）

**新增的固定样本集**：`docs\samples\` 下 15 项，覆盖中文 / 空格 / 多重扩展名 / dotfile / 无扩展名 / `&` / 大小写混合 / 目录。

**M2 期间发现的 v1.0 缺陷**：§8.5 的 `WordSeps` 含 `'`，与"`don't` 不能变 `Don'T`"自相矛盾（详见 §8.5 v1.1 说明）。

### M3 —— 执行 + 撤销（已完成，2026-10-31）

验收（**实测结果**）：

- [x] 批量改名成功，逐项结果正确
      → 断言覆盖 10 项真实文件；真机 12 项 CaseOnly 批量改名通过
- [x] 进度条正常，中途取消能停下
      → v1.5 已接线：`IFileOperationProgressSink::PostRenameItem` 提供逐项回调，进度条确定态 + 「取消」按钮。
        断言 23 组（300 项，最后一次回调 `300/300`）、24 组（取消后 Shell 停止后续、已完成项保留）。
        旧描述"`ProgressRing` 不确定态 / 无逐项回调 / 无取消按钮"已作废（那时 sink 还没实现）
- [x] 5 个"仅大小写"项真的改了名（资源管理器里刷新后确认，不是"点了没变化"）
      → 真机 12 项全大写改名成功（`a&b.txt` → `A&B.txt` 等），磁盘确认
- [x] `Ctrl+Z` 全部还原，含那 5 个
      → 断言 13 端到端覆盖；真机验证通过
- [x] 手动删掉 1 个文件后撤销 → 提示"1 项失效"，其余正常
      → 断言 11 覆盖（失效 1 / 回滚 1）
- [ ] 执行中 `taskkill` → 重启弹"上次未完成，是否回滚"
      → 已实现（`FindPending` + ContentDialog）。未做 `taskkill` 式破坏性实测，但核心路径（Pending 命中、`UndoAsync` 拒绝 Pending、`RollbackPendingAsync` 可回滚）已由断言 15 / 17 覆盖
- [x] 改名到 `CON` / `a.` / 含 `|` → 全部在预览阶段就被拦，无法执行
      → M2 断言覆盖
- [x] 目标已存在 → 红字 + 执行禁用
      → **v1.2 才补上**（原计划推迟，导致过一次真实覆盖，见 §8.8 警告）；断言 12 覆盖
- [x] 开"自动编号"后变成 `a (1).txt` 且可执行
      → 已实现（§8.8 / v1.4）；断言 14 覆盖：磁盘冲突 → `b (1).txt`，批内互撞 → 首项保留原名、次项 `same (1).txt`
- [x] 执行完底栏出现结果条 `已改名 N 项 · 撤销(Ctrl+Z)`，点撤销后变 `已撤销 N 项`
      → 修复过"结果条被预览统计覆盖"（`TextChanged` 异步派发导致）

> **M3 期间的三个真实故障**（v1.1 未能预见）：
> ① 缺磁盘冲突检测 → 改名覆盖同名文件、静默丢数据；
> ② CaseOnly 两步改名塞进 IFileOperation 队列 → `0x80070002`（🔧 v1.5 更正：真实病因是「引用尚未生成的临时名」，与大小写无关；该两步方案已整体作废）；
> ③ `File.Exists` 大小写不敏感 → 执行校验误判成功、撤销整批失效。

### M4 —— 观感与单实例（已完成，2026-11-01）

验收（**实测结果**）：

- [x] Mica 背景跟随系统深浅色，启动无白闪
      → 采用 `MicaBackdrop` 默认 Kind；内容区不叠填充层，标题栏与页面一体（§7.4 v1.3）
- [x] 自定义标题栏，拖动/双击最大化/最小化按钮全部正常
      → 标题栏 32px + `TitleBarHeightOption.Standard`，按钮与标题对齐
- [ ] `Enter` 执行 / `Esc` 关闭 / 上下键移动 / `Space` 切勾选
      → 已实现（`KeyDown` + `handledEventsToo`，`Space` 才拿得到），⏳ 待人工确认手感
- [x] 已开着窗口时再右键另一批 → **不开第二个窗口**，原窗口置前并替换列表
      → 已验证：A 运行时启动 B，B 转发后退出，进程数保持 1
- [x] 那个"置前"是真的在前台可交互，不是只在任务栏闪一下
      → 已验证：记事本抢前台后再启动 B，前台窗口被抢回「批量重命名」
- [x] 5000 文件右键到可交互 < 1.5s
      → **实测窗口出现 174ms**（模拟 Shell 管道传入 5000 条路径）
- [x] 实测资源占用
      → 空窗口 **109MB**、5000 项 **139MB**（v1.3 时为 204MB，v1.5 换掉行内 `CheckBox` 后降到 180MB，
        v1.6 按官方模板复刻勾选框后为 139MB）、进程数 1、关窗无残留。
        红线最终为 **120 / 150MB**（§7.8.7）；同状态 PowerRename 为 123MB，差距在 C# 基座

> **M4 期间的两个认知修正**：
> ① Mica 不是"整窗糊一层半透明"——内容区要不要叠填充层取决于要不要分层，叠了反而出现色块分界；
> ② 包图标在任务栏会被垫「强调色底板」（plating）。🔧 **v1.9**：`altform-unplated` 变体无效，正确解法是 `AppWindow.SetIcon(ico 绝对路径)`——显式设窗口图标后，任务栏不再回落到包 logo，底板自动消失。⚠ 不能传 exe 路径（会挑到 .NET 默认图标）。

### M5 —— 安装闭环 + 兜底入口（已完成，2026-11-01）

验收（**实测结果**）：

- [x] 先 `Uninstall.ps1` 再全新 `Install-Sparse.ps1`，一次成功
      → 实测：卸载后包数量 **0**、SendTo 清空；全新安装注册 `Status: Ok` + SendTo 重建
- [x] 改清单 `DisplayName` 重装后菜单文案跟着变
      → 实测：改为「批量重命名·测试」→ 重装 → `Get-AppxPackageManifest` 读回一致；恢复后回到「批量重命名」
- [x] 改 `Identity/@Name` 时脚本自动先卸载旧包（不报 `0x80073CFB`）
      → 脚本改为按 `BatchRename*` **前缀扫描**（Identity 改名后旧包名不同，只按当前名找会漏），Name 或 Publisher 不一致就先卸载。⏳ 未实机改 Identity 验证
- [ ] 「发送到 > 批量重命名」多选一次收到全部路径
      → 快捷方式已创建（`%APPDATA%\Microsoft\Windows\SendTo\批量重命名.lnk`，1.2KB，指向 exe）；
        `ShellPayload.Read()` 已支持**命令行参数**入口（stdin 为空时回落到 argv，跳过 `--` 开关与非路径参数）。
        ⏳ 待人工多选验证
- [x] 应用内「如何让它出现在主菜单」链接能打开设置页（`ms-settings:`）
      → 左栏底部 `HyperlinkButton`「命令没出现在右键菜单？」→ `ms-settings:personalization-contextmenu`，
        失败自动回退 `ms-settings:personalization`

> **为什么不实现传统 verb（§10.2 入口③）**：`HKCU\Software\Classes\*\shell\*` 的 `MultiSelectModel`
> 默认是 `Single`，多选时只传第一个文件——对批量改名工具等于废掉；即使设成 `Document`/`Player`，
> 它也只出现在旧菜单里。SendTo 这条兜底更可靠，且不受 26H2 新菜单折叠影响。

---

## 10. 26H2 适配

### 10.1 事实

26H2（Build 26300）重做了文件资源管理器右键菜单（`[官方]`，KB5127936 发行说明）：

- 菜单更快、更短，内建命令可逐个开关；
- **第三方扩展默认收进「应用扩展」子菜单**，可以在「设置 > 个性化 > 上下文菜单」里选择显示在主菜单 / 子菜单 / 隐藏；
- 「显示更多选项」改为 `Shift + 右键`。

对我们的直接影响：**"右键直接看到"不完全由代码决定**。同一份代码在不同用户的机器上，可能一级可见，也可能在子菜单里，取决于该用户的设置（以及企业策略）。

### 10.2 四个入口与优先级

| 入口 | 实现 | 多选支持 | 可靠性 |
|---|---|---|---|
| **① 原生 Verb（主入口）** | 稀疏包 + `IExplorerCommand` | ✅ 完整 `IShellItemArray` | 唯一原生，但可能被折叠 |
| **② SendTo 快捷方式** | `.lnk` 放 `shell:sendto`，指向 exe，带 `--from-shell` | ✅ **多选也是一次收到全部路径** | 高（不受新菜单影响） |
| ③ 传统 verb | `HKCU\Software\Classes\*\shell\BatchRename` + `MultiSelectModel=Player` | ⚠️ 需显式设 `MultiSelectModel`，默认 `Single` 只传 `%1` | 中 |
| ④ 拖放 / 无参启动 | 窗口 `AllowDrop` + `StorageItems` | ✅ | 兜底 |

⚠️ 关于 ③：很多人以为写个 `*\shell` 就完事，但默认 `MultiSelectModel` 是 `Single`，多选时**只传第一个文件**——对一个批量改名工具来说等于废了。要设 `Document` 或 `Player`（最多 100 项）并配合 `DelegateExecute`/`%*`。即便如此它也只出现在旧菜单里，所以**不能替代 ①**。

### 10.3 应用内引导

左栏底部有「命令没出现在右键菜单？」链接，点击弹说明对话框：

> 26H2 默认把第三方扩展收进「应用扩展」子菜单。
> 最省事：右键菜单最底部有「自定义菜单」按钮，点它直达设置页。
> 手动进入：设置 > 个性化 > 右键菜单。
> 或者绕开：右键 > 发送到 > 批量重命名。

> 🔧 **v1.8：不要试图用 `ms-settings:` 直达「右键菜单」页**
>
> 那个偏好页是 26H2 新增的，没有稳定的 URI。实测 `ms-settings:personalization-contextmenu` 无效，
> 而 `Windows.System.Launcher.LaunchUriAsync` 对**无效 URI 返回 `true` 并打开设置首页**——
> 所以"逐个 URI 尝试、成功即返回"的写法会直接停在首页，用户看到一个毫无帮助的默认页。
>
> 正确做法：对话框给出可操作步骤，只把 `ms-settings:personalization`（这个确定有效）作为兜底按钮。

```csharp
// ms-settings 深链（具体 anchor 可能在后续版本变动，失效则退回到通用页面）
await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-contextmenu"));
// 兜底：ms-settings:personalization
```

`[判断]`：该 URI 的具体 slug 需要在 26H2 实机验证；写代码时做"尝试深链，5 秒无响应则打开通用页"的降级。

---

## 11. 风险清单与排障

### 11.1 风险表（按踩中概率排序）

| # | 现象 | 原因 | 规避 |
|---|---|---|---|
| 1 | 菜单项完全不出现，无任何报错 | CLSID 三处不一致 | GUID 单一真源在 `.h`；`Install-Sparse.ps1` 自动校验；M1 用 harness 先验 |
| 2 | 菜单项在，点了没反应，**什么提示都没有** | DLL 位数不匹配（`0x800700C1`）；`Invoke` 抛异常；`CreateProcess` 失败（exe 被 AV 拦截/被移动） | 固定 x64；`Invoke` 外层 `try/catch`；**所有失败路径都走 `ReportLaunchFailure`（§5.4）弹框 + 落日志**——静默失败是这类工具最招骂的缺陷 |
| 3 | 菜单项在子菜单里，以为没装上 | 26H2 默认折叠 | 应用内引导 + SendTo 兜底（§10） |
| 4 | `Add-AppxPackage` 报 `0x80073CFF` | 未开开发者模式 | 开开发者模式，或改走自签名证书 |
| 5 | 报 `0x800B0109` | 证书链不受信 | `.cer` 同时导入 `LocalMachine\Root` 和 `TrustedPeople` |
| 6 | 报 `0x80073CFB` | 同 Name 但 Publisher/Version 变了 | 脚本先比 Publisher，不同则先 `Remove-AppxPackage` |
| 7 | 主程序 `dotnet publish` 后启动即崩（`0xc000027b`） | unpackaged + self-contained 的已知坑 | 四个属性齐全（§7.1）；发布目录确认有 `Microsoft.ui.xaml.dll` / `Bootstrap.dll` / `resources.pri` |
| 8 | 首启 `REGDB_E_CLASSNOTREG` | `WindowsPackageType` 没设 `None` | 见 §7.1 |
| 9 | `0x80040154` | `EnableMsixTooling=true` 与手写清单打架 | 显式 `false` |
| 10 | dllhost 里完全没法断点 | 进程模型限制 | `OutputDebugString` + DebugView；harness 直调；`tasklist /m` 定位 |
| 11 | 改了 DLL 但行为没变 | dllhost 缓存旧 DLL（文件被占用） | 改前 `Stop-Process -Name dllhost`；或先重启 explorer |
| 12 | 右键卡 1–3 秒 | `GetState` 里做了 IO（路径解析/注册表/网络） | §5.3 硬规则 |
| 13 | 万级文件时资源管理器假死 | 写管道阻塞了 STA 线程 | 1MB 缓冲 + 后台写线程 + `HANDLE_LIST` 白名单 |
| 14 | 仅改大小写"点了没变" | 用 `File.Exists` 校验导致误判成功（NTFS 不区分大小写） | 改用 `ExistsExact` 精确比较（§8.7） |
| 15 | 含 `\n` 的文件名解析错位 | 传参按行分隔 | `\0` 分隔（§3.3） |
| 16 | 用户写 lookaround 正则崩溃 | `NonBacktracking` 不支持 | `NotSupportedException` 静默回退 + 250ms 超时 |
| 17 | 撤销时报"文件已存在" | 目标路径被别的文件占了 | 逐项失效判定（§8.10.3），不覆盖 |

### 11.2 排障命令集

```powershell
# 包状态
Get-AppxPackage -Name BatchRename.Shell | Format-List Name,Publisher,Version,Status,PackageFullName

# 注册表里 PackagedCom 是否写进去了
Get-ChildItem 'HKLM:\SOFTWARE\Classes\PackagedCom\ClassIndex' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like 'A1B2C3D4*' }

# DLL 导出与位数
dumpbin /exports .\bin\publish\BatchRename.Shell.dll | Select-String 'Dll'
dumpbin /headers .\bin\publish\BatchRename.Shell.dll | Select-String 'machine'

# 谁加载了我们的 DLL
tasklist /m BatchRename.Shell.dll

# 包激活错误
Get-WinEvent -LogName 'Microsoft-Windows-AppModel-Runtime/Admin' -MaxEvents 20 |
    Format-Table TimeCreated,Id,Message -Wrap

# 强制重载 Shell
Stop-Process -Name explorer -Force; Start-Sleep 1
# 图标缓存（仅当图标不对时）
ie4uinit.exe -show
```

### 11.3 排查决策树

```
菜单没出现
├─ 包注册失败？ → Get-AppxPackage 无输出 → 看安装脚本报错码（§11.1 #4/#5/#6）
├─ 包在，Status=Ok？
│   ├─ tasklist /m 无输出 → DLL 没被加载
│   │   ├─ 导出缺失 → WRL Module 没实例化 / 编成静态库
│   │   ├─ 位数不对 → 重编 x64
│   │   └─ CLSID 不一致 → 三处 diff
│   └─ 有输出但菜单仍无 → 26H2 折叠（去设置看）或 GetState 返回了 ECS_HIDDEN
└─ 全过 → 换一个用户配置/新用户账户交叉验证是不是策略（GPO）挡了
```

---

## 12. 附录

### 12.1 关键 API 速查

| 用途 | API |
|---|---|
| 菜单命令 | `IExplorerCommand`（`shobjidl_core.h`） |
| 选中项 | `IShellItemArray` → `EnumItems` → `IEnumShellItems` → `IShellItem` |
| 取物理路径 | `IShellItem::GetDisplayName(SIGDN_FILESYSPATH, ...)` |
| 判断是否文件系统对象 | `IShellItem::GetAttributes(SFGAO_FILESYSTEM, ...)` |
| 创建 shell item | `SHCreateItemFromParsingName` |
| 文件操作 | `IFileOperation` / `IFileOperationProgressSink` |
| 卷大小写属性 | `GetVolumeInformationW` 的 `FILE_CASE_SENSITIVE_SEARCH` |
| COM 注册（打包） | `com:Extension Category="windows.comServer"` + `com:SurrogateServer` |
| 菜单注册 | `desktop4:Extension Category="windows.fileExplorerContextMenus"` + `desktop5:ItemType/Verb` |
| 稀疏包注册 | `Add-AppxPackage -Register <manifest> -ExternalLocation <dir>` |
| 前置窗口 | `AllowSetForegroundWindow`（前一个前台进程调用）+ `SetForegroundWindow` |

### 12.2 命名与版本约定

| 项 | 约定 |
|---|---|
| 解决方案 | `BatchRename.sln` |
| Shell 工程 / 输出 | `BatchRename.Shell` → `BatchRename.Shell.dll` |
| 主程序工程 / 输出 | `BatchRename.App` → `BatchRename.App.exe` |
| 包名 | `BatchRename.Shell`（与工程同名，便于排障时一眼对上） |
| Verb Id | `BatchRenameFiles` / `BatchRenameFolders` |
| CLSID | 自建固定 GUID，写在 `.h` 的 `DECLSPEC_UUID`，**永不变更** |
| journal 目录 | `%LOCALAPPDATA%\BatchRename\journal\` |
| 日志目录 | `%LOCALAPPDATA%\BatchRename\logs\`（`shell.log` / `app.log`，各留 1MB 循环） |
| 临时改名前缀 | ~~`$br$`~~ 🔧 v1.5 已作废：所有项直接进同一队列，不再产生中间态 |
| 版本号 | `Major.Minor.Patch.Build`，清单 `Version` 必须四段 |

### 12.3 待确认 / 可能需要调整的点

| 项 | 当前默认 | 何时需要改 |
|---|---|---|
| UI 语言 | 中英资源，跟随系统 | 只想中文就删掉 `en-US` |
| 冲突自动编号格式 | `name (1).ext` | 想要 `name-1.ext` 之类，改 `ConflictResolver` 一处 |
| 撤销期限 | 7 天 / 20 批 | 觉得短就调常量 |
| 执行后行为 | 只改文件名，不打开资源管理器 | 想"改完自动选中"再加 |
| 是否做拖放 | 做（低成本兜底） | 不做可省 ~50 行 |
| 是否需要 ARM64 | 不做（x64 走仿真） | 换 ARM 机器且要求原生性能时再加一个平台配置 |
| 托盘图标 / 开机自启 | 都不做（无常驻，见 §7.7） | 若哪天真觉得 1 秒冷启动烦，加托盘 + 空闲退出即可，实现路径已在 §7.7 留档 |

---

## 附：本文档的边界

- 不含可编译工程。所有 C#/C++ 片段是**契约级**（接口签名、状态机、关键循环），抄进去还要补 using、命名空间和错误处理。
- 所有 `[判断]` 结论必须在 Windows 上实测。特别是：`IFileOperation` 对仅大小写改名的内部处理、`ThreadingModel="Both"` 的实际行为、`ms-settings:personalization-contextmenu` 这个 slug 是否有效。
- 26H2 的右键菜单仍在灰度推送（Controlled Feature Rollout），不同机器同一 build 的菜单形态可能不同。验收时以自己机器的实际行为为准，不要拿别人的截图对照。


---

## 修订历史

> 按时间**倒序**排列，最新的在最前。编号在各版本内独立。
> 正文里的 `🔧 vX.Y` 标注指向这里的对应条目。

## v1.12 补充（2026-11-02：文档与代码对齐 + 校验脚本）

起因是一次「文件里还有多余或过时的内容么」的排查：发现 §6.4 声称的脚本全文比真文件少 80 行，另有多处引用已删除的评审文档。本轮把口径一次性拉齐，并加了机器校验防止再漂。

| # | 位置 | 问题 | 处理 |
|---|---|---|---|
| 1 | §6.4 | 「`Install-Sparse.ps1` 全文」停在 79 行，真文件 159 行（缺 `-Encoding UTF8` 修复、`$RepoRoot` 参数化、**Assets 同步**、SendTo 创建、CLSID 一致性校验、`-SkipExplorerRestart`） | 用真文件重写；并在 §6.2/§6.4 顶部标明「由 `tools/Verify-Docs.ps1` 校验」 |
| 2 | §v1.3 #7 / §8.10 / §9 | 三处写「见 REVIEW §11 / §7.6」，而该评审文档已在 v1.11 删除 | 改指本文 §7.8.7，或直接写明结论 |
| 3 | §6.2 | `MinVersion` 写 `10.0.19041.0`（=Win10 2004），README 却要求 26H2 | 清单改 `10.0.22621.0`（Win11 22H2）；安装器加运行时门槛：`< 22621` 直接拦、`< 26300` 只警告「菜单项可能不进主菜单，用发送到」 |
| 4 | 新增 `tools/Verify-Docs.ps1` | 文档镜像与真文件、断言条数、悬空引用三类漂移全靠人眼 | 三个校验合一个脚本，失败返回 1；CI 在 `dotnet test` 之前执行 |
| 5 | README | 「失败提示只显示首条原因」（实际是 ≤200 行的失败清单）、「图标旧了就重启资源管理器」（实测还要 kill `dllhost.exe`）、递归限制与常见问题重复 | 三处纠正，删重复段 |
| 6 | ARCHITECTURE §7.2 / §7.5 | 产物图里有 `packaging/Assets → bin/publish/Assets` 的箭头，却没写谁拷的、不拷会怎样 | 补「由 Install-Sparse.ps1 注册前拷贝」+ 换图标后要清 dllhost 图标缓存；新增 §7.5 文档校验 |
| 7 | `ConflictResolver.cs` | 注释引用已删除评审文档的 P3 TODO | 去掉引用，保留抽取理由 |



## v1.11 补充（2026-11-02：文档收尾与工程清理）

本轮不涉及功能变更，只做文档结构整理与残留清理。

| # | 位置 | 变化 |
|---|---|---|
| 1 | `README.md` | 重写为**面向用户**：功能、安装、使用、快捷键、常见问题、系统要求、已知限制。技术内容（环境要求、构建、测试、打包、目录结构、维护约定）全部移交 ARCHITECTURE |
| 2 | `docs/ARCHITECTURE.md` | 新增 §6.1 与 PowerRename 的对照、§7.1 环境要求、§7.3 测试、§7.4 打包、§10 踩坑清单；§9 由「扩展点」改为「里程碑状态与后续方向」 |
| 3 | `docs/REVIEW-2026-11-01.md` | 693 行的评审记录，架构级结论并入 ARCHITECTURE（§6.1 / §10），问题修复记录并入本文 v1.5–v1.10，原文件删除 |
| 4 | 断言数 | 全库统一为 **124 项**（此前 33 / 67 / 79 / 118 多个版本并存） |
| 5 | 过时标记 | M1 验收里的「Shift+右键 传统 verb」由 `[ ] 未实现` 改为 `[x] 有意不做` 并写明原因 |


## v1.10 补充（2026-11-01：跨位置安装 + 单文件安装器）

| # | 位置 | 问题 | 更正 |
|---|---|---|---|
| 1 | §6.2 安装 | 旧包处理只看 `Identity`/`Publisher` | **还要看安装位置**。稀疏包绑死绝对路径，从 `E:\Project\...\packaging` 装过的包不能直接从 `%LOCALAPPDATA%\...\packaging` 重注册，会报 `0x80073D0B`（"无法移动，因为该程序包正被使用或安装在其它外部位置"）。脚本现在比较 `Get-AppxPackage.InstallLocation` 与清单目录 |
| 2 | §6.2 安装 | 卸载前不停进程 | `Remove-AppxPackage` 在 dllhost 加载着 Shell DLL 时会报 `0x80073D02`。卸载前必须先停 App 进程 + 按模块名过滤停 dllhost |
| 3 | §6.2 分发 | 只有 zip + 脚本 | 新增 `tools/Build-Setup.ps1`：把 payload 内嵌进一个自包含单文件 exe（约 96MB），双击即装、写「应用和功能」条目、支持 `/S` 静默模式 |



## v1.7 修订记录（2026-11-01：内存实测，证伪 x:Bind 优化方向）

v1.7 来自一次受控 A/B 实验：把行模板的 7 处 `{Binding}` 全部换成静态文本后对比内存。

| # | 位置 | v1.6 的问题 | v1.7 更正 |
|---|---|---|---|
| 1 | §7.8.1 | 分场景预算仍是 v1.0 的 `[判断]` 推算值（50–70MB / 65–95MB） | 全部换成实测（私有提交）：空窗 **114MB**、500 项 **135MB**、2000 项 138MB、5000 项 **143MB** |
| 2 | §7.8.7 | v1.3 判断「5000 项多出的 ~94MB 来自 `RenameItem` 与 ListView 的绑定/布局对象」，并把「改 `x:Bind`」列为优化方向 | **实测证伪**：绑定层总开销**仅 2MB**（186 → 184MB），每项数据仅 1.8KB（500→5000 项只涨 8MB），列表首屏固定开销 21MB。`x:Bind` / 抽 `UserControl` 是**CPU 优化**（省反射），不是内存优化，已从待办移除 |
| 3 | §7.8.7 红线 | 无「有没有列表」这个维度 | 新增：空窗 → 显示列表的 **+21MB 固定开销与项数无关**，优化逐项成本没有空间 |
## v1.6 修订记录（2026-11-01：评审问题集中修复，编号延续 v1.5 表）

| # | 位置 | 问题 | 更正 |
|---|---|---|---|
| 13 | §6.4 | 安装脚本 `Get-Content -Raw` 未指定编码；清单是无 BOM 的 UTF-8，PS 5.1 按 GBK 解码 → `[xml]` 抛异常、脚本停在第 28 行 | 显式 `-Encoding UTF8`（两处），两个 `.ps1` 改存 **UTF-8 with BOM**（PS 5.1 对无 BOM 脚本按 ANSI 解码，中文注释可能吃掉后续行） |
| 14 | §8.4 / §7.4 | `IsRenamed` 项在 `RenameEngine.Run` 里被直接 `continue` 跳过：同一窗口内**永远无法二次改名**，用户感知为"改过的项被锁定" | `IsRenamed` 降级为**显示标记**：项照常参与规则计算；只有"当前规则对它无作用"时才回显 `State=Renamed` / 「已改名」。二次改名恢复可用 |
| 15 | §8.8 | 只查了「批内互撞」和「磁盘已存在」，**没查批内循环**：`a→b` + `b→a` 两项都被"源名会让出"放行，执行时 Shell 在 `FOF_NOCONFIRMATION` 下静默覆盖（实测原 `b.txt` 内容永久消失） | 新增 `MarkBatchCycles`：沿"目标名 = 另项源名"走，走回本轮起点即成环，整环标 `ConflictInBatch` / 「批内循环改名」。O(n)，链不误伤 |
| 16 | §8.9.2 | 入队顺序就是列表顺序；链式改名 `a→b` + `b→c` 若先执行 `a→b`，`b` 的原内容被静默覆盖 | 入队前 `OrderByDependency` 做依赖排序（目标是别人的源名 → 别人先动）；无依赖时保持原顺序，零成本 |
| 17 | §8.9.4 | v1.2 决定"不实现 sink，执行后按名字校验" | 改为**实现 `IFileOperationProgressSink::PostRenameItem`**：逐项成败与 HRESULT 由 Shell 直接给。删掉 O(n²) 的名字校验（实测 4000 项约 2.9s、撤销翻倍），也修掉"链式改名把成功的项误报失败"；`CaseOnly` 项另外用 `ExistsExact` 复核一次 |
| 18 | §7.4 / §8.9.5 | 执行后无条件清空查找/替换框（v1.4 的取舍：当时改过的项被锁定，清空只是"眼不见"） | **不再清空**。P1-7 之后已改名项照常参与计算，清空只会丢掉用户刚输入的内容（失败项无法直接重试，想再套一轮得重敲）。规则留着时，已改名项不命中规则会回显「已改名」，命中就如实显示"还会再改一次" |
| 19 | §8.11 | 只有单层 150ms 防抖，且 `RefreshPreview` 内含整目录枚举：每敲一个字都要枚举一遍所有涉及目录 | **两层防抖落地**：150ms 纯内存（规则 + 批内互撞/环）+ 400ms 磁盘检查（`Run(..., checkDisk: false)`）。执行前仍强制跑一次完整校验，不会拿"没查过磁盘"的状态去改文件 |
| 20 | §7.5 | `Ctrl+A` / `Space` 改完勾选后「全选」复选框状态不同步；`Esc` 直接关窗，未执行的规则白敲 | 复选框改三态并与列表同步（`SyncSelectAllBox`）；有未执行改动时 `Esc` 弹二次确认 |
| 21 | §7.4 | 状态色硬编码、浅色主题下绿/黄对比度不足；`AppWindow.Resize` 用物理像素，150% 缩放下窗口只有 787×480 DIP | 颜色优先取 `SystemFillColor*Brush` 主题资源（取不到才退回固定色）；窗口尺寸按 `GetDpiForWindow` 换算 |
| 22 | §5.5 / §6.7 | 单实例管道 `while(true)` 无退避（持续失败会吃满一个核）；`active.pid` 永不清理（`Dispose` 无人调用）；csproj 里留着「临时」的 `EmitCompilerGeneratedFiles` | 失败按 250ms×(次数) 退避并写 `app.log`（上限 4s）；`Window.Closed` → `Dispose` 清 pid + 释放 mutex；删掉临时开关（需要时命令行加 `-p:EmitCompilerGeneratedFiles=true`） |
| 23 | §8.9.5 / §8.11 | 执行期只有不确定态进度环、没有取消按钮；`CancellationToken` 接受但没人用 | **逐项进度 + 取消落地**：sink 的 `PostRenameItem` 按 ~100 次/批 上报 `(完成, 总数)`，UI 切线程画确定态进度条并显示「正在改名 34 / 96 …」；「取消」置位令牌 → `PreRenameItem` 抛 → Shell 停后续。**坑**：此时 `PerformOperations()` 不返回错误码，而是把失败 HRESULT 变成 `OperationCanceledException` 抛出，必须就地捕获，否则会被当成"执行中断" |
| 24 | §7.4 / §7.8.7 | 行内勾选用 `CheckBox`；5000 项实测 180MB | **换成 Border + FontIcon 指示灯 + 整行命中**（`BoolToVisibilityConverter`）。实测分解：空窗 110.4MB；显示 10 项 133MB；显示 200/1000/5000 项 179.6/177/180MB —— 即"每项一个容器"并不成立（项数翻 25 倍内存不变），真正贵的是**每个已实现行的 `CheckBox`**。换掉后 5000 项 **137MB**（−43MB）。<br>**两个坑**：① `ListViewItem` 会吃掉子元素的 `Tapped`，只能走 `ListView.ItemClick`；② `ItemClick` 不给点击位置，用 `PointerPressed`（`handledEventsToo: true`）记下 x，落在左侧 56px 勾选列内才切换，其余位置保持"选中"。<br>**推论**：`x:Bind` / `UserControl` / `ItemsRepeater` 都压不动这笔开销，因为它们优化的是逐项容器与绑定，而瓶颈是行内模板化控件 |
| 25 | §7.4 | 行模板允许"4 个叶子控件"的表述已被实测推翻 | 规则改为：**行内只允许叶子元素（`TextBlock`/`FontIcon`/`Border`），禁止任何模板化控件**（`CheckBox`/`ToggleButton`/`Button`）。需要交互时走 `ItemClick` + `PointerPressed` 记 x 判列（见 #24），不要直接给叶子元素挂 `Tapped` |
| 26 | §7.4 | 用户要求"不用官方组件，但形态和官方完全一致" | 用两个 `Border`（未勾选/已勾选各一层，`Visibility` 二选一）+ `FontIcon` 复刻官方 CheckBox。**像素级对齐**（同窗口对照官方「全选」实测，1.25 缩放）：20×20 DIP 方框、`ControlCornerRadius` 圆角；勾选态 `AccentFillColorDefaultBrush` 填充（两侧实测同为 `(228,164,124)`）、`E73E` 勾字形 12px + `TextOnAccentFillColorPrimaryBrush`；**未勾选态必须 `Transparent` 填充**（官方内部像素与背后面板只差 3；铺 `ControlFillColorDefaultBrush` 会明显发白）；描边 `ControlStrongStrokeColorDefaultBrush`。代价 **139MB**（简单版 137MB、官方 `CheckBox` 180MB）。<br>**坑**：`CheckBoxFillUnchecked`/`CheckBoxStrokeChecked`/`CheckBoxForegroundChecked` 这些键是 **UWP 命名，WinUI 3 里不存在**，`ThemeResource` 解析期直接抛 `Cannot find a Resource`，进程起来就崩（见 `app.log`）。<br>**仅剩差异**：官方有 PointerOver/Pressed 颜色过渡动画，这里没有——要那层动画就必须换回模板化控件（内存回到 180MB 量级） |
| 27 | §7.4 状态列 | 代码里"优先取系统主题资源、取不到退回写死 RGB"，实际**一直走写死**：`Application.Current.Resources` 不含系统主题字典（查不到）；缓存按 `Application.RequestedTheme` 判深浅且切主题不清缓存 | 改成**颜色由 XAML 声明、代码只取对象**：`RootGrid.Resources` 里声明 5 个 `SolidColorBrush`，`Color="{ThemeResource SystemFillColorCritical/Caution/Success/TextFillColorSecondary}"`；`Renamed` 用资源别名 `<StaticResource x:Key="RowStateAccent" ResourceKey="AccentTextFillColorPrimaryBrush" />`（`AccentTextFillColorPrimary` 这个 **Color** 键在 WinUI 3 里不存在）。转换器只按 key 从窗口资源取，**不缓存**（缓存会把旧 brush 钉死，切主题不生效）。实测五支：`#C5FFFFFF / #FF6CCB5F / #FFFCE100 / #FFFF99A4 / #FFF3C59B` |
| 28 | §7.4 全选 | `IsThreeState="True"` + `Checked/Unchecked/Indeterminate` 回调：点击循环是 false→true→null→false，从"全勾"点一下会落到 `null`，而处理器对 `null` 直接 return → 框停在中间态、列表毫无变化（假死） | 改 `Click="OnSelectAllClick"`：中间态**只用于显示**（部分勾选），点击意图一律从列表真实状态推断（不是全勾→全勾；已全勾→全不勾），处理完立刻 `SyncSelectAllBox()` 纠正显示。实测两次点击：全勾 ↔ 全不勾，中间态不再被用户点出来 |
| 29 | §7.4 表头 | 列头放在 `ListView.Header` 里，跟着列表一起滚走，5000 项时对不上位 | 移出列表改成固定行（`Border` + 三列，列宽与行模板一致 `40/*/28/*/110`），底部加 1px `CardStrokeColorDefaultBrush` 分隔线；列表移到第 2 行、结果条第 3 行 |
| 30 | §8.9.5 失败清单 | 底栏只能显示首条失败原因，其余项只在状态列有红字 | 执行完（`finally` 里 `SetBusy(false)` 之后）弹 `ContentDialog`：逐项列「原名 → 新名 + 原因」，最多 200 行，超出提示用「复制清单」（`DataPackage` + `Clipboard`）带走全部。**坑**：不能在 `try` 里 `await` 对话框——`SetBusy(false)` 在 `finally`，会被对话框挡住，表现为弹窗时底栏还写着「正在改名 N / N」且取消按钮还亮着 |
| 31 | §4.3 分层 | `RenameEngine` 465 行：名字计算（`BuildNewName`）、合法性（`Validate`）与冲突判定（批内互撞/循环/磁盘/自动编号）混在一起 | 抽出 `Core/ConflictResolver.cs`（227 行，`internal static`）：只关心"名字之间/名字与磁盘"的冲突，契约是**只读入参列表、就地写回 State/Message/NewName**，不碰规则、不写磁盘；`RenameEngine` 降到 262 行。调用顺序在 `Run()` 里显式保留：互撞 → 循环 → 磁盘 → 编号。**坑**：搬过去的方法默认还是 `private`，要在 `ConflictResolver` 里改成 `internal`，否则 `Run()` 直接编译不过 |
| 32 | §9 测试 | 124 条断言只有 console 入口，没有 `dotnet test`、没有 CI | 断言体抽成 `tests/BatchRename.Core.Tests/CoreSuite.cs`（**唯一实现**，`CoreSuite.RunAsync(report)` 返回 `(Checks, Failures)`）：console 入口 `BatchRename.ShellTest` 只负责逐条打印，xUnit 入口 `CoreSuiteTests` 断言`Failures == 0` 且 `Checks == 124`（条数守卫，防止悄悄少跑）。加 `.github/workflows/ci.yml`（windows-latest：test + App build + console build）。**坑**：① xUnit 项目必须和 ShellTest 一样带 `Microsoft.Windows.CsWin32` + 同一份 `NativeMethods.txt`，否则 Core 里的 `IShellItem`/`PCWSTR`/`HRESULT` 全找不到；② 断言里有 `await`，`Run` 必须是 `async Task<Result>`；③ `Console.WriteLine()` 被批量改成 `Note()` 后要给它默认参数；④ CI **不要** `dotnet build BatchRename.sln`——C++ 的 `BatchRename.Shell.vcxproj` 需要 MSBuild.exe/VS 工具链，dotnet CLI 会报 `VCTargetsPath` 缺失 |

> 断言测试同步扩到 **124 项**（新增：15 组 Pending 回滚 10 项、16 组目录/文件大小写改名 6 项、14 组扩展名+编号 2 项、17 组中断回写 4 项、18 组二次改名 4 项、19 组链式顺序 4 项、20 组循环检测 5 项、21 组逐项失败 4 项、22 组两层防抖 3 项、23 组进度 3 项、24 组取消 3 项）。

---

## 目录

- [0. 文档说明](#0-文档说明)
- [1. 需求与范围](#1-需求与范围)
- [2. 技术选型](#2-技术选型)
- [3. 系统架构](#3-系统架构)
- [4. 工程结构](#4-工程结构)
- [5. Shell 扩展规格](#5-shell-扩展规格)
- [6. 打包与注册](#6-打包与注册)
- [7. 主程序规格](#7-主程序规格)
- [8. 重命名引擎规格](#8-重命名引擎规格)
- [9. 里程碑与验收](#9-里程碑与验收)
- [10. 26H2 适配](#10-26h2-适配)


## v1.5 修订记录（2026-11-01：系统级撤销、journal 降级、CaseOnly 两步作废）

v1.5 依据真实 `IFileOperation` 探针 + 资源管理器实发 `Ctrl+Z` 的端到端实测修订（探针方法与结论见 ARCHITECTURE §10）。

| # | 位置 | v1.4 的问题 | v1.5 更正 |
|---|---|---|---|
| 1 | §0.3 第 8 条 | 「Explorer 的系统级 Ctrl+Z 无法撤销其它进程发起的批量重命名」标 `[判断]` | **错**。`FOFX_ADDUNDORECORD (0x20000000)` 就是把操作放进 Shell 撤销栈（官方文档："should be placed on the undo stack, preferred to FOF_ALLOWUNDO"）。实测：改名进程退出后，资源管理器 `Ctrl+Z` 仍能撤回，一次一批、LIFO |
| 2 | §8.9.2 | flags 里"有意不加 `FOF_ALLOWUNDO`（撤销我们自己记）" | 改为 **`FOFX_ADDUNDORECORD`**（正向改名加，journal 驱动的反向执行不加，否则会多出"撤销的撤销"记录） |
| 3 | §8.9.2 / §8.10.4 | CaseOnly 项走 `File.Move` 两步中转 | **整条作废**。实测 `File.Move` 与 `IFileOperation::RenameItem` 都能直接做仅大小写改名（**文件与目录**都行）；而 `File.Move` 动不了目录，旧实现让「文件夹名只改大小写」必然失败。现在所有项一律进同一个 `IFileOperation` 队列 |
| 4 | §8.9.1 | 未调 `SetOwnerWindow` | 补上。否则 UAC 提示框与 Shell 错误框没有 owner，可能弹在窗口后面 |
| 5 | §8.10.1 | 20 个 journal、7 天有效期、`RuleSummary` 用于列表 | 降级为**单次操作事务日志**：只有一个 `last-batch.json`（覆盖写）。删掉 7 天判定、`Prune(20)`、多批次列表。journal 只留两个职能：write-ahead 崩溃标记 + 软件内「撤销」按钮 |
| 6 | §8.10.2 | 崩溃恢复对话框调 `UndoAsync`，而 `UndoAsync` 明确拒绝 `Pending` | 新增 **`RollbackPendingAsync`**：回滚按钮 100% 失败的问题（旧版实测 `Rejected=True`）修好 |
| 7 | §8.10.3 | 撤销部分失败也无条件写 `Undone` | 只有**全部成功**才定格 `Undone`；部分失败保留原状态，允许重试。新增「已是原名」计数，避免重试时把已回滚项误报成"已失效" |
| 8 | §8.9.5 / §7.6 | 软件内撤销按钮只认内存里的 `_lastJournal`，关窗重开即失效 | 启动时 `LoadLast()` 挂回最近一批（`Applied` 才点亮按钮） |
| 9 | §7.4 | 执行前不刷引擎，150ms 防抖窗口内点「执行」会用旧规则 | `OnExecute` 开头 `_debounce.Stop(); RefreshPreview();` |
| 10 | §8.10.1 | 示例 JSON 用 camelCase | 实际落盘是 PascalCase（System.Text.Json 默认不改名），示例已按实际输出更正 |
| 11 | §7.3 / §8.9.5 | `OnExecute` 是裸 `async void`：任何异常都冒到 DispatcherQueue → 进程静默消失，journal 永久卡 Pending | 加 try/catch：按**磁盘现状**回写逐项 `Ok`（`ReconcileFromDisk`）、写 `Error` 字段、日志落 `app.log`、窗口存活。App 另挂三个异常兜底钩子（Application / AppDomain / TaskScheduler） |
| 12 | §8.8 | 自动编号用**改名前**的 `item.Ext` 反推 stem → 改扩展名时产出 `a.md (1).txt`（编号跑到扩展名之后，扩展名还被打回原样） | 改为 `NameSplitter.Split(item.NewName, …)`，编号加在新扩展名之前 |



## v1.4 补充（M3 遗留项回补）

| # | 位置 | 问题 | 更正 |
|---|---|---|---|
| 1 | §8.10.2 | 实现只写了 `state=Applied`，**没回写逐项 ok** | 已在执行后按实际结果回写。不回写的话，失败项在 journal 里仍是 `Ok=true`，撤销会去回滚根本没改名的项 |
| 2 | §8.8 | 「自动编号」只在规格书里，未实现 | 已实现：`n=0` 先试原名（批内互撞时第一项应保留原名），之后 `stem (n).ext`，上限 999 |
| 3 | §7.4 | 底栏缺「全选/全不选」 | 已补为 `CheckBox`，位于撤销/执行左侧；`Ctrl+A` 保留 |

> 断言测试同步扩到 **79 项**（新增自动编号 9 项、Pending journal 3 项）。<br>（后续批次继续演进：92 → 102 → 115 → 118 → **124 项**，见文末 v1.5/v1.6 修订表）



## v1.3 修订记录（M4 界面、单实例与实测校准）

v1.3 依据 M4（观感 + 单实例）的实现与真机实测修订。**#7 是实测数据对 v1.0 预估的校准**，不是代码缺陷。

| # | 位置 | v1.2 的问题 | v1.3 更正 |
|---|---|---|---|
| 1 | §7.4 | 内容区叠 `LayerFillColorDefaultBrush` | Mica 只作整窗底层，**内容区不叠连续填充层**；否则标题栏与内容之间会出现明显色块分界，和 WinUI Gallery / PowerRename 的观感不一致 |
| 2 | §7.4 | `MicaBackdrop Kind="BaseAlt"` | 改用默认 **`Base`**。BaseAlt 是给「选项卡式标题栏」应用增强对比用的，普通工具用它会显得材质过重 |
| 3 | §7.4 | 标题栏 48px 高 | 系统 caption 默认 32px，两者不一致时窗口按钮与标题**垂直错位**。元素高度与 `TitleBarHeightOption.Standard` 必须同为 32px |
| 4 | §7.4 | 规则栏横排在顶部 | 改为 PowerRename 式：左 300px 操作区（纵向）+ 右预览卡片；撤销/执行贴左栏底部，结果条固定在卡片内底部 |
| 5 | §5.2 | `GetIcon` 返回 `exe路径,0` | 改返回独立的 **`Assets\menu.ico`**。exe 图标受 Shell 图标缓存影响，替换后菜单可能长期显示旧图，且无法把菜单图标与应用图标分开设计 |
| 6 | §7.6 | Mutex 名 `Local\BatchRename.SingleInstance.{PackageFamilyName}` | 应用由 Shell `CreateProcess` 启动，`Package.Current` 不可用，改固定名 |
| 7 | §7.8.7 | 空窗口 80MB / 5000 项 110MB 红线 | **实测 110MB / 204MB**。v1.0 的数字是 `[判断]` 推算而非实测，明显偏低；红线调整为 120MB / 220MB。<br>🔧 **v1.6 复核**：行内 `CheckBox` 换成自绘勾选框后 5000 项降到 **139MB**，红线收紧为 **120MB / 150MB**（§7.8.7）；同状态下 PowerRename 0.102 为 123MB，差距在 C# 运行时基座（实测数据与分解都在 §7.8.7） |
| 8 | §6.1 | 未提包图标的 plating | 只给默认 logo 时，任务栏会给图标垫一块**强调色底板**（plating），圆角外露出的就是系统 accent color。<br>🔧 **v1.9 更正**：`altform-unplated` 变体**实测未被采纳**（44/150/50 全套生成后仍带底板）。真正的解法是 `AppWindow.SetIcon()`，见 §6.1 |



## v1.2 修订记录

v1.2 依据 M3（执行 + 撤销）的实现与真机测试结果修订。**其中 #2 与 #5 是会导致数据丢失的严重问题**，已就地更正。

| # | 位置 | v1.1 的问题 | v1.2 更正 |
|---|---|---|---|
| 1 | §7.1 / §8.9.3 | CsWin32 清单写 `<None Include="Core\NativeMethods.txt" />` | 必须挂 **`<AdditionalFiles>`**，否则源生成器读不到，等于没配 |
| 2 | §8.8 | 只把「磁盘已存在」检测算作 M3 的事 | **M2 预览阶段就必须做**。缺失时改名会直接覆盖同名文件，**静默丢数据**，且用户完全无感 |
| 3 | §8.9.2 | CaseOnly 在同一 `IFileOperation` 内连续入队两次 `RenameItem`，第二次按临时名重建 `IShellItem` | **行不通**。`PerformOperations` 之前临时文件根本不存在，`SHCreateItemFromParsingName` 返回 `0x80070002`；IFileOperation 队列无法引用前序结果。改用 `File.Move` 两步 |
| 4 | §8.9.4 | 回调方法名写 `PostRename` | 实际接口是 **`PostRenameItem`**。本项目最终不实现 sink，改为执行后用文件系统校验结果（省掉 15 个回调方法） |
| 5 | §8.7 / §8.10.3 | 未提大小写敏感陷阱 | **`File.Exists` 在 NTFS 上忽略大小写**，绝不能用于：① 校验「仅大小写改名」是否生效；② 判断撤销时源名是否被占用。两处都会把整批判成失败/失效 |
| 6 | §5.2 | `GetTitle` 返回「批量重命名 (N)」 | 改为固定「批量重命名」。数量在打开的窗口里看更直观，菜单标题每次变反而干扰定位 |



## v1.1 修订记录

v1.1 依据 `E:\Project\BatchRename` 的**实际编译与运行结果**修订。以下 8 处均为 v1.0 写错或写漏、到实现阶段才暴露的问题，正文对应位置已就地更正并标注 `🔧 v1.1`。

| # | 位置 | v1.0 的问题 | v1.1 更正 |
|---|---|---|---|
| 1 | §0.4 / §7.1 | 写 `Windows App SDK 2.5.x` | 实测 NuGet 最新稳定版是 **2.6.4**，已在项目中使用 |
| 2 | §4.4 | 链接依赖漏 `user32.lib` | `MessageBoxW` 需要它，缺了直接 `LNK2019` |
| 3 | §5.1 | 头文件声明未带 `noexcept`，但 §5.3 实现带了 | 声明与定义必须一致，否则 `C2382` |
| 4 | §5.1 | 称 `wrl/module.h` 会自动导出三个函数 | **不会**。必须用 `.def` 或 `__declspec(dllexport)`；且签名要用 `STDAPI` 对齐 `combaseapi.h`，否则 `C2375` |
| 5 | §5.4 | `SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0)` | **应为 `writeEnd`**。清掉 `readEnd` 的继承标志再放进 `HANDLE_LIST` 会让 `CreateProcess` 返回 87 |
| 6 | §7.1 | 只提 `PublishSingleFile` 会导致 `0xc000027b` | WinAppSDK **2.6.4 的 publish 不生成 `resources.pri`**，非打包应用缺它必崩，已在 csproj 加 Target |
| 7 | §8.5 | `WordSeps` 含 `'`，却要求"`don't` 不变 `Don'T`" | 自相矛盾。`'` 已移除，且非字母数字字符不改变词首状态 |
| 8 | §5.4 / §6.7 | 日志路径写 `%LOCALAPPDATA%\BatchRename\logs\shell.log` | dllhost 在包容器内运行，实际被虚拟化到 `...\Packages\BatchRename.Shell_*\LocalCache\Local\BatchRename\logs\` |

另附两条 WinUI 3 工程约束（v1.0 未提及），见 §7.4 末尾。


