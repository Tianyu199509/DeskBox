<#
.SYNOPSIS
    汇总 measure-aot-memory-curve.ps1 采得的 CSV：按阶段输出基线/峰值/末值，
    计算批间私有峰值趋势，输出 plateau vs 持续爬升的判别结论；
    可选对照实验根 DeskBox.log 的 [Memory] 托管堆数字。
#>
param(
    [string]$CsvPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.artifacts\aot-curve-samples.csv")),
    [string]$DataRoot = "$env:LOCALAPPDATA\DeskBox-AotCurve",
    # plateau 判别：最后一批的峰值相对第一批峰值允许的涨幅
    [double]$PlateauTolerancePercent = 15,
    # Runs the fixed-data verdict regression cases and exits; no CSV needed.
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# ── Verdict state machine ───────────────────────────────────────────────
# One trend series → 'Rise' | 'Flat' | 'Fall' | 'Mixed'. A strict
# every-step-monotonic check is too brittle for release gating: a single
# -1% noise dip must not wave a +200% climb through as a plateau. Instead
# a rise needs both the endpoint gain over tolerance AND the majority of
# consecutive steps pointing up; a fall needs the endpoint below tolerance;
# everything within tolerance is flat, and a big net move without majority
# direction is mixed.
function Get-TrendState {
    param([double[]]$Values, [double]$TolerancePercent)
    # Fewer than two samples is not evidence of stability: "not measured"
    # must never satisfy the plateau contract's "idle baseline stable" leg.
    if ($Values.Count -lt 2) { return 'Insufficient' }
    $first = $Values[0]; $last = $Values[-1]
    $deltaPercent = if ($first -gt 0) { (($last - $first) / $first) * 100 } else { 0 }
    $ups = 0; $downs = 0
    for ($i = 1; $i -lt $Values.Count; $i++) {
        if ($Values[$i] -gt $Values[$i - 1]) { $ups++ }
        elseif ($Values[$i] -lt $Values[$i - 1]) { $downs++ }
    }
    if ($deltaPercent -gt $TolerancePercent -and $ups -gt $downs) { return 'Rise' }
    if ($deltaPercent -lt -$TolerancePercent) { return 'Fall' }
    if ([math]::Abs($deltaPercent) -le $TolerancePercent) { return 'Flat' }
    return 'Mixed'
}

# Three-way verdict: PLATEAU only when batch peaks AND idle baselines are
# both non-rising — the protocol lists "空闲段基线稳定" as a plateau
# condition, and an idle creep with flat peaks is exactly the leak shape a
# gate must not wave through.
function Get-CurveVerdict {
    param([double[]]$BatchPeaks, [double[]]$IdleBaselines, [double]$TolerancePercent)
    $peakState = Get-TrendState $BatchPeaks $TolerancePercent
    $idleState = Get-TrendState $IdleBaselines $TolerancePercent
    if ($peakState -eq 'Rise') { return 'RISE' }
    if (($peakState -eq 'Flat' -or $peakState -eq 'Fall') -and
        ($idleState -eq 'Flat' -or $idleState -eq 'Fall')) { return 'PLATEAU' }
    return 'INCONCLUSIVE'
}

if ($SelfTest) {
    $cases = @(
        @{ Peaks = @(100, 200, 198, 300); Idles = @();                    Expect = 'RISE';         Note = 'one -1% noise dip inside a +200% climb' },
        @{ Peaks = @(100, 103, 99, 105);  Idles = @(400, 405, 398);       Expect = 'PLATEAU';      Note = 'bounded peaks, stable idles' },
        @{ Peaks = @(100, 103, 105);      Idles = @(400, 500, 600);       Expect = 'INCONCLUSIVE'; Note = 'flat peaks but idle baseline creeps' },
        @{ Peaks = @(100, 300, 300, 300); Idles = @();                    Expect = 'RISE';         Note = 'a single large step still flags' },
        @{ Peaks = @(100, 200, 150);      Idles = @();                    Expect = 'INCONCLUSIVE'; Note = 'net +50% without majority direction' },
        @{ Peaks = @(100, 90, 80);        Idles = @(400, 395);            Expect = 'PLATEAU';      Note = 'falling peaks are not a leak signal' },
        @{ Peaks = @(100, 103, 99, 105);  Idles = @();                    Expect = 'INCONCLUSIVE'; Note = 'flat peaks but no idle evidence at all' },
        @{ Peaks = @(100, 103, 99, 105);  Idles = @(400);                 Expect = 'INCONCLUSIVE'; Note = 'a single idle sample is not a trend' },
        @{ Peaks = @(100, 103, 99, 105);  Idles = @(400, 398);            Expect = 'PLATEAU';      Note = 'flat peaks plus two stable idles' }
    )
    $failures = 0
    foreach ($case in $cases) {
        $actual = Get-CurveVerdict $case.Peaks $case.Idles $PlateauTolerancePercent
        $ok = $actual -eq $case.Expect
        if (-not $ok) { $failures++ }
        Write-Host ("  [{0}] peaks=[{1}] idles=[{2}] -> {3} (expect {4}) — {5}" -f `
            $(if ($ok) { 'PASS' } else { 'FAIL' }),
            ($case.Peaks -join ','), $(if ($case.Idles.Count) { $case.Idles -join ',' } else { '-' }),
            $actual, $case.Expect, $case.Note)
    }
    if ($failures -gt 0) { throw "$failures verdict self-test case(s) failed." }
    Write-Host 'All verdict self-test cases passed.'
    exit 0
}

if (-not (Test-Path $CsvPath)) { throw "No samples at $CsvPath" }

$rows = Import-Csv $CsvPath | ForEach-Object {
    $_.privateMB = [double]$_.privateMB
    $_.workingSetMB = [double]$_.workingSetMB
    $_.elapsedSeconds = [double]$_.elapsedSeconds
    $_
}

Write-Host "== per-phase summary (private commit, MB) =="
$phases = $rows | Group-Object phase
$summary = foreach ($g in $phases) {
    $stats = $g.Group | Measure-Object -Property privateMB -Minimum -Maximum -Average
    [PSCustomObject]@{
        phase   = $g.Name
        samples = $g.Count
        minMB   = [math]::Round($stats.Minimum, 1)
        avgMB   = [math]::Round($stats.Average, 1)
        maxMB   = [math]::Round($stats.Maximum, 1)
        lastMB  = [math]::Round(($g.Group | Select-Object -Last 1).privateMB, 1)
    }
}
$summary | Format-Table -AutoSize

$phaseOrder = { ($rows | Where-Object phase -eq $_.Name | Select-Object -First 1).timestamp }
$batchPhases = $phases | Where-Object { $_.Name -match 'batch' } | Sort-Object $phaseOrder
$idlePhases = $phases | Where-Object { $_.Name -match '^idle' } | Sort-Object $phaseOrder

Write-Host "== batch-phase peak trend =="
$peaks = foreach ($g in $batchPhases) {
    [math]::Round(($g.Group | Measure-Object -Property privateMB -Maximum).Maximum, 1)
}
if ($peaks.Count -gt 0) {
    for ($i = 0; $i -lt $peaks.Count; $i++) {
        Write-Host ("  {0}: peak {1:N1} MB" -f $batchPhases[$i].Name, $peaks[$i])
    }
    $first = $peaks[0]; $last = $peaks[-1]
    $deltaPercent = if ($first -gt 0) { [math]::Round((($last - $first) / $first) * 100, 1) } else { 0 }
    Write-Host ("  first->last peak: {0:N1} -> {1:N1} MB ({2}{3:N1}%)" -f $first, $last, $(if ($deltaPercent -ge 0) { '+' } else { '' }), $deltaPercent)
} else {
    Write-Host "  (no batch phases)"
}

# Idle baselines are part of the plateau contract: peaks can look flat
# while the settled floor keeps creeping — that is still a leak shape.
Write-Host "== idle-phase baseline trend =="
$idleBaselines = foreach ($g in $idlePhases) {
    [math]::Round(($g.Group | Measure-Object -Property privateMB -Average).Average, 1)
}
if ($idleBaselines.Count -gt 0) {
    for ($i = 0; $i -lt $idleBaselines.Count; $i++) {
        Write-Host ("  {0}: baseline {1:N1} MB" -f $idlePhases[$i].Name, $idleBaselines[$i])
    }
    $first = $idleBaselines[0]; $last = $idleBaselines[-1]
    $deltaPercent = if ($first -gt 0) { [math]::Round((($last - $first) / $first) * 100, 1) } else { 0 }
    Write-Host ("  first->last baseline: {0:N1} -> {1:N1} MB ({2}{3:N1}%)" -f $first, $last, $(if ($deltaPercent -ge 0) { '+' } else { '' }), $deltaPercent)
} else {
    Write-Host "  (no idle phases)"
}

if ($peaks.Count -ge 2) {
    $verdict = Get-CurveVerdict $peaks $idleBaselines $PlateauTolerancePercent
    switch ($verdict) {
        'RISE' {
            Write-Host "  VERDICT: RISE (持续爬升) — 批间峰值显著上涨且多数步进向上 (涨幅 > ${PlateauTolerancePercent}%)，需要查 native 侧持有。" -ForegroundColor Yellow
        }
        'PLATEAU' {
            Write-Host "  VERDICT: PLATEAU (平台期) — 批间峰值与空闲基线均未显著爬升 (涨幅 <= ${PlateauTolerancePercent}%)，按可接受处理。" -ForegroundColor Green
        }
        default {
            Write-Host "  VERDICT: INCONCLUSIVE (证据不足) — 峰值/基线趋势不一致或无方向性多数，不能按平台期放行；人工判读或补采数据。" -ForegroundColor Magenta
        }
    }
} else {
    Write-Host "(少于 2 个 batch 阶段，跳过趋势判别)"
}

$logPath = Join-Path $DataRoot 'DeskBox.log'
if (Test-Path $logPath) {
    Write-Host "`n== [Memory] managed heap lines (tail 12, from experiment log) =="
    Select-String -Path $logPath -Pattern '\[Memory\].*(managedHeap|cleanup outcome)' |
        Select-Object -Last 12 |
        ForEach-Object {
            $line = $_.Line
            $t = [regex]::Match($line, '^\[([\d:.]+)\]').Groups[1].Value
            $priv = [regex]::Match($line, 'privateAfterMB=([\d.]+)').Groups[1].Value
            $heap = [regex]::Match($line, 'managedHeapAfterMB=([\d.]+)').Groups[1].Value
            "  [$t] privateAfter=${priv}MB managedHeapAfter=${heap}MB"
        }
} else {
    Write-Host "(no experiment log at $logPath)"
}
