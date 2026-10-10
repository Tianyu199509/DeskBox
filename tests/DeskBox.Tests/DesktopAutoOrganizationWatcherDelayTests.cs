using System.Collections.Concurrent;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Delay contracts for desktop auto-organization: a newly observed file is
/// held for the configured interval (the realtime preset is 10 seconds)
/// before the move runs, and a later change re-arms the wait from that
/// change instead of letting the file move on the original schedule.
/// </summary>
public sealed class DesktopAutoOrganizationWatcherDelayTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealtimeDefault_HoldsANewFileForTenSecondsThenMovesIt()
    {
        using var harness = WatcherHarness.Create(_root, delaySeconds: 10);
        string desktopFile = Path.Combine(harness.DesktopPath, "note.txt");
        File.WriteAllText(desktopFile, "content");

        await WaitUntilAsync(
            () => harness.RequestedDelays.Any(d => d >= TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        Assert.True(File.Exists(desktopFile));

        // The advance clears the due time plus the directory-quiet and
        // stability gates, which also run on the manual clock.
        harness.Clock.Advance(TimeSpan.FromSeconds(40));
        await WaitUntilFileMovedAsync(harness, desktopFile);
        Assert.True(File.Exists(Path.Combine(harness.TargetStoragePath, "note.txt")));
        await WaitUntilAsync(() => !harness.Completed.IsEmpty);
        Assert.All(harness.Completed, e => Assert.Equal("note.txt", e.FileName));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(43200)]
    public async Task LongerPresets_HoldTheFileUntilTheConfiguredIntervalElapses(
        int delaySeconds)
    {
        using var harness = WatcherHarness.Create(_root, delaySeconds);
        string desktopFile = Path.Combine(harness.DesktopPath, "note.txt");
        File.WriteAllText(desktopFile, "content");

        var interval = TimeSpan.FromSeconds(delaySeconds);
        await WaitUntilAsync(() => harness.RequestedDelays.Any(d => d >= interval));
        harness.Clock.Advance(interval - TimeSpan.FromSeconds(1));
        await Task.Delay(300);
        Assert.True(File.Exists(desktopFile));

        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilFileMovedAsync(harness, desktopFile);
        Assert.True(File.Exists(Path.Combine(harness.TargetStoragePath, "note.txt")));
    }

    [Fact]
    public async Task ChangeDuringTheWait_ReArmsItFromTheNewChange()
    {
        using var harness = WatcherHarness.Create(_root, delaySeconds: 60);
        string desktopFile = Path.Combine(harness.DesktopPath, "note.txt");
        File.WriteAllText(desktopFile, "v1");

        var interval = TimeSpan.FromSeconds(60);
        await WaitUntilAsync(() => harness.RequestedDelays.Any(d => d >= interval));

        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        File.WriteAllText(desktopFile, "v2");
        await WaitUntilAsync(
            () => harness.RequestedDelays.Count(d => d >= interval) >= 2);

        // Past the original due time: the re-armed wait must still hold.
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(300);
        Assert.True(File.Exists(desktopFile));

        harness.Clock.Advance(TimeSpan.FromSeconds(40));
        await WaitUntilFileMovedAsync(harness, desktopFile);
        Assert.True(File.Exists(Path.Combine(harness.TargetStoragePath, "note.txt")));
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

    /// <summary>
    /// Waits for the file to move while nudging the manual clock past the
    /// watcher's deferred-retry window: a transient failure at move time
    /// schedules a retry two fake minutes out, which a frozen clock would
    /// never reach within a real-time-only wait.
    /// </summary>
    private static async Task WaitUntilFileMovedAsync(
        WatcherHarness harness,
        string desktopFile)
    {
        TimeSpan advanced = TimeSpan.Zero;
        TimeSpan budget = TimeSpan.FromMinutes(6);
        TimeSpan step = TimeSpan.FromMinutes(2.5);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (File.Exists(desktopFile))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"File was not moved before the timeout " +
                    $"(clock advanced {advanced} beyond the scripted steps).");
            }

            await Task.Delay(100);
            if (advanced < budget)
            {
                harness.Clock.Advance(step);
                advanced += step;
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(20);
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

    private sealed class WatcherHarness : IDisposable
    {
        public string DesktopPath { get; }
        public string TargetStoragePath { get; }
        public ManualClock Clock { get; }
        public ConcurrentQueue<TimeSpan> RequestedDelays { get; }
        public ConcurrentQueue<DesktopAutoOrganizationCompleted> Completed { get; }

        private readonly DesktopAutoOrganizationWatcher _watcher;

        private WatcherHarness(
            string desktopPath,
            string targetStoragePath,
            DesktopAutoOrganizationWatcher watcher,
            ManualClock clock,
            ConcurrentQueue<TimeSpan> requestedDelays,
            ConcurrentQueue<DesktopAutoOrganizationCompleted> completed)
        {
            DesktopPath = desktopPath;
            TargetStoragePath = targetStoragePath;
            _watcher = watcher;
            Clock = clock;
            RequestedDelays = requestedDelays;
            Completed = completed;
        }

        public static WatcherHarness Create(string root, int delaySeconds)
        {
            string desktopPath = Directory.CreateDirectory(
                Path.Combine(root, "desktop")).FullName;
            string targetStorage = Directory.CreateDirectory(
                Path.Combine(root, "docs-widget")).FullName;
            var settingsService = new SettingsService(Path.Combine(root, "settings"));
            settingsService.Settings.DesktopAutoOrganizationEnabled = true;
            settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds =
                delaySeconds;
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
            var completed = new ConcurrentQueue<DesktopAutoOrganizationCompleted>();
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settingsService, fileService);
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
            watcher.ItemOrganized += completed.Enqueue;
            watcher.Start();

            return new WatcherHarness(
                desktopPath,
                targetStorage,
                watcher,
                clock,
                requestedDelays,
                completed);
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
