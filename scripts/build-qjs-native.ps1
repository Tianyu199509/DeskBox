[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$IntermediateDirectory,

    [ValidateSet("Dynamic", "Static")]
    [string]$CrtLinkage = "Static",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$qjsRoot = Join-Path $repoRoot "native\deskbox-qjs"
$amalgamSource = Join-Path $qjsRoot "vendor\quickjs-amalgam.c"
$amalgamHeader = Join-Path $qjsRoot "vendor\quickjs.h"
$bridgeSource = Join-Path $qjsRoot "deskbox_qjs_bridge.c"
$publicHeader = Join-Path $repoRoot "native\include\deskbox_qjs.h"

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$objRoot = if ([string]::IsNullOrWhiteSpace($IntermediateDirectory)) {
    Join-Path $outputRoot "obj"
}
else {
    [System.IO.Path]::GetFullPath($IntermediateDirectory)
}

$outputDll = Join-Path $outputRoot "deskbox_qjs.dll"
$outputPdb = Join-Path $outputRoot "deskbox_qjs.pdb"

$msvcHelper = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
$peHelper = Join-Path $PSScriptRoot "native-pe-contract.ps1"
if (-not (Test-Path -LiteralPath $msvcHelper -PathType Leaf)) {
    throw "MSVC environment helper was not found: '$msvcHelper'."
}
if (-not (Test-Path -LiteralPath $peHelper -PathType Leaf)) {
    throw "Native PE contract helper was not found: '$peHelper'."
}

. $msvcHelper
. $peHelper

$requiredExports = @(
    "deskbox_qjs_abi_version",
    "deskbox_qjs_compile_module",
    "deskbox_qjs_invoke_bytecode",
    "deskbox_qjs_free_buffer"
)

if (-not $ValidateOnly.IsPresent) {
    foreach ($src in @($amalgamSource, $amalgamHeader, $bridgeSource, $publicHeader)) {
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            throw "Required QuickJS native source is missing: '$src'."
        }
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $objRoot -Force | Out-Null

    $toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
    $envState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
    try {
        $clExe = Join-Path $toolchain.LinkerDirectory "cl.exe"
        $linkExe = $toolchain.LinkerPath
        if (-not (Test-Path -LiteralPath $clExe -PathType Leaf)) {
            throw "MSVC compiler cl.exe was not found at '$clExe'."
        }

        $crtFlag = if ($CrtLinkage -eq "Static") { "/MT" } else { "/MD" }
        $optFlags = if ($Configuration -eq "Release") {
            @("/O2", "/GL-")
        }
        else {
            @("/O2")
        }

        $commonFlags = @(
            "/nologo",
            "/c",
            $crtFlag,
            "/Zi",
            "/Gy",
            "/Gw",
            "/GF",
            "/utf-8",
            "/std:c11",
            "/experimental:c11atomics",
            "/wd4098",
            "/wd4100",
            "/wd4244",
            "/wd4267",
            "/D_CRT_SECURE_NO_WARNINGS",
            "/DWIN32_LEAN_AND_MEAN",
            "/DNOMINMAX"
        ) + $optFlags

        $compilerPdb = Join-Path $objRoot "deskbox_qjs_vc.pdb"
        $amalgamObj = Join-Path $objRoot "quickjs-amalgam.obj"
        $bridgeObj = Join-Path $objRoot "deskbox_qjs_bridge.obj"
        $fingerprintPath = Join-Path $objRoot "build-options.fingerprint"

        $scriptWriteTicks = (Get-Item -LiteralPath $PSCommandPath).LastWriteTimeUtc.Ticks
        $msvcHelperWriteTicks = (Get-Item -LiteralPath $msvcHelper).LastWriteTimeUtc.Ticks
        $buildFingerprint = "$Platform|$Configuration|$CrtLinkage|$crtFlag|$($optFlags -join ',')|$clExe|$scriptWriteTicks|$msvcHelperWriteTicks"
        $previousFingerprint = if (Test-Path -LiteralPath $fingerprintPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($fingerprintPath, [System.Text.Encoding]::UTF8).Trim()
        }
        else {
            ""
        }
        $fingerprintChanged = $previousFingerprint -cne $buildFingerprint
        if ($fingerprintChanged) {
            foreach ($staleArtifact in @($amalgamObj, $bridgeObj, $compilerPdb)) {
                if (Test-Path -LiteralPath $staleArtifact -PathType Leaf) {
                    Remove-Item -LiteralPath $staleArtifact -Force
                }
            }
        }

        $needAmalgamCompile = $fingerprintChanged -or -not (Test-Path -LiteralPath $amalgamObj -PathType Leaf)
        if (-not $needAmalgamCompile) {
            $objTime = (Get-Item -LiteralPath $amalgamObj).LastWriteTimeUtc
            $srcTime = (Get-Item -LiteralPath $amalgamSource).LastWriteTimeUtc
            $hdrTime = (Get-Item -LiteralPath $amalgamHeader).LastWriteTimeUtc
            if ($srcTime -gt $objTime -or $hdrTime -gt $objTime) {
                $needAmalgamCompile = $true
            }
        }

        if ($needAmalgamCompile) {
            $amalgamArgs = $commonFlags + @(
                "/Fd$compilerPdb",
                "/Fo$amalgamObj",
                $amalgamSource
            )
            & $clExe @amalgamArgs | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "QuickJS amalgamation compilation failed for $Platform with exit code $LASTEXITCODE."
            }
        }

        $bridgeArgs = $commonFlags + @(
            "/Fd$compilerPdb",
            "/Fo$bridgeObj",
            $bridgeSource
        )
        & $clExe @bridgeArgs | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "DeskBox QuickJS bridge compilation failed for $Platform with exit code $LASTEXITCODE."
        }

        [System.IO.File]::WriteAllText(
            $fingerprintPath,
            $buildFingerprint + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))

        $stagedDll = Join-Path $objRoot "deskbox_qjs.dll"
        $stagedPdb = Join-Path $objRoot "deskbox_qjs.pdb"
        $stagedImpLib = Join-Path $objRoot "deskbox_qjs.lib"

        $linkArgs = @(
            "/NOLOGO",
            "/DLL",
            "/DEBUG:FULL",
            "/OPT:REF",
            "/OPT:ICF",
            "/INCREMENTAL:NO",
            "/DYNAMICBASE",
            "/HIGHENTROPYVA",
            "/NXCOMPAT",
            "/OUT:$stagedDll",
            "/PDB:$stagedPdb",
            "/IMPLIB:$stagedImpLib",
            $amalgamObj,
            $bridgeObj,
            "kernel32.lib",
            "bcrypt.lib"
        )
        & $linkExe @linkArgs | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "DeskBox QuickJS DLL link failed for $Platform with exit code $LASTEXITCODE."
        }

        Copy-Item -LiteralPath $stagedDll -Destination $outputDll -Force
        if (Test-Path -LiteralPath $stagedPdb -PathType Leaf) {
            Copy-Item -LiteralPath $stagedPdb -Destination $outputPdb -Force
        }
    }
    finally {
        Exit-DeskBoxMsvcEnvironment -State $envState
    }
}

$contract = Get-DeskBoxNativePeContract `
    -Path $outputDll `
    -ExpectedPlatform $Platform `
    -RequiredExports $requiredExports

if ($CrtLinkage -eq "Static") {
    $vcImports = @(
        $contract.ImportedModules |
            Where-Object { $_ -match '^(?:VCRUNTIME|MSVCP|VCCORLIB|CONCRT|UCRTBASE|api-ms-win-crt-)' }
    )
    if ($vcImports.Count -gt 0) {
        throw "deskbox_qjs.dll ($Platform) unexpectedly imports dynamic VC++/UCRT runtime DLLs: $($vcImports -join ', ')."
    }
}

[PSCustomObject]@{
    Platform = $Platform
    Configuration = $Configuration
    CrtLinkage = $CrtLinkage
    AbiVersion = 1
    OutputDirectory = $outputRoot
    Dll = $outputDll
    Pdb = if (Test-Path -LiteralPath $outputPdb -PathType Leaf) { $outputPdb } else { $null }
    Machine = $contract.Machine
    MachineHex = $contract.MachineHex
    RequiredExports = @($contract.RequiredExports)
    Exports = $contract.Exports
    ImportedModules = $contract.ImportedModules
}
