# Fast iteration loop for the real-Glance slice: republish only package v3 and
# run only the --real-package scenario against the most recent host build.
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $repoRoot "scripts\rust-arm64-msvc-environment.ps1")
$toolchain = Get-DeskBoxMsvcEnvironment -Platform x64
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    $latestRun = Get-ChildItem -LiteralPath (Join-Path $repoRoot ".artifacts\glance-native\runs") -Directory |
        Sort-Object Name -Descending | Select-Object -First 1
    $hostOutput = Join-Path $latestRun.FullName "host"
    $hostExe = Join-Path $hostOutput "DeskBox.Glance.NativeHost.exe"
    if (-not (Test-Path -LiteralPath $hostExe)) { throw "no host build in $($latestRun.FullName)" }
    $packageOutput = Join-Path $latestRun.FullName "package-v3"
    $project = Join-Path $repoRoot "spikes\glance-native\Package\Glance.NativePackage.csproj"
    & dotnet publish $project --no-restore -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:PublishAot=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
        -p:IlcUseEnvironmentalTools=true -p:GlancePackageVersion=3 -o $packageOutput
    if ($LASTEXITCODE -ne 0) { throw "package publish failed" }
    Remove-Item -LiteralPath (Join-Path $packageOutput "real-summary.json") -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $packageOutput "activation-error.txt") -ErrorAction SilentlyContinue
    $evidence = Join-Path $latestRun.FullName "result-real-fast"
    Remove-Item -LiteralPath $evidence -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $process = Start-Process -FilePath $hostExe -WorkingDirectory $hostOutput -WindowStyle Hidden -PassThru -ArgumentList @(
        "--real-package", ('"{0}"' -f $packageOutput), ('"{0}"' -f $evidence))
    if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id; throw "probe timed out" }
    Write-Output ("exit=" + $process.ExitCode)
    if (Test-Path -LiteralPath (Join-Path $evidence "result.json")) {
        Get-Content -LiteralPath (Join-Path $evidence "result.json") -Raw
    }
    if (Test-Path -LiteralPath (Join-Path $packageOutput "activation-error.txt")) {
        Write-Output "--- activation error ---"
        Get-Content -LiteralPath (Join-Path $packageOutput "activation-error.txt") -Raw
    }
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}
