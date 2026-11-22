[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$CargoTargetDirectory,

    [ValidateSet("Dynamic", "Static")]
    [string]$CrtLinkage = "Static",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $repoRoot "native\Cargo.toml"
$targetTriple = if ($Platform -eq "ARM64") {
    "aarch64-pc-windows-msvc"
}
else {
    "x86_64-pc-windows-msvc"
}
$cargoProfile = if ($Configuration -eq "Release") { "release" } else { "debug" }
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$cargoTargetRoot = if ([string]::IsNullOrWhiteSpace($CargoTargetDirectory)) {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot "native\target"))
}
else {
    [System.IO.Path]::GetFullPath($CargoTargetDirectory)
}
$msvcEnvironmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
if (-not (Test-Path -LiteralPath $msvcEnvironmentScript -PathType Leaf)) {
    throw "MSVC environment helper was not found: '$msvcEnvironmentScript'."
}
. $msvcEnvironmentScript

$outputExe = Join-Path $outputRoot "DeskBox.ThumbnailProxy.exe"
$outputPdb = Join-Path $outputRoot "DeskBox.ThumbnailProxy.pdb"

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a PE image."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' does not contain a PE signature."
        }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

if (-not $ValidateOnly.IsPresent) {
    $defaultCargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $defaultCargoBin "cargo.exe") -PathType Leaf)) {
        $env:PATH = "$defaultCargoBin;$env:PATH"
    }
    $cargo = (Get-Command cargo -ErrorAction Stop).Source
    $rustc = (Get-Command rustc -ErrorAction Stop).Source
    # PowerShell 5.1 wraps redirected native stderr as ErrorRecords, and
    # $ErrorActionPreference = "Stop" turns the first rustup/rustc progress
    # line (for example "info: syncing channel updates...") into a fatal
    # RemoteException on runners without a pre-synced toolchain. Scope a
    # tolerant preference around the probe and keep stdout strings only.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $targetLibDirectory = @(& $rustc --print target-libdir --target $targetTriple 2>$null |
        Where-Object { $_ -is [string] })
    $ErrorActionPreference = $previousErrorActionPreference
    if ($LASTEXITCODE -ne 0 -or
        $targetLibDirectory.Count -ne 1 -or
        -not (Test-Path -LiteralPath $targetLibDirectory[0] -PathType Container)) {
        throw "Rust target '$targetTriple' is not installed for the pinned toolchain."
    }

    $cargoArguments = @(
        "build",
        "--manifest-path", $manifestPath,
        "--package", "deskbox-thumbnail-proxy",
        "--locked",
        "--target", $targetTriple,
        "--target-dir", $cargoTargetRoot
    )
    if ($Configuration -eq "Release") {
        $cargoArguments += "--release"
    }

    $msvcToolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
    $msvcEnvironmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $msvcToolchain

    $previousCargoColor = [Environment]::GetEnvironmentVariable("CARGO_TERM_COLOR", "Process")
    $previousEncodedRustFlags =
        [Environment]::GetEnvironmentVariable("CARGO_ENCODED_RUSTFLAGS", "Process")
    $previousRustFlags = [Environment]::GetEnvironmentVariable("RUSTFLAGS", "Process")
    $crtTargetFeature = if ($CrtLinkage -eq "Static") {
        "target-feature=+crt-static"
    }
    else {
        "target-feature=-crt-static"
    }
    # The workspace profile unwinds for the in-process deskbox-native FFI
    # catch_unwind guards; this out-of-process proxy keeps panic=abort.
    $encodedRustFlags = @("-C", $crtTargetFeature, "-C", "panic=abort") -join [char]0x1F
    try {
        [Environment]::SetEnvironmentVariable("CARGO_TERM_COLOR", "never", "Process")
        [Environment]::SetEnvironmentVariable(
            "CARGO_ENCODED_RUSTFLAGS",
            $encodedRustFlags,
            "Process")
        [Environment]::SetEnvironmentVariable("RUSTFLAGS", $null, "Process")
        Push-Location $repoRoot
        try {
            & $cargo @cargoArguments
            if ($LASTEXITCODE -ne 0) {
                throw "Rust thumbnail proxy build failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("CARGO_TERM_COLOR", $previousCargoColor, "Process")
        [Environment]::SetEnvironmentVariable(
            "CARGO_ENCODED_RUSTFLAGS",
            $previousEncodedRustFlags,
            "Process")
        [Environment]::SetEnvironmentVariable("RUSTFLAGS", $previousRustFlags, "Process")
        if ($null -ne $msvcEnvironmentState) {
            Exit-DeskBoxMsvcEnvironment -State $msvcEnvironmentState
        }
    }

    $cargoOutput = Join-Path $cargoTargetRoot "$targetTriple\$cargoProfile"
    $builtExe = Join-Path $cargoOutput "deskbox-thumbnail-proxy.exe"
    $builtPdb = Join-Path $cargoOutput "deskbox_thumbnail_proxy.pdb"
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
        throw "Rust thumbnail proxy build did not produce '$builtExe'."
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $stampUtc = [DateTime]::UtcNow
    Copy-Item -LiteralPath $builtExe -Destination $outputExe -Force
    (Get-Item -LiteralPath $outputExe).LastWriteTimeUtc = $stampUtc
    if (Test-Path -LiteralPath $builtPdb -PathType Leaf) {
        Copy-Item -LiteralPath $builtPdb -Destination $outputPdb -Force
        (Get-Item -LiteralPath $outputPdb).LastWriteTimeUtc = $stampUtc
    }
}

if (-not (Test-Path -LiteralPath $outputExe -PathType Leaf)) {
    throw "Rust thumbnail proxy output is missing '$outputExe'."
}
$expectedMachine = if ($Platform -eq "ARM64") { 0xAA64 } else { 0x8664 }
$machine = Get-PeMachine -Path $outputExe
if ($machine -ne $expectedMachine) {
    throw "Unexpected thumbnail proxy PE machine 0x$($machine.ToString('X4')); expected $Platform (0x$($expectedMachine.ToString('X4')))."
}

# Verify embedded RT_MANIFEST declares PerMonitorV2 DPI awareness (proves mt.exe embedded DeskBox.ThumbnailProxy.manifest).
$peBytes = [System.IO.File]::ReadAllBytes($outputExe)
$peText = [System.Text.Encoding]::ASCII.GetString($peBytes)
if (-not $peText.Contains("PerMonitorV2") -or -not $peText.Contains("DeskBox.ThumbnailProxy.app")) {
    throw "DeskBox.ThumbnailProxy.exe is missing its embedded PerMonitorV2 application manifest."
}

[PSCustomObject]@{
    Platform = $Platform
    Configuration = $Configuration
    Target = $targetTriple
    CrtLinkage = $CrtLinkage
    CargoTargetDirectory = $cargoTargetRoot
    OutputDirectory = $outputRoot
    ValidationOnly = $ValidateOnly.IsPresent
    Exe = $outputExe
    Pdb = if (Test-Path -LiteralPath $outputPdb -PathType Leaf) { $outputPdb } else { $null }
    Machine = $machine
    MachineHex = "0x$($machine.ToString('X4'))"
}
