[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("x64", "ARM64")]
    [string]$Platform,

    [string]$SourcePublishDirectory = "",

    [string]$OutputDirectory = "",

    [string]$SignThumbprint = "auto",

    [string[]]$TimestampUrls = @(
        "http://time.certum.pl",
        "http://timestamp.digicert.com",
        "http://timestamp.sectigo.com"
    )
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$platformSegment = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }

if ([string]::IsNullOrWhiteSpace($SourcePublishDirectory)) {
    $SourcePublishDirectory = Join-Path $artifactRoot "aot-retail\$runtimeIdentifier\publish"
}
$SourcePublishDirectory = [System.IO.Path]::GetFullPath($SourcePublishDirectory)
if (-not (Test-Path -LiteralPath $SourcePublishDirectory -PathType Container)) {
    throw "Source publish directory does not exist: '$SourcePublishDirectory'. Run publish-aot-retail.ps1 -Platform $Platform first."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot "signed-release-$runtimeIdentifier"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must reside inside '$artifactRoot': '$OutputDirectory'."
}

$signHelper = Join-Path $PSScriptRoot "Sign-DeskBoxFiles.ps1"
$resolvedCert = & $signHelper -Thumbprint $SignThumbprint -ResolveOnly
Write-Host "Using code-signing certificate: $($resolvedCert.Subject) ($($resolvedCert.Thumbprint))" -ForegroundColor Cyan

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
$stagedPublishDir = Join-Path $OutputDirectory "publish"
$installerOutDir = Join-Path $OutputDirectory "direct-installer"
New-Item -ItemType Directory -Path $stagedPublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutDir -Force | Out-Null

Copy-Item -Path (Join-Path $SourcePublishDirectory "*") -Destination $stagedPublishDir -Recurse -Force

$firstPartyPeFiles = @(
    "DeskBox.exe",
    "DeskBox.Updater.exe",
    "DeskBox.ThumbnailProxy.exe",
    "deskbox_native.dll",
    "deskbox_qjs.dll"
) | ForEach-Object { Join-Path $stagedPublishDir $_ }

Write-Host "Signing first-party Native AOT & native PE payloads for $Platform..." -ForegroundColor Cyan
& $signHelper -Thumbprint $resolvedCert.Thumbprint -TimestampUrls $TimestampUrls -FilePaths $firstPartyPeFiles | Out-Null

$innoCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
if ($innoCandidates.Count -eq 0) {
    throw "ISCC.exe (Inno Setup 6) was not found."
}
$iscc = [System.IO.Path]::GetFullPath($innoCandidates[0])

$issPath = if ($Platform -eq "ARM64") {
    Join-Path $repoRoot "installer\DeskBox.arm64.iss"
} else {
    Join-Path $repoRoot "installer\DeskBox.iss"
}
$issText = Get-Content -LiteralPath $issPath -Raw
$baseMatch = [regex]::Match($issText, '(?m)^\s*#define\s+MyAppOutputBaseName\s+"([^"]+)"\s*$')
$verMatch  = [regex]::Match($issText, '(?m)^\s*#define\s+MyAppVersion\s+"([^"]+)"\s*$')
$outputBaseName = "{0}_{1}_{2}" -f $baseMatch.Groups[1].Value, $verMatch.Groups[1].Value, $platformSegment

$powershellExe = (Get-Process -Id $PID).Path
# Use Inno Setup's $q quote macro so ISCC's argv parser never splits paths containing spaces.
$signCommand = "`$q$powershellExe`$q -NoProfile -ExecutionPolicy Bypass -File `$q$signHelper`$q -Thumbprint $($resolvedCert.Thumbprint) -FilePath `$f"

$innoArgs = @(
    "/Qp",
    "/DDeskBoxNativeAot=1",
    "/DDeskBoxBundledRuntime=1",
    "/DDeskBoxUseSignTool=1",
    "/DMyAppReleaseDir=$stagedPublishDir",
    "/F$outputBaseName",
    "/O$installerOutDir",
    "/Sdeskboxsign=$signCommand",
    $issPath
)

Write-Host "Compiling and signing Inno Setup installer '$outputBaseName.exe'..." -ForegroundColor Cyan
& $iscc @innoArgs | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $installerOutDir "$outputBaseName.exe"
$installerSig = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($installerSig.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $installerSig.TimeStamperCertificate) {
    throw "Signed installer verification failed for '$installerPath': status=$($installerSig.Status)."
}

$sha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$sidecarPath = "$installerPath.sha256"
[System.IO.File]::WriteAllText($sidecarPath, $sha256)

[pscustomobject]@{
    Platform      = $Platform
    Installer     = $installerPath
    Bytes         = (Get-Item -LiteralPath $installerPath).Length
    Sha256        = $sha256
    Sidecar       = $sidecarPath
    Signature     = $installerSig.Status.ToString()
    Signer        = $installerSig.SignerCertificate.Subject
    Timestamper   = $installerSig.TimeStamperCertificate.Subject
}
