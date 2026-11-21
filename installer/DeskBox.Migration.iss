[Code]
const
  DeskBoxAppCompatLayersKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers';

var
  PreExistingInstallDirDetected: Boolean;

function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): string; forward;

#if DeskBoxBundledRuntime
function StripLeadingUtf8BomChar(Value: string): string;
begin
  Result := Value;
  if (Length(Result) > 0) and (Result[1] = #$FEFF) then
    Delete(Result, 1, 1);
end;

function NormalizeDeskBoxManifestEntry(Value: string; var Normalized: string): Boolean;
var
  Trimmed: string;
  Remaining: string;
  Segment: string;
  Built: string;
  SeparatorPos: Integer;
begin
  Result := False;
  Normalized := '';
  Trimmed := Trim(StripLeadingUtf8BomChar(Value));
  if (Trimmed = '') or (Copy(Trimmed, 1, 1) = '#') then
    Exit;

  StringChangeEx(Trimmed, '/', '\', True);
  if (Copy(Trimmed, 1, 1) = '\') or (Pos(':', Trimmed) > 0) then
    Exit;

  Built := '';
  Remaining := Trimmed;
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

    if Segment = '' then
      Continue;

    if (Segment = '.') or (Segment = '..') then
      Exit;

    if Built = '' then
      Built := Segment
    else
      Built := Built + '\' + Segment;
  end;

  if Built = '' then
    Exit;

  Normalized := Built;
  Result := True;
end;

procedure ReadNormalizedDeskBoxManifestEntries(
  RootPath: string;
  ManifestPath: string;
  Entries: TStringList);
var
  RawLines: TArrayOfString;
  Index: Integer;
  Normalized: string;
  CandidateFullPath: string;
begin
  Entries.Clear;
  Entries.CaseSensitive := False;
  Entries.Duplicates := dupIgnore;
  Entries.Sorted := True;

  if not LoadStringsFromFile(ManifestPath, RawLines) then
    RaiseException('DeskBox could not read install manifest: ' + ManifestPath);

  for Index := 0 to GetArrayLength(RawLines) - 1 do
  begin
    if not NormalizeDeskBoxManifestEntry(RawLines[Index], Normalized) then
      Continue;

    CandidateFullPath := AddBackslash(RootPath) + Normalized;
    if not IsDirectoryWithinRoot(ExtractFileDir(CandidateFullPath), RootPath) then
      Continue;

    Entries.Add(Normalized);
  end;
end;

procedure PruneEmptyParentDirectories(DirectoryPath: string; StopRootPath: string);
var
  CurrentDir: string;
  NormalizedStop: string;
  ParentDir: string;
begin
  CurrentDir := RemoveBackslashUnlessRoot(DirectoryPath);
  NormalizedStop := RemoveBackslashUnlessRoot(StopRootPath);
  while (CurrentDir <> '') and
        (CompareText(CurrentDir, NormalizedStop) <> 0) and
        (Length(CurrentDir) > Length(NormalizedStop)) and
        (CompareText(Copy(CurrentDir, 1, Length(NormalizedStop) + 1), NormalizedStop + '\') = 0) do
  begin
    if IsPathReparsePoint(CurrentDir) then
      Break;
    if not RemoveDir(CurrentDir) then
      Break;
    ParentDir := RemoveBackslashUnlessRoot(ExtractFileDir(CurrentDir));
    if CompareText(ParentDir, CurrentDir) = 0 then
      Break;
    CurrentDir := ParentDir;
  end;
end;

procedure RemoveStaleDeskBoxInstallEntries(
  RootPath: string;
  CandidateManifestPath: string;
  CurrentEntries: TStringList);
var
  RawLines: TArrayOfString;
  ProcessedEntries: TStringList;
  Index: Integer;
  RawTrimmed: string;
  Normalized: string;
  CandidateFullPath: string;
  FailedCount: Integer;
  FailedSummary: string;
begin
  if not LoadStringsFromFile(CandidateManifestPath, RawLines) then
    RaiseException('DeskBox could not read cleanup manifest: ' + CandidateManifestPath);

  FailedCount := 0;
  FailedSummary := '';
  ProcessedEntries := TStringList.Create;
  try
    ProcessedEntries.CaseSensitive := False;
    ProcessedEntries.Duplicates := dupIgnore;
    ProcessedEntries.Sorted := True;

    for Index := 0 to GetArrayLength(RawLines) - 1 do
    begin
      RawTrimmed := Trim(StripLeadingUtf8BomChar(RawLines[Index]));
      if (RawTrimmed = '') or (Copy(RawTrimmed, 1, 1) = '#') then
        Continue;

      if not NormalizeDeskBoxManifestEntry(RawTrimmed, Normalized) then
      begin
        Log('Ignoring unsafe DeskBox install manifest entry: ' + RawTrimmed);
        Continue;
      end;

      CandidateFullPath := AddBackslash(RootPath) + Normalized;
      if not IsDirectoryWithinRoot(ExtractFileDir(CandidateFullPath), RootPath) then
      begin
        Log('Ignoring out-of-root DeskBox install manifest entry: ' + RawTrimmed);
        Continue;
      end;

      if ProcessedEntries.IndexOf(Normalized) >= 0 then
        Continue;
      ProcessedEntries.Add(Normalized);

      if CurrentEntries.IndexOf(Normalized) >= 0 then
        Continue;

      if not FileExists(CandidateFullPath) then
        Continue;

      if IsPathReparsePoint(CandidateFullPath) or IsPathReparsePoint(ExtractFileDir(CandidateFullPath)) then
      begin
        Log('Skipping reparse-point entry during stale install cleanup: ' + Normalized);
        Continue;
      end;

      if DeleteFile(CandidateFullPath) or (not FileExists(CandidateFullPath)) then
      begin
        Log('Removed stale DeskBox install file: ' + Normalized);
        PruneEmptyParentDirectories(ExtractFileDir(CandidateFullPath), RootPath);
      end
      else
      begin
        FailedCount := FailedCount + 1;
        if FailedSummary = '' then
          FailedSummary := Normalized
        else
          FailedSummary := FailedSummary + '; ' + Normalized;
      end;
    end;
  finally
    ProcessedEntries.Free;
  end;

  if FailedCount > 0 then
    RaiseException(Format(
      'DeskBox install cleanup could not remove %d stale file(s): %s', [FailedCount, FailedSummary]));
end;

procedure RunDeskBoxInstallCleanup(
  InstallRoot: string;
  CurrentManifestPath: string;
  LegacyManifestPath: string;
  PreviousManifestPath: string);
var
  NormalizedRoot: string;
  DriveRoot: string;
  CurrentEntries: TStringList;
begin
  NormalizedRoot := NormalizeDirPath(InstallRoot);
  if (NormalizedRoot = '') or (not DirExists(NormalizedRoot)) then
    RaiseException('The DeskBox install root does not exist: ' + InstallRoot);

  DriveRoot := NormalizeDirPath(ExtractFileDrive(NormalizedRoot));
  if (DriveRoot <> '') and (CompareText(NormalizedRoot, DriveRoot) = 0) then
    RaiseException('Refusing to clean a volume root: ' + NormalizedRoot);

  if not FileExists(AddBackslash(NormalizedRoot) + DeskBoxLegacyExeName) then
    RaiseException('The selected install root does not contain DeskBox.exe: ' + NormalizedRoot);

  if (Trim(CurrentManifestPath) = '') or (not FileExists(CurrentManifestPath)) then
    RaiseException('The current DeskBox install manifest is missing: ' + CurrentManifestPath);

  CurrentEntries := TStringList.Create;
  try
    ReadNormalizedDeskBoxManifestEntries(NormalizedRoot, CurrentManifestPath, CurrentEntries);
    if CurrentEntries.IndexOf('DeskBox.exe') < 0 then
      RaiseException('The current DeskBox install manifest is invalid because ''DeskBox.exe'' is missing.');
    if CurrentEntries.IndexOf('DeskBox.InstallManifest.txt') < 0 then
      RaiseException('The current DeskBox install manifest is invalid because ''DeskBox.InstallManifest.txt'' is missing.');

    if (Trim(LegacyManifestPath) = '') or (not FileExists(LegacyManifestPath)) then
      RaiseException('The DeskBox legacy install manifest is missing: ' + LegacyManifestPath);

    if (Trim(PreviousManifestPath) <> '') and FileExists(PreviousManifestPath) then
    begin
      RemoveStaleDeskBoxInstallEntries(NormalizedRoot, PreviousManifestPath, CurrentEntries);
      Log('DeskBox install cleanup completed using the previous manifest.');
    end
    else
    begin
      RemoveStaleDeskBoxInstallEntries(NormalizedRoot, LegacyManifestPath, CurrentEntries);
      Log('DeskBox legacy bundled-runtime cleanup completed using the exact compatibility manifest.');
    end;
  finally
    CurrentEntries.Free;
  end;
end;

procedure CleanupDeskBoxInstall;
var
  CurrentManifestPath: string;
  LegacyManifestPath: string;
  PreviousManifestPath: string;
begin
  if not DirectInstallUpgrade then
  begin
    if not PreExistingInstallDirDetected then
    begin
      Log('DeskBox first-install path detected; stale install cleanup skipped.');
      Exit;
    end;
    Log('Pre-existing DeskBox install directory detected at wizard target; running stale install cleanup.');
  end;

  ExtractTemporaryFile('DeskBox.LegacyBundledRuntimeFiles.txt');

  CurrentManifestPath := ExpandConstant('{tmp}\DeskBox.InstallManifest.current.txt');
  LegacyManifestPath := ExpandConstant('{tmp}\DeskBox.LegacyBundledRuntimeFiles.txt');
  PreviousManifestPath := AddBackslash(WizardDirValue) + 'DeskBox.InstallManifest.txt';

  RunDeskBoxInstallCleanup(
    WizardDirValue,
    CurrentManifestPath,
    LegacyManifestPath,
    PreviousManifestPath);

  Log('DeskBox stale install-file cleanup completed successfully.');
end;
#endif

procedure DeleteAppCompatLayerValue(RootKey: Integer; ExePath: string);
var
  Value: string;
begin
  if ExePath = '' then
    Exit;

  if RegQueryStringValue(RootKey, DeskBoxAppCompatLayersKey, ExePath, Value) and
     (Pos('RUNASADMIN', Uppercase(Value)) > 0) then
  begin
    if RegDeleteValue(RootKey, DeskBoxAppCompatLayersKey, ExePath) then
      Log('DeskBox installer removed AppCompat RUNASADMIN value: ' + ExePath)
    else
      Log('DeskBox installer could not remove AppCompat value: ' + ExePath);
  end;
end;

procedure CleanupInstallAppCompatFlags(InstallPath: string);
var
  ExePath: string;
begin
  ExePath := AddBackslash(NormalizeDirPath(InstallPath)) + DeskBoxLegacyExeName;
  DeleteAppCompatLayerValue(HKEY_CURRENT_USER, AddBackslash(NormalizeDirPath(InstallPath)) + 'DeskBox.Settings.exe');
  DeleteAppCompatLayerValue(HKEY_CURRENT_USER, ExePath);

  if IsAdminInstallMode then
  begin
    DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, ExePath);
    DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, AddBackslash(NormalizeDirPath(InstallPath)) + 'DeskBox.Settings.exe');
  end;
end;

function InitializeSetup: Boolean;
begin
  // Program Files is now the supported all-users location. Older installations
  // found there are upgraded in place instead of being treated as disposable
  // legacy copies.
  Result := PrepareDirectInstallPlan;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := DirectInstallUpgrade and (PageID = wpSelectDir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  DependencyError: string;
  Index: Integer;
  UnregisteredPath: string;
  StoppedAnyProcess: Boolean;
begin
  Result := '';
  PreExistingInstallDirDetected :=
    FileExists(AddBackslash(WizardDirValue) + 'DeskBox.InstallManifest.txt') or
    FileExists(AddBackslash(WizardDirValue) + DeskBoxLegacyExeName);
  StoppedAnyProcess := DirectInstallUpgrade or PreExistingInstallDirDetected;

  // Close the DeskBox process belonging to the target install directory as well as
  // any unregistered portable/unpacked DeskBox.exe holding the single-instance mutex.
  if not StopDeskBoxProcessesAtPath(WizardDirValue) then
    Log('DeskBox path-scoped process shutdown failed; Setup will continue with Restart Manager handling file locks.');

  for Index := 0 to GetArrayLength(RunningUnregisteredProcessPaths) - 1 do
  begin
    UnregisteredPath := RunningUnregisteredProcessPaths[Index];
    if (UnregisteredPath <> '') and (not SameInstallPath(UnregisteredPath, WizardDirValue)) then
    begin
      StoppedAnyProcess := True;
      if not StopDeskBoxProcessesAtPath(UnregisteredPath) then
        Log('DeskBox unregistered process shutdown failed for: ' + UnregisteredPath);
    end;
  end;

  // Give the process time to fully exit before Restart Manager / post-install launch runs.
  if StoppedAnyProcess then
    Sleep(2000);
  Log('DeskBox process termination completed.');

  // DeskBox must continue to run at normal user integrity so Explorer drag and
  // drop remains available after an upgrade from older Program Files builds.
  CleanupInstallAppCompatFlags(WizardDirValue);

  DependencyError := PrepareDeskBoxDependencies(NeedsRestart);
  if DependencyError <> '' then
    Result := DependencyError;
end;
