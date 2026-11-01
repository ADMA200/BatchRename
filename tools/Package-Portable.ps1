#requires -Version 5.1
<#
.SYNOPSIS
    把 BatchRename 打包成可拷到其它机器直接安装的 zip。

.DESCRIPTION
    产出的 zip 解压后包含：
      BatchRename\
        ├─ 安装.cmd            双击即可（内部调用 tools\Install-Sparse.ps1）
        ├─ 安装说明.txt
        ├─ packaging\          稀疏包清单 + Assets
        ├─ bin\publish\        Shell DLL + App 全量产物（172MB 量级）
        └─ tools\              安装/卸载脚本

.EXAMPLE
    .\tools\Package-Portable.ps1
    .\tools\Package-Portable.ps1 -SelfContained     # 连 .NET 运行时一起打包（约 +70MB）
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$OutputDir,
    # 默认框架依赖：目标机需装 .NET 10 Desktop Runtime。
    # 加这个开关则把 .NET 一起打进去，目标机零依赖，代价是体积。
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }

$publishDir = Join-Path $RepoRoot 'bin\publish'
$manifestPath = Join-Path $RepoRoot 'packaging\AppxManifest.xml'

Write-Host '[..] Building App' -ForegroundColor Cyan
$publishArgs = @('publish', (Join-Path $RepoRoot 'src\BatchRename.App\BatchRename.App.csproj'), '-c', 'Release')
if ($SelfContained) {
    $publishArgs += @('-p:SelfContained=true', '--self-contained')
    Write-Host '     (自包含模式：会一并打包 .NET 运行时)' -ForegroundColor DarkGray
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not (Test-Path $publishDir)) { throw "publish 目录不存在：$publishDir" }

# --- 组装 staging ---
$stamp  = Get-Date -Format 'yyyyMMdd'
$stage  = Join-Path $OutputDir "BatchRename-$stamp"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "[..] Staging -> $stage" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item (Join-Path $RepoRoot 'packaging') $stage -Recurse -Force
Copy-Item (Join-Path $RepoRoot 'tools')     $stage -Recurse -Force

$publishTarget = Join-Path $stage 'bin\publish'
New-Item -ItemType Directory -Path $publishTarget -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $publishTarget -Recurse -Force

# --- 双击安装入口（免去用户开 PowerShell 改执行策略）---
$installCmd = @'
@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-Sparse.ps1"
echo.
pause
'@
Set-Content -Path (Join-Path $stage '安装.cmd') -Value $installCmd -Encoding OEM

# --- 安装说明 ---
$dotnetNote = if ($SelfContained) { '无需安装（本包已自带 .NET 运行时）' } else { '必须先装 .NET 10 Desktop Runtime（x64）' }
$readme = @"
BatchRename 安装说明
====================

一、前置条件
  1. Windows 11 26H2（Build 26300+）
  2. 必须开启「开发者模式」：设置 > 系统 > 开发者选项 > 开发人员模式
     （未开启会报 0x80073CFF）
  3. .NET 10 Desktop Runtime（x64）：$dotnetNote
     https://dotnet.microsoft.com/download/dotnet/10.0

二、安装
  双击本目录下的「安装.cmd」
  成功标志：
    [ok] CLSID consistent across manifest and header
    [ok] Assets synced -> ...\Assets (8 files)
    [ok] Registered BatchRename.Shell_...
    [ok] SendTo: ...\SendTo\批量重命名.lnk

  脚本会自动重启资源管理器（窗口会闪一下，正常）。

三、使用
  - 右键菜单：选中文件 > 右键 > 批量重命名
    若没看到：26H2 默认把扩展收进「应用扩展」子菜单。
    右键菜单最底部有「自定义菜单」按钮，可直达设置页调整。
  - 兜底入口：右键 > 发送到 > 批量重命名（不受折叠影响，多选也支持）

四、重要
  ⚠ 安装后不要移动或重命名本目录！
     稀疏包注册时会记录绝对路径，移动后菜单会失效，需要重新运行「安装.cmd」。

五、卸载
  右键「安装.cmd」> 以 PowerShell 运行同一目录下的 tools\Uninstall.ps1
  或命令行：
    powershell -ExecutionPolicy Bypass -File .\tools\Uninstall.ps1

  卸载会移除：包注册、SendTo 快捷方式。
  本目录下的文件需自己删（程序不会自杀）。
"@
Set-Content -Path (Join-Path $stage '安装说明.txt') -Value $readme -Encoding UTF8

# --- 压缩 ---
$zipPath = Join-Path $OutputDir "BatchRename-$stamp.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host '[..] Compressing' -ForegroundColor Cyan
Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "[ok] $zipPath  ($sizeMb MB)" -ForegroundColor Green
Write-Host '     拷到目标机解压 → 双击「安装.cmd」' -ForegroundColor DarkGray
