using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DeskBox.Services;

internal readonly record struct DirectUrlMatch(
    string NormalizedUri,
    string DisplayText);

internal readonly record struct ExplicitWebSearchMatch(
    string Query,
    string EngineName,
    string TargetUri);

/// <summary>
/// Pure, NativeAOT-safe evaluator for direct URL opening, explicit search-engine
/// prefixes (e.g. "? query", "g: query", "bd: query", "gh: query"), and fallback
/// web search in the global command palette.
/// </summary>
internal static class SearchWebAndUrlEvaluator
{
    public const string OpenUrlActionPrefix = "open-url:";
    public const string WebSearchActionPrefix = "web-search:";

    private static readonly HashSet<string> s_knownWebTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "cn", "net", "org", "io", "dev", "ai", "app", "me", "co", "cc", "tv",
        "xyz", "top", "tech", "site", "online", "store", "cloud", "info", "biz", "edu",
        "gov", "mil", "int", "uk", "jp", "kr", "de", "fr", "ru", "au", "ca", "br", "in",
        "hk", "tw", "sg", "gg", "sh", "so", "to", "ly", "gl", "fm", "im", "do", "design",
        "tools", "run", "page", "link", "wiki", "news", "blog", "chat", "pub", "live",
        "pro", "vip", "ltd", "work", "moe", "ink", "fun", "us", "eu", "nl", "ch", "se",
        "no", "fi", "dk", "it", "es", "pl", "cz", "nz", "mx", "ar", "za", "id", "my",
        "vn", "th", "ph"
    };

    private static readonly HashSet<string> s_localFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "msi", "msix", "appx", "bat", "cmd", "ps1", "vbs", "dll", "sys",
        "txt", "md", "markdown", "log", "ini", "cfg", "conf", "json", "jsonc", "xml",
        "yaml", "yml", "toml", "csv", "tsv", "sql", "db", "sqlite",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "rtf", "odt", "epub",
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "heic", "psd",
        "mp3", "wav", "flac", "aac", "ogg", "m4a", "mp4", "mkv", "avi", "mov", "wmv", "webm",
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab",
        "cs", "csproj", "sln", "xaml", "cpp", "c", "h", "hpp", "rs", "py", "js", "ts",
        "jsx", "tsx", "vue", "svelte", "java", "kt", "swift", "rb", "php", "css", "scss",
        "less", "bak", "tmp", "old", "lock", "cer", "pfx", "pem", "key"
    };

    public static bool TryEvaluateDirectUrl(string? rawInput, out DirectUrlMatch match)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        string trimmed = rawInput.Trim();
        if (trimmed.Length < 4 || trimmed.Length > 2048 || ContainsWhitespaceOrControl(trimmed))
        {
            return false;
        }

        // Reject Windows drive paths (C:\..., D:/...), UNC paths (\\...), and env vars (%...%).
        if (IsWindowsFileSystemPath(trimmed))
        {
            return false;
        }

        // 1. Explicit http:// or https://
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? explicitUri) &&
                IsPlausibleHttpHost(explicitUri.Host, hasExplicitScheme: true))
            {
                match = new DirectUrlMatch(explicitUri.AbsoluteUri, trimmed);
                return true;
            }

            return false;
        }

        // 2. Explicit mailto:user@domain.tld
        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            string address = trimmed["mailto:".Length..];
            if (IsPlausibleEmail(address) &&
                Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? mailUri))
            {
                match = new DirectUrlMatch(mailUri.AbsoluteUri, trimmed);
                return true;
            }

            return false;
        }

        // 3. Explicit Windows settings URI: ms-settings:display, ms-settings:windowsupdate, etc.
        if (trimmed.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string page = trimmed["ms-settings:".Length..];
            if (IsSafeMsSettingsTarget(page))
            {
                match = new DirectUrlMatch(trimmed, trimmed);
                return true;
            }

            return false;
        }

        // Reject other colon prefixes (inline scopes like "app:foo", "f:bar", etc.) unless host:port.
        int slashIndex = trimmed.IndexOfAny(['/', '?', '#']);
        string authority = slashIndex >= 0 ? trimmed[..slashIndex] : trimmed;

        string hostPart = authority;
        if (authority.Contains(':'))
        {
            int colonIdx = authority.LastIndexOf(':');
            string portText = authority[(colonIdx + 1)..];
            if (!ushort.TryParse(portText, out ushort port) || port == 0)
            {
                return false;
            }

            hostPart = authority[..colonIdx];
        }

        if (string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        // 4. localhost:port or localhost/path
        if (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (!authority.Contains(':') && slashIndex < 0)
            {
                return false;
            }

            string candidate = $"http://{trimmed}";
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? localUri))
            {
                match = new DirectUrlMatch(localUri.AbsoluteUri, trimmed);
                return true;
            }

            return false;
        }

        // 5. IPv4 address (e.g. 192.168.1.1, 127.0.0.1:8080/api)
        if (IsStrictIpv4(hostPart))
        {
            string candidate = $"http://{trimmed}";
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? ipUri))
            {
                match = new DirectUrlMatch(ipUri.AbsoluteUri, trimmed);
                return true;
            }

            return false;
        }

        // 6. Bare domain (e.g. github.com, github.com/microsoft/PowerToys, docs.microsoft.com)
        if (IsPlausibleBareDomain(hostPart, hasPathOrPort: slashIndex >= 0 || authority.Contains(':')))
        {
            string candidate = $"https://{trimmed}";
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? domainUri))
            {
                match = new DirectUrlMatch(domainUri.AbsoluteUri, trimmed);
                return true;
            }
        }

        return false;
    }

    public const string EngineBing = "bing";
    public const string EngineGoogle = "google";
    public const string EngineBaidu = "baidu";
    public const string EngineDuckDuckGo = "duckduckgo";
    public const string EngineCustom = "custom";

    public static string NormalizeEngineId(string? engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return EngineBing;
        }

        string normalized = engineId.Trim().ToLowerInvariant();
        return normalized is EngineBing or EngineGoogle or EngineBaidu or EngineDuckDuckGo or EngineCustom
            ? normalized
            : EngineBing;
    }

    public static string ResolveEngineDisplayName(
        string? engineId,
        string? customUrlTemplate = null,
        string? localizedCustomLabel = null)
    {
        string normalized = NormalizeEngineId(engineId);
        return normalized switch
        {
            EngineGoogle => "Google",
            EngineBaidu => "Baidu",
            EngineDuckDuckGo => "DuckDuckGo",
            EngineCustom => TryBuildCustomSearchUri("deskbox", customUrlTemplate, out string sampleUri) &&
                            Uri.TryCreate(sampleUri, UriKind.Absolute, out Uri? parsed) &&
                            !string.IsNullOrWhiteSpace(parsed.Host)
                ? parsed.Host
                : (string.IsNullOrWhiteSpace(localizedCustomLabel) ? "Bing" : localizedCustomLabel!),
            _ => "Bing"
        };
    }

    public static bool TryEvaluateExplicitWebSearch(
        string? rawInput,
        out ExplicitWebSearchMatch match,
        string? defaultEngineId = EngineBing,
        string? customUrlTemplate = null)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        string trimmed = rawInput.Trim();
        if (trimmed.StartsWith('?') || trimmed.StartsWith('？'))
        {
            string query = trimmed[1..].Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string effectiveName = ResolveEngineDisplayName(defaultEngineId, customUrlTemplate);
            match = new ExplicitWebSearchMatch(
                query,
                effectiveName,
                BuildFallbackWebSearchUri(query, defaultEngineId, customUrlTemplate));
            return true;
        }

        int colonIndex = trimmed.IndexOfAny([':', '：']);
        if (colonIndex <= 0)
        {
            return false;
        }

        string prefix = trimmed[..colonIndex].Trim().ToLowerInvariant();
        string keyword = trimmed[(colonIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        string encoded = Uri.EscapeDataString(keyword);
        switch (prefix)
        {
            case "web":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    ResolveEngineDisplayName(defaultEngineId, customUrlTemplate),
                    BuildFallbackWebSearchUri(keyword, defaultEngineId, customUrlTemplate));
                return true;

            case "bing":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "Bing",
                    $"https://www.bing.com/search?q={encoded}");
                return true;

            case "g":
            case "google":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "Google",
                    $"https://www.google.com/search?q={encoded}");
                return true;

            case "bd":
            case "baidu":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "Baidu",
                    $"https://www.baidu.com/s?wd={encoded}");
                return true;

            case "ddg":
            case "duckduckgo":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "DuckDuckGo",
                    $"https://duckduckgo.com/?q={encoded}");
                return true;

            case "gh":
            case "github":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "GitHub",
                    $"https://github.com/search?q={encoded}");
                return true;

            case "bili":
            case "bilibili":
                match = new ExplicitWebSearchMatch(
                    keyword,
                    "Bilibili",
                    $"https://search.bilibili.com/all?keyword={encoded}");
                return true;

            default:
                return false;
        }
    }

    public static string BuildFallbackWebSearchUri(
        string query,
        string? engineId = EngineBing,
        string? customUrlTemplate = null)
    {
        string normalized = (query ?? string.Empty).Trim();
        string encoded = Uri.EscapeDataString(normalized);

        switch (NormalizeEngineId(engineId))
        {
            case EngineGoogle:
                return $"https://www.google.com/search?q={encoded}";

            case EngineBaidu:
                return $"https://www.baidu.com/s?wd={encoded}";

            case EngineDuckDuckGo:
                return $"https://duckduckgo.com/?q={encoded}";

            case EngineCustom:
                if (TryBuildCustomSearchUri(normalized, customUrlTemplate, out string customUri))
                {
                    return customUri;
                }
                return $"https://www.bing.com/search?q={encoded}";

            default:
                return $"https://www.bing.com/search?q={encoded}";
        }
    }

    public static bool TryBuildCustomSearchUri(
        string query,
        string? customUrlTemplate,
        out string safeUri)
    {
        safeUri = string.Empty;
        if (string.IsNullOrWhiteSpace(customUrlTemplate))
        {
            return false;
        }

        string template = customUrlTemplate.Trim();
        if (!template.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !template.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string encoded = Uri.EscapeDataString((query ?? string.Empty).Trim());
        string candidate;
        if (template.Contains("{q}", StringComparison.OrdinalIgnoreCase))
        {
            candidate = template.Replace("{q}", encoded, StringComparison.OrdinalIgnoreCase);
        }
        else if (template.Contains("{query}", StringComparison.OrdinalIgnoreCase))
        {
            candidate = template.Replace("{query}", encoded, StringComparison.OrdinalIgnoreCase);
        }
        else if (template.Contains("%s", StringComparison.OrdinalIgnoreCase))
        {
            candidate = template.Replace("%s", encoded, StringComparison.OrdinalIgnoreCase);
        }
        else if (template.EndsWith('=') || template.EndsWith('/') || template.EndsWith('?'))
        {
            candidate = string.Concat(template, encoded);
        }
        else
        {
            string separator = template.Contains('?') ? "&q=" : "?q=";
            candidate = string.Concat(template, separator, encoded);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsPlausibleHttpHost(uri.Host, hasExplicitScheme: true))
        {
            return false;
        }

        safeUri = uri.AbsoluteUri;
        return true;
    }

    public static bool ShouldOfferFallbackWebSearch(string? effectiveQuery)
    {
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            return false;
        }

        string trimmed = effectiveQuery.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 256 || IsWindowsFileSystemPath(trimmed))
        {
            return false;
        }

        return true;
    }

    public static bool TryNormalizeSafeExternalUri(string? candidate, out string safeUri)
    {
        safeUri = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string trimmed = candidate.Trim();
        if (trimmed.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string page = trimmed["ms-settings:".Length..];
            if (IsSafeMsSettingsTarget(page))
            {
                safeUri = trimmed;
                return true;
            }

            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            safeUri = uri.AbsoluteUri;
            return true;
        }

        return false;
    }

    public static bool TryLaunchSafeExternalUri(string? candidate)
    {
        if (!TryNormalizeSafeExternalUri(candidate, out string safeUri))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = safeUri,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Search] Failed to launch URI '{safeUri}': {ex.Message}");
            return false;
        }
    }

    public static bool IsWindowsFileSystemPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) ||
            text.StartsWith('%') ||
            text.Contains('\\'))
        {
            return true;
        }

        return text.Length >= 3 &&
               char.IsAsciiLetter(text[0]) &&
               text[1] == ':' &&
               (text[2] == '\\' || text[2] == '/');
    }

    private static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeMsSettingsTarget(string page)
    {
        if (page.Length > 128)
        {
            return false;
        }

        foreach (char c in page)
        {
            if (!char.IsAsciiLetterOrDigit(c) &&
                c is not ('-' or '_' or ':' or '?' or '=' or '&'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlausibleEmail(string address)
    {
        int at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at >= address.Length - 3)
        {
            return false;
        }

        string domain = address[(at + 1)..];
        return IsPlausibleBareDomain(domain, hasPathOrPort: true);
    }

    private static bool IsPlausibleHttpHost(string host, bool hasExplicitScheme)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IsStrictIpv4(host) ||
            Uri.CheckHostName(host) == UriHostNameType.IPv6)
        {
            return true;
        }

        if (hasExplicitScheme)
        {
            return host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.');
        }

        return IsPlausibleBareDomain(host, hasPathOrPort: false);
    }

    private static bool IsPlausibleBareDomain(string host, bool hasPathOrPort)
    {
        if (host.Length < 4 || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string[] labels = host.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (string label in labels)
        {
            if (label.Length == 0 || label.Length > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        string tld = labels[^1];
        if (!hasPathOrPort && s_localFileExtensions.Contains(tld))
        {
            return false;
        }

        if (tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) && tld.Length >= 6)
        {
            return true;
        }

        return s_knownWebTlds.Contains(tld);
    }

    private static bool IsStrictIpv4(string text)
    {
        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3 || !byte.TryParse(part, out _))
            {
                return false;
            }
        }

        return true;
    }
}
