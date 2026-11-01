#requires -Version 5.1
<#
.SYNOPSIS
    注册 BatchRename 稀疏包（免签 / 开发者模式路线）。
.NOTES
    必须以普通用户运行；不要用管理员（包注册在任何 IL 都可以）。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PackageName = 'BatchRename.Shell',
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = 'Stop'

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

# --- 1. Publisher 变了必须先卸载（否则 0x80073CFB） ---
$newPublisher = $manifest.Package.Identity.Publisher
$old = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($old -and $old.Publisher -ne $newPublisher) {
    Write-Host "[warn] Publisher changed ($($old.Publisher) -> $newPublisher); removing old package" -ForegroundColor Yellow
    Remove-AppxPackage -Package $old.PackageFullName
}

# --- 2. 注册 ---
Add-AppxPackage -Register $manifestPath -ExternalLocation $externalPath
$pkg = Get-AppxPackage -Name $PackageName
Write-Host "[ok] Registered $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host "     Status: $($pkg.Status)"

# --- 3. 重启 Explorer（唯一可靠的让 Shell 重新加载 Verb 表的办法） ---
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
