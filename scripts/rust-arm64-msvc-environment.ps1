function Get-DeskBoxMsvcEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("x64", "ARM64")]
        [string]$Platform
    )

    $targetArchitecture = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
    $requiredComponent = if ($Platform -eq "ARM64") {
        "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
    }
    else {
        "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
    }
    $cargoLinkerVariable = if ($Platform -eq "ARM64") {
        "CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_LINKER"
    }
    else {
        "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER"
    }

    $processArchitecture =
        [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $osArchitecture =
        [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $hostArchitectures = @(
        if ($processArchitecture -eq "Arm64") {
            "arm64"
            "x64"
        }
        elseif ($processArchitecture -eq "X64") {
            "x64"
        }
        else {
            throw "DeskBox Rust builds require a native ARM64 or x64 PowerShell host; found '$processArchitecture'."
        }
    )

    $vsWhereCandidates = @(
        @(
            $command = Get-Command vswhere.exe -ErrorAction SilentlyContinue
            if ($null -ne $command) {
                $command.Source
            }
            foreach ($programFilesRoot in @(
                    ${env:ProgramFiles(x86)},
                    $env:ProgramFiles,
                    $env:ProgramW6432)) {
                if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
                    Join-Path $programFilesRoot "Microsoft Visual Studio\Installer\vswhere.exe"
                }
            }
        ) | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            (Test-Path -LiteralPath $_ -PathType Leaf)
        } | ForEach-Object {
            [System.IO.Path]::GetFullPath($_)
        } | Select-Object -Unique
    )
    if ($vsWhereCandidates.Count -eq 0) {
        throw "Visual Studio Installer discovery tool vswhere.exe was not found."
    }
    $vsWhere = $vsWhereCandidates[0]

    $linkerCandidates = @(
        for ($hostPriority = 0; $hostPriority -lt $hostArchitectures.Count; $hostPriority++) {
            $hostArchitecture = $hostArchitectures[$hostPriority]
            $hostToolSegment = if ($hostArchitecture -eq "arm64") {
                "Hostarm64"
            }
            else {
                "Hostx64"
            }
            & $vsWhere `
                -all `
                -products * `
                -requires $requiredComponent `
                -find "VC\Tools\MSVC\*\bin\$hostToolSegment\$targetArchitecture\link.exe" 2>$null |
                ForEach-Object {
                    if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) {
                        return
                    }

                    $linkerPath = [System.IO.Path]::GetFullPath($_)
                    $match = [regex]::Match($linkerPath, "\\MSVC\\([^\\]+)\\")
                    if (-not $match.Success) {
                        return
                    }

                    $vcToolsDirectory = Split-Path -Parent (
                        Split-Path -Parent (
                            Split-Path -Parent (Split-Path -Parent $linkerPath)))
                    $vcLibraryDirectory = Join-Path $vcToolsDirectory "lib\$targetArchitecture"
                    $vcLibraryFile = Join-Path $vcLibraryDirectory "libcmt.lib"
                    $vcIncludeDirectory = Join-Path $vcToolsDirectory "include"
                    if (-not (Test-Path -LiteralPath $vcLibraryFile -PathType Leaf) -or
                        -not (Test-Path -LiteralPath $vcIncludeDirectory -PathType Container)) {
                        return
                    }

                    [pscustomobject]@{
                        HostPriority = $hostPriority
                        HostArchitecture = $hostArchitecture
                        HostToolSegment = $hostToolSegment
                        Version = [version]$match.Groups[1].Value
                        VersionText = $match.Groups[1].Value
                        LinkerPath = $linkerPath
                        VcToolsDirectory = $vcToolsDirectory
                        VcLibraryDirectory = $vcLibraryDirectory
                        VcIncludeDirectory = $vcIncludeDirectory
                    }
                }
        }
    ) | Sort-Object `
        @{ Expression = { $_.HostPriority }; Ascending = $true }, `
        @{ Expression = { $_.Version }; Descending = $true }, `
        @{ Expression = { $_.LinkerPath }; Descending = $true }
    if (@($linkerCandidates).Count -eq 0) {
        throw "Visual Studio component $requiredComponent does not provide a complete $Platform linker/libcmt pair."
    }

    $selected = $linkerCandidates[0]
    $windowsKitsRoots = @(
        foreach ($programFilesRoot in @(
                ${env:ProgramFiles(x86)},
                $env:ProgramFiles,
                $env:ProgramW6432)) {
            if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
                Join-Path $programFilesRoot "Windows Kits\10"
            }
        }
    ) | Where-Object {
        Test-Path -LiteralPath $_ -PathType Container
    } | ForEach-Object {
        [System.IO.Path]::GetFullPath($_)
    } | Select-Object -Unique
    $windowsKitCandidates = @(
        foreach ($windowsKitsRoot in $windowsKitsRoots) {
            $windowsKitLibRoot = Join-Path $windowsKitsRoot "Lib"
            if (-not (Test-Path -LiteralPath $windowsKitLibRoot -PathType Container)) {
                continue
            }

            foreach ($sdkDirectory in @(Get-ChildItem -LiteralPath $windowsKitLibRoot -Directory)) {
                $ucrtLibrary = Join-Path $sdkDirectory.FullName "ucrt\$targetArchitecture\ucrt.lib"
                $kernelLibrary = Join-Path $sdkDirectory.FullName "um\$targetArchitecture\kernel32.lib"
                if (-not (Test-Path -LiteralPath $ucrtLibrary -PathType Leaf) -or
                    -not (Test-Path -LiteralPath $kernelLibrary -PathType Leaf)) {
                    continue
                }

                for ($toolPriority = 0; $toolPriority -lt $hostArchitectures.Count; $toolPriority++) {
                    $sdkHostArchitecture = $hostArchitectures[$toolPriority]
                    $sdkBinDirectory =
                        Join-Path $windowsKitsRoot "bin\$($sdkDirectory.Name)\$sdkHostArchitecture"
                    if ((Test-Path -LiteralPath (Join-Path $sdkBinDirectory "rc.exe") -PathType Leaf) -and
                        (Test-Path -LiteralPath (Join-Path $sdkBinDirectory "mt.exe") -PathType Leaf)) {
                        [pscustomobject]@{
                            Version = [version]$sdkDirectory.Name.TrimEnd('.')
                            VersionText = $sdkDirectory.Name
                            Root = $windowsKitsRoot
                            LibraryRoot = $sdkDirectory.FullName
                            BinDirectory = $sdkBinDirectory
                            HostArchitecture = $sdkHostArchitecture
                            ToolPriority = $toolPriority
                        }
                    }
                }
            }
        }
    ) | Sort-Object `
        @{ Expression = { $_.Version }; Descending = $true }, `
        @{ Expression = { $_.ToolPriority }; Ascending = $true }, `
        @{ Expression = { $_.Root }; Ascending = $true }
    $windowsKit = @($windowsKitCandidates | Select-Object -First 1)
    if ($windowsKit.Count -eq 0) {
        throw "Windows SDK $Platform UCRT/UM libraries and a compatible ARM64/x64-hosted rc.exe are not installed."
    }

    $windowsKitsRoot = $windowsKit[0].Root
    $sdkVersion = $windowsKit[0].VersionText
    $sdkIncludeRoot = Join-Path $windowsKitsRoot "Include\$sdkVersion"
    $sdkIncludeDirectories = @(
        "ucrt",
        "shared",
        "um",
        "winrt",
        "cppwinrt"
    ) | ForEach-Object { Join-Path $sdkIncludeRoot $_ }
    $missingIncludeDirectories = @(
        $sdkIncludeDirectories |
            Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) })
    if ($missingIncludeDirectories.Count -gt 0) {
        throw "Windows SDK include directories are incomplete: $($missingIncludeDirectories -join ', ')."
    }

    [pscustomobject]@{
        Platform = $Platform
        TargetArchitecture = $targetArchitecture
        ProcessArchitecture = $processArchitecture
        OsArchitecture = $osArchitecture
        HostArchitecture = $selected.HostArchitecture
        HostToolSegment = $selected.HostToolSegment
        RequiredComponent = $requiredComponent
        CargoLinkerVariable = $cargoLinkerVariable
        MsvcVersion = $selected.VersionText
        LinkerPath = $selected.LinkerPath
        LinkerDirectory = Split-Path -Parent $selected.LinkerPath
        VcToolsDirectory = $selected.VcToolsDirectory
        VcLibraryDirectory = $selected.VcLibraryDirectory
        VcIncludeDirectory = $selected.VcIncludeDirectory
        WindowsKitsRoot = $windowsKitsRoot
        WindowsSdkVersion = $sdkVersion
        WindowsSdkLibRoot = $windowsKit[0].LibraryRoot
        WindowsSdkUcrtLibraryDirectory =
            Join-Path $windowsKit[0].LibraryRoot "ucrt\$targetArchitecture"
        WindowsSdkUmLibraryDirectory =
            Join-Path $windowsKit[0].LibraryRoot "um\$targetArchitecture"
        WindowsSdkHostArchitecture = $windowsKit[0].HostArchitecture
        WindowsSdkBinDirectory = $windowsKit[0].BinDirectory
        WindowsSdkIncludeDirectories = $sdkIncludeDirectories
    }
}

function Enter-DeskBoxMsvcEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Toolchain
    )

    $libraryDirectories = @(
        $Toolchain.VcLibraryDirectory,
        $Toolchain.WindowsSdkUcrtLibraryDirectory,
        $Toolchain.WindowsSdkUmLibraryDirectory)
    $includeDirectories = @($Toolchain.VcIncludeDirectory) +
        @($Toolchain.WindowsSdkIncludeDirectories)
    $pathDirectories = @(
        $Toolchain.LinkerDirectory,
        $Toolchain.WindowsSdkBinDirectory)

    $existingPath = [Environment]::GetEnvironmentVariable("PATH", "Process")
    $existingLib = [Environment]::GetEnvironmentVariable("LIB", "Process")
    $existingInclude = [Environment]::GetEnvironmentVariable("INCLUDE", "Process")
    $updates = [ordered]@{}
    $updates[[string]$Toolchain.CargoLinkerVariable] = $Toolchain.LinkerPath
    $updates["VCToolsInstallDir"] = $Toolchain.VcToolsDirectory.TrimEnd('\', '/') + '\'
    $updates["VCToolsVersion"] = [string]$Toolchain.MsvcVersion
    $updates["WindowsSdkDir"] = $Toolchain.WindowsKitsRoot.TrimEnd('\', '/') + '\'
    $updates["WindowsSDKVersion"] = $Toolchain.WindowsSdkVersion.TrimEnd('\', '/') + '\'
    $updates["UniversalCRTSdkDir"] = $Toolchain.WindowsKitsRoot.TrimEnd('\', '/') + '\'
    $updates["UCRTVersion"] = $Toolchain.WindowsSdkVersion
    $updates["VSCMD_ARG_HOST_ARCH"] = $Toolchain.HostArchitecture
    $updates["VSCMD_ARG_TGT_ARCH"] = $Toolchain.TargetArchitecture
    $updates["PATH"] = (($pathDirectories + @($existingPath)) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'
    $updates["LIB"] = (($libraryDirectories + @($existingLib)) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'
    $updates["INCLUDE"] = (($includeDirectories + @($existingInclude)) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'

    $previousValues = @{}
    foreach ($entry in $updates.GetEnumerator()) {
        $previousValues[$entry.Key] =
            [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            [string]$entry.Value,
            "Process")
    }

    [pscustomobject]@{
        VariableNames = @($updates.Keys)
        PreviousValues = $previousValues
    }
}

function Exit-DeskBoxMsvcEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$State
    )

    foreach ($variableName in @($State.VariableNames)) {
        $previousValue = $State.PreviousValues[$variableName]
        if ($null -eq $previousValue) {
            Remove-Item -LiteralPath "Env:$variableName" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable(
                $variableName,
                [string]$previousValue,
                "Process")
        }
    }
}

# Compatibility wrappers keep the existing ARM64 build scripts narrow while the
# shared implementation also supplies the explicit x64 NativeAOT environment.
function Get-DeskBoxArm64MsvcEnvironment {
    [CmdletBinding()]
    param()

    Get-DeskBoxMsvcEnvironment -Platform ARM64
}

function Enter-DeskBoxArm64MsvcEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Toolchain
    )

    Enter-DeskBoxMsvcEnvironment -Toolchain $Toolchain
}

function Exit-DeskBoxArm64MsvcEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$State
    )

    Exit-DeskBoxMsvcEnvironment -State $State
}
