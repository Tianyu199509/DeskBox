[Code]
const
  DeskBoxProductAppId = '{5E052824-3456-427E-9759-3BCAE078A1D3}';
  DeskBoxLegacyExeName = 'DeskBox.exe';
  DeskBoxUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxWowUninstallKey = 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxInstallStateKey = 'Software\DeskBox\DirectInstall';

var
  DirectInstallUpgrade: Boolean;
  ExistingInstallPath: string;
  ExistingInstallCount: Integer;
  ExistingInstallCandidates: string;

function NormalizeDirPath(Path: string): string;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant(Trim(Path)));
end;

function SameInstallPath(LeftPath: string; RightPath: string): Boolean;
begin
  Result := CompareText(NormalizeDirPath(LeftPath), NormalizeDirPath(RightPath)) = 0;
end;

function ExtractExecutablePath(CommandLine: string): string;
var
  RemainingText: string;
  EndPosition: Integer;
begin
  Result := '';
  CommandLine := Trim(CommandLine);
  if CommandLine = '' then
    Exit;

  if Copy(CommandLine, 1, 1) = '"' then
  begin
    RemainingText := Copy(CommandLine, 2, MaxInt);
    EndPosition := Pos('"', RemainingText);
    if EndPosition > 0 then
      Result := Copy(RemainingText, 1, EndPosition - 1);
    Exit;
  end;

  EndPosition := Pos(' ', CommandLine);
  if EndPosition > 0 then
    Result := Copy(CommandLine, 1, EndPosition - 1)
  else
    Result := CommandLine;
end;

function IsDeskBoxInstallPath(Path: string): Boolean;
var
  NormalizedPath: string;
begin
  NormalizedPath := NormalizeDirPath(Path);
  Result :=
    (NormalizedPath <> '') and
    DirExists(NormalizedPath) and
    FileExists(AddBackslash(NormalizedPath) + DeskBoxLegacyExeName);
end;

function IsRegisteredDeskBoxInstallPath(Path: string): Boolean;
var
  NormalizedPath: string;
begin
  NormalizedPath := NormalizeDirPath(Path);
  Result :=
    (NormalizedPath <> '') and
    DirExists(NormalizedPath) and
    (IsDeskBoxInstallPath(NormalizedPath) or
     FileExists(AddBackslash(NormalizedPath) + 'DeskBox.Updater.exe') or
     FileExists(AddBackslash(NormalizedPath) + 'DeskBox.runtimeconfig.json'));
end;

function InstallCandidateListContains(Path: string): Boolean;
var
  Needle: string;
  Haystack: string;
begin
  Needle := Uppercase(#13#10 + NormalizeDirPath(Path) + #13#10);
  Haystack := Uppercase(#13#10 + ExistingInstallCandidates + #13#10);
  Result := Pos(Needle, Haystack) > 0;
end;

procedure AddInstallCandidate(Path: string; Source: string; RequireExecutable: Boolean);
var
  NormalizedPath: string;
begin
  NormalizedPath := NormalizeDirPath(Path);
  if (NormalizedPath = '') or InstallCandidateListContains(NormalizedPath) then
    Exit;

  if RequireExecutable then
  begin
    if not IsDeskBoxInstallPath(NormalizedPath) then
      Exit;
  end
  else if not IsRegisteredDeskBoxInstallPath(NormalizedPath) then
    Exit;

  if ExistingInstallCandidates = '' then
    ExistingInstallCandidates := NormalizedPath
  else
    ExistingInstallCandidates := ExistingInstallCandidates + #13#10 + NormalizedPath;

  ExistingInstallCount := ExistingInstallCount + 1;
  if ExistingInstallPath = '' then
    ExistingInstallPath := NormalizedPath;

  Log('DeskBox install candidate detected from ' + Source + ': ' + NormalizedPath);
end;

procedure AddRegistryInstallCandidate(RootKey: Integer; KeyName: string; Source: string);
var
  InstallPath: string;
begin
  InstallPath := '';
  if RegQueryStringValue(RootKey, KeyName, 'InstallLocation', InstallPath) then
    AddInstallCandidate(InstallPath, Source, False);
end;

function TryReadShortcutTarget(ShortcutPath: string; var TargetPath: string): Boolean;
var
  ShellObject: Variant;
  ShortcutObject: Variant;
begin
  Result := False;
  TargetPath := '';
  if not FileExists(ShortcutPath) then
    Exit;

  try
    ShellObject := CreateOleObject('WScript.Shell');
    ShortcutObject := ShellObject.CreateShortcut(ShortcutPath);
    TargetPath := Trim(Format('%s', [ShortcutObject.TargetPath]));
    Result := TargetPath <> '';
  except
    Log('DeskBox could not inspect shortcut: ' + ShortcutPath);
  end;
end;

function ShortcutTargetsInstall(ShortcutPath: string; InstallPath: string): Boolean;
var
  TargetPath: string;
begin
  Result :=
    TryReadShortcutTarget(ShortcutPath, TargetPath) and
    SameInstallPath(ExtractFileDir(TargetPath), InstallPath) and
    (CompareText(ExtractFileName(TargetPath), DeskBoxLegacyExeName) = 0);
end;

procedure AddShortcutInstallCandidate(ShortcutPath: string);
var
  TargetPath: string;
  TargetDirectory: string;
begin
  if not TryReadShortcutTarget(ShortcutPath, TargetPath) then
    Exit;

  if CompareText(ExtractFileName(TargetPath), DeskBoxLegacyExeName) <> 0 then
    Exit;

  TargetDirectory := ExtractFileDir(TargetPath);
  AddInstallCandidate(TargetDirectory, 'shortcut ' + ShortcutPath, True);
end;

procedure CollectDirectInstallCandidates;
begin
  ExistingInstallPath := '';
  ExistingInstallCount := 0;
  ExistingInstallCandidates := '';

  AddRegistryInstallCandidate(HKEY_CURRENT_USER, DeskBoxUninstallKey, 'HKCU uninstall');
  AddRegistryInstallCandidate(HKEY_CURRENT_USER, DeskBoxWowUninstallKey, 'HKCU 32-bit uninstall');
  AddRegistryInstallCandidate(HKEY_LOCAL_MACHINE, DeskBoxUninstallKey, 'HKLM uninstall');
  AddRegistryInstallCandidate(HKEY_LOCAL_MACHINE, DeskBoxWowUninstallKey, 'HKLM 32-bit uninstall');
  AddRegistryInstallCandidate(HKEY_CURRENT_USER, DeskBoxInstallStateKey, 'HKCU DeskBox install state');
  AddRegistryInstallCandidate(HKEY_LOCAL_MACHINE, DeskBoxInstallStateKey, 'HKLM DeskBox install state');

  AddInstallCandidate(ExpandConstant('{localappdata}\Programs\DeskBox'), 'current default path', True);
  AddInstallCandidate(ExpandConstant('{localappdata}\DeskBox'), 'legacy user path', True);
  AddInstallCandidate(ExpandConstant('{commonpf}\DeskBox'), 'default Program Files path', True);
  AddInstallCandidate(ExpandConstant('{commonpf32}\DeskBox'), 'default Program Files (x86) path', True);

  AddShortcutInstallCandidate(ExpandConstant('{userprograms}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{commonprograms}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{userdesktop}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{commondesktop}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{userstartup}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{commonstartup}\DeskBox.lnk'));
  AddShortcutInstallCandidate(ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DeskBox.lnk'));
end;

function TryReadExplicitDirectory(var DirectoryPath: string): Boolean;
var
  Index: Integer;
  Parameter: string;
  Prefix: string;
begin
  Result := False;
  DirectoryPath := '';
  Prefix := '/DIR=';

  for Index := 1 to ParamCount do
  begin
    Parameter := ParamStr(Index);
    if CompareText(Copy(Parameter, 1, Length(Prefix)), Prefix) = 0 then
    begin
      DirectoryPath := Copy(Parameter, Length(Prefix) + 1, MaxInt);
      if (Length(DirectoryPath) >= 2) and
         (Copy(DirectoryPath, 1, 1) = '"') and
         (Copy(DirectoryPath, Length(DirectoryPath), 1) = '"') then
        DirectoryPath := Copy(DirectoryPath, 2, Length(DirectoryPath) - 2);

      DirectoryPath := Trim(DirectoryPath);
      Result := DirectoryPath <> '';
      Exit;
    end;
  end;
end;

function GetDefaultInstallDir(Param: string): string;
begin
  if DirectInstallUpgrade and (ExistingInstallPath <> '') then
    Result := ExistingInstallPath
  else
    Result := ExpandConstant('{autopf}\DeskBox');
end;

function GetInstallScopeName(Param: string): string;
begin
  if IsAdminInstallMode then
    Result := 'all-users'
  else
    Result := 'current-user';
end;

function BuildInstallCandidateList: string;
begin
  Result := ExistingInstallCandidates;
  StringChangeEx(Result, #13#10, #13#10 + '  ', True);
  if Result <> '' then
    Result := '  ' + Result;
end;

function ShouldSuppressDirectInstallMessages: Boolean;
var
  Index: Integer;
  Parameter: string;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    Parameter := Uppercase(ParamStr(Index));
    if (Parameter = '/VERYSILENT') or (Parameter = '/SUPPRESSMSGBOXES') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

const
  // PE machine field (IMAGE_FILE_HEADER.Machine) of an ARM64 image. The
  // guard below only blocks this one direction: the x64 installer is the
  // default download everywhere, so it is the one that lands on ARM64
  // machines by accident; the deliberate x64 -> ARM64 conversion stays open.
  DESKBOX_PE_MACHINE_ARM64 = $AA64;

function ExpectedInstallerPeMachine: Integer;
begin
  Result := {#DeskBoxInstallerPeMachineHex};
end;

// Reads the PE machine field of an executable. Returns False when the file
// is missing, too short, or not a PE image — callers treat that as
// "unknown" and never block on it. TFileStream.Read stores each byte in the
// corresponding character of the string, so high bytes survive as chars
// U+0080..U+00FF and Ord() recovers the exact byte value.
function TryReadPeMachine(FileName: string; var Machine: Integer): Boolean;
var
  Stream: TFileStream;
  S: string;
  PeOffset: Integer;
begin
  Result := False;
  Machine := 0;
  if not FileExists(FileName) then
    Exit;
  try
    Stream := TFileStream.Create(FileName, fmOpenRead or fmShareDenyNone);
    try
      if Stream.Size < $40 then
        Exit;
      Stream.Seek($3C, soFromBeginning);
      S := '';
      if Stream.Read(S, 4) <> 4 then
        Exit;
      PeOffset := Ord(S[1]) or (Ord(S[2]) shl 8) or (Ord(S[3]) shl 16) or
        (Ord(S[4]) shl 24);
      if (PeOffset < 0) or (PeOffset + 6 > Stream.Size) then
        Exit;
      Stream.Seek(PeOffset, soFromBeginning);
      S := '';
      if Stream.Read(S, 6) <> 6 then
        Exit;
      if (S[1] <> 'P') or (S[2] <> 'E') or (S[3] <> #0) or (S[4] <> #0) then
        Exit;
      Machine := Ord(S[5]) or (Ord(S[6]) shl 8);
      Result := True;
    finally
      Stream.Free;
    end;
  except
    Result := False;
    Machine := 0;
  end;
end;

function PrepareDirectInstallPlan: Boolean;
var
  ExplicitDirectory: string;
  MessageText: string;
  ExistingMachine: Integer;
begin
  Result := False;
  DirectInstallUpgrade := False;
  CollectDirectInstallCandidates;

  if TryReadExplicitDirectory(ExplicitDirectory) and IsDeskBoxInstallPath(ExplicitDirectory) then
    AddInstallCandidate(ExplicitDirectory, 'explicit /DIR path', True);

  if ExistingInstallCount > 1 then
  begin
    MessageText :=
      ExpandConstant('{cm:MultipleInstallationsTitle}') + #13#10#13#10 +
      FmtMessage(ExpandConstant('{cm:MultipleInstallationsBody}'), [BuildInstallCandidateList]) + #13#10#13#10 +
      ExpandConstant('{cm:MultipleInstallationsFooter}');
    Log('DeskBox installation blocked because multiple installations were detected: ' + ExistingInstallCandidates);
    if not ShouldSuppressDirectInstallMessages then
      MsgBox(MessageText, mbError, MB_OK);
    Exit;
  end;

  if ExistingInstallCount = 1 then
  begin
    DirectInstallUpgrade := True;
    if TryReadExplicitDirectory(ExplicitDirectory) and
       (ExplicitDirectory <> '') and
       (not SameInstallPath(ExplicitDirectory, ExistingInstallPath)) then
    begin
      MessageText := FmtMessage(ExpandConstant('{cm:UpgradeDirectoryMismatch}'), [ExistingInstallPath, ExplicitDirectory]);
      Log('DeskBox installation blocked because /DIR does not match the existing install: ' + ExplicitDirectory);
      if not ShouldSuppressDirectInstallMessages then
        MsgBox(MessageText, mbError, MB_OK);
      DirectInstallUpgrade := False;
      Exit;
    end;

    // Architecture guard: silently replacing a native ARM64 install with
    // the emulated x64 build strands every future update on the x64 channel
    // (the running x64 process always selects x64 assets). The deliberate
    // conversion direction — the ARM64 installer over an x64 install —
    // stays allowed.
    if (ExpectedInstallerPeMachine <> DESKBOX_PE_MACHINE_ARM64) and
       TryReadPeMachine(AddBackslash(ExistingInstallPath) + '{#MyAppExeName}', ExistingMachine) and
       (ExistingMachine = DESKBOX_PE_MACHINE_ARM64) then
    begin
      MessageText := FmtMessage(ExpandConstant('{cm:ArchitectureMismatchBody}'), [ExistingInstallPath]);
      Log('DeskBox installation blocked: the existing DeskBox.exe is the native ARM64 build; this installer is x64.');
      if not ShouldSuppressDirectInstallMessages then
        MsgBox(MessageText, mbError, MB_OK);
      DirectInstallUpgrade := False;
      Exit;
    end;

    Log('DeskBox upgrade locked to existing install directory: ' + ExistingInstallPath);
  end
  else
    Log('DeskBox installation plan: first install.');

  Result := True;
end;

function EscapePowerShellString(Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

function StopDeskBoxProcessesAtPath(InstallPath: string): Boolean;
var
  PowerShellPath: string;
  CommandLine: string;
  Parameters: string;
  ResultCode: Integer;
begin
  Result := True;
  InstallPath := NormalizeDirPath(InstallPath);
  if InstallPath = '' then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
  begin
    Log('DeskBox could not find Windows PowerShell for path-scoped process shutdown.');
    Result := False;
    Exit;
  end;

  CommandLine :=
    '$target = [System.IO.Path]::GetFullPath(''' + EscapePowerShellString(InstallPath) + ''').TrimEnd(''\''); ' +
    'Get-CimInstance Win32_Process | ' +
    'Where-Object { (@(''DeskBox.exe'', ''DeskBox.ThumbnailProxy.exe'') -icontains $_.Name) -and $_.ExecutablePath -and ' +
    '([System.IO.Path]::GetDirectoryName($_.ExecutablePath)).TrimEnd(''\'') -ieq $target } | ' +
    'ForEach-Object { Stop-Process -Id $_.ProcessId -Force }';
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + CommandLine + '"';

  Log('DeskBox stopping processes under: ' + InstallPath);
  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('DeskBox path-scoped process shutdown could not be started.');
    Result := False;
    Exit;
  end;

  Log('DeskBox path-scoped process shutdown exit code: ' + IntToStr(ResultCode));
  Result := ResultCode = 0;
end;
