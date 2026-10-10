using System.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Contracts for the recoverable-skip policy's detection side: the probe
/// must classify why a mapped folder is not usable (missing vs. access
/// denied vs. offline device) instead of collapsing every failure into
/// "deleted", and the startup advisor must report such targets without
/// touching the persisted switch, rule, or baseline state.
/// </summary>
public sealed class DesktopOrganizationTargetAvailabilityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public DesktopOrganizationTargetAvailabilityTests()
    {
        DesktopOrganizationTargetAvailabilityAdvisor.ResetForTests();
    }

    [Fact]
    public void Probe_ClassifiesExistingDirectoryAsAvailable()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Available,
            DesktopOrganizationTargetProbe.Classify(_root));
    }

    [Fact]
    public void Probe_ClassifiesMissingDirectoryAsMissing()
    {
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Missing,
            DesktopOrganizationTargetProbe.Classify(Path.Combine(_root, "no-such-box")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Probe_ClassifiesMissingPathAsMissing(string? path)
    {
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Missing,
            DesktopOrganizationTargetProbe.Classify(path));
    }

    [Theory]
    [InlineData(2, DesktopOrganizationTargetAvailability.Missing)]
    [InlineData(3, DesktopOrganizationTargetAvailability.Missing)]
    [InlineData(5, DesktopOrganizationTargetAvailability.AccessDenied)]
    [InlineData(21, DesktopOrganizationTargetAvailability.Unreachable)]
    [InlineData(53, DesktopOrganizationTargetAvailability.Unreachable)]
    public void Probe_ClassifiesWin32FailuresByHresult(
        int nativeErrorCode,
        DesktopOrganizationTargetAvailability expected)
    {
        // Win32Exception reports HResult = 0x80070000 | native error code,
        // which mirrors what the file system layer surfaces for the same
        // failures when a DirectoryInfo metadata touch fails.
        Assert.Equal(
            expected,
            DesktopOrganizationTargetProbe.ClassifyFailure(new Win32Exception(nativeErrorCode)));
    }

    [Fact]
    public void Probe_ClassifiesManagedExceptionsByKind()
    {
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Missing,
            DesktopOrganizationTargetProbe.ClassifyFailure(new FileNotFoundException()));
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Missing,
            DesktopOrganizationTargetProbe.ClassifyFailure(new DirectoryNotFoundException()));
        Assert.Equal(
            DesktopOrganizationTargetAvailability.AccessDenied,
            DesktopOrganizationTargetProbe.ClassifyFailure(new UnauthorizedAccessException()));
        // An unknown IO failure defaults to recoverable-unreachable, never
        // to a deletion verdict.
        Assert.Equal(
            DesktopOrganizationTargetAvailability.Unreachable,
            DesktopOrganizationTargetProbe.ClassifyFailure(new IOException("transient")));
    }

    [Fact]
    public void Advisor_ReportsUnavailableTargetWithoutTouchingPersistedState()
    {
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "advisor-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "advisor-settings"));
        WidgetConfig widget = CreateWidget("Docs", mappedFolder);
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            Extensions = [".txt"]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        DateTimeOffset baseline = DateTimeOffset.UtcNow;
        settings.Settings.DesktopAutoOrganizationBaselineUtc = baseline;
        var reports = new List<DesktopOrganizationTargetUnavailableSummary>();
        string? probedPath = null;

        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            path =>
            {
                probedPath = path;
                return DesktopOrganizationTargetAvailability.Unreachable;
            });

        DesktopOrganizationTargetUnavailableSummary report = Assert.Single(reports);
        Assert.Equal("Docs", Assert.Single(report.WidgetNames));
        Assert.Equal(mappedFolder, Assert.Single(report.TargetPaths));
        Assert.Equal(mappedFolder, probedPath);
        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(settings.Settings.DesktopOrganizationRules[0].IsEnabled);
        Assert.Equal(baseline, settings.Settings.DesktopAutoOrganizationBaselineUtc);

        // One notice per widget per session: a dead drive with steady
        // traffic must not re-warn on every evaluation.
        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            _ => DesktopOrganizationTargetAvailability.Unreachable);
        Assert.Single(reports);
    }

    [Fact]
    public void Advisor_AggregatesMultipleUnavailableWidgetsIntoOneReport()
    {
        var settings = new SettingsService(Path.Combine(_root, "aggregate-settings"));
        foreach (string name in new[] { "Docs", "Media" })
        {
            WidgetConfig widget = CreateWidget(name, Path.Combine(_root, name));
            settings.Settings.Widgets.Add(widget);
            settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
            {
                TargetWidgetId = widget.Id,
                Extensions = [".txt"]
            });
        }

        settings.Settings.DesktopAutoOrganizationEnabled = true;
        var reports = new List<DesktopOrganizationTargetUnavailableSummary>();

        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            _ => DesktopOrganizationTargetAvailability.Unreachable);

        DesktopOrganizationTargetUnavailableSummary report = Assert.Single(reports);
        Assert.Equal(2, report.WidgetNames.Count);
        Assert.Contains("Docs", report.WidgetNames);
        Assert.Contains("Media", report.WidgetNames);
    }

    [Fact]
    public void Advisor_ReportsTargetWhoseMappedPathIsNotBackfilled()
    {
        var settings = new SettingsService(Path.Combine(_root, "backfill-settings"));
        WidgetConfig widget = CreateWidget("Docs", "");
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            Extensions = [".txt"]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        var reports = new List<DesktopOrganizationTargetUnavailableSummary>();

        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            _ => DesktopOrganizationTargetAvailability.Available);

        Assert.Single(reports);
    }

    [Fact]
    public void Advisor_SkipsDisabledRulesDisabledWidgetsAndSelectorlessRules()
    {
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "skip-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "skip-settings"));
        WidgetConfig live = CreateWidget("Live", mappedFolder);
        WidgetConfig disabledWidget = CreateWidget("DisabledWidget", mappedFolder);
        disabledWidget.IsDisabled = true;
        settings.Settings.Widgets.Add(live);
        settings.Settings.Widgets.Add(disabledWidget);
        settings.Settings.DeletedWidgetIds.Add("tombstoned");
        WidgetConfig tombstoned = CreateWidget("Tombstoned", mappedFolder);
        tombstoned.Id = "tombstoned";
        settings.Settings.Widgets.Add(tombstoned);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = live.Id,
            Extensions = [".txt"],
            IsEnabled = false
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = disabledWidget.Id,
            Extensions = [".txt"]
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = "tombstoned",
            Extensions = [".txt"]
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = live.Id
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        var reports = new List<DesktopOrganizationTargetUnavailableSummary>();

        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            _ => DesktopOrganizationTargetAvailability.Unreachable);

        Assert.Empty(reports);
    }

    [Fact]
    public void Advisor_StaysSilentWhenAutoOrganizationIsOff()
    {
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "off-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "off-settings"));
        WidgetConfig widget = CreateWidget("Docs", mappedFolder);
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            Extensions = [".txt"]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = false;
        var reports = new List<DesktopOrganizationTargetUnavailableSummary>();

        DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
            settings,
            reports.Add,
            _ => throw new InvalidOperationException(
                "the probe must not run while the feature is off"));

        Assert.Empty(reports);
    }

    public void Dispose()
    {
        DesktopOrganizationTargetAvailabilityAdvisor.ResetForTests();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static WidgetConfig CreateWidget(string name, string path) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        WidgetKind = WidgetKind.File,
        MappedFolderPath = path
    };
}
