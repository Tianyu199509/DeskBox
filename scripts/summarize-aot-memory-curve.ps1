<#
.SYNOPSIS
    汇总 measure-aot-memory-curve.ps1 采得的 CSV：按阶段输出基线/峰值/末值，
    计算批间私有峰值趋势，输出 plateau vs 持续爬升的判别结论；
    可选对照实验根 DeskBox.log 的 [Memory] 托管堆数字。
#>
param(
    [string]$CsvPath = "D:\project\wingezi\deskbox-aot-curve-samples.csv",
    [string]$DataRoot = "$env:LOCALAPPDATA\DeskBox-AotCurve",
    # plateau 判别：最后一批的峰值相对第一批峰值允许的涨幅
    [double]$PlateauTolerancePercent = 15
)

$ErrorActionPreference = 'Stop'
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

$batchPhases = $phases | Where-Object { $_.Name -match 'batch' } | Sort-Object { ($rows | Where-Object phase -eq $_.Name | Select-Object -First 1).timestamp }
if ($batchPhases.Count -ge 2) {
    Write-Host "== batch-phase peak trend =="
    $peaks = foreach ($g in $batchPhases) {
        [math]::Round(($g.Group | Measure-Object -Property privateMB -Maximum).Maximum, 1)
    }
    for ($i = 0; $i -lt $peaks.Count; $i++) {
        Write-Host ("  {0}: peak {1:N1} MB" -f $batchPhases[$i].Name, $peaks[$i])
    }
    $first = $peaks[0]; $last = $peaks[-1]
    $deltaPercent = if ($first -gt 0) { [math]::Round((($last - $first) / $first) * 100, 1) } else { 0 }
    $monotonicRise = $true
    for ($i = 1; $i -lt $peaks.Count; $i++) { if ($peaks[$i] -lt $peaks[$i - 1]) { $monotonicRise = $false } }
    Write-Host ("  first->last peak: {0:N1} -> {1:N1} MB ({2}{3:N1}%)" -f $first, $last, $(if ($deltaPercent -ge 0) { '+' } else { '' }), $deltaPercent)
    if ($monotonicRise -and $deltaPercent -gt $PlateauTolerancePercent) {
        Write-Host "  VERDICT: RISE (持续爬升) — 批间峰值单调递增且总涨幅 > ${PlateauTolerancePercent}%，需要查 native 侧持有。" -ForegroundColor Yellow
    } else {
        Write-Host "  VERDICT: PLATEAU (平台期) — 批间峰值未单调爬升或涨幅 <= ${PlateauTolerancePercent}%，按可接受处理。" -ForegroundColor Green
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
