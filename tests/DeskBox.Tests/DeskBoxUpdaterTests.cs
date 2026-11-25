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

    [Theory]
    [InlineData(1, true, false, true, "failed")]
    [InlineData(1, true, false, false, "cancelled")]
    [InlineData(1, true, true, true, "failed")]
    [InlineData(2, true, false, true, "cancelled")]
    public void ElevationSignaturePolicyBlock_MapsToFailedInsteadOfCancelled(
        int exitCode,
        bool elevationRequired,
        bool installerLogWritten,
        bool validateAdminCodeSignatures,
        string expected)
    {
        Assert.Equal(
            expected,
            DeskBox.Updater.Program.GetIncompleteUpdateOutcome(
                exitCode,
                elevationRequired,
                installerLogWritten,
                validateAdminCodeSignatures));
    }

    [Theory]
    [InlineData(4551, true)] // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION (Smart App Control / WDAC)
    [InlineData(4556, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_MALICIOUS
    [InlineData(4557, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_PUA
    [InlineData(4558, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_DANGEROUS_EXT
    [InlineData(4559, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE
    [InlineData(4580, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE
    [InlineData(4581, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_UNATTAINABLE
    [InlineData(4582, true)] // ERROR_SYSTEM_INTEGRITY_REPUTATION_EXPLICIT_DENY_FILE
    [InlineData(1260, true)] // ERROR_ACCESS_DISABLED_BY_POLICY
    [InlineData(786, true)]  // ERROR_ACCESS_DISABLED_NO_SAFER_UI_BY_POLICY
    [InlineData(577, true)]  // ERROR_INVALID_IMAGE_HASH
    [InlineData(8235, true)] // ERROR_DS_REFERRAL (ValidateAdminCodeSignatures)
    [InlineData(unchecked((int)0x800711C7), true)] // HRESULT_FROM_WIN32(4551)
    [InlineData(unchecked((int)0x800704EC), true)] // HRESULT_FROM_WIN32(1260)
    [InlineData(4555, false)] // Adjacent non-reputation CI error
    [InlineData(4560, false)] // Adjacent non-reputation CI error
    [InlineData(4579, false)] // Adjacent boundary below 4580
    [InlineData(4583, false)] // ERROR_SYSTEM_INTEGRITY_WHQL_NOT_SATISFIED (driver-only boundary)
    [InlineData(0, false)]
    [InlineData(2, false)]    // ERROR_FILE_NOT_FOUND
    [InlineData(5, false)]    // ERROR_ACCESS_DENIED
    [InlineData(1223, false)] // ERROR_CANCELLED (UAC declined)
    [InlineData(1460, false)] // ERROR_TIMEOUT
    public void PolicyOrSignatureBlockWin32Error_ClassifiesSmartAppControlAndPolicyCodes(
        int nativeErrorCode,
        bool expected)
    {
        Assert.Equal(expected, DeskBox.Updater.Program.IsPolicyOrSignatureBlockWin32Error(nativeErrorCode));
        Assert.Equal(expected, DeskBox.Services.AppUpdateService.IsPolicyOrSignatureBlockWin32Error(nativeErrorCode));
    }

    [Theory]
    [InlineData(DeskBox.Updater.Program.PolicyBlockedExitCode, "blocked-by-policy")]
    [InlineData(4551, "blocked-by-policy")]
    [InlineData(4580, "blocked-by-policy")]
    [InlineData(1260, "blocked-by-policy")]
    [InlineData(1460, "failed")]
    public void IncompleteInstallerExitCode_MapsPolicyBlockToBlockedByPolicyOutcome(
        int exitCode,
        string expected)
    {
        Assert.Equal(expected, DeskBox.Updater.Program.GetIncompleteUpdateOutcome(exitCode));
    }

    [Theory]
    [InlineData("blocked-by-policy", "blocked-by-policy")]
    [InlineData(" BLOCKED-BY-POLICY ", "blocked-by-policy")]
    [InlineData("\"blocked-by-policy\"", "blocked-by-policy")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("path-mismatch", "path-mismatch")]
    [InlineData("failed", "failed")]
    [InlineData("unknown-outcome", null)]
    public void NormalizeUpdateInstallOutcome_AcceptsBlockedByPolicyAcrossAppAndSettingsHost(
        string raw,
        string? expected)
    {
        Assert.Equal(expected, DeskBox.App.NormalizeUpdateInstallOutcome(raw));
        Assert.Equal(
            expected,
            DeskBox.App.SettingsHostLaunch.TryGetInstallResultArgument(
                ["DeskBox.exe", "--settings", "1234", "--settings-install-result", raw]));
    }

    [Fact]
    public void ResolveUpdateInstallResultDialog_RoutesBlockedByPolicyToDedicatedCopyAndStoreAction()
    {
        var policySpec = DeskBox.Views.SettingsWindow.ResolveUpdateInstallResultDialog("blocked-by-policy");
        Assert.Equal("Settings.Update.InstallBlockedByPolicyTitle", policySpec.TitleKey);
        Assert.Equal("Settings.Update.InstallBlockedByPolicyBody", policySpec.BodyKey);
        Assert.True(policySpec.OfferMicrosoftStoreInsteadOfManualDownload);

        var cancelledSpec = DeskBox.Views.SettingsWindow.ResolveUpdateInstallResultDialog("cancelled");
        Assert.Equal("Settings.Update.InstallCancelledTitle", cancelledSpec.TitleKey);
        Assert.Equal("Settings.Update.InstallCancelledBody", cancelledSpec.BodyKey);
        Assert.False(cancelledSpec.OfferMicrosoftStoreInsteadOfManualDownload);

        var mismatchSpec = DeskBox.Views.SettingsWindow.ResolveUpdateInstallResultDialog("path-mismatch");
        Assert.Equal("Settings.Update.InstallFailedTitle", mismatchSpec.TitleKey);
        Assert.Equal("Settings.Update.InstallPathMismatchBody", mismatchSpec.BodyKey);
        Assert.False(mismatchSpec.OfferMicrosoftStoreInsteadOfManualDownload);

        var failedSpec = DeskBox.Views.SettingsWindow.ResolveUpdateInstallResultDialog("failed");
        Assert.Equal("Settings.Update.InstallFailedTitle", failedSpec.TitleKey);
        Assert.Equal("Settings.Update.InstallFailedBody", failedSpec.BodyKey);
        Assert.False(failedSpec.OfferMicrosoftStoreInsteadOfManualDownload);
    }

    [Fact]
    public void AppUpdateInstallResult_PolicyBlocked_RoundTripsThroughSettingsIpcJson()
    {
        var original = DeskBox.Models.AppUpdateInstallResult.PolicyBlocked("Blocked by Smart App Control");
        string? encoded = DeskBox.Services.SettingsIpcPayload.Encode(
            original,
            DeskBox.Services.SettingsIpcJsonContext.Default.AppUpdateInstallResult);
        DeskBox.Models.AppUpdateInstallResult? decoded = DeskBox.Services.SettingsIpcPayload.Decode(
            encoded,
            DeskBox.Services.SettingsIpcJsonContext.Default.AppUpdateInstallResult);

        Assert.NotNull(decoded);
        Assert.False(decoded.Success);
        Assert.True(decoded.BlockedByPolicy);
        Assert.Equal("Blocked by Smart App Control", decoded.ErrorMessage);
    }
}
