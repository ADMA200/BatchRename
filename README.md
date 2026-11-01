# BatchRename

Windows 11 资源管理器右键菜单里的批量重命名工具，对标 PowerRename，个人自用、非商业。

选中 N 个文件/文件夹 → 右键「批量重命名」→ 实时预览「原名 → 新名」→ 执行 / `Ctrl+Z` 撤销。

撤销有**两条独立通路**：软件内的「撤销」按钮（读 `last-batch.json`，关掉程序再打开也能撤最近一批）与**资源管理器里的 `Ctrl+Z`**（由 `IFileOperation` 的 `FOFX_ADDUNDORECORD` 登记到 Shell 撤销栈，程序退出后依然有效，一次退一批）。

## 当前状态

| 里程碑 | 范围 | 状态 |
|---|---|---|
| **M1** | 右键菜单出现 + 拉起窗口 + 路径透传 | ✅ 已通过验收 |
| **M2** | 重命名引擎 + 实时预览（124 项自动断言） | 引擎 ✅ / UI ✅ |
| **M3** | 执行 + 撤销（`IFileOperation` + 单文件事务日志 + Shell 撤销栈） | ✅ 已通过真机验证 |
| **M4** | Mica / 自定义标题栏 / 键盘 / 单实例 | ✅ 已通过真机验证 |
| **M5** | 安装闭环 + 传统 verb 兜底 | 🟡 部分：稀疏包已注册（`Get-AppxPackage BatchRename.Shell` → `1.0.0.0 / Ok`，位置 `packaging\`），传统 verb 兜底与「干净机器从零安装」的完整验收未做 |

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

两个入口跑的是**同一份断言实现**（`tests\BatchRename.Core.Tests\CoreSuite.cs`），不会各写一遍漂移：

```powershell
# xUnit（CI 用，一条测试覆盖全部断言，同时守卫断言条数 == 124）
dotnet test tests\BatchRename.Core.Tests\BatchRename.Core.Tests.csproj -c Release

# 控制台（人肉看逐条输出）
dotnet build src\BatchRename.ShellTest\BatchRename.ShellTest.csproj -c Release
dotnet src\BatchRename.ShellTest\bin\Release\net10.0-windows10.0.26100.0\BatchRename.ShellTest.dll
```

覆盖 NameSplitter / 作用域语义 / 大小写 Invariant / 正则 / 九条校验 / 批内互撞 / 磁盘冲突 / 自动编号 / 性能预算 / **执行器 + 撤销 journal（真实文件）** / Pending 回滚 / **文件与目录的仅大小写改名** / 执行中断后的磁盘回写 / 二次改名 / 链式排序 / 循环检测 / 逐项失败 / 两层防抖 / 执行期进度与取消，共 **124 项**。退出码非 0 表示有断言失败。

> ⚠️ 测试 10–16 会在 `%TEMP%\BatchRenameTest\` 下创建临时文件并真实改名，用完自动清理，**不会碰你的实际文件**；journal 也会被指到 `%TEMP%\BatchRenameTestJournal\`，不会覆盖你的 `last-batch.json`。
> 测试执行器的 `AddUndoRecord` 一律为 `false`，所以不会往你资源管理器的 Ctrl+Z 里塞临时文件。
> 但只要在 Windows 上跑这些测试，就别同时开着 `docs\samples` 做手工改名测试，避免互相干扰。

## 目录结构

```
BatchRename\
├─ BatchRename.sln
├─ docs\
│  ├─ SPEC.md                规格书（v1.5，契约与验收标准）
│  ├─ ARCHITECTURE.md        架构说明（进程拓扑、模块、决策、排障）
│  ├─ REVIEW-2026-11-01.md   代码评审与实测记录（含系统级撤销验证、journal 去留分析）
│  └─ samples\               验收用样本文件名（15 项边界情况）
├─ src\
│  ├─ BatchRename.Shell\     C++ / WRL / x64，右键菜单 COM 组件
│  ├─ BatchRename.App\       C# / WinUI 3，主程序
│  │  ├─ Core\               引擎与执行器（不依赖 WinUI）
│  │  │  ├─ NameSplitter.cs          主名/扩展名切分
│  │  │  ├─ RenameRule.cs            替换 + 大小写
│  │  │  ├─ RenameEngine.cs          编排 + 九条校验 + 冲突检测
│  │  │  ├─ FileOperationRunner.cs   STA + IFileOperation 执行（含 Shell 撤销栈登记）
│  │  │  ├─ ConflictResolver.cs      批内互撞/循环/磁盘冲突/自动编号（从 RenameEngine 抽出）
│  │  │  ├─ UndoJournal.cs           单文件事务日志（write-ahead）+ 反向执行
│  │  │  └─ NativeMethods.txt        CsWin32 输入清单
│  │  ├─ Services\
│  │  │  ├─ SingleInstanceGate.cs    命名 Mutex + 命名管道激活
│  │  │  └─ AppLog.cs                App 侧日志（异常兜底，1MB 循环）
│  │  └─ Models\RenameItem.cs
│  └─ BatchRename.ShellTest\ 断言测试的**控制台入口**（薄壳，逐条打印）
├─ tests\BatchRename.Core.Tests\
│  ├─ CoreSuite.cs          124 条断言的唯一实现（console 与 xUnit 共用）
│  └─ CoreSuiteTests.cs     xUnit 入口 + 断言条数守卫
├─ .github\workflows\ci.yml  test + App build + console build（windows-latest）
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
| [docs/SPEC.md](docs/SPEC.md) | 规格书 v1.5。需求、契约、清单全文、脚本全文、验收标准、已知缺陷修订 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架构说明。进程拓扑、启动时序、模块职责、关键决策、排障地图 |
| [docs/REVIEW-2026-11-01.md](docs/REVIEW-2026-11-01.md) | 代码评审。问题清单（P0/P1/P2）、实测证据、优化建议与修复批次 |

## 维护约定

1. **工程改动 → 同步规格书**。契约、属性、CLI 行为有任何变化，或踩到新坑，立刻更新 `docs/SPEC.md`，并在正文加 `🔧 vX.Y` 标注 + 头部修订记录表追加一行。
2. **git 提交前 → 更新 README 与架构说明**。里程碑状态、构建方式、目录结构、架构决策有变化就先改这两份，再提交。
3. 提交信息用 `feat:` / `fix:` / `docs:` / `chore:` 前缀，正文列要点。

## 已知限制

- **失败清单最多列 200 条**：超出部分提示用「复制清单」带走（避免一次塞几千个 TextBlock）；底栏仍只显示首条原因
- **软件内撤销只覆盖最近一批**：`last-batch.json` 每次执行覆盖写，没有历史批次列表（7 天 / 20 批的历史已按 v1.5 砍掉）；更早的批次只能靠资源管理器的 Ctrl+Z（受 Shell 撤销栈生命周期限制）
- **执行中断会留下半完成状态**：`OnExecute` 已加 try/catch + `JournalError`，异常时按磁盘现状回写逐项 ok 并写 `app.log`；中断只会在下次启动时提示回滚
- 未做 `Shift+右键` 传统菜单兜底（M5）
- 未做拖放启动（M5）
- 图标为临时方案（应用 Aa / 菜单铅笔），任务栏 plating 未彻底解决
- 不支持递归子文件夹、云盘/回收站等虚拟项（设计上不做）

## 历史坑（详见 SPEC 各版修订记录与 docs/REVIEW-2026-11-01.md）

- **`File.Exists` 在 NTFS 上忽略大小写**，不能用来校验「仅大小写改名」，也不能判断撤销时源名是否被占用
- **`File.Exists` 对目录返回 false**，判断目录存在必须用 `Directory.Exists`（v1.5 写 `LoadLast` 时踩到）
- **`File.Move` 动不了目录**（抛 `FileNotFoundException`）；目录改名要用 `Directory.Move` 或交给 `IFileOperation`
- **系统级 Ctrl+Z 不是死路**：`FOFX_ADDUNDORECORD` 会把整批登记进 Shell 撤销栈，进程退出后依然有效；旧版"有意不加 `FOF_ALLOWUNDO`"导致资源管理器里 Ctrl+Z 完全无反应
- **`File.Move` / `IFileOperation` 都能直接做仅大小写改名**（文件与目录皆可）——v1.2 的"必须两步中转"是误判，真正的病因是"同一 `IFileOperation` 队列引用尚未生成的临时名"
- **磁盘冲突检测必须在预览阶段做**，否则执行会静默覆盖同名文件；`FOF_NOCONFIRMATION` 下覆盖连提示都没有
- **"源名会让出"的冲突豁免会把环放过去**：`a→b` + `b→a` 两项互相把对方当空位。v1.5 起由 `MarkBatchCycles` 在预览阶段拦掉（整环标「批内循环改名」）
- **链式改名 `a→b` + `b→c` 的入队顺序决定会不会丢数据**：错序时 `a→b` 会先覆盖 `b` 的原内容。v1.5 起执行器按依赖排序入队
- **`IFileOperation` 改名失败的 HRESULT 常来自复制引擎**（`0x8027xxxx`，如 `0x80270027` = 源文件被占用），不是 `0x8007xxxx`；只映射 Win32 那族会把错误显示成原始码
- **`ListView` 行模板里放一个 `CheckBox`，5000 项就多占 46–49MB**：模板化控件（含内置状态动画）会按已实现行摊资源，换成 `Border` + `FontIcon` 后 180MB → 137MB，再按官方模板复刻成双层 Border 为 139MB。行内只允许叶子元素
- **WinUI 3 的 `XamlControlsResources.ThemeDictionaries` 按启动时主题固化**：代码里取 `"Light"` 字典拿到的可能仍是深色值（实测 `TextFillColorSecondary` 两套都是 `#C5FFFFFF`），所以**不要用代码查主题色**——把 `{ThemeResource}` 写在 XAML 里、转换器只取现成的 brush 对象；切主题时 XAML 改的是同一个 brush 实例的 `Color`，已绑定的行自动刷新（缓存反而会把它钉死）
- **`IsThreeState="True"` 的点击循环会经过 `null`**：三态只该用来"显示"部分选中；在 `Checked/Indeterminate` 回调里对 `null` 直接 return，会让全选框停在中间态且列表毫无变化。用 `Click` + 从列表真实状态推断意图，处理完立刻回写显示
- **WinUI 3 没有 `CheckBoxFillUnchecked` 这批键**：那是 UWP 命名，`ThemeResource` 解析期直接抛 `Cannot find a Resource`，进程起不来。行内自绘勾选框要用通用令牌：勾选态 `AccentFillColorDefaultBrush` + `TextOnAccentFillColorPrimaryBrush`，未勾选态 **`Transparent`** 填充 + `ControlStrongStrokeColorDefaultBrush` 描边
- **`ListViewItem` 会吃掉子元素的 `Tapped`/`PointerPressed`**：行内交互只能走 `ListView.ItemClick`（`IsItemClickEnabled="True"`），要判"点的是哪一列"得另外用 `AddHandler(PointerPressedEvent, …, handledEventsToo: true)` 记下 x
