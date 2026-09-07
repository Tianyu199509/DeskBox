[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [switch]$NoStop,

    [switch]$ExpectExistingInstance,

    [switch]$AllowEarlyExit,

    [ValidateRange(1, 30)]
    [int]$StartupWaitSeconds = 5
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RequiredAuditProfileVersion = 60
$RequiredSummarySchemaVersion = 55
$RequiredRustAbiVersion = 2
$RequiredRustCapabilities = 511
$RequiredRustExportCount = 10

$repoRootPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$auditRunRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRootPath ".artifacts\aot-audit\win-x64"))
$expectedSummaryPath = [System.IO.Path]::GetFullPath(
    (Join-Path $auditRunRoot "summary.json"))
$expectedPublishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $auditRunRoot "publish"))
$previewEvidenceDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRootPath ".artifacts\aot-preview\win-x64"))
$sessionPath = Join-Path $previewEvidenceDirectory "session.json"

function Test-PathEqual {
    param(
        [Parameter(Mandatory)]
        [string]$First,

        [Parameter(Mandatory)]
        [string]$Second
    )

    return [string]::Equals(
        [System.IO.Path]::GetFullPath($First).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.IO.Path]::GetFullPath($Second).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathEqualOrInside {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    return [string]::Equals(
            $normalizedRoot,
            $normalizedCandidate,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedCandidate.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-TextSha256 {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DirectoryStateFingerprint {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $normalizedPath -PathType Container)) {
        return [PSCustomObject]@{
            exists = $false
            fileCount = 0
            bytes = 0L
            fingerprint = Get-TextSha256 -Value "<missing>"
        }
    }

    $files = @(Get-ChildItem -LiteralPath $normalizedPath -File -Recurse -Force)
    $records = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($normalizedPath.Length).TrimStart('\', '/')
        $records.Add(
            ("{0}|{1}|{2}" -f @(
                $relativePath.Replace('\', '/').ToUpperInvariant(),
                $file.Length,
                $file.LastWriteTimeUtc.Ticks)))
    }
    $records.Sort([System.StringComparer]::Ordinal)

    return [PSCustomObject]@{
        exists = $true
        fileCount = $files.Count
        bytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        fingerprint = Get-TextSha256 -Value ([string]::Join("`n", $records))
    }
}

function Get-AotPreviewProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $normalizedExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
    return @(
        Get-CimInstance Win32_Process -Filter "Name='DeskBox.exe'" |
            Where-Object {
                if ([string]::IsNullOrWhiteSpace($_.ExecutablePath)) {
                    return $false
                }

                try {
                    return [string]::Equals(
                        [System.IO.Path]::GetFullPath($_.ExecutablePath),
                        $normalizedExecutablePath,
                        [System.StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    return $false
                }
            }
    )
}

if ($ExpectExistingInstance.IsPresent -and -not $NoStop.IsPresent) {
    throw "-ExpectExistingInstance requires -NoStop so the primary preview process is preserved."
}

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = $expectedSummaryPath
}
$SummaryPath = [System.IO.Path]::GetFullPath($SummaryPath)
if (-not (Test-PathEqual -First $SummaryPath -Second $expectedSummaryPath)) {
    throw "Native AOT preview accepts only the repository audit summary '$expectedSummaryPath'."
}
if (-not (Test-Path -LiteralPath $SummaryPath -PathType Leaf)) {
    throw "Native AOT audit summary was not found: '$SummaryPath'."
}

$summary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
if ([int]$summary.schemaVersion -ne $RequiredSummarySchemaVersion -or
    [int]$summary.auditProfileVersion -ne $RequiredAuditProfileVersion) {
    throw "Native AOT audit summary is stale. Expected schema/profile $RequiredSummarySchemaVersion/$RequiredAuditProfileVersion."
}
if (-not [bool]$summary.sourceStableDuringAudit) {
    throw "Native AOT audit source changed during publish. Run the audit again before previewing."
}
if ([string]$summary.configuration -cne "Release" -or
    [string]$summary.platform -cne "x64" -or
    [string]$summary.runtimeIdentifier -cne "win-x64") {
    throw "Native AOT preview requires the audited Release x64/win-x64 output."
}
if (-not (Test-PathEqual -First ([string]$summary.publishDirectory) -Second $expectedPublishDirectory)) {
    throw "Native AOT audit summary points at an unexpected publish directory."
}
if (-not (Test-Path -LiteralPath $expectedPublishDirectory -PathType Container)) {
    throw "Audited Native AOT publish directory was not found: '$expectedPublishDirectory'."
}

$exe = Join-Path $expectedPublishDirectory "DeskBox.exe"
$rustDll = Join-Path $expectedPublishDirectory "deskbox_native.dll"
foreach ($requiredFile in @($exe, $rustDll)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Audited Native AOT file was not found: '$requiredFile'."
    }
}

$exeImages = @($summary.peImages | Where-Object { [string]$_.file -ceq "DeskBox.exe" })
if ($exeImages.Count -ne 1) {
    throw "Native AOT audit summary must contain exactly one DeskBox.exe PE record."
}
$exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $exeSha256,
        [string]$exeImages[0].sha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DeskBox.exe no longer matches the audited SHA256. Run the AOT audit again."
}

if (-not [bool]$summary.rustNative.enabled -or
    -not [bool]$summary.rustNative.publishMatchesStaging -or
    [int]$summary.rustNative.abiVersion -ne $RequiredRustAbiVersion -or
    [int]$summary.rustNative.capabilities -ne $RequiredRustCapabilities -or
    @($summary.rustNative.requiredExports).Count -ne $RequiredRustExportCount) {
    throw "Native AOT audit summary does not contain the required Rust ABI/capability/export contract."
}
$rustSha256 = (Get-FileHash -LiteralPath $rustDll -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $rustSha256,
        [string]$summary.rustNative.publishSha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "deskbox_native.dll no longer matches the audited SHA256. Run the AOT audit again."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $worktreeName = Split-Path $repoRootPath -Leaf
    $safeWorktreeName = $worktreeName -replace '[^A-Za-z0-9._-]', '-'
    $pathHash = (Get-TextSha256 -Value $repoRootPath.ToUpperInvariant()).Substring(0, 8)
    $DataRoot = Join-Path $env:LOCALAPPDATA "DeskBox-AotPreview\$safeWorktreeName-$pathHash"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
if ((Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot)) {
    throw "Refusing to start Native AOT preview with the production data root or an overlapping path: '$DataRoot'."
}
if ((Test-PathEqualOrInside -Root $expectedPublishDirectory -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $expectedPublishDirectory)) {
    throw "Refusing to overlap the Native AOT preview data root with audited binaries: '$DataRoot'."
}

$productionStateBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$existingPreviewProcesses = @(Get-AotPreviewProcesses -ExecutablePath $exe)
if ($ExpectExistingInstance.IsPresent) {
    if ($existingPreviewProcesses.Count -ne 1) {
        throw "Existing-instance verification requires exactly one audited Native AOT preview process."
    }
}
elseif ($NoStop.IsPresent -and $existingPreviewProcesses.Count -gt 0) {
    throw "An audited Native AOT preview is already running. Use -ExpectExistingInstance with -NoStop to verify redirection."
}
elseif (-not $NoStop.IsPresent) {
    $existingPreviewProcesses |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    if ($existingPreviewProcesses.Count -gt 0) {
        Start-Sleep -Milliseconds 800
    }
}

$previousAotPreviewRoot = [Environment]::GetEnvironmentVariable(
    "DESKBOX_AOT_PREVIEW_DATA_ROOT",
    "Process")
$previousDevelopmentRoot = [Environment]::GetEnvironmentVariable(
    "DESKBOX_DEV_DATA_ROOT",
    "Process")
try {
    [Environment]::SetEnvironmentVariable(
        "DESKBOX_AOT_PREVIEW_DATA_ROOT",
        $DataRoot,
        "Process")
    [Environment]::SetEnvironmentVariable(
        "DESKBOX_DEV_DATA_ROOT",
        $null,
        "Process")
    $process = Start-Process `
        -FilePath $exe `
        -WorkingDirectory $expectedPublishDirectory `
        -WindowStyle Hidden `
        -PassThru
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DESKBOX_AOT_PREVIEW_DATA_ROOT",
        $previousAotPreviewRoot,
        "Process")
    [Environment]::SetEnvironmentVariable(
        "DESKBOX_DEV_DATA_ROOT",
        $previousDevelopmentRoot,
        "Process")
}

Start-Sleep -Seconds $StartupWaitSeconds
$previewProcessesAfterStart = @(Get-AotPreviewProcesses -ExecutablePath $exe)
$startedProcessStillRunning = @(
    $previewProcessesAfterStart |
        Where-Object { $_.ProcessId -eq $process.Id }
).Count -eq 1
$existingInstanceActivated = $false
$primaryProcessId = $process.Id

if ($ExpectExistingInstance.IsPresent) {
    if ($startedProcessStillRunning) {
        throw "The secondary Native AOT preview process must exit after redirecting activation."
    }

    $primaryProcessId = $existingPreviewProcesses[0].ProcessId
    if ($previewProcessesAfterStart.Count -ne 1 -or
        $previewProcessesAfterStart[0].ProcessId -ne $primaryProcessId) {
        throw "The primary Native AOT preview process did not survive existing-instance activation."
    }
    $existingInstanceActivated = $true
}
elseif ($AllowEarlyExit.IsPresent) {
    if ($previewProcessesAfterStart.Count -gt 1 -or
        ($previewProcessesAfterStart.Count -eq 1 -and
            -not $startedProcessStillRunning)) {
        throw "Native AOT preview early-exit mode observed an unexpected preview process."
    }
}
elseif (-not $startedProcessStillRunning -or $previewProcessesAfterStart.Count -ne 1) {
    throw "Native AOT preview did not remain alive after $StartupWaitSeconds seconds. Inspect '$DataRoot\DeskBox.log'."
}

New-Item -ItemType Directory -Path $previewEvidenceDirectory -Force | Out-Null
$session = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    summaryPath = $SummaryPath
    auditProfileVersion = [int]$summary.auditProfileVersion
    auditSummarySchemaVersion = [int]$summary.schemaVersion
    executablePath = $exe
    executableSha256 = $exeSha256
    rustNativePath = $rustDll
    rustNativeSha256 = $rustSha256
    previewDataRoot = $DataRoot
    previewLogPath = Join-Path $DataRoot "DeskBox.log"
        productionDataRoot = $productionDataRoot
        productionDataFingerprintAlgorithm = "path-upper-length-lastwriteutc-v1-ordinal"
        productionDataExistedBefore = $productionStateBefore.exists
    productionDataFingerprintBefore = $productionStateBefore.fingerprint
    productionDataFileCountBefore = $productionStateBefore.fileCount
    productionDataBytesBefore = $productionStateBefore.bytes
    startedProcessId = $process.Id
    primaryProcessId = $primaryProcessId
    running = $startedProcessStillRunning
    existingInstanceActivated = $existingInstanceActivated
}
$session | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $sessionPath -Encoding UTF8

[PSCustomObject]@{
    Exe = $exe
    ExeSha256 = $exeSha256
    RustNativeDll = $rustDll
    RustNativeSha256 = $rustSha256
    StartedProcessId = $process.Id
    PrimaryProcessId = $primaryProcessId
    Running = $startedProcessStillRunning
    ExistingInstanceActivated = $existingInstanceActivated
    DataRoot = $DataRoot
    LogPath = Join-Path $DataRoot "DeskBox.log"
    ProductionDataFingerprintBefore = $productionStateBefore.fingerprint
    SummaryPath = $SummaryPath
    SessionPath = $sessionPath
}
