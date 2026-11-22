using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services.CustomWidgets;

/// <summary>
/// Offline signature gate for remote catalog templates and OTA updates.
/// Every executable entry in <c>catalog.v1.json</c> / <c>widgets.v1.json</c> must carry a
/// <c>contentSha256</c> digest and an ECDSA P-256 signature verifiable against the built-in
/// offline public key before DeskBox merges it into the official Workshop gallery or applies
/// an automatic OTA upgrade.
/// </summary>
internal static class CustomWidgetCatalogSignatureVerifier
{
    private static readonly byte[] EmptySha256Bytes = SHA256.HashData([]);
    private static readonly string EmptySha256Hex = Convert.ToHexStringLower(EmptySha256Bytes);

    private static readonly JsonDocumentOptions PermissiveJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    internal const string DefaultPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKgQZ2G2WnAnrrKmcQzvvqp/WfR/B
        LsPMS4Fu2K0FxKjT8ZzO5v7tci3WMThTc2EC8xjtwiklO4UcFIISUgkjVA==
        -----END PUBLIC KEY-----
        """;

    private static readonly Lazy<ECParameters> DefaultPublicKeyParams = new(static () =>
    {
        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(DefaultPublicKeyPem);
        return ecdsa.ExportParameters(includePrivateParameters: false);
    });

    internal static string SerializeCanonicalJsonElement(JsonElement element, bool relaxedEscaping = false)
    {
        using var ms = new MemoryStream();
        var options = relaxedEscaping
            ? new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            : default;
        using (var writer = new Utf8JsonWriter(ms, options))
        {
            WriteCanonicalJsonElement(writer, element);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string ComputeCanonicalJsonHash(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return EmptySha256Hex;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                WriteCanonicalJsonElement(writer, doc.RootElement);
            }

            return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
        }
        catch
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson)));
        }
    }

    internal static void WriteCanonicalJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty prop in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonicalJsonElement(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJsonElement(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    internal static string NormalizeCatalogEntryId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return string.Empty;
        }

        string val = rawId.Trim();
        if (val.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
        {
            val = val["deskbox.".Length..];
        }

        return val.Trim().ToLowerInvariant();
    }

    internal static string NormalizeNewlines(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// Builds the UTF-8 preimage signed by <c>scripts/publish_widget_catalog.py</c>:
    /// <c>normId + "\n" + version + "\n" + manifestAstSha256 + "\n" + logicSha256 + "\n" + localesAstSha256 + "\n" + deprecatedFlag + "\n" + minAppVersion + "\n" + minSchemaVersion</c>.
    /// Hashing the parsed JSON AST makes the digest invariant to indentation and Unicode escaping
    /// choices across Python and .NET while remaining sensitive to every structural or value change.
    /// </summary>
    internal static byte[] BuildPreimageBytes(
        string id,
        string version,
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        bool deprecated = false,
        string? minAppVersion = null,
        int minSchemaVersion = 0)
    {
        string normId = NormalizeCatalogEntryId(id);
        string normVersion = (version ?? string.Empty).Trim();
        string normalizedLogic = NormalizeNewlines(logicJs);
        string? effectiveLogicForSync = string.IsNullOrWhiteSpace(normalizedLogic) ? null : normalizedLogic;

        string manifestDigest = ComputeManifestAstSha256(manifestJson, effectiveLogicForSync);
        string logicDigest = normalizedLogic.Length == 0
            ? EmptySha256Hex
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedLogic)));
        string localesDigest = ComputeLocalesAstSha256(locales);
        string deprecatedToken = deprecated ? "1" : "0";
        string normMinApp = (minAppVersion ?? string.Empty).Trim();
        string normMinSchema = Math.Max(0, minSchemaVersion).ToString(CultureInfo.InvariantCulture);

        string preimage = string.Join(
            '\n',
            normId,
            normVersion,
            manifestDigest,
            logicDigest,
            localesDigest,
            deprecatedToken,
            normMinApp,
            normMinSchema);
        return Encoding.UTF8.GetBytes(preimage);
    }

    internal static string ComputeContentSha256(
        string id,
        string version,
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales = null,
        bool deprecated = false,
        string? minAppVersion = null,
        int minSchemaVersion = 0) =>
        Convert.ToHexStringLower(SHA256.HashData(BuildPreimageBytes(
            id,
            version,
            manifestJson,
            logicJs,
            locales,
            deprecated,
            minAppVersion,
            minSchemaVersion)));

    internal static string ComputeManifestAstSha256(string? manifestJson, string? syncedLogicJs)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return EmptySha256Hex;
        }

        string syncedManifest = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, syncedLogicJs);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(syncedManifest, PermissiveJsonOptions);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendCanonicalJsonElement(hasher, doc.RootElement);
            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        catch
        {
            // Malformed JSON falls back to raw UTF-8 bytes so invalid payloads still hash distinctly.
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));
        }
    }

    internal static string ComputeLocalesAstSha256(CustomWidgetLocaleCatalog? locales)
    {
        if (locales is not { HasAnyLocale: true })
        {
            return EmptySha256Hex;
        }

        var nonEmptyCultures = new List<(byte[] KeyUtf8, IReadOnlyDictionary<string, string> Entries)>();
        foreach ((string culture, IReadOnlyDictionary<string, string> entries) in locales.Locales)
        {
            if (string.IsNullOrWhiteSpace(culture) || entries is not { Count: > 0 })
            {
                continue;
            }

            nonEmptyCultures.Add((Encoding.UTF8.GetBytes(culture), entries));
        }

        if (nonEmptyCultures.Count == 0)
        {
            return EmptySha256Hex;
        }

        nonEmptyCultures.Sort(static (a, b) => a.KeyUtf8.AsSpan().SequenceCompareTo(b.KeyUtf8));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAscii(hasher, $"O{nonEmptyCultures.Count}:");
        foreach ((byte[] cultureUtf8, IReadOnlyDictionary<string, string> entries) in nonEmptyCultures)
        {
            AppendBytesToken(hasher, 'K', cultureUtf8);
            var sortedEntries = new List<(byte[] KeyUtf8, byte[] ValUtf8)>(entries.Count);
            foreach ((string k, string v) in entries)
            {
                sortedEntries.Add((Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(v ?? string.Empty)));
            }

            sortedEntries.Sort(static (a, b) => a.KeyUtf8.AsSpan().SequenceCompareTo(b.KeyUtf8));
            AppendAscii(hasher, $"O{sortedEntries.Count}:");
            foreach ((byte[] kUtf8, byte[] vUtf8) in sortedEntries)
            {
                AppendBytesToken(hasher, 'K', kUtf8);
                AppendBytesToken(hasher, 'S', vUtf8);
            }

            AppendAscii(hasher, "};");
        }

        AppendAscii(hasher, "};");
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static void AppendCanonicalJsonElement(IncrementalHash hasher, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                AppendAscii(hasher, "Z;");
                break;

            case JsonValueKind.True:
                AppendAscii(hasher, "T;");
                break;

            case JsonValueKind.False:
                AppendAscii(hasher, "F;");
                break;

            case JsonValueKind.String:
                AppendBytesToken(hasher, 'S', Encoding.UTF8.GetBytes(element.GetString() ?? string.Empty));
                break;

            case JsonValueKind.Number:
                AppendAscii(hasher, $"N{NormalizeJsonNumber(element)};");
                break;

            case JsonValueKind.Array:
                AppendAscii(hasher, $"A{element.GetArrayLength()}:");
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AppendCanonicalJsonElement(hasher, item);
                }

                AppendAscii(hasher, "];");
                break;

            case JsonValueKind.Object:
                var props = new List<(byte[] NameUtf8, JsonElement Value)>();
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    props.Add((Encoding.UTF8.GetBytes(prop.Name), prop.Value));
                }

                props.Sort(static (a, b) => a.NameUtf8.AsSpan().SequenceCompareTo(b.NameUtf8));
                AppendAscii(hasher, $"O{props.Count}:");
                foreach ((byte[] nameUtf8, JsonElement val) in props)
                {
                    AppendBytesToken(hasher, 'K', nameUtf8);
                    AppendCanonicalJsonElement(hasher, val);
                }

                AppendAscii(hasher, "};");
                break;
        }
    }

    private static string NormalizeJsonNumber(JsonElement numberElement)
    {
        if (numberElement.TryGetInt64(out long i64))
        {
            return i64.ToString(CultureInfo.InvariantCulture);
        }

        if (numberElement.TryGetDecimal(out decimal dec))
        {
            decimal normalized = dec / 1.000000000000000000000000000000000m;
            if (normalized == 0m)
            {
                return "0";
            }

            return normalized.ToString(CultureInfo.InvariantCulture);
        }

        double dbl = numberElement.GetDouble();
        return dbl == 0d ? "0" : dbl.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void AppendBytesToken(IncrementalHash hasher, char tag, byte[] utf8)
    {
        AppendAscii(hasher, $"{tag}{utf8.Length}:");
        hasher.AppendData(utf8);
        AppendAscii(hasher, ";");
    }

    private static void AppendAscii(IncrementalHash hasher, string ascii) =>
        hasher.AppendData(Encoding.ASCII.GetBytes(ascii));

    /// <summary>
    /// Fails closed: unsigned entries, digest mismatches, cross-ID substitutions, or bad signatures
    /// are rejected for Workshop gallery merging, deprecations, upgrade notices, and OTA upgrades.
    /// </summary>
    internal static bool VerifyEntry(
        CustomWidgetRemoteCatalogItemDto item,
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? publicKeyPem = null)
    {
        if (item is null ||
            string.IsNullOrWhiteSpace(item.Id) ||
            string.IsNullOrWhiteSpace(item.ContentSha256) ||
            string.IsNullOrWhiteSpace(item.Signature))
        {
            return false;
        }

        string normId = NormalizeCatalogEntryId(item.Id);
        if (string.IsNullOrWhiteSpace(normId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.ManifestId) &&
            !string.Equals(NormalizeCatalogEntryId(item.ManifestId), normId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool requiresNewerClient = CustomWidgetManifestParser.RequiresNewerClient(
            item.MinSchemaVersion,
            item.MinAppVersion,
            out _);
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            if (!item.Deprecated && !requiresNewerClient)
            {
                return false;
            }
        }
        else if (!MatchesManifestRootId(manifestJson, normId))
        {
            return false;
        }

        byte[] preimageBytes = BuildPreimageBytes(
            item.Id,
            item.Version,
            manifestJson,
            logicJs,
            locales,
            item.Deprecated,
            item.MinAppVersion,
            item.MinSchemaVersion ?? 0);
        return VerifyPreimageSignature(preimageBytes, item.ContentSha256, item.Signature, publicKeyPem);
    }

    internal static byte[] BuildPromptPreimageBytes(CustomWidgetRemotePromptDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        using var ms = new MemoryStream();
        void WriteToken(char tag, string? val)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(val ?? string.Empty);
            byte[] header = Encoding.ASCII.GetBytes($"{tag}{utf8.Length}:");
            ms.Write(header);
            ms.Write(utf8);
            ms.WriteByte((byte)';');
        }

        WriteToken('H', "deskbox-prompts-v1");
        WriteToken('N', doc.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        WriteToken('N', doc.PromptVersion.ToString(CultureInfo.InvariantCulture));
        WriteToken('S', (doc.MinAppVersion ?? string.Empty).Trim());
        WriteToken('S', (doc.MaxAppVersion ?? string.Empty).Trim());
        WriteToken('S', NormalizeNewlines(doc.SystemPrompt?.Trim()));
        WriteToken('S', NormalizeNewlines(doc.SystemPromptZh?.Trim()));
        WriteToken('S', NormalizeNewlines(doc.SystemPromptEn?.Trim()));
        WriteToken('S', NormalizeNewlines(doc.ExtraDesignRules?.Trim()));
        WriteToken('S', NormalizeNewlines(doc.SkillMarkdown?.Trim()));

        var patches = new List<CustomWidgetCapabilityPatchDto>();
        if (doc.CapabilityPatches is { Count: > 0 })
        {
            foreach (CustomWidgetCapabilityPatchDto? patch in doc.CapabilityPatches)
            {
                if (patch is not null && !string.IsNullOrWhiteSpace(patch.Id))
                {
                    patches.Add(patch);
                }
            }
        }

        WriteToken('A', patches.Count.ToString(CultureInfo.InvariantCulture));
        foreach (CustomWidgetCapabilityPatchDto patch in patches)
        {
            WriteToken('S', patch.Id.Trim());
            WriteToken('S', NormalizeNewlines(patch.TitleZh?.Trim()));
            WriteToken('S', NormalizeNewlines(patch.TitleEn?.Trim()));
            WriteToken('S', NormalizeNewlines(patch.SummaryZh?.Trim()));
            WriteToken('S', NormalizeNewlines(patch.SummaryEn?.Trim()));
            WriteToken('S', NormalizeNewlines(patch.CompactContractMarkdown?.Trim()));
            WriteToken('S', NormalizeNewlines(patch.GoldenSnippetJson?.Trim()));
            WriteToken('B', patch.Deprecated ? "1" : "0");

            var keywords = new List<(byte[] Utf8, string Text)>();
            if (patch.TriggerKeywords is { Count: > 0 })
            {
                var seenLower = new HashSet<string>(StringComparer.Ordinal);
                foreach (string? rawKw in patch.TriggerKeywords)
                {
                    if (string.IsNullOrWhiteSpace(rawKw))
                    {
                        continue;
                    }

                    string trimmed = rawKw.Trim();
                    if (seenLower.Add(trimmed.ToLowerInvariant()))
                    {
                        keywords.Add((Encoding.UTF8.GetBytes(trimmed), trimmed));
                    }
                }

                keywords.Sort(static (a, b) => a.Utf8.AsSpan().SequenceCompareTo(b.Utf8));
            }

            WriteToken('A', keywords.Count.ToString(CultureInfo.InvariantCulture));
            foreach ((_, string kw) in keywords)
            {
                WriteToken('S', kw);
            }
        }

        return ms.ToArray();
    }

    internal static bool VerifyPromptDocument(
        CustomWidgetRemotePromptDocument? doc,
        string? publicKeyPem = null)
    {
        if (doc is null ||
            string.IsNullOrWhiteSpace(doc.ContentSha256) ||
            string.IsNullOrWhiteSpace(doc.Signature))
        {
            return false;
        }

        byte[] preimageBytes = BuildPromptPreimageBytes(doc);
        return VerifyPreimageSignature(preimageBytes, doc.ContentSha256, doc.Signature, publicKeyPem);
    }

    private static bool MatchesManifestRootId(string manifestJson, string expectedNormId)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifestJson, PermissiveJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("id", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String)
            {
                return true;
            }

            string rawManifestId = idEl.GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(rawManifestId) ||
                   string.Equals(NormalizeCatalogEntryId(rawManifestId), expectedNormId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyPreimageSignature(
        byte[] preimageBytes,
        string expectedSha256Hex,
        string signatureHex,
        string? publicKeyPem)
    {
        try
        {
            byte[] expectedDigest = Convert.FromHexString(expectedSha256Hex.Trim());
            byte[] signature = Convert.FromHexString(signatureHex.Trim());
            if (expectedDigest.Length != SHA256.HashSizeInBytes)
            {
                return false;
            }

            byte[] actualDigest = SHA256.HashData(preimageBytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            {
                return false;
            }

            using ECDsa publicKeys = string.IsNullOrWhiteSpace(publicKeyPem)
                ? ECDsa.Create(DefaultPublicKeyParams.Value)
                : CreateFromPem(publicKeyPem);
            return publicKeys.VerifyData(
                preimageBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }

    private static ECDsa CreateFromPem(string publicKeyPem)
    {
        ECDsa ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }
}
