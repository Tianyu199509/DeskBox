[CmdletBinding()]
param(
    # Path of the built DeskBox.Cli.exe. Defaults to the Debug CLI output.
    [string]$CliPath,

    # Path of the built DeskBox.exe used when -LaunchApp is set.
    [string]$AppPath,

    # Launch DeskBox with an isolated development data root for the smoke,
    # then stop it afterwards. When not set, the script targets an app that
    # is already running.
    [switch]$LaunchApp,

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "CommandApiEndToEnd"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$evidenceRoot = Join-Path $repoRoot ".artifacts\commandapi-smoke"
$devDataRoot = Join-Path $repoRoot ".artifacts\commandapi-smoke\data-root"
$cliPath = if ([string]::IsNullOrWhiteSpace($CliPath)) {
    Join-Path $repoRoot "src\DeskBox.Cli\bin\Debug\net10.0\DeskBox.Cli.exe"
}
else {
    [System.IO.Path]::GetFullPath($CliPath)
}
$appPath = if ([string]::IsNullOrWhiteSpace($AppPath)) {
    Join-Path $repoRoot "src\DeskBox\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\DeskBox.exe"
}
else {
    [System.IO.Path]::GetFullPath($AppPath)
}

if (-not (Test-Path $cliPath)) {
    throw "DeskBox.Cli.exe not found at '$cliPath'. Build src/DeskBox.Cli first."
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$launchedProcess = $null
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

function Invoke-CliScenario {
    param([string]$Name, [string[]]$Arguments, [scriptblock]$Assert)
    Write-Host "  [$scenario] $Name"
    $output = (& $cliPath @Arguments 2>&1) -join "`n"
    $exitCode = $LASTEXITCODE
    & $Assert @{ Output = $output; ExitCode = $exitCode } | Out-Null
    $results.Add([ordered]@{
        scenario      = $Name
        exitCode      = $exitCode
        outputPreview = if ($output.Length -gt 400) { $output.Substring(0, 400) } else { $output }
    }) | Out-Null
}

function Wait-ForPipe {
    param([string]$Pipe)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path "\\.\pipe\$Pipe") {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

try {
    if ($LaunchApp) {
        if (-not (Test-Path $appPath)) {
            throw "DeskBox.exe not found at '$appPath'. Build src/DeskBox with -p:Platform=x64 first."
        }
        New-Item -ItemType Directory -Force -Path $devDataRoot | Out-Null
        Write-Host "  [$scenario] launching DeskBox with dev data root $devDataRoot"
        # Child processes inherit the caller environment; setting it here
        # scopes the dev data root to exactly this launched instance.
        $env:DESKBOX_DEV_DATA_ROOT = $devDataRoot
        try {
            $launchedProcess = Start-Process -FilePath $appPath -PassThru
        }
        finally {
            Remove-Item Env:\DESKBOX_DEV_DATA_ROOT -ErrorAction SilentlyContinue
        }
    }

    # The dev data root hashes to a distinct instance scope; resolve the pipe
    # the same way the app does by asking the CLI to probe with the dev root.
    $env:DESKBOX_CLI_SMOKE = "1"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    Invoke-CliScenario -Name "ping" -Arguments @("ping", "--timeout", "10000") -Assert {
        param($o)
        if ($o.ExitCode -ne 0) { throw "ping failed with exit code $($o.ExitCode): $($o.Output)" }
        if ($o.Output -notmatch "running") { throw "ping output missing 'running': $($o.Output)" }
    }

    Invoke-CliScenario -Name "info" -Arguments @("info", "--json") -Assert {
        param($o)
        if ($o.ExitCode -ne 0) { throw "info failed: $($o.Output)" }
        if ($o.Output -notmatch '"protocolVersion":1') { throw "info JSON missing protocolVersion" }
        if ($o.Output -notmatch '"capabilities"') { throw "info JSON missing capabilities" }
    }

    Invoke-CliScenario -Name "schema" -Arguments @("schema") -Assert {
        param($o)
        if ($o.ExitCode -ne 0) { throw "schema failed: $($o.Output)" }
        if ($o.Output -notmatch "server/ping") { throw "schema output missing server/ping" }
        if ($o.Output -notmatch "quickcapture/add") { throw "schema output missing quickcapture/add" }
    }

    Invoke-CliScenario -Name "settings" -Arguments @("settings", "get", "--json") -Assert {
        param($o)
        if ($o.ExitCode -ne 0) { throw "settings get failed: $($o.Output)" }
        if ($o.Output -notmatch '"theme"') { throw "settings snapshot missing theme" }
    }

    Invoke-CliScenario -Name "quickcapture-add-dry-run" -Arguments @(
        "quickcapture", "add", "commandapi-smoke-entry", "--dry-run") -Assert {
        param($o)
        if ($o.ExitCode -ne 0) { throw "quickcapture dry-run failed: $($o.Output)" }
    }

    Write-Host "  [$scenario] all scenarios passed"
    $summary = [ordered]@{
        scenario   = $scenario
        status     = "passed"
        completedAt = (Get-Date).ToUniversalTime().ToString("o")
        cliPath    = $cliPath
        launchedApp = [bool]$launchedProcess
        results    = $results
    }
    $summaryPath = Join-Path $evidenceRoot "summary.json"
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8
    Write-Host "  [$scenario] evidence written to $summaryPath"
    exit 0
}
catch {
    $failure = [ordered]@{
        scenario   = $scenario
        status     = "failed"
        completedAt = (Get-Date).ToUniversalTime().ToString("o")
        error      = $_.Exception.Message
        results    = $results
    }
    $failurePath = Join-Path $evidenceRoot "summary.json"
    $failure | ConvertTo-Json -Depth 6 | Set-Content -Path $failurePath -Encoding UTF8
    Write-Host "  [$scenario] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($launchedProcess -and -not $launchedProcess.HasExited) {
        Write-Host "  [$scenario] stopping launched DeskBox process"
        try {
            $launchedProcess.CloseMainWindow() | Out-Null
            if (-not $launchedProcess.WaitForExit(10_000)) {
                $launchedProcess.Kill()
            }
        }
        catch {
            Write-Host "  [$scenario] warning: failed to stop app cleanly: $($_.Exception.Message)"
        }
    }
}
