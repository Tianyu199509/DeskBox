using System.Buffers.Binary;
using DeskBox.Features.Clipboard;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class ClipboardWidgetTests
{
    private static string FakeToken(params string[] parts) => string.Join(string.Empty, parts);

    [Fact]
    public async Task DefaultEnablement_IsDisabledOnFreshSettings_AndRuntimeLeaseIsIdempotent()
    {
        var freshSettings = new AppSettings();
        Assert.False(FeatureWidgetSettings.IsEnabled(freshSettings, WidgetKind.Clipboard));

        FeatureWidgetSettings.SetEnabled(freshSettings, WidgetKind.Clipboard, true);
        Assert.True(FeatureWidgetSettings.IsEnabled(freshSettings, WidgetKind.Clipboard));

        FeatureWidgetSettings.SetEnabled(freshSettings, WidgetKind.Clipboard, false);
        Assert.False(FeatureWidgetSettings.IsEnabled(freshSettings, WidgetKind.Clipboard));

        int startCalls = 0;
        int stopCalls = 0;
        var runtime = new ClipboardFeatureRuntime(() => startCalls++, () => stopCalls++);

        await runtime.StartAsync();
        await runtime.StartAsync();
        Assert.Equal(1, startCalls);
        Assert.Equal(0, stopCalls);

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Equal(1, startCalls);
        Assert.Equal(1, stopCalls);
    }

    [Fact]
    public void Classifier_ClassifiesSemanticSubKinds_AndNormalizesColorFormats()
    {
        ClipboardClassifier.ClassifyText(
            "https://github.com/EcoPasteHub/EcoPaste",
            htmlContent: "<a href='https://github.com/EcoPasteHub/EcoPaste'>link</a>",
            rtfContent: null,
            out ClipboardTextSubKind urlKind,
            out string? urlColor);
        Assert.Equal(ClipboardTextSubKind.Url, urlKind);
        Assert.Null(urlColor);

        ClipboardClassifier.ClassifyText("#3b82f6", null, null, out ClipboardTextSubKind hex6Kind, out string? hex6Color);
        Assert.Equal(ClipboardTextSubKind.Color, hex6Kind);
        Assert.Equal("#FF3B82F6", hex6Color);

        ClipboardClassifier.ClassifyText("#0f8", null, null, out ClipboardTextSubKind hex3Kind, out string? hex3Color);
        Assert.Equal(ClipboardTextSubKind.Color, hex3Kind);
        Assert.Equal("#FF00FF88", hex3Color);

        ClipboardClassifier.ClassifyText("#11223380", null, null, out ClipboardTextSubKind hex8Kind, out string? hex8Color);
        Assert.Equal(ClipboardTextSubKind.Color, hex8Kind);
        Assert.Equal("#80112233", hex8Color);

        ClipboardClassifier.ClassifyText("rgb(16, 185, 129)", null, null, out ClipboardTextSubKind rgbKind, out string? rgbColor);
        Assert.Equal(ClipboardTextSubKind.Color, rgbKind);
        Assert.Equal("#FF10B981", rgbColor);

        ClipboardClassifier.ClassifyText("hsl(0, 100%, 50%)", null, null, out ClipboardTextSubKind hslKind, out string? hslColor);
        Assert.Equal(ClipboardTextSubKind.Color, hslKind);
        Assert.Equal("#FFFF0000", hslColor);

        ClipboardClassifier.ClassifyText("hello@deskbox.app", null, null, out ClipboardTextSubKind emailKind, out _);
        Assert.Equal(ClipboardTextSubKind.Email, emailKind);

        ClipboardClassifier.ClassifyText(@"D:\project\wingezi\README.md", null, null, out ClipboardTextSubKind pathKind, out _);
        Assert.Equal(ClipboardTextSubKind.Path, pathKind);

        ClipboardClassifier.ClassifyText("Rich paragraph\nSecond line", "<b>Rich</b>", null, out ClipboardTextSubKind htmlKind, out _);
        Assert.Equal(ClipboardTextSubKind.Html, htmlKind);

        ClipboardClassifier.ClassifyText("RTF paragraph\nSecond line", null, @"{\rtf1\ansi Rich}", out ClipboardTextSubKind rtfKind, out _);
        Assert.Equal(ClipboardTextSubKind.Rtf, rtfKind);

        string longWithGaps = "First line\r\n\r\n\r\nSecond line\n" + new string('x', 300);
        string summary = ClipboardClassifier.BuildSummary(longWithGaps);
        Assert.Equal(ClipboardClassifier.SummaryMaxChars, summary.Length);
        Assert.StartsWith("First line\nSecond line\n", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_DetectsAndMasksHighConfidenceSecrets_WithoutFlaggingNormalText()
    {
        string openAiKey = FakeToken("sk", "-proj-", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "1234567890abcd");
        string githubPat = FakeToken("ghp", "_", "1234567890", "abcdefghijklmnopqrstuvwx");
        string awsAccessKey = FakeToken("AKIA", "IOSFODNN7", "EXAMPLE");
        string jwt = string.Join(".", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkRlc2tCb3gifQ", "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c");
        string pem = FakeToken("-----BEGIN ", "OPENSSH PRIVATE KEY-----\n", "b3BlbnNzaC1rZXktdjEAAAAABG5vbmU=\n", "-----END ", "OPENSSH PRIVATE KEY-----");
        string labeled = "OPENAI_API_KEY = \"super_secret_production_token_987654\"";

        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(openAiKey));
        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(githubPat));
        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(awsAccessKey));
        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(jwt));
        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(pem));
        Assert.True(ClipboardClassifier.IsHighConfidenceSecret(labeled));

        Assert.False(ClipboardClassifier.IsHighConfidenceSecret("Meeting notes: ship Clipboard widget v1 today."));
        Assert.False(ClipboardClassifier.IsHighConfidenceSecret("https://learn.microsoft.com/en-us/windows/apps/design/"));
        Assert.False(ClipboardClassifier.IsHighConfidenceSecret(@"C:\Program Files\DeskBox\DeskBox.exe"));

        string maskedOpenAi = ClipboardClassifier.MaskSecretForDisplay(openAiKey);
        Assert.Contains("••••••••••••", maskedOpenAi, StringComparison.Ordinal);
        Assert.DoesNotContain("GHIJKLMNOPQRSTUVWXYZ123456", maskedOpenAi, StringComparison.Ordinal);

        string maskedPem = ClipboardClassifier.MaskSecretForDisplay(pem);
        Assert.Contains(FakeToken("-----BEGIN ", "OPENSSH PRIVATE KEY-----"), maskedPem, StringComparison.Ordinal);
        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjE", maskedPem, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformInterop_ParsesPngDimensions_AndBuildsValidBmpFromDib()
    {
        byte[] pngHeader = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(pngHeader, 0);
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(8, 4), 13u);
        pngHeader[12] = (byte)'I';
        pngHeader[13] = (byte)'H';
        pngHeader[14] = (byte)'D';
        pngHeader[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(16, 4), 1920u);
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(20, 4), 1080u);

        Assert.True(ClipboardPlatformInterop.TryParsePngDimensions(pngHeader, out int width, out int height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
        Assert.False(ClipboardPlatformInterop.TryParsePngDimensions(pngHeader.AsSpan(0, 16), out _, out _));
        Assert.False(ClipboardPlatformInterop.TryParsePngDimensions("not-a-png-payload-at-all-1234"u8, out _, out _));

        byte[] dib32 = new byte[40 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(dib32.AsSpan(0, 4), 40u); // biSize
        BinaryPrimitives.WriteInt32LittleEndian(dib32.AsSpan(4, 4), 2);    // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(dib32.AsSpan(8, 4), 2);    // biHeight
        BinaryPrimitives.WriteUInt16LittleEndian(dib32.AsSpan(12, 2), 1);  // biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(dib32.AsSpan(14, 2), 32); // biBitCount

        Assert.True(ClipboardPlatformInterop.TryBuildBmpFromDib(dib32, out byte[]? bmp32));
        Assert.NotNull(bmp32);
        Assert.Equal(14 + dib32.Length, bmp32!.Length);
        Assert.Equal((byte)'B', bmp32[0]);
        Assert.Equal((byte)'M', bmp32[1]);
        Assert.Equal((uint)bmp32.Length, BinaryPrimitives.ReadUInt32LittleEndian(bmp32.AsSpan(2, 4)));
        Assert.Equal(54u, BinaryPrimitives.ReadUInt32LittleEndian(bmp32.AsSpan(10, 4)));

        byte[] dib8 = new byte[40 + 1024 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(dib8.AsSpan(0, 4), 40u);
        BinaryPrimitives.WriteInt32LittleEndian(dib8.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib8.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib8.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib8.AsSpan(14, 2), 8);

        Assert.True(ClipboardPlatformInterop.TryBuildBmpFromDib(dib8, out byte[]? bmp8));
        Assert.NotNull(bmp8);
        Assert.Equal(1078u, BinaryPrimitives.ReadUInt32LittleEndian(bmp8!.AsSpan(10, 4)));
    }

    [Fact]
    public void WriteScope_SuppressesSelfWritesAcrossTextPathsImageAndContentHash()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        try
        {
            Assert.False(DeskBoxClipboardWriteScope.ShouldIgnoreText("hello world"));
            DeskBoxClipboardWriteScope.MarkText("  hello\r\nworld  ");
            Assert.True(DeskBoxClipboardWriteScope.ShouldIgnoreText("hello\nworld"));
            Assert.False(DeskBoxClipboardWriteScope.ShouldIgnoreText("something else"));

            DeskBoxClipboardWriteScope.MarkWrite(
                hasImage: true,
                paths: [@"C:\Work\b.png", @"c:\work\a.png\"]);
            Assert.True(DeskBoxClipboardWriteScope.ShouldIgnoreImage());
            Assert.True(DeskBoxClipboardWriteScope.ShouldIgnorePaths([@"C:\WORK\A.PNG", @"C:\Work\B.png"]));
            Assert.False(DeskBoxClipboardWriteScope.ShouldIgnorePaths([@"C:\Work\C.png"]));

            DeskBoxClipboardWriteScope.MarkContentHash("hash-clip-001");
            Assert.True(DeskBoxClipboardWriteScope.ShouldIgnoreContentHash("hash-clip-001"));
            Assert.False(DeskBoxClipboardWriteScope.ShouldIgnoreContentHash("hash-clip-002"));
        }
        finally
        {
            DeskBoxClipboardWriteScope.ClearForTesting();
        }
    }

    [Fact]
    public async Task HistoryService_DeduplicatesPinsPrunesAndPersistsAcrossStoreReload()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "Clipboard", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            settingsService.Settings.Clipboard.ClipboardMaxItems = 10;
            settingsService.Settings.Clipboard.ClipboardRetentionDays = 7;

            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            var pinnedItem = new ClipboardHistoryItem
            {
                Id = "pin-1",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-pin-1",
                TextContent = "Pinned reference command",
                Summary = "Pinned reference command",
                IsPinned = true
            };
            await service.IngestItemAsync(
                pinnedItem,
                new ClipboardObservedApp { AppName = "Windows Terminal", AppPath = @"C:\Apps\wt.exe" });
            await service.FlushAsync();

            // Backdate an unpinned expired item in the store and verify retention pruning removes it while sparing pinned items.
            var expiredDoc = await store.LoadAsync();
            expiredDoc.Items[0].UpdatedAtUtcTicks = DateTime.UtcNow.AddDays(-30).Ticks;
            expiredDoc.Items.Add(new ClipboardHistoryItem
            {
                Id = "expired-unpinned",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-expired",
                TextContent = "Stale snippet",
                Summary = "Stale snippet",
                IsPinned = false,
                CreatedAtUtcTicks = DateTime.UtcNow.AddDays(-20).Ticks,
                UpdatedAtUtcTicks = DateTime.UtcNow.AddDays(-20).Ticks
            });
            await store.SaveAsync(expiredDoc);

            using var reloadedService = new ClipboardHistoryService(new ClipboardStore(tempRoot), settingsService);
            await reloadedService.IngestItemAsync(
                new ClipboardHistoryItem
                {
                    Id = "fresh-1",
                    Kind = ClipboardItemKind.Text,
                    ContentHash = "hash-fresh-1",
                    TextContent = "First fresh copy",
                    Summary = "First fresh copy"
                },
                new ClipboardObservedApp { AppName = "VS Code", AppPath = @"C:\Apps\Code.exe" });

            await reloadedService.IngestItemAsync(
                new ClipboardHistoryItem
                {
                    Id = "fresh-2",
                    Kind = ClipboardItemKind.Files,
                    ContentHash = "hash-fresh-2",
                    FilePaths = [@"C:\Docs\report.pdf"],
                    Summary = "report.pdf"
                });

            // Re-ingesting hash-fresh-1 deduplicates, bumps to top, and increments UseCount.
            ClipboardHistoryItem? deduped = await reloadedService.IngestItemAsync(
                new ClipboardHistoryItem
                {
                    Id = "fresh-1-dup",
                    Kind = ClipboardItemKind.Text,
                    ContentHash = "hash-fresh-1",
                    TextContent = "First fresh copy",
                    Summary = "First fresh copy",
                    SourceAppName = "Cursor",
                    SourceAppPath = @"C:\Apps\Cursor.exe"
                });

            Assert.NotNull(deduped);
            Assert.Equal("fresh-1", deduped!.Id);
            Assert.Equal(2, deduped.UseCount);
            Assert.Equal("Cursor", deduped.SourceAppName);
            Assert.Equal(["fresh-1", "fresh-2", "pin-1"], reloadedService.Items.Select(x => x.Id).ToArray());
            Assert.DoesNotContain(reloadedService.Items, x => x.Id == "expired-unpinned");

            ClipboardStorageStats stats = await reloadedService.GetStorageStatsAsync();
            Assert.Equal(3, stats.ActiveItemCount);
            Assert.Equal(1, stats.PinnedItemCount);
            Assert.Equal(2, stats.ObservedApps.Count);

            int cleared = await reloadedService.ClearUnpinnedAsync();
            Assert.Equal(2, cleared);
            Assert.Equal(["pin-1"], reloadedService.Items.Select(x => x.Id).ToArray());

            ClipboardStoreDocument persisted = await new ClipboardStore(tempRoot).LoadAsync();
            Assert.Single(persisted.Items);
            Assert.Equal("pin-1", persisted.Items[0].Id);
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
    public async Task ViewModel_FiltersSearchesMasksSecretsAndBuildsCapsuleSummary()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardVm", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "text-1",
                Kind = ClipboardItemKind.Text,
                SubKind = ClipboardTextSubKind.Url,
                ContentHash = "h1",
                TextContent = "https://deskbox.app/docs",
                Summary = "https://deskbox.app/docs",
                SourceAppName = "Edge",
                IsPinned = true
            });

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "img-1",
                Kind = ClipboardItemKind.Image,
                ContentHash = "h2",
                ImageFileName = "sample.png",
                ImageWidth = 800,
                ImageHeight = 600,
                ByteSize = 24576
            });

            string rawSecret = FakeToken("sk", "-proj-", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "1234567890abcd");
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "secret-1",
                Kind = ClipboardItemKind.Text,
                ContentHash = "h3",
                TextContent = rawSecret,
                Summary = rawSecret,
                IsSensitive = true
            });

            var config = new WidgetConfig { Id = "clipboard-main", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(
                config,
                service,
                settingsService,
                localization,
                uiDispatch: action => action());

            await vm.InitializeAsync();
            Assert.Equal(3, vm.VisibleItems.Count);

            // Default SecretProtectionMode == "Mask" masks the display text and capsule summary.
            Assert.True(vm.VisibleItems[0].IsMaskedSecret);
            Assert.Contains("••••••••••••", vm.VisibleItems[0].DisplayText, StringComparison.Ordinal);
            Assert.DoesNotContain("GHIJKLMNOPQRSTUVWXYZ123456", vm.VisibleItems[0].DisplayText, StringComparison.Ordinal);

            (string capsulePrimary, string capsuleSecondary) = vm.BuildCapsuleSummary(hideSensitiveContent: false);
            Assert.Contains("••••••••••••", capsulePrimary, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(capsuleSecondary));

            (string hiddenPrimary, string hiddenSecondary) = vm.BuildCapsuleSummary(hideSensitiveContent: true);
            Assert.DoesNotContain("••••", hiddenPrimary, StringComparison.Ordinal);
            Assert.Equal(string.Empty, hiddenSecondary);

            // Switching SecretProtectionMode to "None" unmasks the card preview.
            settingsService.Settings.Clipboard.ClipboardSensitiveProtectionMode = ClipboardSettingsSlice.SecretProtectionNone;
            vm.ApplyAppearanceFromSettings();
            Assert.False(vm.VisibleItems[0].IsMaskedSecret);
            Assert.Equal(rawSecret, vm.VisibleItems[0].DisplayText);

            // Category filters & search query.
            vm.ActiveFilter = ClipboardFilterKind.Images;
            Assert.Equal(["img-1"], vm.VisibleItems.Select(x => x.Id).ToArray());

            vm.ActiveFilter = ClipboardFilterKind.Pinned;
            Assert.Equal(["text-1"], vm.VisibleItems.Select(x => x.Id).ToArray());

            vm.ActiveFilter = ClipboardFilterKind.All;
            vm.SearchText = "deskbox.app";
            Assert.Equal(["text-1"], vm.VisibleItems.Select(x => x.Id).ToArray());
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
    public void TitleIconAndPrivacyPolicies_CoverClipboardWidget()
    {
        Assert.Equal(WidgetTitleIconKindNames.Clipboard, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Clipboard));
        Assert.Equal("clipboard", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Clipboard));
        Assert.Equal("Clipboard.Title", WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Clipboard));

        string svg = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Assets/WidgetTitleIcons/clipboard.svg"));
        Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", svg, StringComparison.Ordinal);

        Assert.True(WidgetCompactPrivacyPolicy.HidesSensitiveContent(true, WidgetKind.Clipboard));
    }

    [Fact]
    public void CapturePriority_PrefersTextAndRichFormatsOverCompositeSpreadsheetImages_WhilePreservingPureImageCopies()
    {
        // Priority 1: File drops always beat everything else.
        ClipboardItemKind? filesPriority = ClipboardHistoryService.ResolveCapturePriorityKind(
            hasFilesDrop: true,
            hasNonWhitespacePlainText: true,
            htmlFragment: "<table><tr><td>A1</td></tr></table>",
            rtfText: @"{\rtf1\ansi A1}",
            hasImagePayload: true,
            out _);
        Assert.Equal(ClipboardItemKind.Files, filesPriority);

        // Priority 2: Excel / WPS / PowerPoint cell copy (text + HTML + RTF + PNG/DIB bitmap) -> Text wins!
        ClipboardItemKind? spreadsheetPriority = ClipboardHistoryService.ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: true,
            htmlFragment: "Version:0.9\r\n<!--StartFragment--><table><tr><td>42</td><td>108</td></tr></table><!--EndFragment-->",
            rtfText: @"{\rtf1\ansi 42\tab 108\par}",
            hasImagePayload: true,
            out _);
        Assert.Equal(ClipboardItemKind.Text, spreadsheetPriority);

        // Rich HTML with visible text and no CF_UNICODETEXT still resolves as Text with extracted fallback text.
        ClipboardItemKind? htmlTextPriority = ClipboardHistoryService.ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: false,
            htmlFragment: "Version:0.9\r\n<html><body><!--StartFragment--><p>Quarterly &amp; Annual</p><!--EndFragment--></body></html>",
            rtfText: null,
            hasImagePayload: true,
            out string? htmlFallback);
        Assert.Equal(ClipboardItemKind.Text, htmlTextPriority);
        Assert.Equal("Quarterly & Annual", htmlFallback);

        // Rich RTF with visible text and no CF_UNICODETEXT resolves as Text.
        ClipboardItemKind? rtfTextPriority = ClipboardHistoryService.ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: false,
            htmlFragment: null,
            rtfText: @"{\rtf1\ansi{\fonttbl{\f0 Segoe UI;}}\pard Revenue\tab 2026\par}",
            hasImagePayload: true,
            out string? rtfFallback);
        Assert.Equal(ClipboardItemKind.Text, rtfTextPriority);
        Assert.Equal("Revenue\t2026", rtfFallback);

        // Browser right-click "Copy image" (HTML has only <img> tag, no visible text) -> Image wins!
        ClipboardItemKind? browserCopyImagePriority = ClipboardHistoryService.ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: false,
            htmlFragment: "Version:0.9\r\n<html><body><!--StartFragment--><img src=\"https://deskbox.app/logo.png\" alt=\"\"/><!--EndFragment--></body></html>",
            rtfText: @"{\rtf1\ansi{\pict\pngblip 89504e47}}",
            hasImagePayload: true,
            out string? emptyRichFallback);
        Assert.Equal(ClipboardItemKind.Image, browserCopyImagePriority);
        Assert.Null(emptyRichFallback);
    }

    [Fact]
    public async Task StoreAndService_ExternalizeLargeTextAndRichPayloads_MigrateLegacyInlineJson_AndCalculateColdDiskStats()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPayloads", Guid.NewGuid().ToString("N"));
        string legacyRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardLegacy", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            string largeBody = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"Line {i:D4}: {new string('A', 24)}")) + "\nTAIL_MARKER_999";
            Assert.True(largeBody.Length > ClipboardHistoryItem.InlineTextMaxChars);
            int expectedLineCount = largeBody.Count(c => c == '\n') + 1;
            string htmlBody = "<!--StartFragment--><div><b>Formatted</b> payload</div><!--EndFragment-->";
            string rtfBody = @"{\rtf1\ansi\b Formatted\b0  payload}";

            ClipboardHistoryItem? ingested = await service.IngestItemAsync(
                new ClipboardHistoryItem
                {
                    Id = "large-rich-1",
                    Kind = ClipboardItemKind.Text,
                    SubKind = ClipboardTextSubKind.Html,
                    ContentHash = "hash-large-rich-1",
                    Summary = ClipboardClassifier.BuildSummary(largeBody),
                    TextContent = largeBody,
                    HtmlContent = htmlBody,
                    RtfContent = rtfBody,
                    IsPinned = true
                },
                new ClipboardObservedApp { AppName = "Excel", AppPath = @"C:\Office\EXCEL.EXE" });
            await service.FlushAsync();

            Assert.NotNull(ingested);
            Assert.Equal(ClipboardHistoryItem.InlineTextMaxChars, ingested!.TextContent!.Length);
            Assert.Null(ingested.HtmlContent);
            Assert.Null(ingested.RtfContent);
            Assert.True(ingested.HasExternalTextPayload);
            Assert.True(ingested.HasExternalRichPayload);
            Assert.Equal(largeBody.Length, ingested.CharacterCount);
            Assert.Equal(expectedLineCount, ingested.LineCount);

            // Verify clipboard.json is compact (unindented) and free of inline multi-KB text/HTML/RTF bodies.
            string rawJson = await File.ReadAllTextAsync(Path.Combine(tempRoot, "clipboard.json"));
            Assert.DoesNotContain("\n  ", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("TAIL_MARKER_999", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("<!--StartFragment-->", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\rtf1\ansi", rawJson, StringComparison.Ordinal);

            // Verify lazy full-text, bounded preview, and rich payload reads.
            Assert.Equal(largeBody, await service.GetFullTextAsync(ingested));
            Assert.Equal(htmlBody, await service.GetHtmlPayloadAsync(ingested));
            Assert.Equal(rtfBody, await service.GetRtfPayloadAsync(ingested));

            (string preview, bool wasTruncated) = await service.GetTextPreviewAsync(ingested, 1024);
            Assert.True(wasTruncated);
            Assert.Equal(1024, preview.Length);
            Assert.Equal(largeBody[..1024], preview);

            // Verify cold disk storage stats work without loading ClipboardStoreDocument into RAM.
            ClipboardStorageStats coldDiskStats = await ClipboardStore.CalculateDiskStorageStatsAsync(tempRoot);
            Assert.Equal(1, coldDiskStats.ActiveItemCount);
            Assert.Equal(1, coldDiskStats.PinnedItemCount);
            Assert.True(coldDiskStats.TotalCacheBytes > 0);
            Assert.Single(coldDiskStats.ObservedApps);
            Assert.Equal("Excel", coldDiskStats.ObservedApps[0].AppName);

            using var coldService = new ClipboardHistoryService(new ClipboardStore(tempRoot), settingsService);
            Assert.False(coldService.IsStoreLoadedInMemory);
            ClipboardStorageStats coldServiceStats = await coldService.GetStorageStatsAsync();
            Assert.False(coldService.IsStoreLoadedInMemory);
            Assert.Equal(1, coldServiceStats.ActiveItemCount);
            Assert.Equal(coldDiskStats.TotalCacheBytes, coldServiceStats.TotalCacheBytes);

            // Deleting the item removes all orphaned payload files from clipboard/payloads/.
            Assert.Equal(3, Directory.GetFiles(service.PayloadDirectory).Length);
            Assert.True(await service.DeleteItemAsync("large-rich-1"));
            Assert.Empty(Directory.GetFiles(service.PayloadDirectory));

            // Verify 100% backward-compatible migration of legacy inline clipboard.json items on LoadAsync().
            Directory.CreateDirectory(legacyRoot);
            string legacyText = new string('L', 5000);
            string legacyJson =
                "{\"version\":1,\"items\":[{\"id\":\"legacy-1\",\"kind\":\"Text\",\"subKind\":\"Html\",\"contentHash\":\"h-legacy\"," +
                $"\"summary\":\"LLL\",\"textContent\":\"{legacyText}\",\"htmlContent\":\"<b>Legacy</b>\"}}],\"observedApps\":[]}}";
            await File.WriteAllTextAsync(Path.Combine(legacyRoot, "clipboard.json"), legacyJson);

            var legacyStore = new ClipboardStore(legacyRoot);
            ClipboardStoreDocument migratedDoc = await legacyStore.LoadAsync();
            Assert.Single(migratedDoc.Items);
            ClipboardHistoryItem migratedItem = migratedDoc.Items[0];
            Assert.Equal(5000, migratedItem.CharacterCount);
            Assert.Equal(ClipboardHistoryItem.InlineTextMaxChars, migratedItem.TextContent!.Length);
            Assert.Null(migratedItem.HtmlContent);
            Assert.True(migratedItem.HasExternalTextPayload);
            Assert.True(migratedItem.HasExternalRichPayload);
            Assert.Equal(legacyText, await legacyStore.ReadPayloadTextAsync(migratedItem.TextPayloadFileName));
            Assert.Equal("<b>Legacy</b>", await legacyStore.ReadPayloadTextAsync(migratedItem.HtmlPayloadFileName));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            if (Directory.Exists(legacyRoot))
            {
                Directory.Delete(legacyRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ViewModelAndInterop_DeferUnattachedRebuilds_GuardStaleVisualLoads_AndCacheAppNames()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardDeferred", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);

            // 1. Large multi-gap BuildSummary completes in a single bounded pass.
            string hugeGappyText = "HeaderLine\r\n\r\n\r\n\r\nSecondLine\r\n" + new string('Z', 300_000);
            string summary = ClipboardClassifier.BuildSummary(hugeGappyText);
            Assert.Equal(ClipboardClassifier.SummaryMaxChars, summary.Length);
            Assert.StartsWith("HeaderLine\nSecondLine\n", summary, StringComparison.Ordinal);

            // 2. Friendly app display name cache avoids repeated FileVersionInfo disk reads.
            ClipboardPlatformInterop.ClearFriendlyAppNameCacheForTesting();
            string dummyExePath = Path.Combine(tempRoot, "CustomToolApp.exe");
            await File.WriteAllBytesAsync(dummyExePath, [0x4D, 0x5A]);
            string firstResolved = ClipboardPlatformInterop.ResolveFriendlyAppName(dummyExePath);
            Assert.Equal("CustomToolApp", firstResolved);
            File.Delete(dummyExePath);
            string cachedResolved = ClipboardPlatformInterop.ResolveFriendlyAppName(dummyExePath, fallbackStem: "DifferentFallback");
            Assert.Equal("CustomToolApp", cachedResolved);

            // 3. Unattached ViewModel skips rebuilding VisibleItems on Ctrl+C while still updating capsule summary.
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            var config = new WidgetConfig { Id = "clipboard-hibernated", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(
                config,
                service,
                settingsService,
                localization,
                uiDispatch: static action => action());

            await vm.InitializeAsync();
            vm.SetViewAttached(false);
            Assert.Empty(vm.VisibleItems);

            int summaryNotifications = 0;
            vm.SummaryChanged += (_, _) => summaryNotifications++;

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "deferred-1",
                Kind = ClipboardItemKind.Text,
                ContentHash = "h-def-1",
                TextContent = "Copied while collapsed",
                Summary = "Copied while collapsed"
            });

            Assert.Empty(vm.VisibleItems);
            Assert.Equal(1, vm.TotalItemCount);
            Assert.True(summaryNotifications >= 1);
            (string capsuleText, _) = vm.BuildCapsuleSummary(hideSensitiveContent: false);
            Assert.Equal("Copied while collapsed", capsuleText);

            // Attaching the view rebuilds VisibleItems once.
            vm.SetViewAttached(true);
            Assert.Single(vm.VisibleItems);
            Assert.Equal("deferred-1", vm.VisibleItems[0].Id);

            // 4. Generation counter in ClipboardItemViewModel aborts in-flight visual loads when recycled.
            string fakeThumbPath = Path.Combine(tempRoot, "thumb.png");
            string fakeAppPath = Path.Combine(tempRoot, "source.exe");
            await File.WriteAllBytesAsync(fakeThumbPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(fakeAppPath, [1, 2, 3]);

            var thumbGate = new TaskCompletionSource<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int thumbLoaderCalls = 0;
            int iconLoaderCalls = 0;

            var itemVm = new ClipboardItemViewModel(
                new ClipboardHistoryItem
                {
                    Id = "img-gen-1",
                    Kind = ClipboardItemKind.Image,
                    ImageFileName = "thumb.png",
                    SourceAppName = "Source",
                    SourceAppPath = fakeAppPath
                },
                localization,
                ClipboardSettingsSlice.SecretProtectionMask,
                showSourceApp: true,
                textSize: 13,
                secondaryTextSize: 11.5,
                previewMaxLines: 2,
                cardPadding: default,
                cardMinHeight: 50,
                thumbnailPathResolver: _ => fakeThumbPath,
                iconLoader: (_, _) =>
                {
                    iconLoaderCalls++;
                    return Task.FromResult<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>(null);
                },
                thumbnailLoader: (_, _) =>
                {
                    thumbLoaderCalls++;
                    return thumbLoaderCalls == 1
                        ? thumbGate.Task
                        : Task.FromResult<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>(null);
                });

            Task inFlightLoad = itemVm.EnsureVisualAssetsLoadedAsync();
            Assert.Equal(1, thumbLoaderCalls);
            Assert.Equal(0, iconLoaderCalls);

            // Simulate container recycle while thumbnail decode is still in flight.
            itemVm.ReleaseVisualAssets();
            thumbGate.SetResult(null);
            await inFlightLoad;

            // Stale continuation must abort before reaching iconLoader.
            Assert.Equal(0, iconLoaderCalls);

            // Subsequent realization loads cleanly under the new generation.
            await itemVm.EnsureVisualAssetsLoadedAsync();
            Assert.Equal(2, thumbLoaderCalls);
            Assert.Equal(1, iconLoaderCalls);
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
    public async Task AdapterAndViewModel_KeepExpandedProjectionAndEmptyStateIntactAcrossLongHiddenMaintenance()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardLongHidden", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();

            // Seed persisted clipboard.json before creating a fresh cold service + adapter.
            using (var seedService = new ClipboardHistoryService(new ClipboardStore(tempRoot), settingsService))
            {
                await seedService.IngestItemAsync(new ClipboardHistoryItem
                {
                    Id = "persisted-1",
                    Kind = ClipboardItemKind.Text,
                    ContentHash = "hash-persisted-1",
                    TextContent = "Persisted across session",
                    Summary = "Persisted across session"
                });
                await seedService.FlushAsync();
            }

            using var service = new ClipboardHistoryService(new ClipboardStore(tempRoot), settingsService);
            var config = new WidgetConfig { Id = "clipboard-expanded", WidgetKind = WidgetKind.Clipboard };
            int viewFactoryCalls = 0;
            using var adapter = new DeskBox.Controls.WidgetContents.ClipboardWidgetContentAdapter(
                config,
                localization,
                settingsService,
                service,
                _ =>
                {
                    viewFactoryCalls++;
                    throw new InvalidOperationException("Headless InitializeAsync must not materialize XAML.");
                });

            // 1. Adapter.InitializeAsync loads clipboard.json without materializing XAML,
            // so born-hibernated capsules show accurate summaries immediately on startup.
            await adapter.InitializeAsync();
            Assert.Equal(0, viewFactoryCalls);
            Assert.False(adapter.HasMaterializedView);
            Assert.True(adapter.IsReadyForReuse);
            Assert.Equal(1, adapter.ViewModel.TotalItemCount);
            (string bootPrimary, _) = adapter.ViewModel.BuildCapsuleSummary(hideSensitiveContent: false);
            Assert.Equal("Persisted across session", bootPrimary);

            // 2. Attach expanded projection and verify initial items + visibility state.
            adapter.ViewModel.SetViewAttached(true);
            Assert.True(adapter.ViewModel.IsViewAttached);
            Assert.Single(adapter.ViewModel.VisibleItems);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, adapter.ViewModel.ListVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, adapter.ViewModel.EmptyStateVisibility);

            // 3. Long-hidden maintenance on an expanded widget must NOT detach the view projection
            // or clear VisibleItems; subsequent Ctrl+C copies must appear immediately.
            adapter.OnWindowLongHidden();
            Assert.True(adapter.ViewModel.IsViewAttached);
            Assert.Single(adapter.ViewModel.VisibleItems);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, adapter.ViewModel.ListVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, adapter.ViewModel.EmptyStateVisibility);

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "copied-after-reveal",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-copied-after-reveal",
                TextContent = "Copied right after tray restore",
                Summary = "Copied right after tray restore"
            });

            Assert.Equal(2, adapter.ViewModel.VisibleItems.Count);
            Assert.Equal("copied-after-reveal", adapter.ViewModel.VisibleItems[0].Id);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, adapter.ViewModel.ListVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, adapter.ViewModel.EmptyStateVisibility);

            // 4. Detaching the view (when collapsing/hibernating) MUST raise PropertyChanged for
            // ListVisibility and EmptyStateVisibility so empty-state UI never gets stuck Collapsed.
            var raisedProperties = new HashSet<string>(StringComparer.Ordinal);
            adapter.ViewModel.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.PropertyName))
                {
                    raisedProperties.Add(args.PropertyName);
                }
            };

            adapter.ViewModel.SetViewAttached(false);
            Assert.Empty(adapter.ViewModel.VisibleItems);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, adapter.ViewModel.ListVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, adapter.ViewModel.EmptyStateVisibility);
            Assert.Contains(nameof(ClipboardWidgetViewModel.ListVisibility), raisedProperties);
            Assert.Contains(nameof(ClipboardWidgetViewModel.EmptyStateVisibility), raisedProperties);

            // 5. Clearing all items and re-attaching shows EmptyStateVisibility == Visible.
            await service.ClearUnpinnedAsync();
            adapter.ViewModel.SetViewAttached(true);
            Assert.Empty(adapter.ViewModel.VisibleItems);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, adapter.ViewModel.ListVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, adapter.ViewModel.EmptyStateVisibility);
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
    public async Task Store_SensitiveItems_AreDpapiEncryptedAtRest_MigrateLegacyCleartext_AndDropUnreadableCiphertext()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-clipboard-dpapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new ClipboardStore(Path.Combine(tempRoot, "clipboard"));
            string inlineSecret = FakeToken("sk", "-proj-", "INLINE-SUPER-SECRET-TOKEN-998877665544");
            string spilledSecret = string.Concat(Enumerable.Repeat(FakeToken("ghp", "_", "SPILLED_SECRET_PAYLOAD_BLOCK_1234567890_"), 70));
            string secretHtml = FakeToken("<div><span>ghp", "_", "SPILLED_HTML_SECRET_9876543210</span></div>");
            Assert.True(spilledSecret.Length > ClipboardHistoryItem.InlineTextMaxChars);

            var initialDoc = new ClipboardStoreDocument
            {
                Items =
                [
                    new ClipboardHistoryItem
                    {
                        Id = "sens-inline",
                        Kind = ClipboardItemKind.Text,
                        ContentHash = "hash-sens-inline",
                        TextContent = inlineSecret,
                        Summary = inlineSecret,
                        IsSensitive = true
                    },
                    new ClipboardHistoryItem
                    {
                        Id = "sens-spilled",
                        Kind = ClipboardItemKind.Text,
                        ContentHash = "hash-sens-spilled",
                        TextContent = spilledSecret,
                        HtmlContent = secretHtml,
                        Summary = spilledSecret[..120],
                        IsSensitive = true
                    }
                ]
            };

            await store.SaveAsync(initialDoc);

            // Live in-memory items remain usable (not replaced with ciphertext).
            Assert.Equal(inlineSecret, initialDoc.Items[0].TextContent);

            // Raw JSON on disk must contain neither inline nor spilled secret, and Summary must be masked.
            string rawJson = await File.ReadAllTextAsync(store.StorePath);
            Assert.DoesNotContain(inlineSecret, rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SPILLED_SECRET_PAYLOAD_BLOCK", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SPILLED_HTML_SECRET", rawJson, StringComparison.Ordinal);
            Assert.Contains(ClipboardStore.DpapiPayloadPrefix, rawJson, StringComparison.Ordinal);
            using (var rawDoc = System.Text.Json.JsonDocument.Parse(rawJson))
            {
                string? persistedSummary = rawDoc.RootElement.GetProperty("items")[0].GetProperty("summary").GetString();
                Assert.Contains("••••", persistedSummary, StringComparison.Ordinal);
            }

            // Spilled payload files on disk must also be DPAPI-encrypted.
            string[] payloadFiles = Directory.GetFiles(store.PayloadDirectory);
            Assert.NotEmpty(payloadFiles);
            foreach (string payloadFile in payloadFiles)
            {
                string rawPayload = await File.ReadAllTextAsync(payloadFile);
                Assert.StartsWith(ClipboardStore.DpapiPayloadPrefix, rawPayload, StringComparison.Ordinal);
                Assert.DoesNotContain("SPILLED_SECRET_PAYLOAD_BLOCK", rawPayload, StringComparison.Ordinal);
                Assert.DoesNotContain("SPILLED_HTML_SECRET", rawPayload, StringComparison.Ordinal);
            }

            // Reloading decrypts inline text, preview, and spilled payloads cleanly.
            ClipboardStoreDocument reloaded = await store.LoadAsync();
            Assert.Equal(2, reloaded.Items.Count);
            ClipboardHistoryItem reloadedInline = reloaded.Items.Single(x => x.Id == "sens-inline");
            ClipboardHistoryItem reloadedSpilled = reloaded.Items.Single(x => x.Id == "sens-spilled");
            Assert.Equal(inlineSecret, reloadedInline.TextContent);

            string? hydratedSpilledText = await store.ReadPayloadTextAsync(reloadedSpilled.TextPayloadFileName);
            string? hydratedHtml = await store.ReadPayloadTextAsync(reloadedSpilled.HtmlPayloadFileName);
            (string? previewText, bool previewTruncated) = await store.ReadPayloadTextPreviewAsync(reloadedSpilled.TextPayloadFileName, 64);
            Assert.Equal(spilledSecret, hydratedSpilledText);
            Assert.Equal(secretHtml, hydratedHtml);
            Assert.True(previewTruncated);
            Assert.Equal(spilledSecret[..64], previewText);

            // Legacy cleartext sensitive item + corrupted/cross-machine DPAPI item:
            // legacy item migrates to dpapi: on LoadAsync, corrupted item is omitted gracefully.
            string legacyRoot = Path.Combine(tempRoot, "legacy-clipboard");
            Directory.CreateDirectory(legacyRoot);
            string legacySecret = FakeToken("AKIA", "IOSFODNN7EXAMPLE", "_LEGACY_CLEARTEXT");
            await File.WriteAllTextAsync(
                Path.Combine(legacyRoot, "clipboard.json"),
                $$"""
                {
                  "version": 1,
                  "items": [
                    {
                      "id": "legacy-clear",
                      "kind": "Text",
                      "contentHash": "h-legacy",
                      "textContent": "{{legacySecret}}",
                      "summary": "{{legacySecret}}",
                      "isSensitive": true
                    },
                    {
                      "id": "foreign-dpapi",
                      "kind": "Text",
                      "contentHash": "h-foreign",
                      "textContent": "dpapi:not-valid-dpapi-base64!!!",
                      "summary": "••••••••••••",
                      "isSensitive": true
                    }
                  ]
                }
                """);

            var legacyStore = new ClipboardStore(legacyRoot);
            ClipboardStoreDocument migrated = await legacyStore.LoadAsync();
            Assert.Single(migrated.Items);
            Assert.Equal("legacy-clear", migrated.Items[0].Id);
            Assert.Equal(legacySecret, migrated.Items[0].TextContent);

            string migratedRawJson = await File.ReadAllTextAsync(legacyStore.StorePath);
            Assert.DoesNotContain(legacySecret, migratedRawJson, StringComparison.Ordinal);
            Assert.Contains(ClipboardStore.DpapiPayloadPrefix, migratedRawJson, StringComparison.Ordinal);
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
    public void TryComputeDibPixelOffset_HandlesBitfieldsV5TwoBppAndRejectsInvalidHeaders()
    {
        static byte[] MakeInfoHeader(uint headerSize, int width, int height, ushort bitCount, uint compression = 0, uint clrUsed = 0)
        {
            byte[] header = new byte[Math.Max((int)headerSize, 40)];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), height);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), bitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), compression);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32, 4), clrUsed);
            return header;
        }

        // 40-byte BITMAPINFOHEADER + 32bpp BI_BITFIELDS (3 DWORD masks = 12 bytes) => 14 + 40 + 12 = 66
        byte[] bitfields40 = MakeInfoHeader(40, width: 16, height: 16, bitCount: 32, compression: 3);
        Assert.True(ClipboardPlatformInterop.TryComputeDibPixelOffset(bitfields40, fullDibLength: 40 + 12 + 1024, out uint offsetBf40));
        Assert.Equal(66u, offsetBf40);

        // 124-byte BITMAPV5HEADER with BI_BITFIELDS embeds masks inside the 124-byte header => 14 + 124 = 138
        byte[] v5Header = MakeInfoHeader(124, width: 16, height: -16, bitCount: 32, compression: 3);
        Assert.True(ClipboardPlatformInterop.TryComputeDibPixelOffset(v5Header, fullDibLength: 124 + 1024, out uint offsetV5));
        Assert.Equal(138u, offsetV5);

        // 40-byte 2bpp indexed palette (4 colors * 4 bytes = 16 bytes) => 14 + 40 + 16 = 70
        byte[] twoBpp = MakeInfoHeader(40, width: 8, height: 8, bitCount: 2);
        Assert.True(ClipboardPlatformInterop.TryComputeDibPixelOffset(twoBpp, fullDibLength: 40 + 16 + 64, out uint offset2Bpp));
        Assert.Equal(70u, offset2Bpp);

        // Zero width / zero height / invalid bitCount / overflowing clrUsed must fail closed.
        byte[] zeroWidth = MakeInfoHeader(40, width: 0, height: 8, bitCount: 32);
        Assert.False(ClipboardPlatformInterop.TryComputeDibPixelOffset(zeroWidth, fullDibLength: 256, out _));

        byte[] zeroHeight = MakeInfoHeader(40, width: 8, height: 0, bitCount: 32);
        Assert.False(ClipboardPlatformInterop.TryComputeDibPixelOffset(zeroHeight, fullDibLength: 256, out _));

        byte[] invalidBpp = MakeInfoHeader(40, width: 8, height: 8, bitCount: 15);
        Assert.False(ClipboardPlatformInterop.TryComputeDibPixelOffset(invalidBpp, fullDibLength: 256, out _));

        byte[] hugeClrUsed = MakeInfoHeader(40, width: 8, height: 8, bitCount: 8, clrUsed: 0x4000_0001u);
        Assert.False(ClipboardPlatformInterop.TryComputeDibPixelOffset(hugeClrUsed, fullDibLength: 4096, out _));
    }
}
