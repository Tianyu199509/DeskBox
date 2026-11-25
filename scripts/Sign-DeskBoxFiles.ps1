[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [Alias("FilePath")]
    [string[]]$FilePaths = @(),

    [string]$Thumbprint = "auto",

    [string]$ExpectedSubjectContains = "",

    [string[]]$TimestampUrls = @(
        "http://time.certum.pl",
        "http://timestamp.digicert.com",
        "http://timestamp.sectigo.com"
    ),

    [string]$Description = "DeskBox",

    [string]$DescriptionUrl = "https://deskbox.fun",

    [switch]$AllowSelfSignedForTesting,

    [switch]$ResolveOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-SignToolPath {
    $hostSubdir = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
        [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $sdkCandidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "$hostSubdir\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($null -ne $sdkCandidate) {
            return [System.IO.Path]::GetFullPath($sdkCandidate)
        }
    }

    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    throw "signtool.exe was not found under Windows Kits 10 or on PATH."
}

function Resolve-CodeSigningCertificate {
    param(
        [string]$RequestedThumbprint,
        [string]$SubjectFilter,
        [bool]$AllowSelfSigned
    )

    $now = Get-Date
    $allCodeSigning = @(
        Get-ChildItem -Path "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
            Where-Object {
                $_.HasPrivateKey -and
                $_.NotBefore -le $now -and
                $_.NotAfter -gt $now -and
                ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" })
            }
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedThumbprint) -and
        $RequestedThumbprint.Trim() -ne "auto") {
        $normalized = ($RequestedThumbprint -replace '\s+', '').ToUpperInvariant()
        $exact = @($allCodeSigning | Where-Object { $_.Thumbprint.ToUpperInvariant() -eq $normalized })
        if ($exact.Count -eq 0) {
            throw (
                "Code-signing certificate '$normalized' was not found in Cert:\CurrentUser\My (or is expired / missing private key). " +
                "Make sure SimplySign Desktop is running and connected."
            )
        }

        $cert = $exact[0]
        if (-not $AllowSelfSigned -and $cert.Subject -eq $cert.Issuer) {
            throw "Refusing to sign release binaries with self-signed certificate '$($cert.Subject)' ($($cert.Thumbprint)). Pass -AllowSelfSignedForTesting only in local tests."
        }
        return $cert
    }

    $candidates = @(
        $allCodeSigning | Where-Object {
            ($AllowSelfSigned -or $_.Subject -ne $_.Issuer) -and
            ([string]::IsNullOrWhiteSpace($SubjectFilter) -or $_.Subject -like "*$SubjectFilter*")
        } | Sort-Object `
            @{ Expression = { if ($_.Issuer -match 'Certum') { 0 } else { 1 } } }, `
            @{ Expression = { $_.NotAfter }; Descending = $true }
    )

    if ($candidates.Count -eq 0) {
        throw (
            "No publicly issued code-signing certificate was found in Cert:\CurrentUser\My. " +
            "Log in to Certum SimplySign Desktop so the virtual smartcard is mounted, then retry."
        )
    }

    return $candidates[0]
}

$signTool = Get-SignToolPath
$certificate = Resolve-CodeSigningCertificate `
    -RequestedThumbprint $Thumbprint `
    -SubjectFilter $ExpectedSubjectContains `
    -AllowSelfSigned ([bool]$AllowSelfSignedForTesting)

if ($ResolveOnly) {
    [pscustomobject]@{
        SignTool   = $signTool
        Thumbprint = $certificate.Thumbprint
        Subject    = $certificate.Subject
        Issuer     = $certificate.Issuer
        NotAfter   = $certificate.NotAfter.ToString("O")
    }
    return
}

if ($FilePaths.Count -eq 0) {
    throw "Specify at least one file via -FilePaths / -FilePath, or pass -ResolveOnly."
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($rawPath in $FilePaths) {
    $resolvedPath = [System.IO.Path]::GetFullPath($rawPath.Trim('"'))
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "File to sign does not exist: '$resolvedPath'."
    }

    $signed = $false
    $lastOutput = ""
    if ($AllowSelfSignedForTesting -and $TimestampUrls.Count -eq 0) {
        $args = @(
            "sign",
            "/fd", "SHA256",
            "/d", $Description,
            "/du", $DescriptionUrl,
            "/sha1", $certificate.Thumbprint,
            $resolvedPath
        )
        $lastOutput = (& $signTool @args 2>&1) | Out-String
        if ($LASTEXITCODE -eq 0) {
            $signed = $true
        }
    }
    else {
        foreach ($tsUrl in $TimestampUrls) {
            if ([string]::IsNullOrWhiteSpace($tsUrl)) { continue }
            $args = @(
                "sign",
                "/fd", "SHA256",
                "/tr", $tsUrl,
                "/td", "SHA256",
                "/d", $Description,
                "/du", $DescriptionUrl,
                "/sha1", $certificate.Thumbprint,
                $resolvedPath
            )
            $lastOutput = (& $signTool @args 2>&1) | Out-String
            if ($LASTEXITCODE -eq 0) {
                $signed = $true
                break
            }
            Write-Warning "Timestamping '$($resolvedPath)' via '$tsUrl' failed; trying next server..."
        }
    }

    if (-not $signed) {
        throw "signtool.exe failed for '$resolvedPath':`n$lastOutput"
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $resolvedPath
    if (-not $AllowSelfSignedForTesting) {
        if ($sig.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Signature verification failed for '$resolvedPath': status=$($sig.Status) ($($sig.StatusMessage))."
        }
        if ($null -eq $sig.TimeStamperCertificate) {
            throw "Signed file '$resolvedPath' is missing an RFC 3161 timestamp."
        }
    }
    elseif ($sig.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Test signature was not embedded in '$resolvedPath'."
    }

    Write-Host "Signed: $([System.IO.Path]::GetFileName($resolvedPath)) [$($certificate.Subject)]" -ForegroundColor Green
    $results.Add([pscustomobject]@{
        Path             = $resolvedPath
        Status           = $sig.Status.ToString()
        SignerSubject    = $sig.SignerCertificate.Subject
        Thumbprint       = $sig.SignerCertificate.Thumbprint
        TimestampSubject = if ($null -ne $sig.TimeStamperCertificate) { $sig.TimeStamperCertificate.Subject } else { $null }
    })
}

return $results
