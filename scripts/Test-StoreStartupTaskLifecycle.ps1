param(
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$auditRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $auditRoot -Force | Out-Null
$probeId = [Guid]::NewGuid().ToString('N')
$packageName = 'DeskBox.Probe.' + $probeId
$aliasName = 'DeskBox-StartupProbe-' + $probeId + '.exe'
$taskName = 'DeskBox Startup Probe-' + $probeId
$probeLog = Join-Path $auditRoot 'probe-launches.log'
$reportPath = Join-Path $auditRoot 'lifecycle-result.json'
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint)
if (-not $certificate.HasPrivateKey) { throw 'The existing test certificate has no private key.' }
$sdk = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK packaging tools are unavailable.' }
$makeAppx = Join-Path $sdk.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdk.FullName 'x64\signtool.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$report = [ordered]@{ packageName = $packageName; taskName = $taskName; firstLaunch = $null; updatedLaunch = $null; taskRemainsAfterUninstall = $null; cleanupSucceeded = $false; error = $null }

function Invoke-ProbeTask([string]$ExpectedVersion) {
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (Test-Path -LiteralPath $probeLog) {
            $line = Get-Content -LiteralPath $probeLog | Where-Object { $_ -like ('version=' + $ExpectedVersion + ';*') } | Select-Object -Last 1
            if ($line) { return $line }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('No probe launch recorded for version ' + $ExpectedVersion)
}

try {
    foreach ($version in @('1.0.0.0', '2.0.0.0')) {
        $packageRoot = Join-Path $auditRoot $version
        New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Assets') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src\DeskBox\Assets\Store\Square150x150Logo.png') -Destination (Join-Path $packageRoot 'Assets\Logo.png')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src\DeskBox\Assets\Store\Square44x44Logo.png') -Destination (Join-Path $packageRoot 'Assets\SmallLogo.png')
        $source = @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
[assembly: AssemblyVersion("$version")]
internal static class Probe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void Main(string[] args) {
        if (args.Length != 1) return;
        int length=0; GetCurrentPackageFullName(ref length, null);
        var name=new StringBuilder(length); GetCurrentPackageFullName(ref length, name);
        IntPtr token; int elevated=-1, size;
        if (OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 8, out token)) {
            GetTokenInformation(token, 20, out elevated, 4, out size); CloseHandle(token);
        }
        File.AppendAllText(args[0], "version=$version;elevated=" + elevated + ";package=" + name + Environment.NewLine);
    }
}
"@
        $sourcePath = Join-Path $auditRoot ('Probe-' + $version + '.cs')
        $source | Set-Content -LiteralPath $sourcePath -Encoding utf8
        & $compiler /nologo /target:winexe /platform:x64 ('/out:' + (Join-Path $packageRoot 'Probe.exe')) $sourcePath
        if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
        $publisher = [Security.SecurityElement]::Escape($certificate.Subject)
        @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap uap3 desktop rescap">
  <Identity Name="$packageName" Publisher="$publisher" Version="$version" ProcessorArchitecture="x64" />
  <Properties><DisplayName>DeskBox Startup Probe</DisplayName><PublisherDisplayName>DeskBox Test</PublisherDisplayName><Logo>Assets\Logo.png</Logo></Properties>
  <Resources><Resource Language="en-US" /></Resources>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Applications><Application Id="App" Executable="Probe.exe" EntryPoint="Windows.FullTrustApplication">
    <uap:VisualElements DisplayName="DeskBox Startup Probe" Description="Temporary startup lifecycle test" BackgroundColor="transparent" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\SmallLogo.png" />
    <Extensions><uap3:Extension Category="windows.appExecutionAlias" Executable="Probe.exe" EntryPoint="Windows.FullTrustApplication"><uap3:AppExecutionAlias><desktop:ExecutionAlias Alias="$aliasName" /></uap3:AppExecutionAlias></uap3:Extension></Extensions>
  </Application></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Encoding utf8
        $packagePath = Join-Path $auditRoot ('probe-' + $version + '.msix')
        & $makeAppx pack /d $packageRoot /p $packagePath /o | Out-File -LiteralPath (Join-Path $auditRoot ('pack-' + $version + '.log'))
        if ($LASTEXITCODE -ne 0) { throw 'Probe packaging failed.' }
        & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $packagePath | Out-File -LiteralPath (Join-Path $auditRoot ('sign-' + $version + '.log'))
        if ($LASTEXITCODE -ne 0) { throw 'Probe signing failed.' }
        Add-AppxPackage -Path $packagePath -ErrorAction Stop
        if ($version -eq '1.0.0.0') {
            $aliasPath = Join-Path $env:LOCALAPPDATA ('Microsoft\WindowsApps\' + $aliasName)
            $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $userName = [Security.SecurityElement]::Escape([Security.Principal.WindowsIdentity]::GetCurrent().Name)
            $taskCommand = [Security.SecurityElement]::Escape($aliasPath)
            $taskArguments = [Security.SecurityElement]::Escape('"' + $probeLog + '"')
            $taskXmlPath = Join-Path $auditRoot 'probe-task.xml'
            @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Source>DeskBox Startup Probe</Source><URI>\$taskName</URI></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$userName</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$userSid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>
  <Actions Context="Author"><Exec><Command>$taskCommand</Command><Arguments>$taskArguments</Arguments></Exec></Actions>
</Task>
"@ | Set-Content -LiteralPath $taskXmlPath -Encoding unicode
            & (Join-Path $env:WINDIR 'System32\schtasks.exe') /Create /TN $taskName /XML $taskXmlPath /F
            if ($LASTEXITCODE -ne 0) { throw 'Probe task registration failed.' }
            $report.firstLaunch = Invoke-ProbeTask $version
        } else {
            $report.updatedLaunch = Invoke-ProbeTask $version
        }
    }
    $probePackage = Get-AppxPackage -Name $packageName
    if ($probePackage.Name -ne $packageName) { throw 'Unexpected package identity before removal.' }
    Remove-AppxPackage -Package $probePackage.PackageFullName
    $report.taskRemainsAfterUninstall = $null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
}
catch {
    $report.error = $_.Exception.Message
}
finally {
    # Remove only the GUID-scoped resources created by this invocation.
    Get-AppxPackage -Name $packageName | Where-Object { $_.Name -eq $packageName } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    $report.cleanupSucceeded = -not (Get-AppxPackage -Name $packageName) -and -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 5
}
if ($report.error) { throw $report.error }
