[Code]
const
  DeskBoxProcessName = 'DeskBox.exe';
  DeskBoxDataSettingsPath = '{localappdata}\DeskBox\data\settings.json';
  DeskBoxDefaultManagedStorageRootPath = '{%USERPROFILE}\DeskBox';
  DeskBoxLocalAppDataRoot = '{localappdata}\DeskBox';
  DeskBoxStartupRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  ShouldRemoveLocalAppData: Boolean;

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
    Result := Result + '  ' + Format(ExpandConstant('{cm:MoreItems}'), [DisplayedCount - 12]) + #13#10;
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
    Format(ExpandConstant('{cm:ConfirmStorageBody}'), [FolderCount, FileCount]) + #13#10#13#10 +
    Summary +
    ExpandConstant('{cm:ConfirmStorageFooter}');

  Result := MsgBox(MessageText, mbConfirmation, MB_YESNO) = IDYES;
end;

function ConfirmRemoveLocalAppData: Boolean;
var
  AppDataRoot: string;
  MessageText: string;
begin
  Result := False;
  AppDataRoot := ExpandConstant(DeskBoxLocalAppDataRoot);

  if not DirExists(AppDataRoot) then
    Exit;

  MessageText :=
    Format(ExpandConstant('{cm:ConfirmRemoveAppData}'), [AppDataRoot]);

  Result := MsgBox(MessageText, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure StopDeskBoxProcess;
var
  ResultCode: Integer;
begin
  Log('正在停止 DeskBox 进程。');
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM ' + DeskBoxProcessName + ' /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Log('taskkill 返回代码：' + IntToStr(ResultCode));
end;

procedure RemoveStartupRegistryEntry;
var
  Value: string;
  StartupShortcutPath: string;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, DeskBoxStartupRunKey, 'DeskBox', Value) then
  begin
    if RegDeleteValue(HKEY_CURRENT_USER, DeskBoxStartupRunKey, 'DeskBox') then
      Log('DeskBox uninstall removed startup registry entry.')
    else
      Log('DeskBox uninstall failed to remove startup registry entry.');
  end;

  // Also remove the legacy startup folder shortcut.
  StartupShortcutPath := ExpandConstant('{userstartup}\DeskBox.lnk');
  if FileExists(StartupShortcutPath) then
  begin
    if DeleteFile(StartupShortcutPath) then
      Log('DeskBox uninstall removed legacy startup shortcut.')
    else
      Log('DeskBox uninstall failed to remove legacy startup shortcut.');
  end;
end;

procedure RemoveLocalAppDataRoot;
var
  AppDataRoot: string;
begin
  AppDataRoot := ExpandConstant(DeskBoxLocalAppDataRoot);
  if DirExists(AppDataRoot) then
  begin
    if DelTree(AppDataRoot, True, True, True) then
      Log('DeskBox uninstall removed local app data directory: ' + AppDataRoot)
    else
      Log('DeskBox uninstall failed to remove local app data directory: ' + AppDataRoot);
  end;
end;

procedure RemoveTaskbarPinnedShortcut;
var
  Path: string;
begin
  Path := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DeskBox.lnk');
  if FileExists(Path) then
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

function InitializeUninstall: Boolean;
begin
  Result := ConfirmManagedStoragePreserved;
  if Result then
    ShouldRemoveLocalAppData := ConfirmRemoveLocalAppData
  else
    ShouldRemoveLocalAppData := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopDeskBoxProcess;

  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStartupRegistryEntry;
    RemoveTaskbarPinnedShortcut;
    RemoveAppCompatFlag;
    if ShouldRemoveLocalAppData then
      RemoveLocalAppDataRoot
    else
      Log('DeskBox uninstall kept local app data directory.');
  end;
end;
