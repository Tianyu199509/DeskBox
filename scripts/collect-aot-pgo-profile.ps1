[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [int]$WarmupSeconds = 14,

    [string]$OutputMibcPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) {
        throw "collect-aot-pgo-profile.ps1 requires PowerShell 7+ (pwsh) for System.Reflection.Metadata.PEReader."
    }
    $forwardArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Platform", $Platform, "-WarmupSeconds", $WarmupSeconds)
    if (-not [string]::IsNullOrWhiteSpace($OutputMibcPath)) {
        $forwardArgs += @("-OutputMibcPath", $OutputMibcPath)
    }
    & $pwsh.Source @forwardArgs | Write-Output
    return
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
if ([string]::IsNullOrWhiteSpace($OutputMibcPath)) {
    $OutputMibcPath = Join-Path $repoRoot "src\DeskBox\Profiles\DeskBox.Startup.mibc"
}

if ($null -eq (Get-Command cargo -ErrorAction SilentlyContinue)) {
    $userCargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
    if (Test-Path -LiteralPath (Join-Path $userCargoBin "cargo.exe") -PathType Leaf) {
        $env:PATH = "$userCargoBin;$env:PATH"
    }
}

$dotnetTrace = Get-Command dotnet-trace -ErrorAction SilentlyContinue
$dotnetTraceExe = if ($null -ne $dotnetTrace) {
    $dotnetTrace.Source
}
else {
    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-trace.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $candidate
    }
    else {
        throw "dotnet-trace was not found. Install it with: dotnet tool install --global dotnet-trace"
    }
}

$dotnetPgo = Get-Command dotnet-pgo -ErrorAction SilentlyContinue
$dotnetPgoExe = if ($null -ne $dotnetPgo) {
    $dotnetPgo.Source
}
else {
    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-pgo.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $candidate
    }
    else {
        throw "dotnet-pgo was not found. Install it with: dotnet tool install --global dotnet-pgo --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json --prerelease"
    }
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("deskbox-pgo-" + [Guid]::NewGuid().ToString("N"))
$buildArtifactsDir = Join-Path $workRoot "build-artifacts"
$buildOut = Join-Path $workRoot "app"
$previewDataRoot = Join-Path $workRoot "data-root"
$previewDataDir = Join-Path $previewDataRoot "data"
$previewFilesDir = Join-Path $workRoot "sample-files"
$tracePath = Join-Path $workRoot "deskbox-startup.nettrace"
New-Item -ItemType Directory -Path $buildOut -Force | Out-Null
New-Item -ItemType Directory -Path $previewDataDir -Force | Out-Null
New-Item -ItemType Directory -Path $previewFilesDir -Force | Out-Null

try {
    $project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
    $updaterProject = Join-Path $repoRoot "src\DeskBox.Updater\DeskBox.Updater.csproj"
    foreach ($restoreProject in @($project, $updaterProject)) {
        & dotnet restore $restoreProject --locked-mode -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:SelfContained=true --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restore '$restoreProject' for PGO instrumentation build."
        }
    }

    & dotnet build $project -c Release --no-restore -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:SelfContained=true -p:DeskBoxPgoInstrumentation=true -p:EventSourceSupport=true --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build Release JIT image with DeskBoxPgoInstrumentation=true for PGO trace collection."
    }

    $builtDir = Join-Path $repoRoot "src\DeskBox\bin\$Platform\Release\net10.0-windows10.0.22621.0\$rid"
    if (-not (Test-Path -LiteralPath $builtDir -PathType Container)) {
        throw "Expected build output directory not found: '$builtDir'."
    }
    Copy-Item -Path (Join-Path $builtDir "*") -Destination $buildOut -Recurse -Force

    $exePath = Join-Path $buildOut "DeskBox.exe"
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Built executable not found: '$exePath'."
    }

    $seedProc = Start-Process -FilePath $exePath -ArgumentList @("--pgo-seed-data", $previewDataRoot, $previewFilesDir) -WorkingDirectory $buildOut -Wait -PassThru
    if ($seedProc.ExitCode -ne 0) {
        $seedErrFile = Join-Path $previewDataRoot "pgo-seed-error.txt"
        $seedErrText = if (Test-Path -LiteralPath $seedErrFile -PathType Leaf) { Get-Content -LiteralPath $seedErrFile -Raw } else { "no error log written" }
        throw "DeskBox --pgo-seed-data failed with exit code $($seedProc.ExitCode): $seedErrText"
    }

    foreach ($requiredSeedFile in @(
            (Join-Path $previewDataDir "settings.json"),
            (Join-Path $previewDataDir "widget-layout.json"),
            (Join-Path $previewDataDir "custom-widgets\pgo-custom\manifest.json"))) {
        if (-not (Test-Path -LiteralPath $requiredSeedFile -PathType Leaf)) {
            throw "Expected PGO seed file was not created: '$requiredSeedFile'."
        }
    }

    $envBackup = @{
        DESKBOX_AOT_PREVIEW_DATA_ROOT = [Environment]::GetEnvironmentVariable("DESKBOX_AOT_PREVIEW_DATA_ROOT", "Process")
        DOTNET_TieredPGO = [Environment]::GetEnvironmentVariable("DOTNET_TieredPGO", "Process")
        DOTNET_TC_QuickJitForLoops = [Environment]::GetEnvironmentVariable("DOTNET_TC_QuickJitForLoops", "Process")
        DOTNET_ReadyToRun = [Environment]::GetEnvironmentVariable("DOTNET_ReadyToRun", "Process")
    }
    try {
        [Environment]::SetEnvironmentVariable("DESKBOX_AOT_PREVIEW_DATA_ROOT", $previewDataRoot, "Process")
        [Environment]::SetEnvironmentVariable("DOTNET_TieredPGO", "1", "Process")
        [Environment]::SetEnvironmentVariable("DOTNET_TC_QuickJitForLoops", "1", "Process")
        [Environment]::SetEnvironmentVariable("DOTNET_ReadyToRun", "0", "Process")
        $duration = [TimeSpan]::FromSeconds([Math]::Max(8, $WarmupSeconds)).ToString("dd\:hh\:mm\:ss")
        & $dotnetTraceExe collect --providers "Microsoft-Windows-DotNETRuntime:0x1E000080018:5" --duration $duration -o $tracePath -- $exePath
    }
    finally {
        foreach ($kv in $envBackup.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, "Process")
        }
        Get-CimInstance Win32_Process -Filter "Name='DeskBox.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -eq $exePath } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }

    if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) {
        throw "Expected PGO trace was not produced at '$tracePath'."
    }

    $profileDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputMibcPath))
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null

    $refs = @(
        Get-ChildItem -LiteralPath $buildOut -Filter "*.dll" -File |
            Where-Object {
                $s = [System.IO.File]::OpenRead($_.FullName)
                $pe = [System.Reflection.PortableExecutable.PEReader]::new($s)
                try { $pe.HasMetadata } catch { $false } finally { $pe.Dispose(); $s.Dispose() }
            } |
            ForEach-Object { "-r"; $_.FullName }
    )
    & $dotnetPgoExe create-mibc -t $tracePath -o $OutputMibcPath --compressed @refs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet-pgo create-mibc failed."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($OutputMibcPath)
    try {
        $entry = $zip.Entries | Where-Object { $_.Length -gt 200 } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Generated MIBC '$OutputMibcPath' contains no metadata payload."
        }

        $ms = [System.IO.MemoryStream]::new()
        $entryStream = $entry.Open()
        try {
            $entryStream.CopyTo($ms)
        }
        finally {
            $entryStream.Dispose()
        }

        $ms.Position = 0
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($ms)
        try {
            $mr = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            $typeNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            $typeByHandle = @{}
            foreach ($trh in $mr.TypeReferences) {
                $tr = $mr.GetTypeReference($trh)
                $fullName = "$($mr.GetString($tr.Namespace)).$($mr.GetString($tr.Name))"
                [void]$typeNames.Add($fullName)
                $typeByHandle[$trh] = $fullName
            }

            foreach ($mrh in $mr.MemberReferences) {
                $memberRef = $mr.GetMemberReference($mrh)
                $memberName = $mr.GetString($memberRef.Name)
                if ($memberName -eq ".ctor" -and $memberRef.Parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) {
                    $parentType = $typeByHandle[[System.Reflection.Metadata.TypeReferenceHandle]$memberRef.Parent]
                    if ($parentType -eq "DeskBox.Views.OnboardingWindow") {
                        throw "Generated MIBC traced OnboardingWindow..ctor instead of steady-state desktop startup; check PGO seed schema."
                    }
                }
            }

            $requiredHotTypes = @(
                "DeskBox.Controls.WidgetContents.FileSurfaceContent",
                "DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer",
                "DeskBox.Controls.WidgetContents.TodoWidgetContent",
                "DeskBox.Controls.WidgetContents.WeatherWidgetContent"
            )
            $missingHotTypes = @($requiredHotTypes | Where-Object { -not $typeNames.Contains($_) })
            if ($missingHotTypes.Count -gt 0) {
                throw "Generated MIBC is missing expected desktop widget types: $($missingHotTypes -join ', ')."
            }
        }
        finally {
            $peReader.Dispose()
            $ms.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }

    [pscustomobject]@{
        MibcPath = [System.IO.Path]::GetFullPath($OutputMibcPath)
        Bytes = (Get-Item -LiteralPath $OutputMibcPath).Length
        Platform = $Platform
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
