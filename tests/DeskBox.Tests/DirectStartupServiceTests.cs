using DeskBox.Services;

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
    public void Migration_MovesOwnedTaskToTheRunEntry()
    {
        var taskBackend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath)
        };
        var runStore = new FakeRunEntryStore();
        var service = CreateService(taskBackend, runStore);

        service.TryMigrateLegacyRegistration();

        Assert.Equal(0, taskBackend.RegisterCount);
        Assert.Equal(1, taskBackend.DeleteCount);
        Assert.Equal($"\"{ExecutablePath}\" --startup", runStore.Value);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Migration_KeepsOwnedTaskWhenTheRunEntryIsBlocked()
    {
        // A live foreign Run entry (target exists on disk) must not be
        // overwritten, so the owned scheduled task stays as the registration.
        string foreignCommand = $"\"{Environment.SystemDirectory}\\cmd.exe\" /c exit";
        var taskBackend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath)
        };
        var runStore = new FakeRunEntryStore { Value = foreignCommand };
        var service = CreateService(taskBackend, runStore);

        service.TryMigrateLegacyRegistration();

        Assert.Equal(0, taskBackend.RegisterCount);
        Assert.Equal(0, taskBackend.DeleteCount);
        Assert.Equal(foreignCommand, runStore.Value);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Enable_UsesRunEntryWithoutTouchingTheScheduler()
    {
        var taskBackend = new FakeTaskBackend();
        var runStore = new FakeRunEntryStore();
        var service = CreateService(taskBackend, runStore);

        service.Enable();

        Assert.Equal(0, taskBackend.RegisterCount);
        Assert.Equal($"\"{ExecutablePath}\" --startup", runStore.Value);
        Assert.Equal(1, runStore.WriteCount);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Enable_RemovesTheOwnedSupersededTask()
    {
        var taskBackend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath)
        };
        var runStore = new FakeRunEntryStore();
        var service = CreateService(taskBackend, runStore);

        service.Enable();

        Assert.Equal(1, taskBackend.DeleteCount);
        Assert.Null(taskBackend.Registration);
        Assert.Equal($"\"{ExecutablePath}\" --startup", runStore.Value);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Enable_FallsBackToTheScheduledTaskWhenRunEntryIsBlocked()
    {
        var taskBackend = new FakeTaskBackend { RegisterResult = true };
        var runStore = new FakeRunEntryStore
        {
            Value = $"\"{Environment.SystemDirectory}\\cmd.exe\" /c exit"
        };
        var service = CreateService(taskBackend, runStore);

        service.Enable();

        Assert.Equal(1, taskBackend.RegisterCount);
        Assert.Equal(0, runStore.WriteCount);
        Assert.Equal(
            $"\"{Environment.SystemDirectory}\\cmd.exe\" /c exit",
            runStore.Value);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Enable_TakesOverOrphanedRunEntryPointingAtMissingTarget()
    {
        var runStore = new FakeRunEntryStore
        {
            Value = "\"D:\\RemovedInstallation\\DeskBox.exe\" --startup"
        };
        var taskBackend = new FakeTaskBackend();
        var service = CreateService(taskBackend, runStore);

        service.Enable();

        Assert.Equal($"\"{ExecutablePath}\" --startup", runStore.Value);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void IsEnabled_FalseWhenStartupAppsDisablesTheRunEntry()
    {
        var runStore = new FakeRunEntryStore
        {
            Value = $"\"{ExecutablePath}\" --startup"
        };
        var service = CreateService(
            new FakeTaskBackend(),
            runStore,
            runEntryApproved: false);

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Disable_RemovesOnlyRegistrationsOwnedByCurrentExecutable()
    {
        var taskBackend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(ExecutablePath)
        };
        var runStore = new FakeRunEntryStore
        {
            Value = $"\"{ExecutablePath}\" --startup"
        };
        var service = CreateService(taskBackend, runStore);

        service.Disable();

        Assert.Equal(1, taskBackend.DeleteCount);
        Assert.Equal(1, runStore.DeleteCount);
        Assert.Null(taskBackend.Registration);
        Assert.Null(runStore.Value);
    }

    [Fact]
    public void Disable_PreservesTaskAndRunEntryOwnedByAnotherInstallation()
    {
        const string otherExecutable = @"D:\OtherDeskBox\DeskBox.exe";
        var taskBackend = new FakeTaskBackend
        {
            Registration = CreatePreferredRegistration(otherExecutable)
        };
        var runStore = new FakeRunEntryStore
        {
            Value = $"\"{otherExecutable}\" --startup"
        };
        var service = CreateService(taskBackend, runStore);

        service.Disable();

        Assert.Equal(0, taskBackend.DeleteCount);
        Assert.Equal(0, runStore.DeleteCount);
        Assert.NotNull(taskBackend.Registration);
        Assert.NotNull(runStore.Value);
    }

    private static DirectStartupService CreateService(
        IDirectStartupTaskBackend taskBackend,
        IDirectStartupRunEntryStore runStore,
        bool runEntryApproved = true) =>
        new(
            taskBackend,
            runStore,
            () => ExecutablePath,
            logger: _ => { },
            runEntryApprovedProvider: () => runEntryApproved);

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

        public bool RegisterResult { get; set; }

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
            Registration = null;
            return true;
        }
    }

    private sealed class FakeRunEntryStore : IDirectStartupRunEntryStore
    {
        public string? Value { get; set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? Read() => Value;

        public void Write(string commandLine)
        {
            WriteCount++;
            Value = commandLine;
        }

        public void Delete()
        {
            DeleteCount++;
            Value = null;
        }
    }
}
