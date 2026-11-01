#requires -Version 5.1
<#
.SYNOPSIS
    文档一致性校验：SPEC 内嵌全文 vs 真实文件、断言条数、悬空引用。
.DESCRIPTION
    SPEC §6.2 / §6.4 内嵌了 AppxManifest.xml 与 Install-Sparse.ps1 的逐字全文。
    这种镜像必然漂移（v1.5 就漂过：脚本 159 行、文档还停在 79 行），所以用脚本卡住。
.NOTES
    2026-11-02 首次加入；CI 在 dotnet test 之前执行。
#>
param([string]$RepoRoot)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$script:fail = 0
function Fail([string]$msg) { $script:fail++; Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Pass([string]$msg) { Write-Host "[ ok ] $msg" -ForegroundColor Green }

function Read-Normalized([string]$path) {
    $t = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $t = $t -replace "`r`n", "`n"
    return $t.Trim()
}

function Get-FencedBlock([string]$text, [string]$heading) {
    $i = $text.IndexOf($heading, [StringComparison]::Ordinal)
    if ($i -lt 0) { throw "SPEC 里找不到章节: $heading" }
    $open = $text.IndexOf('```', $i)
    if ($open -lt 0) { throw "章节后没有代码块: $heading" }
    $bodyStart = $text.IndexOf("`n", $open) + 1
    $close = $text.IndexOf('```', $bodyStart)
    if ($close -lt 0) { throw "代码块未闭合: $heading" }
    return $text.Substring($bodyStart, $close - $bodyStart).Trim()
}

function Show-FirstDiff([string]$doc, [string]$real, [string]$label) {
    $a = $doc -split "`n"
    $b = $real -split "`n"
    $n = [Math]::Max($a.Count, $b.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $x = if ($i -lt $a.Count) { $a[$i] } else { '<缺失>' }
        $y = if ($i -lt $b.Count) { $b[$i] } else { '<缺失>' }
        if ($x -cne $y) {
            Fail "$label 第 $($i + 1) 行起不一致"
            Write-Host "        文档: $x" -ForegroundColor DarkYellow
            Write-Host "        实际: $y" -ForegroundColor DarkYellow
            $from = [Math]::Max(0, $i - 2)
            for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $n); $j++) {
                $p = if ($j -lt $a.Count) { $a[$j] } else { '<缺失>' }
                $q = if ($j -lt $b.Count) { $b[$j] } else { '<缺失>' }
                Write-Host "        doc: $p" -ForegroundColor DarkGray
                Write-Host "        act: $q" -ForegroundColor DarkGray
            }
            return
        }
    }
}

Write-Host "RepoRoot: $RepoRoot"
$specPath = Join-Path $RepoRoot 'docs\SPEC.md'
$spec = Read-Normalized $specPath

# ---- 1. SPEC 内嵌全文 vs 真实文件 ----
$pairs = @(
    @{ Heading = '### 6.2 AppxManifest.xml 全文';           File = 'packaging\AppxManifest.xml';       Label = 'AppxManifest.xml' },
    @{ Heading = '### 6.4 安装脚本 `tools\Install-Sparse.ps1` 全文'; File = 'tools\Install-Sparse.ps1'; Label = 'Install-Sparse.ps1' }
)

foreach ($p in $pairs) {
    $real = Read-Normalized (Join-Path $RepoRoot $p.File)
    $doc  = Get-FencedBlock $spec $p.Heading
    if ($doc -ceq $real) {
        Pass "$($p.Label) 与文档逐字一致（$((($real -split "`n")).Count) 行）"
    } else {
        Show-FirstDiff $doc $real $p.Label
    }
}

# ---- 2. 断言条数一致性 ----
$guard = Join-Path $RepoRoot 'tests\BatchRename.Core.Tests\CoreSuiteTests.cs'
$m = [regex]::Match((Read-Normalized $guard), 'ExpectedChecks\s*=\s*(\d+)')
if (-not $m.Success) {
    Fail 'CoreSuiteTests.cs 里找不到 ExpectedChecks'
} else {
    $expected = [int]$m.Groups[1].Value
    $docs = @(
        'README.md',
        'docs\ARCHITECTURE.md',
        'docs\SPEC.md',
        '.github\workflows\ci.yml'
    )
    foreach ($d in $docs) {
        $full = Join-Path $RepoRoot $d
        if (-not (Test-Path $full)) { continue }
        $txt = Read-Normalized $full
        $hits = [regex]::Matches($txt, '(\d+)\s*(?:项|条)\s*断言')
        $bad = @()
        foreach ($h in $hits) {
            if ([int]$h.Groups[1].Value -ne $expected) { $bad += $h.Groups[1].Value }
        }
        if ($bad.Count -gt 0) {
            Fail "$d 里的断言条数与守卫值 $expected 不一致: $($bad -join ', ')"
        }
    }
    Pass "断言条数一致：$expected（README / ARCHITECTURE / SPEC / CI）"
}

# ---- 3. 悬空引用（指向已删除的 REVIEW 文档）----
$dangling = 0
foreach ($d in @('README.md', 'docs\ARCHITECTURE.md', 'docs\SPEC.md')) {
    $full = Join-Path $RepoRoot $d
    if (-not (Test-Path $full)) { continue }
    $lineNo = 0
    foreach ($line in (Read-Normalized $full) -split "`n") {
        $lineNo++
        # -cmatch：PowerShell 的 -match 默认忽略大小写，会把 RefreshPreview / review 清单一起吃掉
        if ($line -cnotmatch 'REVIEW\s*§|REVIEW-\d{4}') { continue }
        if ($line -match '删除|并入') { continue }   # 历史记录里写明"已删除/并入"是合法的
        Fail "$d`:$lineNo 仍引用已删除的评审文档"
        $dangling++
    }
}
if ($dangling -eq 0) { Pass '没有指向已删除评审文档的引用' }

Write-Host ''
if ($script:fail -gt 0) {
    Write-Host "文档校验失败：$($script:fail) 项" -ForegroundColor Red
    exit 1
}
Write-Host '文档校验通过。' -ForegroundColor Green
exit 0
