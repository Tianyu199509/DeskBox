[Code]
const
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.9/dotnet-runtime-10.0.9-win-arm64.exe';
  DotNetRuntimeFallbackUrl = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-arm64.exe';
  DotNetRuntimeInstallerName = 'dotnet-runtime-10.0.9-win-arm64.exe';
  WindowsAppRuntimeUrl = 'https://download.microsoft.com/download/5e0f2e92-f3ef-4023-97f0-bd57018a478c/WindowsAppRuntimeInstall-arm64.exe';
  WindowsAppRuntimeFallbackUrl = 'https://aka.ms/windowsappsdk/2.2/2.2.0/windowsappruntimeinstall-arm64.exe';
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

function IsDotNet10RuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{pf}\dotnet\shared\Microsoft.NETCore.App\*'), FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          if IsMajorVersion(FindRec.Name, 10) then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsWindowsAppRuntime22Installed: Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "$pkg = Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''ARM64'' -and [version]$_.Version -ge [version]''2.2.0.0'' } | Select-Object -First 1; if (-not $pkg) { $pkg = Get-AppxPackage -AllUsers -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''ARM64'' -and [version]$_.Version -ge [version]''2.2.0.0'' } | Select-Object -First 1 }; if ($pkg) { exit 0 } exit 1"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

procedure DetectDeskBoxDependencies;
begin
  ShouldInstallDotNetRuntime := not IsDotNet10RuntimeInstalled;
  ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime22Installed;

  Log('DeskBox dependency check: dotnet10Missing=' + IntToStr(Integer(ShouldInstallDotNetRuntime)));
  Log('DeskBox dependency check: windowsAppRuntimeMissing=' + IntToStr(Integer(ShouldInstallWindowsAppRuntime)));
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
      ErrorMessage := 'Download was cancelled.';
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
      ErrorMessage := 'Download was cancelled.'
    else
      ErrorMessage :=
        DisplayName + ' download failed.' + #13#10 +
        'Primary URL: ' + Url + #13#10 +
        'Fallback URL: ' + FallbackUrl + #13#10 +
        'Error: ' + GetExceptionMessage;

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
      DependencyDownloadPage.Msg1Label.Caption := 'Downloading .NET 10 Runtime ARM64...';
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
      DependencyDownloadPage.Msg1Label.Caption := 'Downloading Windows App Runtime 2.2 ARM64...';
      if not DownloadDependencyWithProgress(
        'Windows App Runtime 2.2 ARM64',
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
    'Installing ' + DisplayName + '...',
    'This may take a few minutes. Please do not close this window.');

  if not ShellExec('runas', InstallerPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(DisplayName + ' installer could not be started with administrator permission. Please allow the Windows prompt and try again.', mbCriticalError, MB_OK, IDOK);
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
      DisplayName + ' installation failed. Exit code: ' + IntToStr(ResultCode) + '.' + #13#10 +
      'Please confirm this Windows version is supported, or install the dependency manually and run DeskBox setup again.',
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
        'Windows App Runtime 2.2 ARM64',
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
  DependencyDownloadPage := CreateDownloadPage('Preparing DeskBox runtime', 'Downloading missing runtime dependencies.', nil);
  DependencyDownloadPage.ShowBaseNameInsteadOfUrl := True;
  DependencyInstallPage := CreateOutputProgressPage('Preparing DeskBox runtime', 'Installing missing runtime dependencies.');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedsRestart: Boolean;
begin
  Result := True;

  if (CurPageID <> wpReady) or DependenciesPrepared then
    Exit;

  NeedsRestart := False;
  DetectDeskBoxDependencies;

  if not DownloadDeskBoxDependencies then
  begin
    Result := False;
    Exit;
  end;

  if not InstallDeskBoxDependencies(NeedsRestart) then
  begin
    Result := False;
    Exit;
  end;

  DependenciesPrepared := True;

  if NeedsRestart then
  begin
    SuppressibleMsgBox('Runtime dependencies were installed, but Windows needs to restart. Restart your PC, then run DeskBox setup again.', mbInformation, MB_OK, IDOK);
    Result := False;
  end;
end;
