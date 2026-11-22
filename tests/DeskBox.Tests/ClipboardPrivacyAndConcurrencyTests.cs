using System.Security.Cryptography;
using System.Text;
using DeskBox.Models;
using DeskBox.Features.Clipboard;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class ClipboardPrivacyAndConcurrencyTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void KeyedContentHashes_AreDeterministicPerKey_AndDifferFromPlainSha256()
    {
        byte[] keyA = RandomNumberGenerator.GetBytes(32);
        byte[] keyB = RandomNumberGenerator.GetBytes(32);

        string textA1 = ClipboardContentHashing.ComputeNormalizedTextContentHash("hello world", keyA);
        string textA2 = ClipboardContentHashing.ComputeNormalizedTextContentHash("hello world", keyA);
        string textB = ClipboardContentHashing.ComputeNormalizedTextContentHash("hello world", keyB);
        string textPlain = ClipboardContentHashing.ComputeNormalizedTextContentHash("hello world");
        Assert.Equal(textA1, textA2);
        Assert.StartsWith("txt:h1:", textA1, StringComparison.Ordinal);
        Assert.NotEqual(textA1, textB);
        Assert.NotEqual(textA1, textPlain);
        Assert.StartsWith("txt:", textPlain, StringComparison.Ordinal);
        Assert.DoesNotContain(":h1:", textPlain, StringComparison.Ordinal);

        // CRLF normalization still applies under HMAC (dedup parity with the legacy path).
        Assert.Equal(
            ClipboardContentHashing.ComputeNormalizedTextContentHash("line1\nline2", keyA),
            ClipboardContentHashing.ComputeNormalizedTextContentHash("line1\r\nline2", keyA));

        string imgA = ClipboardContentHashing.ComputeImageContentHash(OnePixelPng, keyA);
        Assert.Equal(imgA, ClipboardContentHashing.ComputeImageContentHash(OnePixelPng, keyA));
        Assert.StartsWith("img:h1:", imgA, StringComparison.Ordinal);
        Assert.NotEqual(imgA, ClipboardContentHashing.ComputeImageContentHash(OnePixelPng, keyB));
        Assert.Equal(
            ClipboardContentHashing.MediaFileNameFromContentHash(imgA),
            imgA["img:h1:".Length..] + ".png");

        string filesA = ClipboardContentHashing.ComputeFilesContentHash(["C:\\a.txt", "C:\\b.txt"], keyA);
        Assert.Equal(filesA, ClipboardContentHashing.ComputeFilesContentHash(["C:\\b.txt", "C:\\a.txt"], keyA));
        Assert.StartsWith("files:h1:", filesA, StringComparison.Ordinal);
        Assert.NotEqual(filesA, ClipboardContentHashing.ComputeFilesContentHash(["C:\\a.txt", "C:\\b.txt"], keyB));
    }

    [Fact]
    public async Task LegacyPlainHashes_MigrateToKeyedHashes_OnLoad_AndImageFilesAreRenamed()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPrivacy", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempRoot);
            string textPlainHash = ClipboardContentHashing.ComputeNormalizedTextContentHash("legacy secret text");
            string imgPlainHash = ClipboardContentHashing.ComputeImageContentHash(OnePixelPng);
            string legacyLeaf = imgPlainHash["img:".Length..] + ".png";
            Directory.CreateDirectory(store.ImageDirectory);
            Directory.CreateDirectory(store.ThumbnailDirectory);
            await File.WriteAllBytesAsync(Path.Combine(store.ImageDirectory, legacyLeaf), OnePixelPng);
            await File.WriteAllBytesAsync(Path.Combine(store.ThumbnailDirectory, legacyLeaf), OnePixelPng);

            await store.SaveAsync(new ClipboardStoreDocument
            {
                Items =
                {
                    new ClipboardHistoryItem
                    {
                        Id = "legacy-text",
                        Kind = ClipboardItemKind.Text,
                        ContentHash = textPlainHash,
                        TextContent = "legacy secret text",
                        Summary = "legacy secret text"
                    },
                    new ClipboardHistoryItem
                    {
                        Id = "legacy-image",
                        Kind = ClipboardItemKind.Image,
                        ContentHash = imgPlainHash,
                        ImageFileName = legacyLeaf,
                        ThumbnailFileName = legacyLeaf,
                        Summary = "1 × 1"
                    }
                }
            });

            ClipboardStoreDocument loaded = await new ClipboardStore(tempRoot).LoadAsync();

            ClipboardHistoryItem text = Assert.Single(loaded.Items, i => i.Id == "legacy-text");
            Assert.StartsWith("txt:h1:", text.ContentHash, StringComparison.Ordinal);
            Assert.Equal(
                ClipboardContentHashing.ComputeNormalizedTextContentHash("legacy secret text", store.GetOrCreateContentHashKey()),
                text.ContentHash);

            ClipboardHistoryItem image = Assert.Single(loaded.Items, i => i.Id == "legacy-image");
            Assert.StartsWith("img:h1:", image.ContentHash, StringComparison.Ordinal);
            Assert.Equal(ClipboardContentHashing.MediaFileNameFromContentHash(image.ContentHash), image.ImageFileName);
            Assert.Equal(image.ImageFileName, image.ThumbnailFileName);
            Assert.True(File.Exists(Path.Combine(store.ImageDirectory, image.ImageFileName)));
            Assert.False(File.Exists(Path.Combine(store.ImageDirectory, legacyLeaf)));
            Assert.True(File.Exists(Path.Combine(store.ThumbnailDirectory, image.ThumbnailFileName)));

            // Second load is a no-op: keyed hashes are left untouched.
            ClipboardStoreDocument reloaded = await new ClipboardStore(tempRoot).LoadAsync();
            Assert.Equal(image.ContentHash, Assert.Single(reloaded.Items, i => i.Id == "legacy-image").ContentHash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ContentHashKey_PersistsAcrossStoreInstances_AndIsDpapiProtected()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPrivacy", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempRoot);
            byte[] key1 = store.GetOrCreateContentHashKey();
            Assert.Equal(32, key1.Length);
            Assert.Equal(key1, store.GetOrCreateContentHashKey());
            Assert.Equal(key1, new ClipboardStore(tempRoot).GetOrCreateContentHashKey());

            string keyFile = Path.Combine(tempRoot, "hmac-key.bin");
            Assert.True(File.Exists(keyFile));
            string atRest = File.ReadAllText(keyFile);
            Assert.DoesNotContain(Convert.ToBase64String(key1), atRest, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SpilledLegacyText_MigratesToFullPayloadHmacHash_NotTruncatedPreview()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPrivacy", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempRoot);
            string fullText = new string('A', 2400) + "-tail-marker-9876";
            string legacyHash = ClipboardContentHashing.ComputeNormalizedTextContentHash(fullText);

            await store.SaveAsync(new ClipboardStoreDocument
            {
                Items =
                {
                    new ClipboardHistoryItem
                    {
                        Id = "spilled-legacy",
                        Kind = ClipboardItemKind.Text,
                        ContentHash = legacyHash,
                        TextContent = fullText,
                        Summary = "spilled"
                    }
                }
            });

            ClipboardStoreDocument loaded = await new ClipboardStore(tempRoot).LoadAsync();
            ClipboardHistoryItem migrated = Assert.Single(loaded.Items);
            Assert.False(string.IsNullOrWhiteSpace(migrated.TextPayloadFileName));

            byte[] hmacKey = store.GetOrCreateContentHashKey();
            string expectedFullHash = ClipboardContentHashing.ComputeNormalizedTextContentHash(fullText, hmacKey);
            string wrongTruncatedHash = ClipboardContentHashing.ComputeNormalizedTextContentHash(fullText.AsSpan(0, 2048), hmacKey);
            Assert.NotEqual(wrongTruncatedHash, expectedFullHash);
            Assert.Equal(expectedFullHash, migrated.ContentHash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SensitiveImage_EncryptsMediaFilesAtRestWithDpapi_PreservesDimensionsSummary_AndExportsDecryptedPng()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPrivacy", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempRoot);
            byte[] key = store.GetOrCreateContentHashKey();
            string hash = ClipboardContentHashing.ComputeImageContentHash(OnePixelPng, key);
            string leaf = ClipboardContentHashing.MediaFileNameFromContentHash(hash);

            Directory.CreateDirectory(store.ImageDirectory);
            Directory.CreateDirectory(store.ThumbnailDirectory);
            string imgPath = Path.Combine(store.ImageDirectory, leaf);
            string thumbPath = Path.Combine(store.ThumbnailDirectory, leaf);
            await File.WriteAllBytesAsync(imgPath, OnePixelPng);
            await File.WriteAllBytesAsync(thumbPath, OnePixelPng);

            await store.SaveAsync(new ClipboardStoreDocument
            {
                Items =
                {
                    new ClipboardHistoryItem
                    {
                        Id = "secret-shot",
                        Kind = ClipboardItemKind.Image,
                        ContentHash = hash,
                        ImageFileName = leaf,
                        ThumbnailFileName = leaf,
                        ImageWidth = 1920,
                        ImageHeight = 1080,
                        Summary = "1920 × 1080",
                        OcrText = "ghp_1234567890abcdefghijklmnopqrstuvwxyzABCD",
                        IsSensitive = true
                    }
                }
            });

            Assert.True(DeskBox.Platform.ClipboardStorageFileOps.IsDpapiProtectedMediaFile(imgPath));
            Assert.True(DeskBox.Platform.ClipboardStorageFileOps.IsDpapiProtectedMediaFile(thumbPath));
            byte[] rawOnDisk = await File.ReadAllBytesAsync(imgPath);
            Assert.NotEqual(OnePixelPng, rawOnDisk);

            string rawJson = await File.ReadAllTextAsync(store.StorePath);
            Assert.Contains("1920 \\u00D7 1080", rawJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghp_1234567890", rawJson, StringComparison.Ordinal);

            Assert.True(DeskBox.Platform.ClipboardStorageFileOps.TryReadMediaBytes(imgPath, out byte[] decrypted));
            Assert.Equal(OnePixelPng, decrypted);

            string exportedPath = Path.Combine(tempRoot, "exported.png");
            DeskBox.Platform.ClipboardStorageFileOps.CopyFileOverwrite(imgPath, exportedPath);
            Assert.Equal(OnePixelPng, await File.ReadAllBytesAsync(exportedPath));

            ClipboardStoreDocument reloaded = await new ClipboardStore(tempRoot).LoadAsync();
            ClipboardHistoryItem hydrated = Assert.Single(reloaded.Items);
            Assert.Equal("1920 × 1080", hydrated.Summary);
            Assert.Equal("ghp_1234567890abcdefghijklmnopqrstuvwxyzABCD", hydrated.OcrText);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InFlightMediaRefCount_PreservesSharedPngUntilLastConcurrentCaptureReleases()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPrivacy", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempRoot);
            var settings = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            using var service = new ClipboardHistoryService(store, settings);

            Directory.CreateDirectory(store.ImageDirectory);
            string sharedLeaf = "shared_capture.png";
            string imgPath = Path.Combine(store.ImageDirectory, sharedLeaf);
            await File.WriteAllBytesAsync(imgPath, OnePixelPng);

            service.AcquireInFlightMediaLease(sharedLeaf);
            service.AcquireInFlightMediaLease(sharedLeaf);
            service.ReleaseInFlightMediaLease(sharedLeaf);

            // One capture is still in flight: orphan sweep must preserve the file.
            _ = await service.ClearUnpinnedAsync();
            Assert.True(File.Exists(imgPath));

            // Final capture releases its lease: subsequent orphan sweep deletes unreferenced media.
            service.ReleaseInFlightMediaLease(sharedLeaf);
            _ = await service.ClearUnpinnedAsync();
            Assert.False(File.Exists(imgPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ClipboardUrlTitleResolver_DefaultsOff_SkipsSensitiveLinks_AndDecodesGbkAndBoundaryUtf8()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.Clipboard.ClipboardFetchUrlTitles);
        Assert.False(defaults.ClipboardFetchUrlTitles);

        // Clean public article URLs are eligible when the user opts in.
        Assert.True(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://learn.microsoft.com/dotnet/whats-new/dotnet-10", out Uri? cleanUri));
        Assert.NotNull(cleanUri);

        // One-time magic/login/reset/verify/unsubscribe/presigned/local URLs must NEVER be prefetched.
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://app.example.com/auth/magic?id=123", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://app.example.com/account/verify-email?u=1", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://app.example.com/password/reset/abc", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://mail.example.com/unsubscribe?list=weekly", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://bucket.s3.amazonaws.com/report.html?X-Amz-Signature=deadbeef", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://portal.example.com/view?token=secret123", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("http://localhost:3000/docs", out _));
        Assert.False(ClipboardUrlTitleResolver.IsSafePublicPageCandidate("https://intranet.local/wiki", out _));

        // GBK/GB18030 <meta charset> decoding and incomplete trailing UTF-8 sequence trimming.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        byte[] gbkHtml = System.Text.Encoding.GetEncoding("GB18030")
            .GetBytes("<html><head><meta charset=\"gbk\"><title>中文网页标题</title></head></html>");
        string decodedGbk = ClipboardUrlTitleResolver.DecodeHtmlSnippet(gbkHtml, headerCharset: null);
        Assert.True(ClipboardClassifier.TryExtractHtmlDocumentTitle(decodedGbk, out string? gbkTitle));
        Assert.Equal("中文网页标题", gbkTitle);

        byte[] utf8WithTrailingSplit = [.. "<title>边界测试</title>"u8, 0xE4, 0xB8];
        string decodedUtf8 = ClipboardUrlTitleResolver.DecodeHtmlSnippet(utf8WithTrailingSplit, "utf-8");
        Assert.DoesNotContain('\uFFFD', decodedUtf8);
        Assert.True(ClipboardClassifier.TryExtractHtmlDocumentTitle(decodedUtf8, out string? utf8Title));
        Assert.Equal("边界测试", utf8Title);
    }
}