using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AutoStartDefaultPolicyTests
{
    [Fact]
    public void ShouldApply_IsFalseOnceTheDefaultWasApplied()
    {
        Assert.True(AutoStartDefaultPolicy.ShouldApply(new AppSettings()));
        Assert.False(AutoStartDefaultPolicy.ShouldApply(
            new AppSettings { AutoStartDefaultApplied = true }));
    }

    [Fact]
    public void Resolve_EnablesOnlyANeverRegisteredStartup()
    {
        var startup = new RecordingStartupService(StartupRegistrationState.NotRegistered)
        {
            EnableResult = StartupRegistrationState.Enabled
        };

        StartupRegistrationState state = AutoStartDefaultPolicy.Resolve(startup);

        Assert.Equal(StartupRegistrationState.Enabled, state);
        Assert.Equal(1, startup.EnableCalls);
    }

    [Theory]
    [InlineData(StartupRegistrationState.Enabled)]
    [InlineData(StartupRegistrationState.Pending)]
    [InlineData(StartupRegistrationState.DisabledByUser)]
    [InlineData(StartupRegistrationState.DisabledByTaskScheduler)]
    [InlineData(StartupRegistrationState.BlockedOrFailed)]
    [InlineData(StartupRegistrationState.PathMismatch)]
    public void Resolve_NeverOverridesAnExistingRegistration(StartupRegistrationState current)
    {
        var startup = new RecordingStartupService(current);

        StartupRegistrationState state = AutoStartDefaultPolicy.Resolve(startup);

        Assert.Equal(current, state);
        Assert.Equal(0, startup.EnableCalls);
    }

    [Fact]
    public void Resolve_ReportsAWindowsConsentPendingEnable()
    {
        var startup = new RecordingStartupService(StartupRegistrationState.NotRegistered)
        {
            EnableResult = StartupRegistrationState.Pending
        };

        StartupRegistrationState state = AutoStartDefaultPolicy.Resolve(startup);

        Assert.Equal(StartupRegistrationState.Pending, state);
        Assert.True(AutoStartDefaultPolicy.IsEnabledState(state));
    }

    [Fact]
    public void Resolve_ReportsAFailedEnableWithoutInventingSuccess()
    {
        var startup = new RecordingStartupService(StartupRegistrationState.NotRegistered)
        {
            EnableResult = StartupRegistrationState.BlockedOrFailed
        };

        StartupRegistrationState state = AutoStartDefaultPolicy.Resolve(startup);

        Assert.Equal(StartupRegistrationState.BlockedOrFailed, state);
        Assert.False(AutoStartDefaultPolicy.IsEnabledState(state));
    }

    [Theory]
    [InlineData(StartupRegistrationState.Enabled, true)]
    [InlineData(StartupRegistrationState.Pending, true)]
    [InlineData(StartupRegistrationState.NotRegistered, false)]
    [InlineData(StartupRegistrationState.DisabledByUser, false)]
    [InlineData(StartupRegistrationState.DisabledByTaskScheduler, false)]
    [InlineData(StartupRegistrationState.BlockedOrFailed, false)]
    [InlineData(StartupRegistrationState.PathMismatch, false)]
    public void IsEnabledState_MirrorsOnlyLiveRegistrations(
        StartupRegistrationState state,
        bool expected)
    {
        Assert.Equal(expected, AutoStartDefaultPolicy.IsEnabledState(state));
    }

    [Fact]
    public async Task SettingsLoad_LegacyProfileWithAutoStartFalseAndNoAppliedFlag_MarksDefaultApplied()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxLegacyAutoStart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Legacy settings.json from before autoStartDefaultApplied existed,
            // with user explicitly opting out of autoStart.
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "settings.json"),
                """{"schemaVersion": 1, "autoStart": false}""");

            var settings = new SettingsService(tempDir);
            await settings.LoadAsync();

            Assert.False(settings.Settings.Core.AutoStart);
            Assert.True(settings.Settings.Core.AutoStartDefaultApplied);
            Assert.False(AutoStartDefaultPolicy.ShouldApply(settings.Settings));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SettingsLoad_ModernProfileWithExplicitAppliedFalse_RemainsRetriable()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxRetryAutoStart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Modern settings.json whose first-run default attempt hit BlockedOrFailed
            // writes explicit autoStartDefaultApplied: false so next launch can retry.
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "settings.json"),
                """{"schemaVersion": 1, "autoStart": false, "autoStartDefaultApplied": false}""");

            var settings = new SettingsService(tempDir);
            await settings.LoadAsync();

            Assert.False(settings.Settings.Core.AutoStart);
            Assert.False(settings.Settings.Core.AutoStartDefaultApplied);
            Assert.True(AutoStartDefaultPolicy.ShouldApply(settings.Settings));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SettingsApplyExternalSnapshot_PreservesMainOwnedAutoStartFields()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxMainOwnedAutoStart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsService(tempDir);
            await settings.LoadAsync();

            settings.Settings.Core.AutoStart = false;
            settings.Settings.Core.AutoStartMode = StartupMode.ScheduledTask;
            settings.Settings.Core.AutoStartDefaultApplied = true;
            await settings.SaveAsync();

            // Simulate a stale SettingsHost snapshot carrying outdated AutoStart fields.
            string staleHostJson = """
            {
              "schemaVersion": 1,
              "autoStart": true,
              "autoStartDefaultApplied": false,
              "autoStartMode": "Standard",
              "silentStartup": true
            }
            """;

            bool applied = await settings.ApplyExternalSnapshotAsync(staleHostJson);

            Assert.True(applied);
            Assert.True(settings.Settings.Core.SilentStartup);
            Assert.False(settings.Settings.Core.AutoStart);
            Assert.Equal(StartupMode.ScheduledTask, settings.Settings.Core.AutoStartMode);
            Assert.True(settings.Settings.Core.AutoStartDefaultApplied);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(StartupRegistrationState.NotRegistered, false, false, true, "Settings.AutoStart.ExternallyRemoved")]
    [InlineData(StartupRegistrationState.NotRegistered, false, false, false, null)]
    [InlineData(StartupRegistrationState.Enabled, true, false, true, "Settings.AutoStart.Fallback")]
    [InlineData(StartupRegistrationState.Enabled, false, false, true, null)]
    [InlineData(StartupRegistrationState.DisabledByUser, false, false, true, "Settings.AutoStart.WindowsDisabled")]
    [InlineData(StartupRegistrationState.DisabledByTaskScheduler, false, false, true, "Settings.AutoStart.TaskDisabled")]
    [InlineData(StartupRegistrationState.BlockedOrFailed, false, true, true, "Settings.AutoStart.ChangeFailed")]
    public void SettingsAutoStartStatusKey_DistinguishesExternalRemovalFromManualTurnOff(
        StartupRegistrationState state,
        bool usedFallback,
        bool operationFailed,
        bool? preferenceAtLoad,
        string? expectedKey)
    {
        Assert.Equal(
            expectedKey,
            DeskBox.ViewModels.SettingsViewModel.ResolveAutoStartStatusKey(
                state, usedFallback, operationFailed, preferenceAtLoad));
    }

    private sealed class RecordingStartupService(StartupRegistrationState state)
        : IStartupService
    {
        public int EnableCalls { get; private set; }

        public StartupRegistrationState EnableResult { get; init; } =
            StartupRegistrationState.Enabled;

        public StartupRegistrationState GetState() => state;

        public bool IsEnabled() => state == StartupRegistrationState.Enabled;

        public string? GetRunValue() => null;

        public StartupOperationResult Enable()
        {
            EnableCalls++;
            return new StartupOperationResult(EnableResult);
        }

        public StartupOperationResult Disable() =>
            new(StartupRegistrationState.NotRegistered);

        public StartupOperationResult SetEnabled(bool enabled) =>
            enabled ? Enable() : Disable();
    }
}
