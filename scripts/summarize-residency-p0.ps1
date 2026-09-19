<#
.SYNOPSIS
    Residency P0 归因汇总：从实验实例的 DeskBox.log 抽切换延迟四点、回切间隔、
    MemorySample 的 per-kind 物化树计数与私有内存阶跃、强制 GC 前后值；
    可选叠加采样器 CSV 的分阶段起/峰/末。协议见 residency-p0-attribution-protocol.md。
.EXAMPLE
    ./scripts/summarize-residency-p0.ps1 -LogPath "$env:LOCALAPPDATA\DeskBox-ResidencyP0\DeskBox.log" `
        -CsvPath D:\project\wingezi\deskbox-residency-p0-samples.csv
#>
param(
    [Parameter(Mandatory)] [string]$LogPath,
    [string]$CsvPath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $LogPath)) { throw "Log not found: $LogPath" }

function Get-KeyValue([string]$Line, [string]$Key) {
    if ($Line -match "(?<![A-Za-z])$([regex]::Escape($Key))=(\S+)") { return $Matches[1] }
    return $null
}

# App.Log stamps lines as "[HH:mm:ss.fff] ..." (no date); a date-prefixed
# variant is accepted too in case the log is ever re-formatted.
function Get-Stamp([string]$Line) {
    if ($Line -match '^\[((?:\d{4}-\d{2}-\d{2}[ T])?\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]') { return $Matches[1] }
    return ''
}

function Get-Percentile([double[]]$Values, [double]$P) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $rank = [math]::Ceiling($P / 100.0 * $sorted.Count) - 1
    return $sorted[[math]::Max(0, [math]::Min($sorted.Count - 1, $rank))]
}

function Format-Dist([double[]]$Values, [string]$Unit) {
    if (-not $Values -or $Values.Count -eq 0) { return 'n=0' }
    'n={0} p50={1} p80={2} p95={3} max={4} {5}' -f $Values.Count,
        (Get-Percentile $Values 50), (Get-Percentile $Values 80),
        (Get-Percentile $Values 95), ($Values | Measure-Object -Maximum).Maximum, $Unit
}

$lines = Get-Content -LiteralPath $LogPath

# ── ① 切换延迟四点 + 回切间隔 ────────────────────────────────────────────
Write-Host "`n== Group switch latency ([WidgetGroup] Switched) =="
$switches = foreach ($line in $lines) {
    if ($line -notmatch '\[WidgetGroup\] Switched') { continue }
    $source = Get-KeyValue $line 'source'
    if (-not $source) { continue }   # pre-P0 log line without instrumentation
    [pscustomobject]@{
        source     = $source
        prepareMs  = Get-KeyValue $line 'prepareMs'
        firstFrame = Get-KeyValue $line 'firstFrameMs'
        settleMs   = Get-KeyValue $line 'settleMs'
        totalMs    = Get-KeyValue $line 'totalMs'
        sinceLast  = Get-KeyValue $line 'sinceLastActiveMs'
    }
}
if (-not $switches) {
    Write-Host '  (no instrumented switch lines yet)'
} else {
    foreach ($group in $switches | Group-Object source) {
        Write-Host ("  source={0}" -f $group.Name)
        foreach ($field in 'prepareMs', 'firstFrame', 'settleMs', 'totalMs') {
            $vals = @($group.Group | ForEach-Object { $_.$field } | Where-Object { $_ -and $_ -ne '-' } | ForEach-Object { [double]$_ })
            Write-Host ("    {0,-11} {1}" -f $field, (Format-Dist $vals 'ms'))
        }
    }
    $inactivity = @($switches | ForEach-Object { $_.sinceLast } | Where-Object { $_ -and $_ -ne '-' } | ForEach-Object { [double]$_ / 1000.0 })
    Write-Host ("  sinceLastActive (TTL input) {0}" -f (Format-Dist $inactivity 's'))
}

# ── ② MemorySample：物化树计数变化点与私有内存阶跃 ─────────────────────
Write-Host "`n== MemorySample residency track ([Perf] MemorySample, needs DESKBOX_PERF_LOG=1) =="
$samples = foreach ($line in $lines) {
    if ($line -notmatch '\[Perf\] MemorySample') { continue }
    [pscustomobject]@{
        time         = Get-Stamp $line
        privateMB    = [double](Get-KeyValue $line 'privateMB')
        gcHeapMB     = [double](Get-KeyValue $line 'gcHeapMB')
        thumbMB      = [double](Get-KeyValue $line 'thumbCacheMB')
        bitmapMB     = [double](Get-KeyValue $line 'decodedBitmapMB')
        materialized = Get-KeyValue $line 'materializedContentByKind'
        cached       = Get-KeyValue $line 'cachedContentByKind'
        windows      = Get-KeyValue $line 'windows'
    }
}
if (-not $samples) {
    Write-Host '  (no MemorySample lines — run the instance with DESKBOX_PERF_LOG=1)'
} else {
    $previous = $null
    foreach ($s in $samples) {
        $changed = $null -eq $previous -or $s.materialized -ne $previous.materialized -or $s.cached -ne $previous.cached -or $s.windows -ne $previous.windows
        if ($changed) {
            $delta = if ($previous) { '{0:+0.0;-0.0}' -f ($s.privateMB - $previous.privateMB) } else { 'start' }
            Write-Host ("  {0}  priv={1,7:N1} MB ({2,7})  gc={3,6:N1}  thumb={4,5:N1}  bitmap={5,5:N1}  windows={6}  materialized={7}  cached={8}" -f `
                $s.time, $s.privateMB, $delta, $s.gcHeapMB, $s.thumbMB, $s.bitmapMB, $s.windows, $s.materialized, $s.cached)
        }
        $previous = $s
    }
    Write-Host ("  last: priv={0:N1} MB materialized={1} cached={2}" -f $previous.privateMB, $previous.materialized, $previous.cached)
}

# ── ③ 强制 GC 分支 ─────────────────────────────────────────────────────
Write-Host "`n== Forced GC branch ([Memory] Deep finalizer cleanup completed) =="
$reclaims = foreach ($line in $lines) {
    if ($line -notmatch 'Deep finalizer cleanup completed') { continue }
    [pscustomobject]@{
        time    = Get-Stamp $line
        status  = Get-KeyValue $line 'status'
        before  = [double](Get-KeyValue $line 'reclaimPrivateBeforeMB')
        after   = [double](Get-KeyValue $line 'reclaimPrivateAfterMB')
        gcBefore = [double](Get-KeyValue $line 'gcHeapBeforeMB')
        gcAfter  = [double](Get-KeyValue $line 'gcHeapAfterMB')
    }
}
if (-not $reclaims) {
    Write-Host '  (none — hide every widget to the tray and wait HiddenCacheCleanupDelaySeconds)'
} else {
    foreach ($r in $reclaims) {
        Write-Host ("  {0}  status={1}  private {2:N1} -> {3:N1} MB ({4:+0.0;-0.0})  gcHeap {5:N1} -> {6:N1} MB" -f `
            $r.time, $r.status, $r.before, $r.after, ($r.after - $r.before), $r.gcBefore, $r.gcAfter)
    }
}

# ── ④ 采样器 CSV 分阶段 ────────────────────────────────────────────────
if ($CsvPath -and (Test-Path $CsvPath)) {
    Write-Host "`n== Sampler phases ($CsvPath) =="
    Import-Csv $CsvPath | Group-Object phase | ForEach-Object {
        $p = @($_.Group | ForEach-Object { [double]$_.privateMB })
        '  {0,-16} n={1,3} first={2,7:N1} peak={3,7:N1} last={4,7:N1} MB' -f $_.Name, $_.Count, $p[0], ($p | Measure-Object -Maximum).Maximum, $p[-1]
    }
}
