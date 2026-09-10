extern alias GlancePkg;

using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using PackageDataFile = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDataFile;

namespace DeskBox.Tests;

public sealed class NativeInstanceSettingsSubscriptionTests
{
    [Fact]
    public async Task CommittedSettingsReachTheRunningPackageReaderWithoutRecreation()
    {
        string root = Directory.CreateTempSubdirectory("deskbox-live-settings").FullName;
        try
        {
            var first = new GlanceWidgetStore(Path.Combine(root, "glance", "widgets"), "one");
            var second = new GlanceWidgetStore(Path.Combine(root, "glance", "widgets"), "two");
            await first.SaveAsync(new GlanceWidgetData { ShowTime = true });
            await second.SaveAsync(new GlanceWidgetData { ShowTime = true });
            var adapter = new StoreAdapter(first);
            var queue = new Queue<Action>();
            var identity = new NativePackageIdentity(new string('a', 64), "deskbox.glance");
            string instanceRoot = identity.ResolveInstanceDataRoot(root, "one");
            int refreshed = 0;
            using var subscription = new NativeInstanceSettingsSubscription(
                adapter, "one", action => { queue.Enqueue(action); return true; },
                () => NativeWidgetDataMigration.TrySync(adapter, identity.PublisherFingerprint,
                    identity.PackageId, "one", root),
                () => refreshed++);

            queue.Dequeue()();
            Assert.True(PackageDataFile.Load(instanceRoot)!.Settings.ShowTime);

            await second.UpdateAsync(data => data.ShowTime = false);
            Assert.Empty(queue); // another widget must not refresh this one
            await first.UpdateAsync(data =>
            {
                data.ShowTime = false;
                data.ShowPhotoControls = false;
                data.Layout = GlanceLayoutMode.Centered;
                data.RotationIntervalMinutes = 5;
            });
            await first.UpdateAsync(data => data.RotationIntervalMinutes = 30);
            Assert.Single(queue); // rapid commits coalesce to the latest snapshot
            queue.Dequeue()();
            var settings = PackageDataFile.Load(instanceRoot)!.Settings;
            Assert.False(settings.ShowTime);
            Assert.False(settings.ShowPhotoControls);
            Assert.Equal("Centered", settings.Layout.ToString());
            Assert.Equal(30, settings.RotationIntervalMinutes);
            Assert.Equal(2, refreshed);

            // The native package's minimal write-through patch goes through
            // the same successful-persistence notification as the host UI.
            var patch = GlanceInstanceConfigPatch.TryParse("""{"showChineseFestivals":false}""")!;
            await GlanceInstanceConfigPatch.CommitAsync(first, patch);
            queue.Dequeue()();
            Assert.False(PackageDataFile.Load(instanceRoot)!.Settings.ShowChineseFestivals);
            Assert.False(PackageDataFile.Load(instanceRoot)!.Settings.ShowTime);

            await first.UpdateAsync(data => data.ShowTime = true);
            subscription.Dispose();
            subscription.Dispose();
            queue.Dequeue()(); // queued before destruction: no sync, no ABI call
            Assert.Equal(3, refreshed);
            Assert.False(PackageDataFile.Load(instanceRoot)!.Settings.ShowTime);
            await first.UpdateAsync(data => data.ShowTime = false);
            Assert.Empty(queue);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedSnapshotDoesNotRefreshAndLaterNotificationRetries()
    {
        var adapter = new FakeAdapter();
        var queue = new Queue<Action>();
        bool syncSucceeds = false;
        int refreshed = 0;
        using var subscription = new NativeInstanceSettingsSubscription(
            adapter, "one", action => { queue.Enqueue(action); return true; },
            () => syncSucceeds, () => refreshed++);
        queue.Dequeue()();
        Assert.Equal(0, refreshed);
        syncSucceeds = true;
        adapter.Notify();
        queue.Dequeue()();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public void RejectedDispatcherQueueDoesNotLoseLaterChanges()
    {
        var adapter = new FakeAdapter();
        var queue = new Queue<Action>();
        bool accept = false;
        using var subscription = new NativeInstanceSettingsSubscription(
            adapter, "one", action =>
            {
                if (!accept) return false;
                queue.Enqueue(action);
                return true;
            }, () => true, () => { });
        Assert.Empty(queue);
        accept = true;
        adapter.Notify();
        Assert.Single(queue);
    }

    [Fact]
    public void SnapshotExceptionIsContainedAndExplicitRefreshRetries()
    {
        var queue = new Queue<Action>();
        int attempts = 0, refreshed = 0;
        using var subscription = new NativeInstanceSettingsSubscription(
            new FakeAdapter(), "one", action => { queue.Enqueue(action); return true; },
            () => ++attempts == 1 ? throw new IOException("fixture") : true,
            () => refreshed++);
        queue.Dequeue()();
        Assert.Equal(0, refreshed);
        subscription.RequestRefresh();
        queue.Dequeue()();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task FailedAuthoritySaveDoesNotPublishASettingsChange()
    {
        string root = Directory.CreateTempSubdirectory("deskbox-live-settings-fail").FullName;
        try
        {
            var store = new GlanceWidgetStore(root, "one");
            await store.SaveAsync(new GlanceWidgetData());
            int notifications = 0;
            using var observer = GlanceInstanceMigration.SubscribeChanges(store, () => notifications++);
            using (var locked = new FileStream(store.StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => store.UpdateAsync(data => data.ShowTime = false));
            Assert.Equal(0, notifications);
            await store.SaveAsync(new GlanceWidgetData { ShowTime = false });
            Assert.Equal(1, notifications);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class StoreAdapter(GlanceWidgetStore store) : ILegacyInstanceMigration
    {
        public string DataFileName => GlanceInstanceMigration.Instance.DataFileName;
        public string? ResolveLegacyContent(string root, string id) =>
            GlanceInstanceMigration.Instance.ResolveLegacyContent(root, id);
        public bool TryApplyPatch(string id, string patch) => false;
        public IDisposable SubscribeChanges(string id, Action changed) =>
            GlanceInstanceMigration.SubscribeChanges(store, changed);
    }

    private sealed class FakeAdapter : ILegacyInstanceMigration, IDisposable
    {
        private Action? _changed;
        public string DataFileName => "fixture.json";
        public string? ResolveLegacyContent(string root, string id) => null;
        public bool TryApplyPatch(string id, string patch) => false;
        public IDisposable SubscribeChanges(string id, Action changed)
        {
            _changed = changed;
            return this;
        }
        public void Notify() => _changed?.Invoke();
        public void Dispose() => _changed = null;
    }
}
