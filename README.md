# BatchRename

Windows 11 资源管理器右键菜单里的批量重命名工具，对标 PowerRename，个人自用、非商业。

选中 N 个文件/文件夹 → 右键「批量重命名」→ 实时预览「原名 → 新名」→ 执行 / `Ctrl+Z` 撤销。

## 当前状态

| 里程碑 | 范围 | 状态 |
|---|---|---|
| **M1** | 右键菜单出现 + 拉起窗口 + 路径透传 | ✅ 已通过验收 |
| **M2** | 重命名引擎 + 实时预览（33 项自动断言） | 引擎 ✅ / UI ✅ |
| **M3** | 执行 + 撤销（`IFileOperation` + WAL journal） | ✅ 已通过真机验证 |
| **M4** | Mica / 自定义标题栏 / 键盘 / 单实例 | ✅ 已通过真机验证 |
| **M5** | 安装闭环 + 传统 verb 兜底 | ⬜ 未开始 |

当前基线标签：`v0.3-M4`

## 环境要求

| 组件 | 版本 | 说明 |
|---|---|---|
| Windows | 11 26H2 / Build 26300+ | 稀疏包清单的 `MaxVersionTested` 按此填 |
| Visual Studio | 2026 | 需勾选「.NET 桌面开发」+ **「使用 C++ 的桌面开发」**（含 MSVC 工具集，缺 `include` 目录会无法编译） |
| Windows SDK | 10.0.26100.0 | |
| .NET | 10 SDK | `dotnet --list-sdks` 应能看到 10.x |
| Windows App SDK | 2.6.4 | 走 NuGet，无需单独安装 |
| **开发者模式** | **必须开启** | 稀疏包免签注册的前置条件，未开报 `0x80073CFF` |

开启开发者模式：

```powershell
# 管理员 PowerShell
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
  -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -Type DWord
```

## 快速开始

### 1. 构建

**方式 A：Visual Studio / MSBuild（推荐）**

```powershell
msbuild BatchRename.sln /p:Configuration=Release /p:Platform=x64 /m
```

或用 VS 2026 直接打开 `BatchRename.sln`，选 `Release | x64` 生成。

**方式 B：命令行分部构建**

```powershell
# Shell（C++，输出到 bin\publish\BatchRename.Shell.dll）
msbuild src\BatchRename.Shell\BatchRename.Shell.vcxproj /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=%~dp0

# 主程序（C#，publish 到 bin\publish\BatchRename.App\）
dotnet publish src\BatchRename.App\BatchRename.App.csproj -c Release
```

> 若当前环境限制 `MSBuild.exe` 执行（部分安全策略会拦），C++ 侧可改用 `cl.exe` + `link.exe` 手工编译，完整命令见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#构建与产物)。

### 2. 安装（注册右键菜单）

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Install-Sparse.ps1
```

脚本会做：三处 CLSID 一致性自检 → 检查 Publisher 变更 → `Add-AppxPackage -Register` → 重启 explorer。

**必须以普通用户运行**，不需要管理员。

### 3. 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Uninstall.ps1
```

### 4. 跑断言测试

```powershell
dotnet build src\BatchRename.ShellTest\BatchRename.ShellTest.csproj -c Release
dotnet src\BatchRename.ShellTest\bin\Release\net10.0-windows10.0.26100.0\BatchRename.ShellTest.dll
```

覆盖 NameSplitter / 作用域语义 / 大小写 Invariant / 正则 / 九条校验 / 批内互撞 / 磁盘冲突 / 自动编号 / 性能预算 / **执行器 + 撤销 journal（真实文件）** / Pending 恢复，共 **79 项**。退出码非 0 表示有断言失败。

> ⚠️ 测试 10–13 会在 `%TEMP%\BatchRenameTest\` 下创建临时文件并真实改名，用完自动清理，**不会碰你的实际文件**。
> 但只要在 Windows 上跑这些测试，就别同时开着 `docs\samples` 做手工改名测试，避免互相干扰。

## 目录结构

```
BatchRename\
├─ BatchRename.sln
├─ docs\
│  ├─ SPEC.md                规格书（v1.2，契约与验收标准）
│  ├─ ARCHITECTURE.md        架构说明（进程拓扑、模块、决策、排障）
│  └─ samples\               验收用样本文件名（15 项边界情况）
├─ src\
│  ├─ BatchRename.Shell\     C++ / WRL / x64，右键菜单 COM 组件
│  ├─ BatchRename.App\       C# / WinUI 3，主程序
│  │  ├─ Core\               引擎与执行器（不依赖 WinUI）
│  │  │  ├─ NameSplitter.cs          主名/扩展名切分
│  │  │  ├─ RenameRule.cs            替换 + 大小写
│  │  │  ├─ RenameEngine.cs          编排 + 九条校验 + 冲突检测
│  │  │  ├─ FileOperationRunner.cs   STA + IFileOperation 执行
│  │  │  ├─ UndoJournal.cs           WAL journal + 反向执行
│  │  │  └─ NativeMethods.txt        CsWin32 输入清单
│  │  ├─ Services\
│  │  │  └─ SingleInstanceGate.cs    命名 Mutex + 命名管道激活
│  │  └─ Models\RenameItem.cs
│  └─ BatchRename.ShellTest\ 引擎+执行器断言测试（控制台，67 项）
├─ packaging\
│  ├─ AppxManifest.xml       稀疏包清单（CLSID 与 Verb 注册）
│  └─ Assets\                3 个 Logo
├─ tools\
│  ├─ Install-Sparse.ps1
│  └─ Uninstall.ps1
└─ bin\publish\              外部位置根（安装时 -ExternalLocation 指向这里）
   ├─ BatchRename.Shell.dll
   ├─ Assets\
   └─ BatchRename.App\
```

## 文档

| 文档 | 内容 |
|---|---|
| [docs/SPEC.md](docs/SPEC.md) | 规格书 v1.1。需求、契约、清单全文、脚本全文、验收标准、已知缺陷修订 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架构说明。进程拓扑、启动时序、模块职责、关键决策、排障地图 |

## 维护约定

1. **工程改动 → 同步规格书**。契约、属性、CLI 行为有任何变化，或踩到新坑，立刻更新 `docs/SPEC.md`，并在正文加 `🔧 vX.Y` 标注 + 头部修订记录表追加一行。
2. **git 提交前 → 更新 README 与架构说明**。里程碑状态、构建方式、目录结构、架构决策有变化就先改这两份，再提交。
3. 提交信息用 `feat:` / `fix:` / `docs:` / `chore:` 前缀，正文列要点。

## 已知限制

- **执行期无逐项进度**：`IFileOperation` 批量执行拿不到细粒度回调，只有不确定态进度环；`RunAsync` 已接受 `CancellationToken` 但 UI 尚未接取消按钮
- **部分失败只显示首条原因**：未做 §8.9.5 的失败清单对话框
- `Esc` 关闭前没有"有未执行改动"的二次确认
- 未做 `Shift+右键` 传统菜单兜底（M5）
- 未做拖放启动（M5）
- 图标为临时方案（应用 Aa / 菜单铅笔），任务栏 plating 未彻底解决
- 不支持递归子文件夹、云盘/回收站等虚拟项（设计上不做）

## 历史坑（详见 SPEC v1.1 / v1.2 修订记录）

- **`File.Exists` 在 NTFS 上忽略大小写**，不能用来校验「仅大小写改名」，也不能判断撤销时源名是否被占用
- **CaseOnly 两步改名不能塞进单个 `IFileOperation` 队列**（前一步的结果尚不存在）
- **磁盘冲突检测必须在预览阶段做**，否则执行会静默覆盖同名文件
