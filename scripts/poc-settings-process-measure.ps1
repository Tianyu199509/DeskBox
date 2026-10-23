# Phase 0 PoC measurement for the settings-process split (1.6.0 batch 1).
# Spawns DeskBox.exe --settings-poc, times spawn-to-first-activation, samples
# the process memory, closes the window (validating close-exits-process), and
# optionally measures the in-proc comparison: how much memory the current
# single-process settings window adds to the main instance.
#
# Usage:
#   pwsh scripts/poc-settings-process-measure.ps1 -ExePath <DeskBox.exe> [-Iterations 3] [-IncludeInProc]
# Requires: the main DeskBox instance already running (for the in-proc part),
# PowerShell 7 (pwsh) for DateTimeOffset parsing convenience.

param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [int]$Iterations = 3,
    [switch]$IncludeInProc
)

$ErrorActionPreference = 'Stop'
$pocLog = Join-Path ([System.IO.Path]::GetTempPath()) 'deskbox-settings-poc.log'

function Get-MainProcess {
    Get-Process DeskBox -ErrorAction SilentlyContinue |
        Where-Object { -not $_.HasExited } |
        Sort-Object StartTime |
        Select-Object -First 1
}

function Read-PocMarkers([int]$ProcessId) {
    if (-not (Test-Path $pocLog)) { return @() }
    Get-Content $pocLog | Where-Object { $_ -match "pid=$ProcessId " }
}

function Parse-MarkerTime([string]$Line) {
    # Format: <ISO-8601 UTC> +<ms>ms pid=<id> <marker>
    if ($Line -match '^(\S+) \+') {
        return [datetimeoffset]::Parse($Matches[1])
    }
    return $null
}

$results = [ordered]@{
    exe          = $ExePath
    machine      = $env:COMPUTERNAME
    os           = [System.Environment]::OSVersion.VersionString
    mainPid      = $null
    mainBaseline = $null
    pocRuns      = @()
    inProc       = $null
}

$main = Get-MainProcess
if ($main) {
    $results.mainPid = $main.Id
    Start-Sleep -Seconds 2
    $results.mainBaseline = [ordered]@{
        workingSetMB = [math]::Round($main.WorkingSet64 / 1MB, 1)
        privateMB    = [math]::Round($main.PrivateMemorySize64 / 1MB, 1)
    }
    Write-Host ("main pid={0} baseline WS={1}MB private={2}MB" -f `
        $main.Id, $results.mainBaseline.workingSetMB, $results.mainBaseline.privateMB)
}

for ($i = 1; $i -le $Iterations; $i++) {
    if (Test-Path $pocLog) { Remove-Item $pocLog -Force }
    $spawnUtc = [datetimeoffset]::UtcNow
    $proc = Start-Process -FilePath $ExePath -ArgumentList '--settings-poc' -PassThru
    $activatedLine = $null
    $deadline = [datetime]::Now.AddSeconds(25)
    while ([datetime]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 100
        if ($proc.HasExited) { break }
        $activatedLine = Read-PocMarkers $proc.Id |
            Where-Object { $_ -match ' window-activated$' } |
            Select-Object -First 1
        if ($activatedLine) { break }
    }

    if (-not $activatedLine) {
        Write-Warning ("run {0}: no activation marker (exited={1})" -f $i, $proc.HasExited)
        $results.pocRuns += [ordered]@{ run = $i; failed = $true }
        continue
    }

    $activatedUtc = Parse-MarkerTime $activatedLine
    $spawnToShownMs = [math]::Round(($activatedUtc - $spawnUtc).TotalMilliseconds)

    Start-Sleep -Seconds 2   # let first-frame allocations settle
    $pocProc = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $workingSetMB = [math]::Round($pocProc.WorkingSet64 / 1MB, 1)
    $privateMB = [math]::Round($pocProc.PrivateMemorySize64 / 1MB, 1)

    # Close the window; the process must exit on its own (PoC contract).
    $null = $pocProc.CloseMainWindow()
    $exited = $proc.WaitForExit(15000)
    $markers = (Read-PocMarkers $proc.Id) -join ' | '

    $run = [ordered]@{
        run             = $i
        pid             = $proc.Id
        spawnToShownMs  = $spawnToShownMs
        workingSetMB    = $workingSetMB
        privateMB       = $privateMB
        exitedOnClose   = $exited
        markers         = $markers
    }
    $results.pocRuns += $run
    Write-Host ("run {0}: spawn->shown {1}ms WS={2}MB private={3}MB exitedOnClose={4}" -f `
        $i, $spawnToShownMs, $workingSetMB, $privateMB, $exited)
}

if ($IncludeInProc) {
    if (-not $main) { throw 'IncludeInProc requires a running main instance.' }
    $main.Refresh()
    $before = [ordered]@{
        workingSetMB = [math]::Round($main.WorkingSet64 / 1MB, 1)
        privateMB    = [math]::Round($main.PrivateMemorySize64 / 1MB, 1)
    }
    # A bare secondary launch signals the primary, whose bare-activation
    # policy opens the in-proc settings window.
    $null = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds 10   # window build + settle
    $main.Refresh()
    $after = [ordered]@{
        workingSetMB = [math]::Round($main.WorkingSet64 / 1MB, 1)
        privateMB    = [math]::Round($main.PrivateMemorySize64 / 1MB, 1)
    }
    $results.inProc = [ordered]@{
        before = $before
        after  = $after
        deltaWorkingSetMB = [math]::Round($after.workingSetMB - $before.workingSetMB, 1)
        deltaPrivateMB    = [math]::Round($after.privateMB - $before.privateMB, 1)
    }
    Write-Host ("in-proc settings open: dWS={0}MB dPrivate={1}MB" -f `
        $results.inProc.deltaWorkingSetMB, $results.inProc.deltaPrivateMB)
}

$outDir = Join-Path $PSScriptRoot '..\artifacts\poc-settings-process'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir ("measure-{0}-{1:yyyyMMdd-HHmmss}.json" -f $env:COMPUTERNAME, [datetime]::Now)
$results | ConvertTo-Json -Depth 5 | Set-Content -Path $outPath -Encoding UTF8
Write-Host "results -> $outPath"
