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
        "russian"
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
        int adminShortcutOffer = code.IndexOf("OfferManagedStorageShortcut", adminShortcutGuard, StringComparison.Ordinal);
        int adminBranchResult = code.IndexOf("Result := True;", adminShortcutGuard, StringComparison.Ordinal);
        Assert.True(
            adminShortcutGuard >= 0 &&
            adminShortcutOffer > adminShortcutGuard &&
            adminBranchResult > adminShortcutOffer,
            "The default all-users uninstall path should offer the current user's managed-storage shortcut when that profile has DeskBox settings.");
        Assert.Contains("CreateOleObject('WScript.Shell')", code, StringComparison.Ordinal);
        Assert.Contains("ShortcutObject.TargetPath := FolderPath", code, StringComparison.Ordinal);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON1", code, StringComparison.Ordinal);
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
        Assert.Contains("{autoprograms}", installer, StringComparison.Ordinal);
        Assert.Contains("{autodesktop}", installer, StringComparison.Ordinal);
        Assert.Contains("Root: HKA; Subkey: \"Software\\DeskBox\\DirectInstall\"", installer, StringComparison.Ordinal);
        Assert.Contains("ValueName: \"InstallScope\"", installer, StringComparison.Ordinal);
        Assert.Contains("if (CurStep = ssPostInstall) and (not IsAdminInstallMode) then", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Root: HKCU; Subkey: \"Software\\DeskBox\"; ValueType: string; ValueName: \"InstallLanguage\"", installer, StringComparison.Ordinal);
        Assert.Contains("ExpandConstant('{autopf}\\DeskBox')", installation, StringComparison.Ordinal);
        Assert.Contains("HKEY_LOCAL_MACHINE, DeskBoxInstallStateKey", installation, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree(LegacyInstallPath", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DeskBoxAdminCleanupParam", migration, StringComparison.Ordinal);
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
