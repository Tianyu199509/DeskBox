using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.CustomWidgets;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetCatalogSigningTests
{
    private const string ManifestCompact =
        "{\"id\":\"deskbox.sign-demo\",\"schemaVersion\":1,\"icon\":\"\\uE71D\",\"script\":{\"entry\":\"logic.js\"},\"root\":\"root\",\"elements\":{\"root\":{\"type\":\"Text\",\"text\":\"${$computed.banner}\"}}}";

    private const string ManifestIndented = """
        {
          "elements": {
            "root": {
              "text": "${$computed.banner}",
              "type": "Text"
            }
          },
          "icon": "",
          "id": "deskbox.sign-demo",
          "root": "root",
          "schemaVersion": 1,
          "script": {
            "entry": "logic.js"
          }
        }
        """;

    private const string LogicJs = "export function onInit(ctx) {\n  ctx.computed.banner = 'v1';\n}";

    private static (CustomWidgetRemoteCatalogItemDto Item, string PublicPem) BuildSignedItem(
        string id,
        string version,
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales = null)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = key.ExportSubjectPublicKeyInfoPem();
        byte[] preimage = CustomWidgetCatalogSignatureVerifier.BuildPreimageBytes(
            id, version, manifestJson, logicJs, locales);
        string contentSha = Convert.ToHexStringLower(SHA256.HashData(preimage));
        byte[] signature = key.SignData(
            preimage,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return (new CustomWidgetRemoteCatalogItemDto
        {
            Id = id,
            ManifestId = $"deskbox.{CustomWidgetCatalogSignatureVerifier.NormalizeCatalogEntryId(id)}",
            Name = "Signed Demo",
            Version = version,
            ManifestJson = manifestJson,
            LogicJs = logicJs,
            ContentSha256 = contentSha,
            Signature = Convert.ToHexString(signature)
        }, publicPem);
    }

    [Fact]
    public void SignedEntry_Verifies_AcrossFormattingAndCrlf_WhileTamperedOrUnsignedFailClosed()
    {
        var localeMap = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["meta.name"] = "签名测试" },
            ["ja-JP"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["meta.name"] = "署名テスト" }
        };
        var locales = new CustomWidgetLocaleCatalog(
            CustomWidgetLocaleMeta.BuildFromBaseDictionary("zh-CN", localeMap["zh-CN"]),
            localeMap);

        (CustomWidgetRemoteCatalogItemDto item, string publicPem) = BuildSignedItem(
            "sign-demo", "1.2.0", ManifestCompact, LogicJs, locales);
        Assert.True(CustomWidgetCatalogSignatureVerifier.VerifyEntry(item, ManifestCompact, LogicJs, locales, publicPem));

        // Indented / reordered JSON keys / literal PUA glyph vs \uE71D escape and CRLF logic.js verify identically.
        string crlfLogic = LogicJs.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.True(CustomWidgetCatalogSignatureVerifier.VerifyEntry(item, ManifestIndented, crlfLogic, locales, publicPem));

        // Tampered manifest value fails closed.
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(
            item,
            ManifestCompact.Replace("banner", "pwned", StringComparison.Ordinal),
            LogicJs,
            locales,
            publicPem));

        // Tampered script fails closed.
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(
            item,
            ManifestCompact,
            LogicJs.Replace("v1", "evil", StringComparison.Ordinal),
            locales,
            publicPem));

        // Tampered locale value fails closed.
        var tamperedMap = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["meta.name"] = "被篡改" },
            ["ja-JP"] = localeMap["ja-JP"]
        };
        var tamperedLocales = new CustomWidgetLocaleCatalog(locales.Meta, tamperedMap);
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(item, ManifestCompact, LogicJs, tamperedLocales, publicPem));

        // Missing signature/digest fields.
        var unsigned = new CustomWidgetRemoteCatalogItemDto { Id = item.Id, Version = item.Version, ManifestJson = ManifestCompact };
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(unsigned, ManifestCompact, LogicJs, locales, publicPem));

        // Signed by a different key than the pinned public key.
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(
            item, ManifestCompact, LogicJs, locales, CustomWidgetCatalogSignatureVerifier.DefaultPublicKeyPem));

        // Digest mismatch with valid signature.
        var wrongDigest = new CustomWidgetRemoteCatalogItemDto
        {
            Id = item.Id,
            Version = item.Version,
            ManifestJson = ManifestCompact,
            LogicJs = LogicJs,
            ContentSha256 = new string('a', 64),
            Signature = item.Signature
        };
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(wrongDigest, ManifestCompact, LogicJs, locales, publicPem));
    }

    [Fact]
    public async Task RemoteCatalogService_EnforcesSignatureOnGalleryAndOta_AndPreservesSignatureAcrossColdCacheReload()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "CatalogSignE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ECDsa publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publisherPublicPem = publisherKey.ExportSubjectPublicKeyInfoPem();

            const string signedManifest = """
            {
              "schemaVersion": 1,
              "id": "deskbox.countdown",
              "name": "Signed Countdown v99",
              "version": "99.0.0",
              "origin": "official",
              "root": "root",
              "elements": {
                "root": { "type": "Text", "text": "signed-v99" }
              }
            }
            """;

            byte[] preimage = CustomWidgetCatalogSignatureVerifier.BuildPreimageBytes(
                "countdown", "99.0.0", signedManifest, logicJs: null, locales: null);
            string contentSha = Convert.ToHexStringLower(SHA256.HashData(preimage));
            string signatureHex = Convert.ToHexString(publisherKey.SignData(
                preimage,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));

            string catalogPayload = $$"""
            {
              "schemaVersion": 2,
              "updatedAtUtc": "2026-11-21T12:00:00Z",
              "widgets": [
                {
                  "id": "countdown",
                  "manifestId": "deskbox.countdown",
                  "name": "Signed Countdown v99",
                  "version": "99.0.0",
                  "contentSha256": "{{contentSha}}",
                  "signature": "{{signatureHex}}",
                  "manifest": {{signedManifest}}
                },
                {
                  "id": "pomodoro",
                  "manifestId": "deskbox.pomodoro",
                  "name": "Unsigned Tampered Pomodoro",
                  "version": "99.0.0",
                  "manifest": {
                    "schemaVersion": 1,
                    "id": "deskbox.pomodoro",
                    "name": "Unsigned Tampered Pomodoro",
                    "version": "99.0.0",
                    "root": "root",
                    "elements": { "root": { "type": "Text", "text": "unsigned-evil" } }
                  }
                },
                {
                  "id": "unsigned-remote-only",
                  "manifestId": "deskbox.unsigned-remote-only",
                  "name": "Unsigned Remote",
                  "version": "1.0.0",
                  "manifest": {
                    "schemaVersion": 1,
                    "id": "deskbox.unsigned-remote-only",
                    "name": "Unsigned Remote",
                    "version": "1.0.0",
                    "root": "root",
                    "elements": { "root": { "type": "Text", "text": "unsigned-new" } }
                  }
                }
              ]
            }
            """;

            bool VerifyWithTestKey(
                CustomWidgetRemoteCatalogItemDto dto,
                string? json,
                string? js,
                CustomWidgetLocaleCatalog? loc) =>
                CustomWidgetCatalogSignatureVerifier.VerifyEntry(dto, json, js, loc, publisherPublicPem);

            string cacheDir = Path.Combine(tempRoot, "cache");
            var catalog = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new StubHttpHandler(HttpStatusCode.NotFound),
                VerifyWithTestKey);

            Assert.True(await catalog.ApplyCatalogJsonAsync(catalogPayload, "\"sig-v1\"", CustomWidgetRemoteCatalogService.PrimaryCatalogUrl));

            // 1. Workshop Gallery: signed countdown upgrades to 99.0.0; unsigned pomodoro stays built-in; unsigned remote-only is blocked.
            IReadOnlyList<CustomWidgetCatalogEntry> merged = catalog.GetMergedOfficialTemplates();
            CustomWidgetCatalogEntry countdown = Assert.Single(merged, e => e.Id == "countdown");
            Assert.Equal("99.0.0", countdown.Version);
            Assert.True(countdown.IsRemoteUpgraded);

            CustomWidgetCatalogEntry pomodoro = Assert.Single(merged, e => e.Id == "pomodoro");
            Assert.False(pomodoro.IsRemoteUpgraded);
            Assert.NotEqual("99.0.0", pomodoro.Version);

            Assert.DoesNotContain(merged, e => e.Id == "unsigned-remote-only");

            // 2. Cold-start disk reload: CloneWithoutHydratedArtifacts must preserve ContentSha256 + Signature.
            var coldCatalog = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new StubHttpHandler(HttpStatusCode.ServiceUnavailable),
                VerifyWithTestKey);
            CustomWidgetCatalogEntry coldCountdown = Assert.Single(coldCatalog.GetMergedOfficialTemplates(), e => e.Id == "countdown");
            Assert.Equal("99.0.0", coldCountdown.Version);
            Assert.True(coldCountdown.IsRemoteUpgraded);

            Assert.True(coldCatalog.TryResolveNewerOfficialBundle(
                "{\"schemaVersion\":1,\"id\":\"deskbox.countdown\",\"version\":\"1.0.0\",\"origin\":\"official\",\"root\":\"r\",\"elements\":{\"r\":{\"type\":\"Text\",\"text\":\"old\"}}}",
                out CustomWidgetTemplateBundle? upgradedCountdown));
            Assert.NotNull(upgradedCountdown);
            Assert.Contains("signed-v99", upgradedCountdown!.ManifestJson, StringComparison.Ordinal);

            Assert.False(coldCatalog.TryResolveNewerOfficialBundle(
                "{\"schemaVersion\":1,\"id\":\"deskbox.pomodoro\",\"version\":\"1.0.0\",\"origin\":\"official\",\"root\":\"r\",\"elements\":{\"r\":{\"type\":\"Text\",\"text\":\"old\"}}}",
                out _));

            // 3. Unsigned payload attempting to deprecate countdown, inject unsigned requiresUpgrade, or clobber signed cache is rejected while preserving verified countdown v99.0.0!
            const string hostileMirrorPayload = """
            {
              "schemaVersion": 2,
              "updatedAtUtc": "2026-11-22T00:00:00Z",
              "widgets": [
                {
                  "id": "countdown",
                  "manifestId": "deskbox.countdown",
                  "name": "Unsigned Downgrade",
                  "version": "100.0.0",
                  "deprecated": true
                },
                {
                  "id": "spoof-upgrade-card",
                  "manifestId": "deskbox.spoof-upgrade-card",
                  "name": "Fake Upgrade Card",
                  "version": "1.0.0",
                  "minAppVersion": "99.0.0",
                  "contentSha256": "x",
                  "signature": "y"
                }
              ]
            }
            """;
            Assert.True(await coldCatalog.ApplyCatalogJsonAsync(hostileMirrorPayload, "\"sig-v2\"", CustomWidgetRemoteCatalogService.MirrorCatalogUrl));
            IReadOnlyList<CustomWidgetCatalogEntry> afterHostile = coldCatalog.GetMergedOfficialTemplates();
            CustomWidgetCatalogEntry retainedCountdown = Assert.Single(afterHostile, e => e.Id == "countdown");
            Assert.Equal("99.0.0", retainedCountdown.Version);
            Assert.DoesNotContain(afterHostile, e => e.Id == "spoof-upgrade-card");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemotePromptService_EnforcesEcdsaSignatureOnOverridesAndRollbacks_AndVerifiesTrackedPromptCatalog()
    {
        // Verify tracked catalog/prompts.v1.json against built-in DefaultPublicKeyPem.
        string trackedPromptsPath = TestPaths.FromRepository("catalog/prompts.v1.json");
        var trackedDoc = System.Text.Json.JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(trackedPromptsPath, Encoding.UTF8),
            CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptDocument);
        Assert.NotNull(trackedDoc);
        Assert.True(CustomWidgetCatalogSignatureVerifier.VerifyPromptDocument(trackedDoc));

        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "PromptSignE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pubPem = key.ExportSubjectPublicKeyInfoPem();

            var signedOverride = new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 1,
                PromptVersion = 3,
                ExtraDesignRules = "- SIGNED_OTA_RULE: Keep tabular numerals crisp."
            };
            byte[] preimage = CustomWidgetCatalogSignatureVerifier.BuildPromptPreimageBytes(signedOverride);
            signedOverride.ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(preimage));
            signedOverride.Signature = Convert.ToHexString(key.SignData(preimage, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

            string currentPayload = System.Text.Json.JsonSerializer.Serialize(
                signedOverride,
                CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptDocument);

            var promptSvc = new CustomWidgetRemotePromptService(
                tempRoot,
                new DynamicJsonHttpHandler(() => currentPayload),
                currentAppVersionOverride: new Version(1, 6, 0, 0),
                signatureVerifier: doc => CustomWidgetCatalogSignatureVerifier.VerifyPromptDocument(doc, pubPem));

            Assert.True(await promptSvc.RefreshInBackgroundAsync(force: true));
            Assert.True(promptSvc.GetEffectivePromptPack().IsRemoteOverride);
            Assert.Contains("SIGNED_OTA_RULE", promptSvc.GetEffectivePromptPack().ExtraDesignRules, StringComparison.Ordinal);

            // Unsigned rollback must NOT retire an active signed override.
            currentPayload = """{"schemaVersion":1,"promptVersion":1}""";
            Assert.False(await promptSvc.RefreshInBackgroundAsync(force: true));
            Assert.True(promptSvc.GetEffectivePromptPack().IsRemoteOverride);

            // Tampered extraDesignRules with original signature fails closed.
            var tampered = new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 1,
                PromptVersion = 4,
                ExtraDesignRules = "- EVIL_INJECTION",
                ContentSha256 = signedOverride.ContentSha256,
                Signature = signedOverride.Signature
            };
            Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyPromptDocument(tampered, pubPem));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OfficialOriginTrust_NeverImplicitlyGrantsCommandExecution()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "CatalogSignTrust", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomWidgetTrustStore(tempRoot);
            CustomWidgetTrustProfile officialProfile = store.GetProfile(
                "deskbox.pomodoro",
                manifestOriginFallback: "Official",
                manifestTemplateId: "deskbox.pomodoro");

            Assert.True(officialProfile.TrustedLocalAuthoring);
            Assert.False(officialProfile.IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));
            Assert.True(officialProfile.IsPermissionGranted(CustomWidgetPermissionKind.Notifications));

            store.SetPermissionOverride(
                "deskbox.pomodoro",
                CustomWidgetPermissionKind.CommandExecution,
                enabled: true,
                defaultTrustedIfNew: true);
            Assert.True(store.GetProfile("deskbox.pomodoro").IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class StubHttpHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class DynamicJsonHttpHandler(Func<string> payloadProvider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payloadProvider(), Encoding.UTF8, "application/json")
            });
    }
}
