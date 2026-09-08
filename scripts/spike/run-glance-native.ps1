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
$todoProject = Join-Path $repoRoot "spikes\glance-native\TodoPackage\Todo.NativePackage.csproj"
$hostProject = Join-Path $repoRoot "spikes\glance-native\Host\Glance.NativeHost.csproj"
$common = @("-c", "Release", "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid",
    "-p:PublishAot=true", "-p:SelfContained=true", "-p:WindowsAppSDKSelfContained=false",
    "-p:IlcUseEnvironmentalTools=true")
function Invoke-DotNet([string[]]$CommandArguments) {
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
function Invoke-Probe {
    param([string]$Exe, [string]$WorkingDirectory, [string[]]$Arguments, [string]$Evidence, [int]$TimeoutSeconds = 30)
    New-Item -ItemType Directory -Path $Evidence -Force | Out-Null
    $process = Start-Process -FilePath $Exe -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru -ArgumentList $Arguments
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id
        throw "Probe timed out. Evidence: $Evidence"
    }
    $resultFile = Join-Path $Evidence "result.json"
    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "Probe did not produce a result. Evidence: $Evidence"
    }
    if ($process.ExitCode -ne 0) { throw "Probe exited with $($process.ExitCode). Evidence: $Evidence" }
    return (Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json)
}
try {
    foreach ($project in @($packageProject, $todoProject, $hostProject)) {
        Invoke-DotNet -CommandArguments @("restore", $project, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=false")
        Invoke-DotNet -CommandArguments @("restore", $project, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=true")
    }
    $hostOutput = Join-Path $runRoot "host"
    Invoke-DotNet -CommandArguments (@("publish", $hostProject, "--no-restore") + $common + @("-o", $hostOutput))
    # CONTRACT FINDING: publish drops Page XBFs for this unpackaged layout, but
    # ms-appx resolution needs them on disk next to the exe (App.xaml's
    # ApplicationDefinition XBF embeds differently). Copy Page XBFs explicitly;
    # path is platform-aware so ARM64 BuildOnly keeps honest evidence.
    $xbfSource = Join-Path $repoRoot "spikes\glance-native\Host\obj\$Platform\Release\net10.0-windows10.0.22621.0\$rid"
    Get-ChildItem -LiteralPath $xbfSource -Filter "*.xbf" -ErrorAction SilentlyContinue |
        Where-Object Name -ne "App.xbf" |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $hostOutput -Force }
    $hostExe = Join-Path $hostOutput "DeskBox.Glance.NativeHost.exe"
    $originalHostHash = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash
    $originalLayout = Join-Path $repoRoot "src\DeskBox\Services\GlanceCalendarLayoutCalculator.cs"
    $variantLayout = Join-Path $runRoot "GlanceCalendarLayoutCalculator.v2.cs"
    $source = [IO.File]::ReadAllText($originalLayout)
    if (-not $source.Contains("CompactCalendarThreshold = 320")) { throw "Glance source threshold changed; update the probe deliberately." }
    [IO.File]::WriteAllText($variantLayout, $source.Replace("CompactCalendarThreshold = 320", "CompactCalendarThreshold = 360"))

    $packageOutputs = @{}
    foreach ($version in @(1, 2, 3)) {
        $packageOutput = Join-Path $runRoot "package-v$version"
        $layout = if ($version -eq 2) { $variantLayout } else { $originalLayout }
        Invoke-DotNet -CommandArguments (@("publish", $packageProject, "--no-restore") + $common +
            @("-p:GlancePackageVersion=$version", "-p:GlanceLayoutSource=$layout", "-o", $packageOutput))
        $packageOutputs[$version] = $packageOutput
    }
    $todoOutput = Join-Path $runRoot "todo-package"
    Invoke-DotNet -CommandArguments (@("publish", $todoProject, "--no-restore") + $common + @("-o", $todoOutput))

    $packageDllHashes = @{}
    foreach ($version in @(1, 2, 3)) {
        $dll = Join-Path $packageOutputs[$version] "DeskBox.Glance.NativePackage.dll"
        $packageDllHashes[$version] = [ordered]@{
            path = $dll
            sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath $dll).Length
        }
    }
    if ($packageDllHashes[1].sha256 -eq $packageDllHashes[2].sha256) { throw "Packages v1/v2 must differ." }
    if ($packageDllHashes[2].sha256 -eq $packageDllHashes[3].sha256) { throw "Packages v2/v3 must differ." }

    $results = @()
    $canExecute = -not $BuildOnly -and $Platform -eq "x64"
    if ($canExecute) {
        foreach ($version in @(1, 2)) {
            $evidence = Join-Path $runRoot "result-v$version"
            $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
                "--development-package", ('"{0}"' -f $packageOutputs[$version]), ('"{0}"' -f $evidence))
            $expectedHeight = if ($version -eq 1) { 244 } else { 268 }
            if ($result.dynamicCodeSupported -or $result.packageVersion -ne $version -or
                $result.panelHeightFor340 -ne $expectedHeight -or $result.calendarActualHeight -ne $expectedHeight -or
                $result.heading -ne "Glance native package v$version") {
                throw "AOT/module version/business behavior assertion failed: $evidence"
            }
            if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
            $results += [pscustomobject]@{ scenario = "simple-v$version"; result = $result; evidence = $evidence }
        }

        # Real Glance slice: production XAML + production services in the package.
        $evidence = Join-Path $runRoot "result-real"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--real-package", ('"{0}"' -f $packageOutputs[3]), ('"{0}"' -f $evidence))
        $festivalDays = @($result.packageSummary.festivalDays)
        if ($result.dynamicCodeSupported -or
            [string]::IsNullOrWhiteSpace($result.traditionalTitle) -or
            $result.traditionalTitle -ne $result.packageSummary.traditionalTitle -or
            $result.packageSummary.traditionalTextDayCount -lt 28 -or
            $result.packageSummary.decoratedDayCount -lt 35 -or
            $festivalDays.Count -lt 1 -or
            $result.calendarActualHeight -le 200) {
            throw "Real-Glance slice assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "real-glance"; result = $result; evidence = $evidence }

        # Compiled-XAML (XBF) slice: PINNED NEGATIVE. XBF LoadComponent cannot
        # locate compiled XAML in a dynamically loaded AOT DLL (even a type-free
        # minimal control); AOT DLLs export no DllGetActivationFactory; the
        # class-library build produces no standalone .pri and MrtCore has no
        # file-based loading. The scenario records all three outcomes; if a
        # future Windows App SDK flips any of them, the assertion forces the
        # spike contract findings to be re-evaluated.
        $evidence = Join-Path $runRoot "result-compiled"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--compiled-package", ('"{0}"' -f $packageOutputs[3]), ('"{0}"' -f $evidence))
        if ($result.compiledXamlLoads -or
            $result.compiledStages -ne "none" -or
            $result.activationFactoryExport -or
            $result.localizationProbe.priFilesNextToDll -ne "none") {
            throw "Compiled-XAML pin flipped - re-evaluate the spike contract findings: $evidence"
        }
        $results += [pscustomobject]@{ scenario = "compiled-xaml-pinned-negative"; result = $result; evidence = $evidence }

        # Full Glance slice: rotation timer, action-bar click, settings toggle,
        # destroy/recreate persistence. Backgrounds are generated (no binaries in
        # the repository); settings file cleared for a deterministic first run.
        $backgrounds = Join-Path $packageOutputs[3] "backgrounds"
        New-Item -ItemType Directory -Path $backgrounds -Force | Out-Null
        Add-Type -AssemblyName System.Drawing
        foreach ($pair in @(@("bg-1.png", "40, 70, 130"), @("bg-2.png", "130, 55, 45"))) {
            $bitmap = New-Object System.Drawing.Bitmap(440, 560)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $channels = $pair[1].Split(",") | ForEach-Object { [int]$_.Trim() }
            $graphics.Clear([System.Drawing.Color]::FromArgb($channels[0], $channels[1], $channels[2]))
            $graphics.Dispose()
            $bitmap.Save((Join-Path $backgrounds $pair[0]), [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
        }
        Remove-Item -LiteralPath (Join-Path $packageOutputs[3] "glance-settings.json") -ErrorAction SilentlyContinue
        $evidence = Join-Path $runRoot "result-full"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--full-package", ('"{0}"' -f $packageOutputs[3]), ('"{0}"' -f $evidence))
        if ($result.dynamicCodeSupported -or
            $result.rotationIndexAfterTimer -lt 1 -or
            $result.indexAfterNextClick -eq $result.rotationIndexAfterTimer -or
            $result.afterFestivalOff.showFestivals -or
            $result.afterFestivalOff.festivalDayCount -ne 0 -or
            $result.afterRecreate.showFestivals -or
            $result.afterRecreate.festivalDayCount -ne 0 -or
            $result.afterRecreate.currentImageIndex -ne 0 -or
            $result.afterRecreate.traditionalTextDayCount -ne 42) {
            throw "Full-Glance slice assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "full-glance"; result = $result; evidence = $evidence }

        # Batch C comprehensive interaction probe: host custom control + toolkit
        # control in package text XAML, three-root ABI, theme tokens, event wiring.
        $interactionProject = Join-Path $repoRoot "spikes\glance-native\InteractionPackage\Interaction.NativePackage.csproj"
        Invoke-DotNet -CommandArguments @("restore", $interactionProject, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=false")
        Invoke-DotNet -CommandArguments @("restore", $interactionProject, "-p:Platform=$Platform", "-p:RuntimeIdentifier=$rid", "-p:PublishAot=true")
        $interactionOutput = Join-Path $runRoot "interaction-package"
        Invoke-DotNet -CommandArguments (@("publish", $interactionProject, "--no-restore") + $common + @("-o", $interactionOutput))
        $evidence = Join-Path $runRoot "result-interaction"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--interaction-package", ('"{0}"' -f $interactionOutput), ('"{0}"' -f $evidence)) -TimeoutSeconds 45
        if (-not $result.hostControlResolved -or
            -not $result.toolkitControlResolved -or
            $result.itemsAfterFirstEdit -ne 1 -or
            $result.itemsAfterRecreate -ne 1 -or
            -not $result.packageRootUntouched -or
            -not $result.packageSummary.themeTokenApplied -or
            $result.packageSummary.eventWiringCount -lt 2 -or
            $result.packageSummary.destroyCalls -lt 1) {
            throw "Interaction probe assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "interaction-probe"; result = $result; evidence = $evidence }

        # Lifecycle: destroy and recreate the view within one process.
        $evidence = Join-Path $runRoot "result-lifecycle"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--lifecycle-package", ('"{0}"' -f $packageOutputs[1]), ('"{0}"' -f $evidence))
        if (-not $result.firstLoaded -or -not $result.firstUnloaded -or -not $result.secondLoaded -or
            $result.secondCalendarActualHeight -ne 244 -or $result.secondHeading -ne "Glance native package v1") {
            throw "Lifecycle assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "lifecycle"; result = $result; evidence = $evidence }

        # Multi-package: two native DLLs loaded side by side in one host process.
        $evidence = Join-Path $runRoot "result-multi"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--multi-package", ('"{0}"' -f $packageOutputs[2]), ('"{0}"' -f $packageOutputs[3]), ('"{0}"' -f $evidence))
        if ($result.simplePackageVersion -ne 2 -or $result.simpleCalendarActualHeight -ne 268 -or
            [string]::IsNullOrWhiteSpace($result.realTraditionalTitle)) {
            throw "Multi-package assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "multi-package"; result = $result; evidence = $evidence }

        # Todo slice: edit through the projected control, persist, recreate, reload.
        $storedItems = Join-Path $todoOutput "todo-items.json"
        if (Test-Path -LiteralPath $storedItems) { Remove-Item -LiteralPath $storedItems -Force }
        $evidence = Join-Path $runRoot "result-todo"
        $result = Invoke-Probe -Exe $hostExe -WorkingDirectory $hostOutput -Evidence $evidence -Arguments @(
            "--todo-package", ('"{0}"' -f $todoOutput), ('"{0}"' -f $evidence))
        if ($result.itemsAfterFirstEdit -ne 1 -or $result.itemsAfterRecreate -ne 1 -or
            -not $result.persistedItem.Contains("采购牛奶")) {
            throw "Todo edit/persistence assertion failed: $evidence"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $evidence "view.png"))) { throw "Missing rendered view: $evidence" }
        $results += [pscustomobject]@{ scenario = "todo-edit-persist"; result = $result; evidence = $evidence }

        if ((Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash -ne $originalHostHash) {
            throw "Host changed while switching packages."
        }
    }

    # ConvertTo-Json rejects non-string hashtable keys; re-key with "v<version>".
    $packageSummary = [ordered]@{}
    foreach ($version in @(1, 2, 3)) { $packageSummary["v$version"] = $packageDllHashes[$version] }
    $summary = [ordered]@{
        platform = $Platform
        runRoot = $runRoot
        hostExe = $hostExe
        hostSha256 = $originalHostHash
        packages = $packageSummary
        todoPackage = [ordered]@{
            path = (Join-Path $todoOutput "DeskBox.Todo.NativePackage.dll")
            sha256 = (Get-FileHash -LiteralPath (Join-Path $todoOutput "DeskBox.Todo.NativePackage.dll") -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath (Join-Path $todoOutput "DeskBox.Todo.NativePackage.dll")).Length
        }
        executed = $canExecute
        results = $results
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot "summary.json") -Encoding UTF8
    $summary | ConvertTo-Json -Depth 8
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}
