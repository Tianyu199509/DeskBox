namespace DeskBox.Tests;

public sealed class InstallerUninstallContractTests
{
    private static readonly string[] InstallerLanguages =
    [
        "english",
        "chinesesimplified",
        "chinesetraditional",
        "japanese",
        "german",
        "brazilianportuguese",
        "hindi",
        "spanish",
        "french",
        "arabic",
        "bengali",
        "russian",
        "korean"
    ];

    private static readonly string[] AppDataChoiceMessages =
    [
        "AppDataChoiceTitle",
        "ConfirmRemoveAppData",
        "KeepAppDataButton",
        "RemoveAppDataButton",
        "AppDataCleanupFailed"
    ];

    private static readonly string[] ManagedStorageShortcutMessages =
    [
        "ManagedStorageShortcutPrompt",
        "ManagedStorageShortcutCreateFailed"
    ];

    private static readonly string[] StoreEditionMessages =
    [
        "StoreEditionDataPreserved",
        "StoreEditionStateUnknown"
    ];

    private static readonly string[] DependencyMessages =
    [
        "DependencyDownloadCancelled",
        "DependencyDownloadFailed",
        "DependencyDownloadFailedSummary",
        "DependencyInstallStartFailed",
        "DependencyInstallFailed",
        "DependencyInstallFailedSummary",
        "RuntimeDependencyComment"
    ];

    [Fact]
    public void Uninstall_OffersSafeKeepOrPurgeChoice()
    {
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");

        Assert.Contains("ChooseAppDataRemoval", code, StringComparison.Ordinal);
        Assert.Contains("SuppressibleTaskDialogMsgBox", code, StringComparison.Ordinal);
        Assert.Contains("IDYES", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxPurgeUserDataParameter = '/PURGEUSERDATA'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxAppDataRootPath = '{localappdata}\\DeskBox'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRecoveryRootPath = '{localappdata}\\DeskBox-Recovery'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxTemporaryRootPath = '{%TEMP}\\DeskBox'", code, StringComparison.Ordinal);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON2", code, StringComparison.Ordinal);
        Assert.Contains("RemoveNotificationRegistration", code, StringComparison.Ordinal);
        Assert.Contains("if ActivatorId <> '' then", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Format(ExpandConstant('{cm:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree(GetManagedStorageRootPath", code, StringComparison.Ordinal);
        Assert.Contains("OfferManagedStorageShortcut", code, StringComparison.Ordinal);
        Assert.Contains("managedStorageDesktopShortcutPath", code, StringComparison.Ordinal);
        Assert.Contains("GetManagedStorageShortcutPath", code, StringComparison.Ordinal);
        Assert.DoesNotContain("managedStorageDesktopShortcutEnabled", code, StringComparison.Ordinal);
        Assert.DoesNotContain("if not ShortcutEnabled", code, StringComparison.Ordinal);
        Assert.Contains("if FileExists(ExpandConstant(DeskBoxDataSettingsPath)) then", code, StringComparison.Ordinal);
        int adminShortcutGuard = code.IndexOf(
            "if FileExists(ExpandConstant(DeskBoxDataSettingsPath)) then",
            code.IndexOf("if IsAdminInstallMode then", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int adminStorageConfirm = code.IndexOf("Result := ConfirmManagedStoragePreserved;", adminShortcutGuard, StringComparison.Ordinal);
        int adminShortcutOffer = code.IndexOf("OfferManagedStorageShortcut", adminShortcutGuard, StringComparison.Ordinal);
        int adminAppDataChoice = code.IndexOf("Result := ChooseAppDataRemoval;", adminShortcutOffer, StringComparison.Ordinal);
        int adminBranchResult = code.IndexOf("Result := True;", adminAppDataChoice, StringComparison.Ordinal);
        Assert.True(
            adminShortcutGuard >= 0 &&
            adminStorageConfirm > adminShortcutGuard &&
            adminShortcutOffer > adminStorageConfirm &&
            adminAppDataChoice > adminShortcutOffer &&
            adminBranchResult > adminAppDataChoice,
            "The default all-users uninstall path should confirm managed storage, offer the shortcut, and offer the app-data keep/remove choice when the initiating profile has DeskBox settings.");
        int postUninstallStep = code.IndexOf(
            "if CurUninstallStep = usPostUninstall then",
            StringComparison.Ordinal);
        Assert.True(postUninstallStep >= 0);
        string postUninstallBody = code[postUninstallStep..];
        Assert.DoesNotContain("if IsAdminInstallMode then", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("RemoveStartupRegistryEntry;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("RemoveTaskbarPinnedShortcut;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("RemoveAppCompatFlag;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("RemoveNotificationRegistration;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("RemoveDeskBoxProtocolRegistration;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("DeskBoxProtocolRegistryKey = 'Software\\Classes\\deskbox'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxProtocolRegistryKey + '\\shell\\open\\command'", code, StringComparison.Ordinal);
        Assert.Contains("RemoveDeskBoxDataDirectories;", postUninstallBody, StringComparison.Ordinal);
        Assert.Contains("CreateOleObject('WScript.Shell')", code, StringComparison.Ordinal);
        Assert.Contains("ShortcutObject.TargetPath := FolderPath", code, StringComparison.Ordinal);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON1", code, StringComparison.Ordinal);
        Assert.Contains("if CurrentProfileHasDeskBoxData then", code, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(code, "UninstallSilent"));
        Assert.Contains("RemoveOwnedNotificationActivators", code, StringComparison.Ordinal);
        Assert.Contains("RegGetSubkeyNames(", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxStartupApprovedRunKey", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxFeedbackTemporaryRootPath = '{%TEMP}\\DeskBoxFeedback'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSettingsHostTraceLogPath = '{%TEMP}\\deskbox-settings-host.log'", code, StringComparison.Ordinal);
        Assert.Contains("3bd2d2b294e73d6e.customDestinations-ms", code, StringComparison.Ordinal);
        Assert.Contains("RemoveAppCompatFlagForRootAndExe(HKEY_LOCAL_MACHINE, ExePath)", code, StringComparison.Ordinal);
        Assert.Contains("RemoveAppCompatFlagForRootAndExe(HKEY_LOCAL_MACHINE, SettingsExePath)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_RemovesOnlyTheStartupTaskOwnedByCurrentInstall()
    {
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");

        Assert.Contains(
            "DeskBoxStartupTaskNamePrefix = 'DeskBox User Startup'",
            code,
            StringComparison.Ordinal);
        Assert.Contains("CreateOleObject('Schedule.Service')", code, StringComparison.Ordinal);
        Assert.Contains("TaskDefinition.Actions", code, StringComparison.Ordinal);
        Assert.Contains(
            "SameInstallPath(ExtractFileDir(ActionPath), ExpandConstant('{app}'))",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompareText(ExtractFileName(ActionPath), DeskBoxProcessName)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "RootFolder.DeleteTask(TaskName, 0)",
            code,
            StringComparison.Ordinal);
        Assert.True(
            code.LastIndexOf("RemoveStartupScheduledTasks;", StringComparison.Ordinal) <
            code.LastIndexOf("RemoveStartupRegistryEntry;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("installer/DeskBox.iss")]
    [InlineData("installer/DeskBox.arm64.iss")]
    public void DirectInstaller_SupportsMachineAndCurrentUserScopes(string scriptPath)
    {
        string installer = ReadRepositoryFile(scriptPath);
        string installation = ReadRepositoryFile("installer/DeskBox.Installation.iss");
        string migration = ReadRepositoryFile("installer/DeskBox.Migration.iss");

        Assert.Contains("PrivilegesRequired=admin", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog commandline", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousPrivileges=yes", installer, StringComparison.Ordinal);
        Assert.Contains("SetupLogging=yes", installer, StringComparison.Ordinal);
        Assert.Contains("UninstallLogging=yes", installer, StringComparison.Ordinal);
        Assert.Contains("{autoprograms}", installer, StringComparison.Ordinal);
        Assert.Contains("{autodesktop}", installer, StringComparison.Ordinal);
        Assert.Contains("Root: HKA; Subkey: \"Software\\DeskBox\\DirectInstall\"", installer, StringComparison.Ordinal);
        Assert.Contains("ValueName: \"InstallScope\"", installer, StringComparison.Ordinal);
        Assert.Contains("ValueName: \"InstallLanguage\"; ValueData: \"{code:InstallLanguageCode}\"", installer, StringComparison.Ordinal);
        Assert.Contains("if (CurStep = ssPostInstall) and (not IsAdminInstallMode) then", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Root: HKCU; Subkey: \"Software\\DeskBox\"; ValueType: string; ValueName: \"InstallLanguage\"", installer, StringComparison.Ordinal);
        Assert.Contains("ExpandConstant('{autopf}\\DeskBox')", installation, StringComparison.Ordinal);
        Assert.Contains("HKEY_LOCAL_MACHINE, DeskBoxInstallStateKey", installation, StringComparison.Ordinal);
        Assert.Contains("IsPerUserProfileInstallPath(ExistingInstallPath)", installation, StringComparison.Ordinal);
        Assert.Contains("{cm:PerUserUpgradeScopeMismatch}", installation, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree(LegacyInstallPath", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DeskBoxAdminCleanupParam", migration, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(installer, "AppUserModelID: \"DeskBox.DeskBox\""));
        Assert.Contains("'DeskBox.Settings.exe'", installation, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Name", installation, StringComparison.Ordinal);
        Assert.Contains("Wait-Process -Id", installation, StringComparison.Ordinal);
        Assert.Contains("DeskBox.Settings.exe", migration, StringComparison.Ordinal);
        string stage7c1 = ReadRepositoryFile("scripts/build-stage-7c1-distribution.ps1");
        Assert.Contains(
            "Join-Path $directPublishDirectory \"DeskBox.Settings.exe\"",
            stage7c1,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("installer/DeskBox.Installation.iss")]
    [InlineData("installer/DeskBox.Dependencies.iss")]
    [InlineData("installer/DeskBox.Dependencies.arm64.iss")]
    public void InstallerCustomMessagePlaceholders_UseFmtMessage(string path)
    {
        string code = ReadRepositoryFile(path);

        Assert.DoesNotContain("Format(ExpandConstant('{cm:", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallChoice_IsLocalizedForEveryInstallerLanguage()
    {
        string messages =
            ReadRepositoryFile("installer/DeskBox.iss") + Environment.NewLine +
            ReadRepositoryFile("installer/DeskBox.NewLanguageCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in AppDataChoiceMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DependencyMessages_AreLocalizedForEveryInstallerLanguage()
    {
        string messages = ReadRepositoryFile("installer/DeskBox.DependencyCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in DependencyMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void FullRetailInstallers_SkipExternalDependenciesAndKeepStandardNames()
    {
        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            Assert.Contains("#ifndef DeskBoxBundledRuntime", content, StringComparison.Ordinal);
            Assert.Contains("#define MyAppPackageSuffix \"\"", content, StringComparison.Ordinal);
            Assert.Contains("{#MyAppPackageSuffix}", content, StringComparison.Ordinal);
            Assert.Contains("DeskBox.InstallManifest.txt", content, StringComparison.Ordinal);
            Assert.Contains("BeforeInstall: CleanupDeskBoxInstall", content, StringComparison.Ordinal);
            Assert.Contains(
                $"OutputBaseFilename={{#MyAppOutputBaseName}}_{{#MyAppVersion}}_{(script.EndsWith("arm64.iss", StringComparison.Ordinal) ? "arm64" : "x64")}",
                content,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Type: filesandordirs; Name: \"{app}\\Microsoft.WindowsAppRuntime\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "#define MyAppPackageSuffix \"_Full\"",
                content,
                StringComparison.Ordinal);
        }

        string distribution = ReadRepositoryFile("scripts/build-stage-7c1-distribution.ps1");
        Assert.Contains("\"/DDeskBoxBundledRuntime=1\"", distribution, StringComparison.Ordinal);
        Assert.Contains(
            "$installerOutputBaseName = \"{0}_{1}_{2}\"",
            distribution,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_Full.exe", distribution, StringComparison.Ordinal);

        foreach (string dependencyScript in new[]
                 {
                     "installer/DeskBox.Dependencies.iss",
                     "installer/DeskBox.Dependencies.arm64.iss"
                 })
        {
            string content = ReadRepositoryFile(dependencyScript);
            Assert.Contains("#if DeskBoxBundledRuntime", content, StringComparison.Ordinal);
            Assert.Contains("external runtime dependency setup skipped", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeShortcutModule_IsPackagedByX64AndArm64NativeAotInstallers()
    {
        string x64 = ReadRepositoryFile("installer/DeskBox.iss");
        string arm64 = ReadRepositoryFile("installer/DeskBox.arm64.iss");
        string retailPublish = ReadRepositoryFile("scripts/publish-aot-retail.ps1");
        const string nativeSource =
            "Source: \"{#MyAppReleaseDir}\\deskbox_native.dll\"; DestDir: \"{app}\"; Flags: ignoreversion";
        const string nativeExclusions =
            "Excludes: \"DeskBox.Updater.*,deskbox_native.dll,deskbox_native.pdb\"";

        Assert.Contains("-p:DeskBoxRustNative=true", retailPublish, StringComparison.Ordinal);
        Assert.Contains(nativeExclusions, x64, StringComparison.Ordinal);
        Assert.Contains(nativeSource, x64, StringComparison.Ordinal);

        Assert.Contains(nativeExclusions, arm64, StringComparison.Ordinal);
        Assert.Contains("#if DeskBoxNativeAot", arm64, StringComparison.Ordinal);
        Assert.Contains(nativeSource, arm64, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedStorageShortcutPrompt_IsLocalizedForEveryInstallerLanguage()
    {
        string messages = ReadRepositoryFile("installer/DeskBox.UninstallCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in ManagedStorageShortcutMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Uninstall_StoreEditionProbe_IsThreeStateWithPlatformPrecheck()
    {
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");

        Assert.Contains("GetStoreDeskBoxInstallState", code, StringComparison.Ordinal);
        Assert.Contains("StoreDeskBoxStateNotInstalled", code, StringComparison.Ordinal);
        Assert.Contains("StoreDeskBoxStateInstalled", code, StringComparison.Ordinal);
        Assert.Contains("StoreDeskBoxStateUnknown", code, StringComparison.Ordinal);
        Assert.Contains("Result := StoreDeskBoxStateUnknown;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStoreDeskBoxInstalled", code, StringComparison.Ordinal);

        // The platform pre-check must decide whether AppX can exist at all
        // with native Inno Setup probes only, without spawning another process.
        Assert.Contains(
            "DirExists(ExpandConstant('{win}\\System32\\WindowsApps'))",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\\CurrentControlSet\\Services\\AppXSvc')",
            code,
            StringComparison.Ordinal);

        // The PowerShell probe must stay scoped to the current user's
        // package registration and keep its documented exit-code contract.
        Assert.Contains("Get-AppxPackage -Name", code, StringComparison.Ordinal);
        Assert.Contains("exit 1", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_StoreEditionUnknownState_FailsClosed()
    {
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");

        // Both the /PURGEUSERDATA path and the interactive keep-or-remove path
        // must branch on the tri-state probe and treat Unknown exactly like
        // installed: the purge stays blocked and the dedicated message is shown.
        Assert.Equal(2, CountOccurrences(code, "case GetStoreDeskBoxInstallState of"));
        Assert.Equal(2, CountOccurrences(code, "StoreDeskBoxStateUnknown:"));
        Assert.Equal(2, CountOccurrences(code, "{cm:StoreEditionStateUnknown}"));
        Assert.Contains("{cm:StoreEditionDataPreserved}", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreEditionMessages_AreLocalizedForEveryInstallerLanguage()
    {
        string messages = ReadRepositoryFile("installer/DeskBox.UninstallCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in StoreEditionMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }

        // The corrected copy must not claim the two editions share one
        // application-data directory: the Store edition keeps its data in its
        // own MSIX LocalCache and this uninstaller never touches it.
        Assert.DoesNotContain(
            "Both editions share the same application data",
            messages,
            StringComparison.Ordinal);
        Assert.DoesNotContain("两个版本共用同一应用数据目录", messages, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallCustomMessages_KeySetsAreAlignedAcrossLanguages()
    {
        string messages = ReadRepositoryFile("installer/DeskBox.UninstallCustomMessages.iss");
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");
        var tables = InstallerLanguages.ToDictionary(
            language => language,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (string line in messages.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int dotIndex = line.IndexOf('.');
            int equalsIndex = line.IndexOf('=');
            if (dotIndex <= 0 || equalsIndex <= dotIndex)
            {
                continue;
            }

            string language = line[..dotIndex].Trim();
            if (tables.TryGetValue(language, out HashSet<string>? keys))
            {
                keys.Add(line[(dotIndex + 1)..equalsIndex].Trim());
            }
        }

        HashSet<string> english = tables["english"];
        Assert.Contains("StoreEditionDataPreserved", english);
        Assert.Contains("StoreEditionStateUnknown", english);
        foreach ((string language, HashSet<string> keys) in tables)
        {
            Assert.Equal(
                english.OrderBy(key => key, StringComparer.Ordinal),
                keys.OrderBy(key => key, StringComparer.Ordinal));
        }

        foreach (string message in StoreEditionMessages)
        {
            Assert.Contains("{cm:" + message + "}", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CustomMessageKeysAndPlaceholders_AreAlignedAcrossLanguages()
    {
        string messages = string.Join(
            Environment.NewLine,
            ReadRepositoryFile("installer/DeskBox.iss"),
            ReadRepositoryFile("installer/DeskBox.NewLanguageCustomMessages.iss"),
            ReadRepositoryFile("installer/DeskBox.UninstallCustomMessages.iss"),
            ReadRepositoryFile("installer/DeskBox.DependencyCustomMessages.iss"));
        var tables = InstallerLanguages.ToDictionary(
            language => language,
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (string line in messages.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = line.IndexOf('=');
            int dotIndex = line.IndexOf('.');
            if (dotIndex <= 0 || equalsIndex <= dotIndex)
            {
                continue;
            }

            string language = line[..dotIndex].Trim();
            if (!tables.TryGetValue(language, out Dictionary<string, string>? table))
            {
                continue;
            }

            string key = line[(dotIndex + 1)..equalsIndex].Trim();
            Assert.True(table.TryAdd(key, line[(equalsIndex + 1)..]), $"Duplicate {language}.{key}");
        }

        Dictionary<string, string> english = tables["english"];
        foreach ((string language, Dictionary<string, string> table) in tables)
        {
            Assert.Equal(
                english.Keys.OrderBy(key => key, StringComparer.Ordinal),
                table.Keys.OrderBy(key => key, StringComparer.Ordinal));

            foreach ((string key, string englishValue) in english)
            {
                Assert.Equal(
                    GetInnoPlaceholders(englishValue),
                    GetInnoPlaceholders(table[key]));
            }
        }
    }

    [Fact]
    public void TraditionalChinese_IsImmediatelyAfterSimplifiedChinese()
    {
        string simplified =
            "Name: \"chinesesimplified\"; MessagesFile: \"Languages\\ChineseSimplified.isl\"";
        string traditional =
            "Name: \"chinesetraditional\"; MessagesFile: \"Languages\\ChineseTraditional.isl\"";

        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            int simplifiedIndex = content.IndexOf(simplified, StringComparison.Ordinal);
            int traditionalIndex = content.IndexOf(traditional, StringComparison.Ordinal);

            Assert.True(simplifiedIndex >= 0);
            Assert.True(traditionalIndex > simplifiedIndex);
            Assert.Contains(
                "#include \"DeskBox.DependencyCustomMessages.iss\"",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "AppComments={cm:RuntimeDependencyComment}",
                content,
                StringComparison.Ordinal);
        }

        string languageFile = ReadRepositoryFile("installer/Languages/ChineseTraditional.isl");
        Assert.Contains("LanguageName=繁體中文", languageFile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hindi", "hi-IN")]
    [InlineData("spanish", "es-ES")]
    [InlineData("french", "fr-FR")]
    [InlineData("arabic", "ar-SA")]
    [InlineData("bengali", "bn-BD")]
    [InlineData("russian", "ru-RU")]
    [InlineData("korean", "ko-KR")]
    [InlineData("chinesetraditional", "zh-TW")]
    public void InstallerLanguage_IsPassedToFirstAppLaunch(
        string installerLanguage,
        string appLanguage)
    {
        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            Assert.Contains(
                $"ActiveLanguage = '{installerLanguage}' then Result := '{appLanguage}'",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "#include \"DeskBox.Uninstall.iss\"",
                content,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DirectInstaller_DiscoversHeuristicInstallationsAndFiltersExclusions()
    {
        string installation = ReadRepositoryFile("installer/DeskBox.Installation.iss");

        Assert.Contains("if Pos('{', TrimmedPath) > 0 then", installation, StringComparison.Ordinal);
        Assert.Contains("UnquoteExecutablePath", installation, StringComparison.Ordinal);
        Assert.Contains("TryGetUserProfileRootForPath", installation, StringComparison.Ordinal);
        Assert.Contains("IsOtherUserProfileInstallPath", installation, StringComparison.Ordinal);
        Assert.Contains("IsExcludedHeuristicInstallPath", installation, StringComparison.Ordinal);
        Assert.Contains("AddHeuristicInstallCandidate", installation, StringComparison.Ordinal);
        Assert.Contains("AddStartupRunInstallCandidate", installation, StringComparison.Ordinal);
        Assert.Contains("AddLoadedUserHivesCandidates", installation, StringComparison.Ordinal);
        Assert.Contains("HasLoadedUserHiveRegistrationForPath(ExistingInstallPath)", installation, StringComparison.Ordinal);
        Assert.Contains("AddStartupScheduledTaskCandidates", installation, StringComparison.Ordinal);
        Assert.Contains("AddAllUserProfileInstallCandidates", installation, StringComparison.Ordinal);
        Assert.Contains("AddRunningProcessInstallCandidates", installation, StringComparison.Ordinal);
        Assert.Contains("AddRunningProcessCandidatesViaPowerShell", installation, StringComparison.Ordinal);
        Assert.Contains("WbemScripting.SWbemLocator", installation, StringComparison.Ordinal);
        Assert.Contains("SELECT ExecutablePath FROM Win32_Process WHERE Name = ''DeskBox.exe''", installation, StringComparison.Ordinal);

        Assert.Contains("'\\WINDOWSAPPS\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\APPDATA\\LOCAL\\TEMP\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\APPDATA\\LOCAL\\DESKBOX\\UPDATES\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\BIN\\DEBUG\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\BIN\\RELEASE\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\.ARTIFACTS\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\ARTIFACTS\\'", installation, StringComparison.Ordinal);
        Assert.Contains("'\\APPPACKAGES\\'", installation, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectInstaller_InstallationScript_CompilesAndEvaluatesDiscoveryRules()
    {
        string? isccPath = TryFindInnoSetupCompiler();
        if (isccPath is null)
        {
            return;
        }

        string repoRoot = FindRepositoryRoot();
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox-InstallerContract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string fakePublish = Path.Combine(tempRoot, "publish");
            Directory.CreateDirectory(Path.Combine(fakePublish, "Assets"));
            File.WriteAllText(Path.Combine(fakePublish, "DeskBox.exe"), "MZ");
            File.WriteAllText(Path.Combine(fakePublish, "DeskBox.Updater.exe"), "MZ");
            File.WriteAllText(Path.Combine(fakePublish, "deskbox_native.dll"), "MZ");
            File.WriteAllText(Path.Combine(fakePublish, "DeskBox.InstallManifest.txt"), "DeskBox.exe");
            File.Copy(
                Path.Combine(repoRoot, "src", "DeskBox", "Assets", "deskbox.ico"),
                Path.Combine(fakePublish, "Assets", "deskbox.ico"),
                overwrite: true);

            string compiledInstallerDir = Path.Combine(tempRoot, "compiled");
            Directory.CreateDirectory(compiledInstallerDir);
            foreach (string scriptName in new[] { "DeskBox.iss", "DeskBox.arm64.iss" })
            {
                RunProcessOrThrow(
                    isccPath,
                    [
                        "/Qp",
                        "/DDeskBoxNativeAot=1",
                        "/DDeskBoxBundledRuntime=1",
                        $"/DMyAppReleaseDir={fakePublish}",
                        $"/O{compiledInstallerDir}",
                        Path.Combine(repoRoot, "installer", scriptName)
                    ]);
            }

            string installationScriptPath = Path.Combine(repoRoot, "installer", "DeskBox.Installation.iss");
            string currentUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            string usersRoot = Path.GetDirectoryName(currentUserProfile) ?? @"C:\Users";
            string probeScriptPath = Path.Combine(tempRoot, "DiscoveryProbe.iss");
            string probeOutputDir = Path.Combine(tempRoot, "probe");
            Directory.CreateDirectory(probeOutputDir);

            File.WriteAllText(
                probeScriptPath,
                $$"""
                #define MyAppExeName "DeskBox.exe"
                #define DeskBoxInstallerPeMachineHex "$8664"

                [Setup]
                AppName=DeskBoxDiscoveryProbe
                AppVersion=1.6.0
                DefaultDirName={autopf}\DeskBoxDiscoveryProbe
                PrivilegesRequired=lowest
                OutputDir={{probeOutputDir}}
                OutputBaseFilename=DeskBoxDiscoveryProbe
                SetupLogging=yes

                [CustomMessages]
                MultipleInstallationsTitle=Multiple
                MultipleInstallationsBody=%1
                MultipleInstallationsFooter=Footer
                UpgradeDirectoryMismatch=%1 vs %2
                PerUserUpgradeScopeMismatch=%1
                ArchitectureMismatchBody=%1

                #include "{{installationScriptPath}}"

                [Code]
                procedure LogBool(Tag: string; Value: Boolean);
                begin
                  if Value then
                    Log('PROBE:' + Tag + '=1')
                  else
                    Log('PROBE:' + Tag + '=0');
                end;

                function InitializeSetup: Boolean;
                begin
                  Log('PROBE:BRACE=' + NormalizeDirPath('C:\Portable\{LiteralBrace}\DeskBox\'));
                  Log('PROBE:UNQUOTE_SPACE=' + UnquoteExecutablePath('C:\Program Files\DeskBox\DeskBox.exe'));
                  Log('PROBE:UNQUOTE_QUOTED=' + UnquoteExecutablePath('"C:\Users\Test User\Desktop\DeskBox\DeskBox.exe"'));
                  LogBool('PERUSER_DESKTOP_POC', IsPerUserProfileInstallPath('{{currentUserProfile}}\Desktop\DeskBox-160-poc-vm-x64\DeskBox-160-poc-vm-x64'));
                  LogBool('PERUSER_OTHER_ACCOUNT', IsPerUserProfileInstallPath('{{usersRoot}}\AnotherDeskBoxUser\Desktop\DeskBox'));
                  LogBool('PERUSER_PUBLIC', IsPerUserProfileInstallPath('{{usersRoot}}\Public\DeskBox'));
                  LogBool('PERUSER_PF', IsPerUserProfileInstallPath(ExpandConstant('{commonpf}\DeskBox')));
                  LogBool('PERUSER_PORTABLE_DRIVE', IsPerUserProfileInstallPath('D:\PortableApps\DeskBox'));
                  LogBool('EXCL_STORE', IsExcludedHeuristicInstallPath('C:\Program Files\WindowsApps\D1FC332A.DeskBoxWidgets_1.6.0.0_x64\DeskBox'));
                  LogBool('EXCL_ALIAS', IsExcludedHeuristicInstallPath(ExpandConstant('{localappdata}\Microsoft\WindowsApps')));
                  LogBool('EXCL_TEMP', IsExcludedHeuristicInstallPath(ExpandConstant('{%TEMP}\DeskBoxExtract')));
                  LogBool('EXCL_UPDATES', IsExcludedHeuristicInstallPath(ExpandConstant('{localappdata}\DeskBox\updates\1.6.0')));
                  LogBool('EXCL_DEBUG', IsExcludedHeuristicInstallPath('D:\project\wingezi\src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0'));
                  LogBool('EXCL_ARTIFACTS', IsExcludedHeuristicInstallPath('D:\project\wingezi\.artifacts\aot-retail\win-x64\publish'));
                  LogBool('EXCL_OTHER_USER_IN_USER_MODE', IsExcludedHeuristicInstallPath('{{usersRoot}}\AnotherDeskBoxUser\AppData\Local\Programs\DeskBox'));
                  LogBool('EXCL_OWN_DESKTOP_POC', IsExcludedHeuristicInstallPath('{{currentUserProfile}}\Desktop\DeskBox-160-poc-vm-x64\DeskBox-160-poc-vm-x64'));
                  LogBool('EXCL_PORTABLE_DRIVE', IsExcludedHeuristicInstallPath('D:\PortableApps\DeskBox'));
                  LogBool('SID_USER', IsLoadedUserAccountSid('S-1-5-21-2861986738-4030568935-1047302796-1000'));
                  LogBool('SID_CLASSES', IsLoadedUserAccountSid('S-1-5-21-2861986738-4030568935-1047302796-1000_Classes'));
                  LogBool('SID_SYSTEM', IsLoadedUserAccountSid('S-1-5-18'));
                  LogBool('TASK_EXACT', IsInstallStartupTaskName('DeskBox User Startup'));
                  LogBool('TASK_SID', IsInstallStartupTaskName('DeskBox User Startup-S-1-5-21-2861986738-4030568935-1047302796-1000'));
                  LogBool('TASK_UNRELATED', IsInstallStartupTaskName('Other Startup Task'));
                  AddRunningProcessInstallCandidates;
                  AddStartupScheduledTaskCandidates;
                  Log('PROBE:DONE');
                  Result := False;
                end;
                """);

            RunProcessOrThrow(isccPath, ["/Qp", probeScriptPath]);

            string probeExePath = Path.Combine(probeOutputDir, "DeskBoxDiscoveryProbe.exe");
            string probeLogPath = Path.Combine(tempRoot, "probe.log");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = probeExePath,
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES \"/LOG={probeLogPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(15000), "Discovery probe installer timed out.");

            string log = File.ReadAllText(probeLogPath);
            Assert.Contains(@"PROBE:BRACE=C:\Portable\{LiteralBrace}\DeskBox", log, StringComparison.Ordinal);
            Assert.Contains(@"PROBE:UNQUOTE_SPACE=C:\Program Files\DeskBox\DeskBox.exe", log, StringComparison.Ordinal);
            Assert.Contains(@"PROBE:UNQUOTE_QUOTED=C:\Users\Test User\Desktop\DeskBox\DeskBox.exe", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:PERUSER_DESKTOP_POC=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:PERUSER_OTHER_ACCOUNT=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:PERUSER_PUBLIC=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:PERUSER_PF=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:PERUSER_PORTABLE_DRIVE=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_STORE=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_ALIAS=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_TEMP=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_UPDATES=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_DEBUG=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_ARTIFACTS=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_OTHER_USER_IN_USER_MODE=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_OWN_DESKTOP_POC=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:EXCL_PORTABLE_DRIVE=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:SID_USER=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:SID_CLASSES=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:SID_SYSTEM=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:TASK_EXACT=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:TASK_SID=1", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:TASK_UNRELATED=0", log, StringComparison.Ordinal);
            Assert.Contains("PROBE:DONE", log, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temp compiler outputs.
            }
        }
    }

    private static string? TryFindInnoSetupCompiler()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 7", "ISCC.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void RunProcessOrThrow(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), $"Process timed out: {fileName}");
        Assert.True(
            process.ExitCode == 0,
            $"Command failed with exit code {process.ExitCode}: {fileName}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string[] GetInnoPlaceholders(string value)
    {
        return System.Text.RegularExpressions.Regex.Matches(value, @"%(?:n|\d+)")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
