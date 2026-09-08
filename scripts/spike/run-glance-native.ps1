[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")][string]$Platform = "x64",
    [switch]$BuildOnly
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $repoRoot "scripts\rust-arm64-msvc-environment.ps1")
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$runRoot = Join-Path $repoRoot (".artifacts\glance-native\runs\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + "-" + $Platform)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$packageProject = Join-Path $repoRoot "spikes\glance-native\Package\Glance.NativePackage.csproj"
$hostProject = Join-Path $repoRoot "spikes\glance-native\Host\Glance.NativeHost.csproj"
$common = @("-c", "Release", "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid",
    "-p:PublishAot=true", "-p:SelfContained=true", "-p:WindowsAppSDKSelfContained=false",
    "-p:IlcUseEnvironmentalTools=true")
function Invoke-DotNet([string[]]$CommandArguments) {
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
try {
    foreach ($project in @($packageProject, $hostProject)) {
        Invoke-DotNet -CommandArguments @("restore", $project, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=false")
        Invoke-DotNet -CommandArguments @("restore", $project, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=true")
    }
    $hostOutput = Join-Path $runRoot "host"
    Invoke-DotNet -CommandArguments (@("publish", $hostProject, "--no-restore") + $common + @("-o", $hostOutput))
    $hostExe = Join-Path $hostOutput "DeskBox.Glance.NativeHost.exe"
    $originalHostHash = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash
    $originalLayout = Join-Path $repoRoot "src\DeskBox\Services\GlanceCalendarLayoutCalculator.cs"
    $variantLayout = Join-Path $runRoot "GlanceCalendarLayoutCalculator.v2.cs"
    $source = [IO.File]::ReadAllText($originalLayout)
    if (-not $source.Contains("CompactCalendarThreshold = 320")) { throw "Glance source threshold changed; update the probe deliberately." }
    [IO.File]::WriteAllText($variantLayout, $source.Replace("CompactCalendarThreshold = 320", "CompactCalendarThreshold = 360"))
    $results = @()
    foreach ($version in @(1, 2)) {
        $packageOutput = Join-Path $runRoot "package-v$version"
        $layout = if ($version -eq 1) { $originalLayout } else { $variantLayout }
        Invoke-DotNet -CommandArguments (@("publish", $packageProject, "--no-restore") + $common +
            @("-p:GlancePackageVersion=$version", "-p:GlanceLayoutSource=$layout", "-o", $packageOutput))
        $dll = Join-Path $packageOutput "DeskBox.Glance.NativePackage.dll"
        $record = [ordered]@{
            version = $version
            packageDll = $dll
            packageSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
            packageDllBytes = (Get-Item -LiteralPath $dll).Length
            hostSha256 = $originalHostHash
            runtimeExecuted = $false
        }
        if (-not $BuildOnly -and $Platform -eq "x64") {
            $evidence = Join-Path $runRoot "result-v$version"
            New-Item -ItemType Directory -Path $evidence -Force | Out-Null
            $process = Start-Process -FilePath $hostExe -WorkingDirectory $hostOutput -WindowStyle Hidden -PassThru -ArgumentList @(
                "--development-package", ('"{0}"' -f $packageOutput), ('"{0}"' -f $evidence))
            if (-not $process.WaitForExit(30000)) {
                Stop-Process -Id $process.Id
                throw "Glance probe timed out. Evidence: $evidence"
            }
            $resultFile = Join-Path $evidence "result.json"
            if (-not (Test-Path -LiteralPath $resultFile)) {
                throw "Glance probe did not produce a result. Evidence: $evidence; package: $packageOutput"
            }
            $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
            $expectedHeight = if ($version -eq 1) { 244 } else { 268 }
            if ($result.dynamicCodeSupported -or $result.packageVersion -ne $version -or
                $result.panelHeightFor340 -ne $expectedHeight -or $result.calendarActualHeight -ne $expectedHeight -or
                $result.heading -ne "Glance native package v$version" -or $process.ExitCode -ne 0) {
                throw "AOT/module version/business behavior assertion failed: $resultFile"
            }
            if ((Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash -ne $originalHostHash) {
                throw "Host changed while switching packages."
            }
            $record.runtimeExecuted = $true
            $record.result = $result
            $record.screenshot = Join-Path $evidence "view.png"
            if (-not (Test-Path -LiteralPath $record.screenshot)) { throw "Missing rendered view: $evidence" }
        }
        $results += [pscustomobject]$record
    }
    if ($results[0].packageSha256 -eq $results[1].packageSha256) { throw "The two native packages must differ." }
    $summary = [ordered]@{ platform = $Platform; runRoot = $runRoot; hostExe = $hostExe; results = $results }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot "summary.json") -Encoding UTF8
    $summary | ConvertTo-Json -Depth 8
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}
