[Code]
const
  DeskBoxProcessName = 'DeskBox.exe';
  DeskBoxDataSettingsPath = '{localappdata}\DeskBox\data\settings.json';
  DeskBoxDefaultManagedStorageRootPath = '{%USERPROFILE}\DeskBox';
  DeskBoxAppDataRootPath = '{localappdata}\DeskBox';
  DeskBoxRecoveryRootPath = '{localappdata}\DeskBox-Recovery';
  // Identity Name of the Microsoft Store (MSIX) package declared in
  // src\DeskBox\Package.appxmanifest. The Store edition keeps its data inside
  // %USERPROFILE%\DeskBox\.appdata (and legacy MSIX LocalCache), separate from
  // the two data roots above, so this uninstaller never deletes Store data;
  // while the Store edition is present (or its state cannot be verified) those
  // roots are still kept as a conservative safety measure.
  DeskBoxStorePackageIdentityName = 'D1FC332A.DeskBoxWidgets';
  DeskBoxStorePackagesAncestorKey = 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion';
  DeskBoxStorePackagesRegistryKey = 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages';
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

function DeskBoxMultiByteToWideChar(
  CodePage: Cardinal;
  dwFlags: Cardinal;
  lpMultiByteStr: AnsiString;
  cbMultiByte: Integer;
  lpWideCharStr: string;
  cchWideChar: Integer): Integer;
  external 'MultiByteToWideChar@kernel32.dll stdcall';

function TrimString(Value: string): string;
begin
  Result := Trim(Value);
end;

function LoadUtf8StringFromFile(FileName: string; var Text: string): Boolean;
var
  RawBytes: AnsiString;
  WideLen: Integer;
begin
  Result := False;
  Text := '';
  if not LoadStringFromFile(FileName, RawBytes) then
    Exit;

  if (Length(RawBytes) >= 3) and
     (Ord(RawBytes[1]) = $EF) and
     (Ord(RawBytes[2]) = $BB) and
     (Ord(RawBytes[3]) = $BF) then
    Delete(RawBytes, 1, 3);

  if Length(RawBytes) = 0 then
  begin
    Result := True;
    Exit;
  end;

  WideLen := DeskBoxMultiByteToWideChar(65001, 0, RawBytes, Length(RawBytes), '', 0);
  if WideLen <= 0 then
  begin
    Text := string(RawBytes);
    Result := True;
    Exit;
  end;

  SetLength(Text, WideLen);
  if DeskBoxMultiByteToWideChar(65001, 0, RawBytes, Length(RawBytes), Text, WideLen) <= 0 then
    Text := string(RawBytes);
  Result := True;
end;

function TryParseHexDigit(Ch: Char; var Digit: Integer): Boolean;
begin
  if (Ch >= '0') and (Ch <= '9') then
  begin
    Digit := Ord(Ch) - Ord('0');
    Result := True;
  end
  else if (Ch >= 'a') and (Ch <= 'f') then
  begin
    Digit := 10 + (Ord(Ch) - Ord('a'));
    Result := True;
  end
  else if (Ch >= 'A') and (Ch <= 'F') then
  begin
    Digit := 10 + (Ord(Ch) - Ord('A'));
    Result := True;
  end
  else
  begin
    Digit := 0;
    Result := False;
  end;
end;

function UnescapeJsonString(Value: string): string;
var
  I: Integer;
  Len: Integer;
  NextCh: Char;
  D1, D2, D3, D4: Integer;
  CodeUnit: Integer;
begin
  Result := '';
  Len := Length(Value);
  I := 1;
  while I <= Len do
  begin
    if (Value[I] = '\') and (I < Len) then
    begin
      NextCh := Value[I + 1];
      if NextCh = '\' then
      begin
        Result := Result + '\';
        I := I + 2;
        Continue;
      end
      else if NextCh = '"' then
      begin
        Result := Result + '"';
        I := I + 2;
        Continue;
      end
      else if NextCh = '/' then
      begin
        Result := Result + '/';
        I := I + 2;
        Continue;
      end
      else if NextCh = 'n' then
      begin
        Result := Result + #10;
        I := I + 2;
        Continue;
      end
      else if NextCh = 'r' then
      begin
        Result := Result + #13;
        I := I + 2;
        Continue;
      end
      else if NextCh = 't' then
      begin
        Result := Result + #9;
        I := I + 2;
        Continue;
      end
      else if NextCh = 'b' then
      begin
        Result := Result + #8;
        I := I + 2;
        Continue;
      end
      else if NextCh = 'f' then
      begin
        Result := Result + #12;
        I := I + 2;
        Continue;
      end
      else if ((NextCh = 'u') or (NextCh = 'U')) and (I + 5 <= Len) and
              TryParseHexDigit(Value[I + 2], D1) and
              TryParseHexDigit(Value[I + 3], D2) and
              TryParseHexDigit(Value[I + 4], D3) and
              TryParseHexDigit(Value[I + 5], D4) then
      begin
        CodeUnit := (D1 shl 12) or (D2 shl 8) or (D3 shl 4) or D4;
        Result := Result + Chr(CodeUnit);
        I := I + 6;
        Continue;
      end;
    end;

    Result := Result + Value[I];
    I := I + 1;
  end;
end;

function TryReadJsonStringValue(Json: string; PropertyName: string; var Value: string): Boolean;
var
  Key: string;
  SearchOffset: Integer;
  Remaining: string;
  RelativePos: Integer;
  KeyPosition: Integer;
  ColonPosition: Integer;
  StartPosition: Integer;
  EndPosition: Integer;
  CurrentPosition: Integer;
  BackslashCount: Integer;
  Ch: string;
begin
  Result := False;
  Value := '';
  Key := '"' + PropertyName + '"';
  SearchOffset := 1;
  while SearchOffset <= Length(Json) do
  begin
    Remaining := Copy(Json, SearchOffset, MaxInt);
    RelativePos := Pos(Key, Remaining);
    if RelativePos = 0 then
      Exit;

    KeyPosition := SearchOffset + RelativePos - 1;
    ColonPosition := KeyPosition + Length(Key);
    while ColonPosition <= Length(Json) do
    begin
      Ch := Copy(Json, ColonPosition, 1);
      if (Ch = ' ') or (Ch = #9) or (Ch = #10) or (Ch = #13) then
        ColonPosition := ColonPosition + 1
      else
        Break;
    end;

    if (ColonPosition > Length(Json)) or (Copy(Json, ColonPosition, 1) <> ':') then
    begin
      SearchOffset := KeyPosition + Length(Key);
      Continue;
    end;

    StartPosition := ColonPosition + 1;
    while (StartPosition <= Length(Json)) and
          ((Copy(Json, StartPosition, 1) = ' ') or
           (Copy(Json, StartPosition, 1) = #9) or
           (Copy(Json, StartPosition, 1) = #10) or
           (Copy(Json, StartPosition, 1) = #13)) do
      StartPosition := StartPosition + 1;

    if (StartPosition > Length(Json)) or (Copy(Json, StartPosition, 1) <> '"') then
    begin
      SearchOffset := ColonPosition + 1;
      Continue;
    end;

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

    Exit;
  end;
end;

function GetManagedStorageRootPath: string;
var
  SettingsPath: string;
  Json: string;
  ConfiguredPath: string;
begin
  Result := ExpandConstant(DeskBoxDefaultManagedStorageRootPath);
  SettingsPath := ExpandConstant(DeskBoxDataSettingsPath);

  if not FileExists(SettingsPath) then
    Exit;

  if not LoadUtf8StringFromFile(SettingsPath, Json) then
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
  Json: string;
  ConfiguredPath: string;
begin
  ShortcutPath := '';
  SettingsPath := ExpandConstant(DeskBoxDataSettingsPath);

  if not FileExists(SettingsPath) then
    Exit;

  if not LoadUtf8StringFromFile(SettingsPath, Json) then
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

function ProbeStoreDeskBoxRegistryKey(
  RootKey: Integer;
  SubKeyName: string;
  VerifiedAncestorKey: string): Integer;
var
  PackageNames: TArrayOfString;
  ChildNames: TArrayOfString;
  PackagePrefix: string;
  PackageName: string;
  CurrentKey: string;
  Remaining: string;
  Segment: string;
  SeparatorPos: Integer;
  FoundChild: Boolean;
  Index: Integer;
begin
  Result := StoreDeskBoxStateUnknown;
  if RegGetSubkeyNames(RootKey, SubKeyName, PackageNames) then
  begin
    PackagePrefix := DeskBoxStorePackageIdentityName + '_';
    for Index := 0 to GetArrayLength(PackageNames) - 1 do
    begin
      PackageName := PackageNames[Index];
      if (CompareText(PackageName, DeskBoxStorePackageIdentityName) = 0) or
         (CompareText(Copy(PackageName, 1, Length(PackagePrefix)), PackagePrefix) = 0) then
      begin
        Result := StoreDeskBoxStateInstalled;
        Log('DeskBox uninstall detected the Microsoft Store edition for the current user: ' + PackageName);
        Exit;
      end;
    end;

    Result := StoreDeskBoxStateNotInstalled;
    Log('DeskBox uninstall found no Microsoft Store edition for the current user.');
    Exit;
  end;

  // On fresh local accounts or IoT/Enterprise builds where AppXSvc exists but
  // no AppX package was ever registered in this profile, AppModel\Repository\Packages
  // may not be created yet. Verify ancestor readability so genuine absence reports
  // NotInstalled while ACL/hive failures stay Unknown (fail closed).
  if (VerifiedAncestorKey <> '') and
     (Length(SubKeyName) > Length(VerifiedAncestorKey) + 1) and
     (CompareText(Copy(SubKeyName, 1, Length(VerifiedAncestorKey) + 1), VerifiedAncestorKey + '\') = 0) then
  begin
    CurrentKey := VerifiedAncestorKey;
    Remaining := Copy(SubKeyName, Length(VerifiedAncestorKey) + 2, MaxInt);
    while Remaining <> '' do
    begin
      SeparatorPos := Pos('\', Remaining);
      if SeparatorPos > 0 then
      begin
        Segment := Copy(Remaining, 1, SeparatorPos - 1);
        Remaining := Copy(Remaining, SeparatorPos + 1, MaxInt);
      end
      else
      begin
        Segment := Remaining;
        Remaining := '';
      end;

      if not RegGetSubkeyNames(RootKey, CurrentKey, ChildNames) then
        Exit;

      FoundChild := False;
      for Index := 0 to GetArrayLength(ChildNames) - 1 do
      begin
        if CompareText(ChildNames[Index], Segment) = 0 then
        begin
          FoundChild := True;
          Break;
        end;
      end;

      if not FoundChild then
      begin
        Result := StoreDeskBoxStateNotInstalled;
        Log('DeskBox uninstall confirmed AppModel repository subkey "' + Segment + '" is absent for the current user; no Microsoft Store edition is installed.');
        Exit;
      end;

      CurrentKey := CurrentKey + '\' + Segment;
    end;
  end;
end;

function GetStoreDeskBoxInstallState: Integer;
begin
  // Three-state probe for the Microsoft Store edition. The Store edition
  // stores its data in its own MSIX LocalCache, not in the data roots this
  // uninstaller manages, so the result only decides whether the direct
  // edition's data may be purged. Enumerating HKCU AppModel\Repository\Packages
  // inspects only packages registered for the current user without spawning any
  // external child process: purging only happens outside admin install mode
  // (see InitializeUninstall), and even an all-users uninstaller is elevated
  // from the initiating user's session, so that user's package registration is
  // the one that matters. A failed registry enumeration never silently becomes
  // "not installed": systems without the AppX platform are short-circuited by
  // the pre-check below, and every other failure stays Unknown (fail closed).
  Result := StoreDeskBoxStateUnknown;

  // Platform pre-check, using only native Inno Setup calls so no extra
  // process is spawned. LTSC and stripped-down Windows ship without the AppX
  // platform: with neither the %WINDIR%\System32\WindowsApps directory nor
  // the AppXSvc service present, no Store edition can be installed at all,
  // so the purge may proceed. On an AppX-capable system where the package
  // repository cannot be enumerated, the state stays Unknown and callers fail closed.
  if (not DirExists(ExpandConstant('{win}\System32\WindowsApps'))) and
     (not DirExists(ExpandConstant('{commonpf}\WindowsApps'))) and
     (not RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\AppXSvc')) then
  begin
    Result := StoreDeskBoxStateNotInstalled;
    Log('DeskBox uninstall found no AppX platform (WindowsApps directory and AppXSvc service are absent); the Microsoft Store edition cannot be installed.');
    Exit;
  end;

  Result := ProbeStoreDeskBoxRegistryKey(
    HKEY_CURRENT_USER,
    DeskBoxStorePackagesRegistryKey,
    DeskBoxStorePackagesAncestorKey);
  if Result = StoreDeskBoxStateUnknown then
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

function IsOwnedStartupExecutableName(FileName: string): Boolean;
begin
  Result :=
    (CompareText(FileName, DeskBoxProcessName) = 0) or
    (CompareText(FileName, 'DeskBox.Settings.exe') = 0) or
    (CompareText(FileName, 'DeskBox.Updater.exe') = 0);
end;

function ShortcutTargetsCurrentInstall(ShortcutPath: string): Boolean;
var
  TargetPath: string;
begin
  Result :=
    TryReadShortcutTarget(ShortcutPath, TargetPath) and
    SameInstallPath(ExtractFileDir(TargetPath), ExpandConstant('{app}')) and
    IsOwnedStartupExecutableName(ExtractFileName(TargetPath));
end;

procedure RemoveStartupRunForRoot(
  RootKey: Integer;
  RunKey: string;
  ApprovedKey: string;
  ScopeLabel: string;
  AllowOrphanApprovedCleanup: Boolean);
var
  Value: string;
  StartupExecutablePath: string;
begin
  if RegQueryStringValue(RootKey, RunKey, 'DeskBox', Value) then
  begin
    StartupExecutablePath := ExtractExecutablePath(Value);
    if SameInstallPath(ExtractFileDir(StartupExecutablePath), ExpandConstant('{app}')) and
       IsOwnedStartupExecutableName(ExtractFileName(StartupExecutablePath)) and
       RegDeleteValue(RootKey, RunKey, 'DeskBox') then
    begin
      RegDeleteValue(RootKey, ApprovedKey, 'DeskBox');
      Log('DeskBox uninstall removed startup registry entry (' + ScopeLabel + ').');
    end
    else
      Log('DeskBox uninstall preserved startup registry entry owned by another DeskBox installation (' + ScopeLabel + ').');
  end
  else if AllowOrphanApprovedCleanup and PurgeDeskBoxAppData then
    RegDeleteValue(RootKey, ApprovedKey, 'DeskBox');
end;

procedure RemoveOwnedStartupShortcut(ShortcutPath: string; ScopeLabel: string);
begin
  if ShortcutTargetsCurrentInstall(ShortcutPath) then
  begin
    if DeleteFile(ShortcutPath) then
      Log('DeskBox uninstall removed legacy startup shortcut (' + ScopeLabel + ').')
    else
      Log('DeskBox uninstall failed to remove legacy startup shortcut (' + ScopeLabel + ').');
  end;
end;

procedure RemoveStartupRegistryEntry;
var
  UserSids: TArrayOfString;
  Index: Integer;
  Sid: string;
begin
  RemoveStartupRunForRoot(
    HKEY_CURRENT_USER,
    DeskBoxStartupRunKey,
    DeskBoxStartupApprovedRunKey,
    'HKCU',
    True);

  if IsAdminInstallMode and RegGetSubkeyNames(HKEY_USERS, '', UserSids) then
  begin
    for Index := 0 to GetArrayLength(UserSids) - 1 do
    begin
      Sid := UserSids[Index];
      if IsLoadedUserAccountSid(Sid) then
      begin
        RemoveStartupRunForRoot(
          HKEY_USERS,
          Sid + '\' + DeskBoxStartupRunKey,
          Sid + '\' + DeskBoxStartupApprovedRunKey,
          'HKU\' + Sid,
          False);
      end;
    end;
  end;

  // Also remove ownership-checked legacy startup folder shortcuts.
  RemoveOwnedStartupShortcut(ExpandConstant('{userstartup}\DeskBox.lnk'), 'user');
  if IsAdminInstallMode then
    RemoveOwnedStartupShortcut(ExpandConstant('{commonstartup}\DeskBox.lnk'), 'common');
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
      try
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
            ActionPath := UnquoteExecutablePath(Action.Path);
            if SameInstallPath(ExtractFileDir(ActionPath), ExpandConstant('{app}')) and
               IsOwnedStartupExecutableName(ExtractFileName(ActionPath)) then
            begin
              RootFolder.DeleteTask(TaskName, 0);
              Log('DeskBox uninstall removed startup scheduled task: ' + TaskName);
            end
            else
              Log('DeskBox uninstall preserved a startup task owned by another DeskBox installation: ' + TaskName);
          end;
        end;
      except
        Log('DeskBox uninstall skipped a startup scheduled task that could not be inspected.');
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
