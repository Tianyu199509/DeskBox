param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output 'package'
if (Test-Path -LiteralPath $payload) { throw 'Use a fresh probe output directory.' }
$packageName = 'DeskBox.NP.' + [Guid]::NewGuid().ToString('N')
$clsid = [Guid]::NewGuid().ToString().ToUpperInvariant()
$probeRoot = Join-Path ([Environment]::GetFolderPath('CommonDocuments')) ('DeskBox-NotificationActivationProbe\' + $packageName)
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint)
if (-not $certificate.HasPrivateKey) { throw 'The existing test certificate has no private key.' }
$sdk = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
$runtime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' |
    Where-Object { $_.Architecture -eq 'X64' } | Sort-Object Version -Descending | Select-Object -First 1
if (-not $runtime) { throw 'The Windows App Runtime 2 framework must already be installed.' }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem -LiteralPath $PublishDirectory -Force | Copy-Item -Destination $payload -Recurse
New-Item -ItemType Directory -Path (Join-Path $payload 'Assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'src\DeskBox\Assets\Store\Square150x150Logo.png') -Destination (Join-Path $payload 'Assets\Logo.png')
Copy-Item -LiteralPath (Join-Path $repo 'src\DeskBox\Assets\Store\Square44x44Logo.png') -Destination (Join-Path $payload 'Assets\SmallLogo.png')
$publisher = [Security.SecurityElement]::Escape($certificate.Subject)
$frameworkPublisher = [Security.SecurityElement]::Escape($runtime.Publisher)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop com rescap">
 <Identity Name="$packageName" Publisher="$publisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
 <Properties><DisplayName>DeskBox notification probe</DisplayName><PublisherDisplayName>DeskBox</PublisherDisplayName><Logo>Assets\Logo.png</Logo></Properties>
 <Resources><Resource Language="en-US" /></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
 <PackageDependency Name="Microsoft.WindowsAppRuntime.2" Publisher="$frameworkPublisher" MinVersion="2.5.1.0" /></Dependencies>
 <Applications><Application Id="App" Executable="DeskBox.NotificationActivationProbe.exe" EntryPoint="Windows.FullTrustApplication">
 <uap:VisualElements DisplayName="DeskBox notification probe" Description="Isolated activation verification" BackgroundColor="transparent" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\SmallLogo.png" />
 <Extensions><desktop:Extension Category="windows.toastNotificationActivation"><desktop:ToastNotificationActivation ToastActivatorCLSID="$clsid" /></desktop:Extension>
 <com:Extension Category="windows.comServer"><com:ComServer><com:ExeServer Executable="DeskBox.NotificationActivationProbe.exe" Arguments="----AppNotificationActivated:" DisplayName="DeskBox notification probe"><com:Class Id="$clsid" DisplayName="DeskBox notification probe" /></com:ExeServer></com:ComServer></com:Extension></Extensions>
 </Application></Applications><Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $payload 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
$msix = Join-Path $output 'notification-probe.msix'
& (Join-Path $sdk.FullName 'x64\makeappx.exe') pack /d $payload /p $msix /o > (Join-Path $output 'pack.log')
if ($LASTEXITCODE -ne 0) { throw 'Probe packaging failed.' }
& (Join-Path $sdk.FullName 'x64\signtool.exe') sign /fd SHA256 /sha1 $CertificateThumbprint $msix > (Join-Path $output 'sign.log')
if ($LASTEXITCODE -ne 0) { throw 'Probe signing failed.' }
$installed = $null
$report = [ordered]@{ PackageName=$packageName; Clsid=$clsid; Events=@(); Forwarded=@(); Passed=$false; Removed=$false }
try {
    Add-AppxPackage -Path $msix
    $installed = Get-AppxPackage -Name $packageName
    if (-not $installed) { throw 'The isolated package was not installed.' }
    $invoker = Join-Path $PublishDirectory 'DeskBox.NotificationActivationProbe.exe'
    foreach ($token in @('cold', 'warm')) {
        $process = Start-Process -FilePath $invoker -ArgumentList @('--invoke', $clsid, $token) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'COM activation timed out.' }
        if ($process.ExitCode -ne 0) { throw "COM activation failed: $($process.ExitCode). See $probeRoot" }
        $eventsFile = Join-Path $probeRoot 'events.log'
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $events = @(if (Test-Path -LiteralPath $eventsFile) { Get-Content -LiteralPath $eventsFile })
            if (@($events | Where-Object { $_ -like ('*id=' + $token + ' *') }).Count -gt 0) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (@($events | Where-Object { $_ -like ('*id=' + $token + ' *') }).Count -ne 1) { throw "Missing or duplicate $token activation." }
    }
    $report.Events = $events
    if ($events.Count -ne 2 -or $events[0] -notlike '*source=CurrentAppInstance*' -or $events[1] -notlike '*source=NotificationInvokedEvent*') { throw 'Cold/warm source mismatch.' }
    $pids = @($events | ForEach-Object { [regex]::Match($_, 'pid=(\d+)').Groups[1].Value } | Select-Object -Unique)
    if ($pids.Count -ne 1) { throw 'The warm callback was delivered to a different process.' }
    if (@($events | Where-Object { $_ -notlike '*input=30m' }).Count) { throw 'Notification user input was lost.' }
    $drain = Start-Process -FilePath $invoker -ArgumentList @('--drain', ('"' + $probeRoot + '"')) -WindowStyle Hidden -PassThru
    if (-not $drain.WaitForExit(10000)) { $drain.Kill(); throw 'Envelope forwarding timed out.' }
    if ($drain.ExitCode -ne 0) { throw 'Envelope forwarding failed.' }
    $report.Forwarded = @(Get-Content -LiteralPath (Join-Path $probeRoot 'forwarded.log'))
    $report.Passed = $true
}
finally {
    if ($installed -and $installed.Name -eq $packageName) {
        $prefix = $installed.InstallLocation.TrimEnd('\') + '\'
        Get-CimInstance Win32_Process -Filter "Name='DeskBox.NotificationActivationProbe.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        Remove-AppxPackage -Package $installed.PackageFullName
        $report.Removed = -not [bool](Get-AppxPackage -Name $packageName)
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
}
$report | ConvertTo-Json -Depth 5
