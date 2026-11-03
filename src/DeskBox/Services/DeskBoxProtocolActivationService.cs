using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum DeskBoxProtocolAction
{
    OpenStudio,
    InstallWidget
}

public sealed record DeskBoxProtocolIntent(
    DeskBoxProtocolAction Action,
    string? Idea = null,
    string? WidgetId = null,
    string? TemplateId = null,
    Uri? RemoteManifestUri = null,
    string? LocalPackageFilePath = null);

public sealed record DeskBoxProtocolResolvedBundle(
    string? ManifestJson,
    string? LogicJs,
    CustomWidgetLocaleCatalog? Locales,
    string? DisplayLabel,
    string OriginKind,
    string? ErrorMessage);

/// <summary>
/// Parses, validates, forwards, and resolves <c>deskbox://</c> deep-link protocol activations
/// (<c>deskbox://studio</c> and <c>deskbox://install-widget</c>).
/// </summary>
public static class DeskBoxProtocolActivationService
{
    public const string Scheme = "deskbox";
    private const int MaxRemoteManifestBytes = 512 * 1024;
    private const int MaxRemotePackageBytes = 4 * 1024 * 1024;
    private static readonly Lazy<HttpClient> DefaultHttpClient = new(CreateDefaultHttpClient);

    private static string PendingProtocolUriPath =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, "pending-protocol-uri.txt");

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedPublicIpAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox", "1.0"));
        return client;
    }

    public static string? TryExtractProtocolUri(
        IReadOnlyList<string>? commandLineArgs,
        string? packagedLaunchArgs = null)
    {
        if (commandLineArgs is not null)
        {
            for (int i = 1; i < commandLineArgs.Count; i++)
            {
                string candidate = commandLineArgs[i].Trim().Trim('"');
                if (candidate.StartsWith("deskbox://", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (TryBuildLocalPackageProtocolUri(candidate, out string? pkgUri))
                {
                    return pkgUri;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(packagedLaunchArgs))
        {
            foreach (string token in SplitLooseCommandLine(packagedLaunchArgs))
            {
                if (token.StartsWith("deskbox://", StringComparison.OrdinalIgnoreCase))
                {
                    return token;
                }

                if (TryBuildLocalPackageProtocolUri(token, out string? pkgUri))
                {
                    return pkgUri;
                }
            }
        }

        return null;
    }

    private static bool IsSupportedLocalPackageExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".deskbox-widget", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".deskboxwidget", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".dbbox", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeLocalDrivePath(string rawPath, out string normalizedFullPath)
    {
        normalizedFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) ||
            rawPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            rawPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(rawPath.Trim());
            if (full.Length < 3 ||
                !char.IsLetter(full[0]) ||
                full[1] != ':' ||
                (full[2] != '\\' && full[2] != '/'))
            {
                return false;
            }

            normalizedFullPath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildLocalPackageProtocolUri(string candidate, out string? protocolUri)
    {
        protocolUri = null;
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith("-", StringComparison.Ordinal) ||
            !IsSupportedLocalPackageExtension(candidate))
        {
            return false;
        }

        if (!IsSafeLocalDrivePath(candidate, out string fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        protocolUri = $"deskbox://install-widget?file={Uri.EscapeDataString(fullPath)}";
        return true;
    }

    public static bool TryParseProtocolUri(
        string? rawUri,
        out DeskBoxProtocolIntent? intent,
        out string? errorMessage)
    {
        intent = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawUri))
        {
            errorMessage = "Empty protocol URI.";
            return false;
        }

        if (!Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Unsupported URI scheme in '{rawUri}'.";
            return false;
        }

        string route = !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host.Trim().ToLowerInvariant()
            : uri.AbsolutePath.Trim('/').ToLowerInvariant();

        Dictionary<string, string> query = ParseQueryString(uri.Query);

        switch (route)
        {
            case "studio":
            case "workshop":
            {
                query.TryGetValue("idea", out string? idea);
                if (string.IsNullOrWhiteSpace(idea))
                {
                    query.TryGetValue("prompt", out idea);
                }

                query.TryGetValue("widgetid", out string? widgetId);
                intent = new DeskBoxProtocolIntent(
                    DeskBoxProtocolAction.OpenStudio,
                    Idea: string.IsNullOrWhiteSpace(idea) ? null : idea.Trim(),
                    WidgetId: string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim());
                return true;
            }

            case "install-widget":
            case "preview-widget":
            {
                if (query.TryGetValue("url", out string? rawUrl) && !string.IsNullOrWhiteSpace(rawUrl))
                {
                    if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(rawUrl, out Uri? httpsUri, out string? urlErr) ||
                        httpsUri is null)
                    {
                        errorMessage = urlErr ?? "Invalid HTTPS manifest URL.";
                        return false;
                    }

                    if (!CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(httpsUri))
                    {
                        errorMessage = $"Manifest URL host '{httpsUri.DnsSafeHost}' is not in the trusted DeskBox official allowlist.";
                        return false;
                    }

                    intent = new DeskBoxProtocolIntent(
                        DeskBoxProtocolAction.InstallWidget,
                        RemoteManifestUri: httpsUri);
                    return true;
                }

                if ((query.TryGetValue("template", out string? tpl) || query.TryGetValue("id", out tpl)) &&
                    !string.IsNullOrWhiteSpace(tpl))
                {
                    intent = new DeskBoxProtocolIntent(
                        DeskBoxProtocolAction.InstallWidget,
                        TemplateId: tpl.Trim());
                    return true;
                }

                if (query.TryGetValue("file", out string? rawFile) && !string.IsNullOrWhiteSpace(rawFile))
                {
                    if (!IsSupportedLocalPackageExtension(rawFile) ||
                        !IsSafeLocalDrivePath(rawFile, out string safeFullPath))
                    {
                        errorMessage = "Invalid or unsupported local widget package path.";
                        return false;
                    }

                    intent = new DeskBoxProtocolIntent(
                        DeskBoxProtocolAction.InstallWidget,
                        LocalPackageFilePath: safeFullPath);
                    return true;
                }

                errorMessage = "Missing 'template', 'url', or 'file' query parameter in deskbox://install-widget.";
                return false;
            }

            default:
            {
                errorMessage = $"Unknown deskbox:// action '{route}'.";
                return false;
            }
        }
    }

    public static void StorePendingProtocolUri(string protocolUri)
    {
        if (string.IsNullOrWhiteSpace(protocolUri))
        {
            return;
        }

        try
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(PendingProtocolUriPath, protocolUri.Trim());
        }
        catch
        {
            // Ignore best-effort IPC handoff write failure
        }
    }

    public static string? TakePendingProtocolUri()
    {
        try
        {
            if (!File.Exists(PendingProtocolUriPath))
            {
                return null;
            }

            string raw = File.ReadAllText(PendingProtocolUriPath, Encoding.UTF8).Trim();
            CustomWidgetStorageFileOps.DeleteFileIfExists(PendingProtocolUriPath);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(string? ManifestJson, string? DisplayLabel, string? ErrorMessage)> ResolveInstallWidgetManifestAsync(
        DeskBoxProtocolIntent intent,
        HttpMessageHandler? customHandler = null,
        CancellationToken ct = default)
    {
        DeskBoxProtocolResolvedBundle bundle = await ResolveInstallWidgetBundleAsync(intent, customHandler, ct)
            .ConfigureAwait(false);
        return (bundle.ManifestJson, bundle.DisplayLabel, bundle.ErrorMessage);
    }

    public static async Task<DeskBoxProtocolResolvedBundle> ResolveInstallWidgetBundleAsync(
        DeskBoxProtocolIntent intent,
        HttpMessageHandler? customHandler = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(intent.LocalPackageFilePath))
        {
            try
            {
                CustomWidgetImportedBundle imported = await CustomWidgetPackageService
                    .ReadPackageBundleAsync(intent.LocalPackageFilePath)
                    .ConfigureAwait(false);
                return new DeskBoxProtocolResolvedBundle(
                    imported.RawJson,
                    imported.LogicJs,
                    imported.Locales,
                    Path.GetFileNameWithoutExtension(intent.LocalPackageFilePath),
                    CustomWidgetOriginKinds.Community,
                    null);
            }
            catch (Exception ex)
            {
                return new DeskBoxProtocolResolvedBundle(
                    null,
                    null,
                    null,
                    null,
                    CustomWidgetOriginKinds.Community,
                    ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(intent.TemplateId))
        {
            string tplId = intent.TemplateId.Trim();
            if (!IsKnownLocalOrCachedTemplate(tplId))
            {
                await CustomWidgetRemoteCatalogService.Shared.RefreshInBackgroundAsync(force: true, ct)
                    .ConfigureAwait(false);
            }

            if (!IsKnownLocalOrCachedTemplate(tplId))
            {
                return new DeskBoxProtocolResolvedBundle(
                    null,
                    null,
                    null,
                    null,
                    CustomWidgetOriginKinds.Official,
                    Localized.Format("CustomWidget.Protocol.TemplateNotFoundFormat", tplId));
            }

            CustomWidgetTemplateBundle tplBundle = CustomWidgetTemplates.ResolveEffectiveTemplateBundle(tplId);
            string json = tplBundle.ManifestJson;
            if (!string.IsNullOrWhiteSpace(tplBundle.LogicJs))
            {
                json = CustomWidgetTemplates.SyncManifestScriptPropertyJson(json, tplBundle.LogicJs);
            }

            json = CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
            CustomWidgetManifest? localizedManifest = parsed.Manifest is not null && tplBundle.Locales is { HasAnyLocale: true }
                ? parsed.Manifest.WithLocales(tplBundle.Locales)
                : parsed.Manifest;
            string displayLabel = localizedManifest?.ResolveLocalizedName(Localized.ActiveCultureName) ?? tplId;
            return new DeskBoxProtocolResolvedBundle(
                json,
                tplBundle.LogicJs,
                tplBundle.Locales,
                displayLabel,
                CustomWidgetOriginKinds.Official,
                null);
        }

        if (intent.RemoteManifestUri is not null)
        {
            HttpClient client = customHandler is not null
                ? new HttpClient(customHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) }
                : DefaultHttpClient.Value;
            try
            {
                using HttpResponseMessage resp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                    client,
                    intent.RemoteManifestUri,
                    requireTrustedOfficialHost: true,
                    cancellationToken: ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new DeskBoxProtocolResolvedBundle(
                        null,
                        null,
                        null,
                        null,
                        CustomWidgetOriginKinds.Community,
                        Localized.Format("CustomWidget.Protocol.RemoteHttpErrorFormat", (int)resp.StatusCode));
                }

                await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                int total = 0;
                bool? isZipPayload = null;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    ms.Write(buffer, 0, read);
                    total += read;
                    if (isZipPayload is null && ms.Length >= 4)
                    {
                        byte[] header = ms.GetBuffer();
                        isZipPayload = header[0] == 0x50 &&
                                       header[1] == 0x4B &&
                                       header[2] == 0x03 &&
                                       header[3] == 0x04;
                    }

                    int maxBytes = isZipPayload == true ? MaxRemotePackageBytes : MaxRemoteManifestBytes;
                    if (total > maxBytes)
                    {
                        return new DeskBoxProtocolResolvedBundle(
                            null,
                            null,
                            null,
                            null,
                            CustomWidgetOriginKinds.Community,
                            Localized.T("CustomWidget.Protocol.RemoteTooLarge"));
                    }
                }

                CustomWidgetImportedBundle imported = await CustomWidgetPackageService
                    .ReadPackageBundleFromBytesAsync(ms.ToArray(), markExternalAsCommunity: true)
                    .ConfigureAwait(false);
                string stamped = CustomWidgetManifestMetadataStamper.StampProvenance(
                    imported.RawJson,
                    origin: CustomWidgetOriginKinds.Community,
                    isCustomized: false);
                return new DeskBoxProtocolResolvedBundle(
                    stamped,
                    imported.LogicJs,
                    imported.Locales,
                    imported.Manifest.ResolveLocalizedName(Localized.ActiveCultureName),
                    CustomWidgetOriginKinds.Community,
                    null);
            }
            catch (CustomWidgetIncompatibleVersionException ex)
            {
                return new DeskBoxProtocolResolvedBundle(
                    null,
                    null,
                    null,
                    null,
                    CustomWidgetOriginKinds.Community,
                    string.IsNullOrWhiteSpace(ex.Message)
                        ? Localized.T("CustomWidget.Protocol.ClientUpgradeRequired")
                        : ex.Message);
            }
            catch (Exception ex)
            {
                return new DeskBoxProtocolResolvedBundle(
                    null,
                    null,
                    null,
                    null,
                    CustomWidgetOriginKinds.Community,
                    ex.Message);
            }
            finally
            {
                if (customHandler is not null)
                {
                    client.Dispose();
                }
            }
        }

        return new DeskBoxProtocolResolvedBundle(
            null,
            null,
            null,
            null,
            CustomWidgetOriginKinds.Community,
            Localized.T("CustomWidget.Protocol.InvalidLink"));
    }

    private static bool IsKnownLocalOrCachedTemplate(string templateId)
    {
        string norm = templateId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? templateId["deskbox.".Length..]
            : templateId;
        if (CustomWidgetTemplates.IsKnownBuiltInTemplate(norm))
        {
            return true;
        }

        return CustomWidgetRemoteCatalogService.Shared
            .GetMergedOfficialTemplates()
            .Any(e => !e.RequiresClientUpgrade &&
                      (string.Equals(e.Id, norm, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(e.ManifestId, templateId, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, string> ParseQueryString(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        string trimmed = query.TrimStart('?');
        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            string k = Uri.UnescapeDataString(part[..eq].Replace('+', ' '));
            string v = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result[k] = v;
        }

        return result;
    }

    private static IEnumerable<string> SplitLooseCommandLine(string raw)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in raw)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
