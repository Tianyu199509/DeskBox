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
$requiredArtifacts = @(
    "deskbox_native.dll",
    "deskbox_native.pdb"
)
$requiredExportNames = @(
    "deskbox_native_abi_version",
    "deskbox_native_capabilities",
    "deskbox_shortcut_read_v2",
    "deskbox_shortcut_resolve_no_ui_v2",
    "deskbox_shortcut_write_v2",
    "deskbox_shortcut_resolve_with_ui_v2",
    "deskbox_music_volume_v1",
    "deskbox_explorer_shell_launch_v1",
    "deskbox_quick_access_v1",
    "deskbox_recycle_bin_v1",
    "deskbox_plugin_verify_ed25519_v1"
)
$peContractScript = Join-Path $PSScriptRoot "native-pe-contract.ps1"
$arm64EnvironmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
if (-not (Test-Path -LiteralPath $peContractScript -PathType Leaf)) {
    throw "Native PE contract helper was not found: '$peContractScript'."
}
. $peContractScript
if ($Platform -eq "ARM64") {
    if (-not (Test-Path -LiteralPath $arm64EnvironmentScript -PathType Leaf)) {
        throw "ARM64 MSVC environment helper was not found: '$arm64EnvironmentScript'."
    }
    . $arm64EnvironmentScript
}

if (-not $ValidateOnly.IsPresent) {
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
        throw "Rust target '$targetTriple' is not installed for the pinned toolchain. Install rust-std 1.96.0 for this target before building $Platform."
    }

    $cargoArguments = @(
        "build",
        "--manifest-path", $manifestPath,
        "--package", "deskbox-native",
        "--locked",
        "--target", $targetTriple,
        "--target-dir", $cargoTargetRoot
    )
    if ($Configuration -eq "Release") {
        $cargoArguments += "--release"
    }

    $arm64EnvironmentState = $null
    if ($Platform -eq "ARM64") {
        $arm64Toolchain = Get-DeskBoxArm64MsvcEnvironment
        $arm64EnvironmentState =
            Enter-DeskBoxArm64MsvcEnvironment -Toolchain $arm64Toolchain
    }

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
    $encodedRustFlags = @("-C", $crtTargetFeature) -join [char]0x1F
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
                throw "Rust native build failed with exit code $LASTEXITCODE."
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
        if ($null -ne $arm64EnvironmentState) {
            Exit-DeskBoxArm64MsvcEnvironment -State $arm64EnvironmentState
        }
    }

    $cargoOutput = Join-Path $cargoTargetRoot "$targetTriple\$cargoProfile"
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    foreach ($artifactName in $requiredArtifacts) {
        $sourcePath = Join-Path $cargoOutput $artifactName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Rust native build did not produce '$sourcePath'."
        }

        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $outputRoot $artifactName) -Force
    }
}

foreach ($artifactName in $requiredArtifacts) {
    $outputPath = Join-Path $outputRoot $artifactName
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw "Rust native output is missing '$outputPath'."
    }
}

if (-not ("DeskBoxNativeContractProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class DeskBoxNativeContractInfo
{
    public DeskBoxNativeContractInfo(uint abiVersion, ulong capabilities, string[] requiredExports)
    {
        AbiVersion = abiVersion;
        Capabilities = capabilities;
        RequiredExports = requiredExports;
    }

    public uint AbiVersion { get; private set; }
    public ulong Capabilities { get; private set; }
    public string[] RequiredExports { get; private set; }
}

public static class DeskBoxNativeContractProbe
{
    private static readonly string[] RequiredExportNames = new[]
    {
        "deskbox_native_abi_version",
        "deskbox_native_capabilities",
        "deskbox_shortcut_read_v2",
        "deskbox_shortcut_resolve_no_ui_v2",
        "deskbox_shortcut_write_v2",
        "deskbox_shortcut_resolve_with_ui_v2",
        "deskbox_music_volume_v1",
        "deskbox_explorer_shell_launch_v1",
        "deskbox_quick_access_v1",
        "deskbox_recycle_bin_v1",
        "deskbox_plugin_verify_ed25519_v1"
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CapabilitiesDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string exportName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    public static DeskBoxNativeContractInfo ReadContract(string modulePath)
    {
        const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        const uint LoadLibrarySearchSystem32 = 0x00000800;
        IntPtr module = LoadLibraryExW(
            modulePath,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load the DeskBox native module.");
        }

        try
        {
            foreach (string exportName in RequiredExportNames)
            {
                RequireExport(module, exportName);
            }

            IntPtr abiExport = RequireExport(module, "deskbox_native_abi_version");
            var abiProbe = (AbiVersionDelegate)Marshal.GetDelegateForFunctionPointer(
                abiExport,
                typeof(AbiVersionDelegate));
            IntPtr capabilitiesExport = RequireExport(module, "deskbox_native_capabilities");
            var capabilitiesProbe = (CapabilitiesDelegate)Marshal.GetDelegateForFunctionPointer(
                capabilitiesExport,
                typeof(CapabilitiesDelegate));

            return new DeskBoxNativeContractInfo(
                abiProbe(),
                capabilitiesProbe(),
                (string[])RequiredExportNames.Clone());
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static IntPtr RequireExport(IntPtr module, string exportName)
    {
        IntPtr export = GetProcAddress(module, exportName);
        if (export == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(
                "The DeskBox native export '" + exportName + "' is missing.");
        }

        return export;
    }
}
"@
}

$copiedDll = Join-Path $outputRoot "deskbox_native.dll"
$peContract = Get-DeskBoxNativePeContract `
    -Path $copiedDll `
    -ExpectedPlatform $Platform `
    -RequiredExports $requiredExportNames
$processArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$expectedProcessArchitecture = if ($Platform -eq "ARM64") { "Arm64" } else { "X64" }
$runtimeProbeExecuted = $processArchitecture -eq $expectedProcessArchitecture
$runtimeProbeReason = if ($runtimeProbeExecuted) {
    "host-and-target-architectures-match"
}
else {
    "cross-architecture-static-validation-only"
}
$importedModules = @($peContract.ImportedModules)
$vcruntimeImports = @($importedModules | Where-Object {
    $_.StartsWith("VCRUNTIME", [System.StringComparison]::OrdinalIgnoreCase) -or
    $_.StartsWith("MSVCP", [System.StringComparison]::OrdinalIgnoreCase)
})
if ($CrtLinkage -eq "Static" -and $vcruntimeImports.Count -gt 0) {
    throw "Static CRT build still imports redistributable CRT modules: $($vcruntimeImports -join ', ')."
}
$contractValidation = if ($runtimeProbeExecuted) {
    "runtime-load-plus-static-pe"
}
else {
    "static-pe-plus-frozen-source-constants"
}

if ($runtimeProbeExecuted) {
    $contract = [DeskBoxNativeContractProbe]::ReadContract($copiedDll)
    $abiVersion = $contract.AbiVersion
    $capabilities = $contract.Capabilities
    $requiredExports = @($contract.RequiredExports)
}
else {
    $header = Get-Content -LiteralPath (Join-Path $repoRoot "native\include\deskbox_native.h") -Raw
    $rustSource = Get-Content -LiteralPath (Join-Path $repoRoot "native\deskbox-native\src\lib.rs") -Raw
    foreach ($token in @(
            "#define DESKBOX_NATIVE_ABI_VERSION 2u",
            "#define DESKBOX_NATIVE_CAPABILITIES DESKBOX_NATIVE_CAPABILITIES_PLUGIN_V1")) {
        if ($header.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
            throw "Rust native frozen header contract is missing '$token'."
        }
    }
    foreach ($token in @(
            "pub const DESKBOX_NATIVE_ABI_VERSION: u32 = 2;",
            "pub const DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1: u64 = 1 << 8;")) {
        if ($rustSource.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
            throw "Rust native frozen source contract is missing '$token'."
        }
    }

    $abiVersion = 2
    $capabilities = 1023
    $requiredExports = @($peContract.RequiredExports)
}
if ($abiVersion -ne 2) {
    throw "Rust native ABI mismatch: expected 2, found $abiVersion."
}

if ($capabilities -ne 1023) {
    throw "Rust native Stage 5B-4C1B2B capability mismatch: expected 1023, found 0x$($capabilities.ToString('X16'))."
}

[PSCustomObject]@{
    Platform = $Platform
    Configuration = $Configuration
    Target = $targetTriple
    CrtLinkage = $CrtLinkage
    CargoTargetDirectory = $cargoTargetRoot
    OutputDirectory = $outputRoot
    AbiVersion = $abiVersion
    Capabilities = $capabilities
    RequiredExports = $requiredExports
    Machine = $peContract.Machine
    MachineHex = $peContract.MachineHex
    MachineName = $peContract.MachineName
    ExportCount = $peContract.ExportCount
    SizeOfImage = $peContract.SizeOfImage
    ImportCount = $peContract.ImportCount
    ImportedModules = $importedModules
    VcRuntimeImports = $vcruntimeImports
    ContractValidation = $contractValidation
    RuntimeProbeExecuted = $runtimeProbeExecuted
    RuntimeProbeReason = $runtimeProbeReason
    ProcessArchitecture = $processArchitecture
    ExpectedProcessArchitecture = $expectedProcessArchitecture
    ValidationOnly = $ValidateOnly.IsPresent
    Dll = $copiedDll
    Pdb = Join-Path $outputRoot "deskbox_native.pdb"
}
