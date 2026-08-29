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
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog", installer, StringComparison.Ordinal);
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
    public void BundledRuntimeInstallers_SkipExternalDependencySetupAndUseDistinctNames()
    {
        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            Assert.Contains("#ifndef DeskBoxBundledRuntime", content, StringComparison.Ordinal);
            Assert.Contains("{#MyAppPackageSuffix}", content, StringComparison.Ordinal);
        }

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
        const string nativeSource =
            "Source: \"{#MyAppReleaseDir}\\deskbox_native.dll\"; DestDir: \"{app}\"; Flags: ignoreversion";
        const string nativeExclusions =
            "Excludes: \"DeskBox.Updater.*,deskbox_native.dll,deskbox_native.pdb\"";

        Assert.Contains("-p:DeskBoxRustNative=true", x64, StringComparison.Ordinal);
        Assert.Contains(nativeExclusions, x64, StringComparison.Ordinal);
        Assert.Contains(nativeSource, x64, StringComparison.Ordinal);

        Assert.Contains(nativeExclusions, arm64, StringComparison.Ordinal);
        Assert.Contains("#if DeskBoxNativeAot", arm64, StringComparison.Ordinal);
        Assert.Contains(nativeSource, arm64, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:DeskBoxRustNative=true", arm64, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomMessageKeysAndPlaceholders_AreAlignedAcrossLanguages()
    {
        string messages = string.Join(
            Environment.NewLine,
            ReadRepositoryFile("installer/DeskBox.iss"),
            ReadRepositoryFile("installer/DeskBox.NewLanguageCustomMessages.iss"),
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
