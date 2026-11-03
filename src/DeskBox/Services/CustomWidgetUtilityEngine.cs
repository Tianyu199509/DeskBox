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
    public bool HasText => !string.IsNullOrEmpty(Text);

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

        return path.Trim() switch
        {
            "text" or "content" => Text,
            "preview" => string.IsNullOrEmpty(Preview)
                ? LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.EmptyPreview")
                : Preview,
            "length" or "chars" => Length.ToString(CultureInfo.InvariantCulture),
            "lines" or "lineCount" => LineCount.ToString(CultureInfo.InvariantCulture),
            "kind" or "type" => Kind,
            "hasText" => Length > 0 ? "true" : "false",
            "kindLabel" => Kind switch
            {
                "url" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Url"),
                "json" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Json"),
                "jwt" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Jwt"),
                "unicodeEscape" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.UnicodeEscape"),
                "urlEncoded" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.UrlEncoded"),
                "base64" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Base64"),
                "timestamp" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Timestamp"),
                "hexColor" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.HexColor"),
                "number" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Number"),
                "text" => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Text"),
                _ => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Clipboard.Kind.Empty")
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

        if (IsJwtLikeText(trimmed))
        {
            return "jwt";
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

        if (ContainsUnicodeEscapeSequence(trimmed))
        {
            return "unicodeEscape";
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

        if (LooksUrlEncoded(trimmed))
        {
            return "urlEncoded";
        }

        if (LooksLikeBase64Blob(trimmed))
        {
            return "base64";
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "number";
        }

        return "text";
    }

    /// <summary>Three-segment <c>eyJ…</c> JWT shape (empty signature allowed for <c>alg=none</c>).</summary>
    private static bool IsJwtLikeText(string trimmed)
    {
        // ^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]*)?$
        if (!trimmed.StartsWith("eyJ", StringComparison.Ordinal) || trimmed.Length < 8)
        {
            return false;
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 3 || parts[0].Length < 4 || parts[1].Length < 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            foreach (char c in part)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    return false;
                }
            }
        }

        return parts[1].StartsWith("eyJ", StringComparison.Ordinal);
    }

    /// <summary>Contains at least one <c>\uXXXX</c> escape sequence.</summary>
    private static bool ContainsUnicodeEscapeSequence(string trimmed)
    {
        for (int i = 0; i + 5 < trimmed.Length; i++)
        {
            if (trimmed[i] == '\\' &&
                trimmed[i + 1] == 'u' &&
                Uri.IsHexDigit(trimmed[i + 2]) &&
                Uri.IsHexDigit(trimmed[i + 3]) &&
                Uri.IsHexDigit(trimmed[i + 4]) &&
                Uri.IsHexDigit(trimmed[i + 5]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksUrlEncoded(string trimmed)
    {
        // Percent-encoded bodies are compact printable ASCII with %XX pairs and no whitespace.
        if (trimmed.Length < 3 || trimmed.Length > 4096)
        {
            return false;
        }

        bool hasHexPair = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || c > 0x7E)
            {
                return false;
            }

            if (c == '%' )
            {
                hasHexPair = true;
            }
        }

        if (!hasHexPair)
        {
            return false;
        }

        for (int i = 0; i + 2 < trimmed.Length; i++)
        {
            if (trimmed[i] == '%' &&
                Uri.IsHexDigit(trimmed[i + 1]) &&
                Uri.IsHexDigit(trimmed[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeBase64Blob(string trimmed)
    {
        // Base64 heuristic: >= 24 chars, base64 charset, decodes to valid UTF-8,
        // and carries at least one non-alphanumeric marker to avoid flagging plain words.
        if (trimmed.Length < 24 || trimmed.Length > 8192 || trimmed.Length % 4 == 1)
        {
            return false;
        }

        bool hasMarker = false;
        foreach (char c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_'))
            {
                return false;
            }

            if (c is '+' or '/' or '=')
            {
                hasMarker = true;
            }
        }

        if (!hasMarker)
        {
            return false;
        }

        try
        {
            string decoded = CustomWidgetUtilityEngine.DecodeBase64Utf8(trimmed);
            if (decoded.IndexOf('\uFFFD') >= 0)
            {
                return false;
            }

            int printable = 0;
            foreach (char c in decoded)
            {
                if (!char.IsControl(c))
                {
                    printable++;
                }
            }

            return decoded.Length > 0 && printable * 100 / decoded.Length >= 90;
        }
        catch
        {
            return false;
        }
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
            ["Europe/Istanbul"] = "Turkey Standard Time",
            ["Asia/Shanghai"] = "China Standard Time",
            ["Asia/Chongqing"] = "China Standard Time",
            ["Asia/Hong_Kong"] = "China Standard Time",
            ["Asia/Macau"] = "China Standard Time",
            ["Asia/Taipei"] = "Taipei Standard Time",
            ["Asia/Singapore"] = "Singapore Standard Time",
            ["Asia/Tokyo"] = "Tokyo Standard Time",
            ["Asia/Seoul"] = "Korea Standard Time",
            ["Asia/Dubai"] = "Arabian Standard Time",
            ["Asia/Riyadh"] = "Arabian Standard Time",
            ["Asia/Kolkata"] = "India Standard Time",
            ["Asia/Karachi"] = "West Asia Standard Time",
            ["Asia/Bangkok"] = "SE Asia Standard Time",
            ["Asia/Jakarta"] = "SE Asia Standard Time",
            ["America/New_York"] = "Eastern Standard Time",
            ["America/Chicago"] = "Central Standard Time",
            ["America/Denver"] = "Mountain Standard Time",
            ["America/Phoenix"] = "US Mountain Standard Time",
            ["America/Los_Angeles"] = "Pacific Standard Time",
            ["America/Toronto"] = "Eastern Standard Time",
            ["America/Vancouver"] = "Pacific Standard Time",
            ["America/Mexico_City"] = "Central Standard Time (Mexico)",
            ["America/Sao_Paulo"] = "E. South America Standard Time",
            ["Australia/Sydney"] = "AUS Eastern Standard Time",
            ["Australia/Perth"] = "W. Australia Standard Time",
            ["Australia/Lord_Howe"] = "Lord Howe Standard Time",
            ["Pacific/Auckland"] = "New Zealand Standard Time"
        };

    /// <summary>Locale resolution order shared by <see cref="ResolveTimeZoneCityName"/> and tz label words.</summary>
    private static readonly string[] TzLocaleOrder =
    [
        "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "es-ES", "fr-FR", "pt-BR", "ru-RU", "hi-IN", "ar-SA", "bn-BD"
    ];

    private sealed record TzLocaleWords(
        string Ahead,
        string Behind,
        string SameOffset,
        string SameDay,
        string PrevDay,
        string NextDay);

    private static readonly TzLocaleWords[] TzLocaleWordsTable =
    [
        new("ahead", "behind", "same as local", "same day", "yesterday", "tomorrow"),
        new("快", "慢", "与本地一致", "同日", "昨天", "明天"),
        new("快", "慢", "與本地一致", "同日", "昨天", "明天"),
        new("進み", "遅れ", "ローカルと同じ", "同日", "昨日", "明日"),
        new("빠름", "느림", "현지와 동일", "같은 날", "어제", "내일"),
        new("voraus", "hinter", "wie lokal", "gleicher Tag", "gestern", "morgen"),
        new("adelantado", "retrasado", "igual que local", "mismo día", "ayer", "mañana"),
        new("en avance", "en retard", "identique à l'heure locale", "même jour", "hier", "demain"),
        new("à frente", "atrás", "igual ao local", "mesmo dia", "ontem", "amanhã"),
        new("впереди", "позади", "как местное", "тот же день", "вчера", "завтра"),
        new("आगे", "पीछे", "स्थानीय जैसा", "एक ही दिन", "बीता कल", "आने वाला कल"),
        new("متقدم", "متأخر", "مطابق للمحلي", "نفس اليوم", "أمس", "غدًا"),
        new("সামনে", "পিছনে", "স্থানীয়ের মতো", "একই দিন", "গতকাল", "আগামীকাল")
    ];

    /// <summary>
    /// Localized display names for the 24 built-in world-clock business cities,
    /// indexed identically to <see cref="TzLocaleOrder"/> ("本地惯例名 + 英文" principle).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> TzCityDisplayNameTable =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["America/Los_Angeles"] = ["San Francisco", "旧金山", "舊金山", "サンフランシスコ", "샌프란시스코", "San Francisco", "San Francisco", "San Francisco", "São Francisco", "Сан-Франциско", "सैन फ़्रांसिस्को", "سان فرانسيسكو", "সান ফ্রান্সিস্কো"],
            ["America/New_York"] = ["New York", "纽约", "紐約", "ニューヨーク", "뉴욕", "New York", "Nueva York", "New York", "Nova Iorque", "Нью-Йорк", "न्यूयॉर्क", "نيويورك", "নিউইয়র্ক"],
            ["America/Chicago"] = ["Chicago", "芝加哥", "芝加哥", "シカゴ", "시카고", "Chicago", "Chicago", "Chicago", "Chicago", "Чикаго", "शिकागो", "شيكاغو", "শিকাগো"],
            ["America/Toronto"] = ["Toronto", "多伦多", "多倫多", "トロント", "토론토", "Toronto", "Toronto", "Toronto", "Toronto", "Торонто", "टोरंटो", "تورونتو", "টরন্টো"],
            ["America/Sao_Paulo"] = ["São Paulo", "圣保罗", "聖保羅", "サンパウロ", "상파울루", "São Paulo", "São Paulo", "São Paulo", "São Paulo", "Сан-Паулу", "साओ पाउलो", "ساو باولو", "সাও পাওলো"],
            ["America/Mexico_City"] = ["Mexico City", "墨西哥城", "墨西哥城", "メキシコシティ", "멕시코시티", "Mexiko-Stadt", "Ciudad de México", "Mexico", "Cidade do México", "Мехико", "मेक्सिको सिटी", "مدينة المكسيك", "মেক্সিকো সিটি"],
            ["Europe/London"] = ["London", "伦敦", "倫敦", "ロンドン", "런던", "London", "Londres", "Londres", "Londres", "Лондон", "लंदन", "لندن", "লন্ডন"],
            ["Europe/Paris"] = ["Paris", "巴黎", "巴黎", "パリ", "파리", "Paris", "París", "Paris", "Paris", "Париж", "पैरिस", "باريس", "প্যারিস"],
            ["Europe/Berlin"] = ["Berlin", "柏林", "柏林", "ベルリン", "베를린", "Berlin", "Berlín", "Berlin", "Berlim", "Берлин", "बर्लिन", "برلين", "বার্লিন"],
            ["Europe/Moscow"] = ["Moscow", "莫斯科", "莫斯科", "モスクワ", "모스크바", "Moskau", "Moscú", "Moscou", "Moscou", "Москва", "मॉस्को", "موسكو", "মস্কো"],
            ["Europe/Istanbul"] = ["Istanbul", "伊斯坦布尔", "伊斯坦堡", "イスタンブール", "이스탄불", "Istanbul", "Estambul", "Istanbul", "Istambul", "Стамбул", "इस्तांबुल", "إسطنبول", "ইস্তানবুল"],
            ["Asia/Shanghai"] = ["Beijing", "北京", "北京", "北京", "베이징", "Peking", "Pekín", "Pékin", "Pequim", "Пекин", "बीजिंग", "بكين", "বেইজিং"],
            ["Asia/Hong_Kong"] = ["Hong Kong", "香港", "香港", "香港", "홍콩", "Hongkong", "Hong Kong", "Hong Kong", "Hong Kong", "Гонконг", "हांग कांग", "هونغ كونغ", "হংকং"],
            ["Asia/Taipei"] = ["Taipei", "台北", "台北", "台北", "타이베이", "Taipeh", "Taipéi", "Taipei", "Taipé", "Тайбэй", "ताइपे", "تايبيه", "তাইপেই"],
            ["Asia/Singapore"] = ["Singapore", "新加坡", "新加坡", "シンガポール", "싱가포르", "Singapur", "Singapur", "Singapour", "Singapura", "Сингапур", "सिंगापुर", "سنغافورة", "সিঙ্গাপুর"],
            ["Asia/Tokyo"] = ["Tokyo", "东京", "東京", "東京", "도쿄", "Tokio", "Tokio", "Tokyo", "Tóquio", "Токио", "टोक्यो", "طوكيو", "টোকিও"],
            ["Asia/Seoul"] = ["Seoul", "首尔", "首爾", "ソウル", "서울", "Seoul", "Seúl", "Séoul", "Seul", "Сеул", "सियोल", "سول", "সিউল"],
            ["Asia/Kolkata"] = ["New Delhi", "新德里", "新德里", "ニューデリー", "뉴델리", "Neu-Delhi", "Nueva Delhi", "New Delhi", "Nova Délhi", "Нью-Дели", "नई दिल्ली", "نيودلهي", "নয়াদিল্লি"],
            ["Asia/Bangkok"] = ["Bangkok", "曼谷", "曼谷", "バンコク", "방콕", "Bangkok", "Bangkok", "Bangkok", "Bangcoc", "Бангкок", "बैंकॉक", "بانكوك", "ব্যাংকক"],
            ["Asia/Dubai"] = ["Dubai", "迪拜", "杜拜", "ドバイ", "두바이", "Dubai", "Dubái", "Dubaï", "Dubai", "Дубай", "दुबई", "دبي", "দুবাই"],
            ["Asia/Riyadh"] = ["Riyadh", "利雅得", "利雅德", "リヤド", "리야드", "Riad", "Riad", "Riyad", "Riade", "Рияд", "रियाद", "الرياض", "রিয়াদ"],
            ["Australia/Sydney"] = ["Sydney", "悉尼", "雪梨", "シドニー", "시드니", "Sydney", "Sídney", "Sydney", "Sydney", "Сидней", "सिडनी", "سيدني", "সিডনি"],
            ["Australia/Perth"] = ["Perth", "珀斯", "伯斯", "パース", "퍼스", "Perth", "Perth", "Perth", "Perth", "Перт", "पर्थ", "بيرث", "পার্থ"],
            ["Pacific/Auckland"] = ["Auckland", "奥克兰", "奧克蘭", "オークランド", "오클랜드", "Auckland", "Auckland", "Auckland", "Auckland", "Окленд", "ऑकलैंड", "أوكلاند", "অকল্যান্ড"]
        };

    private static readonly Regex LatinWordRegex = new(
        @"[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSupportedTextTransform(string? op)
    {
        string norm = NormalizeTextOp(op);
        return norm is "jsonpretty" or "jsonminify" or "jsonpretty4"
                    or "urlencode" or "urldecode"
                    or "base64encode" or "base64decode"
                    or "timestamptodate" or "datetotimestamp" or "datetotimestampms"
                    or "jwtdecode"
                    or "unicodeunescape" or "unicodeescape"
                    or "jsonescape" or "jsonunescape"
                    or "uuid" or "uuidnodash" or "uuidupper" or "uuidnodashupper"
                    or "upper" or "lower" or "trim"
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
            "jsonformat4" or "prettyjson4" or "jsonindent4" => "jsonpretty4",
            "encodeuri" or "encodeuricomponent" => "urlencode",
            "decodeuri" or "decodeuricomponent" => "urldecode",
            "b64encode" or "tobase64" => "base64encode",
            "b64decode" or "frombase64" => "base64decode",
            "unixtodate" or "tstodate" => "timestamptodate",
            "totimestamp" or "unix" => "datetotimestamp",
            "totimestampms" or "unixms" or "nowms" => "datetotimestampms",
            "jwt" or "decodejwt" or "jwtsubject" => "jwtdecode",
            "unicodetotext" or "unicodedecode" or "unicodetozh" => "unicodeunescape",
            "texttounicode" or "unicodeencode" => "unicodeescape",
            "escapejson" or "jsonstringescape" => "jsonescape",
            "unescapejson" or "jsonstringunescape" => "jsonunescape",
            "guid" or "uuid4" => "uuid",
            "uuidnodashes" or "guidnodash" => "uuidnodash",
            "uuiduppercase" or "guidupper" => "uuidupper",
            "uuidnodashuppercase" or "guidnodashupper" => "uuidnodashupper",
            "uppercase" or "toupper" => "upper",
            "lowercase" or "tolower" => "lower",
            "count" or "stats" => "wordcount",
            _ => clean
        };
    }

    public static CustomWidgetTransformResult ExecuteTextTransform(
        string? input,
        string? operation,
        DateTimeOffset? nowOverride = null,
        string? cultureName = null)
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
            string jwtState = string.Empty;
            string output = op switch
            {
                "uuid" => Guid.NewGuid().ToString("D"),
                "uuidnodash" => Guid.NewGuid().ToString("N"),
                "uuidupper" => Guid.NewGuid().ToString("D").ToUpperInvariant(),
                "uuidnodashupper" => Guid.NewGuid().ToString("N").ToUpperInvariant(),
                "upper" => src.ToUpperInvariant(),
                "lower" => src.ToLowerInvariant(),
                "trim" => src.Trim(),
                "urlencode" => Uri.EscapeDataString(src),
                "urldecode" => Uri.UnescapeDataString(src.Replace('+', ' ')),
                "base64encode" => Convert.ToBase64String(Encoding.UTF8.GetBytes(src)),
                "base64decode" => DecodeBase64Utf8(src),
                "sha256" => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant(),
                "md5" => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant(),
                "jsonpretty" => FormatJsonText(src, indented: true),
                "jsonpretty4" => FormatJsonText(src, indented: true, indentSize: 4),
                "jsonminify" => FormatJsonText(src, indented: false),
                "jsonescape" => EscapeJsonStringLiteral(src),
                "jsonunescape" => UnescapeJsonStringLiteral(src),
                "unicodeescape" => EscapeNonAsciiAsUnicode(src),
                "unicodeunescape" => UnescapeUnicodeLiterals(src),
                "jwtdecode" => DecodeJwt(src, nowOverride ?? DateTimeOffset.Now, cultureName, out jwtState),
                "timestamptodate" => ConvertTimestampToDate(src),
                "datetotimestamp" => ConvertDateToTimestamp(src, nowOverride ?? DateTimeOffset.Now, milliseconds: false),
                "datetotimestampms" => ConvertDateToTimestamp(src, nowOverride ?? DateTimeOffset.Now, milliseconds: true),
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
                ErrorMessage: null,
                JwtState: jwtState);
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
                ErrorMessage: ex.Message,
                JwtState: op == "jwtdecode" ? "invalid" : string.Empty);
        }
    }

    internal static string DecodeBase64Utf8(string raw)
    {
        string cleaned = raw.Trim().Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal);
        int rem = cleaned.Length % 4;
        if (rem is 2 or 3)
        {
            cleaned = cleaned + new string('=', 4 - rem);
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
        }
        catch (FormatException)
        {
            throw new FormatException("Invalid Base64 input: the text is not valid Base64/Base64Url data.");
        }
    }

    private static string FormatJsonText(string raw, bool indented, int indentSize = 2)
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
            IndentSize = Math.Clamp(indentSize, 1, 8),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            doc.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Offline Base64Url JWT decoder (header + payload pretty-print, exp/nbf diagnostics).
    /// Signature is NEVER verified — decode only.
    /// </summary>
    private static string DecodeJwt(string raw, DateTimeOffset now, string? cultureName, out string jwtState)
    {
        jwtState = string.Empty;
        string trimmed = raw.Trim();
        string[] segments = trimmed.Split('.');
        if (segments.Length is < 2 or > 3 ||
            segments.Any(static s => s.Length == 0))
        {
            throw new FormatException("Invalid JWT: expected 'header.payload.signature' Base64Url segments.");
        }

        string headerPretty = FormatDecodedJwtSegment(segments[0], "JWT header");
        string payloadPretty = FormatDecodedJwtSegment(segments[1], "JWT payload");

        string effectiveCulture = cultureName ?? LocalizationService.ResolveAmbientCultureName();
        bool isZh = effectiveCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        bool isZhTw = isZh && LocalizationService.IsTraditionalChineseCulture(effectiveCulture);
        string verifyNote = isZh
            ? (isZhTw ? "[ℹ️ 僅解碼 · 未驗證簽章]" : "[ℹ️ 仅解码 · 未验证签名]")
            : "[ℹ️ Decode only · signature NOT verified]";
        string statusLine = BuildJwtExpiryStatusLine(payloadPretty, now, isZh, isZhTw, out jwtState);

        return string.Join(Environment.NewLine,
            statusLine,
            verifyNote,
            "HEADER:",
            headerPretty,
            "PAYLOAD:",
            payloadPretty);
    }

    private static string FormatDecodedJwtSegment(string segment, string label)
    {
        byte[] bytes;
        string b64 = segment.Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal);
        int rem = b64.Length % 4;
        if (rem is 2 or 3)
        {
            b64 += new string('=', 4 - rem);
        }

        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            throw new FormatException($"Invalid JWT: {label} is not valid Base64Url data.");
        }

        string json = Encoding.UTF8.GetString(bytes);
        try
        {
            return FormatJsonText(json, indented: true);
        }
        catch (JsonException)
        {
            throw new FormatException($"Invalid JWT: {label} does not decode to valid JSON.");
        }
    }

    private static string BuildJwtExpiryStatusLine(
        string payloadPretty,
        DateTimeOffset now,
        bool isZh,
        bool isZhTw,
        out string jwtState)
    {
        long? expUnix = TryReadJwtNumericClaim(payloadPretty, "exp");
        long? nbfUnix = TryReadJwtNumericClaim(payloadPretty, "nbf");

        string nbfNote = string.Empty;
        if (nbfUnix is long nbf && nbf > now.ToUnixTimeSeconds())
        {
            string until = HumanizeDuration(TimeSpan.FromSeconds(nbf - now.ToUnixTimeSeconds()), isZh, isZhTw);
            nbfNote = isZh
                ? (isZhTw ? $" | ⏳ nbf 未生效（約 {until} 後生效）" : $" | ⏳ nbf 未生效（约 {until} 后生效）")
                : $" | ⏳ nbf not yet active (in {until})";
        }

        if (expUnix is long exp)
        {
            DateTimeOffset expLocal = DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime();
            string expText = expLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (exp <= now.ToUnixTimeSeconds())
            {
                string ago = HumanizeDuration(TimeSpan.FromSeconds(now.ToUnixTimeSeconds() - exp), isZh, isZhTw);
                jwtState = "expired";
                return isZh
                    ? (isZhTw ? $"[⚠️ 已過期 {ago} | exp: {expText}]{nbfNote}" : $"[⚠️ 已过期 {ago} | exp: {expText}]{nbfNote}")
                    : $"[⚠️ Expired {ago} ago | exp: {expText}]{nbfNote}";
            }

            string left = HumanizeDuration(TimeSpan.FromSeconds(exp - now.ToUnixTimeSeconds()), isZh, isZhTw);
            jwtState = "valid";
            return isZh
                ? (isZhTw ? $"[✅ 未過期 · 剩餘 {left} | exp: {expText}]{nbfNote}" : $"[✅ 未过期 · 剩余 {left} | exp: {expText}]{nbfNote}")
                : $"[✅ Not expired · {left} left | exp: {expText}]{nbfNote}";
        }

        jwtState = "noexp";
        return isZh
            ? (isZhTw ? $"[ℹ️ Payload 無 exp 宣告，無法判斷過期]{nbfNote}" : $"[ℹ️ Payload 无 exp 声明，无法判断过期]{nbfNote}")
            : $"[ℹ️ Payload has no exp claim; expiry unknown]{nbfNote}";
    }

    private static long? TryReadJwtNumericClaim(string prettyJson, string claim)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(prettyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(claim, out JsonElement el) ||
                el.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return (long)Math.Round(el.GetDouble());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HumanizeDuration(TimeSpan span, bool isZh, bool isZhTw)
    {
        string hourWord = isZhTw ? "小時" : "小时";
        string minFullWord = isZhTw ? "分鐘" : "分钟";
        if (span.TotalDays >= 1)
        {
            int days = (int)Math.Floor(span.TotalDays);
            int hours = span.Hours;
            return isZh
                ? (hours > 0 ? $"{days} 天 {hours} {hourWord}" : $"{days} 天")
                : (hours > 0 ? $"{days}d {hours}h" : $"{days}d");
        }

        if (span.TotalHours >= 1)
        {
            int hours = (int)Math.Floor(span.TotalHours);
            int minutes = span.Minutes;
            return isZh
                ? (minutes > 0 ? $"{hours} {hourWord} {minutes} 分" : $"{hours} {hourWord}")
                : (minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h");
        }

        int onlyMinutes = Math.Max(1, (int)Math.Floor(span.TotalMinutes));
        return isZh ? $"{onlyMinutes} {minFullWord}" : $"{onlyMinutes}m";
    }

    private static string EscapeNonAsciiAsUnicode(string raw)
    {
        var sb = new StringBuilder(raw.Length + 16);
        foreach (char c in raw)
        {
            if (c > 0x7E || c < 0x20)
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string UnescapeUnicodeLiterals(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' &&
                i + 5 < raw.Length &&
                raw[i + 1] == 'u' &&
                TryParseHex4(raw, i + 2, out char decoded))
            {
                sb.Append(decoded);
                i += 5;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool TryParseHex4(string s, int start, out char value)
    {
        value = '\0';
        if (start + 4 > s.Length)
        {
            return false;
        }

        int acc = 0;
        for (int k = 0; k < 4; k++)
        {
            int d = s[start + k] switch
            {
                >= '0' and <= '9' => s[start + k] - '0',
                >= 'a' and <= 'f' => s[start + k] - 'a' + 10,
                >= 'A' and <= 'F' => s[start + k] - 'A' + 10,
                _ => -1
            };
            if (d < 0)
            {
                return false;
            }

            acc = (acc << 4) | d;
        }

        value = (char)acc;
        return true;
    }

    private static string EscapeJsonStringLiteral(string raw)
    {
        var sb = new StringBuilder(raw.Length + 16);
        foreach (char c in raw)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string UnescapeJsonStringLiteral(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            trimmed = trimmed[1..^1];
        }

        var sb = new StringBuilder(trimmed.Length);
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c != '\\' || i + 1 >= trimmed.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = trimmed[++i];
            switch (next)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (TryParseHex4(trimmed, i + 1, out char decoded))
                    {
                        sb.Append(decoded);
                        i += 4;
                    }
                    else
                    {
                        sb.Append('\\').Append(next);
                    }

                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }

        return sb.ToString();
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

    private static string ConvertDateToTimestamp(string raw, DateTimeOffset now, bool milliseconds)
    {
        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "now", StringComparison.OrdinalIgnoreCase))
        {
            return milliseconds
                ? now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                : now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset dto))
        {
            return milliseconds
                ? dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                : dto.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
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
        string? cultureName = null,
        double offsetHours = 0)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "HH:mm" : format.Trim();
        if (effectiveFormat.Length > 32)
        {
            effectiveFormat = "HH:mm";
        }

        DateTimeOffset targetTime = ConvertToZone(ApplyScrubOffset(now, offsetHours), zoneId);
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

    /// <summary>
    /// Returns a localized relative-difference label such as <c>"慢 7h · 同日"</c> / <c>"behind 7h · yesterday"</c>
    /// comparing the target zone wall clock against the local machine wall clock at the (optionally
    /// scrubbed) instant. DST is derived from <see cref="TimeZoneInfo"/> so transition weeks stay correct.
    /// </summary>
    public static string ResolveTimeZoneDiffLabel(
        string? zoneId,
        double offsetHours,
        DateTimeOffset now,
        string? cultureName = null)
    {
        try
        {
            DateTimeOffset instant = ApplyScrubOffset(now, offsetHours);
            DateTimeOffset target = ConvertToZone(instant, zoneId);
            DateTimeOffset local = instant.ToLocalTime();
            int diffMinutes = (int)Math.Round(
                (target.Offset - local.Offset).TotalMinutes,
                MidpointRounding.AwayFromZero);
            TzLocaleWords words = TzLocaleWordsTable[ResolveTzLocaleIndex(cultureName)];

            string diffPart = diffMinutes == 0
                ? words.SameOffset
                : $"{(diffMinutes > 0 ? words.Ahead : words.Behind)} {FormatDiffHours(Math.Abs(diffMinutes))}h";
            string dayPart = target.Date == local.Date
                ? words.SameDay
                : target.Date > local.Date ? words.NextDay : words.PrevDay;
            return $"{diffPart} · {dayPart}";
        }
        catch (InvalidTimeZoneException)
        {
            return TzLocaleWordsTable[ResolveTzLocaleIndex(cultureName)].SameOffset;
        }
    }

    /// <summary>
    /// Classifies the target-zone wall clock at the (optionally scrubbed) instant into
    /// <c>"work"</c> (inside <paramref name="workWindow"/>), <c>"edge"</c> (07:00–window start or
    /// window end–22:00), or <c>"sleep"</c> (22:00–07:00).
    /// </summary>
    public static string ResolveTimeZoneWorkState(
        string? zoneId,
        string? workWindow,
        double offsetHours,
        DateTimeOffset now)
    {
        const string WorkStateWork = "work";
        const string WorkStateEdge = "edge";
        const string WorkStateSleep = "sleep";

        (int startMinutes, int endMinutes) = ParseWorkWindow(workWindow);
        const int EdgeMorningStart = 7 * 60;
        const int EdgeEveningEnd = 22 * 60;

        try
        {
            DateTimeOffset target = ConvertToZone(ApplyScrubOffset(now, offsetHours), zoneId);
            int minutes = target.Hour * 60 + target.Minute;
            if (minutes >= startMinutes && minutes < endMinutes)
            {
                return WorkStateWork;
            }
            if ((minutes >= EdgeMorningStart && minutes < startMinutes) ||
                (minutes >= endMinutes && minutes < EdgeEveningEnd))
            {
                return WorkStateEdge;
            }
            return WorkStateSleep;
        }
        catch (InvalidTimeZoneException)
        {
            return WorkStateSleep;
        }
    }

    /// <summary>
    /// Returns the localized display name for a built-in world-clock city (24 business hubs across
    /// 13 locales); unknown zone IDs fall back to the trimmed input, and <c>"UTC"</c> stays verbatim.
    /// </summary>
    public static string ResolveTimeZoneCityName(string? zoneId, string? cultureName = null)
    {
        string clean = (zoneId ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        if (string.Equals(clean, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "local", StringComparison.OrdinalIgnoreCase))
        {
            return clean.ToUpperInvariant();
        }

        return TzCityDisplayNameTable.TryGetValue(clean, out string[]? names)
            ? names[ResolveTzLocaleIndex(cultureName)]
            : clean;
    }

    private static DateTimeOffset ApplyScrubOffset(DateTimeOffset now, double offsetHours)
    {
        if (double.IsNaN(offsetHours) || double.IsInfinity(offsetHours) ||
            Math.Abs(offsetHours) < 1e-9 || Math.Abs(offsetHours) > 24 * 30)
        {
            return now;
        }

        return now.AddHours(offsetHours);
    }

    private static string FormatDiffHours(int absoluteMinutes)
    {
        double hours = absoluteMinutes / 60.0;
        double rounded = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - Math.Truncate(rounded)) < 0.01)
        {
            return ((int)rounded).ToString(CultureInfo.InvariantCulture);
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static (int StartMinutes, int EndMinutes) ParseWorkWindow(string? workWindow)
    {
        const int DefaultStart = 9 * 60;
        const int DefaultEnd = 18 * 60;
        string raw = (workWindow ?? string.Empty).Trim();
        int dash = raw.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash >= raw.Length - 1)
        {
            return (DefaultStart, DefaultEnd);
        }

        if (!TryParseClockMinutes(raw[..dash], out int start) ||
            !TryParseClockMinutes(raw[(dash + 1)..], out int end) ||
            start >= end)
        {
            return (DefaultStart, DefaultEnd);
        }

        return (start, end);
    }

    private static bool TryParseClockMinutes(string text, out int minutes)
    {
        minutes = 0;
        string trimmed = text.Trim();
        if (trimmed.Length is 0 or > 5)
        {
            return false;
        }

        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        string hourPart = colon < 0 ? trimmed : trimmed[..colon];
        string minutePart = colon < 0 ? "0" : trimmed[(colon + 1)..];
        if (!int.TryParse(hourPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(minutePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins) ||
            hours is < 0 or > 23 || mins is < 0 or > 59)
        {
            return false;
        }

        minutes = (hours * 60) + mins;
        return true;
    }

    private static int ResolveTzLocaleIndex(string? cultureName)
    {
        string raw = (cultureName ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return 0;
        }

        for (int i = 0; i < TzLocaleOrder.Length; i++)
        {
            if (string.Equals(TzLocaleOrder[i], raw, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        string primary = raw.Split('-')[0];
        for (int i = 0; i < TzLocaleOrder.Length; i++)
        {
            if (TzLocaleOrder[i].StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
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
