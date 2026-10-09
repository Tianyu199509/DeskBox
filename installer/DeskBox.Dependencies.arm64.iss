[Code]
const
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-win-arm64.exe';
  DotNetRuntimeFallbackUrl = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-arm64.exe';
  DotNetRuntimeInstallerName = 'dotnet-runtime-10.0.12-win-arm64.exe';
  WindowsAppRuntimeUrl = 'https://aka.ms/windowsappsdk/2.5/2.5.1/windowsappruntimeinstall-arm64.exe';
  WindowsAppRuntimeFallbackUrl = 'https://download.microsoft.com/download/83fdef97-1ca8-491d-80f3-a94b5adc5b1c/WindowsAppRuntimeInstall-arm64.exe';
  WindowsAppRuntimeInstallerName = 'WindowsAppRuntimeInstall-arm64.exe';

var
  DependencyDownloadPage: TDownloadWizardPage;
  DependencyInstallPage: TOutputProgressWizardPage;
  ShouldInstallDotNetRuntime: Boolean;
  ShouldInstallWindowsAppRuntime: Boolean;
  DependenciesPrepared: Boolean;

function IsMajorVersion(Value: string; ExpectedMajor: Integer): Boolean;
var
  DotPosition: Integer;
  MajorText: string;
begin
  DotPosition := Pos('.', Value);
  if DotPosition > 0 then
    MajorText := Copy(Value, 1, DotPosition - 1)
  else
    MajorText := Value;

  Result := StrToIntDef(MajorText, 0) = ExpectedMajor;
end;

function IsCompatibleDotNetRuntimeVersion(Value: string): Boolean;
begin
  // A preview/RC folder such as 10.0.0-preview.7 cannot satisfy an app that
  // targets the stable Microsoft.NETCore.App 10.0.0 framework.
  Result := (Pos('-', Value) = 0) and IsMajorVersion(Value, 10);
end;

var
  DotNet10RuntimeDetected: Boolean;

procedure DetectDotNet10RuntimeFromOutput(
  const S: String;
  const Error, FirstLine: Boolean);
var
  LineText: string;
  VersionText: string;
  VersionEnd: Integer;
begin
  if Error then
  begin
    Log('dotnet --list-runtimes error: ' + S);
    Exit;
  end;

  LineText := Trim(S);
  if Pos('Microsoft.NETCore.App ', LineText) <> 1 then
    Exit;

  VersionText := Copy(LineText, Length('Microsoft.NETCore.App ') + 1, MaxInt);
  VersionEnd := Pos(' ', VersionText);
  if VersionEnd > 0 then
    VersionText := Copy(VersionText, 1, VersionEnd - 1);

  if IsCompatibleDotNetRuntimeVersion(VersionText) then
    DotNet10RuntimeDetected := True;
end;

function IsDotNet10RuntimeInstalledAt(BasePath: string): Boolean;
var
  DotNetPath: string;
  ResultCode: Integer;
begin
  Result := False;
  DotNetPath := AddBackslash(BasePath) + 'dotnet\dotnet.exe';
  if not FileExists(DotNetPath) then
    Exit;

  DotNet10RuntimeDetected := False;
  try
    if not ExecAndLogOutput(
      DotNetPath,
      '--list-runtimes',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode,
      @DetectDotNet10RuntimeFromOutput) then
    begin
      Log('DeskBox dependency check could not run: ' + DotNetPath);
      Exit;
    end;
  except
    Log('DeskBox dependency check failed: ' + GetExceptionMessage);
    Exit;
  end;

  Result := (ResultCode = 0) and DotNet10RuntimeDetected;
end;

function IsDotNet10RuntimeInstalled: Boolean;
begin
  // {autopf} follows the installer architecture (Program Files on x64 and
  // native ARM64 Program Files on Windows ARM). Keep {pf} as a compatibility
  // fallback for older Inno Setup installations and custom layouts.
  Result :=
    IsDotNet10RuntimeInstalledAt(ExpandConstant('{autopf}')) or
    IsDotNet10RuntimeInstalledAt(ExpandConstant('{pf}'));
end;

function IsWindowsAppRuntime25Installed: Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "$pkg = Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''ARM64'' -and [version]$_.Version -ge [version]''2.5.1.0'' } | Select-Object -First 1; if (-not $pkg) { $pkg = Get-AppxPackage -AllUsers -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''ARM64'' -and [version]$_.Version -ge [version]''2.5.1.0'' } | Select-Object -First 1 }; if ($pkg) { exit 0 } exit 1"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

procedure DetectDeskBoxDependencies;
begin
#if DeskBoxNativeAot
  ShouldInstallDotNetRuntime := False;
#else
  ShouldInstallDotNetRuntime := not IsDotNet10RuntimeInstalled;
#endif
  ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime25Installed;

  Log('DeskBox dependency check: dotnet10Missing=' + IntToStr(Integer(ShouldInstallDotNetRuntime)));
  Log('DeskBox dependency check: windowsAppRuntimeMissing=' + IntToStr(Integer(ShouldInstallWindowsAppRuntime)));
end;

procedure WaitForDeskBoxDependencies;
var
  Attempt: Integer;
begin
  // Runtime installers can return just before Windows finishes publishing the
  // machine-wide registration. Recheck for a few seconds before continuing.
  for Attempt := 1 to 10 do
  begin
    DetectDeskBoxDependencies;
    if not (ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime) then
      Exit;

    Sleep(1000);
  end;

  DetectDeskBoxDependencies;
end;

function DownloadDependencyWithProgress(
  DisplayName: string;
  Url: string;
  FallbackUrl: string;
  FileName: string;
  var ErrorMessage: string): Boolean;
begin
  Result := False;
  ErrorMessage := '';

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(Url, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
    Exit;
  except
    if DependencyDownloadPage.AbortedByUser then
    begin
      ErrorMessage := ExpandConstant('{cm:DependencyDownloadCancelled}');
      Exit;
    end;

    ErrorMessage := GetExceptionMessage;
    Log(DisplayName + ' primary download failed: ' + ErrorMessage);
  end;

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(FallbackUrl, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
  except
    if DependencyDownloadPage.AbortedByUser then
      ErrorMessage := ExpandConstant('{cm:DependencyDownloadCancelled}')
    else
      ErrorMessage := FmtMessage(
        ExpandConstant('{cm:DependencyDownloadFailed}'), [DisplayName, Url, FallbackUrl, GetExceptionMessage]);

    Log(DisplayName + ' fallback download failed: ' + ErrorMessage);
  end;
end;

function DownloadDeskBoxDependencies: Boolean;
var
  ErrorMessage: string;
begin
  Result := True;

  if not (ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime) then
    Exit;

  DependencyDownloadPage.Show;
  try
    if ShouldInstallDotNetRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := ExpandConstant('{cm:DownloadingDotNet}');
      if not DownloadDependencyWithProgress(
        '.NET 10 Runtime ARM64',
        DotNetRuntimeUrl,
        DotNetRuntimeFallbackUrl,
        DotNetRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := ExpandConstant('{cm:DownloadingWinAppRuntime}');
      if not DownloadDependencyWithProgress(
        'Windows App Runtime 2.5 ARM64',
        WindowsAppRuntimeUrl,
        WindowsAppRuntimeFallbackUrl,
        WindowsAppRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyDownloadPage.Hide;
  end;
end;

function InstallDownloadedDependency(
  DisplayName: string;
  FileName: string;
  Parameters: string;
  Step: Integer;
  StepCount: Integer;
  var NeedsRestart: Boolean): Boolean;
var
  InstallerPath: string;
  ResultCode: Integer;
begin
  Result := False;
  InstallerPath := ExpandConstant('{tmp}\' + FileName);

  DependencyInstallPage.SetProgress(Step - 1, StepCount);
  DependencyInstallPage.SetText(
    FmtMessage(ExpandConstant('{cm:InstallingDependency}'), [DisplayName]),
    '');

  if not ShellExec('runas', InstallerPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(
      FmtMessage(ExpandConstant('{cm:DependencyInstallStartFailed}'), [DisplayName]),
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
  begin
    NeedsRestart := True;
    Result := True;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(
      FmtMessage(
        ExpandConstant('{cm:DependencyInstallFailed}'), [DisplayName, IntToStr(ResultCode)]),
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  DependencyInstallPage.SetProgress(Step, StepCount);
  Result := True;
end;

function InstallDeskBoxDependencies(var NeedsRestart: Boolean): Boolean;
var
  Step: Integer;
  StepCount: Integer;
begin
  Result := True;
  Step := 0;
  StepCount := 0;

  if ShouldInstallDotNetRuntime then
    StepCount := StepCount + 1;

  if ShouldInstallWindowsAppRuntime then
    StepCount := StepCount + 1;

  if StepCount = 0 then
    Exit;

  DependencyInstallPage.Show;
  try
    if ShouldInstallDotNetRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        '.NET 10 Runtime ARM64',
        DotNetRuntimeInstallerName,
        '/install /quiet /norestart',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        'Windows App Runtime 2.5 ARM64',
        WindowsAppRuntimeInstallerName,
        '--quiet',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyInstallPage.Hide;
  end;
end;

procedure InitializeWizard;
begin
  DependencyDownloadPage := CreateDownloadPage(ExpandConstant('{cm:DependencyDownloadTitle}'), ExpandConstant('{cm:DependencyDownloadSubtitle}'), nil);
  DependencyDownloadPage.ShowBaseNameInsteadOfUrl := True;
  DependencyInstallPage := CreateOutputProgressPage(ExpandConstant('{cm:DependencyInstallTitle}'), ExpandConstant('{cm:DependencyInstallSubtitle}'));
end;

function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): String;
begin
  Result := '';
#if DeskBoxBundledRuntime
  NeedsRestart := False;
  DependenciesPrepared := True;
  Log('DeskBox bundled-runtime installer: external runtime dependency setup skipped.');
  Exit;
#endif
  if DependenciesPrepared then
    Exit;

  NeedsRestart := False;
  DetectDeskBoxDependencies;

  if not DownloadDeskBoxDependencies then
  begin
    Result := ExpandConstant('{cm:DependencyDownloadFailedSummary}');
    Exit;
  end;

  if not InstallDeskBoxDependencies(NeedsRestart) then
  begin
    Result := ExpandConstant('{cm:DependencyInstallFailedSummary}');
    Exit;
  end;

  if NeedsRestart then
  begin
    Result := ExpandConstant('{cm:NeedsRestart}');
    Exit;
  end;

  WaitForDeskBoxDependencies;
  if ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime then
  begin
    Result := ExpandConstant('{cm:DependencyVerificationFailed}');
    Exit;
  end;

  DependenciesPrepared := True;
end;
