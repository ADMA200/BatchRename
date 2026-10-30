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

$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if (-not $pkg) {
    Write-Host 'Not installed.' -ForegroundColor Yellow
    return
}

Write-Host "[..] Removing $($pkg.PackageFullName)"
Remove-AppxPackage -Package $pkg.PackageFullName

if (-not $SkipExplorerRestart) {
    Write-Host '[..] Restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
}

Write-Host '[ok] Uninstalled.' -ForegroundColor Green
Write-Host 'Now close any running BatchRename.App process, then you can delete bin\publish.' -ForegroundColor DarkGray
