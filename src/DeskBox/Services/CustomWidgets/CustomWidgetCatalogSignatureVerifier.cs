using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services.CustomWidgets;

/// <summary>
/// Offline signature gate for remote catalog OTA. The catalog travels over HTTPS
/// from deskbox.fun / GitHub, but a compromised origin or MITM would silently
/// replace official widget scripts that carry full default trust — so each entry
/// carries an ECDSA P-256 signature over its executable content, verified against
/// this built-in public key before any automatic upgrade. The matching private key
/// is kept offline by the publisher and never enters the repository.
/// </summary>
internal static class CustomWidgetCatalogSignatureVerifier
{
    // Canonical JSON must match scripts/publish_widget_catalog.py byte-for-byte:
    // unescaped non-ASCII, compact, sorted keys.
    private static readonly JsonWriterOptions RelaxedWriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal const string DefaultPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKgQZ2G2WnAnrrKmcQzvvqp/WfR/B
        LsPMS4Fu2K0FxKjT8ZzO5v7tci3WMThTc2EC8xjtwiklO4UcFIISUgkjVA==
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// Digest preimage covering identity plus every executable part. Each part is
    /// hashed separately so the signature is independent of JSON escaping choices:
    /// id + version + sha256(manifest) + sha256(logic.js) + sha256(canonical locales).
    /// </summary>
    internal static string ComputeContentSha256(
        string id,
        string version,
        string? manifestJson,
        string? logicJs,
        string? canonicalLocalesJson)
    {
        static string HexOf(string? s) =>
            string.IsNullOrEmpty(s)
                ? Convert.ToHexStringLower(SHA256.HashData([]))
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        string preimage = string.Join(
            '\n',
            id,
            version,
            HexOf(manifestJson),
            HexOf(logicJs),
            HexOf(canonicalLocalesJson));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));
    }

    internal static string? CanonicalLocalesJson(CustomWidgetLocaleCatalog? locales)
    {
        if (locales is not { HasAnyLocale: true })
        {
            return null;
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string culture, IReadOnlyDictionary<string, string> entries) in locales.Locales)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, RelaxedWriterOptions))
            {
                writer.WriteStartObject();
                foreach ((string key, string value) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            sorted[culture] = Encoding.UTF8.GetString(ms.ToArray());
        }

        using var outer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outer, RelaxedWriterOptions))
        {
            writer.WriteStartObject();
            foreach ((string culture, string json) in sorted)
            {
                writer.WritePropertyName(culture);
                writer.WriteRawValue(json, skipInputValidation: false);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(outer.ToArray());
    }

    /// <summary>Test seam mirroring RootOverride: when set, replaces built-in key verification.</summary>
    internal static Func<CustomWidgetRemoteCatalogItemDto, string, string?, CustomWidgetLocaleCatalog?, bool>? VerifyOverrideForTests;

    /// <summary>
    /// Fails closed: unsigned entries, digest mismatches, or bad signatures never
    /// auto-upgrade. Callers treat false as "skip this OTA", never as an error.
    /// </summary>
    internal static bool VerifyEntry(
        CustomWidgetRemoteCatalogItemDto item,
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? publicKeyPem = null)
    {
        if (VerifyOverrideForTests is { } overrideVerifier)
        {
            return overrideVerifier(item, manifestJson, logicJs, locales);
        }

        if (string.IsNullOrWhiteSpace(item.ContentSha256) || string.IsNullOrWhiteSpace(item.Signature))
        {
            return false;
        }

        try
        {
            byte[] expectedDigest = Convert.FromHexString(item.ContentSha256.Trim());
            byte[] signature = Convert.FromHexString(item.Signature.Trim());
            string computed = ComputeContentSha256(
                item.Id,
                item.Version,
                manifestJson,
                logicJs,
                CanonicalLocalesJson(locales));
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedDigest,
                    Convert.FromHexString(computed)))
            {
                return false;
            }

            using ECDsa publicKeys = ECDsa.Create();
            publicKeys.ImportFromPem(publicKeyPem ?? DefaultPublicKeyPem);
            return publicKeys.VerifyHash(expectedDigest, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }
}