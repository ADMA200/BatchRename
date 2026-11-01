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

# --- 1. 旧包处理：Publisher 或 Identity 变了都必须先卸载（否则 0x80073CFB） ---
$newPublisher = $manifest.Package.Identity.Publisher
$newName      = $manifest.Package.Identity.Name

# 按前缀找历史安装：Identity 改名后旧包的名字不同，只按当前名找会漏掉
$existing = @(Get-AppxPackage -Name 'BatchRename*' -ErrorAction SilentlyContinue)
foreach ($old in $existing) {
    if ($old.Publisher -ne $newPublisher -or $old.Name -ne $newName) {
        Write-Host "[warn] 旧包与清单身份不一致，先卸载：$($old.PackageFullName)" -ForegroundColor Yellow
        Write-Host "       (Name: $($old.Name) -> $newName ; Publisher: $($old.Publisher) -> $newPublisher)" -ForegroundColor DarkGray
        Remove-AppxPackage -Package $old.PackageFullName -ErrorAction Stop
    }
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
