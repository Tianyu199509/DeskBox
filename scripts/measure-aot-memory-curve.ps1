<#
.SYNOPSIS
    AOT Release 内存曲线采样器：以隔离数据根启动候选构建，周期记录私有提交/工作集，
    支持按键打阶段标记，结束后输出各阶段摘要。判别线见协议文档。
.EXAMPLE
    # 阶段化采样（每阶段单独一条命令，append 同一 CSV）：
    ./scripts/measure-aot-memory-curve.ps1 -Phase baseline -Minutes 5
    ./scripts/measure-aot-memory-curve.ps1 -Phase batch1 -Minutes 10 -Attach
    # -Attach 模式不启动新进程，采样已运行的实验实例
#>
param(
    [string]$ExePath = "D:\project\wingezi\src\DeskBox\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\publish\DeskBox.exe",
    [string]$DataRoot = "$env:LOCALAPPDATA\DeskBox-AotCurve",
    [string]$CsvPath = "D:\project\wingezi\deskbox-aot-curve-samples.csv",
    [string]$Phase = "phase",
    [double]$Minutes = 10,
    [switch]$Attach,
    [double]$IntervalSeconds = 10
)

$ErrorActionPreference = 'Stop'

function Get-SampleRow([int]$Pid_, [string]$PhaseName, [string]$Note) {
    $p = Get-Process -Id $Pid_ -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $null }
    [PSCustomObject]@{
        timestamp      = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
        phase          = $PhaseName
        elapsedSeconds = [math]::Round(((Get-Date) - $script:startTime).TotalSeconds, 1)
        privateMB      = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        workingSetMB   = [math]::Round($p.WorkingSet64 / 1MB, 1)
        commitMB       = [math]::Round($p.PagedMemorySize64 / 1MB, 1)
        handles        = $p.HandleCount
        threads        = $p.Threads.Count
        cpuSeconds     = [math]::Round($p.CPU, 1)
        note           = $Note
    }
}

$script:startTime = Get-Date
$processId = $null

if (-not $Attach) {
    if (-not (Test-Path $ExePath)) { throw "Exe not found: $ExePath" }
    New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
    # 隔离数据根：非默认根派生独立的单实例锁 scope，可与安装版共存，
    # settings/log/journal 全部落在实验根下。
    $env:DESKBOX_AOT_PREVIEW_DATA_ROOT = $DataRoot
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
    $processId = $proc.Id
    Write-Host "Launched experiment instance PID=$processId dataRoot=$DataRoot"
} else {
    # 附加模式：按数据根对应的锁 scope 不好反查，直接按 exe 路径找唯一实例。
    $candidates = Get-Process DeskBox -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExePath }
    if ($candidates.Count -ne 1) {
        throw "Expected exactly 1 running instance of the experiment exe, found $($candidates.Count). Start it with the launch block first."
    }
    $processId = $candidates[0].Id
    Write-Host "Attached to experiment instance PID=$processId"
}

Write-Host "Sampling phase='$Phase' for $Minutes minute(s) every ${IntervalSeconds}s. Press M+Enter to insert a marker row; Ctrl+C to stop early (samples are flushed per row)."
Write-Host "CSV: $CsvPath"

$deadline = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    $row = Get-SampleRow $processId $Phase ''
    if ($null -eq $row) {
        Write-Warning "Process $processId exited; stopping sampler."
        break
    }
    $row | Export-Csv -Path $CsvPath -Append -NoTypeInformation
    Write-Host ("{0}  priv={1,8:N1} MB  ws={2,8:N1} MB  handles={3}  threads={4}" -f `
        $row.timestamp, $row.privateMB, $row.workingSetMB, $row.handles, $row.threads)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $IntervalSeconds) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq 'M') {
                $mark = Get-SampleRow $processId $Phase 'MARKER'
                if ($mark) { $mark | Export-Csv -Path $CsvPath -Append -NoTypeInformation }
                Write-Host '--- marker inserted ---'
            }
        }
        Start-Sleep -Milliseconds 200
    }
}

Write-Host "`nPhase '$Phase' done. Peak private in this phase:"
Import-Csv $CsvPath | Where-Object phase -eq $Phase |
    Measure-Object -Property privateMB -Maximum -Minimum -Average |
    ForEach-Object { Write-Host ("  min={0:N1}  avg={1:N1}  max={2:N1}  (MB, private commit)" -f $_.Minimum, $_.Average, $_.Maximum) }
