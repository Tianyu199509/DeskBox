[Code]
const
  DeskBoxProductAppId = '{5E052824-3456-427E-9759-3BCAE078A1D3}';
  DeskBoxLegacyExeName = 'DeskBox.exe';
  DeskBoxUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxWowUninstallKey = 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1';
  DeskBoxInstallStateKey = 'Software\DeskBox\DirectInstall';
  DeskBoxInstallStartupRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  DeskBoxInstallWowStartupRunKey = 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run';
  DeskBoxInstallStartupTaskPrefix = 'DeskBox User Startup';

var
  DirectInstallUpgrade: Boolean;
  ExistingInstallPath: string;
  ExistingInstallCount: Integer;
  ExistingInstallCandidates: string;

function NormalizeDirPath(Path: string): string;
var
  TrimmedPath: string;
begin
  TrimmedPath := Trim(Path);
  if TrimmedPath = '' then
  begin
    Result := '';
    Exit;
  end;

  if Pos('{', TrimmedPath) > 0 then
  begin
    try
      TrimmedPath := ExpandConstant(TrimmedPath);
    except
      Log('DeskBox preserved literal brace path without constant expansion: ' + TrimmedPath);
    end;
  end;

  Result := RemoveBackslashUnlessRoot(TrimmedPath);
end;

function SameInstallPath(LeftPath: string; RightPath: string): Boolean;
begin
  Result := CompareText(NormalizeDirPath(LeftPath), NormalizeDirPath(RightPath)) = 0;
end;

function IsDirectoryWithinRoot(Path: string; RootPath: string): Boolean;
var
  NormalizedPath: string;
  NormalizedRoot: string;
  RootPrefix: string;
begin
  Result := False;
  NormalizedPath := NormalizeDirPath(Path);
  NormalizedRoot := NormalizeDirPath(RootPath);
  if (NormalizedPath = '') or (NormalizedRoot = '') then
    Exit;

  if CompareText(NormalizedPath, NormalizedRoot) = 0 then
  begin
    Result := True;
    Exit;
  end;

  RootPrefix := AddBackslash(NormalizedRoot);
  Result :=
    (Length(NormalizedPath) > Length(RootPrefix)) and
    (CompareText(Copy(NormalizedPath, 1, Length(RootPrefix)), RootPrefix) = 0);
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

  if FileExists(CommandLine) then
  begin
    Result := CommandLine;
    Exit;
  end;

  EndPosition := Pos(' ', CommandLine);
  if EndPosition > 0 then
    Result := Copy(CommandLine, 1, EndPosition - 1)
  else
    Result := CommandLine;
end;

function UnquoteExecutablePath(RawPath: string): string;
begin
  Result := Trim(RawPath);
  if (Length(Result) >= 2) and
     (Copy(Result, 1, 1) = '"') and
     (Copy(Result, Length(Result), 1) = '"') then
    Result := Trim(Copy(Result, 2, Length(Result) - 2))
  else if Copy(Result, 1, 1) = '"' then
    Result := ExtractExecutablePath(Result);
end;

function EscapePowerShellString(Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '"', '', True);
  StringChangeEx(Result, '''', '''''', True);
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

function GetUsersRootPath: string;
var
  PublicProfilePath: string;
  CurrentProfilePath: string;
  CandidateRoot: string;
begin
  Result := '';
  CandidateRoot := '';
  PublicProfilePath := NormalizeDirPath(ExpandConstant('{%PUBLIC}'));
  if PublicProfilePath <> '' then
    CandidateRoot := NormalizeDirPath(ExtractFileDir(PublicProfilePath));

  if (CandidateRoot = '') or
     (CompareText(RemoveBackslashUnlessRoot(ExtractFileDrive(CandidateRoot)), CandidateRoot) = 0) then
  begin
    CurrentProfilePath := NormalizeDirPath(ExpandConstant('{%USERPROFILE}'));
    if CurrentProfilePath <> '' then
      CandidateRoot := NormalizeDirPath(ExtractFileDir(CurrentProfilePath));
  end;

  if (CandidateRoot <> '') and
     (CompareText(RemoveBackslashUnlessRoot(ExtractFileDrive(CandidateRoot)), CandidateRoot) <> 0) and
     (not IsDirectoryWithinRoot(CandidateRoot, ExpandConstant('{win}'))) and
     (not SameInstallPath(CandidateRoot, ExpandConstant('{commonpf}'))) and
     (not SameInstallPath(CandidateRoot, ExpandConstant('{commonpf32}'))) and
     (not SameInstallPath(CandidateRoot, ExpandConstant('{commonappdata}'))) then
    Result := CandidateRoot;
end;

function IsSharedOrTemplateProfileDirName(FolderName: string): Boolean;
begin
  Result :=
    (FolderName = '') or
    (FolderName = '.') or
    (FolderName = '..') or
    (CompareText(FolderName, 'Public') = 0) or
    (CompareText(FolderName, 'Default') = 0) or
    (CompareText(FolderName, 'Default User') = 0) or
    (CompareText(FolderName, 'All Users') = 0);
end;

function TryGetUserProfileRootForPath(CandidatePath: string; var ProfileRoot: string): Boolean;
var
  NormalizedPath: string;
  UsersRoot: string;
  PublicRoot: string;
  RelativePath: string;
  AccountFolder: string;
  SlashPosition: Integer;
begin
  Result := False;
  ProfileRoot := '';
  NormalizedPath := NormalizeDirPath(CandidatePath);
  UsersRoot := GetUsersRootPath;
  if (NormalizedPath = '') or (UsersRoot = '') then
    Exit;

  PublicRoot := NormalizeDirPath(ExpandConstant('{%PUBLIC}'));
  if (PublicRoot <> '') and IsDirectoryWithinRoot(NormalizedPath, PublicRoot) then
    Exit;

  if SameInstallPath(NormalizedPath, UsersRoot) or
     (not IsDirectoryWithinRoot(NormalizedPath, UsersRoot)) then
    Exit;

  RelativePath := Copy(NormalizedPath, Length(AddBackslash(UsersRoot)) + 1, MaxInt);
  SlashPosition := Pos('\', RelativePath);
  if SlashPosition > 0 then
    AccountFolder := Copy(RelativePath, 1, SlashPosition - 1)
  else
    AccountFolder := RelativePath;

  if IsSharedOrTemplateProfileDirName(AccountFolder) then
    Exit;

  ProfileRoot := AddBackslash(UsersRoot) + AccountFolder;
  Result := True;
end;

function IsPerUserProfileInstallPath(CandidatePath: string): Boolean;
var
  ProfileRoot: string;
  CurrentProfilePath: string;
begin
  CurrentProfilePath := NormalizeDirPath(ExpandConstant('{%USERPROFILE}'));
  Result :=
    IsDirectoryWithinRoot(CandidatePath, ExpandConstant('{localappdata}')) or
    IsDirectoryWithinRoot(CandidatePath, ExpandConstant('{userappdata}')) or
    ((CurrentProfilePath <> '') and
     (not IsDirectoryWithinRoot(CurrentProfilePath, ExpandConstant('{win}'))) and
     IsDirectoryWithinRoot(CandidatePath, CurrentProfilePath)) or
    TryGetUserProfileRootForPath(CandidatePath, ProfileRoot);
end;

function IsOtherUserProfileInstallPath(CandidatePath: string): Boolean;
var
  ProfileRoot: string;
  CurrentProfilePath: string;
begin
  Result := False;
  CurrentProfilePath := NormalizeDirPath(ExpandConstant('{%USERPROFILE}'));
  if (CurrentProfilePath = '') or IsDirectoryWithinRoot(CurrentProfilePath, ExpandConstant('{win}')) then
    Exit;

  if TryGetUserProfileRootForPath(CandidatePath, ProfileRoot) then
    Result := not SameInstallPath(ProfileRoot, CurrentProfilePath);
end;

function PathContainsSegment(UpperBoundedPath: string; Segment: string): Boolean;
begin
  Result := Pos(Uppercase(Segment), UpperBoundedPath) > 0;
end;

function IsExcludedHeuristicInstallPath(Path: string): Boolean;
var
  NormalizedPath: string;
  UpperBoundedPath: string;
  TempRoot: string;
  SetupTempRoot: string;
begin
  Result := False;
  NormalizedPath := NormalizeDirPath(Path);
  if NormalizedPath = '' then
  begin
    Result := True;
    Exit;
  end;

  if (not IsAdminInstallMode) and IsOtherUserProfileInstallPath(NormalizedPath) then
  begin
    Result := True;
    Exit;
  end;

  UpperBoundedPath := Uppercase(AddBackslash(NormalizedPath));

  // Exclude Microsoft Store / MSIX package and execution alias directories.
  if IsDirectoryWithinRoot(NormalizedPath, ExpandConstant('{commonpf}\WindowsApps')) or
     IsDirectoryWithinRoot(NormalizedPath, ExpandConstant('{localappdata}\Microsoft\WindowsApps')) or
     PathContainsSegment(UpperBoundedPath, '\WINDOWSAPPS\') then
  begin
    Result := True;
    Exit;
  end;

  // Exclude temporary directories and DeskBox self-update staging folders.
  TempRoot := NormalizeDirPath(ExpandConstant('{%TEMP}'));
  if (TempRoot <> '') and IsDirectoryWithinRoot(NormalizedPath, TempRoot) then
  begin
    Result := True;
    Exit;
  end;

  SetupTempRoot := '';
  if not IsUninstaller then
  begin
    try
      SetupTempRoot := NormalizeDirPath(ExpandConstant('{tmp}'));
    except
      SetupTempRoot := '';
    end;
  end;
  if (SetupTempRoot <> '') and IsDirectoryWithinRoot(NormalizedPath, SetupTempRoot) then
  begin
    Result := True;
    Exit;
  end;

  if PathContainsSegment(UpperBoundedPath, '\APPDATA\LOCAL\TEMP\') or
     PathContainsSegment(UpperBoundedPath, '\APPDATA\LOCAL\DESKBOX\UPDATES\') then
  begin
    Result := True;
    Exit;
  end;

  // Exclude repository build and packaging output directories.
  if PathContainsSegment(UpperBoundedPath, '\BIN\DEBUG\') or
     PathContainsSegment(UpperBoundedPath, '\BIN\RELEASE\') or
     PathContainsSegment(UpperBoundedPath, '\.ARTIFACTS\') or
     PathContainsSegment(UpperBoundedPath, '\ARTIFACTS\') or
     PathContainsSegment(UpperBoundedPath, '\APPPACKAGES\') then
  begin
    Result := True;
    Exit;
  end;
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

procedure AddHeuristicInstallCandidate(Path: string; Source: string; RequireExecutable: Boolean);
var
  NormalizedPath: string;
begin
  NormalizedPath := NormalizeDirPath(Path);
  if NormalizedPath = '' then
    Exit;

  if IsExcludedHeuristicInstallPath(NormalizedPath) then
  begin
    Log('DeskBox ignored excluded heuristic install candidate from ' + Source + ': ' + NormalizedPath);
    Exit;
  end;

  AddInstallCandidate(NormalizedPath, Source, RequireExecutable);
end;

procedure AddRegistryInstallCandidate(RootKey: Integer; KeyName: string; Source: string);
var
  InstallPath: string;
begin
  InstallPath := '';
  if RegQueryStringValue(RootKey, KeyName, 'InstallLocation', InstallPath) then
    AddInstallCandidate(InstallPath, Source, False);
end;

procedure AddStartupRunInstallCandidate(RootKey: Integer; KeyName: string; Source: string);
var
  CommandValue: string;
  ExecutablePath: string;
begin
  CommandValue := '';
  if not RegQueryStringValue(RootKey, KeyName, 'DeskBox', CommandValue) then
    Exit;

  ExecutablePath := ExtractExecutablePath(CommandValue);
  if (ExecutablePath = '') or
     (CompareText(ExtractFileName(ExecutablePath), DeskBoxLegacyExeName) <> 0) then
    Exit;

  AddHeuristicInstallCandidate(ExtractFileDir(ExecutablePath), Source, True);
end;

function IsLoadedUserAccountSid(SubkeyName: string): Boolean;
var
  UpperName: string;
  ClassesSuffix: string;
begin
  Result := False;
  UpperName := Uppercase(Trim(SubkeyName));
  if (UpperName = '') or
     (UpperName = '.DEFAULT') or
     (UpperName = 'S-1-5-18') or
     (UpperName = 'S-1-5-19') or
     (UpperName = 'S-1-5-20') then
    Exit;

  if Pos('S-1-', UpperName) <> 1 then
    Exit;

  ClassesSuffix := '_CLASSES';
  if (Length(UpperName) > Length(ClassesSuffix)) and
     (Copy(UpperName, Length(UpperName) - Length(ClassesSuffix) + 1, Length(ClassesSuffix)) = ClassesSuffix) then
    Exit;

  Result := True;
end;

function RegistryKeyMatchesInstallPath(
  RootKey: Integer;
  KeyName: string;
  CandidatePath: string): Boolean;
var
  RegisteredPath: string;
begin
  Result := False;
  RegisteredPath := '';
  if RegQueryStringValue(RootKey, KeyName, 'InstallLocation', RegisteredPath) then
    Result := SameInstallPath(RegisteredPath, CandidatePath);
end;

function HasMachineRegistrationForPath(CandidatePath: string): Boolean;
begin
  Result :=
    RegistryKeyMatchesInstallPath(HKEY_LOCAL_MACHINE, DeskBoxInstallStateKey, CandidatePath) or
    RegistryKeyMatchesInstallPath(HKEY_LOCAL_MACHINE, DeskBoxUninstallKey, CandidatePath) or
    RegistryKeyMatchesInstallPath(HKEY_LOCAL_MACHINE, DeskBoxWowUninstallKey, CandidatePath);
end;

function HasCurrentUserRegistrationForPath(CandidatePath: string): Boolean;
begin
  Result :=
    RegistryKeyMatchesInstallPath(HKEY_CURRENT_USER, DeskBoxInstallStateKey, CandidatePath) or
    RegistryKeyMatchesInstallPath(HKEY_CURRENT_USER, DeskBoxUninstallKey, CandidatePath) or
    RegistryKeyMatchesInstallPath(HKEY_CURRENT_USER, DeskBoxWowUninstallKey, CandidatePath);
end;

function HasLoadedUserHiveRegistrationForPath(CandidatePath: string): Boolean;
var
  UserSids: TArrayOfString;
  Index: Integer;
  Sid: string;
begin
  Result := False;
  if not RegGetSubkeyNames(HKEY_USERS, '', UserSids) then
    Exit;

  for Index := 0 to GetArrayLength(UserSids) - 1 do
  begin
    Sid := UserSids[Index];
    if IsLoadedUserAccountSid(Sid) then
    begin
      if RegistryKeyMatchesInstallPath(HKEY_USERS, Sid + '\' + DeskBoxInstallStateKey, CandidatePath) or
         RegistryKeyMatchesInstallPath(HKEY_USERS, Sid + '\' + DeskBoxUninstallKey, CandidatePath) or
         RegistryKeyMatchesInstallPath(HKEY_USERS, Sid + '\' + DeskBoxWowUninstallKey, CandidatePath) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
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

procedure AddHeuristicShortcutInstallCandidate(ShortcutPath: string);
var
  TargetPath: string;
begin
  if not TryReadShortcutTarget(ShortcutPath, TargetPath) then
    Exit;

  if CompareText(ExtractFileName(TargetPath), DeskBoxLegacyExeName) <> 0 then
    Exit;

  AddHeuristicInstallCandidate(ExtractFileDir(TargetPath), 'profile shortcut ' + ShortcutPath, True);
end;

procedure AddLoadedUserHivesCandidates;
var
  UserSids: TArrayOfString;
  Index: Integer;
  Sid: string;
begin
  if not IsAdminInstallMode then
    Exit;

  if not RegGetSubkeyNames(HKEY_USERS, '', UserSids) then
    Exit;

  for Index := 0 to GetArrayLength(UserSids) - 1 do
  begin
    Sid := UserSids[Index];
    if IsLoadedUserAccountSid(Sid) then
    begin
      AddRegistryInstallCandidate(HKEY_USERS, Sid + '\' + DeskBoxUninstallKey, 'HKU ' + Sid + ' uninstall');
      AddRegistryInstallCandidate(HKEY_USERS, Sid + '\' + DeskBoxWowUninstallKey, 'HKU ' + Sid + ' 32-bit uninstall');
      AddRegistryInstallCandidate(HKEY_USERS, Sid + '\' + DeskBoxInstallStateKey, 'HKU ' + Sid + ' DeskBox install state');
      AddStartupRunInstallCandidate(HKEY_USERS, Sid + '\' + DeskBoxInstallStartupRunKey, 'HKU ' + Sid + ' Run startup');
    end;
  end;
end;

function IsInstallStartupTaskName(TaskName: string): Boolean;
begin
  Result :=
    (CompareText(TaskName, DeskBoxInstallStartupTaskPrefix) = 0) or
    (Pos(Uppercase(DeskBoxInstallStartupTaskPrefix + '-'), Uppercase(TaskName)) = 1);
end;

procedure AddStartupScheduledTaskCandidates;
var
  ScheduleService: Variant;
  RootFolder: Variant;
  RegisteredTasks: Variant;
  RegisteredTask: Variant;
  TaskDefinition: Variant;
  Actions: Variant;
  Action: Variant;
  ActionPath: string;
  ExecutablePath: string;
  TaskName: string;
  TaskIndex: Integer;
  ActionIndex: Integer;
begin
  try
    ScheduleService := CreateOleObject('Schedule.Service');
    ScheduleService.Connect;
    RootFolder := ScheduleService.GetFolder('\');
    RegisteredTasks := RootFolder.GetTasks(1);

    for TaskIndex := 1 to RegisteredTasks.Count do
    begin
      RegisteredTask := RegisteredTasks.Item(TaskIndex);
      TaskName := RegisteredTask.Name;
      if IsInstallStartupTaskName(TaskName) then
      begin
        TaskDefinition := RegisteredTask.Definition;
        Actions := TaskDefinition.Actions;
        for ActionIndex := 1 to Actions.Count do
        begin
          ActionPath := '';
          try
            Action := Actions.Item(ActionIndex);
            ActionPath := Action.Path;
          except
            ActionPath := '';
          end;

          ExecutablePath := UnquoteExecutablePath(ActionPath);
          if (ExecutablePath <> '') and
             (CompareText(ExtractFileName(ExecutablePath), DeskBoxLegacyExeName) = 0) then
            AddHeuristicInstallCandidate(
              ExtractFileDir(ExecutablePath),
              'startup scheduled task ' + TaskName,
              True);
        end;
      end;
    end;
  except
    Log('DeskBox startup scheduled tasks could not be inspected during install discovery.');
  end;
end;

procedure AddAllUserProfileInstallCandidates;
var
  UsersRoot: string;
  ProfileRoot: string;
  FindRec: TFindRec;
begin
  if not IsAdminInstallMode then
    Exit;

  UsersRoot := GetUsersRootPath;
  if (UsersRoot = '') or (not DirExists(UsersRoot)) then
    Exit;

  if FindFirst(AddBackslash(UsersRoot) + '*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (not IsSharedOrTemplateProfileDirName(FindRec.Name)) then
        begin
          ProfileRoot := AddBackslash(UsersRoot) + FindRec.Name;
          AddHeuristicInstallCandidate(
            ProfileRoot + '\AppData\Local\Programs\DeskBox',
            'user profile default path (' + FindRec.Name + ')',
            True);
          AddHeuristicInstallCandidate(
            ProfileRoot + '\AppData\Local\DeskBox',
            'user profile legacy path (' + FindRec.Name + ')',
            True);
          AddHeuristicShortcutInstallCandidate(ProfileRoot + '\Desktop\DeskBox.lnk');
          AddHeuristicShortcutInstallCandidate(
            ProfileRoot + '\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\DeskBox.lnk');
          AddHeuristicShortcutInstallCandidate(
            ProfileRoot + '\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\DeskBox.lnk');
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure AddRunningExecutableCandidate(RawExecutablePath: string; Source: string);
var
  ExecutablePath: string;
begin
  ExecutablePath := UnquoteExecutablePath(RawExecutablePath);
  if (ExecutablePath = '') or
     (CompareText(ExtractFileName(ExecutablePath), DeskBoxLegacyExeName) <> 0) then
    Exit;

  AddHeuristicInstallCandidate(ExtractFileDir(ExecutablePath), Source, True);
end;

procedure AddRunningProcessCandidatesViaPowerShell;
var
  PowerShellPath: string;
  TempOutputFile: string;
  CommandLine: string;
  Parameters: string;
  ResultCode: Integer;
  Lines: TArrayOfString;
  Index: Integer;
begin
  if IsUninstaller then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
    Exit;

  try
    TempOutputFile := ExpandConstant('{tmp}\deskbox-running-paths.txt');
  except
    Exit;
  end;

  DeleteFile(TempOutputFile);
  CommandLine :=
    '$out = ''' + EscapePowerShellString(TempOutputFile) + '''; ' +
    '$paths = New-Object System.Collections.Generic.List[string]; ' +
    'Get-Process -Name ''DeskBox'' -ErrorAction SilentlyContinue | ForEach-Object { ' +
      'try { if ($_.Path) { [void]$paths.Add([string]$_.Path) } } catch {} ' +
    '}; ' +
    '[System.IO.File]::WriteAllLines($out, $paths, [System.Text.Encoding]::UTF8); ' +
    'exit 0';
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + CommandLine + '"';

  if Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     (ResultCode = 0) and
     LoadStringsFromFile(TempOutputFile, Lines) then
  begin
    for Index := 0 to GetArrayLength(Lines) - 1 do
      AddRunningExecutableCandidate(Lines[Index], 'running DeskBox.exe process (PowerShell fallback)');
  end;

  DeleteFile(TempOutputFile);
end;

procedure AddRunningProcessInstallCandidates;
var
  Locator: Variant;
  Service: Variant;
  Processes: Variant;
  ProcessItem: Variant;
  ExecutablePathValue: Variant;
  ExecutablePath: string;
  Count: Integer;
  Index: Integer;
  HadUnreadableProcessPath: Boolean;
begin
  HadUnreadableProcessPath := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\cimv2');
    Processes := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''DeskBox.exe''');
    Count := Processes.Count;
    for Index := 0 to Count - 1 do
    begin
      ProcessItem := Processes.ItemIndex(Index);
      ExecutablePathValue := ProcessItem.ExecutablePath;
      if (not VarIsNull(ExecutablePathValue)) and (not VarIsEmpty(ExecutablePathValue)) then
      begin
        ExecutablePath := ExecutablePathValue;
        AddRunningExecutableCandidate(ExecutablePath, 'running DeskBox.exe process');
      end
      else
        HadUnreadableProcessPath := True;
    end;

    if HadUnreadableProcessPath then
      AddRunningProcessCandidatesViaPowerShell;
  except
    Log('DeskBox WMI running-process probe failed (' + GetExceptionMessage + '); trying PowerShell fallback.');
    AddRunningProcessCandidatesViaPowerShell;
  end;
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

  AddStartupRunInstallCandidate(HKEY_CURRENT_USER, DeskBoxInstallStartupRunKey, 'HKCU Run startup');
  AddStartupRunInstallCandidate(HKEY_LOCAL_MACHINE, DeskBoxInstallStartupRunKey, 'HKLM Run startup');
  AddStartupRunInstallCandidate(HKEY_LOCAL_MACHINE, DeskBoxInstallWowStartupRunKey, 'HKLM 32-bit Run startup');
  AddLoadedUserHivesCandidates;
  AddStartupScheduledTaskCandidates;
  AddAllUserProfileInstallCandidates;
  AddRunningProcessInstallCandidates;
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

    if IsAdminInstallMode and
       (not HasMachineRegistrationForPath(ExistingInstallPath)) and
       (HasCurrentUserRegistrationForPath(ExistingInstallPath) or
        HasLoadedUserHiveRegistrationForPath(ExistingInstallPath) or
        IsPerUserProfileInstallPath(ExistingInstallPath)) then
    begin
      MessageText := FmtMessage(ExpandConstant('{cm:PerUserUpgradeScopeMismatch}'), [ExistingInstallPath]);
      Log('DeskBox installation blocked: existing per-user installation cannot be upgraded in all-users mode: ' + ExistingInstallPath);
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
    '$names = @(''DeskBox.exe'', ''DeskBox.Settings.exe'', ''DeskBox.ThumbnailProxy.exe''); ' +
    '$stemNames = @(''DeskBox'', ''DeskBox.Settings'', ''DeskBox.ThumbnailProxy''); ' +
    '$target = [System.IO.Path]::GetFullPath(''' + EscapePowerShellString(InstallPath) + ''').TrimEnd(''\''); ' +
    '$ids = New-Object System.Collections.Generic.List[int]; ' +
    'try { ' +
      'Get-CimInstance Win32_Process -ErrorAction Stop | ' +
      'Where-Object { ($names -icontains $_.Name) -and $_.ExecutablePath -and ' +
        '([System.IO.Path]::GetDirectoryName($_.ExecutablePath)).TrimEnd(''\'') -ieq $target } | ' +
      'ForEach-Object { [void]$ids.Add([int]$_.ProcessId) } ' +
    '} catch { ' +
      'Get-Process -Name $stemNames -ErrorAction SilentlyContinue | ' +
      'ForEach-Object { ' +
        'try { ' +
          '$p = $_.Path; ' +
          'if ($p -and ([System.IO.Path]::GetDirectoryName($p)).TrimEnd(''\'') -ieq $target) { [void]$ids.Add([int]$_.Id) } ' +
        '} catch {} ' +
      '} ' +
    '}; ' +
    '$failed = $false; ' +
    'foreach ($id in ($ids | Select-Object -Unique)) { ' +
      'try { ' +
        'Stop-Process -Id $id -Force -ErrorAction Stop; ' +
        'Wait-Process -Id $id -Timeout 5 -ErrorAction SilentlyContinue ' +
      '} catch { ' +
        'if (Get-Process -Id $id -ErrorAction SilentlyContinue) { $failed = $true } ' +
      '} ' +
    '}; ' +
    'if ($failed) { exit 1 }; ' +
    'exit 0';
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
