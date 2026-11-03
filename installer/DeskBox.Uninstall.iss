[Code]
const
  DeskBoxProcessName = 'DeskBox.exe';
  DeskBoxDataSettingsPath = '{localappdata}\DeskBox\data\settings.json';
  DeskBoxDefaultManagedStorageRootPath = '{%USERPROFILE}\DeskBox';
  DeskBoxAppDataRootPath = '{localappdata}\DeskBox';
  DeskBoxRecoveryRootPath = '{localappdata}\DeskBox-Recovery';
  // Identity Name of the Microsoft Store (MSIX) package declared in
  // src\DeskBox\Package.appxmanifest. The Store edition keeps its data inside
  // its own MSIX LocalCache, separate from the two data roots above, so this
  // uninstaller never deletes Store data; while the Store edition is present
  // (or its state cannot be verified) those roots are still kept as a
  // conservative safety measure.
  DeskBoxStorePackageIdentityName = 'D1FC332A.DeskBoxWidgets';
  DeskBoxTemporaryRootPath = '{%TEMP}\DeskBox';
  DeskBoxFeedbackTemporaryRootPath = '{%TEMP}\DeskBoxFeedback';
  DeskBoxSettingsHostTraceLogPath = '{%TEMP}\deskbox-settings-host.log';
  DeskBoxJumpListCustomDestinationsPath = '{userappdata}\Microsoft\Windows\Recent\CustomDestinations\3bd2d2b294e73d6e.customDestinations-ms';
  DeskBoxProductRegistryKey = 'Software\DeskBox';
  DeskBoxStartupRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  DeskBoxStartupApprovedRunKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  DeskBoxStartupTaskNamePrefix = 'DeskBox User Startup';
  DeskBoxAppUserModelId = 'DeskBox.DeskBox';
  DeskBoxAppUserModelIdRegistryKey = 'Software\Classes\AppUserModelId';
  DeskBoxNotificationSettingsRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Notifications\Settings';
  DeskBoxClassesClsidRegistryKey = 'Software\Classes\CLSID';
  DeskBoxProtocolRegistryKey = 'Software\Classes\deskbox';
  DeskBoxPurgeUserDataParameter = '/PURGEUSERDATA';
  DeskBoxManagedStorageShortcutFileName = 'DeskBox Files.lnk';
  DeskBoxManagedStorageShortcutDescription = 'DeskBox managed storage';
  // Three states of the Microsoft Store edition probe: not registered for the
  // current user, registered for the current user, or unverifiable. Unknown
  // is handled exactly like installed (fail closed) so shared user content is
  // never purged while the Store edition's presence is in doubt.
  StoreDeskBoxStateNotInstalled = 0;
  StoreDeskBoxStateInstalled = 1;
  StoreDeskBoxStateUnknown = 2;

var
  PurgeDeskBoxAppData: Boolean;

function TrimString(Value: string): string;
begin
  Result := Trim(Value);
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

procedure GetManagedStorageShortcutPath(var ShortcutPath: string);
var
  SettingsPath: string;
  Json: AnsiString;
  ConfiguredPath: string;
begin
  ShortcutPath := '';
  SettingsPath := ExpandConstant(DeskBoxDataSettingsPath);

  if not FileExists(SettingsPath) then
    Exit;

  if not LoadStringFromFile(SettingsPath, Json) then
    Exit;

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
begin
  FolderPath := GetManagedStorageRootPath;

  // The shortcut is a recovery affordance for a person watching the
  // uninstaller; silent runs have nobody to ask and must not spawn desktop
  // icons (a suppressed prompt would fall through to the default Yes).
  if UninstallSilent then
  begin
    Log('DeskBox silent uninstall skipped the managed storage shortcut offer. Managed storage root: ' + FolderPath);
    Exit;
  end;

  if not FolderContainsManagedStorageItems(FolderPath) then
    Exit;

  GetManagedStorageShortcutPath(ConfiguredShortcutPath);

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

  // Silent runs (including QuietUninstallString = /SILENT without
  // /SUPPRESSMSGBOXES) have no interactive user to confirm this notice and
  // never delete managed storage anyway.
  if UninstallSilent then
  begin
    Log('DeskBox silent uninstall preserved non-empty managed storage folder: ' + FolderPath);
    Exit;
  end;

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

function GetStoreDeskBoxInstallState: Integer;
var
  ResultCode: Integer;
begin
  // Three-state probe for the Microsoft Store edition. The Store edition
  // stores its data in its own MSIX LocalCache, not in the data roots this
  // uninstaller manages, so the result only decides whether the direct
  // edition's data may be purged. Get-AppxPackage without -AllUsers only
  // reports packages registered for the current user, which is the same
  // profile whose data this uninstaller would purge: purging only happens
  // outside admin install mode (see InitializeUninstall), and even an
  // all-users uninstaller is elevated from the initiating user's session, so
  // that user's package registration is the one that matters. PowerShell exit
  // codes: 0 = the package is registered for this user, 1 = Get-AppxPackage
  // returned nothing (not installed), 2 = the probe itself failed, 3 = the
  // probe exceeded its 30-second Wait-Job budget, anything else = the probe
  // failed unexpectedly. A failed or timed-out probe never silently becomes
  // "not installed": systems without the AppX platform are short-circuited
  // by the pre-check below before PowerShell is spawned, and every other
  // failure stays Unknown, which callers treat as installed.
  Result := StoreDeskBoxStateUnknown;

  // Platform pre-check, using only native Inno Setup calls so no extra
  // process is spawned. LTSC and stripped-down Windows ship without the AppX
  // platform: with neither the %WINDIR%\System32\WindowsApps directory nor
  // the AppXSvc service present, no Store edition can be installed at all,
  // so the purge may proceed. It runs before the PowerShell probe so those
  // systems never spawn a process that cannot answer the question. On an
  // AppX-capable system the state stays Unknown and callers must fail closed.
  if (not DirExists(ExpandConstant('{win}\System32\WindowsApps'))) and
     (not RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\AppXSvc')) then
  begin
    Result := StoreDeskBoxStateNotInstalled;
    Log('DeskBox uninstall found no AppX platform (WindowsApps directory and AppXSvc service are absent); the Microsoft Store edition cannot be installed.');
    Exit;
  end;

  // The Get-AppxPackage probe runs inside a PowerShell job with a hard
  // 30-second budget: a wedged AppX repository must not hang the uninstall
  // entry points that call this function. A job's exit code does not
  // propagate to the parent, so the job scriptblock reports 0/1/2 as its
  // output and the outer script maps that output to its own exit codes;
  // a timeout stops and removes the job and exits 3, which lands in the
  // catch-all branch below (Unknown, fail closed).
  if not Exec(
       ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "try { $job = Start-Job { try { if (Get-AppxPackage -Name ''' +
         DeskBoxStorePackageIdentityName +
         ''' -ErrorAction Stop) { 0 } else { 1 } } catch { 2 } }; if (Wait-Job -Job $job -Timeout 30) { $code = Receive-Job -Job $job; ' +
         'Remove-Job -Job $job -Force; if ($code -eq 0) { exit 0 }; if ($code -eq 1) { exit 1 }; exit 2 }; ' +
         'Stop-Job -Job $job; Remove-Job -Job $job -Force; exit 3 } catch { exit 2 }"',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode) then
    Log('DeskBox uninstall could not start the Microsoft Store edition detection.')
  else
  begin
    case ResultCode of
      0:
        begin
          Result := StoreDeskBoxStateInstalled;
          Log('DeskBox uninstall detected the Microsoft Store edition for the current user.');
          Exit;
        end;
      1:
        begin
          Result := StoreDeskBoxStateNotInstalled;
          Log('DeskBox uninstall found no Microsoft Store edition for the current user.');
          Exit;
        end;
    else
      Log('DeskBox uninstall could not detect the Microsoft Store edition (PowerShell exit code ' + IntToStr(ResultCode) + ').');
    end;
  end;

  Log('DeskBox uninstall could not verify the Microsoft Store edition on an AppX-capable system; treating it as installed to protect user data.');
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
    case GetStoreDeskBoxInstallState of
      StoreDeskBoxStateInstalled:
        begin
          PurgeDeskBoxAppData := False;
          Log('DeskBox uninstall skipped the /PURGEUSERDATA purge because the Microsoft Store edition is still installed for this user.');
          // Interactive callers deserve the same explanation the Unknown
          // branch gets; silent runs suppress this automatically.
          if not UninstallSilent then
            SuppressibleMsgBox(
              ExpandConstant('{cm:StoreEditionDataPreserved}'),
              mbInformation,
              MB_OK,
              IDOK);
        end;
      StoreDeskBoxStateUnknown:
        begin
          PurgeDeskBoxAppData := False;
          Log('DeskBox uninstall skipped the /PURGEUSERDATA purge because the Microsoft Store edition state could not be verified.');
          if not UninstallSilent then
            SuppressibleMsgBox(
              ExpandConstant('{cm:StoreEditionStateUnknown}'),
              mbInformation,
              MB_OK,
              IDOK);
        end;
    else
      Log('DeskBox uninstall will purge application data because /PURGEUSERDATA was specified.');
    end;
    Result := True;
    Exit;
  end;

  // QuietUninstallString passes /SILENT without /SUPPRESSMSGBOXES; never block
  // background/winget uninstalls on a modal TaskDialog when /PURGEUSERDATA was
  // not requested.
  if UninstallSilent then
  begin
    PurgeDeskBoxAppData := False;
    Log('DeskBox silent uninstall will preserve application data and recovery snapshots.');
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
        case GetStoreDeskBoxInstallState of
          StoreDeskBoxStateInstalled:
            begin
              PurgeDeskBoxAppData := False;
              Log('DeskBox uninstall preserved application data because the Microsoft Store edition is still installed for this user.');
              SuppressibleMsgBox(
                ExpandConstant('{cm:StoreEditionDataPreserved}'),
                mbInformation,
                MB_OK,
                IDOK);
            end;
          StoreDeskBoxStateUnknown:
            begin
              PurgeDeskBoxAppData := False;
              Log('DeskBox uninstall preserved application data because the Microsoft Store edition state could not be verified.');
              SuppressibleMsgBox(
                ExpandConstant('{cm:StoreEditionStateUnknown}'),
                mbInformation,
                MB_OK,
                IDOK);
            end;
        else
          begin
            PurgeDeskBoxAppData := True;
            Log('DeskBox uninstall will permanently remove application data and recovery snapshots.');
          end;
        end;
        Result := True;
      end;
    else
      Log('DeskBox uninstall was cancelled at the application data choice.');
  end;
end;

procedure StopDeskBoxProcess;
begin
  Log('正在停止 DeskBox 进程。');
  if StopDeskBoxProcessesAtPath(ExpandConstant('{app}')) then
    // The uninstaller does not run Restart Manager, so give the killed
    // processes a beat to release their file handles before DelTree.
    Sleep(1500)
  else
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
    begin
      RegDeleteValue(HKEY_CURRENT_USER, DeskBoxStartupApprovedRunKey, 'DeskBox');
      Log('DeskBox uninstall removed startup registry entry.');
    end
    else
      Log('DeskBox uninstall preserved startup registry entry owned by another DeskBox installation.')
  end
  else if PurgeDeskBoxAppData then
    RegDeleteValue(HKEY_CURRENT_USER, DeskBoxStartupApprovedRunKey, 'DeskBox');

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
  TaskbarPinnedDir: string;
  Path: string;
  FindRec: TFindRec;
begin
  TaskbarPinnedDir := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar');
  Path := AddBackslash(TaskbarPinnedDir) + 'DeskBox.lnk';
  if ShortcutTargetsCurrentInstall(Path) then
  begin
    if DeleteFile(Path) then
      Log('DeskBox uninstall removed taskbar pinned shortcut.')
    else
      Log('DeskBox uninstall failed to remove taskbar pinned shortcut.');
  end;

  if DirExists(TaskbarPinnedDir) and FindFirst(AddBackslash(TaskbarPinnedDir) + '*.lnk', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        begin
          Path := AddBackslash(TaskbarPinnedDir) + FindRec.Name;
          if ShortcutTargetsCurrentInstall(Path) then
          begin
            if DeleteFile(Path) then
              Log('DeskBox uninstall removed taskbar pinned shortcut: ' + FindRec.Name)
            else
              Log('DeskBox uninstall failed to remove taskbar pinned shortcut: ' + FindRec.Name);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure RemoveAppCompatFlagForRootAndExe(RootKey: Integer; ExePath: string);
var
  Value: string;
begin
  if RegQueryStringValue(RootKey, DeskBoxAppCompatLayersKey, ExePath, Value) then
  begin
    if RegDeleteValue(RootKey, DeskBoxAppCompatLayersKey, ExePath) then
      Log('DeskBox uninstall removed AppCompat value: ' + ExePath)
    else
      Log('DeskBox uninstall failed to remove AppCompat value: ' + ExePath);
  end;
end;

procedure RemoveAppCompatFlag;
var
  ExePath: string;
  SettingsExePath: string;
begin
  ExePath := ExpandConstant('{app}\DeskBox.exe');
  SettingsExePath := ExpandConstant('{app}\DeskBox.Settings.exe');
  RemoveAppCompatFlagForRootAndExe(HKEY_CURRENT_USER, ExePath);
  RemoveAppCompatFlagForRootAndExe(HKEY_CURRENT_USER, SettingsExePath);
  if IsAdminInstallMode then
  begin
    RemoveAppCompatFlagForRootAndExe(HKEY_LOCAL_MACHINE, ExePath);
    RemoveAppCompatFlagForRootAndExe(HKEY_LOCAL_MACHINE, SettingsExePath);
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
  FeedbackTemporaryPath: string;
  SettingsHostTraceLog: string;
  JumpListPath: string;
  FailedPaths: string;
begin
  FailedPaths := '';
  TemporaryPath := ExpandConstant(DeskBoxTemporaryRootPath);
  if not DeleteExpectedDirectory(
      TemporaryPath,
      ExpandConstant(DeskBoxTemporaryRootPath),
      'DeskBox') then
    AppendFailedCleanupPath(FailedPaths, TemporaryPath);

  FeedbackTemporaryPath := ExpandConstant(DeskBoxFeedbackTemporaryRootPath);
  DeleteExpectedDirectory(
    FeedbackTemporaryPath,
    ExpandConstant(DeskBoxFeedbackTemporaryRootPath),
    'DeskBoxFeedback');

  SettingsHostTraceLog := ExpandConstant(DeskBoxSettingsHostTraceLogPath);
  if FileExists(SettingsHostTraceLog) then
    DeleteFile(SettingsHostTraceLog);

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

    // Remove the JumpList custom-destinations file only when no other DeskBox
    // installation still owns the shared DeskBox.DeskBox AUMID registration.
    if not RegKeyExists(
         HKEY_CURRENT_USER,
         DeskBoxAppUserModelIdRegistryKey + '\' + DeskBoxAppUserModelId) then
    begin
      JumpListPath := ExpandConstant(DeskBoxJumpListCustomDestinationsPath);
      if FileExists(JumpListPath) then
        DeleteFile(JumpListPath);
    end;

    // The product registry key carries the installer language and install
    // state a re-install may want; it is purge data, so it belongs inside
    // the purge branch. Outside it, a "keep app data" uninstall deleted it
    // unconditionally (the admin branch never did — asymmetric).
    if RegKeyExists(HKEY_CURRENT_USER, DeskBoxProductRegistryKey) and
       not RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, DeskBoxProductRegistryKey) then
      Log('DeskBox uninstall could not remove the DeskBox product registry key.');
  end;

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

function RemoveOwnedNotificationActivators(CurrentActivatorId: string): Boolean;
var
  ClsidNames: TArrayOfString;
  Index: Integer;
  CandidateId: string;
begin
  Result := False;
  if (CurrentActivatorId <> '') and
     NotificationRegistrationTargetsCurrentInstall(CurrentActivatorId) then
  begin
    if RegDeleteKeyIncludingSubkeys(
         HKEY_CURRENT_USER,
         DeskBoxClassesClsidRegistryKey + '\' + CurrentActivatorId) then
      Result := True;
  end;

  if RegGetSubkeyNames(
       HKEY_CURRENT_USER,
       DeskBoxClassesClsidRegistryKey,
       ClsidNames) then
  begin
    for Index := 0 to GetArrayLength(ClsidNames) - 1 do
    begin
      CandidateId := ClsidNames[Index];
      if (CandidateId <> '') and
         NotificationRegistrationTargetsCurrentInstall(CandidateId) then
      begin
        if RegDeleteKeyIncludingSubkeys(
             HKEY_CURRENT_USER,
             DeskBoxClassesClsidRegistryKey + '\' + CandidateId) then
          Result := True;
      end;
    end;
  end;
end;

procedure RemoveNotificationRegistration;
var
  AppUserModelKey: string;
  ActivatorId: string;
  IconPath: string;
  PathAppUserModelId: string;
  HasAppUserModelKey: Boolean;
  RemovedOwnedActivator: Boolean;
  OwnsRegistration: Boolean;
begin
  AppUserModelKey := DeskBoxAppUserModelIdRegistryKey + '\' + DeskBoxAppUserModelId;
  HasAppUserModelKey := RegKeyExists(HKEY_CURRENT_USER, AppUserModelKey);
  ActivatorId := '';
  IconPath := '';
  if HasAppUserModelKey then
  begin
    RegQueryStringValue(HKEY_CURRENT_USER, AppUserModelKey, 'CustomActivator', ActivatorId);
    RegQueryStringValue(HKEY_CURRENT_USER, AppUserModelKey, 'IconUri', IconPath);
  end;

  if ActivatorId <> '' then
    OwnsRegistration := NotificationRegistrationTargetsCurrentInstall(ActivatorId)
  else
    OwnsRegistration := False;

  RemovedOwnedActivator := RemoveOwnedNotificationActivators(ActivatorId);
  if (ActivatorId = '') and
     (RemovedOwnedActivator or (HasAppUserModelKey and PurgeDeskBoxAppData)) then
    OwnsRegistration := True;

  if IconPath = '' then
    IconPath := ExpandConstant(
      '{localappdata}\Microsoft\WindowsAppSDK\DeskBox.DeskBox.png');

  PathAppUserModelId := ExpandConstant('{app}\' + DeskBoxProcessName);
  StringChangeEx(PathAppUserModelId, '\', '.', True);
  RegDeleteKeyIncludingSubkeys(
    HKEY_CURRENT_USER,
    DeskBoxAppUserModelIdRegistryKey + '\' + PathAppUserModelId);

  PathAppUserModelId := ExpandConstant('{app}\DeskBox.Settings.exe');
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
  else if RemovedOwnedActivator then
    Log('DeskBox uninstall removed stale notification activator CLSID(s) for this installation and preserved active registration owned by another DeskBox executable.')
  else if ActivatorId <> '' then
    Log('DeskBox uninstall preserved a notification registration owned by another DeskBox executable.');
end;

procedure RemoveDeskBoxProtocolRegistration;
var
  CommandKey: string;
  CommandValue: string;
  ExecutablePath: string;
begin
  CommandKey := DeskBoxProtocolRegistryKey + '\shell\open\command';
  if not RegQueryStringValue(HKEY_CURRENT_USER, CommandKey, '', CommandValue) then
    Exit;

  ExecutablePath := ExtractExecutablePath(CommandValue);
  if SameInstallPath(ExtractFileDir(ExecutablePath), ExpandConstant('{app}')) and
     (CompareText(ExtractFileName(ExecutablePath), DeskBoxProcessName) = 0) then
  begin
    if RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, DeskBoxProtocolRegistryKey) then
      Log('DeskBox uninstall removed per-user deskbox:// protocol registration.')
    else
      Log('DeskBox uninstall failed to remove per-user deskbox:// protocol registration.');
  end
  else
    Log('DeskBox uninstall preserved per-user deskbox:// protocol registration owned by another DeskBox executable.');
end;

function CurrentProfileHasDeskBoxData: Boolean;
begin
  Result :=
    FileExists(ExpandConstant(DeskBoxDataSettingsPath)) or
    DirExists(ExpandConstant(DeskBoxAppDataRootPath)) or
    DirExists(ExpandConstant(DeskBoxRecoveryRootPath));
end;

function InitializeUninstall: Boolean;
begin
  if IsAdminInstallMode then
  begin
    // A machine-wide uninstaller can run under a different administrator
    // account from the person who started it. Never interpret that elevated
    // account's LocalAppData as the data of every DeskBox user.
    PurgeDeskBoxAppData := False;
    // The usual split-token elevation keeps the initiating user's profile, so
    // existing DeskBox profile directories (or settings.json) prove this
    // account used DeskBox and can choose whether to keep or purge its data.
    // Only offer the managed-storage shortcut when settings.json exists so an
    // elevated admin account without DeskBox settings never guesses another
    // user's default storage path.
    if CurrentProfileHasDeskBoxData then
    begin
      if FileExists(ExpandConstant(DeskBoxDataSettingsPath)) then
      begin
        Result := ConfirmManagedStoragePreserved;
        if not Result then
          Log('DeskBox uninstall aborted: the user cancelled at the non-empty managed storage confirmation.')
        else
          OfferManagedStorageShortcut;
      end
      else
        Result := True;

      if Result then
        Result := ChooseAppDataRemoval;
    end
    else
    begin
      Log('DeskBox all-users uninstall will preserve every user profile''s application data.');
      Log('DeskBox all-users uninstall skipped the managed storage shortcut offer because the current account has no DeskBox profile data.');
      Result := True;
    end;
  end
  else
  begin
    Result := ConfirmManagedStoragePreserved;
    if not Result then
      Log('DeskBox uninstall aborted: the user cancelled at the non-empty managed storage confirmation.')
    else
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

    // The usual split-token elevation keeps HKCU and LocalAppData on the
    // initiating user, and each per-user registration cleanup below is
    // owner-checked against {app}, so running them in both install modes
    // removes the initiating user's dead Run entry, taskbar pin, AppCompat
    // layer, and COM notification activator without touching another
    // DeskBox installation. RemoveDeskBoxDataDirectories only purges
    // LocalAppData/DeskBox-Recovery when PurgeDeskBoxAppData was explicitly
    // approved in InitializeUninstall.
    RemoveStartupRegistryEntry;
    RemoveTaskbarPinnedShortcut;
    RemoveAppCompatFlag;
    RemoveNotificationRegistration;
    RemoveDeskBoxProtocolRegistration;
    RemoveDeskBoxDataDirectories;
    if PurgeDeskBoxAppData then
      Log('DeskBox uninstall removed local app data and recovery snapshots.')
    else
      Log('DeskBox uninstall kept local app data and recovery snapshots.');
  end;
end;
