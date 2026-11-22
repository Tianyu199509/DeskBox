namespace DeskBox.Tests;

public sealed class DeskBoxUpdaterTests
{
    [Fact]
    public void InstallerArguments_ShowProgressAndLockTheExistingDirectory()
    {
        const string InstallDirectory = @"D:\Apps\DeskBox";

        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            InstallDirectory,
            silent: true,
            DeskBox.Updater.DirectInstallScope.CurrentUser);

        Assert.Contains($"/DIR={InstallDirectory}", arguments);
        Assert.Contains("/CURRENTUSER", arguments);
        Assert.DoesNotContain("/ALLUSERS", arguments);
        Assert.Contains("/SILENT", arguments);
        Assert.Contains("/SP-", arguments);
        Assert.Contains("/NORESTART", arguments);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", arguments);
        Assert.DoesNotContain("/VERYSILENT", arguments);
        Assert.DoesNotContain("/SUPPRESSMSGBOXES", arguments);
        Assert.DoesNotContain(arguments, argument =>
            argument.StartsWith("/LANG=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MachineWideInstallerArguments_PreserveAllUsersScope()
    {
        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            @"C:\Program Files\DeskBox",
            silent: true,
            DeskBox.Updater.DirectInstallScope.AllUsers);

        Assert.Contains("/ALLUSERS", arguments);
        Assert.DoesNotContain("/CURRENTUSER", arguments);
    }

    [Fact]
    public void SilentInstallerArguments_PinTheAppLanguageWhenProvided()
    {
        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            @"D:\Apps\DeskBox",
            silent: true,
            DeskBox.Updater.DirectInstallScope.CurrentUser,
            installerLanguage: "english");

        Assert.Contains("/LANG=english", arguments);
    }

    [Fact]
    public void SilentInstallerArguments_OmitLanguageWhenNoneResolved()
    {
        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            @"D:\Apps\DeskBox",
            silent: true,
            DeskBox.Updater.DirectInstallScope.CurrentUser,
            installerLanguage: "");

        Assert.DoesNotContain(arguments, argument =>
            argument.StartsWith("/LANG=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InteractiveInstallerArguments_NeverPinLanguage()
    {
        // A matching /LANG suppresses Inno's Select Language dialog even in
        // interactive runs, which would take the language choice away from a
        // user running Setup manually. The pin must stay silent-only.
        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            @"D:\Apps\DeskBox",
            silent: false,
            DeskBox.Updater.DirectInstallScope.CurrentUser,
            installerLanguage: "english");

        Assert.DoesNotContain("/SILENT", arguments);
        Assert.DoesNotContain(arguments, argument =>
            argument.StartsWith("/LANG=", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("english", "english")]
    [InlineData("English", "english")]
    [InlineData("chinesesimplified", "chinesesimplified")]
    [InlineData("chinesetraditional", "chinesetraditional")]
    [InlineData("japanese", "japanese")]
    [InlineData("german", "german")]
    [InlineData("brazilianportuguese", "brazilianportuguese")]
    [InlineData("hindi", "hindi")]
    [InlineData("spanish", "spanish")]
    [InlineData("french", "french")]
    [InlineData("arabic", "arabic")]
    [InlineData("bengali", "bengali")]
    [InlineData("russian", "russian")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("zh-CN", "")]
    [InlineData("not-a-language", "")]
    [InlineData("english /SILENT", "")]
    public void InstallerLanguageResolver_WhitelistsKnownInstallerNamesOnly(
        string? requested,
        string expected)
    {
        Assert.Equal(expected, DeskBox.Updater.Program.ResolveInstallerLanguage(requested));
    }

    [Fact]
    public void ProgramFilesInstallWithoutRegistration_FallsBackToAllUsersScope()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(programFiles));

        string unregisteredPath = Path.Combine(
            programFiles,
            $"DeskBox-Scope-Probe-{Guid.NewGuid():N}");

        Assert.Equal(
            DeskBox.Updater.DirectInstallScope.AllUsers,
            DeskBox.Updater.Program.ResolveInstallScope(unregisteredPath));
    }

    [Fact]
    public void PerUserInstallWithoutRegistration_DefaultsToCurrentUserScope()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(localAppData));

        string unregisteredPath = Path.Combine(
            localAppData,
            $"DeskBox-Scope-Probe-{Guid.NewGuid():N}");

        Assert.Equal(
            DeskBox.Updater.DirectInstallScope.CurrentUser,
            DeskBox.Updater.Program.ResolveInstallScope(unregisteredPath));
    }

    [Theory]
    [InlineData(2, "cancelled")]
    [InlineData(5, "cancelled")]
    [InlineData(20, "path-mismatch")]
    [InlineData(1, "failed")]
    [InlineData(99, "failed")]
    public void IncompleteInstallerExitCode_MapsToRecoveryOutcome(int exitCode, string expected)
    {
        Assert.Equal(expected, DeskBox.Updater.Program.GetIncompleteUpdateOutcome(exitCode));
    }

    [Theory]
    [InlineData(1, true, false, "cancelled")]
    [InlineData(1, true, true, "failed")]
    [InlineData(1, false, false, "failed")]
    [InlineData(99, true, false, "failed")]
    public void ElevationBootstrapCancellation_MapsToCancelledOnlyWhenInnerLogWasNeverOpened(
        int exitCode,
        bool elevationRequired,
        bool installerLogWritten,
        string expected)
    {
        Assert.Equal(
            expected,
            DeskBox.Updater.Program.GetIncompleteUpdateOutcome(
                exitCode,
                elevationRequired,
                installerLogWritten));
    }
}
