#requires -Version 5.1
<#
.SYNOPSIS
    一步生成「单个 exe」安装包。

.DESCRIPTION
    流程：打包 payload（复用 Package-Portable）→ 内嵌进安装器 → 单文件发布。
    产物：dist\BatchRename-Setup-<日期>.exe（约 70MB，自包含 .NET 运行时）

.EXAMPLE
    .\tools\Build-Setup.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

Write-Host '[1/3] 生成 payload（含 dotnet publish）' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Package-Portable.ps1') -RepoRoot $RepoRoot

$zip = Get-ChildItem (Join-Path $RepoRoot 'dist\*.zip') |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw 'dist 下没有生成 zip' }

$payload = Join-Path $RepoRoot 'installer\BatchRenameSetup\payload.zip'
Copy-Item $zip.FullName $payload -Force
Write-Host "      payload: $([math]::Round((Get-Item $payload).Length / 1MB, 1)) MB" -ForegroundColor DarkGray

Write-Host '[2/3] 编译安装器（自包含单文件，稍慢）' -ForegroundColor Cyan
$proj = Join-Path $RepoRoot 'installer\BatchRenameSetup\BatchRenameSetup.csproj'
$outDir = Join-Path $RepoRoot 'dist\setup'
& dotnet publish $proj -c Release -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host '[3/3] 整理产物' -ForegroundColor Cyan
$exe = Join-Path $outDir 'BatchRenameSetup.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe" }

$final = Join-Path $RepoRoot ("dist\BatchRename-Setup-{0}.exe" -f (Get-Date -Format 'yyyyMMdd'))
Move-Item $exe $final -Force

# 清理中间文件，避免 payload.zip 留在源码树里
Remove-Item $payload -Force -ErrorAction SilentlyContinue

$mb = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host ''
Write-Host "[ok] $final  ($mb MB)" -ForegroundColor Green
Write-Host '     单个 exe，拷到目标机双击即可（无需解压、无需装 .NET）' -ForegroundColor DarkGray
