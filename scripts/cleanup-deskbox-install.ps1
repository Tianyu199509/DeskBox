[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot,

    [Parameter(Mandatory)]
    [string]$CurrentManifestPath,

    [Parameter(Mandatory)]
    [string]$LegacyManifestPath,

    [string]$PreviousManifestPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-SafeInstallRoot {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "The DeskBox install root does not exist: '$resolved'."
    }

    $normalized = $resolved.TrimEnd('\', '/')
    $volumeRoot = [System.IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/')
    if ([string]::Equals($normalized, $volumeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a volume root: '$resolved'."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $normalized "DeskBox.exe") -PathType Leaf)) {
        throw "The selected install root does not contain DeskBox.exe: '$normalized'."
    }

    return $normalized
}

function Normalize-ManifestEntry {
    param([AllowEmptyString()][string]$Value)

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
        return $null
    }

    $normalized = $trimmed.Replace('/', '\')
    if ([System.IO.Path]::IsPathRooted($normalized) -or $normalized.IndexOf(':') -ge 0) {
        return $null
    }

    $segments = @(
        $normalized.Split('\', [System.StringSplitOptions]::RemoveEmptyEntries)
    )
    if ($segments.Count -eq 0 -or
        @($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        return $null
    }

    return ($segments -join '\')
}

function Read-ManifestEntries {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $entries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        $entry = Normalize-ManifestEntry -Value ([string]$line)
        if ($null -ne $entry) {
            [void]$entries.Add($entry)
        }
    }

    return @($entries | Sort-Object)
}

function Resolve-InstallEntryPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RelativePath
    )

    $entry = Normalize-ManifestEntry -Value $RelativePath
    if ($null -eq $entry) {
        return $null
    }

    $rootWithSeparator = $Root + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $entry))
    if (-not $candidate.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return [pscustomobject]@{
        RelativePath = $entry
        FullPath = $candidate
    }
}

function Remove-StaleInstallEntries {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Candidates,
        [Parameter(Mandatory)][System.Collections.Generic.HashSet[string]]$CurrentEntries
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in $Candidates) {
        $resolved = Resolve-InstallEntryPath -Root $Root -RelativePath $candidate
        if ($null -eq $resolved) {
            Write-Warning "Ignoring unsafe DeskBox install manifest entry '$candidate'."
            continue
        }

        if ($CurrentEntries.Contains($resolved.RelativePath)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $resolved.FullPath -PathType Leaf)) {
            continue
        }

        try {
            Remove-Item -LiteralPath $resolved.FullPath -Force -ErrorAction Stop
            Write-Output "Removed stale DeskBox install file: $($resolved.RelativePath)"
        }
        catch {
            $failures.Add("$($resolved.RelativePath): $($_.Exception.Message)")
        }
    }

    if ($failures.Count -gt 0) {
        throw "DeskBox install cleanup could not remove $($failures.Count) stale file(s): $($failures -join '; ')"
    }
}

$root = Resolve-SafeInstallRoot -Path $InstallRoot
$currentManifest = [System.IO.Path]::GetFullPath($CurrentManifestPath)
if (-not (Test-Path -LiteralPath $currentManifest -PathType Leaf)) {
    throw "The current DeskBox install manifest is missing: '$currentManifest'."
}

$currentEntries = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in Read-ManifestEntries -Path $currentManifest) {
    $resolved = Resolve-InstallEntryPath -Root $root -RelativePath $entry
    if ($null -ne $resolved) {
        [void]$currentEntries.Add($resolved.RelativePath)
    }
}

foreach ($requiredEntry in @('DeskBox.exe', 'DeskBox.InstallManifest.txt')) {
    if (-not $currentEntries.Contains($requiredEntry)) {
        throw "The current DeskBox install manifest is invalid because '$requiredEntry' is missing."
    }
}

$legacyManifest = [System.IO.Path]::GetFullPath($LegacyManifestPath)
if (-not (Test-Path -LiteralPath $legacyManifest -PathType Leaf)) {
    throw "The DeskBox legacy install manifest is missing: '$legacyManifest'."
}

$hasPreviousManifest = -not [string]::IsNullOrWhiteSpace($PreviousManifestPath) -and
    (Test-Path -LiteralPath $PreviousManifestPath -PathType Leaf)
if ($hasPreviousManifest) {
    $previousEntries = Read-ManifestEntries -Path $PreviousManifestPath
    Remove-StaleInstallEntries -Root $root -Candidates $previousEntries -CurrentEntries $currentEntries
    Write-Output "DeskBox install cleanup completed using the previous manifest."
}
else {
    $legacyEntries = Read-ManifestEntries -Path $legacyManifest
    Remove-StaleInstallEntries -Root $root -Candidates $legacyEntries -CurrentEntries $currentEntries
    Write-Output "DeskBox legacy bundled-runtime cleanup completed using the exact compatibility manifest."
}
