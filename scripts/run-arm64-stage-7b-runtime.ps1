[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot ".artifacts\arm64-stage7b-runtime"
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$nativeOutput = Join-Path $outputRoot "native"
$testResultsDirectory = Join-Path $outputRoot "test-results"
$cargoTargetRoot = Join-Path $repoRoot ".artifacts\cargo\arm64-stage7b"
$evidencePath = Join-Path $outputRoot "arm64-stage7b-runtime-evidence.json"
$nativeBuildScript = Join-Path $PSScriptRoot "build-rust-native.ps1"
$testProject = Join-Path $repoRoot "tests\DeskBox.Tests\DeskBox.Tests.csproj"

function Invoke-DeskBoxBuildProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Script,

        [Parameter(Mandatory)]
        [string]$ModuleOutput
    )

    $emitted = @(& $Script `
        -Platform ARM64 `
        -Configuration $Configuration `
        -CrtLinkage Static `
        -OutputDirectory $ModuleOutput `
        -CargoTargetDirectory $cargoTargetRoot)
    $results = @($emitted | Where-Object {
        $null -ne $_ -and
        $_.PSObject.Properties.Name -contains "RuntimeProbeExecuted"
    })
    if ($results.Count -ne 1) {
        throw "'$Script' emitted $($results.Count) structured build results; expected exactly one."
    }

    return $results[0]
}

function Get-DeskBoxFileEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{
            path = [System.IO.Path]::GetFullPath($Path)
            exists = $false
        }
    }

    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $item.FullName
        exists = $true
        lengthBytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function Get-DeskBoxCommandText {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $lines = @(& $FileName @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "'$FileName $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }

    return ($lines | ForEach-Object { $_.ToString() }) -join "`n"
}

New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
New-Item -ItemType Directory -Path $testResultsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $cargoTargetRoot -Force | Out-Null

$startedUtc = [DateTime]::UtcNow
$status = "failed"
$failure = $null
$nativeResult = $null
$testExitCode = $null
$testCounters = $null
$dotnetVersion = $null
$rustcVersion = $null
$cargoVersion = $null
$gitCommit = $null
$gitStatusEntries = @()
$processArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$osArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

try {
    if ($processArchitecture -ne "Arm64" -or $osArchitecture -ne "Arm64") {
        throw "Stage 7B requires a native ARM64 OS and process; OS=$osArchitecture, process=$processArchitecture."
    }

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $dotnetVersion = Get-DeskBoxCommandText -FileName $dotnet -Arguments @("--version")
    $rustcVersion = Get-DeskBoxCommandText -FileName "rustc" -Arguments @("-vV")
    $cargoVersion = Get-DeskBoxCommandText -FileName "cargo" -Arguments @("--version")
    if ($rustcVersion.IndexOf(
            "host: aarch64-pc-windows-msvc",
            [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Pinned rustc is not running as an aarch64-pc-windows-msvc host."
    }
    if ($rustcVersion.IndexOf(
            "rustc 1.96.0",
            [System.StringComparison]::Ordinal) -lt 0) {
        throw "Stage 7B requires the repository-pinned rustc 1.96.0 toolchain."
    }

    $gitCommit = Get-DeskBoxCommandText -FileName "git" -Arguments @(
        "-C", $repoRoot, "rev-parse", "HEAD")
    $gitStatusText = Get-DeskBoxCommandText -FileName "git" -Arguments @(
        "-C", $repoRoot, "status", "--porcelain=v1")
    $gitStatusEntries = @($gitStatusText -split "`r?`n" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })

    if (-not $NoRestore.IsPresent) {
        & $dotnet restore $testProject `
            -p:Platform=ARM64 `
            -p:RuntimeIdentifier=win-arm64 `
            --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            throw "ARM64 test restore failed with exit code $LASTEXITCODE."
        }
    }

    $nativeResult = Invoke-DeskBoxBuildProbe `
        -Script $nativeBuildScript `
        -ModuleOutput $nativeOutput
    if (-not $nativeResult.RuntimeProbeExecuted -or
        $nativeResult.ContractValidation -ne "runtime-load-plus-static-pe" -or
        $nativeResult.ProcessArchitecture -ne "Arm64" -or
        $nativeResult.MachineName -ne "ARM64" -or
        $nativeResult.CrtLinkage -ne "Static" -or
        $nativeResult.VcRuntimeImports.Count -ne 0) {
        throw "ARM64 build completed without the required runtime ABI plus static PE validation."
    }
    if ($nativeResult.AbiVersion -ne 2 -or $nativeResult.Capabilities -ne 1023) {
        throw "deskbox_native.dll returned an unexpected ABI or capability mask."
    }
    $previousGate = [Environment]::GetEnvironmentVariable(
        "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
        "Process")
    try {
        [Environment]::SetEnvironmentVariable(
            "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
            "1",
            "Process")
        $testArguments = @(
            "test",
            $testProject,
            "--configuration", $Configuration,
            "-p:Platform=ARM64",
            "-p:RuntimeIdentifier=win-arm64",
            "-p:DeskBoxRustCrtLinkage=Static",
            "-p:WindowsAppSdkBootstrapInitialize=false",
            "--results-directory", $testResultsDirectory,
            "--logger", "trx;LogFileName=arm64-runtime-gate.trx",
            "--filter", "FullyQualifiedName~DeskBox.Tests.Arm64NativeRuntimeGateTests",
            "--blame-hang",
            "--blame-hang-timeout", "5m",
            "--verbosity", "minimal")
        if ($NoRestore.IsPresent) {
            $testArguments += "--no-restore"
        }

        & $dotnet @testArguments
        $testExitCode = $LASTEXITCODE
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
            $previousGate,
            "Process")
    }

    $trxPath = Join-Path $testResultsDirectory "arm64-runtime-gate.trx"
    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -ne $counters) {
            $testCounters = [ordered]@{
                total = [int]$counters.total
                executed = [int]$counters.executed
                passed = [int]$counters.passed
                failed = [int]$counters.failed
                error = [int]$counters.error
                timeout = [int]$counters.timeout
                aborted = [int]$counters.aborted
            }
        }
    }
    if ($testExitCode -ne 0) {
        throw "ARM64 product runtime tests failed with exit code $testExitCode."
    }
    if ($null -eq $testCounters -or
        $testCounters.executed -lt 1 -or
        $testCounters.failed -ne 0 -or
        $testCounters.error -ne 0 -or
        $testCounters.timeout -ne 0 -or
        $testCounters.aborted -ne 0) {
        throw "ARM64 TRX counters do not prove a complete passing runtime gate."
    }

    $status = "passed"
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    $finishedUtc = [DateTime]::UtcNow
    $evidence = [ordered]@{
        schema = "deskbox.arm64-stage7b-runtime-evidence.v1"
        status = $status
        evidenceLevel = "github-hosted-arm64-runtime"
        startedUtc = $startedUtc.ToString("O")
        finishedUtc = $finishedUtc.ToString("O")
        durationSeconds = [Math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 3)
        targetArchitectureRuntimeExecuted = $status -eq "passed"
        targetDeviceExecuted = $status -eq "passed"
        physicalUserDeviceExecuted = $false
        interactiveDesktopExecuted = $false
        process = [ordered]@{
            osArchitecture = $osArchitecture
            processArchitecture = $processArchitecture
            osDescription =
                [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            powershell = $PSVersionTable.PSVersion.ToString()
        }
        runner = [ordered]@{
            provider = "GitHub Actions"
            name = $env:RUNNER_NAME
            environment = $env:RUNNER_ENVIRONMENT
            architecture = $env:RUNNER_ARCH
            imageOs = $env:ImageOS
            imageVersion = $env:ImageVersion
            repository = $env:GITHUB_REPOSITORY
            workflow = $env:GITHUB_WORKFLOW
            runId = $env:GITHUB_RUN_ID
            runAttempt = $env:GITHUB_RUN_ATTEMPT
            ref = $env:GITHUB_REF
            sha = $env:GITHUB_SHA
        }
        source = [ordered]@{
            repositoryRoot = $repoRoot
            commit = $gitCommit
            dirty = $gitStatusEntries.Count -gt 0
            statusEntries = $gitStatusEntries
        }
        toolchain = [ordered]@{
            dotnet = $dotnetVersion
            rustc = $rustcVersion
            cargo = $cargoVersion
        }
        native = if ($null -eq $nativeResult) { $null } else {
            [ordered]@{
                abiVersion = $nativeResult.AbiVersion
                capabilities = $nativeResult.Capabilities
                crtLinkage = $nativeResult.CrtLinkage
                machine = $nativeResult.MachineName
                machineHex = $nativeResult.MachineHex
                exportCount = $nativeResult.ExportCount
                contractValidation = $nativeResult.ContractValidation
                runtimeProbeExecuted = $nativeResult.RuntimeProbeExecuted
                dll = Get-DeskBoxFileEvidence -Path $nativeResult.Dll
                pdb = Get-DeskBoxFileEvidence -Path $nativeResult.Pdb
            }
        }
        tests = [ordered]@{
            exitCode = $testExitCode
            counters = $testCounters
            trx = Get-DeskBoxFileEvidence -Path (
                Join-Path $testResultsDirectory "arm64-runtime-gate.trx")
        }
        failure = $failure
    }
    $evidence | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $evidencePath -Encoding utf8
}

if ($status -ne "passed") {
    throw "DeskBox Stage 7B ARM64 runtime gate failed. Evidence: '$evidencePath'. $failure"
}

[pscustomobject]@{
    Status = $status
    Evidence = $evidencePath
    NativeRuntimeProbeExecuted = $nativeResult.RuntimeProbeExecuted
    TestsExecuted = $testCounters.executed
    TestsPassed = $testCounters.passed
}
