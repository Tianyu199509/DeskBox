[Code]
const
  DeskBoxAdminCleanupParam = '/ADMINCLEANUP=';
  DeskBoxLegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxLegacyWowUninstallKey = 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxAppCompatLayersKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers';
  DeskBoxLegacyExeName = 'DeskBox.exe';

var
  IsMigrationAdminCleanupMode: Boolean;
  MigrationAdminCleanupPath: string;

procedure ExitProcess(ExitCode: Integer);
  external 'ExitProcess@kernel32.dll stdcall';

function NormalizeDirPath(Path: string): string;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant(Path));
end;

function IsDefaultProgramFilesDeskBoxPath(Path: string): Boolean;
var
  NormalizedPath: string;
  ProgramFilesPath: string;
  ProgramFilesX86Path: string;
begin
  NormalizedPath := NormalizeDirPath(Path);
  ProgramFilesPath := NormalizeDirPath('{pf}\DeskBox');
  ProgramFilesX86Path := NormalizeDirPath('{pf32}\DeskBox');

  Result :=
    (CompareText(NormalizedPath, ProgramFilesPath) = 0) or
    (CompareText(NormalizedPath, ProgramFilesX86Path) = 0);
end;

function IsLegacyInstallPath(Path: string): Boolean;
begin
  Result :=
    (Path <> '') and
    IsDefaultProgramFilesDeskBoxPath(Path) and
    FileExists(AddBackslash(Path) + DeskBoxLegacyExeName);
end;

function TryReadLegacyInstallPathFromRegistry(var InstallPath: string): Boolean;
begin
  Result := False;
  InstallPath := '';

  if RegQueryStringValue(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  InstallPath := '';
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;
end;

function TryDetectLegacyInstallPath(var InstallPath: string): Boolean;
var
  CandidatePath: string;
begin
  Result := False;
  InstallPath := '';

  if TryReadLegacyInstallPathFromRegistry(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  CandidatePath := ExpandConstant('{pf}\DeskBox');
  if IsLegacyInstallPath(CandidatePath) then
  begin
    InstallPath := CandidatePath;
    Result := True;
    Exit;
  end;

  CandidatePath := ExpandConstant('{pf32}\DeskBox');
  if IsLegacyInstallPath(CandidatePath) then
  begin
    InstallPath := CandidatePath;
    Result := True;
    Exit;
  end;
end;

function TryReadAdminCleanupMode: Boolean;
var
  I: Integer;
  Param: string;
begin
  Result := False;
  MigrationAdminCleanupPath := '';

  for I := 1 to ParamCount do
  begin
    Param := ParamStr(I);
    if CompareText(Copy(Param, 1, Length(DeskBoxAdminCleanupParam)), DeskBoxAdminCleanupParam) = 0 then
    begin
      MigrationAdminCleanupPath := Copy(Param, Length(DeskBoxAdminCleanupParam) + 1, MaxInt);
      Result := IsLegacyInstallPath(MigrationAdminCleanupPath);
      Exit;
    end;
  end;
end;

procedure StopLegacyDeskBoxProcess;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM DeskBox.exe /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Log('DeskBox migration taskkill exit code: ' + IntToStr(ResultCode));
end;

procedure DeleteShortcutIfExists(Path: string);
begin
  if FileExists(Path) then
  begin
    if DeleteFile(Path) then
      Log('DeskBox migration deleted shortcut: ' + Path)
    else
      Log('DeskBox migration failed to delete shortcut: ' + Path);
  end;
end;

procedure DeleteAppCompatLayerValue(RootKey: Integer; ExePath: string);
var
  Value: string;
begin
  if ExePath = '' then
    Exit;

  if RegQueryStringValue(RootKey, DeskBoxAppCompatLayersKey, ExePath, Value) then
  begin
    if Pos('RUNASADMIN', Uppercase(Value)) > 0 then
    begin
      if RegDeleteValue(RootKey, DeskBoxAppCompatLayersKey, ExePath) then
        Log('DeskBox migration removed AppCompat RUNASADMIN value: ' + ExePath)
      else
        Log('DeskBox migration failed to remove AppCompat value: ' + ExePath);
    end;
  end;
end;

procedure CleanupCurrentUserAppCompatFlags(LegacyInstallPath: string);
begin
  if LegacyInstallPath <> '' then
  begin
    DeleteAppCompatLayerValue(
      HKEY_CURRENT_USER,
      AddBackslash(LegacyInstallPath) + DeskBoxLegacyExeName);
  end;

  DeleteAppCompatLayerValue(
    HKEY_CURRENT_USER,
    ExpandConstant('{localappdata}\Programs\DeskBox\DeskBox.exe'));
end;

function PerformMigrationAdminCleanup(LegacyInstallPath: string): Boolean;
var
  LegacyExePath: string;
begin
  Result := False;

  if not IsLegacyInstallPath(LegacyInstallPath) then
  begin
    Log('DeskBox migration rejected cleanup path: ' + LegacyInstallPath);
    Exit;
  end;

  LegacyExePath := AddBackslash(LegacyInstallPath) + DeskBoxLegacyExeName;
  StopLegacyDeskBoxProcess;

  DeleteShortcutIfExists(ExpandConstant('{commonprograms}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{commondesktop}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{commonstartup}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{commonappdata}\Microsoft\Windows\Start Menu\Programs\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{commonappdata}\Microsoft\Windows\Start Menu\Programs\Startup\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{userprograms}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{userdesktop}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{userstartup}\DeskBox.lnk'));
  DeleteShortcutIfExists(ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DeskBox.lnk'));

  DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, LegacyExePath);

  if RegKeyExists(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey) then
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey);

  if RegKeyExists(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey) then
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey);

  if DirExists(LegacyInstallPath) then
  begin
    if not DelTree(LegacyInstallPath, True, True, True) then
    begin
      Log('DeskBox migration failed to remove legacy directory: ' + LegacyInstallPath);
      Log('DeskBox migration will continue because user-scope install can still proceed.');
    end;
  end;

  Result := True;
end;

function RunMigrationAdminCleanup(LegacyInstallPath: string): Boolean;
var
  ResultCode: Integer;
  Parameters: string;
begin
  Parameters :=
    '/SP- /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "' +
    DeskBoxAdminCleanupParam + LegacyInstallPath + '"';

  Log('DeskBox migration launching admin cleanup for: ' + LegacyInstallPath);
  if not ShellExec(
      'runas',
      ExpandConstant('{srcexe}'),
      Parameters,
      '',
      SW_SHOW,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    Log('DeskBox migration admin cleanup could not be launched.');
    Result := False;
    Exit;
  end;

  Log('DeskBox migration admin cleanup exit code: ' + IntToStr(ResultCode));
  Result := ResultCode = 0;
end;

function InitializeSetup: Boolean;
var
  LegacyInstallPath: string;
begin
  IsMigrationAdminCleanupMode := TryReadAdminCleanupMode;

  if IsMigrationAdminCleanupMode then
  begin
    if PerformMigrationAdminCleanup(MigrationAdminCleanupPath) then
      ExitProcess(0)
    else
      ExitProcess(1);

    Result := False;
    Exit;
  end;

  Result := True;
  if TryDetectLegacyInstallPath(LegacyInstallPath) then
  begin
    Log('DeskBox migration detected legacy install: ' + LegacyInstallPath);
    CleanupCurrentUserAppCompatFlags(LegacyInstallPath);

    if not RunMigrationAdminCleanup(LegacyInstallPath) then
      Log('DeskBox migration admin cleanup failed; continuing with current-user install.');

    CleanupCurrentUserAppCompatFlags(LegacyInstallPath);
  end
  else
  begin
    CleanupCurrentUserAppCompatFlags('');
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsMigrationAdminCleanupMode;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  ResultCode: Integer;
begin
  Result := '';

  // Explicitly terminate DeskBox before file copy. Restart Manager alone
  // cannot always close a tray-first app with multiple top-level windows,
  // causing the "close applications" dialog to hang and eventually timeout.
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM DeskBox.exe /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Log('DeskBox taskkill exit code: ' + IntToStr(ResultCode));

  // Give the process time to fully exit before Restart Manager runs.
  Sleep(2000);

  Log('DeskBox process termination completed.');
end;
