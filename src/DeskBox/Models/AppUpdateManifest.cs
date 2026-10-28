namespace DeskBox.Models;

public sealed class AppUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>
    /// Optional architecture-specific installer metadata. Older manifests can
    /// continue to use the primary fields for a single architecture.
    /// </summary>
    public string Arm64DownloadUrl { get; set; } = string.Empty;
    public string ManualDownloadUrl { get; set; } = string.Empty;
    public string MirrorUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Arm64Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public long Arm64Size { get; set; }
    public string ReleaseNotesUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Summary { get; set; } = [];
    /// <summary>
    /// Full release notes in Markdown, keyed by locale. This is optional so
    /// older manifests remain compatible with clients that only understand
    /// the short summary fields.
    /// </summary>
    public Dictionary<string, string> ReleaseNotes { get; set; } = [];

    public string GetLocalizedSummary(string cultureName)
    {
        string localized = GetValueForLocale(Summary, cultureName);
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        // Non-Chinese locales should never inherit a Chinese-only summary when
        // the server omits their exact translation. Prefer the English
        // fallback, then use any available value for compatibility with older
        // manifests that predate the English entry.
        string english = GetExactValue(Summary, "en-US");
        if (!string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        return Summary.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    /// <summary>
    /// Returns the exact or language-level release note for a requested
    /// locale. This method is used when the user explicitly switches the
    /// language in the release-notes window.
    /// </summary>
    public string GetReleaseNotesForLocale(string cultureName)
    {
        return GetValueForLocale(ReleaseNotes, cultureName);
    }

    /// <summary>
    /// Returns the best available release note for the requested locale.
    /// Exact and language-level translations are preferred, followed by
    /// Chinese for Chinese users and English for every other locale.
    /// </summary>
    public string GetLocalizedReleaseNotes(string cultureName)
    {
        string localized = GetReleaseNotesForLocale(cultureName);
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        return GetExactValue(ReleaseNotes, "en-US");
    }

    private static string GetValueForLocale(
        IReadOnlyDictionary<string, string> values,
        string cultureName)
    {
        if (values.Count == 0 || string.IsNullOrWhiteSpace(cultureName))
        {
            return string.Empty;
        }

        string exact = GetExactValue(values, cultureName);
        if (!string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        string normalized = cultureName.Replace('_', '-');
        string language = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? normalized;
        string neutral = GetExactValue(values, language);
        if (!string.IsNullOrWhiteSpace(neutral))
        {
            return neutral;
        }

        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            string[] preferredLocales = IsTraditionalChineseLocale(normalized)
                ? ["zh-TW", "zh-Hant", "zh-HK", "zh-MO", "zh-CN", "zh-Hans", "zh-SG"]
                : ["zh-CN", "zh-Hans", "zh-SG", "zh-TW", "zh-Hant", "zh-HK", "zh-MO"];

            foreach (string locale in preferredLocales)
            {
                string value = GetExactValue(values, locale);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return values.FirstOrDefault(pair =>
            pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase) ||
            pair.Key.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
    }

    private static string GetExactValue(
        IReadOnlyDictionary<string, string> values,
        string locale)
    {
        return values.FirstOrDefault(pair =>
            pair.Key.Equals(locale, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value)).Value ?? string.Empty;
    }

    private static bool IsTraditionalChineseLocale(string cultureName)
    {
        return cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasReleaseNotes => ReleaseNotes.Values.Any(value => !string.IsNullOrWhiteSpace(value));

    public bool HasReleaseNotesOrUrl => HasReleaseNotes || IsSafeReleaseNotesUrl(ReleaseNotesUrl);

    public static bool IsSafeReleaseNotesUrl(string? url)
    {
        return IsSafeWebUrl(url);
    }

    public static bool IsSafeWebUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}

public enum AppUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    InvalidManifest,
    Failed
}

public sealed class AppUpdateCheckResult
{
    public AppUpdateCheckResult(
        AppUpdateCheckStatus status,
        string currentVersion,
        AppUpdateManifest? manifest = null,
        string? errorMessage = null)
    {
        Status = status;
        CurrentVersion = currentVersion;
        Manifest = manifest;
        ErrorMessage = errorMessage;
    }

    public AppUpdateCheckStatus Status { get; }
    public string CurrentVersion { get; }
    public AppUpdateManifest? Manifest { get; }
    public string? ErrorMessage { get; }
    public bool IsUpdateAvailable => Status == AppUpdateCheckStatus.UpdateAvailable && Manifest is not null;
}

public sealed class AppUpdateDownloadProgress
{
    public AppUpdateDownloadProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }

    public long BytesReceived { get; }
    public long? TotalBytes { get; }
    public double Percent =>
        TotalBytes is > 0
            ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
            : 0;
}

public enum AppUpdateDownloadFailureKind
{
    None,
    InvalidManifest,
    HashMissing,
    HashMismatch,
    Network,
    FileSystem,
    Cancelled
}

public sealed class AppUpdateDownloadResult
{
    // JsonConstructor: the settings-host IPC pipe deserializes download
    // results in the second process (batch 3c-3a); the parameter names
    // mirror the properties for source-generated mapping.
    [System.Text.Json.Serialization.JsonConstructor]
    internal AppUpdateDownloadResult(
        bool success,
        string? filePath,
        AppUpdateDownloadFailureKind failureKind,
        string? errorMessage)
    {
        Success = success;
        FilePath = filePath;
        FailureKind = failureKind;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? FilePath { get; }
    public AppUpdateDownloadFailureKind FailureKind { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateDownloadResult Completed(string filePath) =>
        new(true, filePath, AppUpdateDownloadFailureKind.None, null);

    public static AppUpdateDownloadResult Failed(AppUpdateDownloadFailureKind failureKind, string? errorMessage = null) =>
        new(false, null, failureKind, errorMessage);
}

public sealed class AppUpdateInstallResult
{
    // JsonConstructor: see AppUpdateDownloadResult (batch 3c-3a).
    [System.Text.Json.Serialization.JsonConstructor]
    internal AppUpdateInstallResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateInstallResult Started() => new(true, null);

    public static AppUpdateInstallResult Failed(string errorMessage) => new(false, errorMessage);
}
