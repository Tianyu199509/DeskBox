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
$companionExe = Join-Path $PublishDir "DeskBox.Settings.exe"

if (-not (Test-Path -LiteralPath $deskBoxExe -PathType Leaf)) {
    throw "NativeAOT DeskBox.exe not found at '$deskBoxExe'."
}

if (-not (Test-Path -LiteralPath $companionExe -PathType Leaf)) {
    throw "NativeAOT companion DeskBox.Settings.exe not found at '$companionExe'."
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

$deskBoxMachine = Assert-NativeAotExecutable -Path $deskBoxExe -MinBytes (5 * 1024 * 1024)
$companionMachine = Assert-NativeAotExecutable -Path $companionExe -MinBytes (5 * 1024 * 1024)
if ($deskBoxMachine -ne $companionMachine) {
    throw "PE machine mismatch between DeskBox.exe (0x$($deskBoxMachine.ToString('X4'))) and DeskBox.Settings.exe (0x$($companionMachine.ToString('X4')))."
}

$rpcPayload = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_validate_manifest","arguments":{"manifestJson":"{\"manifestVersion\":1,\"id\":\"custom.aot-smoke\",\"name\":\"AOT Smoke\",\"root\":{\"type\":\"Text\",\"text\":\"OK\"}}"}}}'
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

Write-Host "[AOT-SMOKE] DeskBox.exe --mcp JSON-RPC & DeskBox.Settings.exe companion verified."
