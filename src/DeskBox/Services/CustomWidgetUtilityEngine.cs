using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Launcher = Windows.System.Launcher;

namespace DeskBox.Services;

public sealed record CustomWidgetClipboardSnapshot
{
    public static CustomWidgetClipboardSnapshot Empty { get; } = new();

    public string Text { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public int Length { get; init; }
    public int LineCount { get; init; }
    public string Kind { get; init; } = "empty";

    public static CustomWidgetClipboardSnapshot FromText(string? raw) => CreateFromText(raw);

    public static CustomWidgetClipboardSnapshot CreateFromText(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Empty;
        }

        string capped = raw.Length > 65536 ? raw[..65536] : raw;
        string trimmed = capped.Trim();
        if (trimmed.Length == 0)
        {
            return Empty;
        }

        int lines = 1;
        for (int i = 0; i < capped.Length; i++)
        {
            if (capped[i] == '\n')
            {
                lines++;
            }
        }

        string singleLine = trimmed
            .Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace('\n', '↵')
            .Replace('\r', ' ');
        if (singleLine.Length > 120)
        {
            singleLine = singleLine[..120] + "…";
        }

        string kind = ClassifyClipboardText(trimmed);
        return new CustomWidgetClipboardSnapshot
        {
            Text = capped,
            Preview = singleLine,
            Length = capped.Length,
            LineCount = lines,
            Kind = kind
        };
    }

    public static async Task<CustomWidgetClipboardSnapshot> ReadSystemClipboardAsync()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (content is not null && content.Contains(StandardDataFormats.Text))
            {
                string text = await content.GetTextAsync().AsTask().ConfigureAwait(true);
                return CreateFromText(text);
            }
        }
        catch
        {
        }

        return Empty;
    }

    public string ResolveToken(string path, string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        bool isZh = (cultureName ?? LocalizationService.ResolveAmbientCultureName())
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        return path.Trim() switch
        {
            "text" or "content" => Text,
            "preview" => string.IsNullOrEmpty(Preview)
                ? (isZh ? "剪贴板暂无纯文本内容" : "Clipboard has no plain text")
                : Preview,
            "length" or "chars" => Length.ToString(CultureInfo.InvariantCulture),
            "lines" or "lineCount" => LineCount.ToString(CultureInfo.InvariantCulture),
            "kind" or "type" => Kind,
            "hasText" => Length > 0 ? "true" : "false",
            "kindLabel" => Kind switch
            {
                "url" => isZh ? "网址链接" : "URL",
                "json" => "JSON",
                "timestamp" => isZh ? "Unix 时间戳" : "Timestamp",
                "hexColor" => isZh ? "颜色色值" : "Hex Color",
                "number" => isZh ? "数值" : "Number",
                "text" => isZh ? "纯文本" : "Plain Text",
                _ => isZh ? "空" : "Empty"
            },
            _ => string.Empty
        };
    }

    private static string ClassifyClipboardText(string trimmed)
    {
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return "url";
        }

        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using JsonDocument _ = JsonDocument.Parse(trimmed);
                return "json";
            }
            catch
            {
            }
        }

        if ((trimmed.Length is 10 or 13) &&
            long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long ts) &&
            ts > 946684800L)
        {
            return "timestamp";
        }

        if (trimmed.StartsWith('#') &&
            (trimmed.Length is 4 or 7 or 9) &&
            trimmed[1..].All(Uri.IsHexDigit))
        {
            return "hexColor";
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "number";
        }

        return "text";
    }
}

public sealed record CustomWidgetStorageDrivesSnapshot
{
    public static CustomWidgetStorageDrivesSnapshot Empty { get; } = new();

    public int Count { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int PrimaryPercent { get; init; }
    public string PrimaryFreeGb { get; init; } = "0";
    public string PrimaryTotalGb { get; init; } = "0";
    public IReadOnlyList<CustomWidgetRecord> Records { get; init; } = [];

    public static CustomWidgetStorageDrivesSnapshot Capture() => QueryLocalDrives();

    public static CustomWidgetStorageDrivesSnapshot QueryLocalDrives()
    {
        var records = new List<CustomWidgetRecord>();
        var summaryParts = new List<string>();
        int primaryPct = 0;
        string primaryFree = "0";
        string primaryTotal = "0";

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    {
                        continue;
                    }

                    long total = drive.TotalSize;
                    if (total <= 0)
                    {
                        continue;
                    }

                    long free = Math.Max(0, drive.AvailableFreeSpace);
                    long used = Math.Max(0, total - free);
                    int pct = (int)Math.Clamp(Math.Round((double)used * 100.0 / total), 0, 100);
                    double usedGb = used / (1024.0 * 1024.0 * 1024.0);
                    double freeGb = free / (1024.0 * 1024.0 * 1024.0);
                    double totalGb = total / (1024.0 * 1024.0 * 1024.0);

                    string rootName = drive.Name.TrimEnd('\\');
                    string volLabel = string.Empty;
                    try
                    {
                        volLabel = drive.VolumeLabel?.Trim() ?? string.Empty;
                    }
                    catch
                    {
                    }

                    string displayTitle = string.IsNullOrWhiteSpace(volLabel)
                        ? rootName
                        : $"{volLabel} ({rootName})";
                    string usedStr = usedGb.ToString("0.0", CultureInfo.InvariantCulture);
                    string freeStr = freeGb.ToString("0.0", CultureInfo.InvariantCulture);
                    string totalStr = totalGb.ToString("0.0", CultureInfo.InvariantCulture);
                    string pctStr = pct.ToString(CultureInfo.InvariantCulture);
                    string itemSummary = $"{usedStr} / {totalStr} GB ({pctStr}%)";

                    var rec = new CustomWidgetRecord
                    {
                        Id = rootName.ToLowerInvariant().TrimEnd(':')
                    };
                    rec.Fields["name"] = rootName;
                    rec.Fields["label"] = volLabel;
                    rec.Fields["title"] = displayTitle;
                    rec.Fields["subtitle"] = $"{freeStr} GB free · {totalStr} GB";
                    rec.Fields["usedGb"] = usedStr;
                    rec.Fields["freeGb"] = freeStr;
                    rec.Fields["totalGb"] = totalStr;
                    rec.Fields["percent"] = pctStr;
                    rec.Fields["value"] = pctStr;
                    rec.Fields["summary"] = itemSummary;
                    records.Add(rec);

                    if (summaryParts.Count < 3)
                    {
                        summaryParts.Add($"{rootName} {pctStr}%");
                    }

                    if (records.Count == 1)
                    {
                        primaryPct = pct;
                        primaryFree = freeStr;
                        primaryTotal = totalStr;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return new CustomWidgetStorageDrivesSnapshot
        {
            Count = records.Count,
            Summary = string.Join(" · ", summaryParts),
            PrimaryPercent = primaryPct,
            PrimaryFreeGb = primaryFree,
            PrimaryTotalGb = primaryTotal,
            Records = records
        };
    }

    public string ResolveToken(string path) => (path?.Trim() ?? string.Empty) switch
    {
        "count" => Count.ToString(CultureInfo.InvariantCulture),
        "summary" => Summary,
        "primaryPercent" or "percent" => PrimaryPercent.ToString(CultureInfo.InvariantCulture),
        "primaryFreeGb" or "freeGb" => PrimaryFreeGb,
        "primaryTotalGb" or "totalGb" => PrimaryTotalGb,
        _ => string.Empty
    };
}

public sealed record QuickLaunchTarget(
    bool IsUri,
    string LaunchTarget,
    string TrustToken,
    string DisplayTarget,
    bool RequiresCommunityConsent);

/// <summary>
/// Sandboxed, 100% NativeAOT-compatible utility engine for Custom Widgets:
/// clipboard inspection, pure in-memory text transformation (<c>text.transform</c>),
/// multi-drive storage telemetry (<c>storageDrives</c>), world clock / timezone formatting (<c>tz(...)</c>),
/// and security-gated desktop workflow launching (<c>launcher.open</c>).
/// </summary>
public static class CustomWidgetUtilityEngine
{
    private static readonly HashSet<string> AllowedExternalUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "vscode",
        "vscode-insiders",
        "cursor",
        "windsurf",
        "trae",
        "obsidian",
        "notion",
        "raycast",
        "steam",
        "ms-screenclip",
        "ms-clock",
        "ms-calculator",
        "ms-paint",
        "ms-todo"
    };

    private static readonly IReadOnlyDictionary<string, string> IanaToWindowsTimeZoneFallback =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UTC"] = "UTC",
            ["Etc/UTC"] = "UTC",
            ["GMT"] = "GMT Standard Time",
            ["Europe/London"] = "GMT Standard Time",
            ["Europe/Paris"] = "Romance Standard Time",
            ["Europe/Berlin"] = "W. Europe Standard Time",
            ["Europe/Moscow"] = "Russian Standard Time",
            ["Asia/Shanghai"] = "China Standard Time",
            ["Asia/Chongqing"] = "China Standard Time",
            ["Asia/Hong_Kong"] = "China Standard Time",
            ["Asia/Taipei"] = "Taipei Standard Time",
            ["Asia/Singapore"] = "Singapore Standard Time",
            ["Asia/Tokyo"] = "Tokyo Standard Time",
            ["Asia/Seoul"] = "Korea Standard Time",
            ["Asia/Dubai"] = "Arabian Standard Time",
            ["Asia/Kolkata"] = "India Standard Time",
            ["Asia/Bangkok"] = "SE Asia Standard Time",
            ["America/New_York"] = "Eastern Standard Time",
            ["America/Chicago"] = "Central Standard Time",
            ["America/Denver"] = "Mountain Standard Time",
            ["America/Los_Angeles"] = "Pacific Standard Time",
            ["America/Toronto"] = "Eastern Standard Time",
            ["America/Vancouver"] = "Pacific Standard Time",
            ["America/Sao_Paulo"] = "E. South America Standard Time",
            ["Australia/Sydney"] = "AUS Eastern Standard Time",
            ["Pacific/Auckland"] = "New Zealand Standard Time"
        };

    private static readonly Regex LatinWordRegex = new(
        @"[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSupportedTextTransform(string? op)
    {
        string norm = NormalizeTextOp(op);
        return norm is "jsonpretty" or "jsonminify"
                    or "urlencode" or "urldecode"
                    or "base64encode" or "base64decode"
                    or "timestamptodate" or "datetotimestamp"
                    or "uuid" or "upper" or "lower" or "trim"
                    or "wordcount" or "sha256" or "md5";
    }

    public static string NormalizeTextOp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string clean = raw.Trim().ToLowerInvariant()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        return clean switch
        {
            "jsonformat" or "prettyjson" => "jsonpretty",
            "minifyjson" or "compactjson" => "jsonminify",
            "encodeuri" or "encodeuricomponent" => "urlencode",
            "decodeuri" or "decodeuricomponent" => "urldecode",
            "b64encode" or "tobase64" => "base64encode",
            "b64decode" or "frombase64" => "base64decode",
            "unixtodate" or "tstodate" => "timestamptodate",
            "totimestamp" or "unix" => "datetotimestamp",
            "guid" => "uuid",
            "uppercase" or "toupper" => "upper",
            "lowercase" or "tolower" => "lower",
            "count" or "stats" => "wordcount",
            _ => clean
        };
    }

    public static CustomWidgetTransformResult ExecuteTextTransform(
        string? input,
        string? operation,
        DateTimeOffset? nowOverride = null)
    {
        string op = NormalizeTextOp(operation);
        string src = input ?? string.Empty;
        if (src.Length > 512 * 1024)
        {
            return new CustomWidgetTransformResult(
                false, op, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                "Input text exceeds 512 KB limit.");
        }

        try
        {
            string output = op switch
            {
                "uuid" => Guid.NewGuid().ToString("D"),
                "upper" => src.ToUpperInvariant(),
                "lower" => src.ToLowerInvariant(),
                "trim" => src.Trim(),
                "urlencode" => Uri.EscapeDataString(src),
                "urldecode" => Uri.UnescapeDataString(src.Replace('+', ' ')),
                "base64encode" => Convert.ToBase64String(Encoding.UTF8.GetBytes(src)),
                "base64decode" => Encoding.UTF8.GetString(Convert.FromBase64String(src.Trim())),
                "sha256" => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant(),
                "md5" => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant(),
                "jsonpretty" => FormatJsonText(src, indented: true),
                "jsonminify" => FormatJsonText(src, indented: false),
                "timestamptodate" => ConvertTimestampToDate(src),
                "datetotimestamp" => ConvertDateToTimestamp(src, nowOverride ?? DateTimeOffset.Now),
                "wordcount" => ComputeTextStatsSummary(src),
                _ => throw new InvalidOperationException($"Unsupported text.transform operation '{operation}'.")
            };

            return new CustomWidgetTransformResult(
                Succeeded: true,
                Operation: op,
                SourcePath: string.Empty,
                OutputPath: string.Empty,
                OutputName: string.Empty,
                SizeText: $"{output.Length} chars",
                ResultText: output,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new CustomWidgetTransformResult(
                Succeeded: false,
                Operation: op,
                SourcePath: string.Empty,
                OutputPath: string.Empty,
                OutputName: string.Empty,
                SizeText: string.Empty,
                ResultText: string.Empty,
                ErrorMessage: ex.Message);
        }
    }

    private static string FormatJsonText(string raw, bool indented)
    {
        using JsonDocument doc = JsonDocument.Parse(
            raw,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            doc.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ConvertTimestampToDate(string raw)
    {
        string trimmed = raw.Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val))
        {
            throw new FormatException("Invalid Unix timestamp integer.");
        }

        DateTimeOffset dto = Math.Abs(val) >= 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(val)
            : DateTimeOffset.FromUnixTimeSeconds(val);
        return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string ConvertDateToTimestamp(string raw, DateTimeOffset now)
    {
        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "now", StringComparison.OrdinalIgnoreCase))
        {
            return now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset dto))
        {
            return dto.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        throw new FormatException("Invalid date/time string.");
    }

    private static string ComputeTextStatsSummary(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "0 chars · 0 words · 0 lines";
        }

        int lines = 1;
        int cjkCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n') lines++;
            if ((ch >= '\u4E00' && ch <= '\u9FFF') ||
                (ch >= '\u3400' && ch <= '\u4DBF') ||
                (ch >= '\u3040' && ch <= '\u30FF') ||
                (ch >= '\uAC00' && ch <= '\uD7AF'))
            {
                cjkCount++;
            }
        }

        int latinWords = LatinWordRegex.Matches(text).Count;
        int totalWords = cjkCount + latinWords;
        return $"{text.Length} chars · {totalWords} words · {lines} lines";
    }

    public static string ResolveClockToken(string path, DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        DateTimeOffset local = now.ToLocalTime();
        TimeSpan offset = local.Offset;
        string sign = offset >= TimeSpan.Zero ? "+" : "-";
        TimeSpan abs = offset.Duration();

        return (path?.Trim() ?? string.Empty) switch
        {
            "utc" or "utcTime" => utc.ToString("HH:mm", CultureInfo.InvariantCulture),
            "utcSeconds" or "utcFullTime" => utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "utcDate" => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "localOffset" or "offset" => $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}",
            "unixSeconds" or "unix" => utc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "unixMs" => utc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "iso" => local.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            "seconds" => local.ToString("ss", CultureInfo.InvariantCulture),
            "fullTime" => local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    public static string ResolveTimeZoneFormatted(
        string? zoneId,
        string? format,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "HH:mm" : format.Trim();
        if (effectiveFormat.Length > 32)
        {
            effectiveFormat = "HH:mm";
        }

        DateTimeOffset targetTime = ConvertToZone(now, zoneId);
        CultureInfo culture = CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
            }
        }

        try
        {
            return targetTime.ToString(effectiveFormat, culture);
        }
        catch (FormatException)
        {
            return targetTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private static DateTimeOffset ConvertToZone(DateTimeOffset now, string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return now.ToLocalTime();
        }

        string clean = zoneId.Trim();
        if (string.Equals(clean, "local", StringComparison.OrdinalIgnoreCase))
        {
            return now.ToLocalTime();
        }

        if (string.Equals(clean, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "GMT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Z", StringComparison.OrdinalIgnoreCase))
        {
            return now.ToUniversalTime();
        }

        // Fixed UTC offset syntax: "UTC+8", "UTC-5", "+09:00", "-04:00"
        string offsetCandidate = clean.StartsWith("UTC", StringComparison.OrdinalIgnoreCase)
            ? clean[3..].Trim()
            : clean;
        if (offsetCandidate.Length >= 2 && (offsetCandidate[0] is '+' or '-'))
        {
            int sign = offsetCandidate[0] == '-' ? -1 : 1;
            string body = offsetCandidate[1..];
            string[] parts = body.Split(':');
            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hrs) &&
                hrs is >= 0 and <= 14)
            {
                int mins = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
                    ? Math.Clamp(m, 0, 59)
                    : 0;
                return now.ToOffset(TimeSpan.FromMinutes(sign * ((hrs * 60) + mins)));
            }
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(clean, out TimeZoneInfo? tz) && tz is not null)
        {
            return TimeZoneInfo.ConvertTime(now, tz);
        }

        if (IanaToWindowsTimeZoneFallback.TryGetValue(clean, out string? winId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(winId, out TimeZoneInfo? fallbackTz) &&
            fallbackTz is not null)
        {
            return TimeZoneInfo.ConvertTime(now, fallbackTz);
        }

        return now.ToUniversalTime();
    }

    private static readonly HashSet<string> ExecutableOrScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".msc", ".cpl", ".scr", ".pif", ".hta", ".msi", ".msp",
        ".reg", ".jar", ".lnk", ".url", ".appref-ms", ".py", ".pyw", ".sh"
    };

    public static bool IsExecutableOrScriptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.Trim().Trim('"');
        int adsColon = trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'
            ? trimmed.IndexOf(':', 2)
            : trimmed.IndexOf(':');
        if (adsColon >= 0)
        {
            return true;
        }

        trimmed = trimmed.TrimEnd('.', ' ');
        string ext = Path.GetExtension(trimmed);
        return !string.IsNullOrEmpty(ext) && ExecutableOrScriptExtensions.Contains(ext);
    }

    public static bool TryResolveQuickLaunchTarget(
        string? rawTarget,
        CustomWidgetDataStore? store,
        out QuickLaunchTarget resolved)
    {
        resolved = new QuickLaunchTarget(false, string.Empty, string.Empty, string.Empty, true);
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return false;
        }

        string clean = rawTarget.Trim();

        // 1. Safe known folders (no arbitrary shell execution)
        string? knownFolderPath = clean.ToLowerInvariant() switch
        {
            "downloads" or "shell:downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "desktop" or "shell:desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" or "shell:documents" or "shell:personal" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" or "shell:pictures" or "shell:mypictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" or "shell:music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" or "shell:videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "userprofile" or "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(knownFolderPath) && Directory.Exists(knownFolderPath))
        {
            resolved = new QuickLaunchTarget(
                IsUri: false,
                LaunchTarget: knownFolderPath,
                TrustToken: $"folder:{clean.ToLowerInvariant()}",
                DisplayTarget: knownFolderPath,
                RequiresCommunityConsent: true);
            return true;
        }

        // 2. Whitelisted URI schemes (vscode://, obsidian://, ms-screenclip:, https://, etc.)
        if (Uri.TryCreate(clean, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            if (!AllowedExternalUriSchemes.Contains(uri.Scheme))
            {
                return false;
            }

            string hostOrScheme = string.IsNullOrWhiteSpace(uri.DnsSafeHost)
                ? uri.Scheme.ToLowerInvariant()
                : $"{uri.Scheme.ToLowerInvariant()}:{uri.DnsSafeHost.ToLowerInvariant()}";
            bool isBuiltInUtilityUri = uri.Scheme.StartsWith("ms-", StringComparison.OrdinalIgnoreCase);

            resolved = new QuickLaunchTarget(
                IsUri: true,
                LaunchTarget: uri.AbsoluteUri,
                TrustToken: $"uri:{hostOrScheme}",
                DisplayTarget: uri.AbsoluteUri,
                RequiresCommunityConsent: !isBuiltInUtilityUri);
            return true;
        }

        // 3. Existing local file or directory path
        string candidatePath = store is not null ? store.ResolveRuntimePath(clean) : clean;
        if (!string.IsNullOrWhiteSpace(candidatePath) &&
            (File.Exists(candidatePath) || Directory.Exists(candidatePath)))
        {
            string fullPath = Path.GetFullPath(candidatePath);
            bool insideSandbox = store?.IsInsideManagedAttachments(fullPath) ?? false;
            resolved = new QuickLaunchTarget(
                IsUri: false,
                LaunchTarget: fullPath,
                TrustToken: $"path:{fullPath.ToLowerInvariant()}",
                DisplayTarget: fullPath,
                RequiresCommunityConsent: !insideSandbox);
            return true;
        }

        return false;
    }

    public static async Task<bool> ExecuteQuickLaunchAsync(QuickLaunchTarget target)
    {
        try
        {
            if (target.IsUri && Uri.TryCreate(target.LaunchTarget, UriKind.Absolute, out Uri? uri))
            {
                return await Launcher.LaunchUriAsync(uri).AsTask().ConfigureAwait(false);
            }

            if (Directory.Exists(target.LaunchTarget) || File.Exists(target.LaunchTarget))
            {
                Process.Start(new ProcessStartInfo(target.LaunchTarget) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] launcher.open failed for '{target.DisplayTarget}': {ex.Message}");
        }

        return false;
    }
}
