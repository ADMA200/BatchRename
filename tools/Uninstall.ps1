#requires -Version 5.1
<#
.SYNOPSIS
    卸载 BatchRename 稀疏包。
.NOTES
    注意：dllhost.exe 可能还持着 BatchRename.Shell.dll 的句柄，导致 bin\publish 删不掉。
    不要为了解锁去 kill 所有 dllhost（会连带杀掉其它 COM 代理）。
    正确顺序：先卸载包 → 重启 explorer → 再删目录。
#>
param(
    [string]$PackageName = 'BatchRename.Shell',
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = 'Stop'

# 先删 SendTo 快捷方式（与安装脚本对应，避免卸载后还留个指向已删 exe 的死链接）
$lnkPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\SendTo\批量重命名.lnk'
if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force
    Write-Host '[ok] 已删除 SendTo 快捷方式' -ForegroundColor Green
}

# 按前缀找：Identity 改名后旧包名不同，只按当前名找会漏
$pkgs = @(Get-AppxPackage -Name 'BatchRename*' -ErrorAction SilentlyContinue)
if ($pkgs.Count -eq 0) {
    Write-Host 'Not installed.' -ForegroundColor Yellow
    return
}

foreach ($pkg in $pkgs) {
    Write-Host "[..] Removing $($pkg.PackageFullName)"
    Remove-AppxPackage -Package $pkg.PackageFullName
}

if (-not $SkipExplorerRestart) {
    Write-Host '[..] Restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
}

Write-Host '[ok] Uninstalled.' -ForegroundColor Green
Write-Host 'Now close any running BatchRename.App process, then you can delete bin\publish.' -ForegroundColor DarkGray
