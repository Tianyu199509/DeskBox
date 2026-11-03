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

    [Fact]
    public void ClassifierAndAppNormalization_DetectsCodeSnippetsExtractsUrlTitlesAndCleansAppNames()
    {
        ClipboardClassifier.ClassifyText(
            "{\n  \"name\": \"DeskBox\",\n  \"version\": 2\n}",
            null,
            null,
            out ClipboardTextSubKind jsonKind,
            out _,
            out string? jsonLang);
        Assert.Equal(ClipboardTextSubKind.Code, jsonKind);
        Assert.Equal("JSON", jsonLang);

        ClipboardClassifier.ClassifyText(
            "SELECT id, created_at FROM clipboard_items WHERE is_pinned = 1 ORDER BY last_used_at DESC;",
            null,
            null,
            out ClipboardTextSubKind sqlKind,
            out _,
            out string? sqlLang);
        Assert.Equal(ClipboardTextSubKind.Code, sqlKind);
        Assert.Equal("SQL", sqlLang);

        ClipboardClassifier.ClassifyText(
            "dotnet test .\\tests\\DeskBox.Tests\\DeskBox.Tests.csproj --no-restore -p:Platform=x64",
            null,
            null,
            out ClipboardTextSubKind shellKind,
            out _,
            out string? shellLang);
        Assert.Equal(ClipboardTextSubKind.Code, shellKind);
        Assert.Equal("Shell", shellLang);

        ClipboardClassifier.ClassifyText(
            "public sealed class SampleWidget\n{\n    private readonly int _count;\n    public int Count => _count;\n}",
            null,
            null,
            out ClipboardTextSubKind csharpKind,
            out _,
            out string? csharpLang);
        Assert.Equal(ClipboardTextSubKind.Code, csharpKind);
        Assert.Equal("C#", csharpLang);

        // Normal multi-line prose must stay Plain rather than Code.
        ClipboardClassifier.ClassifyText(
            "有一些细节需要增强优化，你帮我梳理一下怎么做更合理\n1、输入框的选中态边框有点奇怪",
            null,
            null,
            out ClipboardTextSubKind proseKind,
            out _,
            out string? proseLang);
        Assert.Equal(ClipboardTextSubKind.Plain, proseKind);
        Assert.Null(proseLang);

        Assert.True(ClipboardClassifier.TryExtractHtmlAnchorTitle(
            "<!--StartFragment--><a href=\"https://github.com/3899/EcoPaste-Pro\">EcoPaste-Pro &amp; Cross-Platform Clipboard</a><!--EndFragment-->",
            "https://github.com/3899/EcoPaste-Pro",
            out string? anchorTitle));
        Assert.Equal("EcoPaste-Pro & Cross-Platform Clipboard", anchorTitle);

        Assert.True(ClipboardClassifier.TryExtractHtmlDocumentTitle(
            "<html><head><title>  Paste &#x2014; Endless Clipboard  </title></head><body></body></html>",
            out string? docTitle));
        Assert.Equal("Paste — Endless Clipboard", docTitle);

        Assert.Equal("github.com/3899/EcoPaste-Pro", ClipboardClassifier.FormatReadableUrl("https://www.github.com/3899/EcoPaste-Pro/"));
        Assert.Equal("Notepad", ClipboardClassifier.NormalizeFriendlyAppDisplayName("Notepad.exe"));
        Assert.Equal("DeskBox", ClipboardClassifier.NormalizeFriendlyAppDisplayName("DeskBox.Settings"));
        Assert.Equal("DeskBox", ClipboardClassifier.NormalizeFriendlyAppDisplayName("DeskBox.ThumbnailProxy.exe"));

        Assert.True(ClipboardClassifier.IsGeneratedOrChatCacheImagePath(
            @"C:\Users\simon\Documents\WeChat Files\wxid_123\FileStorage\MsgAttach\2026-11\e1934c2cd75f0d27c948492a208a393.png"));
        Assert.False(ClipboardClassifier.IsGeneratedOrChatCacheImagePath(
            @"D:\Design\HeroBanner.png"));
    }

    [Fact]
    public async Task ViewModelAndImageProbing_LinksFilterWeChatSingleImageHeroCardAndQuickSlotsWork()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardPolymorphic", Guid.NewGuid().ToString("N"));
        try
        {
            string wechatDir = Path.Combine(tempRoot, "WeChat Files", "MsgAttach", "2026-11");
            string designDir = Path.Combine(tempRoot, "Design");
            Directory.CreateDirectory(wechatDir);
            Directory.CreateDirectory(designDir);

            // Minimal valid PNG header with IHDR width=691, height=241
            byte[] pngHeader =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x02, 0xB3, // 691
                0x00, 0x00, 0x00, 0xF1, // 241
                0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            ];

            string wechatPng = Path.Combine(wechatDir, "e1934c2cd75f0d27c948492a208a393.png");
            string namedPng = Path.Combine(designDir, "ArchitectureDiagram.png");
            await File.WriteAllBytesAsync(wechatPng, pngHeader);
            await File.WriteAllBytesAsync(namedPng, pngHeader);

            Assert.True(ClipboardPlatformInterop.TryProbeImageFileDimensions(wechatPng, out int w, out int h));
            Assert.Equal(691, w);
            Assert.Equal(241, h);

            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            using var service = new ClipboardHistoryService(new ClipboardStore(Path.Combine(tempRoot, "store")), settingsService);

            await service.ImportDroppedFilesAsync([wechatPng]);
            await service.ImportDroppedFilesAsync([namedPng]);
            await service.ImportDroppedTextAsync("https://github.com/3899/EcoPaste-Pro");
            await service.ImportDroppedTextAsync("#3b82f6");
            await service.ImportDroppedTextAsync("{\n  \"mode\": \"PasteOnSingle\",\n  \"maxItems\": 500\n}");

            // Simulate SourceAppName on the WeChat item to verify clean metadata without "2026-11" or MD5 noise
            ClipboardHistoryItem wechatModel = service.Items.Single(x => x.FilePaths.Contains(wechatPng));
            wechatModel.SourceAppName = "Weixin.exe";

            var config = new WidgetConfig { Id = "clip-poly", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization);
            await vm.InitializeAsync();

            Assert.Equal(5, vm.VisibleItems.Count);
            Assert.Equal("#1", vm.VisibleItems[0].QuickSlotText);
            Assert.Equal("#5", vm.VisibleItems[4].QuickSlotText);
            Assert.True(vm.VisibleItems[0].IsCurrentClipboard);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.VisibleItems[0].CodeBodyVisibility);

            ClipboardItemViewModel colorVm = vm.VisibleItems.Single(x => x.TextSubKind == ClipboardTextSubKind.Color);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, colorVm.ColorBodyVisibility);
            Assert.Contains("RGB(59, 130, 246)", colorVm.ColorSecondaryText, StringComparison.Ordinal);

            // Links filter isolates URL items
            vm.ActiveFilter = ClipboardFilterKind.Links;
            Assert.Single(vm.VisibleItems);
            Assert.Equal(ClipboardTextSubKind.Url, vm.VisibleItems[0].TextSubKind);

            // Images filter includes single previewable image files (both WeChat cache and named PNG)
            vm.ActiveFilter = ClipboardFilterKind.Images;
            Assert.Equal(2, vm.VisibleItems.Count);

            ClipboardItemViewModel wechatVm = vm.VisibleItems.Single(x => x.Model.FilePaths.Contains(wechatPng));
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, wechatVm.HeroImageVisibility);
            Assert.Equal(string.Empty, wechatVm.ImageFileCaptionText);
            Assert.StartsWith("Weixin · 691 × 241", wechatVm.MetaText, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-11", wechatVm.MetaText, StringComparison.Ordinal);

            ClipboardItemViewModel namedVm = vm.VisibleItems.Single(x => x.Model.FilePaths.Contains(namedPng));
            Assert.Equal("ArchitectureDiagram.png", namedVm.ImageFileCaptionText);

            // Horizontally scrollable Fluent 2 filter strip keeps icons and full labels visible across all widths
            vm.UpdateResponsiveFilterChrome(240);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterLabelVisibility);

            vm.UpdateResponsiveFilterChrome(360);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterLabelVisibility);

            vm.UpdateResponsiveFilterChrome(480);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterLabelVisibility);
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
    public async Task NotesAndInlineTextEdits_PersistAcrossReloadAndDpapiSensitiveItems_AndMatchSearch()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardNotesAndEdits", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            string storeRoot = Path.Combine(tempRoot, "store");

            string secretToken = FakeToken("ghp_", "1234567890abcdefghijklmnopqrstuvwxyzABCD");
            using (var service = new ClipboardHistoryService(new ClipboardStore(storeRoot), settingsService))
            {
                await service.ImportDroppedTextAsync("initial plain draft");
                await service.IngestItemAsync(new ClipboardHistoryItem
                {
                    Id = "secret-with-note",
                    Kind = ClipboardItemKind.Text,
                    SubKind = ClipboardTextSubKind.Plain,
                    ContentHash = "hash-secret-note",
                    TextContent = secretToken,
                    Summary = secretToken,
                    IsSensitive = true
                });

                var config = new WidgetConfig { Id = "clip-notes", WidgetKind = WidgetKind.Clipboard };
                using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization);
                await vm.InitializeAsync();

                ClipboardItemViewModel plainVm = vm.VisibleItems.Single(x => x.Id != "secret-with-note");
                ClipboardItemViewModel secretVm = vm.VisibleItems.Single(x => x.Id == "secret-with-note");

                Assert.True(await vm.UpdateItemNoteAsync(secretVm, "GitHub 发布令牌"));
                Assert.True(await vm.UpdateItemNoteAsync(plainVm, "常用配置模板"));
                Assert.True(await vm.UpdateItemTextAsync(plainVm, "{\n  \"env\": \"prod\"\n}", copyAfterSave: false));

                // Search by user note alias finds the item even when the body text does not contain the alias
                vm.SearchText = "发布令牌";
                Assert.Single(vm.VisibleItems);
                Assert.Equal("secret-with-note", vm.VisibleItems[0].Id);

                vm.SearchText = "JSON";
                Assert.Single(vm.VisibleItems);
                Assert.Equal(ClipboardTextSubKind.Code, vm.VisibleItems[0].TextSubKind);

                await service.FlushAsync();
            }

            // Verify DPAPI-encrypted store preserves Note and updated Code classification across reload
            var reloadStore = new ClipboardStore(storeRoot);
            string rawDiskJson = await File.ReadAllTextAsync(reloadStore.StorePath);
            Assert.DoesNotContain(secretToken, rawDiskJson, StringComparison.Ordinal);

            ClipboardStoreDocument reloaded = await reloadStore.LoadAsync();
            ClipboardHistoryItem reloadedSecret = reloaded.Items.Single(x => x.Id == "secret-with-note");
            Assert.Equal("GitHub 发布令牌", reloadedSecret.Note);
            Assert.Equal(secretToken, reloadedSecret.TextContent);

            ClipboardHistoryItem reloadedEdited = reloaded.Items.Single(x => x.Id != "secret-with-note");
            Assert.Equal("常用配置模板", reloadedEdited.Note);
            Assert.Equal(ClipboardTextSubKind.Code, reloadedEdited.SubKind);
            Assert.Equal("JSON", reloadedEdited.CodeLanguage);
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
    public async Task ReuseItem_DoesNotMoveToTopByDefault_AndInternalCacheDropIsRejected()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardReuseAndExportGuard", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService();
            Assert.False(settingsService.Settings.Clipboard.ClipboardMoveToTopOnReuse);

            // Schema v15 -> v16 migration flips legacy default true -> false
            var legacyV15 = new AppSettings { SchemaVersion = 15 };
            legacyV15.Clipboard.ClipboardMoveToTopOnReuse = true;
            var (migrated, applied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(legacyV15);
            Assert.True(applied);
            Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.False(migrated.Clipboard.ClipboardMoveToTopOnReuse);

            using var service = new ClipboardHistoryService(new ClipboardStore(Path.Combine(tempRoot, "store")), settingsService);
            long fixedTicks = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "older",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-older",
                TextContent = "older entry",
                Summary = "older entry",
                CreatedAtUtcTicks = fixedTicks,
                UpdatedAtUtcTicks = fixedTicks
            });
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "newer",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-newer",
                TextContent = "newer entry",
                Summary = "newer entry"
            });

            Assert.Equal(["newer", "older"], service.Items.Select(static x => x.Id).ToArray());
            long ingestedOlderTicks = service.Items[1].UpdatedAtUtcTicks;

            // Clicking/reusing "older" inside the widget keeps it in slot #2 without mutating its timestamp
            await Task.Delay(5);
            await service.RecordItemReuseForTestingAsync("older", forceNotifyChanged: true);
            Assert.Equal(["newer", "older"], service.Items.Select(static x => x.Id).ToArray());
            ClipboardHistoryItem reusedOlder = service.Items[1];
            Assert.Equal(2, reusedOlder.UseCount);
            Assert.Equal(ingestedOlderTicks, reusedOlder.UpdatedAtUtcTicks);

            // Internal transient exported PNGs must never be self-captured as external CF_HDROP file items
            string internalExportPng = Path.Combine(service.ExportDirectory, "DeskBox_Clip_Test.png");
            string externalUserFile = Path.Combine(tempRoot, "UserDoc.pdf");
            Assert.True(service.IsInternalClipboardCacheDrop([internalExportPng]));
            Assert.False(service.IsInternalClipboardCacheDrop([externalUserFile]));
            Assert.False(service.IsInternalClipboardCacheDrop([internalExportPng, externalUserFile]));
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
    public async Task CompactViewMode_HidesHeaderMetadata_AndShrinksImagesToSingleLineRowHeight()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardCompactMode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            using var service = new ClipboardHistoryService(new ClipboardStore(Path.Combine(tempRoot, "store")), settingsService);

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "txt-1",
                Kind = ClipboardItemKind.Text,
                SubKind = ClipboardTextSubKind.Plain,
                ContentHash = "hash-txt-1",
                TextContent = "First line\nSecond line",
                Summary = "First line\nSecond line",
                SourceAppName = "Google Chrome"
            });
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "img-1",
                Kind = ClipboardItemKind.Image,
                ContentHash = "hash-img-1",
                ImageFileName = "sample.png",
                ImageWidth = 287,
                ImageHeight = 378,
                SourceAppName = "PixPin"
            });

            var config = new WidgetConfig { Id = "clip-compact", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization);
            await vm.InitializeAsync();

            Assert.False(vm.IsCompactViewMode);
            ClipboardItemViewModel imgVm = vm.VisibleItems.Single(static x => x.Id == "img-1");
            ClipboardItemViewModel txtVm = vm.VisibleItems.Single(static x => x.Id == "txt-1");
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, imgVm.HeaderRowVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, imgVm.HeroImageVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, imgVm.CompactImageVisibility);

            // Switch to Compact mode: header hidden, hero banner collapsed, inline compact image visible, identical row height
            vm.ToggleViewMode();
            Assert.True(vm.IsCompactViewMode);
            Assert.Equal(ClipboardSettingsSlice.ViewModeCompact, settingsService.Settings.Clipboard.ClipboardViewMode);

            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, imgVm.HeaderRowVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, imgVm.HeroImageVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, imgVm.CompactImageVisibility);
            Assert.Contains("287 × 378", imgVm.CompactImageText, StringComparison.Ordinal);

            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, txtVm.HeaderRowVisibility);
            Assert.Equal(1, txtVm.PreviewMaxLines);
            Assert.Equal(Microsoft.UI.Xaml.TextWrapping.NoWrap, txtVm.BodyTextWrapping);
            Assert.Equal("First line Second line", txtVm.DisplayText);
            Assert.Equal(txtVm.CardMinHeight, imgVm.CardMinHeight);
            Assert.Equal(txtVm.CardPadding, imgVm.CardPadding);
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
    public void ScrollableFluentFilterStrip_AndQuickCaptureTerminology_AreConfiguredCleanly()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml"));
        Assert.Contains("x:Name=\"FilterScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FilterScrollViewer_PointerWheelChanged", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE8D2;", xaml, StringComparison.Ordinal);

        string zhCn = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Strings/zh-CN.json"));
        Assert.Contains("\"Clipboard.Action.SaveToQuickCapture\": \"转存到随记\"", zhCn, StringComparison.Ordinal);
        Assert.Contains("\"Clipboard.Feedback.SavedToQuickCapture\": \"已转存到随记\"", zhCn, StringComparison.Ordinal);

        string zhTw = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Strings/zh-TW.json"));
        Assert.Contains("\"Clipboard.Action.SaveToQuickCapture\": \"轉存至隨記\"", zhTw, StringComparison.Ordinal);
        Assert.Contains("\"Clipboard.Feedback.SavedToQuickCapture\": \"已轉存至隨記\"", zhTw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phase2A_OcrLineJoining_SearchIndexing_AndSensitiveAtRestEncryption_WorkEndToEnd()
    {
        // 1. CJK words join without extra spaces, while Latin words retain spaces.
        string joined = ClipboardOcrService.JoinOcrLines(
        [
            ["剪", "贴", "板", "OCR", "Test", "2026"],
            ["第", "二", "行"]
        ]);
        Assert.Equal($"剪贴板OCR Test 2026{Environment.NewLine}第二行", joined);

        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardOcr", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "img-ocr-1",
                Kind = ClipboardItemKind.Image,
                ContentHash = "hash-ocr-1",
                ImageFileName = "invoice.png",
                ImageWidth = 640,
                ImageHeight = 360,
                OcrText = "发票代码 044031900111 深圳电子普通发票"
            });

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "img-ocr-secret",
                Kind = ClipboardItemKind.Image,
                ContentHash = "hash-ocr-secret",
                ImageFileName = "secret.png",
                IsSensitive = true,
                OcrText = "sk-live-secret-ocr-token-9988"
            });
            await service.FlushAsync();

            // 2. Sensitive OCR text must be DPAPI-encrypted on disk, never stored in plaintext.
            string rawJson = await File.ReadAllTextAsync(Path.Combine(tempRoot, "clipboard.json"));
            Assert.DoesNotContain("sk-live-secret-ocr-token-9988", rawJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:", rawJson, StringComparison.Ordinal);

            // Reload from disk and verify hydration restores plaintext OcrText in memory.
            var reloadedStore = new ClipboardStore(tempRoot);
            ClipboardStoreDocument reloaded = await reloadedStore.LoadAsync();
            Assert.Equal("sk-live-secret-ocr-token-9988", reloaded.Items.Single(x => x.Id == "img-ocr-secret").OcrText);

            // 3. ClipboardWidgetViewModel search indexes OcrText on Image cards.
            var config = new WidgetConfig { Id = "clip-ocr", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization, uiDispatch: static a => a());
            await vm.InitializeAsync();

            vm.SearchText = "044031900111";
            Assert.Single(vm.VisibleItems);
            Assert.Equal("img-ocr-1", vm.VisibleItems[0].Id);
            Assert.True(vm.VisibleItems[0].HasCachedOcr);
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
    public async Task Phase2B_MultiSelect_OrderBadges_MergeFormatting_AndBatchActions_WorkCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardMulti", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            long baseTicks = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc).Ticks;
            for (int i = 1; i <= 4; i++)
            {
                long itemTicks = baseTicks + TimeSpan.FromSeconds(i).Ticks;
                await service.IngestItemAsync(new ClipboardHistoryItem
                {
                    Id = $"item-{i}",
                    Kind = ClipboardItemKind.Text,
                    ContentHash = $"hash-{i}",
                    TextContent = $"Line {i}",
                    Summary = $"Line {i}",
                    CreatedAtUtcTicks = itemTicks,
                    UpdatedAtUtcTicks = itemTicks
                });
            }

            var config = new WidgetConfig { Id = "clip-multi", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization, uiDispatch: static a => a());
            await vm.InitializeAsync();
            // Visual order is item-4, item-3, item-2, item-1 (newest first).
            Assert.Equal(["item-4", "item-3", "item-2", "item-1"], vm.VisibleItems.Select(x => x.Id).ToArray());

            // Ctrl+Click item-2 (#1) then item-4 (#2): merge preserves exact user selection order!
            vm.ToggleMultiSelectItem(vm.VisibleItems[2]); // item-2 -> badge "1"
            vm.ToggleMultiSelectItem(vm.VisibleItems[0]); // item-4 -> badge "2"
            Assert.True(vm.IsMultiSelectMode);
            Assert.Equal(2, vm.MultiSelectedCount);
            Assert.Equal("1", vm.VisibleItems[2].MultiSelectBadgeText);
            Assert.Equal("2", vm.VisibleItems[0].MultiSelectBadgeText);

            string mergedInSelectionOrder = await vm.BuildMergedTextForSelectedAsync(" | ");
            Assert.Equal("Line 2 | Line 4", mergedInSelectionOrder);

            // Batch pin selected items
            int pinnedCount = await vm.BatchPinSelectedAsync();
            Assert.Equal(2, pinnedCount);
            Assert.True(service.Items.Single(x => x.Id == "item-2").IsPinned);
            Assert.True(service.Items.Single(x => x.Id == "item-4").IsPinned);

            // Batch delete selected items exits multi-select and leaves only item-3 & item-1
            int deletedCount = await vm.BatchDeleteSelectedAsync();
            Assert.Equal(2, deletedCount);
            Assert.False(vm.IsMultiSelectMode);
            Assert.Equal(["item-3", "item-1"], vm.VisibleItems.Select(x => x.Id).ToArray());
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
    public async Task Phase3A_PasteQueueFifo_LiveCapture_AndSkipAdvance_WorkCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardQueue", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            long baseTicks = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc).Ticks;
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "q-1",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hq-1",
                TextContent = "First queued",
                Summary = "First queued",
                CreatedAtUtcTicks = baseTicks,
                UpdatedAtUtcTicks = baseTicks
            });
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "q-2",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hq-2",
                TextContent = "Second queued",
                Summary = "Second queued",
                CreatedAtUtcTicks = baseTicks + TimeSpan.FromSeconds(1).Ticks,
                UpdatedAtUtcTicks = baseTicks + TimeSpan.FromSeconds(1).Ticks
            });

            var config = new WidgetConfig { Id = "clip-queue", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization, uiDispatch: static a => a());
            await vm.InitializeAsync();

            int added = await service.EnqueuePasteQueueAsync(["q-1", "q-2"], enableLiveCapture: true);
            Assert.Equal(2, added);
            Assert.True(vm.IsPasteQueueActive);
            Assert.Equal(2, vm.PasteQueueCount);
            Assert.Contains("First queued", vm.PasteQueueHeadPreview, StringComparison.Ordinal);
            Assert.Equal("#1", vm.VisibleItems.Single(x => x.Id == "q-1").PasteQueueBadgeText);
            Assert.Equal("#2", vm.VisibleItems.Single(x => x.Id == "q-2").PasteQueueBadgeText);

            // Live capture while queue is active appends new external copies to FIFO tail (#3)
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "q-3",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hq-3",
                TextContent = "Third live-captured",
                Summary = "Third live-captured",
                CreatedAtUtcTicks = baseTicks + TimeSpan.FromSeconds(2).Ticks,
                UpdatedAtUtcTicks = baseTicks + TimeSpan.FromSeconds(2).Ticks
            });
            Assert.Equal(3, vm.PasteQueueCount);
            Assert.Equal("#3", vm.VisibleItems.Single(x => x.Id == "q-3").PasteQueueBadgeText);

            // Advance FIFO head: q-1 -> q-2 -> q-3 -> empty
            Assert.True(await service.SkipPasteQueueHeadAsync());
            Assert.Equal(2, vm.PasteQueueCount);
            Assert.Contains("Second queued", vm.PasteQueueHeadPreview, StringComparison.Ordinal);

            service.StopPasteQueue();
            Assert.False(vm.IsPasteQueueActive);
            Assert.Equal(0, vm.PasteQueueCount);
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
    public void Phase3B_ClipboardTextTransformService_CleansUrls_FormatsJson_AndMergesPdfLines()
    {
        // 1. Clean tracking URLs while preserving core functional parameters (e.g., WeChat __biz/mid/idx/sn).
        string dirtyWeChatUrl = "https://mp.weixin.qq.com/s?__biz=MzA4&mid=2650&idx=1&sn=abcd1234&chksm=998877&mpshare=1&scene=23&utm_source=timeline#rd";
        Assert.True(ClipboardTextTransformService.TryCleanTrackingUrl(dirtyWeChatUrl, out string cleanedWeChat));
        Assert.Equal("https://mp.weixin.qq.com/s?__biz=MzA4&mid=2650&idx=1&sn=abcd1234#rd", cleanedWeChat);

        string dirtyBilibiliUrl = "https://www.bilibili.com/video/BV1xx411c7mD?p=2&spm_id_from=333.1007&vd_source=deadbeef";
        Assert.True(ClipboardTextTransformService.TryCleanTrackingUrl(dirtyBilibiliUrl, out string cleanedBili));
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD?p=2", cleanedBili);

        Assert.False(ClipboardTextTransformService.TryCleanTrackingUrl("https://deskbox.fun/docs?page=1", out _));

        // 2. NativeAOT-safe JSON formatting & minifying preserves unescaped CJK characters.
        string rawJson = "{\"title\":\"随记\",\"items\":[1,2]}";
        Assert.True(ClipboardTextTransformService.TryFormatJson(rawJson, indented: true, out string prettyJson));
        Assert.Contains("\"title\": \"随记\"", prettyJson, StringComparison.Ordinal);
        Assert.Contains("\n", prettyJson, StringComparison.Ordinal);

        Assert.True(ClipboardTextTransformService.TryFormatJson(prettyJson, indented: false, out string minifiedJson));
        Assert.Equal(rawJson, minifiedJson);
        Assert.False(ClipboardTextTransformService.TryFormatJson("{not-json", indented: true, out _));

        // 3. PDF line-break repair handles CJK (no space), Latin hyphenation, and paragraph preservation.
        string pdfSample = "人工智\r\n能时代已经到来。\r\n\r\nThis is an inter-\r\nnational stand-\r\nard test\r\ncase.";
        string mergedPdf = ClipboardTextTransformService.MergePdfLineBreaks(pdfSample);
        Assert.Equal("人工智能时代已经到来。\n\nThis is an international standard test case.", mergedPdf);
    }

    [Fact]
    public void Phase4_ChordStateMachine_PopupClamping_AndSchemaV17Migration_BehaveDeterministically()
    {
        // 1. Double Ctrl+C state machine: triggers within 60..480ms, rejects auto-repeat & synthetic injections.
        var sm = new ClipboardKeyboardChordStateMachine();
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkLControl, isKeyDown: true, isInjected: false, 1000, observeDoubleCopy: true, observeExternalPaste: true));
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: true, isInjected: false, 1010, observeDoubleCopy: true, observeExternalPaste: true));
        // Auto-repeat while C is still held must NOT count as second press:
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: true, isInjected: false, 1150, observeDoubleCopy: true, observeExternalPaste: true));
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: false, isInjected: false, 1180, observeDoubleCopy: true, observeExternalPaste: true));
        // Synthetic injected C press must be ignored:
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: true, isInjected: true, 1240, observeDoubleCopy: true, observeExternalPaste: true));
        // Genuine second physical C press at +250ms triggers DoubleCopyTriggered:
        Assert.Equal(ClipboardChordObservation.DoubleCopyTriggered, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: true, isInjected: false, 1260, observeDoubleCopy: true, observeExternalPaste: true));
        Assert.Equal(ClipboardChordObservation.None, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkC, isKeyDown: false, isInjected: false, 1300, observeDoubleCopy: true, observeExternalPaste: true));

        // External Ctrl+V paste observation for Paste Queue advancement:
        Assert.Equal(ClipboardChordObservation.ExternalPasteTriggered, sm.ProcessKeyEvent(ClipboardKeyboardChordStateMachine.VkV, isKeyDown: true, isInjected: false, 1500, observeDoubleCopy: true, observeExternalPaste: true));

        // 2. Quick-Paste popup clamping flips above caret near screen bottom and clamps horizontal overflow.
        var workArea = new DeskBox.Platform.Win32Helper.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var nearBottomRightCaret = new DeskBox.Platform.Win32Helper.POINT { X = 1820, Y = 920 };
        var mousePt = new DeskBox.Platform.Win32Helper.POINT { X = 500, Y = 500 };

        var pos = ClipboardPlatformInterop.ComputeClampedPopupPosition(
            ClipboardSettingsSlice.PopupFollowCaret,
            nearBottomRightCaret,
            mousePt,
            workArea,
            popupWidthPx: 368,
            popupHeightPx: 448,
            marginPx: 12,
            caretLineHeightPx: 26);

        Assert.Equal(1920 - 12 - 368, pos.X);
        Assert.True(pos.Y < nearBottomRightCaret.Y, "Popup should flip above the caret when below-caret placement would overflow workArea.Bottom.");

        // 3. Migration 16 -> 17 populates Quick-Paste hotkey & Double-Copy defaults cleanly.
        var legacyV16 = new AppSettings { SchemaVersion = 16 };
        legacyV16.Clipboard.ClipboardViewMode = "Compact";
        legacyV16.Clipboard.ClipboardHotkeyModifiers = 0;
        legacyV16.Clipboard.ClipboardHotkeyKey = 0;
        legacyV16.Clipboard.ClipboardPopupPlacementMode = "";
        legacyV16.Clipboard.ClipboardDoubleCopyAction = "";
        var (migrated, wasMigrated) = new SettingsMigrationPipeline().RunMigrationsOnCopy(legacyV16);
        Assert.True(wasMigrated);
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal("Compact", migrated.Clipboard.ClipboardViewMode);
        Assert.True(migrated.Clipboard.ClipboardHotkeyEnabled);
        Assert.Equal(HotkeyActivationKind.Chord, migrated.Clipboard.ClipboardHotkeyActivationKind);
        Assert.Equal((int)HotkeyModifierKeys.Alt, migrated.Clipboard.ClipboardHotkeyModifiers);
        Assert.Equal(ClipboardSettingsSlice.DefaultHotkeyKey, migrated.Clipboard.ClipboardHotkeyKey);
        Assert.Equal(ClipboardSettingsSlice.PopupFollowCaret, migrated.Clipboard.ClipboardPopupPlacementMode);
        Assert.Equal(ClipboardSettingsSlice.DoubleCopyNone, migrated.Clipboard.ClipboardDoubleCopyAction);
    }

    [Fact]
    public void ReviewFixes_UrlCleanWeChatPocToken_MultiLineJsonArray_OemHotkey_AndGuiCaretFilter()
    {
        // 1. WeChat poc_token parameter (without leading space) is stripped cleanly.
        string wechatWithPocToken = "https://mp.weixin.qq.com/s?__biz=MzA4&mid=2650&idx=1&sn=abcd1234&poc_token=HBa9zWajX&scene=21";
        Assert.True(ClipboardTextTransformService.TryCleanTrackingUrl(wechatWithPocToken, out string cleanedWeChat));
        Assert.Equal("https://mp.weixin.qq.com/s?__biz=MzA4&mid=2650&idx=1&sn=abcd1234", cleanedWeChat);

        // 2. Multi-line pretty-printed JSON array classifies as Code + JSON at capture time.
        string prettyJsonArray = "[\n  {\n    \"id\": 1,\n    \"name\": \"DeskBox\"\n  }\n]";
        ClipboardClassifier.ClassifyText(
            prettyJsonArray,
            htmlContent: null,
            rtfContent: null,
            out ClipboardTextSubKind subKind,
            out _,
            out string? codeLanguage);
        Assert.Equal(ClipboardTextSubKind.Code, subKind);
        Assert.Equal("JSON", codeLanguage);

        // 3. Alt + ` (VK_OEM_3 = 0xC0) validates and formats properly, while bare ` without modifiers is rejected.
        var altBacktick = new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, 0xC0);
        Assert.True(GlobalHotkeyService.IsValidGesture(altBacktick));
        Assert.Equal("`", GlobalHotkeyService.FormatVirtualKey(0xC0));
        Assert.False(GlobalHotkeyService.IsValidGesture(new GlobalHotkeyGesture(HotkeyModifierKeys.None, 0xC0)));

        // 4. Parked non-blinking (0, 0) dummy carets are rejected; blinking (0, 0) and non-origin carets are accepted.
        var originRect = new DeskBox.Platform.Win32Helper.RECT { Left = 0, Top = 0, Right = 1, Bottom = 18 };
        Assert.False(ClipboardPlatformInterop.IsMeaningfulGuiCaret(guiFlags: 0, originRect));
        Assert.True(ClipboardPlatformInterop.IsMeaningfulGuiCaret(guiFlags: 0x00000001, originRect));
        var activeEditorRect = new DeskBox.Platform.Win32Helper.RECT { Left = 42, Top = 120, Right = 43, Bottom = 140 };
        Assert.True(ClipboardPlatformInterop.IsMeaningfulGuiCaret(guiFlags: 0, activeEditorRect));
        var tinyRect = new DeskBox.Platform.Win32Helper.RECT { Left = 10, Top = 10, Right = 11, Bottom = 12 };
        Assert.False(ClipboardPlatformInterop.IsMeaningfulGuiCaret(guiFlags: 0x00000001, tinyRect));

        // 5. ClipboardHotkeyService stays disabled/unregistered when WidgetKind.Clipboard feature is disabled.
        var settingsService = new SettingsService();
        FeatureWidgetSettings.SetEnabled(settingsService.Settings, WidgetKind.Clipboard, false);
        settingsService.Settings.Clipboard.ClipboardHotkeyEnabled = true;
        var localization = TestServices.CreateLocalizationService();
        using var hotkeyService = new ClipboardHotkeyService(settingsService, localization, static () => Task.CompletedTask);
        hotkeyService.RefreshRegistration();
        Assert.False(hotkeyService.IsRegistered);
        Assert.False(((IHookRegistrationMaintenanceTarget)hotkeyService).RegistrationMaintenanceWanted);
    }

    [Fact]
    public async Task ReviewFixes_LiveQueueDuplicates_DoubleCopyCorrelation_ExternalizedJson_AndColdDiskClear()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "ClipboardReviewFixes", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService();
            FeatureWidgetSettings.SetEnabled(settingsService.Settings, WidgetKind.Clipboard, true);
            settingsService.Settings.Clipboard.ClipboardViewMode = ClipboardSettingsSlice.ViewModeCompact;
            var localization = TestServices.CreateLocalizationService();
            var store = new ClipboardStore(tempRoot);
            using var service = new ClipboardHistoryService(store, settingsService);

            // 1. Live Paste Queue preserves duplicate external captures ("Yes", "No", "Yes" -> length 3).
            service.StartLivePasteQueue();
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "dup-yes",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-yes",
                TextContent = "Yes",
                Summary = "Yes"
            }, fromExternalCapture: true);
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "dup-no",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-no",
                TextContent = "No",
                Summary = "No"
            }, fromExternalCapture: true);
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "dup-yes-2",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-yes",
                TextContent = "Yes",
                Summary = "Yes"
            }, fromExternalCapture: true);

            IReadOnlyList<ClipboardHistoryItem> queued = service.GetPasteQueueSnapshot();
            Assert.Equal(3, queued.Count);
            Assert.Equal(["Yes", "No", "Yes"], queued.Select(x => x.TextContent).ToArray());
            Assert.True(await service.AdvancePasteQueueForTestingAsync());
            Assert.Equal(2, service.PasteQueueCount);
            Assert.True(await service.AdvancePasteQueueForTestingAsync());
            Assert.Equal(1, service.PasteQueueCount);
            Assert.True(await service.AdvancePasteQueueForTestingAsync());
            Assert.Equal(0, service.PasteQueueCount);
            Assert.False(service.IsPasteQueueActive);

            // 2. Double-Copy correlation: rejects stale captures, skipped captures, and excluded foreground apps; pins fresh external capture.
            settingsService.Settings.Clipboard.ClipboardDoubleCopyAction = ClipboardSettingsSlice.DoubleCopyPin;
            settingsService.Settings.Clipboard.ClipboardExcludedApps = ["KeePass.exe"];

            // Stale chord observed well after last capture must NOT act on previous item:
            long farFutureTick = Environment.TickCount64 + 5_000;
            Assert.False(await service.EvaluateDoubleCopyForTestingAsync(farFutureTick));

            // Fresh external capture pins cleanly on correlated Double-Copy:
            ClipboardDoubleCopyEventArgs? firedEvent = null;
            service.DoubleCopyActionExecuted += (_, args) => firedEvent = args;
            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "dc-fresh",
                Kind = ClipboardItemKind.Text,
                ContentHash = "hash-dc-fresh",
                TextContent = "Pin me via double Ctrl+C",
                Summary = "Pin me via double Ctrl+C"
            }, fromExternalCapture: true);

            long nowTick = Environment.TickCount64;
            // Excluded foreground app blocks Double-Copy even when recent capture exists:
            Assert.False(await service.EvaluateDoubleCopyForTestingAsync(
                nowTick,
                new ClipboardSourceAppInfo(@"C:\Program Files\KeePass\KeePass.exe", "KeePass")));
            Assert.Null(firedEvent);

            // Allowed foreground app pins the fresh item:
            Assert.True(await service.EvaluateDoubleCopyForTestingAsync(nowTick));
            Assert.NotNull(firedEvent);
            Assert.Equal("dc-fresh", firedEvent!.Item.Id);
            Assert.True(service.Items.Single(x => x.Id == "dc-fresh").IsPinned);

            // Skipped capture (e.g., privacy format / empty selection / secret skip) blocks acting on previous item:
            firedEvent = null;
            service.RecordExternalCaptureSkipForTesting(Environment.TickCount64);
            Assert.False(await service.EvaluateDoubleCopyForTestingAsync(Environment.TickCount64));
            Assert.Null(firedEvent);

            // 3. >2,048-char externalized JSON array payload keeps CodeLanguage == "JSON" and formats/minifies via full payload read.
            string largeJsonItems = string.Join(",\n", Enumerable.Range(1, 90).Select(i => $"  {{ \"index\": {i}, \"label\": \"item-{i:D3}\" }}"));
            string largeJsonArray = "[\n" + largeJsonItems + "\n]";
            Assert.True(largeJsonArray.Length > 2048);
            ClipboardClassifier.ClassifyText(largeJsonArray, null, null, out ClipboardTextSubKind largeKind, out _, out string? largeLang);
            Assert.Equal(ClipboardTextSubKind.Code, largeKind);
            Assert.Equal("JSON", largeLang);

            await service.IngestItemAsync(new ClipboardHistoryItem
            {
                Id = "ext-json",
                Kind = ClipboardItemKind.Text,
                SubKind = largeKind,
                CodeLanguage = largeLang,
                ContentHash = "hash-ext-json",
                TextContent = largeJsonArray,
                Summary = ClipboardClassifier.BuildSummary(largeJsonArray),
                CharCount = largeJsonArray.Length
            });

            var config = new WidgetConfig { Id = "clip-review", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settingsService, localization, uiDispatch: static a => a());
            await vm.InitializeAsync();

            ClipboardItemViewModel extJsonVm = vm.VisibleItems.Single(x => x.Id == "ext-json");
            Assert.True(extJsonVm.Model.HasExternalTextPayload);
            Assert.Equal("JSON", extJsonVm.Model.CodeLanguage);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, extJsonVm.CompactQuickSlotVisibility);
            string loadedFullJson = await vm.GetFullTextAsync(extJsonVm);
            Assert.True(ClipboardTextTransformService.TryFormatJson(loadedFullJson, indented: false, out string minifiedLargeJson));
            Assert.DoesNotContain("\n", minifiedLargeJson, StringComparison.Ordinal);

            // 4. Cold-disk ClearDiskUnpinnedAsync removes unpinned items while preserving pinned items.
            await service.FlushAsync();
            int removedOnDisk = await ClipboardStore.ClearDiskUnpinnedAsync(tempRoot);
            Assert.Equal(3, removedOnDisk); // dup-yes, dup-no, ext-json removed; dc-fresh (pinned) preserved
            ClipboardStorageStats statsAfterClear = await ClipboardStore.CalculateDiskStorageStatsAsync(tempRoot);
            Assert.Equal(1, statsAfterClear.ActiveItemCount);
            Assert.Equal(1, statsAfterClear.PinnedItemCount);

            // 5. ClipboardFeatureRuntime awaits async stop delegate deterministically before completing DisposeAsync.
            bool asyncStopCompleted = false;
            var asyncRuntime = new ClipboardFeatureRuntime(
                () => { },
                async () =>
                {
                    await Task.Yield();
                    asyncStopCompleted = true;
                });
            await asyncRuntime.StartAsync();
            await asyncRuntime.DisposeAsync();
            Assert.True(asyncStopCompleted);
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
    public async Task LegacyQuickCaptureRecentItems_MigrateIdempotentlyToClipboardStore_PreservingMetadataAndImages()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string qcDir = Path.Combine(tempRoot, "quick-capture");
            string clipDir = Path.Combine(tempRoot, "clipboard");
            var qcStore = new QuickCaptureStore(qcDir);
            var clipboardStore = new ClipboardStore(clipDir);

            Directory.CreateDirectory(qcStore.ImageDirectory);
            string legacyPngPath = Path.Combine(qcStore.ImageDirectory, "legacy-shot.png");
            byte[] validOneByOnePng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            await File.WriteAllBytesAsync(legacyPngPath, validOneByOnePng);

            var pinnedCreatedAt = new DateTimeOffset(2026, 2, 10, 8, 30, 0, TimeSpan.Zero);
            var pinnedUpdatedAt = new DateTimeOffset(2026, 2, 11, 9, 45, 0, TimeSpan.Zero);
            var imageCreatedAt = new DateTimeOffset(2026, 2, 12, 10, 0, 0, TimeSpan.Zero);
            var imageUpdatedAt = new DateTimeOffset(2026, 2, 12, 10, 5, 0, TimeSpan.Zero);

            await qcStore.SaveAsync(new QuickCaptureStoreData
            {
                Items =
                [
                    new QuickCaptureItem
                    {
                        Id = "saved-note-1",
                        Type = QuickCaptureItemType.Text,
                        Body = "Saved permanent note",
                        IsPinned = true
                    }
                ],
                RecentItems =
                [
                    new QuickCaptureItem
                    {
                        Id = "recent-pinned-text",
                        Type = QuickCaptureItemType.Text,
                        Body = "Pinned legacy clip\r\nsecond line",
                        ContentHash = "legacy-hash-1",
                        IsPinned = true,
                        CreatedAt = pinnedCreatedAt,
                        UpdatedAt = pinnedUpdatedAt
                    },
                    new QuickCaptureItem
                    {
                        Id = "recent-dup-text",
                        Type = QuickCaptureItemType.Text,
                        Body = "Pinned legacy clip\nsecond line",
                        ContentHash = "legacy-hash-2",
                        IsPinned = false,
                        CreatedAt = pinnedCreatedAt.AddMinutes(-5),
                        UpdatedAt = pinnedUpdatedAt.AddMinutes(-5)
                    },
                    new QuickCaptureItem
                    {
                        Id = "recent-image",
                        Type = QuickCaptureItemType.Image,
                        ImagePath = legacyPngPath,
                        IsPinned = false,
                        CreatedAt = imageCreatedAt,
                        UpdatedAt = imageUpdatedAt
                    },
                    new QuickCaptureItem
                    {
                        Id = "recent-deleted",
                        Type = QuickCaptureItemType.Text,
                        Body = "Tombstoned clip",
                        IsDeleted = true,
                        CreatedAt = pinnedCreatedAt.AddHours(-1),
                        UpdatedAt = pinnedUpdatedAt.AddHours(1)
                    }
                ]
            });

            Assert.Equal(4, (await qcStore.LoadAsync()).RecentItems.Count);

            var quickCaptureService = new QuickCaptureService(qcStore);
            int migratedCount = await quickCaptureService.MigrateLegacyRecentItemsToClipboardAsync(clipboardStore);
            Assert.Equal(2, migratedCount);

            ClipboardStoreDocument clipDoc = await clipboardStore.LoadAsync();
            Assert.Equal(2, clipDoc.Items.Count);
            Assert.DoesNotContain(
                clipDoc.Items,
                x => (x.TextContent ?? string.Empty).Contains("Tombstoned clip", StringComparison.Ordinal));

            ClipboardHistoryItem migratedText = Assert.Single(clipDoc.Items, x => x.Kind == ClipboardItemKind.Text);
            Assert.True(migratedText.IsPinned);
            Assert.Equal(pinnedCreatedAt.UtcTicks, migratedText.CreatedAtUtcTicks);
            Assert.Equal(pinnedUpdatedAt.UtcTicks, migratedText.UpdatedAtUtcTicks);
            Assert.Equal("Pinned legacy clip\r\nsecond line", migratedText.TextContent);

            ClipboardHistoryItem migratedImage = Assert.Single(clipDoc.Items, x => x.Kind == ClipboardItemKind.Image);
            Assert.False(string.IsNullOrWhiteSpace(migratedImage.ImageFileName));
            string copiedClipImagePath = Path.Combine(clipboardStore.ImageDirectory, migratedImage.ImageFileName!);
            Assert.True(File.Exists(copiedClipImagePath));
            Assert.Equal(validOneByOnePng.Length, migratedImage.ByteSize);
            Assert.Equal(imageCreatedAt.UtcTicks, migratedImage.CreatedAtUtcTicks);
            Assert.Equal(imageUpdatedAt.UtcTicks, migratedImage.UpdatedAtUtcTicks);

            QuickCaptureStoreData reloadedQc = await new QuickCaptureStore(qcDir).LoadAsync();
            Assert.Empty(reloadedQc.RecentItems);
            QuickCaptureItem preservedSavedNote = Assert.Single(reloadedQc.Items);
            Assert.Equal("saved-note-1", preservedSavedNote.Id);
            Assert.Equal("Saved permanent note", preservedSavedNote.Body);

            // Second invocation (both warm and cold service instances) is an idempotent no-op.
            Assert.Equal(0, await quickCaptureService.MigrateLegacyRecentItemsToClipboardAsync(clipboardStore));
            Assert.Equal(0, await new QuickCaptureService(new QuickCaptureStore(qcDir)).MigrateLegacyRecentItemsToClipboardAsync(clipboardStore));
            Assert.Equal(2, (await clipboardStore.LoadAsync()).Items.Count);
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
    public async Task ClearDiskAllAsync_RemovesEntriesObservedAppsAndMediaFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardStore(tempDir);
            Directory.CreateDirectory(store.ImageDirectory);
            string seededImagePath = Path.Combine(store.ImageDirectory, "seeded-clip.png");
            await File.WriteAllBytesAsync(seededImagePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            await store.SaveAsync(new ClipboardStoreDocument
            {
                Items =
                [
                    new ClipboardHistoryItem
                    {
                        Id = "pinned-entry",
                        Kind = ClipboardItemKind.Text,
                        ContentHash = "txt:pinned",
                        Summary = "Pinned secret",
                        TextContent = "Pinned secret",
                        IsPinned = true,
                        CreatedAtUtcTicks = DateTime.UtcNow.Ticks,
                        UpdatedAtUtcTicks = DateTime.UtcNow.Ticks
                    },
                    new ClipboardHistoryItem
                    {
                        Id = "unpinned-image",
                        Kind = ClipboardItemKind.Image,
                        ContentHash = "img:seeded",
                        Summary = "1 × 1",
                        ImageFileName = "seeded-clip.png",
                        IsPinned = false,
                        CreatedAtUtcTicks = DateTime.UtcNow.Ticks,
                        UpdatedAtUtcTicks = DateTime.UtcNow.Ticks
                    }
                ],
                ObservedApps =
                [
                    new ClipboardObservedApp
                    {
                        AppPath = @"C:\Windows\System32\notepad.exe",
                        AppName = "Notepad",
                        LastSeenUtcTicks = DateTime.UtcNow.Ticks
                    }
                ]
            });

            int removed = await ClipboardStore.ClearDiskAllAsync(tempDir);
            Assert.Equal(2, removed);
            Assert.False(File.Exists(seededImagePath));

            var reloadedStore = new ClipboardStore(tempDir);
            ClipboardStoreDocument reloadedDoc = await reloadedStore.LoadAsync();
            Assert.Empty(reloadedDoc.Items);
            Assert.Empty(reloadedDoc.ObservedApps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ViewModel_SymmetricRootPadding_And_TabDisplayModes_UpdateFilterVisibilityAndMetrics()
    {
        Assert.Equal(ClipboardSettingsSlice.TabDisplayIconAndText, ClipboardSettingsSlice.NormalizeTabDisplayMode(null));
        Assert.Equal(ClipboardSettingsSlice.TabDisplayIconAndText, ClipboardSettingsSlice.NormalizeTabDisplayMode("invalid"));
        Assert.Equal(ClipboardSettingsSlice.TabDisplayIconOnly, ClipboardSettingsSlice.NormalizeTabDisplayMode("icononly"));
        Assert.Equal(ClipboardSettingsSlice.TabDisplayTextOnly, ClipboardSettingsSlice.NormalizeTabDisplayMode("TextOnly"));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-clip-tabmode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settings = new SettingsService();
            var localization = TestServices.CreateLocalizationService();
            using var service = new ClipboardHistoryService(new ClipboardStore(tempRoot), settings);
            var config = new WidgetConfig { Id = "clip-tab", WidgetKind = WidgetKind.Clipboard };
            using var vm = new ClipboardWidgetViewModel(config, service, settings, localization);

            Assert.Equal(new Microsoft.UI.Xaml.Thickness(10, 10, 10, 10), vm.RootPadding);
            Assert.Equal(ClipboardSettingsSlice.TabDisplayIconAndText, vm.TabDisplayMode);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterLabelVisibility);
            Assert.Equal(11.5, vm.FilterIconFontSize);

            settings.Settings.Clipboard.ClipboardTabDisplayMode = ClipboardSettingsSlice.TabDisplayIconOnly;
            vm.ApplyAppearanceFromSettings();
            Assert.Equal(ClipboardSettingsSlice.TabDisplayIconOnly, vm.TabDisplayMode);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.FilterLabelVisibility);
            Assert.Equal(12.5, vm.FilterIconFontSize);
            Assert.Equal(30, vm.FilterTabButtonMinWidth);
            Assert.Equal(new Microsoft.UI.Xaml.Thickness(8, 0, 8, 0), vm.FilterTabButtonPadding);

            settings.Settings.Clipboard.ClipboardTabDisplayMode = ClipboardSettingsSlice.TabDisplayTextOnly;
            vm.ApplyAppearanceFromSettings();
            Assert.Equal(ClipboardSettingsSlice.TabDisplayTextOnly, vm.TabDisplayMode);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.FilterIconVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.FilterLabelVisibility);
            Assert.Equal(11.5, vm.FilterIconFontSize);
            Assert.Equal(0, vm.FilterTabButtonMinWidth);
            Assert.Equal(new Microsoft.UI.Xaml.Thickness(9, 0, 9, 0), vm.FilterTabButtonPadding);
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
    public void WidgetEditorPopoverWindow_ComputeAnchoredBounds_FlipsAndClampsAcrossEdges()
    {
        var workArea = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

        // 1. Side placement with enough room on the right places popover to the right of owner window.
        var leftOwner = new Win32Helper.RECT { Left = 100, Top = 120, Right = 420, Bottom = 620 };
        var leftAnchor = new Win32Helper.RECT { Left = 110, Top = 200, Right = 410, Bottom = 260 };
        Windows.Graphics.RectInt32 rightPlaced = DeskBox.Views.WidgetEditorPopoverWindow.ComputeAnchoredBounds(
            DeskBox.Views.WidgetPopoverPlacement.Side,
            leftOwner,
            leftAnchor,
            workArea,
            widthPx: 360,
            heightPx: 280,
            gapPx: 8,
            screenMarginPx: 12);
        Assert.Equal(428, rightPlaced.X);
        Assert.Equal(200, rightPlaced.Y);

        // 2. Side placement near right screen edge flips to the left of owner window and clamps vertically.
        var rightOwner = new Win32Helper.RECT { Left = 1560, Top = 600, Right = 1900, Bottom = 1020 };
        var rightAnchor = new Win32Helper.RECT { Left = 1570, Top = 880, Right = 1890, Bottom = 940 };
        Windows.Graphics.RectInt32 leftFlipped = DeskBox.Views.WidgetEditorPopoverWindow.ComputeAnchoredBounds(
            DeskBox.Views.WidgetPopoverPlacement.Side,
            rightOwner,
            rightAnchor,
            workArea,
            widthPx: 360,
            heightPx: 320,
            gapPx: 8,
            screenMarginPx: 12);
        Assert.Equal(1192, leftFlipped.X);
        Assert.Equal(708, leftFlipped.Y);

        // 3. Bottom placement near bottom screen edge flips above anchor.
        var bottomAnchor = new Win32Helper.RECT { Left = 600, Top = 860, Right = 760, Bottom = 900 };
        Windows.Graphics.RectInt32 aboveFlipped = DeskBox.Views.WidgetEditorPopoverWindow.ComputeAnchoredBounds(
            DeskBox.Views.WidgetPopoverPlacement.Bottom,
            leftOwner,
            bottomAnchor,
            workArea,
            widthPx: 320,
            heightPx: 340,
            gapPx: 8,
            screenMarginPx: 12);
        Assert.Equal(600, aboveFlipped.X);
        Assert.Equal(512, aboveFlipped.Y);

        // 4. BottomRight placement aligns popover right edge to anchor right edge.
        Windows.Graphics.RectInt32 bottomRightPlaced = DeskBox.Views.WidgetEditorPopoverWindow.ComputeAnchoredBounds(
            DeskBox.Views.WidgetPopoverPlacement.BottomRight,
            leftOwner,
            new Win32Helper.RECT { Left = 600, Top = 180, Right = 760, Bottom = 220 },
            workArea,
            widthPx: 320,
            heightPx: 260,
            gapPx: 8,
            screenMarginPx: 12);
        Assert.Equal(440, bottomRightPlaced.X);
        Assert.Equal(228, bottomRightPlaced.Y);

        // 5. RTL Side placement prefers the left side of the owner window when space allows.
        var midOwner = new Win32Helper.RECT { Left = 700, Top = 120, Right = 1020, Bottom = 620 };
        var midAnchor = new Win32Helper.RECT { Left = 710, Top = 200, Right = 1010, Bottom = 260 };
        Windows.Graphics.RectInt32 rtlSidePlaced = DeskBox.Views.WidgetEditorPopoverWindow.ComputeAnchoredBounds(
            DeskBox.Views.WidgetPopoverPlacement.Side,
            midOwner,
            midAnchor,
            workArea,
            widthPx: 320,
            heightPx: 260,
            gapPx: 8,
            screenMarginPx: 12,
            isRightToLeft: true);
        Assert.Equal(372, rtlSidePlaced.X);
        Assert.Equal(200, rtlSidePlaced.Y);
    }

    [Fact]
    public void OutOfWidgetPopoverAndClipboardSymmetry_ContractGuards_AreWired()
    {
        string widgetBase = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.cs"));
        Assert.Contains("WidgetEditorPopoverWindow.HasOpenPopoverForOwner(HWnd)", widgetBase, StringComparison.Ordinal);

        string widgetInteraction = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        Assert.Contains("WidgetEditorPopoverWindow.IsPopoverOwnedBy(foregroundWindow, HWnd)", widgetInteraction, StringComparison.Ordinal);

        string commands = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        Assert.Contains("todo-widget-export", commands, StringComparison.Ordinal);
        Assert.Contains("SetTabDisplayMode(", commands, StringComparison.Ordinal);

        string clipboardXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml"));
        Assert.DoesNotContain("RowSpacing=\"5\"", clipboardXaml, StringComparison.Ordinal);
        Assert.Contains("StatusBannersVisibility", clipboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ItemsList\"", clipboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"0,0,6,0\"", clipboardXaml, StringComparison.Ordinal);

        string flyoutActivation = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Helpers/FlyoutTextInputActivation.cs"));
        Assert.Contains("IsInsideTextInputControl", flyoutActivation, StringComparison.Ordinal);
        Assert.Contains("bridges[i] == foregroundBefore", flyoutActivation, StringComparison.Ordinal);
    }
}
