using DeskBox.Services;
using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class DirectStartupServiceTests
{
    private const string ExecutablePath =
        @"C:\Program Files\DeskBox\DeskBox.exe";

    [Fact]
    public void TaskXml_UsesImmediateInteractiveLeastPrivilegeContract()
    {
        const string userSid = "S-1-5-21-1000-1001-1002-1003";

        string xml = DirectStartupTaskBackend.BuildTaskXml(
            ExecutablePath,
            userSid);
        DirectStartupTaskRegistration registration =
            DirectStartupTaskBackend.ParseTaskXml(xml);

        Assert.Equal(Path.GetFullPath(ExecutablePath), registration.ExecutablePath);
        Assert.Equal(
            DirectStartupTaskBackend.GetTaskName(userSid),
            registration.TaskName);
        Assert.Equal(
            DirectStartupTaskBackend.StartupArguments,
            registration.Arguments);
        Assert.Equal(userSid, registration.PrincipalUserId);
        Assert.Equal(userSid, registration.TriggerUserId);
        Assert.Equal("InteractiveToken", registration.LogonType);
        Assert.Equal("LeastPrivilege", registration.RunLevel);
        Assert.Equal(
            DirectStartupTaskBackend.InteractiveTaskPriority,
            registration.Priority);
        Assert.True(registration.Enabled);
        Assert.Equal("PT0S", registration.ExecutionTimeLimit);
        Assert.Equal("IgnoreNew", registration.MultipleInstancesPolicy);
        Assert.True(registration.StartWhenAvailable);
        Assert.False(registration.DisallowStartIfOnBatteries);
        Assert.False(registration.StopIfGoingOnBatteries);
        Assert.False(registration.RunOnlyIfIdle);
        Assert.Empty(registration.TriggerDelay);
        Assert.DoesNotContain("<Delay>", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HighestAvailable", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceAccount", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportedTaskDefaults_AreNormalizedBeforePreferenceValidation()
    {
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value ?? string.Empty;
        var document = System.Xml.Linq.XDocument.Parse(
            DirectStartupTaskBackend.BuildTaskXml(ExecutablePath, userSid));
        System.Xml.Linq.XElement root = document.Root!;
        System.Xml.Linq.XNamespace ns = root.Name.Namespace;

        root.Descendants(ns + "LogonTrigger")
            .Single()
            .Element(ns + "UserId")!
            .Value = identity.Name;
        root.Descendants(ns + "Principal")
            .Single()
            .Element(ns + "RunLevel")!
            .Remove();
        root.Element(ns + "Settings")!
            .Element(ns + "Enabled")!
            .Remove();

        DirectStartupTaskRegistration registration =
            DirectStartupTaskBackend.ParseTaskXml(document.ToString());

        Assert.Equal("LeastPrivilege", registration.RunLevel);
        Assert.True(registration.Enabled);
        Assert.True(new DirectStartupTaskBackend().IsPreferred(
            registration,
            ExecutablePath));
    }

[Fact]
    public void Migration_AdoptsExistingStandardModeWithoutReplacingIt()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run, preference: null);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(0, backend.RegisterCount);
        Assert.Null(backend.Registration);
        Assert.NotNull(run.Value);
        Assert.Equal(StartupMode.Standard, service.Mode);
        Assert.Equal(0, run.WriteCount);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Migration_FailedRegistrationPreservesExistingRun()
    {
        var backend = new FakeTaskBackend { RegisterResult = false };
        string command = $"\"{ExecutablePath}\" --startup";
        var run = new FakeRunEntryStore { Value = command };
        var service = CreateService(backend, run);
        service.SetMode(StartupMode.ScheduledTask);
        Assert.True(DirectStartupService.IsCommandOwnedBy(run.Value, ExecutablePath));
        Assert.Equal(StartupMode.Standard, service.Mode);
        Assert.Null(backend.Registration);
        Assert.Equal(0, run.DeleteCount);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Migration_DoesNotEnableStartupForAnUnregisteredUser()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore();
        CreateService(backend, run).TryMigrateLegacyRegistration();
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(0, run.WriteCount);
    }

    [Fact]
    public void Migration_PreferredTaskIsIdempotent()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var logs = new List<string>();
        var service = CreateService(backend, new FakeRunEntryStore(), logger: logs.Add);
        service.TryMigrateLegacyRegistration();
        service.TryMigrateLegacyRegistration();
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(0, backend.DeleteCount);
        Assert.DoesNotContain(logs, entry => entry.Contains("Migrated startup", StringComparison.Ordinal));
    }

    [Fact]
    public void Enable_UsesOnlyTheScheduledTask()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run);
        Assert.Equal(StartupRegistrationState.Enabled, service.Enable().State);
        Assert.Equal(1, backend.RegisterCount);
        Assert.Null(run.Value);
        Assert.Equal(0, run.WriteCount);
        Assert.Contains("--startup-source=scheduled-task", service.GetRunValue());
    }

    [Fact]
    public void Enable_TaskRegistrationFailureFallsBackToVerifiedStandardStartup()
    {
        var backend = new FakeTaskBackend { RegisterResult = false };
        var run = new FakeRunEntryStore();
        var result = CreateService(backend, run).Enable();
        Assert.Equal(StartupRegistrationState.Enabled, result.State);
        Assert.True(result.IsEnabled);
        Assert.True(result.UsedFallback);
        Assert.Equal(StartupMode.Standard, result.EffectiveMode);
        Assert.Equal(1, run.WriteCount);
        Assert.NotNull(run.Value);
    }

    [Fact]
    public void SwitchToTask_RemovesOwnedRunAfterTaskRegistration()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore
        {
            Value = $"\"{ExecutablePath}\" --startup",
            BeforeDelete = () => Assert.NotNull(backend.Registration)
        };
        Assert.True(CreateService(backend, run).SetMode(StartupMode.ScheduledTask).IsEnabled);
        Assert.Null(run.Value);
        Assert.Equal(0, backend.DeleteCount);
    }

    [Fact]
    public void SwitchToTask_CleanupFailureRollsBackTaskAndPreservesRun()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore
        {
            Value = $"\"{ExecutablePath}\" --startup", FailDelete = true
        };
        var result = CreateService(backend, run).SetMode(StartupMode.ScheduledTask);
        Assert.Equal(StartupRegistrationState.BlockedOrFailed, result.State);
        Assert.NotNull(run.Value);
        Assert.Null(backend.Registration);
        Assert.Equal(1, backend.DeleteCount);
    }

    [Fact]
    public void SwitchToTask_SilentlyIgnoredRunRemovalDoesNotReportSuccess()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore
        {
            Value = $"\"{ExecutablePath}\" --startup", IgnoreDelete = true
        };
        Assert.Equal(StartupRegistrationState.BlockedOrFailed,
            CreateService(backend, run).SetMode(StartupMode.ScheduledTask).State);
        Assert.NotNull(run.Value);
        Assert.Null(backend.Registration);
    }

    [Fact]
    public void Enable_DoesNotOverwriteAnotherLiveInstallationTask()
    {
        var backend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        };
        var run = new FakeRunEntryStore();
        Assert.Equal(StartupRegistrationState.PathMismatch, CreateService(backend, run).Enable().State);
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(0, backend.DeleteCount);
        Assert.Null(run.Value);
    }

    [Fact]
    public void Enable_DoesNotCreateSecondStartupBesideAnotherLiveRun()
    {
        var backend = new FakeTaskBackend();
        string command = $"\"{Environment.SystemDirectory}\\cmd.exe\" /c exit";
        var run = new FakeRunEntryStore { Value = command };
        Assert.Equal(StartupRegistrationState.PathMismatch, CreateService(backend, run).Enable().State);
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(command, run.Value);
    }

    [Fact]
    public void Enable_ReplacesAnOrphanedRunWithoutLeavingASecondEntry()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore
        {
            Value = $"\"{Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "DeskBox.exe")}\" --startup"
        };
        Assert.True(CreateService(backend, run).Enable().IsEnabled);
        Assert.NotNull(backend.Registration);
        Assert.Null(run.Value);
    }

    [Fact]
    public void Migration_PreservesWindowsOptOutAndRemovesBypassingTask()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run, runEntryApproved: false);
        service.TryMigrateLegacyRegistration();
        Assert.Null(backend.Registration);
        Assert.NotNull(run.Value);
        Assert.Equal(0, run.WriteCount);
        Assert.Equal(StartupRegistrationState.DisabledByUser, service.GetState());
        Assert.True(service.Enable().RequiresSystemSettings);
        Assert.Equal(0, backend.RegisterCount);
    }

    [Fact]
    public void Migration_PreservesDisabledTaskAndRemovesBypassingRun()
    {
        var backend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath) with { Enabled = false }
        };
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(StartupRegistrationState.DisabledByTaskScheduler, service.GetState());
        Assert.False(backend.Registration!.Enabled);
        Assert.Null(run.Value);
        Assert.Equal(0, backend.RegisterCount);
    }

    [Fact]
    public void Enable_ExplicitToggleCanReenableDisabledTask()
    {
        var backend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath) with { Enabled = false }
        };
        var service = CreateService(backend, new FakeRunEntryStore());
        Assert.False(service.IsEnabled());
        Assert.True(service.Enable().IsEnabled);
        Assert.True(backend.Registration!.Enabled);
        Assert.Equal(1, backend.RegisterCount);
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("LogonTrigger")]
    public void DisabledTaskOrTrigger_IsReportedAsDisabled(string elementName)
    {
        var document = System.Xml.Linq.XDocument.Parse(
            DirectStartupTaskBackend.BuildTaskXml(ExecutablePath, "S-1-5-21-1000"));
        System.Xml.Linq.XNamespace ns = document.Root!.Name.Namespace;
        document.Descendants(ns + elementName).Single()
            .SetElementValue(ns + "Enabled", "false");
        Assert.False(DirectStartupTaskBackend.ParseTaskXml(document.ToString()).Enabled);
    }

    [Fact]
    public void Disable_CleansOnlyOwnedRegistrations()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run);
        Assert.Equal(StartupRegistrationState.NotRegistered, service.Disable().State);
        Assert.Null(backend.Registration);
        Assert.Null(run.Value);
    }

    [Fact]
    public void Disable_PreservesTaskAndRunEntryOwnedByAnotherInstallation()
    {
        string foreignPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(foreignPath) };
        var run = new FakeRunEntryStore { Value = $"\"{foreignPath}\" /c exit" };
        CreateService(backend, run).Disable();
        Assert.Equal(0, backend.DeleteCount);
        Assert.Equal(0, run.DeleteCount);
        Assert.NotNull(backend.Registration);
        Assert.NotNull(run.Value);
    }

    [Fact]
    public void Factory_KeepsStoreOnItsManagedStartupTask()
    {
        Assert.IsType<StoreStartupService>(StartupServiceFactory.Create(
            new AppDistributionService(AppDistributionChannel.MicrosoftStore)));
        Assert.IsType<DirectStartupService>(StartupServiceFactory.Create(
            new AppDistributionService(AppDistributionChannel.Direct)));
    }

    [Fact]
    public void FreshInstall_DefaultsToStandardStartupAndPersistsTheMode()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run, preference: null);
        Assert.Equal(StartupMode.Standard, service.Mode);
        Assert.True(service.Enable().IsEnabled);
        Assert.Equal(0, backend.RegisterCount);
        Assert.Contains("--startup-source=run", run.Value);
        Assert.Equal(StartupMode.Standard, service.Mode);
    }

    [Fact]
    public void Upgrade_AdoptsAnExistingTaskInsteadOfChangingItsMode()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run, preference: null);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(StartupMode.ScheduledTask, service.Mode);
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(0, run.WriteCount);
    }

    [Fact]
    public void SwitchTaskToStandard_RemovesTaskOnlyAfterRunWasVerified()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run);
        Assert.True(service.SetMode(StartupMode.Standard).IsEnabled);
        Assert.NotNull(run.Value);
        Assert.Null(backend.Registration);
        Assert.Equal(StartupMode.Standard, service.Mode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SwitchTaskToStandard_WriteFailurePreservesTask(bool failWrite, bool ignoreWrite)
    {
        var original = CreatePreferredRegistration(ExecutablePath);
        var backend = new FakeTaskBackend { Registration = original };
        var run = new FakeRunEntryStore { FailWrite = failWrite, IgnoreWrite = ignoreWrite };
        var service = CreateService(backend, run);
        Assert.False(service.SetMode(StartupMode.Standard).IsEnabled);
        Assert.Same(original, backend.Registration);
        Assert.Null(run.Value);
        Assert.Equal(StartupMode.ScheduledTask, service.Mode);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void SwitchTaskToStandard_TaskCleanupFailureRollsBackRun()
    {
        var original = CreatePreferredRegistration(ExecutablePath);
        var backend = new FakeTaskBackend { Registration = original, DeleteResult = false };
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run);
        Assert.False(service.SetMode(StartupMode.Standard).IsEnabled);
        Assert.Same(original, backend.Registration);
        Assert.Null(run.Value);
        Assert.Equal(StartupMode.ScheduledTask, service.Mode);
    }

    [Fact]
    public void FailedTaskSelection_PersistsStandardFallbackInsteadOfRetryingEveryLaunch()
    {
        var backend = new FakeTaskBackend { RegisterResult = false };
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run, preference: StartupMode.Standard);
        var result = service.SetMode(StartupMode.ScheduledTask);
        Assert.True(result.IsEnabled);
        Assert.True(result.UsedFallback);
        Assert.Equal(StartupMode.Standard, service.Mode);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(1, backend.RegisterCount);
        Assert.Null(backend.Registration);
    }

    [Fact]
    public void Enable_RestoresMissingRunEntryWhenWindowsOptOutPersistsAfterDisable()
    {
        bool approved = false;
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup --startup-source=run" };
        StartupMode? preference = StartupMode.Standard;
        var service = new DirectStartupService(
            backend,
            run,
            () => ExecutablePath,
            logger: _ => { },
            runEntryApprovedProvider: () => approved,
            modeProvider: () => preference,
            modeWriter: mode => preference = mode);

        Assert.Equal(StartupRegistrationState.NotRegistered, service.Disable().State);
        Assert.Null(run.Value);

        StartupOperationResult reenable = service.Enable();
        Assert.Equal(StartupRegistrationState.DisabledByUser, reenable.State);
        Assert.True(reenable.RequiresSystemSettings);
        Assert.Equal(1, run.WriteCount);
        Assert.Contains("--startup-source=run", run.Value, StringComparison.Ordinal);
        Assert.Equal(StartupRegistrationState.DisabledByUser, service.GetState());

        approved = true;
        Assert.Equal(StartupRegistrationState.Enabled, service.GetState());
    }

    [Fact]
    public void StandardStartup_DoesNotBypassWindowsOptOut()
    {
        var backend = new FakeTaskBackend { RegisterResult = false };
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run, runEntryApproved: false, preference: StartupMode.Standard);
        Assert.Equal(StartupRegistrationState.DisabledByUser, service.SetMode(StartupMode.ScheduledTask).State);
        Assert.Equal(StartupRegistrationState.DisabledByUser, service.Enable().State);
        Assert.Equal(0, backend.RegisterCount);
        Assert.Equal(0, run.WriteCount);
    }

    [Fact]
    public void SelectingModeWhileTaskIsDisabled_DoesNotEnableAnotherEntry()
    {
        var backend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath) with { Enabled = false }
        };
        var run = new FakeRunEntryStore();
        var service = CreateService(backend, run);
        Assert.Equal(StartupRegistrationState.DisabledByTaskScheduler, service.SetMode(StartupMode.Standard).State);
        service.TryMigrateLegacyRegistration();
        Assert.False(backend.Registration!.Enabled);
        Assert.Equal(0, run.WriteCount);
    }

    [Fact]
    public void UnknownTaskState_PreventsCreatingADuplicateFallback()
    {
        var backend = new FakeTaskBackend { ReadFailed = true, RegisterResult = false };
        var run = new FakeRunEntryStore();
        Assert.False(CreateService(backend, run).Enable().IsEnabled);
        Assert.Null(run.Value);
    }

    [Fact]
    public void ExternalStandardRegistrationIsShownAndPreservedOverAnOldTaskPreference()
    {
        var backend = new FakeTaskBackend();
        var run = new FakeRunEntryStore { Value = $"\"{ExecutablePath}\" --startup" };
        var service = CreateService(backend, run, preference: StartupMode.ScheduledTask);
        Assert.Equal(StartupMode.Standard, service.Mode);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(0, backend.RegisterCount);
        Assert.NotNull(run.Value);
        Assert.Equal(StartupMode.Standard, service.Mode);
    }

    [Fact]
    public void ExistingTaskIsShownOverAnOldStandardPreference()
    {
        var backend = new FakeTaskBackend { Registration = CreatePreferredRegistration(ExecutablePath) };
        var service = CreateService(backend, new FakeRunEntryStore(), preference: StartupMode.Standard);
        Assert.Equal(StartupMode.ScheduledTask, service.Mode);
        service.TryMigrateLegacyRegistration();
        Assert.Equal(0, backend.DeleteCount);
        Assert.Equal(StartupMode.ScheduledTask, service.Mode);
    }

    private static DirectStartupService CreateService(
        IDirectStartupTaskBackend taskBackend,
        IDirectStartupRunEntryStore runStore,
        bool runEntryApproved = true,
        Action<string>? logger = null,
        StartupMode? preference = StartupMode.ScheduledTask) =>
        new(
            taskBackend,
            runStore,
            () => ExecutablePath,
            logger: logger ?? (_ => { }),
            runEntryApprovedProvider: () => runEntryApproved,
            modeProvider: () => preference,
            modeWriter: mode => preference = mode);

    private static DirectStartupTaskRegistration CreatePreferredRegistration(
        string executablePath) =>
        new(
            executablePath,
            DirectStartupTaskBackend.StartupArguments,
            "S-1-5-21-test",
            "S-1-5-21-test",
            "InteractiveToken",
            "LeastPrivilege",
            DirectStartupTaskBackend.InteractiveTaskPriority,
            Enabled: true,
            ExecutionTimeLimit: "PT0S",
            MultipleInstancesPolicy: "IgnoreNew",
            StartWhenAvailable: true,
            DisallowStartIfOnBatteries: false,
            StopIfGoingOnBatteries: false,
            RunOnlyIfIdle: false,
            TriggerDelay: string.Empty);

    private sealed class FakeTaskBackend : IDirectStartupTaskBackend
    {
        public string Error { get; set; } = "registration failed";

        public string LastError => Error;

        public bool ReadFailed { get; set; }

        public bool RegisterResult { get; set; } = true;
        public bool DeleteResult { get; set; } = true;

        public int RegisterCount { get; private set; }

        public int DeleteCount { get; private set; }

        public DirectStartupTaskRegistration? Registration { get; set; }

        public DirectStartupTaskRegistration? Read() => Registration;

        public bool IsPreferred(
            DirectStartupTaskRegistration registration,
            string executablePath) =>
            registration.IsOwnedBy(executablePath) &&
            registration.Enabled &&
            string.Equals(
                registration.RunLevel,
                "LeastPrivilege",
                StringComparison.OrdinalIgnoreCase);

        public bool TryRegister(string executablePath)
        {
            RegisterCount++;
            if (RegisterResult)
            {
                Registration = CreatePreferredRegistration(executablePath);
            }

            return RegisterResult;
        }

        public bool TryDelete()
        {
            DeleteCount++;
            if (!DeleteResult) return false;
            Registration = null;
            return true;
        }

        public bool TryRestore(DirectStartupTaskRegistration registration)
        {
            Registration = registration;
            return true;
        }
    }

    private sealed class FakeRunEntryStore : IDirectStartupRunEntryStore
    {
        public string? Value { get; set; }
        public bool FailDelete { get; set; }
        public bool IgnoreDelete { get; set; }
        public bool FailWrite { get; set; }
        public bool IgnoreWrite { get; set; }
        public Action? BeforeDelete { get; set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? Read() => Value;

        public void Write(string commandLine)
        {
            WriteCount++;
            if (FailWrite) throw new IOException("Run entry is locked");
            if (IgnoreWrite) return;
            Value = commandLine;
        }

        public void Delete()
        {
            BeforeDelete?.Invoke();
            if (FailDelete) throw new IOException("Run entry is locked");
            DeleteCount++;
            if (IgnoreDelete) return;
            Value = null;
        }
    }
}
