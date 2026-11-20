using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Pins the autostart boot-reliability fixes: Unicode task XML (user
/// names and install paths with non-ASCII characters must survive the
/// register→verify round trip), the bounded restart policy on the logon task,
/// and the one-time default not being consumed by a failed attempt.
/// </summary>
public sealed class AutostartBootReliabilityTests
{
    private const string ExecutablePath =
        @"C:\Program Files\DeskBox\DeskBox.exe";

    [Theory]
    [InlineData("小")]
    [InlineData("桌面")]
    [InlineData("café")]
    [InlineData("Δοκιμή")]
    [InlineData("日本語 😀")]
    public void TaskXml_PreservesUnicodePaths(string folderName)
    {
        string path = $@"C:\Users\{folderName}\DeskBox\DeskBox.exe";
        string xml = DirectStartupTaskBackend.BuildTaskXml(path, "S-1-5-21-1000");
        DirectStartupTaskRegistration registration = DirectStartupTaskBackend.ParseTaskXml(xml);

        Assert.Equal(path, registration.ExecutablePath);
        Assert.True(registration.IsOwnedBy(path));
    }

    [Fact]
    public void TaskXml_RegistersABoundedRestartPolicy()
    {
        string xml = DirectStartupTaskBackend.BuildTaskXml(
            ExecutablePath,
            "S-1-5-21-1000-1001-1002-1003");

        Assert.Contains(
            "<Interval>PT1M</Interval>",
            xml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Count>3</Count>",
            xml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IsPreferred_AcceptsOsNormalizedRestartPolicyButRejectsForeignValues()
    {
        // IsPreferred also matches the task name against the current user's
        // SID, so the positive cases must use the real identity.
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value ?? string.Empty;

        string xml = DirectStartupTaskBackend.BuildTaskXml(
            ExecutablePath,
            userSid);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = document.Root!.Name.Namespace;

        // The restart policy is optional for compatibility with older task
        // registrations; mismatching values that are present are still rejected.
        document.Root!
            .Element(ns + "Settings")!
            .Element(ns + "RestartOnFailure")!
            .Remove();
        DirectStartupTaskRegistration normalizedRegistration =
            DirectStartupTaskBackend.ParseTaskXml(document.ToString());
        Assert.True(new DirectStartupTaskBackend().IsPreferred(
            normalizedRegistration,
            ExecutablePath));

        // Present but foreign values stay non-preferred.
        var foreign = System.Xml.Linq.XDocument.Parse(xml);
        foreign.Root!
            .Element(ns + "Settings")!
            .Element(ns + "RestartOnFailure")!
            .Element(ns + "Interval")!
            .Value = "PT5M";
        DirectStartupTaskRegistration foreignRegistration =
            DirectStartupTaskBackend.ParseTaskXml(foreign.ToString());
        Assert.False(new DirectStartupTaskBackend().IsPreferred(
            foreignRegistration,
            ExecutablePath));

        DirectStartupTaskRegistration currentRegistration =
            DirectStartupTaskBackend.ParseTaskXml(xml);
        Assert.True(new DirectStartupTaskBackend().IsPreferred(
            currentRegistration,
            ExecutablePath));
    }

    [Fact]
    public void ShouldMarkApplied_KeepsTheDefaultRetriableOnlyAfterFailure()
    {
        Assert.False(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.BlockedOrFailed));

        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.Enabled));
        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.Pending));
        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.NotRegistered));
        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.DisabledByUser));
        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.DisabledByTaskScheduler));
        Assert.True(AutoStartDefaultPolicy.ShouldMarkApplied(
            StartupRegistrationState.PathMismatch));
    }

    [Fact]
    public void ApplyDefaultOnce_DoesNotConsumeTheDefaultOnFailure()
    {
        string appStartup = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.Startup.cs"));

        Assert.Contains(
            "if (AutoStartDefaultPolicy.ShouldMarkApplied(resolved.State))",
            appStartup,
            StringComparison.Ordinal);
    }
}
