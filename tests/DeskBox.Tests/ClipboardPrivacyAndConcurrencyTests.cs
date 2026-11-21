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
}