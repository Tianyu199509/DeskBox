using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// R1 runtime/data contract repair behavior tests (audit round 21):
/// runtime destroy keeps persistent data (only logical widget deletion
/// removes the instance root), and a failed package shutdown marks the
/// identity Faulted so it cannot be re-activated in this process.
/// </summary>
public class NativeInstanceDataLifecycleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskbox-lifecycle").FullName;
    private bool _disposed;

    private string InstanceRoot(string packageId, string instanceId)
    {
        string hash = NativePackageIdentity.InstanceStorageKey(instanceId);
        return Path.Combine(_root, "packages", "a".PadLeft(64, '0'), packageId, "instances", hash);
    }

    [Fact]
    public async Task DeleteRemovesPopulatedInstanceRoot()
    {
        string root = InstanceRoot("deskbox.test", "widget-1");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "glance-data.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "glance-state.json"), "{}");

        await NativeInstanceDataLifecycle.DeleteAsync("deskbox.test", "widget-1", _root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DeleteIsIdempotent()
    {
        string root = InstanceRoot("deskbox.test", "widget-2");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "glance-data.json"), "{}");

        await NativeInstanceDataLifecycle.DeleteAsync("deskbox.test", "widget-2", _root);
        await NativeInstanceDataLifecycle.DeleteAsync("deskbox.test", "widget-2", _root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DeleteWithNoRootIsSafe()
    {
        await NativeInstanceDataLifecycle.DeleteAsync("deskbox.test", "never-existed", _root);
        // No throw = pass.
    }

    [Fact]
    public async Task DeleteScansAllPublishers()
    {
        // Same instance id under two publishers (dev → official upgrade path):
        // both roots must be cleaned.
        var hash = NativePackageIdentity.InstanceStorageKey("widget-3");
        string rootA = Path.Combine(_root, "packages", "a".PadLeft(64, '0'), "deskbox.test", "instances", hash);
        string rootB = Path.Combine(_root, "packages", "b".PadLeft(64, '0'), "deskbox.test", "instances", hash);
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        await File.WriteAllTextAsync(Path.Combine(rootA, "data.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(rootB, "data.json"), "{}");

        await NativeInstanceDataLifecycle.DeleteAsync("deskbox.test", "widget-3", _root);

        Assert.False(Directory.Exists(rootA));
        Assert.False(Directory.Exists(rootB));
    }

    [Fact]
    public void RuntimeDestroyDoesNotDeleteDataRoots()
    {
        // Source ratchet: the loader's DestroyWidget and Shutdown must not
        // contain any Directory.Delete call — persistent data removal is
        // exclusively owned by NativeInstanceDataLifecycle.DeleteAsync.
        string loader = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs"));
        Assert.DoesNotContain("Directory.Delete", loader);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
