extern alias GlancePkg;

using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using Importer = GlancePkg::DeskBox.GlancePackage.Services.LegacyGlanceImageCacheImporter;

namespace DeskBox.Tests;

public sealed class NativeGlanceImageCacheMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskbox-glance-cache-migration-").FullName;
    private string Source => Path.Combine(_root, "cache", "glance");
    private string Package => Path.Combine(_root, "package");
    private string Target => Path.Combine(Package, "cache", "glance");
    private string TargetCatalog => Path.Combine(Target, "catalog.json");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6b0sAAAAASUVORK5CYII=");

    [Fact]
    public async Task FirstImportRewritesOnlyPathsAndSecondImportPreservesEverything()
    {
        string image = WriteImage(Source, "first.png");
        WriteCatalog(Source, image);
        byte[] originalCatalog = File.ReadAllBytes(Path.Combine(Source, "catalog.json"));
        GlanceInstanceMigration.Instance.PreparePackageData(_root, Package);

        await Importer.ImportAsync(Package);

        byte[] importedCatalog = File.ReadAllBytes(TargetCatalog);
        using JsonDocument document = JsonDocument.Parse(importedCatalog);
        JsonElement entry = document.RootElement[0];
        string path = entry.GetProperty("localPath").GetString()!;
        Assert.StartsWith(Path.Combine(Target, "images") + Path.DirectorySeparatorChar, path);
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.Equal("title-0", entry.GetProperty("title").GetString());
        Assert.Equal("credit", entry.GetProperty("futureMetadata").GetProperty("attribution").GetString());
        Assert.Equal("https://example.com/0", entry.GetProperty("sourcePageUrl").GetString());
        Assert.Equal("Bing", entry.GetProperty("onlineProvider").GetString());

        await Importer.ImportAsync(Package);

        Assert.Equal(importedCatalog, File.ReadAllBytes(TargetCatalog));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
        Assert.Equal(originalCatalog, File.ReadAllBytes(Path.Combine(Source, "catalog.json")));
        Assert.Equal(Png, File.ReadAllBytes(image));
        Assert.True(File.Exists(Path.Combine(Package, "legacy-glance-cache.json")));
    }

    [Fact]
    public void HostPreparesHintEvenWhenThereAreNoLegacyInstanceSettings()
    {
        WriteCatalog(Source, WriteImage(Source, "legacy.png"));
        var identity = new NativePackageIdentity(new string('a', 64), "deskbox.glance");
        Assert.False(NativeWidgetDataMigration.TrySync(GlanceInstanceMigration.Instance,
            identity.PublisherFingerprint, identity.PackageId, "widget", _root));
        string packageRoot = identity.ResolvePackageDataRoot(_root);
        using JsonDocument hint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(packageRoot, "legacy-glance-cache.json")));
        Assert.Equal(1, hint.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(Source, hint.RootElement.GetProperty("sourceRoot").GetString());
        Assert.False(Directory.Exists(Path.Combine(packageRoot, "cache")));
    }

    [Fact]
    public void DefaultPreparationIsOptionalAndPreparationFailureDoesNotBlockSettings()
    {
        var identity = new NativePackageIdentity(new string('b', 64), "test.package");
        ILegacyInstanceMigration defaultAdapter = new DefaultAdapter();
        defaultAdapter.PreparePackageData(_root, Package);
        Assert.False(Directory.Exists(Package));
        Assert.True(NativeWidgetDataMigration.TrySync(new FailingPreparationAdapter(),
            identity.PublisherFingerprint, identity.PackageId, "widget", _root));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(identity.ResolveInstanceDataRoot(_root, "widget"), "data.json")));
    }

    [Fact]
    public async Task ExistingUsablePackageCatalogAndImagesWin()
    {
        WriteCatalog(Source, WriteImage(Source, "old.png"));
        string existingImage = WriteImage(Target, "new.png");
        WriteCatalog(Target, existingImage);
        byte[] existing = File.ReadAllBytes(TargetCatalog);

        await Importer.ImportAsync(Package, Source);

        Assert.Equal(existing, File.ReadAllBytes(TargetCatalog));
        Assert.Equal(Png, File.ReadAllBytes(existingImage));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Fact]
    public async Task MissingImageIsSkippedAndUsableImageRecoversOfflineWithItsOwnMetadata()
    {
        string first = WriteImage(Source, "first.png");
        string missing = Path.Combine(Source, "images", "missing.png");
        WriteCatalog(Source, missing, first);
        byte[] original = File.ReadAllBytes(Path.Combine(Source, "catalog.json"));
        Directory.CreateDirectory(Package);

        await Importer.ImportAsync(Package, Source);

        byte[] imported = File.ReadAllBytes(TargetCatalog);
        using JsonDocument catalog = JsonDocument.Parse(imported);
        Assert.Single(catalog.RootElement.EnumerateArray());
        Assert.Equal("id-1", catalog.RootElement[0].GetProperty("id").GetString());
        Assert.Equal("title-1", catalog.RootElement[0].GetProperty("title").GetString());
        Assert.Equal(Png, File.ReadAllBytes(catalog.RootElement[0].GetProperty("localPath").GetString()!));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Source, "catalog.json")));

        // Once a usable package catalog exists it remains authoritative, even
        // if an expired legacy image subsequently reappears.
        WriteImage(Source, "missing.png");
        await Importer.ImportAsync(Package, Source);
        Assert.Equal(imported, File.ReadAllBytes(TargetCatalog));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Fact]
    public async Task AllMissingImagesLeaveNoCatalogAndCanBeRetriedWhenRestored()
    {
        WriteCatalog(Source, Path.Combine(Source, "images", "missing.png"));
        Directory.CreateDirectory(Package);
        await Importer.ImportAsync(Package, Source);
        Assert.False(File.Exists(TargetCatalog));
        Assert.Empty(Directory.GetFiles(Path.Combine(Target, "images")));

        WriteImage(Source, "missing.png");
        await Importer.ImportAsync(Package, Source);
        Assert.True(File.Exists(TargetCatalog));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Fact]
    public async Task AllExpiredEntriesPreserveDamagedPackageCatalogAndUncataloguedImages()
    {
        string existingImage = WriteImage(Target, "keep.png");
        File.WriteAllText(TargetCatalog, "damaged catalog");
        WriteCatalog(Source, Path.Combine(Source, "images", "missing.png"));

        await Importer.ImportAsync(Package, Source);

        Assert.Equal("damaged catalog", File.ReadAllText(TargetCatalog));
        Assert.Equal(Png, File.ReadAllBytes(existingImage));
    }

    [Fact]
    public async Task SuccessfulRetryReplacesDamagedCatalogButKeepsExistingImageFiles()
    {
        string existingImage = WriteImage(Target, "keep.png");
        File.WriteAllText(TargetCatalog, "damaged catalog");
        WriteImage(Source, "first.png");
        WriteCatalog(Source, Path.Combine("images", "first.png"));

        await Importer.ImportAsync(Package, Source);

        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes(TargetCatalog));
        Assert.Single(catalog.RootElement.EnumerateArray());
        Assert.Equal(Png, File.ReadAllBytes(catalog.RootElement[0].GetProperty("localPath").GetString()!));
        Assert.Equal(Png, File.ReadAllBytes(existingImage));
    }

    [Fact]
    public async Task LockedSourceRollsBackCopiedFilesAndRetriesAfterUnlock()
    {
        string first = WriteImage(Source, "first.png");
        string second = WriteImage(Source, "second.png");
        WriteCatalog(Source, first, second);
        Directory.CreateDirectory(Package);
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Importer.ImportAsync(Package, Source);
            Assert.False(File.Exists(TargetCatalog));
            Assert.Empty(Directory.GetFiles(Path.Combine(Target, "images")));
        }
        await Importer.ImportAsync(Package, Source);
        Assert.True(File.Exists(TargetCatalog));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Target, "images")).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAbsoluteAndTraversalPathsAreRejected(bool relative)
    {
        string outside = WriteImage(_root, "outside.png");
        string reference = relative ? Path.GetRelativePath(Source, outside) : outside;
        WriteCatalog(Source, WriteImage(Source, "valid.png"), reference);
        Directory.CreateDirectory(Package);

        await Importer.ImportAsync(Package, Source);

        Assert.False(File.Exists(TargetCatalog));
        Assert.Equal(Png, File.ReadAllBytes(outside));
        Assert.Empty(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Theory]
    [InlineData("not-image.png")]
    [InlineData("script.exe")]
    public async Task NonImageContentAndUnsupportedExtensionsAreSkippedAlongsideUsableImages(string name)
    {
        string path = WriteImage(Source, name);
        if (name.EndsWith(".png", StringComparison.Ordinal)) File.WriteAllText(path, "this is not an image");
        WriteCatalog(Source, path, WriteImage(Source, "valid.png"));
        Directory.CreateDirectory(Package);
        await Importer.ImportAsync(Package, Source);
        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes(TargetCatalog));
        Assert.Single(catalog.RootElement.EnumerateArray());
        Assert.Equal("id-1", catalog.RootElement[0].GetProperty("id").GetString());
        Assert.Equal(Png, File.ReadAllBytes(catalog.RootElement[0].GetProperty("localPath").GetString()!));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Fact]
    public async Task OversizedImagesAndTooManyEntriesAreRejected()
    {
        string image = WriteImage(Source, "large.png");
        using (FileStream file = File.OpenWrite(image)) file.SetLength(Importer.MaximumImageBytes + 1);
        WriteCatalog(Source, image);
        Directory.CreateDirectory(Package);
        await Importer.ImportAsync(Package, Source);
        Assert.False(File.Exists(TargetCatalog));

        File.WriteAllBytes(image, Png);
        WriteCatalog(Source, Enumerable.Repeat(image, Importer.MaximumEntries + 1).ToArray());
        await Importer.ImportAsync(Package, Source);
        Assert.False(File.Exists(TargetCatalog));
    }

    [Fact]
    public async Task LegalLegacyCacheLargerThan128MiBCanBeImportedWithBoundedCopies()
    {
        Assert.Equal(72L * 18 * 1024 * 1024, Importer.MaximumTotalBytes);
        string image = WriteImage(Source, "large.png");
        // Reuse one source fixture to keep source disk use low. Eight legal
        // 18 MiB entries produce a 144 MiB destination, above the old budget.
        using (FileStream file = File.OpenWrite(image)) file.SetLength(Importer.MaximumImageBytes);
        WriteCatalog(Source, Enumerable.Repeat(image, 8).ToArray());
        Directory.CreateDirectory(Package);

        await Importer.ImportAsync(Package, Source);

        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes(TargetCatalog));
        Assert.Equal(8, catalog.RootElement.GetArrayLength());
        long total = 0;
        foreach (JsonElement entry in catalog.RootElement.EnumerateArray())
        {
            long length = new FileInfo(entry.GetProperty("localPath").GetString()!).Length;
            Assert.Equal(Importer.MaximumImageBytes, length);
            total += length;
        }
        Assert.True(total > 128L * 1024 * 1024);
        Assert.Empty(Directory.GetFiles(Target, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationPropagatesAndNextCallCanRetry()
    {
        WriteCatalog(Source, WriteImage(Source, "first.png"));
        Directory.CreateDirectory(Package);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer.ImportAsync(Package, Source, canceled.Token));
        Assert.False(File.Exists(TargetCatalog));

        // Hold the exact inter-instance gate to exercise cancellation while an
        // import is waiting, without racing the speed of a small image copy.
        using (var gate = new FileStream(Path.Combine(Package, ".legacy-glance-cache.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var waiting = new CancellationTokenSource())
        {
            Task import = Importer.ImportAsync(Package, Source, waiting.Token);
            waiting.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await Importer.ImportAsync(Package, Source);
        Assert.True(File.Exists(TargetCatalog));
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "images")));
    }

    [Fact]
    public async Task TwoConcurrentInstancesCommitOneCatalogAndOneSetOfImages()
    {
        WriteCatalog(Source, WriteImage(Source, "first.png"), WriteImage(Source, "second.png"));
        Directory.CreateDirectory(Package);

        await Task.WhenAll(Importer.ImportAsync(Package, Source), Importer.ImportAsync(Package, Source))
            .WaitAsync(TimeSpan.FromSeconds(20));

        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes(TargetCatalog));
        Assert.Equal(2, catalog.RootElement.GetArrayLength());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Target, "images")).Length);
        Assert.Empty(Directory.GetFiles(Target, "*.tmp", SearchOption.AllDirectories));
    }

    private static string WriteImage(string cacheRoot, string name)
    {
        string images = Path.Combine(cacheRoot, "images");
        Directory.CreateDirectory(images);
        string path = Path.Combine(images, name);
        File.WriteAllBytes(path, Png);
        return path;
    }

    private static void WriteCatalog(string root, params string[] paths)
    {
        Directory.CreateDirectory(root);
        using var stream = File.Create(Path.Combine(root, "catalog.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        for (int index = 0; index < paths.Length; index++)
        {
            writer.WriteStartObject();
            writer.WriteString("id", $"id-{index}");
            writer.WriteString("localPath", paths[index]);
            writer.WriteString("title", $"title-{index}");
            writer.WriteString("sourcePageUrl", $"https://example.com/{index}");
            writer.WriteString("onlineProvider", "Bing");
            writer.WriteStartObject("futureMetadata");
            writer.WriteString("attribution", "credit");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private class DefaultAdapter : ILegacyInstanceMigration
    {
        public string DataFileName => "data.json";
        public string? ResolveLegacyContent(string dataDirectory, string instanceId) => "{}";
        public bool TryApplyPatch(string instanceId, string jsonPatch) => false;
    }

    private sealed class FailingPreparationAdapter : DefaultAdapter, ILegacyInstanceMigration
    {
        public void PreparePackageData(string dataDirectory, string packageDataRoot) => throw new IOException("test failure");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
