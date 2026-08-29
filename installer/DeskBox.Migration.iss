[Code]
const
  DeskBoxAppCompatLayersKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers';

function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): string; forward;

#if DeskBoxBundledRuntime
function QuoteDeskBoxCleanupArgument(Value: string): string;
begin
  Result := '"' + Value + '"';
end;

procedure CleanupDeskBoxInstall;
var
  PowerShellPath: string;
  CleanupScriptPath: string;
  CurrentManifestPath: string;
  LegacyManifestPath: string;
  PreviousManifestPath: string;
  Parameters: string;
  ResultCode: Integer;
begin
  if not DirectInstallUpgrade then
  begin
    Log('DeskBox first-install path detected; stale install cleanup skipped.');
    Exit;
  end;

  ExtractTemporaryFile('cleanup-deskbox-install.ps1');
  ExtractTemporaryFile('DeskBox.LegacyBundledRuntimeFiles.txt');

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  CleanupScriptPath := ExpandConstant('{tmp}\cleanup-deskbox-install.ps1');
  CurrentManifestPath := ExpandConstant('{tmp}\DeskBox.InstallManifest.current.txt');
  LegacyManifestPath := ExpandConstant('{tmp}\DeskBox.LegacyBundledRuntimeFiles.txt');
  PreviousManifestPath := AddBackslash(WizardDirValue) + 'DeskBox.InstallManifest.txt';
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    QuoteDeskBoxCleanupArgument(CleanupScriptPath) +
    ' -InstallRoot ' + QuoteDeskBoxCleanupArgument(WizardDirValue) +
    ' -CurrentManifestPath ' + QuoteDeskBoxCleanupArgument(CurrentManifestPath) +
    ' -LegacyManifestPath ' + QuoteDeskBoxCleanupArgument(LegacyManifestPath) +
    ' -PreviousManifestPath ' + QuoteDeskBoxCleanupArgument(PreviousManifestPath);

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('DeskBox could not start the install cleanup process.');

  if ResultCode <> 0 then
    RaiseException(Format('DeskBox install cleanup failed with exit code %d.', [ResultCode]));

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
  DeleteAppCompatLayerValue(HKEY_CURRENT_USER, ExePath);

  if IsAdminInstallMode then
    DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, ExePath);
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
begin
  Result := '';

  // Close only the DeskBox process that belongs to the install being updated.
  // Restart Manager remains the final file-lock fallback for Setup itself.
  if not StopDeskBoxProcessesAtPath(WizardDirValue) then
    Log('DeskBox path-scoped process shutdown failed; Setup will continue with Restart Manager handling file locks.');

  // Give the process time to fully exit before Restart Manager runs.
  Sleep(2000);
  Log('DeskBox process termination completed.');

  // DeskBox must continue to run at normal user integrity so Explorer drag and
  // drop remains available after an upgrade from older Program Files builds.
  CleanupInstallAppCompatFlags(WizardDirValue);

  DependencyError := PrepareDeskBoxDependencies(NeedsRestart);
  if DependencyError <> '' then
    Result := DependencyError;
end;
