[CmdletBinding()]
param(
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot ".artifacts\aot-preview\win-x64\publish"
}

$deskBoxExe = Join-Path $PublishDir "DeskBox.exe"
# The settings host is the same image relaunched as DeskBox.exe --settings;
# a separate settings executable would statically duplicate the whole app.
$legacyCompanionExe = Join-Path $PublishDir "DeskBox.Settings.exe"

if (-not (Test-Path -LiteralPath $deskBoxExe -PathType Leaf)) {
    throw "NativeAOT DeskBox.exe not found at '$deskBoxExe'."
}

if (Test-Path -LiteralPath $legacyCompanionExe -PathType Leaf) {
    throw "Unexpected legacy companion '$legacyCompanionExe': the settings host must be DeskBox.exe --settings."
}

function Assert-NativeAotExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$MinBytes
    )

    $info = Get-Item -LiteralPath $Path
    if ($info.Length -lt $MinBytes) {
        throw "'$Path' is unexpectedly small ($($info.Length) bytes < $MinBytes); expected a NativeAOT binary, not an apphost stub."
    }

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a valid PE image (missing MZ magic)."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' does not contain a PE signature."
        }

        $machine = $reader.ReadUInt16()
        if ($machine -notin @(0x8664, 0xAA64)) {
            throw "Unexpected PE machine 0x$($machine.ToString('X4')) for '$Path'."
        }

        $optHeaderOffset = $peOffset + 24
        $stream.Position = $optHeaderOffset
        $peMagic = $reader.ReadUInt16()
        if ($peMagic -ne 0x020B) {
            throw "'$Path' is not a 64-bit PE32+ image (magic 0x$($peMagic.ToString('X4')))."
        }

        # DataDirectory[14] (COM/CLR Runtime Header) starts at OptionalHeader + 112 + 14*8 = OptionalHeader + 224.
        $stream.Position = $optHeaderOffset + 224
        $clrRva = $reader.ReadUInt32()
        $clrSize = $reader.ReadUInt32()
        if ($clrRva -ne 0 -or $clrSize -ne 0) {
            throw "'$Path' contains a managed CLR header (RVA=0x$($clrRva.ToString('X8')), Size=$clrSize); expected pure NativeAOT."
        }

        return $machine
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$null = Assert-NativeAotExecutable -Path $deskBoxExe -MinBytes (5 * 1024 * 1024)

$rpcPayload = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_validate_manifest","arguments":{"manifestJson":"{\"schemaVersion\":1,\"id\":\"custom.aot-smoke\",\"name\":\"AOT Smoke\",\"root\":\"r\",\"elements\":{\"r\":{\"type\":\"Text\",\"text\":\"OK\"}}}"}}}'
) -join "`n"
$rpcPayload += "`n"

$psi = [System.Diagnostics.ProcessStartInfo]::new($deskBoxExe)
$psi.ArgumentList.Add("--mcp")
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)

$proc = [System.Diagnostics.Process]::Start($psi)
try {
    # Drain stdout/stderr asynchronously before WaitForExit so >4 KB MCP tool catalogs cannot deadlock the pipe.
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $proc.StandardInput.Write($rpcPayload)
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(15000)) {
        $proc.Kill($true)
        throw "DeskBox.exe --mcp timed out after 15s."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($proc.ExitCode -ne 0) {
        throw "DeskBox.exe --mcp exited with code $($proc.ExitCode): $stderr"
    }
    $lines = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -lt 3) {
        throw "Expected at least 3 JSON-RPC responses from --mcp, got $($lines.Count)."
    }

    $toolsResp = $lines[1] | ConvertFrom-Json
    $toolNames = @($toolsResp.result.tools | ForEach-Object { $_.name })
    if ($toolNames -notcontains "deskbox_validate_manifest") {
        throw "Missing deskbox_validate_manifest in --mcp tools/list."
    }

    $validateResp = $lines[2] | ConvertFrom-Json
    if ($validateResp.result.isError) {
        throw "deskbox_validate_manifest failed under NativeAOT."
    }
}
finally {
    $proc.Dispose()
}

if ([Environment]::UserInteractive) {
    $smokeId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $smokeDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) "DeskBox-AotSettingsSmoke-$smokeId"
    $smokeRecoveryRoot = "$smokeDataRoot-Recovery"
    New-Item -ItemType Directory -Path $smokeDataRoot -Force | Out-Null

    $mainPsi = [System.Diagnostics.ProcessStartInfo]::new($deskBoxExe)
    $mainPsi.UseShellExecute = $false
    $mainPsi.WorkingDirectory = $PublishDir
    $mainPsi.Environment["DESKBOX_AOT_PREVIEW_DATA_ROOT"] = $smokeDataRoot

    $mainProc = $null
    try {
        $mainProc = [System.Diagnostics.Process]::Start($mainPsi)
        $mainLogPath = Join-Path $smokeDataRoot "DeskBox.log"
        $settingsLogPath = Join-Path $smokeDataRoot "DeskBox.Settings.log"

        $launchDeadline = [DateTime]::UtcNow.AddSeconds(20)
        $mainReady = $false
        while ([DateTime]::UtcNow -lt $launchDeadline) {
            if ($mainProc.HasExited) {
                $mainLogTail = if (Test-Path -LiteralPath $mainLogPath -PathType Leaf) {
                    (Get-Content -LiteralPath $mainLogPath -Tail 40) -join "`n"
                }
                else {
                    "<no DeskBox.log>"
                }
                throw "Primary NativeAOT DeskBox.exe exited prematurely (code $($mainProc.ExitCode)):`n$mainLogTail"
            }

            if (Test-Path -LiteralPath $mainLogPath -PathType Leaf) {
                $mainLogContent = Get-Content -LiteralPath $mainLogPath -Raw -ErrorAction SilentlyContinue
                if ($null -ne $mainLogContent -and $mainLogContent.Contains("OnLaunched completed successfully")) {
                    $mainReady = $true
                    break
                }
            }
            Start-Sleep -Milliseconds 200
        }

        if (-not $mainReady) {
            throw "Timed out waiting for primary NativeAOT DeskBox.exe OnLaunched completion."
        }

        $openPsi = [System.Diagnostics.ProcessStartInfo]::new($deskBoxExe)
        $openPsi.ArgumentList.Add("--open-settings")
        $openPsi.UseShellExecute = $false
        $openPsi.WorkingDirectory = $PublishDir
        $openPsi.Environment["DESKBOX_AOT_PREVIEW_DATA_ROOT"] = $smokeDataRoot
        $openProc = [System.Diagnostics.Process]::Start($openPsi)
        try {
            if (-not $openProc.WaitForExit(15000)) {
                $openProc.Kill($true)
                throw "DeskBox.exe --open-settings timed out after 15s."
            }
        }
        finally {
            $openProc.Dispose()
        }

        $settingsDeadline = [DateTime]::UtcNow.AddSeconds(20)
        $settingsShown = $false
        while ([DateTime]::UtcNow -lt $settingsDeadline) {
            if (Test-Path -LiteralPath $settingsLogPath -PathType Leaf) {
                $settingsLogContent = Get-Content -LiteralPath $settingsLogPath -Raw -ErrorAction SilentlyContinue
                if ($null -ne $settingsLogContent) {
                    if ($settingsLogContent.Contains("[SettingsHost] Boot failed") -or
                        $settingsLogContent.Contains("[SettingsHost] Unhandled exception") -or
                        $settingsLogContent.Contains("[SettingsHost] IPC connect failed")) {
                        throw "NativeAOT Settings host failed during boot:`n$settingsLogContent"
                    }
                    if ($settingsLogContent.Contains("[SettingsHost] Window shown")) {
                        $settingsShown = $true
                        break
                    }
                }
            }
            Start-Sleep -Milliseconds 200
        }

        if (-not $settingsShown) {
            $settingsTail = if (Test-Path -LiteralPath $settingsLogPath -PathType Leaf) {
                Get-Content -LiteralPath $settingsLogPath -Raw
            }
            else {
                "<no DeskBox.Settings.log created>"
            }
            throw "Timed out waiting for '[SettingsHost] Window shown' in NativeAOT settings host:`n$settingsTail"
        }
    }
    finally {
        $resolvedTargetExe = [System.IO.Path]::GetFullPath($deskBoxExe)
        foreach ($running in @(Get-Process -Name "DeskBox" -ErrorAction SilentlyContinue)) {
            try {
                if ($null -ne $running.Path -and
                    [string]::Equals(
                        [System.IO.Path]::GetFullPath($running.Path),
                        $resolvedTargetExe,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $running.Id -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
            }
        }
        if ($null -ne $mainProc) {
            try {
                if (-not $mainProc.HasExited) {
                    $mainProc.Kill($true)
                }
                [void]$mainProc.WaitForExit(5000)
            }
            catch {
            }
            $mainProc.Dispose()
        }
        Start-Sleep -Milliseconds 300
        foreach ($cleanupDir in @($smokeDataRoot, $smokeRecoveryRoot)) {
            if (Test-Path -LiteralPath $cleanupDir) {
                Remove-Item -LiteralPath $cleanupDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

Write-Host "[AOT-SMOKE] DeskBox.exe --mcp JSON-RPC and live --settings host window boot verified."
