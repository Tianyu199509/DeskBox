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
    Uri? RemoteManifestUri = null);

/// <summary>
/// Parses, validates, forwards, and resolves <c>deskbox://</c> deep-link protocol activations
/// (<c>deskbox://studio</c> and <c>deskbox://install-widget</c>).
/// </summary>
public static class DeskBoxProtocolActivationService
{
    public const string Scheme = "deskbox";
    private const int MaxRemoteManifestBytes = 512 * 1024;
    private static readonly Lazy<HttpClient> DefaultHttpClient = new(CreateDefaultHttpClient);

    private static string PendingProtocolUriPath =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, "pending-protocol-uri.txt");

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(6)
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
            }
        }

        return null;
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

                errorMessage = "Missing 'template' or 'url' query parameter in deskbox://install-widget.";
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
                return (null, null, Localized.Format("CustomWidget.Protocol.TemplateNotFoundFormat", tplId));
            }

            string json = CustomWidgetManifestMetadataStamper.StampProvenance(
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(tplId),
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
            return (json, parsed.Manifest?.ResolveLocalizedName(Localized.ActiveCultureName) ?? tplId, null);
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
                    return (null, null, Localized.Format("CustomWidget.Protocol.RemoteHttpErrorFormat", (int)resp.StatusCode));
                }

                await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxRemoteManifestBytes)
                    {
                        return (null, null, Localized.T("CustomWidget.Protocol.RemoteTooLarge"));
                    }

                    ms.Write(buffer, 0, read);
                }

                string rawJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawJson);
                if (CustomWidgetManifestParser.IsIncompatibleClientVersion(parsed.Diagnostics))
                {
                    string upgradeMsg = parsed.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message
                                        ?? Localized.T("CustomWidget.Protocol.ClientUpgradeRequired");
                    return (null, null, upgradeMsg);
                }

                if (parsed.Manifest is null || parsed.Diagnostics.Any(static d => d.IsError))
                {
                    string err = parsed.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message
                                 ?? Localized.T("CustomWidget.Protocol.RemoteValidationFailed");
                    return (null, null, err);
                }

                // External deep-link manifest downloads are always treated as community
                // provenance even when hosted on an allowed domain or carrying a "deskbox.*" id.
                string stamped = CustomWidgetManifestMetadataStamper.StampProvenance(
                    rawJson,
                    origin: CustomWidgetOriginKinds.Community,
                    isCustomized: false);
                return (stamped, parsed.Manifest.ResolveLocalizedName(Localized.ActiveCultureName), null);
            }
            catch (Exception ex)
            {
                return (null, null, ex.Message);
            }
            finally
            {
                if (customHandler is not null)
                {
                    client.Dispose();
                }
            }
        }

        return (null, null, Localized.T("CustomWidget.Protocol.InvalidLink"));
    }

    private static bool IsKnownLocalOrCachedTemplate(string templateId)
    {
        string norm = templateId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? templateId["deskbox.".Length..]
            : templateId;
        if (CustomWidgetTemplates.MatchPresetOrNull(norm) is not null)
        {
            return true;
        }

        if (CustomWidgetTemplates.StudioStarters.Any(s =>
                string.Equals(s.Id, norm, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (norm.Equals(CustomWidgetTemplates.WorkLoggerTemplateId, StringComparison.OrdinalIgnoreCase) ||
            norm.Equals(CustomWidgetTemplates.MilestoneTrackerTemplateId, StringComparison.OrdinalIgnoreCase) ||
            norm.Equals(CustomWidgetTemplates.QuickSnippetsTemplateId, StringComparison.OrdinalIgnoreCase))
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
