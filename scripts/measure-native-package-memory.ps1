# DeskBox Native Package Memory Measurement
# Usage: .\measure-memory.ps1
# Run this while DeskBox is running to capture per-process memory metrics.
# Repeat at each stage: baseline (no packages), +Glance, +Weather, +Music, all three.

param(
    [string]$Label = "measurement"
)

$proc = Get-Process -Name DeskBox -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -notmatch 'WindowsApps' } |
    Select-Object -First 1

if (-not $proc) {
    Write-Output "DeskBox.exe not found"
    exit 1
}

$ws = $proc.WorkingSet64 / 1MB
$privateWs = $proc.PrivateMemorySize64 / 1MB
$threads = $proc.Threads.Count
$handles = $proc.HandleCount

# Private Working Set needs performance counters (Get-Process only gives full WS)
$pid = $proc.Id
$counters = Get-Counter -Counter "\Process(deskbox)\Working Set - Private" -ErrorAction SilentlyContinue
$privateWorkingSet = if ($counters) { $counters.CounterSamples[0].CookedValue / 1MB } else { "N/A" }

$commitCounters = Get-Counter -Counter "\Process(deskbox)\Private Bytes" -ErrorAction SilentlyContinue
$privateBytes = if ($commitCounters) { $commitCounters.CounterSamples[0].CookedValue / 1MB } else { "N/A" }

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Write-Output ""
Write-Output "=== DeskBox Memory: $Label ==="
Write-Output "  Time:            $timestamp"
Write-Output "  PID:             $pid"
Write-Output "  Working Set:     $([math]::Round($ws, 1)) MB"
Write-Output "  Private WS:      $([math]::Round($privateWorkingSet, 1)) MB"
Write-Output "  Private Bytes:   $([math]::Round($privateBytes, 1)) MB"
Write-Output "  Threads:         $threads"
Write-Output "  Handles:         $handles"
Write-Output ""

# Append to log
$logLine = "$timestamp,$Label,$([math]::Round($ws,1)),$([math]::Round($privateWorkingSet,1)),$([math]::Round($privateBytes,1)),$threads,$handles"
$logFile = Join-Path $PSScriptRoot "memory-results.csv"
if (-not (Test-Path $logFile)) {
    "Timestamp,Label,WorkingSetMB,PrivateWSMB,PrivateBytesMB,Threads,Handles" | Out-File $logFile -Encoding UTF8
}
Add-Content -Path $logFile -Value $logLine -Encoding UTF8
Write-Output "  Appended to $logFile"
