using System.Security.Cryptography;
using DeskBox.Services;
using DeskBox.Services.CustomWidgets;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetCatalogSigningTests
{
    private const string Manifest = "{\"id\":\"deskbox.sign-demo\",\"schemaVersion\":1,\"elements\":{\"root\":{\"type\":\"Text\",\"text\":\"v1\"}}}";
    private const string LogicJs = "export function onInit(ctx) {\n  ctx.computed.banner = 'v1';\n}";

    private static (CustomWidgetRemoteCatalogItemDto Item, string PublicPem) BuildSignedItem(
        string manifestJson,
        string? logicJs)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = key.ExportSubjectPublicKeyInfoPem();
        string contentSha = CustomWidgetCatalogSignatureVerifier.ComputeContentSha256(
            "sign-demo", "1.2.0", manifestJson, logicJs, canonicalLocalesJson: null);
        byte[] signature = key.SignHash(
            Convert.FromHexString(contentSha),
            DSASignatureFormat.Rfc3279DerSequence);
        return (new CustomWidgetRemoteCatalogItemDto
        {
            Id = "sign-demo",
            Version = "1.2.0",
            ManifestJson = manifestJson,
            LogicJs = logicJs,
            ContentSha256 = contentSha,
            Signature = Convert.ToHexString(signature)
        }, publicPem);
    }

    [Fact]
    public void SignedEntry_Verifies_TamperedOrUnsignedOrWrongKey_FailClosed()
    {
        (CustomWidgetRemoteCatalogItemDto item, string publicPem) = BuildSignedItem(Manifest, LogicJs);
        Assert.True(CustomWidgetCatalogSignatureVerifier.VerifyEntry(item, Manifest, LogicJs, locales: null, publicPem));

        // Tampered manifest content (signature covers different bytes).
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(
            item, Manifest.Replace("v1", "evil", StringComparison.Ordinal), LogicJs, locales: null, publicPem));

        // Missing signature/digest fields.
        var unsigned = new CustomWidgetRemoteCatalogItemDto { Id = item.Id, Version = item.Version, ManifestJson = Manifest };
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(unsigned, Manifest, LogicJs, locales: null, publicPem));

        // Signed by a different key than the pinned one.
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(item, Manifest, LogicJs, locales: null,
            CustomWidgetCatalogSignatureVerifier.DefaultPublicKeyPem));

        // Digest mismatch with valid signature.
        var wrongDigest = new CustomWidgetRemoteCatalogItemDto
        {
            Id = item.Id,
            Version = item.Version,
            ManifestJson = Manifest,
            LogicJs = LogicJs,
            ContentSha256 = new string('a', 64),
            Signature = item.Signature
        };
        Assert.False(CustomWidgetCatalogSignatureVerifier.VerifyEntry(wrongDigest, Manifest, LogicJs, locales: null, publicPem));
    }

    [Fact]
    public void ContentSha256_CoversIdVersionAndParts()
    {
        string baseHash = CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w", "1.0.0", Manifest, LogicJs, null);
        Assert.Equal(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w", "1.0.0", Manifest, LogicJs, null));
        Assert.NotEqual(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w", "1.1.0", Manifest, LogicJs, null));
        Assert.NotEqual(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w2", "1.0.0", Manifest, LogicJs, null));
        Assert.NotEqual(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w", "1.0.0", Manifest + " ", LogicJs, null));
        Assert.NotEqual(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256("w", "1.0.0", Manifest, LogicJs + " ", null));
        // CRLF logic.js normalization is NOT applied here — raw bytes are signed.
        Assert.NotEqual(baseHash, CustomWidgetCatalogSignatureVerifier.ComputeContentSha256(
            "w", "1.0.0", Manifest, LogicJs.Replace("\n", "\r\n", StringComparison.Ordinal), null));
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

            // An explicit user override still decides.
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
}