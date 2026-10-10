using System.Collections.Concurrent;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Runtime contracts for auto-organization against an unavailable target
/// folder: matching desktop items stay recoverable (deferred, never moved
/// elsewhere or dropped), the watcher raises one user-visible
/// target-unavailable notice per episode, and organization resumes by
/// itself once the folder exists again.
/// </summary>
public sealed class DesktopAutoOrganizationWatcherTargetTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Watcher_WarnsAndDefersWhenTargetFolderIsUnavailable_ThenResumesAfterRestore()
    {
        using var harness = TargetHarness.Create(_root);
        string desktopPath = harness.DesktopPath;
        string targetStorage = harness.TargetStoragePath;

        // Episode: the mapped folder goes away while the feature is on.
        Directory.Delete(targetStorage);
        string firstFile = Path.Combine(desktopPath, "first.txt");
        File.WriteAllText(firstFile, "content");

        await WaitUntilAsync(
            () => harness.RequestedDelays.Any(delay => delay >= TimeSpan.FromSeconds(10)));
        harness.Clock.Advance(TimeSpan.FromSeconds(40));

        await WaitUntilAsync(() => !harness.Reports.IsEmpty);
        DesktopOrganizationTargetUnavailableSummary report = harness.Reports.Single();
        Assert.Equal("Docs", Assert.Single(report.WidgetNames));
        Assert.Single(harness.Reports);
        Assert.True(File.Exists(firstFile),
            "an item whose target is unavailable must stay on the desktop");

        // Recovery: the folder returns and a fresh item organizes normally.
        // The real-time pause lets the watcher consume the file's events
        // before the clock jumps, so the activity-quiet window is stamped
        // by the pre-advance clock and can actually elapse.
        Directory.CreateDirectory(targetStorage);
        string secondFile = Path.Combine(desktopPath, "second.txt");
        File.WriteAllText(secondFile, "content");
        await Task.Delay(500);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => !File.Exists(secondFile));
        Assert.True(File.Exists(Path.Combine(targetStorage, "second.txt")));

        // The deferred item from the outage is due after the retry interval
        // and organizes itself, still without a second notice.
        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        await WaitUntilAsync(() => !File.Exists(firstFile));
        Assert.True(File.Exists(Path.Combine(targetStorage, "first.txt")));
        // One unavailability episode must produce exactly one notice.
        Assert.Single(harness.Reports);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        DateTime deadline = DateTime.UtcNow + timeout.Value;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met before the timeout.");
    }

    public void Dispose()
    {
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

    private sealed class TargetHarness : IDisposable
    {
        public string DesktopPath { get; }
        public string TargetStoragePath { get; }
        public ManualClock Clock { get; }
        public ConcurrentQueue<TimeSpan> RequestedDelays { get; }
        public ConcurrentQueue<DesktopOrganizationTargetUnavailableSummary> Reports { get; }

        private readonly DesktopAutoOrganizationWatcher _watcher;

        private TargetHarness(
            string desktopPath,
            string targetStoragePath,
            DesktopAutoOrganizationWatcher watcher,
            ManualClock clock,
            ConcurrentQueue<TimeSpan> requestedDelays,
            ConcurrentQueue<DesktopOrganizationTargetUnavailableSummary> reports)
        {
            DesktopPath = desktopPath;
            TargetStoragePath = targetStoragePath;
            _watcher = watcher;
            Clock = clock;
            RequestedDelays = requestedDelays;
            Reports = reports;
        }

        public static TargetHarness Create(string root)
        {
            string desktopPath = Directory.CreateDirectory(
                Path.Combine(root, "desktop")).FullName;
            string targetStorage = Directory.CreateDirectory(
                Path.Combine(root, "docs-widget")).FullName;
            var settingsService = new SettingsService(Path.Combine(root, "settings"));
            settingsService.Settings.DesktopAutoOrganizationEnabled = true;
            settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds = 10;
            var widget = new WidgetConfig
            {
                Id = "docs-widget",
                Name = "Docs",
                WidgetKind = WidgetKind.File,
                MappedFolderPath = targetStorage
            };
            settingsService.Settings.Widgets.Add(widget);
            settingsService.Settings.DesktopOrganizationRules.Add(
                new DesktopOrganizationRule
                {
                    TargetWidgetId = widget.Id,
                    IsEnabled = true,
                    Extensions = [".txt"]
                });

            var clock = new ManualClock(
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var requestedDelays = new ConcurrentQueue<TimeSpan>();
            var reports = new ConcurrentQueue<DesktopOrganizationTargetUnavailableSummary>();
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(
                settingsService,
                fileService,
                () => desktopPath);
            var widgetManager = new WidgetManager(
                settingsService,
                fileService,
                organizer,
                new ThemeService(settingsService),
                new QuickCaptureService(
                    new QuickCaptureStore(Path.Combine(root, "quick-capture"))),
                () => desktopPath,
                recycleManagedFolderDeletes: false);
            var watcher = new DesktopAutoOrganizationWatcher(
                settingsService,
                organizer,
                widgetManager,
                () => desktopPath,
                () => clock.Now,
                (delay, cancellationToken) =>
                {
                    requestedDelays.Enqueue(delay);
                    return Task.Delay(1, cancellationToken);
                });
            watcher.AutoOrganizationTargetUnavailable += reports.Enqueue;
            watcher.Start();

            return new TargetHarness(
                desktopPath,
                targetStorage,
                watcher,
                clock,
                requestedDelays,
                reports);
        }

        public void Dispose() => _watcher.Dispose();
    }

    private sealed class ManualClock
    {
        private readonly object _gate = new();
        private DateTimeOffset _now;

        public ManualClock(DateTimeOffset start)
        {
            _now = start;
        }

        public DateTimeOffset Now
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }
        }

        public void Advance(TimeSpan span)
        {
            lock (_gate)
            {
                _now += span;
            }
        }
    }
}
