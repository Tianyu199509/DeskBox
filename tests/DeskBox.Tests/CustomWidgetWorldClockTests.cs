using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Pins the <c>deskbox.world-clock</c> official template contract and the timezone
/// expression helpers (<c>tz/tzDiffLabel/tzWorkState/tzCity</c>) across DST boundaries,
/// half-hour zones, cross-day diffs, and the meeting-time scrubber offset overload.
/// </summary>
public sealed class CustomWidgetWorldClockTests
{
    private static readonly string[] ManifestLocales =
    [
        "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "es-ES",
        "fr-FR", "pt-BR", "ru-RU", "hi-IN", "ar-SA", "bn-BD"
    ];

    private static CustomWidgetEvaluationContext Context(
        DateTimeOffset clock,
        string culture = "en-US",
        Dictionary<string, string>? settings = null,
        Dictionary<string, string>? state = null) => new()
    {
        ClockOverride = clock,
        CultureName = culture,
        Settings = settings ?? new Dictionary<string, string>(StringComparer.Ordinal),
        State = state ?? new Dictionary<string, string>(StringComparer.Ordinal)
    };

    [Fact]
    public void WorldClockTemplate_ValidatesWithZeroErrorsAndZeroWarnings()
    {
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.WorldClockTemplateId);
        string json = CustomWidgetTemplates.GetTemplateJson(CustomWidgetTemplates.WorldClockTemplateId);
        Assert.Same(bundle.ManifestJson, json);
        Assert.Contains(
            CustomWidgetTemplates.Presets,
            p => p.Id == CustomWidgetTemplates.WorldClockTemplateId && p.IconGlyph == "\uE774");

        CustomWidgetParseResult parsed = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);
        Assert.NotNull(parsed.Manifest);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        Assert.Empty(parsed.Diagnostics);

        CustomWidgetManifest m = parsed.Manifest!;
        Assert.Equal("deskbox.world-clock", m.Id);
        Assert.Equal("\uE774", m.Icon);
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal(332, m.DefaultSize.Width);
        Assert.Equal(368, m.DefaultSize.Height);
        Assert.Equal(276, m.MinSize.Width);
        Assert.Equal(220, m.MinSize.Height);
        Assert.NotNull(m.Capsule);
        Assert.Equal("\uE774", m.Capsule!.Glyph);
        Assert.Contains(m.Sources, s => s.Type == "clock");
        Assert.Equal("0", m.State["scrubHours"]);

        // Settings contract: modules + 4 city slots + workWindow + hourFormat + showDevUnix
        string[] settingKeys = m.Settings.Select(static s => s.Key).ToArray();
        Assert.Equal(
            ["modules", "city1", "city2", "city3", "city4", "workWindow", "hourFormat", "showDevUnix"],
            settingKeys);

        CustomWidgetSettingDefinition modulesDef = Assert.Single(m.Settings, s => s.Key == "modules" && s.Type == "moduleList");
        Assert.Equal("cities:1,scrubber:1", modulesDef.DefaultValue);

        CustomWidgetSettingDefinition city4 = Assert.Single(m.Settings, s => s.Key == "city4");
        Assert.Equal("none", city4.DefaultValue);
        Assert.Contains(city4.Options, o => o.Value == "none");
        Assert.Equal(25, city4.Options.Count); // none + 24 built-in business cities

        foreach (CustomWidgetSettingDefinition city in m.Settings.Where(s => s.Key.StartsWith("city", StringComparison.Ordinal)))
        {
            // The shared 24-zone list: 6 Americas, 5 Europe, 6 Asia East/SE, 4 South Asia & ME, 3 Oceania
            List<string> zoneValues = city.Options.Select(static o => o.Value).Where(v => v != "none").ToList();
            Assert.Equal(24, zoneValues.Count);
            Assert.Contains("America/Los_Angeles", zoneValues);
            Assert.Contains("America/New_York", zoneValues);
            Assert.Contains("America/Sao_Paulo", zoneValues);
            Assert.Contains("Europe/London", zoneValues);
            Assert.Contains("Europe/Istanbul", zoneValues);
            Assert.Contains("Asia/Kolkata", zoneValues);
            Assert.Contains("Asia/Riyadh", zoneValues);
            Assert.Contains("Asia/Singapore", zoneValues);
            Assert.Contains("Australia/Sydney", zoneValues);
            Assert.Contains("Pacific/Auckland", zoneValues);
        }

        CustomWidgetSettingDefinition workWindow = Assert.Single(m.Settings, s => s.Key == "workWindow");
        Assert.Equal("09-18", workWindow.DefaultValue);
        Assert.Equal(["09-18", "08-17", "10-19"], workWindow.Options.Select(static o => o.Value).ToArray());

        CustomWidgetSettingDefinition hourFormat = Assert.Single(m.Settings, s => s.Key == "hourFormat");
        Assert.Equal(["24h", "12h"], hourFormat.Options.Select(static o => o.Value).ToArray());

        Assert.Contains(m.Settings, s => s.Key == "showDevUnix" && s.Type == "toggle" && s.DefaultValue == "false");

        // View shape: root Stack wrapping the ModuleStack (cities + scrubber) plus the dev bar,
        // with a bound Slider node for the meeting-time scrubber.
        Assert.Equal("Stack", m.View.Type);
        Assert.Equal(2, m.View.Children.Count);
        CustomWidgetNodeSpec moduleStack = m.View.Children[0];
        Assert.Equal("ModuleStack", moduleStack.Type);
        Assert.Equal("settings.modules", moduleStack.Bind);
        Assert.Equal(["cities", "scrubber"], moduleStack.Children.Select(static c => c.Id).ToArray());
        Assert.Contains("showDevUnix", m.View.Children[1].When!, StringComparison.Ordinal);

        CustomWidgetNodeSpec? slider = FindNode(moduleStack, static n => n.Type == "Slider");
        Assert.NotNull(slider);
        Assert.Equal("state.scrubHours", slider!.Bind);
        Assert.Equal(0, slider.Min);
        Assert.Equal(23, slider.Max);
        Assert.Equal(0.5, slider.Step);

        CustomWidgetNodeSpec? scrubButtons = FindNode(moduleStack,
            static n => n.Children.Any(c => c.Type == "Button"));
        Assert.NotNull(scrubButtons);
        Assert.Contains("scrubHours > 0", scrubButtons!.When!, StringComparison.Ordinal);

        // Copy actions branch on city4 visibility so hidden slots never leak into the text.
        CustomWidgetNodeSpec? copyButton = FindNode(moduleStack, static n => n.Type == "Button" && n.Children.Count == 0 && n.OnClick.Count > 1);
        Assert.NotNull(copyButton);
        Assert.Equal(2, copyButton!.OnClick.Count);
        Assert.All(copyButton.OnClick, a => Assert.Equal("clipboard.copy", a.Type));

        CustomWidgetNodeSpec? city4Row = FindNode(moduleStack, static n => n.When != null && n.When.Contains("city4", StringComparison.Ordinal));
        Assert.NotNull(city4Row);

        static CustomWidgetNodeSpec? FindNode(CustomWidgetNodeSpec root, Func<CustomWidgetNodeSpec, bool> match)
        {
            if (match(root))
            {
                return root;
            }

            foreach (CustomWidgetNodeSpec child in root.Children)
            {
                if (FindNode(child, match) is { } hit)
                {
                    return hit;
                }
            }

            return null;
        }
    }

    [Fact]
    public void WorldClockTemplate_I18nCoversAllThirteenLocalesWithIdenticalKeys()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.WorldClockTemplateId);
        Assert.False(bundle.Locales.IsEmpty);

        CustomWidgetManifest raw = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!;
        Assert.Empty(raw.I18n);

        CustomWidgetManifest m = raw.WithLocales(bundle.Locales);
        Assert.Equal(13, m.I18n.Count);

        string[] firstKeys = m.I18n["en-US"].Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ManifestLocales.OrderBy(static l => l, StringComparer.Ordinal),
            m.I18n.Keys.OrderBy(static k => k, StringComparer.Ordinal));
        foreach ((string locale, IReadOnlyDictionary<string, string> dict) in m.I18n)
        {
            Assert.Equal(
                firstKeys,
                dict.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray());
            Assert.All(dict.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }

        // The ar-SA locale must keep every ${...} expression placeholder intact (key names only here).
        Assert.Equal("الساعة العالمية", m.I18n["ar-SA"]["widgetName"]);
        Assert.Equal("世界时钟", m.I18n["zh-CN"]["widgetName"]);
    }

    [Fact]
    public void WorldClockTemplate_ScrubbingEvaluatesLiveCityRowsEndToEnd()
    {
        // 2026-06-01 12:00 UTC == 20:00 in Shanghai (UTC+8, no DST).
        var clock = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["city1"] = "Europe/London",
            ["city2"] = "Asia/Tokyo",
            ["city3"] = "America/New_York",
            ["city4"] = "Asia/Shanghai",
            ["workWindow"] = "09-18",
            ["hourFormat"] = "24h",
            ["showDevUnix"] = "true"
        };

        CustomWidgetEvaluationContext ctx = Context(clock, "en-US", settings, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scrubHours"] = "6"
        });

        Assert.Equal("London", CustomWidgetExpressionEvaluator.Interpolate("${tzCity($settings.city1)}", ctx));
        // Scrubbed instant = 18:00 UTC -> London 19:00 BST, Tokyo 03:00 (+9), Shanghai 02:00 (+8).
        Assert.Equal("19:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz($settings.city1, $settings.hourFormat == '12h' ? 'hh:mm tt' : 'HH:mm', state.scrubHours)}", ctx));
        Assert.Equal("03:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz($settings.city2, 'HH:mm', state.scrubHours)}", ctx));
        Assert.Equal("02:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz($settings.city4, 'HH:mm', state.scrubHours)}", ctx));

        // 12-hour format branch flips the shared format ternary.
        CustomWidgetEvaluationContext ctx12 = Context(clock, "en-US",
            new Dictionary<string, string>(settings, StringComparer.Ordinal) { ["hourFormat"] = "12h" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["scrubHours"] = "6" });
        Assert.Equal("07:00 PM", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz($settings.city1, $settings.hourFormat == '12h' ? 'hh:mm tt' : 'HH:mm', state.scrubHours)}", ctx12));

        // Work-state light: London 19:00 is evening edge (18:00-22:00), Tokyo 03:00 sleeps,
        // Shanghai 02:00 sleeps; the scrubbed UTC instant itself (18:00 +2h preview) is work time.
        Assert.Equal("edge", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState($settings.city1, $settings.workWindow, state.scrubHours)}", ctx));
        Assert.Equal("sleep", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState($settings.city2, $settings.workWindow, state.scrubHours)}", ctx));
        Assert.Equal("work", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState('UTC', '09-18', 2)}", ctx));

        // Badge tone + capsule glyph ternaries resolve to the documented tone vocabulary.
        Assert.Equal("warning", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState($settings.city1, $settings.workWindow, state.scrubHours) == 'work' ? 'success' : tzWorkState($settings.city1, $settings.workWindow, state.scrubHours) == 'edge' ? 'warning' : 'subtle'}", ctx));
        Assert.Equal("muted", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState($settings.city2, $settings.workWindow, state.scrubHours) == 'sleep' ? 'muted' : 'default'}", ctx));
    }

    [Fact]
    public void TzFunction_RespectsDstSpringForwardFallBackAndHalfHourZones()
    {
        // US spring-forward: 2026-03-08 02:00 EST (07:00 UTC) jumps to 03:00 EDT.
        Assert.Equal("02:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 3, 7, 7, 0, 0, TimeSpan.Zero))));
        Assert.Equal("03:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero))));

        // Offset-aware UTC labels track the DST shift automatically (zzz = zone offset).
        Assert.Equal("UTC-05:00", "UTC" + CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'zzz')}",
            Context(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero))));
        Assert.Equal("UTC-04:00", "UTC" + CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'zzz')}",
            Context(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero))));

        // London BST switch on 2026-03-29 at 01:00 UTC.
        Assert.Equal("00:30", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Europe/London', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero))));
        Assert.Equal("02:30", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Europe/London', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero))));

        // Asia/Kolkata keeps its fixed half-hour offset (+05:30, no DST).
        Assert.Equal("17:30", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Asia/Kolkata', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero))));

        // Australia/Lord_Howe: +11 in southern summer, drops only 30 minutes to +10:30 after 2026-04-05.
        Assert.Equal("14:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Australia/Lord_Howe', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero))));
        Assert.Equal("12:30", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Australia/Lord_Howe', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 4, 5, 2, 0, 0, TimeSpan.Zero))));

        // Unknown zone ids degrade to UTC instead of throwing (InvalidTimeZoneException-safe path).
        Assert.Equal("12:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Mars/Olympus_Mons', 'HH:mm')}",
            Context(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void TzFunction_OffsetOverloadScrubsTheInstantWithDstStillApplied()
    {
        // 06:00 UTC on 2026-03-08 = 01:00 EST; scrubbing +1h crosses the 02:00->03:00 spring jump.
        var clock = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal("01:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'HH:mm', 0)}", Context(clock)));
        Assert.Equal("03:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('America/New_York', 'HH:mm', 1)}", Context(clock)));

        // Half-step offsets (scrubber step 0.5) work for half-hour zones:
        // 12:00 UTC + 2.5h = 14:30 UTC -> Kolkata 20:00 IST.
        Assert.Equal("20:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('Asia/Kolkata', 'HH:mm', 2.5)}",
            Context(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero))));

        // Bare numeric second argument is accepted as an offset: tz(zone, offsetHours).
        Assert.Equal("12:00", CustomWidgetExpressionEvaluator.Interpolate(
            "${tz('UTC', 6)}", Context(clock)));

        // Quoted literals always stay format strings (never offsets), mirroring the documented quoting rule.
        Assert.Equal("2026", CustomWidgetExpressionEvaluator.Interpolate("${tz('UTC', 'yyyy')}", Context(clock)));
        Assert.Equal("0600", CustomWidgetExpressionEvaluator.Interpolate("${tz('UTC', 'HHmm')}", Context(clock)));
    }

    [Fact]
    public void TzDiffLabel_BuildsLocalizedDiffAndCrossDayLabelsFromLiveOffsets()
    {
        DateTimeOffset[] instants =
        [
            new(2026, 1, 15, 2, 0, 0, TimeSpan.Zero),
            new(2026, 6, 1, 14, 30, 0, TimeSpan.Zero),
            new(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
            new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)
        ];
        string[] zones = ["America/Los_Angeles", "Europe/London", "Asia/Kolkata", "Asia/Tokyo", "Australia/Sydney", "UTC"];

        foreach (DateTimeOffset instant in instants)
        {
            foreach (string zone in zones)
            {
                foreach ((string culture, string ahead, string behind, string same, string sameDay, string prevDay, string nextDay) in new[]
                {
                    ("en-US", "ahead", "behind", "same as local", "same day", "yesterday", "tomorrow"),
                    ("zh-CN", "快", "慢", "与本地一致", "同日", "昨天", "明天")
                })
                {
                    string actual = CustomWidgetExpressionEvaluator.Interpolate(
                        $"${{tzDiffLabel('{zone}')}}", Context(instant, culture));
                    Assert.Equal(BuildExpectedDiffLabel(zone, 0, instant, ahead, behind, same, sameDay, prevDay, nextDay), actual);
                }
            }
        }

        // Scrub offset shifts the reference instant (cross-day flips included).
        var evening = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        string actualTokyo = CustomWidgetExpressionEvaluator.Interpolate(
            "${tzDiffLabel('Asia/Tokyo', 6)}", Context(evening, "en-US"));
        Assert.Equal(BuildExpectedDiffLabel("Asia/Tokyo", 6, evening, "ahead", "behind", "same as local", "same day", "yesterday", "tomorrow"), actualTokyo);

        // All 13 word tables stay non-empty and keep the ' · ' separator shape.
        foreach (string culture in ManifestLocales)
        {
            string label = CustomWidgetExpressionEvaluator.Interpolate(
                "${tzDiffLabel('Europe/London')}", Context(evening, culture));
            Assert.Contains(" · ", label, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, label);
        }

        static string BuildExpectedDiffLabel(
            string zone, double offsetHours, DateTimeOffset now,
            string ahead, string behind, string same, string sameDay, string prevDay, string nextDay)
        {
            DateTimeOffset instant = offsetHours == 0 ? now : now.AddHours(offsetHours);
            TimeSpan target = ParseZoneOffset(
                CustomWidgetUtilityEngine.ResolveTimeZoneFormatted(zone, "zzz", now));
            TimeSpan local = TimeZoneInfo.Local.GetUtcOffset(instant);
            int diffMinutes = (int)Math.Round((target - local).TotalMinutes, MidpointRounding.AwayFromZero);

            string diffPart = diffMinutes == 0
                ? same
                : $"{(diffMinutes > 0 ? ahead : behind)} {FormatHours(Math.Abs(diffMinutes))}h";

            DateTime targetWall = (instant.UtcDateTime + target).Date;
            DateTime localWall = (instant.UtcDateTime + local).Date;
            string dayPart = targetWall == localWall ? sameDay : targetWall > localWall ? nextDay : prevDay;
            return $"{diffPart} · {dayPart}";
        }

        static string FormatHours(int absoluteMinutes)
        {
            double hours = absoluteMinutes / 60.0;
            double rounded = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
            return Math.Abs(rounded - Math.Truncate(rounded)) < 0.01
                ? ((int)rounded).ToString(CultureInfo.InvariantCulture)
                : rounded.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static TimeSpan ParseZoneOffset(string raw)
        {
            string s = raw.Trim();
            bool negative = s.StartsWith('-');
            string body = negative || s.StartsWith('+') ? s[1..] : s;
            string[] parts = body.Split(':');
            int hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int minutes = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromMinutes((negative ? -1 : 1) * ((hours * 60) + minutes));
        }
    }

    [Fact]
    public void TzWorkState_ClassifiesWorkEdgeSleepWithWindowAndOffsetVariants()
    {
        TimeZoneInfo ny = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset NyAt(int hour, int minute) =>
            new(TimeZoneInfo.ConvertTimeToUtc(
                new DateTime(2026, 6, 1, hour, minute, 0), ny), TimeSpan.Zero);

        // Default 09-18 window.
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(10, 0)));
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(17, 59)));
        Assert.Equal("edge", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(7, 30)));
        Assert.Equal("edge", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(18, 0)));
        Assert.Equal("sleep", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState('America/New_York', $settings.workWindow, 0)}",
            Context(NyAt(23, 0), "en-US", new Dictionary<string, string>(StringComparer.Ordinal) { ["workWindow"] = "09-18" })));
        Assert.Equal("sleep", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(6, 59)));
        Assert.Equal("sleep", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 0, NyAt(22, 0)));

        // Window variants reshape the buffers: 09:30 is work under 08-17 but morning edge under 10-19.
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "08-17", 0, NyAt(9, 30)));
        Assert.Equal("edge", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "10-19", 0, NyAt(9, 30)));

        // Half-hour zones classify on their true wall clock (Kolkata 09:00 IST == 03:30 UTC).
        var kolkataMorning = new DateTimeOffset(2026, 6, 1, 3, 30, 0, TimeSpan.Zero);
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("Asia/Kolkata", "09-18", 0, kolkataMorning));

        // Offset scrubbing shifts the classification window with the meeting instant.
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 2, NyAt(8, 0)));
        Assert.Equal("edge", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("America/New_York", "09-18", 11, NyAt(8, 0)));

        // 2-arg offset form and garbage windows degrade to the 09-18 default.
        Assert.Equal("work", CustomWidgetExpressionEvaluator.Interpolate(
            "${tzWorkState('UTC', 6)}", Context(new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero))));
        Assert.Equal("sleep", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("UTC", "99-88", 0, new DateTimeOffset(2026, 6, 1, 3, 0, 0, TimeSpan.Zero)));

        // HH:MM-HH:MM windows parse too.
        Assert.Equal("work", CustomWidgetUtilityEngine.ResolveTimeZoneWorkState("UTC", "09:30-18:30", 0, new DateTimeOffset(2026, 6, 1, 9, 45, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TzCity_ResolvesLocalizedCityNamesWithEnglishFallback()
    {
        Assert.Equal("Beijing", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "en-US"));
        Assert.Equal("北京", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "zh-CN"));
        Assert.Equal("北京", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "zh-TW"));
        Assert.Equal("サンフランシスコ", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("America/Los_Angeles", "ja-JP"));
        Assert.Equal("런던", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Europe/London", "ko-KR"));
        Assert.Equal("São Paulo", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("America/Sao_Paulo", "pt-BR"));
        Assert.Equal("Пекин", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "ru-RU"));
        Assert.Equal("بكين", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "ar-SA"));
        Assert.Equal("বেইজিং", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "bn-BD"));
        Assert.Equal("新德里", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Kolkata", "zh-CN"));

        // Unknown cultures fall back to English; unknown zones fall back to the raw id; UTC stays verbatim.
        Assert.Equal("Beijing", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Asia/Shanghai", "xx-XX"));
        Assert.Equal("Mars/Olympus_Mons", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("Mars/Olympus_Mons", "zh-CN"));
        Assert.Equal("UTC", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("UTC", "ja-JP"));

        // End-to-end through the evaluator (manifest settings labels use this form).
        Assert.Equal(
            "Tokyo",
            CustomWidgetExpressionEvaluator.Interpolate("${tzCity('Asia/Tokyo')}", Context(DateTimeOffset.UtcNow, "en-US")));
    }

    [Fact]
    public void WorldClockCapability_RegistryDocumentsTheNewFunctionFamily()
    {
        Assert.Equal(35, CustomWidgetCapabilityRegistry.All.Count);
        CustomWidgetCapabilitySpec spec = CustomWidgetCapabilityRegistry.All.Single(c => c.Id == "world-clock");
        Assert.Contains(spec.TriggerTokenPrefixes, p => p == "tzDiffLabel(");
        Assert.Contains(spec.TriggerTokenPrefixes, p => p == "tzWorkState(");
        Assert.Contains(spec.TriggerTokenPrefixes, p => p == "tzCity(");
        Assert.Contains(spec.Members, m => m.Name.Contains("tzDiffLabel", StringComparison.Ordinal));
        Assert.Contains(spec.Members, m => m.Name.Contains("tzWorkState", StringComparison.Ordinal));
        Assert.Contains(spec.Members, m => m.Name.Contains("tzCity", StringComparison.Ordinal));
        Assert.Contains("offsetHours", spec.CompactContractMarkdown, StringComparison.Ordinal);

        // The API reference manual and generated workspace docs pick the new signatures up.
        string zhRef = CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(isChinese: true);
        Assert.Contains("tzWorkState", zhRef, StringComparison.Ordinal);
        Assert.Contains("tzCity", zhRef, StringComparison.Ordinal);
    }
}
