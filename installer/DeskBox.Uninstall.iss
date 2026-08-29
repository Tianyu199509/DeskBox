[Code]
const
  DeskBoxProcessName = 'DeskBox.exe';
  DeskBoxDataSettingsPath = '{localappdata}\DeskBox\data\settings.json';
  DeskBoxDefaultManagedStorageRootPath = '{%USERPROFILE}\DeskBox';
  DeskBoxAppDataRootPath = '{localappdata}\DeskBox';
  DeskBoxRecoveryRootPath = '{localappdata}\DeskBox-Recovery';
  DeskBoxTemporaryRootPath = '{%TEMP}\DeskBox';
  DeskBoxProductRegistryKey = 'Software\DeskBox';
  DeskBoxStartupRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  DeskBoxStartupTaskNamePrefix = 'DeskBox User Startup';
  DeskBoxAppUserModelId = 'DeskBox.DeskBox';
  DeskBoxAppUserModelIdRegistryKey = 'Software\Classes\AppUserModelId';
  DeskBoxNotificationSettingsRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Notifications\Settings';
  DeskBoxClassesClsidRegistryKey = 'Software\Classes\CLSID';
  DeskBoxPurgeUserDataParameter = '/PURGEUSERDATA';
  DeskBoxManagedStorageShortcutFileName = 'DeskBox Files.lnk';
  DeskBoxManagedStorageShortcutDescription = 'DeskBox managed storage';

var
  PurgeDeskBoxAppData: Boolean;

function TrimString(Value: string): string;
begin
  Result := Trim(Value);
end;

function TryReadJsonBooleanValue(Json: string; PropertyName: string; var Value: Boolean): Boolean;
var
  Key: string;
  KeyPosition: Integer;
  ColonPosition: Integer;
  StartPosition: Integer;
  ValueText: string;
begin
  Result := False;
  Key := '"' + PropertyName + '"';
  KeyPosition := Pos(Key, Json);
  if KeyPosition = 0 then
    Exit;

  ColonPosition := KeyPosition + Length(Key);
  while (ColonPosition <= Length(Json)) and (Copy(Json, ColonPosition, 1) <> ':') do
    ColonPosition := ColonPosition + 1;

  if ColonPosition > Length(Json) then
    Exit;

  StartPosition := ColonPosition + 1;
  while (StartPosition <= Length(Json)) and
        ((Copy(Json, StartPosition, 1) = ' ') or
         (Copy(Json, StartPosition, 1) = #9) or
         (Copy(Json, StartPosition, 1) = #10) or
         (Copy(Json, StartPosition, 1) = #13)) do
    StartPosition := StartPosition + 1;

  ValueText := Lowercase(Copy(Json, StartPosition, 5));
  if Copy(ValueText, 1, 4) = 'true' then
  begin
    Value := True;
    Result := True;
  end
  else if ValueText = 'false' then
  begin
    Value := False;
    Result := True;
  end;
end;

function UnescapeJsonString(Value: string): string;
begin
  StringChangeEx(Value, '\/', '/', True);
  StringChangeEx(Value, '\\', '\', True);
  StringChangeEx(Value, '\"', '"', True);
  Result := Value;
end;

function TryReadJsonStringValue(Json: string; PropertyName: string; var Value: string): Boolean;
var
  Key: string;
  KeyPosition: Integer;
  ColonPosition: Integer;
  StartPosition: Integer;
  EndPosition: Integer;
  CurrentPosition: Integer;
  BackslashCount: Integer;
begin
  Result := False;
  Value := '';
  Key := '"' + PropertyName + '"';
  KeyPosition := Pos(Key, Json);
  if KeyPosition = 0 then
    Exit;

  ColonPosition := KeyPosition + Length(Key);
  while (ColonPosition <= Length(Json)) and (Copy(Json, ColonPosition, 1) <> ':') do
    ColonPosition := ColonPosition + 1;

  if ColonPosition > Length(Json) then
    Exit;

  StartPosition := ColonPosition + 1;
  while (StartPosition <= Length(Json)) and
        ((Copy(Json, StartPosition, 1) = ' ') or
         (Copy(Json, StartPosition, 1) = #9) or
         (Copy(Json, StartPosition, 1) = #10) or
         (Copy(Json, StartPosition, 1) = #13)) do
    StartPosition := StartPosition + 1;

  if (StartPosition > Length(Json)) or (Copy(Json, StartPosition, 1) <> '"') then
    Exit;

  CurrentPosition := StartPosition + 1;
  while CurrentPosition <= Length(Json) do
  begin
    if Copy(Json, CurrentPosition, 1) = '"' then
    begin
      BackslashCount := 0;
      EndPosition := CurrentPosition - 1;
      while (EndPosition >= StartPosition + 1) and (Copy(Json, EndPosition, 1) = '\') do
      begin
        BackslashCount := BackslashCount + 1;
        EndPosition := EndPosition - 1;
      end;

      if (BackslashCount mod 2) = 0 then
      begin
        Value := UnescapeJsonString(Copy(Json, StartPosition + 1, CurrentPosition - StartPosition - 1));
        Result := True;
        Exit;
      end;
    end;

    CurrentPosition := CurrentPosition + 1;
  end;
end;

function GetManagedStorageRootPath: string;
var
  SettingsPath: string;
  Json: AnsiString;
  ConfiguredPath: string;
begin
  Result := ExpandConstant(DeskBoxDefaultManagedStorageRootPath);
  SettingsPath := ExpandConstant(DeskBoxDataSettingsPath);

  if not FileExists(SettingsPath) then
    Exit;

  if not LoadStringFromFile(SettingsPath, Json) then
    Exit;

  if TryReadJsonStringValue(Json, 'defaultManagedStorageRootPath', ConfiguredPath) then
  begin
    ConfiguredPath := TrimString(ConfiguredPath);
    if ConfiguredPath <> '' then
      Result := ConfiguredPath;
  end;
end;

procedure GetManagedStorageShortcutSettings(
  var ShortcutEnabled: Boolean;
  var ShortcutPath: string);
var
  SettingsPath: string;
  Json: AnsiString;
  ConfiguredEnabled: Boolean;
  ConfiguredPath: string;
begin
  ShortcutEnabled := True;
  ShortcutPath := '';
  SettingsPath := ExpandConstant(DeskBoxDataSettingsPath);

  if not FileExists(SettingsPath) then
    Exit;

  if not LoadStringFromFile(SettingsPath, Json) then
    Exit;

  if TryReadJsonBooleanValue(Json, 'managedStorageDesktopShortcutEnabled', ConfiguredEnabled) then
    ShortcutEnabled := ConfiguredEnabled;

  if TryReadJsonStringValue(Json, 'managedStorageDesktopShortcutPath', ConfiguredPath) then
    ShortcutPath := TrimString(ConfiguredPath);
end;

function IsSafeDesktopShortcutPath(ShortcutPath: string): Boolean;
begin
  Result :=
    (ShortcutPath <> '') and
    (CompareText(ExtractFileExt(ShortcutPath), '.lnk') = 0) and
    SameInstallPath(ExtractFileDir(ShortcutPath), ExpandConstant('{userdesktop}'));
end;

function ShortcutTargetsManagedStorage(
  ShortcutPath: string;
  FolderPath: string): Boolean;
var
  TargetPath: string;
begin
  Result :=
    IsSafeDesktopShortcutPath(ShortcutPath) and
    TryReadShortcutTarget(ShortcutPath, TargetPath) and
    SameInstallPath(TargetPath, FolderPath);
end;

function FindManagedStorageShortcut(
  FolderPath: string;
  ConfiguredShortcutPath: string;
  var ShortcutPath: string): Boolean;
var
  CandidatePath: string;
  CandidateName: string;
  Number: Integer;
begin
  Result := False;
  ShortcutPath := '';

  if ShortcutTargetsManagedStorage(ConfiguredShortcutPath, FolderPath) then
  begin
    ShortcutPath := ConfiguredShortcutPath;
    Result := True;
    Exit;
  end;

  for Number := 1 to 99 do
  begin
    if Number = 1 then
      CandidateName := DeskBoxManagedStorageShortcutFileName
    else
      CandidateName := 'DeskBox Files (' + IntToStr(Number) + ').lnk';

    CandidatePath := AddBackslash(ExpandConstant('{userdesktop}')) + CandidateName;
    if ShortcutTargetsManagedStorage(CandidatePath, FolderPath) then
    begin
      ShortcutPath := CandidatePath;
      Result := True;
      Exit;
    end;
  end;
end;

function GetAvailableManagedStorageShortcutPath(
  ConfiguredShortcutPath: string): string;
var
  CandidatePath: string;
  CandidateName: string;
  Number: Integer;
begin
  Result := '';
  if IsSafeDesktopShortcutPath(ConfiguredShortcutPath) and
     (not FileExists(ConfiguredShortcutPath)) and
     (not DirExists(ConfiguredShortcutPath)) then
  begin
    Result := ConfiguredShortcutPath;
    Exit;
  end;

  for Number := 1 to 99 do
  begin
    if Number = 1 then
      CandidateName := DeskBoxManagedStorageShortcutFileName
    else
      CandidateName := 'DeskBox Files (' + IntToStr(Number) + ').lnk';

    CandidatePath := AddBackslash(ExpandConstant('{userdesktop}')) + CandidateName;
    if (not FileExists(CandidatePath)) and (not DirExists(CandidatePath)) then
    begin
      Result := CandidatePath;
      Exit;
    end;
  end;
end;

function CreateManagedStorageShortcut(
  ShortcutPath: string;
  FolderPath: string): Boolean;
var
  ShellObject: Variant;
  ShortcutObject: Variant;
begin
  Result := False;
  if (ShortcutPath = '') or (not DirExists(FolderPath)) then
    Exit;

  try
    ShellObject := CreateOleObject('WScript.Shell');
    ShortcutObject := ShellObject.CreateShortcut(ShortcutPath);
    ShortcutObject.TargetPath := FolderPath;
    ShortcutObject.WorkingDirectory := FolderPath;
    ShortcutObject.Description := DeskBoxManagedStorageShortcutDescription;
    ShortcutObject.Save;
    Result := FileExists(ShortcutPath);
  except
    Log('DeskBox uninstall could not create the managed storage shortcut: ' + ShortcutPath);
  end;
end;

function FolderContainsManagedStorageItems(FolderPath: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(FolderPath) then
    Exit;

  if FindFirst(AddBackslash(FolderPath) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure OfferManagedStorageShortcut;
var
  FolderPath: string;
  ConfiguredShortcutPath: string;
  ExistingShortcutPath: string;
  NewShortcutPath: string;
  ShortcutEnabled: Boolean;
begin
  FolderPath := GetManagedStorageRootPath;
  if not FolderContainsManagedStorageItems(FolderPath) then
    Exit;

  GetManagedStorageShortcutSettings(ShortcutEnabled, ConfiguredShortcutPath);
  if not ShortcutEnabled then
  begin
    Log('DeskBox uninstall respected the disabled managed storage desktop shortcut setting.');
    Exit;
  end;

  if FindManagedStorageShortcut(
       FolderPath,
       ConfiguredShortcutPath,
       ExistingShortcutPath) then
  begin
    Log('DeskBox managed storage shortcut already exists: ' + ExistingShortcutPath);
    Exit;
  end;

  if SuppressibleMsgBox(
       FmtMessage(ExpandConstant('{cm:ManagedStorageShortcutPrompt}'), [FolderPath]),
       mbConfirmation,
       MB_YESNO or MB_DEFBUTTON1,
       IDYES) <> IDYES then
  begin
    Log('DeskBox managed storage shortcut creation was declined.');
    Exit;
  end;

  NewShortcutPath := GetAvailableManagedStorageShortcutPath(ConfiguredShortcutPath);
  if CreateManagedStorageShortcut(NewShortcutPath, FolderPath) then
    Log('DeskBox uninstall created managed storage shortcut: ' + NewShortcutPath)
  else
    SuppressibleMsgBox(
      FmtMessage(ExpandConstant('{cm:ManagedStorageShortcutCreateFailed}'), [FolderPath]),
      mbError,
      MB_OK,
      IDOK);
end;

function CountFolderContents(FolderPath: string; var FileCount: Integer; var FolderCount: Integer): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  FileCount := 0;
  FolderCount := 0;

  if not DirExists(FolderPath) then
    Exit;

  Result := True;
  if FindFirst(AddBackslash(FolderPath) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            FolderCount := FolderCount + 1
          else
            FileCount := FileCount + 1;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function BuildManagedStorageSummary(FolderPath: string): string;
var
  FindRec: TFindRec;
  DisplayedCount: Integer;
  ItemLine: string;
begin
  Result := '';
  DisplayedCount := 0;

  if FindFirst(AddBackslash(FolderPath) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if DisplayedCount < 12 then
          begin
            if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
              ItemLine := '  ' + ExpandConstant('{cm:FolderItem}') + ' ' + FindRec.Name
            else
              ItemLine := '  ' + ExpandConstant('{cm:FileItem}') + ' ' + FindRec.Name;

            Result := Result + ItemLine + #13#10;
          end;

          DisplayedCount := DisplayedCount + 1;
        end;
      until FindNext(FindRec) = False;
    finally
      FindClose(FindRec);
    end;
  end;

  if DisplayedCount > 12 then
    Result := Result + '  ' + FmtMessage(ExpandConstant('{cm:MoreItems}'), [IntToStr(DisplayedCount - 12)]) + #13#10;
end;

function ConfirmManagedStoragePreserved: Boolean;
var
  FolderPath: string;
  FileCount: Integer;
  FolderCount: Integer;
  Summary: string;
  MessageText: string;
begin
  Result := True;
  FolderPath := GetManagedStorageRootPath;

  if not CountFolderContents(FolderPath, FileCount, FolderCount) then
    Exit;

  if (FileCount = 0) and (FolderCount = 0) then
    Exit;

  Summary := BuildManagedStorageSummary(FolderPath);
  MessageText :=
    ExpandConstant('{cm:ConfirmStorageTitle}') + ':' + #13#10 +
    FolderPath + #13#10#13#10 +
    FmtMessage(ExpandConstant('{cm:ConfirmStorageBody}'), [IntToStr(FolderCount), IntToStr(FileCount)]) + #13#10#13#10 +
    Summary +
    ExpandConstant('{cm:ConfirmStorageFooter}');

  Result := SuppressibleMsgBox(
    MessageText,
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2,
    IDYES) = IDYES;
end;

function HasUninstallParameter(ParameterName: string): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), ParameterName) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ChooseAppDataRemoval: Boolean;
var
  Choice: Integer;
  DataPaths: string;
  ButtonLabels: TArrayOfString;
begin
  Result := False;
  PurgeDeskBoxAppData := HasUninstallParameter(DeskBoxPurgeUserDataParameter);
  if PurgeDeskBoxAppData then
  begin
    Log('DeskBox uninstall will purge application data because /PURGEUSERDATA was specified.');
    Result := True;
    Exit;
  end;

  DataPaths :=
    ExpandConstant(DeskBoxAppDataRootPath) + #13#10 +
    ExpandConstant(DeskBoxRecoveryRootPath);
  ButtonLabels := [
    ExpandConstant('{cm:KeepAppDataButton}'),
    ExpandConstant('{cm:RemoveAppDataButton}')];
  Choice := SuppressibleTaskDialogMsgBox(
    ExpandConstant('{cm:AppDataChoiceTitle}'),
    FmtMessage(ExpandConstant('{cm:ConfirmRemoveAppData}'), [DataPaths]),
    mbConfirmation,
    MB_YESNOCANCEL,
    ButtonLabels,
    0,
    IDYES);

  case Choice of
    IDYES:
      begin
        PurgeDeskBoxAppData := False;
        Log('DeskBox uninstall will preserve application data and recovery snapshots.');
        Result := True;
      end;
    IDNO:
      begin
        PurgeDeskBoxAppData := True;
        Log('DeskBox uninstall will permanently remove application data and recovery snapshots.');
        Result := True;
      end;
    else
      Log('DeskBox uninstall was cancelled at the application data choice.');
  end;
end;

procedure StopDeskBoxProcess;
begin
  Log('正在停止 DeskBox 进程。');
  if not StopDeskBoxProcessesAtPath(ExpandConstant('{app}')) then
    Log('DeskBox uninstall could not stop only the current installation processes.');
end;

function ShortcutTargetsCurrentInstall(ShortcutPath: string): Boolean;
var
  TargetPath: string;
begin
  Result :=
    TryReadShortcutTarget(ShortcutPath, TargetPath) and
    SameInstallPath(ExtractFileDir(TargetPath), ExpandConstant('{app}')) and
    (CompareText(ExtractFileName(TargetPath), DeskBoxProcessName) = 0);
end;

procedure RemoveStartupRegistryEntry;
var
  Value: string;
  StartupExecutablePath: string;
  StartupShortcutPath: string;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, DeskBoxStartupRunKey, 'DeskBox', Value) then
  begin
    StartupExecutablePath := ExtractExecutablePath(Value);
    if SameInstallPath(ExtractFileDir(StartupExecutablePath), ExpandConstant('{app}')) and
       (CompareText(ExtractFileName(StartupExecutablePath), DeskBoxProcessName) = 0) and
       RegDeleteValue(HKEY_CURRENT_USER, DeskBoxStartupRunKey, 'DeskBox') then
      Log('DeskBox uninstall removed startup registry entry.')
    else
      Log('DeskBox uninstall preserved startup registry entry owned by another DeskBox installation.')
  end;

  // Also remove the legacy startup folder shortcut.
  StartupShortcutPath := ExpandConstant('{userstartup}\DeskBox.lnk');
  if ShortcutTargetsCurrentInstall(StartupShortcutPath) then
  begin
    if DeleteFile(StartupShortcutPath) then
      Log('DeskBox uninstall removed legacy startup shortcut.')
    else
      Log('DeskBox uninstall failed to remove legacy startup shortcut.');
  end;
end;

function IsDeskBoxStartupTaskName(TaskName: string): Boolean;
begin
  Result :=
    (CompareText(TaskName, DeskBoxStartupTaskNamePrefix) = 0) or
    (Pos(Uppercase(DeskBoxStartupTaskNamePrefix + '-'), Uppercase(TaskName)) = 1);
end;

procedure RemoveStartupScheduledTasks;
var
  ScheduleService: Variant;
  RootFolder: Variant;
  RegisteredTasks: Variant;
  RegisteredTask: Variant;
  TaskDefinition: Variant;
  Actions: Variant;
  Action: Variant;
  ActionPath: string;
  TaskName: string;
  TaskIndex: Integer;
begin
  try
    ScheduleService := CreateOleObject('Schedule.Service');
    ScheduleService.Connect;
    RootFolder := ScheduleService.GetFolder('\');
    RegisteredTasks := RootFolder.GetTasks(1);

    for TaskIndex := RegisteredTasks.Count downto 1 do
    begin
      RegisteredTask := RegisteredTasks.Item(TaskIndex);
      TaskName := RegisteredTask.Name;
      if IsDeskBoxStartupTaskName(TaskName) then
      begin
        TaskDefinition := RegisteredTask.Definition;
        Actions := TaskDefinition.Actions;

        if Actions.Count < 1 then
          Log('DeskBox uninstall preserved a startup task with no executable action: ' + TaskName)
        else
        begin
          Action := Actions.Item(1);
          ActionPath := Action.Path;
          if SameInstallPath(ExtractFileDir(ActionPath), ExpandConstant('{app}')) and
             (CompareText(ExtractFileName(ActionPath), DeskBoxProcessName) = 0) then
          begin
            RootFolder.DeleteTask(TaskName, 0);
            Log('DeskBox uninstall removed startup scheduled task: ' + TaskName);
          end
          else
            Log('DeskBox uninstall preserved a startup task owned by another DeskBox installation: ' + TaskName);
        end;
      end;
    end;
  except
    Log('DeskBox startup scheduled tasks could not be fully inspected.');
  end;
end;

procedure RemoveTaskbarPinnedShortcut;
var
  Path: string;
begin
  Path := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DeskBox.lnk');
  if ShortcutTargetsCurrentInstall(Path) then
  begin
    if DeleteFile(Path) then
      Log('DeskBox uninstall removed taskbar pinned shortcut.')
    else
      Log('DeskBox uninstall failed to remove taskbar pinned shortcut.');
  end;
end;

procedure RemoveAppCompatFlag;
var
  ExePath: string;
  Value: string;
begin
  ExePath := ExpandConstant('{app}\DeskBox.exe');
  if RegQueryStringValue(HKEY_CURRENT_USER, DeskBoxAppCompatLayersKey, ExePath, Value) then
  begin
    if RegDeleteValue(HKEY_CURRENT_USER, DeskBoxAppCompatLayersKey, ExePath) then
      Log('DeskBox uninstall removed AppCompat value: ' + ExePath)
    else
      Log('DeskBox uninstall failed to remove AppCompat value: ' + ExePath);
  end;
end;

function DeleteExpectedDirectory(
  Path: string;
  ExpectedPath: string;
  ExpectedLeafName: string): Boolean;
begin
  Result := False;
  if (not SameInstallPath(Path, ExpectedPath)) or
     (CompareText(
        ExtractFileName(RemoveBackslashUnlessRoot(Path)),
        ExpectedLeafName) <> 0) then
  begin
    Log('DeskBox uninstall refused to delete an unexpected directory: ' + Path);
    Exit;
  end;

  Result := True;
  if DirExists(Path) then
  begin
    Result := DelTree(Path, True, True, True);
    if Result then
      Log('DeskBox uninstall removed directory: ' + Path)
    else
      Log('DeskBox uninstall could not completely remove directory: ' + Path);
  end;
end;

procedure AppendFailedCleanupPath(var FailedPaths: string; Path: string);
begin
  if FailedPaths <> '' then
    FailedPaths := FailedPaths + #13#10;
  FailedPaths := FailedPaths + Path;
end;

procedure RemoveDeskBoxDataDirectories;
var
  AppDataPath: string;
  RecoveryPath: string;
  TemporaryPath: string;
  FailedPaths: string;
begin
  FailedPaths := '';
  TemporaryPath := ExpandConstant(DeskBoxTemporaryRootPath);
  if not DeleteExpectedDirectory(
      TemporaryPath,
      ExpandConstant(DeskBoxTemporaryRootPath),
      'DeskBox') then
    AppendFailedCleanupPath(FailedPaths, TemporaryPath);

  if PurgeDeskBoxAppData then
  begin
    AppDataPath := ExpandConstant(DeskBoxAppDataRootPath);
    RecoveryPath := ExpandConstant(DeskBoxRecoveryRootPath);
    if not DeleteExpectedDirectory(
        AppDataPath,
        ExpandConstant(DeskBoxAppDataRootPath),
        'DeskBox') then
      AppendFailedCleanupPath(FailedPaths, AppDataPath);
    if not DeleteExpectedDirectory(
        RecoveryPath,
        ExpandConstant(DeskBoxRecoveryRootPath),
        'DeskBox-Recovery') then
      AppendFailedCleanupPath(FailedPaths, RecoveryPath);

  end;

  if RegKeyExists(HKEY_CURRENT_USER, DeskBoxProductRegistryKey) and
     not RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, DeskBoxProductRegistryKey) then
    Log('DeskBox uninstall could not remove the DeskBox product registry key.');

  if FailedPaths <> '' then
    SuppressibleMsgBox(
      FmtMessage(ExpandConstant('{cm:AppDataCleanupFailed}'), [FailedPaths]),
      mbError,
      MB_OK,
      IDOK);
end;

function NotificationRegistrationTargetsCurrentInstall(ActivatorId: string): Boolean;
var
  LocalServerPath: string;
  ExecutablePath: string;
begin
  Result := False;
  if ActivatorId = '' then
    Exit;

  if not RegQueryStringValue(
      HKEY_CURRENT_USER,
      DeskBoxClassesClsidRegistryKey + '\' + ActivatorId + '\LocalServer32',
      '',
      LocalServerPath) then
    Exit;

  ExecutablePath := ExtractExecutablePath(LocalServerPath);
  Result :=
    SameInstallPath(ExtractFileDir(ExecutablePath), ExpandConstant('{app}')) and
    (CompareText(ExtractFileName(ExecutablePath), DeskBoxProcessName) = 0);
end;

procedure RemoveNotificationRegistration;
var
  AppUserModelKey: string;
  ActivatorId: string;
  IconPath: string;
  PathAppUserModelId: string;
  OwnsRegistration: Boolean;
begin
  AppUserModelKey := DeskBoxAppUserModelIdRegistryKey + '\' + DeskBoxAppUserModelId;
  ActivatorId := '';
  IconPath := '';
  RegQueryStringValue(HKEY_CURRENT_USER, AppUserModelKey, 'CustomActivator', ActivatorId);
  RegQueryStringValue(HKEY_CURRENT_USER, AppUserModelKey, 'IconUri', IconPath);
  OwnsRegistration :=
    (ActivatorId = '') or
    NotificationRegistrationTargetsCurrentInstall(ActivatorId);
  if IconPath = '' then
    IconPath := ExpandConstant(
      '{localappdata}\Microsoft\WindowsAppSDK\DeskBox.DeskBox.png');

  PathAppUserModelId := ExpandConstant('{app}\' + DeskBoxProcessName);
  StringChangeEx(PathAppUserModelId, '\', '.', True);
  RegDeleteKeyIncludingSubkeys(
    HKEY_CURRENT_USER,
    DeskBoxAppUserModelIdRegistryKey + '\' + PathAppUserModelId);

  if OwnsRegistration then
  begin
    if ActivatorId <> '' then
      RegDeleteKeyIncludingSubkeys(
        HKEY_CURRENT_USER,
        DeskBoxClassesClsidRegistryKey + '\' + ActivatorId);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, AppUserModelKey);
    RegDeleteKeyIncludingSubkeys(
      HKEY_CURRENT_USER,
      DeskBoxNotificationSettingsRegistryKey + '\' + DeskBoxAppUserModelId);

    if (IconPath <> '') and
       SameInstallPath(
         ExtractFileDir(IconPath),
         ExpandConstant('{localappdata}\Microsoft\WindowsAppSDK')) then
      DeleteFile(IconPath);

    Log('DeskBox uninstall removed the notification registration owned by this installation.');
  end
  else if ActivatorId <> '' then
    Log('DeskBox uninstall preserved a notification registration owned by another DeskBox executable.');
end;

function InitializeUninstall: Boolean;
begin
  if IsAdminInstallMode then
  begin
    // A machine-wide uninstaller can run under a different administrator
    // account from the person who started it. Never interpret that elevated
    // account's LocalAppData as the data of every DeskBox user.
    PurgeDeskBoxAppData := False;
    Log('DeskBox all-users uninstall will preserve every user profile''s application data.');
    Result := True;
  end
  else
  begin
    Result := ConfirmManagedStoragePreserved;
    if Result then
      OfferManagedStorageShortcut;
    if Result then
      Result := ChooseAppDataRemoval;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopDeskBoxProcess;

  if CurUninstallStep = usPostUninstall then
  begin
    // Scheduled tasks live outside the per-user registry and can be safely
    // removed when their executable action targets this exact installation.
    RemoveStartupScheduledTasks;

    if IsAdminInstallMode then
      Log('DeskBox all-users uninstall preserved per-user startup entries, notifications, settings, and content.')
    else
    begin
      RemoveStartupRegistryEntry;
      RemoveTaskbarPinnedShortcut;
      RemoveAppCompatFlag;
      RemoveNotificationRegistration;
      RemoveDeskBoxDataDirectories;
      if PurgeDeskBoxAppData then
        Log('DeskBox uninstall removed local app data and recovery snapshots.')
      else
        Log('DeskBox uninstall kept local app data and recovery snapshots.');
    end;
  end;
end;
