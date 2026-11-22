using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Deterministic local JSONL analyzer (Claude Code &amp; OpenAI Codex CLI), official cloud API
/// balance/quota client (GLM Coding Plan, DeepSeek, Kimi/Moonshot, OpenRouter), and token
/// expression evaluator for <c>deskbox.ai-pulse</c>.
/// </summary>
public static partial class CustomWidgetAiMonitorEngine
{
    public const string ProviderClaude = "claude";
    public const string ProviderCodex = "codex";
    public const string ProviderGlm = "glm";
    public const string ProviderDeepSeek = "deepseek";
    public const string ProviderKimi = "kimi";
    public const string ProviderOpenRouter = "openrouter";

    public const string KeySelectedProvider = "selectedProvider";
    public const string KeyTrendMetric = "trendMetric";
    public const string KeyWalletTargetBalance = "walletTargetBalance";
    public const string KeyWalletCurrency = "walletCurrency";
    public const double DefaultWalletTargetBalance = 100.0;

    private static readonly (string Code, string Symbol, double PerUsdRate)[] SupportedWalletCurrencyTable =
    [
        ("CNY", "¥", 7.20),
        ("USD", "$", 1.00),
        ("EUR", "€", 0.92),
        ("TWD", "NT$", 32.0),
        ("HKD", "HK$", 7.80),
        ("JPY", "¥", 150.0),
        ("KRW", "₩", 1380.0),
        ("GBP", "£", 0.79),
        ("RUB", "₽", 92.0),
        ("BRL", "R$", 5.60),
        ("INR", "₹", 84.0),
        ("SAR", "﷼", 3.75),
        ("BDT", "৳", 119.0)
    ];

    public static void SetProviderMonitorEnabled(string providerId, bool enabled) =>
        CustomWidgetAiMonitorPreferencesStore.Shared.SetProviderMonitorEnabled(providerId, enabled);

    public static void RemoveMonitorKeyOrHideProvider(string providerId) =>
        CustomWidgetAiMonitorPreferencesStore.Shared.RemoveMonitorKeyOrHideProvider(
            providerId,
            CustomWidgetAiCredentialStore.Shared);

    public static string GetCurrencySymbol(string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            return "$";
        }

        string trimmed = currencyCode.Trim();
        foreach (var entry in SupportedWalletCurrencyTable)
        {
            if (string.Equals(entry.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Symbol;
            }
        }

        return "$";
    }

    public static double ConvertCurrencyAmount(double amount, string? fromCurrency, string? toCurrency)
    {
        if (amount <= 0)
        {
            return 0.0;
        }

        string src = NormalizeWalletCurrencyCodeOrNull(fromCurrency) ?? "USD";
        string dst = NormalizeWalletCurrencyCodeOrNull(toCurrency) ?? "USD";
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            return amount;
        }

        double srcRate = GetPerUsdRate(src);
        double dstRate = GetPerUsdRate(dst);
        if (srcRate <= 0 || dstRate <= 0)
        {
            return amount;
        }

        double usd = amount / srcRate;
        return usd * dstRate;
    }

    private static double GetPerUsdRate(string code)
    {
        foreach (var entry in SupportedWalletCurrencyTable)
        {
            if (string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return entry.PerUsdRate;
            }
        }

        return 1.0;
    }

    public static string? NormalizeWalletCurrencyCodeOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim().ToUpperInvariant();
        foreach (var entry in SupportedWalletCurrencyTable)
        {
            if (string.Equals(entry.Code, trimmed, StringComparison.Ordinal))
            {
                return entry.Code;
            }
        }

        return null;
    }

    public static string ResolveDefaultWalletCurrency(string? explicitCultureOrRegion = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitCultureOrRegion))
        {
            if (TryMapCultureOrRegionToCurrency(explicitCultureOrRegion.Trim(), out string mappedExplicit))
            {
                return mappedExplicit;
            }

            return "USD";
        }

        string osRegion = string.Empty;
        try
        {
            osRegion = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch
        {
            // Ignore invariant / sandboxed region lookup failures.
        }

        string ambientCulture = LocalizationService.ResolveAmbientCultureName();
        if (!string.IsNullOrWhiteSpace(osRegion) &&
            !string.Equals(osRegion, "US", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(osRegion, "IV", StringComparison.OrdinalIgnoreCase) &&
            TryMapRegionCodeToCurrency(osRegion, out string osCurrency))
        {
            return osCurrency;
        }

        if (!string.IsNullOrWhiteSpace(ambientCulture) &&
            TryMapCultureOrRegionToCurrency(ambientCulture, out string ambientCurrency))
        {
            return ambientCurrency;
        }

        if (!string.IsNullOrWhiteSpace(osRegion) &&
            TryMapRegionCodeToCurrency(osRegion, out string fallbackOsCurrency))
        {
            return fallbackOsCurrency;
        }

        return "USD";
    }

    private static bool TryMapCultureOrRegionToCurrency(string token, out string currency)
    {
        if (NormalizeWalletCurrencyCodeOrNull(token) is { } directCode)
        {
            currency = directCode;
            return true;
        }

        string normalized = token.Replace('_', '-').Trim();
        if (normalized.Length == 2 && TryMapRegionCodeToCurrency(normalized, out currency))
        {
            return true;
        }

        int dash = normalized.LastIndexOf('-');
        if (dash > 0 && dash + 1 < normalized.Length)
        {
            string regionPart = normalized[(dash + 1)..];
            if (regionPart.Length == 2 && TryMapRegionCodeToCurrency(regionPart, out currency))
            {
                return true;
            }
        }

        string langPart = dash > 0 ? normalized[..dash] : normalized;
        currency = langPart.ToLowerInvariant() switch
        {
            "zh" => "CNY",
            "ja" => "JPY",
            "ko" => "KRW",
            "de" or "fr" or "es" or "it" or "nl" => "EUR",
            "pt" => "BRL",
            "ru" => "RUB",
            "hi" => "INR",
            "ar" => "SAR",
            "bn" => "BDT",
            "en" => "USD",
            _ => string.Empty
        };
        return currency.Length > 0;
    }

    private static bool TryMapRegionCodeToCurrency(string twoLetterRegion, out string currency)
    {
        currency = twoLetterRegion.ToUpperInvariant() switch
        {
            "CN" => "CNY",
            "TW" => "TWD",
            "HK" or "MO" => "HKD",
            "US" or "CA" or "AU" or "SG" or "NZ" => "USD",
            "GB" or "UK" => "GBP",
            "JP" => "JPY",
            "KR" => "KRW",
            "DE" or "FR" or "ES" or "IT" or "NL" or "BE" or "AT" or "IE" or "PT" or "FI" or "GR" => "EUR",
            "RU" => "RUB",
            "BR" => "BRL",
            "IN" => "INR",
            "SA" or "AE" => "SAR",
            "BD" => "BDT",
            _ => string.Empty
        };
        return currency.Length > 0;
    }

    public static string ResolveWalletCurrency(
        IReadOnlyDictionary<string, string>? settings,
        string? cultureName = null)
    {
        if (settings is not null &&
            settings.TryGetValue(KeyWalletCurrency, out string? configured) &&
            !string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured.Trim(), "auto", StringComparison.OrdinalIgnoreCase) &&
            NormalizeWalletCurrencyCodeOrNull(configured) is { } valid)
        {
            return valid;
        }

        return ResolveDefaultWalletCurrency(cultureName);
    }

    public static double ResolveWalletTargetBalance(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is not null &&
            settings.TryGetValue(KeyWalletTargetBalance, out string? raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) &&
            parsed > 0)
        {
            return Math.Clamp(parsed, 1.0, 1_000_000.0);
        }

        return DefaultWalletTargetBalance;
    }

    public static string FormatWalletTargetSummary(double targetAmount, string currencyCode)
    {
        string sym = GetCurrencySymbol(currencyCode);
        string numText = Math.Abs(targetAmount - Math.Round(targetAmount)) < 1e-4
            ? ((long)Math.Round(targetAmount)).ToString(CultureInfo.InvariantCulture)
            : targetAmount.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{sym}{numText}";
    }

    internal static string MapCurrencySymbolToCode(string? symbolOrCode)
    {
        if (string.IsNullOrWhiteSpace(symbolOrCode))
        {
            return "USD";
        }

        string trimmed = symbolOrCode.Trim();
        if (NormalizeWalletCurrencyCodeOrNull(trimmed) is { } directCode)
        {
            return directCode;
        }

        return trimmed.ToUpperInvariant() switch
        {
            "$" or "US$" => "USD",
            "€" => "EUR",
            "£" => "GBP",
            "HK$" => "HKD",
            "NT$" => "TWD",
            "JP¥" or "JP￥" => "JPY",
            "₩" => "KRW",
            "₽" => "RUB",
            "R$" => "BRL",
            "₹" => "INR",
            "﷼" => "SAR",
            "৳" => "BDT",
            "¥" or "￥" or "CN¥" or "RMB" => "CNY",
            _ => "CNY"
        };
    }

    public static string InferProviderWalletCurrencyCode(CustomWidgetAiProviderSnapshot provider)
    {
        if (NormalizeWalletCurrencyCodeOrNull(provider.PlanBadge) is { } badgeCurrency)
        {
            return badgeCurrency;
        }

        return MapCurrencySymbolToCode(provider.CurrencySymbol);
    }

    public static (double Percent, AiQuotaHealth Health, string Tone, string StatusIcon) EvaluateEffectiveProviderMetrics(
        CustomWidgetAiProviderSnapshot? provider,
        IReadOnlyDictionary<string, string>? settings,
        string? cultureName = null)
    {
        if (provider is null)
        {
            return (0.0, AiQuotaHealth.Idle, "subtle", "\uE946");
        }

        if (!provider.HasLiveData || provider.ErrorMessage is not null)
        {
            return (0.0, AiQuotaHealth.Warning, "warning", "\uE7BA");
        }

        bool showRem = settings is null || IsRemainingMode(settings);
        if (provider.BillingKind == AiProviderBillingKind.WindowQuota && provider.PrimaryWindow is { } pw)
        {
            double pct = showRem ? pw.RemainingPercent : pw.UsedPercent;
            return provider.Health switch
            {
                AiQuotaHealth.Critical => (pct, AiQuotaHealth.Critical, "danger", "\uE7BA"),
                AiQuotaHealth.Warning => (pct, AiQuotaHealth.Warning, "warning", "\uE7BA"),
                _ => (pct, AiQuotaHealth.Healthy, "emerald", "\uE73E")
            };
        }

        if (provider.IsLocalCli && provider.BillingKind == AiProviderBillingKind.WalletBalance)
        {
            return (0.0, AiQuotaHealth.Healthy, "emerald", "\uE73E");
        }

        if (string.Equals(provider.WalletFormatted, "∞", StringComparison.Ordinal))
        {
            return (100.0, AiQuotaHealth.Healthy, "emerald", "\uE73E");
        }

        string targetCurrency = ResolveWalletCurrency(settings, cultureName);
        double targetBalance = ResolveWalletTargetBalance(settings);
        string providerCurrency = InferProviderWalletCurrencyCode(provider);
        double equivalent = ConvertCurrencyAmount(provider.WalletBalance, providerCurrency, targetCurrency);
        double ratioPct = Math.Clamp(equivalent / targetBalance * 100.0, 0.0, 100.0);

        if (ratioPct <= 8.0)
        {
            return (ratioPct, AiQuotaHealth.Critical, "danger", "\uE7BA");
        }

        if (ratioPct <= 25.0)
        {
            return (ratioPct, AiQuotaHealth.Warning, "warning", "\uE7BA");
        }

        return (ratioPct, AiQuotaHealth.Healthy, "emerald", "\uE73E");
    }

    internal const int PrefixFingerprintMaxBytes = 512;

    public static IReadOnlyList<(string Id, string Name, string ShortName, string Monogram, string BrandHex, bool IsLocalCli)> Catalog =>
        GetCatalog();

    public static IReadOnlyList<(string Id, string Name, string ShortName, string Monogram, string BrandHex, bool IsLocalCli)> GetCatalog(
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return
        [
            (ProviderClaude, "Claude Code", "Claude", "C", "#D97757", true),
            (ProviderCodex, "OpenAI Codex", "Codex", "Cx", "#10A37F", true),
            (ProviderGeminiCli, "Gemini CLI", "Gemini", "Gm", "#4285F4", true),
            (ProviderGlm, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Glm"), "GLM", "G", "#06B6D4", false),
            (ProviderMiniMax, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.MiniMax"), "MiniMax", "MM", "#EC4899", false),
            (ProviderDeepSeek, "DeepSeek", "DeepSeek", "DS", "#3B82F6", false),
            (ProviderSiliconFlow, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.SiliconFlow"), "SiliconFlow", "SF", "#7C3AED", false),
            (ProviderKimi, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Kimi"), "Kimi", "Km", "#6366F1", false),
            (ProviderOpenRouter, "OpenRouter", "Router", "OR", "#8B5CF6", false),
            (ProviderOneApi, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.OneApi"), "OneAPI", "OA", "#F59E0B", false)
        ];
    }

    public static string GetProviderDisplayName(string providerId, string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return providerId.ToLowerInvariant() switch
        {
            ProviderClaude => "Claude Code",
            ProviderCodex => "OpenAI Codex",
            ProviderGeminiCli => "Gemini CLI",
            ProviderGlm => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Glm"),
            ProviderMiniMax => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.MiniMax"),
            ProviderDeepSeek => "DeepSeek",
            ProviderSiliconFlow => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.SiliconFlow"),
            ProviderKimi => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Kimi"),
            ProviderOpenRouter => "OpenRouter",
            ProviderOneApi => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.OneApi"),
            _ => providerId
        };
    }


    public static CustomWidgetAiProviderSnapshot? SelectFocusProvider(
        CustomWidgetAiMonitorSnapshot snapshot,
        IReadOnlyDictionary<string, string> state)
    {
        if (snapshot.ActiveProviders.Count == 0)
        {
            return null;
        }

        if (state.TryGetValue(KeySelectedProvider, out string? wanted) &&
            !string.IsNullOrWhiteSpace(wanted))
        {
            CustomWidgetAiProviderSnapshot? match = snapshot.ActiveProviders.FirstOrDefault(
                p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return snapshot.ActiveProviders[0];
    }

    public static bool IsRemainingMode(IReadOnlyDictionary<string, string> settings) =>
        !settings.TryGetValue("quotaDisplayMode", out string? mode) ||
        !string.Equals(mode, "used", StringComparison.OrdinalIgnoreCase);

    public static string FormatWindowPercentText(
        CustomWidgetAiWindowQuota window,
        bool showRemaining,
        out string modeWord,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        if (showRemaining)
        {
            modeWord = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.WordRemaining");
            return FormattableString.Invariant($"{Math.Round(window.RemainingPercent):0}%");
        }

        modeWord = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.WordUsed");
        return FormattableString.Invariant($"{Math.Round(window.UsedPercent):0}%");
    }

    public static string FormatCompactTokens(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000_000_000.0):0.1}B");
        }
        if (tokens >= 1_000_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000_000.0):0.1}M");
        }
        if (tokens >= 1_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000.0):0.#}K");
        }
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatResetCountdown(
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        if (resetsAt is null)
        {
            return LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ResetStandby");
        }

        TimeSpan rem = resetsAt.Value - now;
        if (rem <= TimeSpan.Zero)
        {
            return LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ResetDone");
        }

        if (rem.TotalDays >= 1.0)
        {
            int days = (int)rem.TotalDays;
            int hours = rem.Hours;
            return hours > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ResetDaysHoursFormat", days, hours)
                : LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ResetDaysFormat", days);
        }

        if (rem.TotalHours >= 1.0)
        {
            return LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.ResetHoursMinutesFormat",
                (int)rem.TotalHours,
                rem.Minutes.ToString("D2", CultureInfo.InvariantCulture));
        }

        return LocalizationService.FormatForCulture(
            c,
            "CustomWidget.AiMonitor.ResetMinutesFormat",
            Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes)));
    }

    public static AiQuotaHealth EvaluateWindowHealth(double usedPercent, double warnRemainingPct = 20.0)
    {
        double remaining = Math.Clamp(100.0 - usedPercent, 0.0, 100.0);
        if (remaining <= Math.Min(10.0, warnRemainingPct * 0.5))
        {
            return AiQuotaHealth.Critical;
        }
        if (remaining <= warnRemainingPct)
        {
            return AiQuotaHealth.Warning;
        }
        return AiQuotaHealth.Healthy;
    }

    public static AiQuotaHealth EvaluateWalletHealth(double balance, string currencySymbol, double warnBalanceCny = 15.0)
    {
        double threshold = currencySymbol == "$" ? Math.Max(2.0, warnBalanceCny / 7.0) : warnBalanceCny;
        if (balance <= threshold * 0.25)
        {
            return AiQuotaHealth.Critical;
        }
        if (balance <= threshold)
        {
            return AiQuotaHealth.Warning;
        }
        return AiQuotaHealth.Healthy;
    }


    public static CustomWidgetAiProviderSnapshot ParseDeepSeekBalanceJson(
        string json,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string currency = "CNY";
        double total = 0;
        double granted = 0;
        double toppedUp = 0;

        if (root.TryGetProperty("balance_infos", out JsonElement arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string cur = item.TryGetProperty("currency", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() ?? "CNY"
                    : "CNY";
                double tot = ParseStringOrNumber(item, "total_balance");
                double gr = ParseStringOrNumber(item, "granted_balance");
                double top = ParseStringOrNumber(item, "topped_up_balance");

                if (tot > 0 || total == 0)
                {
                    currency = cur;
                    total = tot;
                    granted = gr;
                    toppedUp = top;
                    if (string.Equals(cur, "CNY", StringComparison.OrdinalIgnoreCase) && tot > 0)
                    {
                        break;
                    }
                }
            }
        }

        string sym = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
        AiQuotaHealth health = EvaluateWalletHealth(total, sym);
        string formatted = FormattableString.Invariant($"{sym} {total:0.00}");
        string toppedUpText = toppedUp.ToString("0.00", CultureInfo.InvariantCulture);
        string grantedText = granted.ToString("0.00", CultureInfo.InvariantCulture);
        string breakdown = granted > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownGrantedFormat",
                sym,
                toppedUpText,
                grantedText)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownTopupFormat",
                currency);

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderDeepSeek,
            Name: "DeepSeek",
            ShortName: "DeepSeek",
            Monogram: "DS",
            BrandHex: "#3B82F6",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: currency,
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: sym,
            WalletBalance: total,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: null,
            RadarValueText: formatted,
            RadarSubtext: granted > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextGrantedFormat", sym, grantedText)
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextOfficial"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseKimiBalanceJson(
        string json,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        double available = ParseStringOrNumber(data, "available_balance");
        double cash = ParseStringOrNumber(data, "cash_balance");
        double voucher = ParseStringOrNumber(data, "voucher_balance");

        bool isIntl = AiProviderCatalog.IsInternationalRegion(region);
        string sym = isIntl ? "$" : "¥";
        AiQuotaHealth health = EvaluateWalletHealth(available, sym);
        string formatted = FormattableString.Invariant($"{sym} {available:0.00}");
        string cashText = cash.ToString("0.00", CultureInfo.InvariantCulture);
        string voucherText = voucher.ToString("0.00", CultureInfo.InvariantCulture);
        string breakdown = voucher > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.KimiBreakdownVoucherFormat",
                sym,
                cashText,
                voucherText)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.KimiBreakdownCashFormat",
                isIntl ? "Moonshot AI" : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.NodeCn"));

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderKimi,
            Name: GetProviderDisplayName(ProviderKimi, c),
            ShortName: "Kimi",
            Monogram: "Km",
            BrandHex: "#6366F1",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: isIntl ? "Global" : "CN",
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: sym,
            WalletBalance: available,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: null,
            RadarValueText: formatted,
            RadarSubtext: voucher > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.KimiSubtextVoucherFormat", sym, voucherText)
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.KimiSubtextLive"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseOpenRouterCreditsJson(
        string json,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        double totalCredits = ParseStringOrNumber(data, "total_credits");
        double totalUsage = ParseStringOrNumber(data, "total_usage");
        double available = Math.Max(0.0, totalCredits - totalUsage);
        double? remPct = totalCredits > 0
            ? Math.Clamp(available / totalCredits * 100.0, 0.0, 100.0)
            : null;

        AiQuotaHealth health = EvaluateWalletHealth(available, "$");
        string formatted = FormattableString.Invariant($"$ {available:0.00}");
        string breakdown = totalCredits > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.OpenRouterBreakdownFormat",
                FormattableString.Invariant($"${totalCredits:0.00}"),
                FormattableString.Invariant($"${totalUsage:0.00}"))
            : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.OpenRouterPrepaid");

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderOpenRouter,
            Name: "OpenRouter",
            ShortName: "Router",
            Monogram: "OR",
            BrandHex: "#8B5CF6",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "Credits",
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: available,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: remPct,
            RadarValueText: formatted,
            RadarSubtext: remPct.HasValue
                ? LocalizationService.FormatForCulture(
                    c,
                    "CustomWidget.AiMonitor.RemainingPctFormat",
                    remPct.Value.ToString("0", CultureInfo.InvariantCulture))
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.MultiModelGateway"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseGlmQuotaJson(
        string json,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        string level = data.TryGetProperty("level", out JsonElement lEl) && lEl.ValueKind == JsonValueKind.String
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase((lEl.GetString() ?? "Coding").ToLowerInvariant())
            : "Coding";

        string label5h = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window5hLabel");
        string label7d = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window7dAllowanceLabel");
        string labelMcp = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.McpToolPoolLabel");
        string autoCycleText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.AutoCycleReset");

        CustomWidgetAiWindowQuota? fiveHour = null;
        CustomWidgetAiWindowQuota? secondary = null;

        if (data.TryGetProperty("limits", out JsonElement limitsArr) &&
            limitsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in limitsArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string type = item.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? string.Empty
                    : string.Empty;
                string window = item.TryGetProperty("window", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.String
                    ? wEl.GetString() ?? string.Empty
                    : string.Empty;

                double usedPct;
                if (item.TryGetProperty("percentage", out JsonElement pctEl) &&
                    (pctEl.ValueKind == JsonValueKind.Number || pctEl.ValueKind == JsonValueKind.String))
                {
                    usedPct = ParseStringOrNumber(item, "percentage");
                }
                else
                {
                    double limit = ParseStringOrNumber(item, "limit");
                    if (limit <= 0) limit = ParseStringOrNumber(item, "total");
                    double usage = ParseStringOrNumber(item, "usage");
                    if (usage <= 0 && limit > 0 && item.TryGetProperty("remaining", out _))
                    {
                        usage = Math.Max(0, limit - ParseStringOrNumber(item, "remaining"));
                    }
                    usedPct = limit > 0 ? Math.Clamp(usage / limit * 100.0, 0.0, 100.0) : 0.0;
                }

                DateTimeOffset? resetAt = ReadEpochMsOrSec(item, "nextResetTime") ?? ReadEpochMsOrSec(item, "resetTime");
                var q = new CustomWidgetAiWindowQuota(
                    Label: window.Contains("week", StringComparison.OrdinalIgnoreCase)
                        ? label7d
                        : type.Contains("TIME", StringComparison.OrdinalIgnoreCase)
                            ? labelMcp
                            : label5h,
                    UsedPercent: Math.Clamp(usedPct, 0.0, 100.0),
                    ResetsAt: resetAt,
                    ResetText: resetAt is not null ? FormatResetCountdown(resetAt, now, c) : autoCycleText,
                    Health: EvaluateWindowHealth(usedPct));

                if (fiveHour is null &&
                    (window.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
                     type.Contains("TOKENS", StringComparison.OrdinalIgnoreCase)))
                {
                    fiveHour = q with { Label = label5h };
                }
                else if (secondary is null)
                {
                    secondary = q;
                }
            }
        }

        fiveHour ??= new CustomWidgetAiWindowQuota(
            label5h,
            0,
            null,
            LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.QuotaHealthy"),
            AiQuotaHealth.Healthy);
        secondary ??= new CustomWidgetAiWindowQuota(
            LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.McpExtPoolLabel"),
            0,
            null,
            LocalizationService.TranslateForCulture(
                c,
                AiProviderCatalog.IsInternationalRegion(region)
                    ? "CustomWidget.AiMonitor.NodeZai"
                    : "CustomWidget.AiMonitor.NodeBigModel"),
            AiQuotaHealth.Healthy);

        bool paceHot = EvaluateWindowPaceIsHot(fiveHour.UsedPercent, fiveHour.ResetsAt, now, TimeSpan.FromHours(5));
        if (paceHot && fiveHour.Health == AiQuotaHealth.Healthy)
        {
            fiveHour = fiveHour with { Health = AiQuotaHealth.Warning };
        }
        AiQuotaHealth overall = (AiQuotaHealth)Math.Max((int)fiveHour.Health, (int)secondary.Health);

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderGlm,
            Name: GetProviderDisplayName(ProviderGlm, c),
            ShortName: "GLM",
            Monogram: "G",
            BrandHex: "#06B6D4",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: level,
            Health: overall,
            PrimaryWindow: fiveHour,
            SecondaryWindow: secondary,
            CurrencySymbol: "¥",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: FormattableString.Invariant($"{Math.Round(fiveHour.RemainingPercent):0}%"),
            RadarSubtext: fiveHour.ResetsAt is not null ? $"{fiveHour.Label} · {fiveHour.ResetText}" : $"GLM {level}",
            ErrorMessage: null,
            UpdatedAt: now,
            IsPaceHot: paceHot);
    }

    internal static CustomWidgetAiProviderSnapshot ParseCloudProviderBody(
        string providerId,
        string rawBody,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        return providerId.ToLowerInvariant() switch
        {
            ProviderDeepSeek => ParseDeepSeekBalanceJson(rawBody, now, cultureName),
            ProviderSiliconFlow => ParseSiliconFlowBalanceJson(rawBody, region, now, cultureName),
            ProviderKimi => ParseKimiBalanceJson(rawBody, region, now, cultureName),
            ProviderGlm => ParseGlmQuotaJson(rawBody, region, now, cultureName),
            ProviderMiniMax => ParseMiniMaxQuotaJson(rawBody, region, now, cultureName),
            ProviderOpenRouter => ParseOpenRouterCreditsJson(rawBody, now, cultureName),
            ProviderOneApi => ParseOneApiBillingJson(rawBody, now, cultureName),
            _ => CreateUnconfiguredSnapshot(providerId, now, cultureName)
        };
    }

    internal static async Task<(CustomWidgetAiProviderSnapshot Snapshot, string? RawBody)> FetchCloudProviderInternalAsync(
        HttpClient httpClient,
        AiProviderCredentialEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        string id = entry.ProviderId.ToLowerInvariant();
        if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            return (CreateUnconfiguredSnapshot(id, now, c), null);
        }

        try
        {
            if (id == ProviderMiniMax)
            {
                return await FetchMiniMaxProviderInternalAsync(httpClient, entry, now, cancellationToken, c);
            }

            if (id == ProviderOneApi)
            {
                return await FetchOneApiProviderInternalAsync(httpClient, entry, now, cancellationToken, c);
            }

            string url = id switch
            {
                ProviderDeepSeek => "https://api.deepseek.com/user/balance",
                ProviderSiliconFlow => AiProviderCatalog.IsInternationalRegion(entry.Region)
                    ? "https://api.siliconflow.com/v1/user/info"
                    : "https://api.siliconflow.cn/v1/user/info",
                ProviderKimi => AiProviderCatalog.IsInternationalRegion(entry.Region)
                    ? "https://api.moonshot.ai/v1/users/me/balance"
                    : "https://api.moonshot.cn/v1/users/me/balance",
                ProviderGlm => AiProviderCatalog.IsInternationalRegion(entry.Region)
                    ? "https://api.z.ai/api/monitor/usage/quota/limit"
                    : "https://open.bigmodel.cn/api/monitor/usage/quota/limit",
                ProviderOpenRouter => "https://openrouter.ai/api/v1/credits",
                _ => throw new InvalidOperationException($"Unsupported AI provider '{id}'.")
            };

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage resp = await httpClient.SendAsync(req, cancellationToken);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                string err = (int)resp.StatusCode is 401 or 403
                    ? LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrInvalidKey")
                    : LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ErrHttpFormat", (int)resp.StatusCode);
                return (CreateErrorSnapshot(id, err, now, c), null);
            }

            return (ParseCloudProviderBody(id, body, entry.Region, now, c), body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (CreateErrorSnapshot(
                id,
                LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrTimeout"),
                now,
                c), null);
        }
        catch (Exception ex)
        {
            return (CreateErrorSnapshot(
                id,
                LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ErrNetworkFormat", ex.GetType().Name),
                now,
                c), null);
        }
    }

    public static CustomWidgetAiProviderSnapshot CreateUnconfiguredSnapshot(
        string providerId,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var meta = GetCatalog(c).FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(meta.Id))
        {
            meta = (providerId, providerId, providerId, "AI", "#06B6D4", false);
        }

        string lowerId = providerId.ToLowerInvariant();
        AiProviderBillingKind kind = lowerId is ProviderClaude or ProviderCodex or ProviderGlm or ProviderMiniMax
            ? AiProviderBillingKind.WindowQuota
            : AiProviderBillingKind.WalletBalance;

        return new CustomWidgetAiProviderSnapshot(
            Id: meta.Id,
            Name: meta.Name,
            ShortName: meta.ShortName,
            Monogram: meta.Monogram,
            BrandHex: meta.BrandHex,
            BillingKind: kind,
            IsLocalCli: meta.IsLocalCli,
            IsConfigured: false,
            HasLiveData: false,
            PlanBadge: string.Empty,
            Health: AiQuotaHealth.Idle,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: lowerId == ProviderGeminiCli ? "$" : "¥",
            WalletBalance: 0,
            WalletFormatted: "--",
            WalletBreakdown: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.NoKeyConfigured"),
            WalletRemainingPercent: null,
            RadarValueText: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Disconnected"),
            RadarSubtext: LocalizationService.TranslateForCulture(
                c,
                meta.IsLocalCli
                    ? "CustomWidget.AiMonitor.WaitLocalSession"
                    : "CustomWidget.AiMonitor.ClickToConfigureKey"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot CreateErrorSnapshot(
        string providerId,
        string errorMessage,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        CustomWidgetAiProviderSnapshot baseSnap = CreateUnconfiguredSnapshot(providerId, now, c);
        return baseSnap with
        {
            IsConfigured = true,
            HasLiveData = false,
            Health = AiQuotaHealth.Warning,
            WalletFormatted = "--",
            WalletBreakdown = errorMessage,
            RadarValueText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Retry"),
            RadarSubtext = errorMessage,
            ErrorMessage = errorMessage
        };
    }

    public static string ResolveToken(
        string path,
        CustomWidgetAiMonitorSnapshot snapshot,
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        bool showRem = IsRemainingMode(settings);
        CustomWidgetAiProviderSnapshot? focus = SelectFocusProvider(snapshot, state);

        string walletCurrency = ResolveWalletCurrency(settings, c);
        double walletTarget = ResolveWalletTargetBalance(settings);
        string walletTargetSummary = FormatWalletTargetSummary(walletTarget, walletCurrency);

        int errorCount = 0;
        int attentionCount = 0;
        CustomWidgetAiProviderSnapshot? urgent = null;
        int highestUrgency = -1;
        foreach (CustomWidgetAiProviderSnapshot candidate in snapshot.ActiveProviders)
        {
            var (_, effHealth, _, _) = EvaluateEffectiveProviderMetrics(candidate, settings, c);
            if (!candidate.HasLiveData || candidate.ErrorMessage is not null)
            {
                errorCount++;
            }
            else if (effHealth is AiQuotaHealth.Warning or AiQuotaHealth.Critical)
            {
                attentionCount++;
            }

            int urgencyScore = (!candidate.HasLiveData || candidate.ErrorMessage is not null)
                ? 4
                : (int)effHealth;
            if (urgencyScore > highestUrgency)
            {
                highestUrgency = urgencyScore;
                urgent = candidate;
            }
        }
        urgent ??= focus;

        var (urgentPct, urgentHealth, urgentTone, _) = EvaluateEffectiveProviderMetrics(urgent, settings, c);
        var (focusPct, focusHealth, focusTone, _) = EvaluateEffectiveProviderMetrics(focus, settings, c);

        return path switch
        {
            "capsuleTitle" => BuildCapsuleTitle(urgent, showRem, c),
            "capsuleSummary" => BuildCapsuleSummary(urgent, snapshot, c),
            "capsuleBadge" => BuildCapsuleBadge(urgent, showRem, c),
            "capsuleIsWarning" => urgent is not null &&
                                  (urgent.ErrorMessage is not null || urgentHealth is AiQuotaHealth.Warning or AiQuotaHealth.Critical)
                ? "true"
                : "false",
            "capsuleProgress" => urgentPct.ToString("0.0", CultureInfo.InvariantCulture),
            "activeCount" => snapshot.ActiveProviders.Count.ToString(CultureInfo.InvariantCulture),
            "errorCount" => errorCount.ToString(CultureInfo.InvariantCulture),
            "attentionCount" => attentionCount.ToString(CultureInfo.InvariantCulture),
            "statusTone" => snapshot.ActiveProviders.Count == 0 ? "subtle" : urgentTone,
            "hasActiveProviders" => snapshot.ActiveProviders.Count > 0 ? "true" : "false",
            "hasLocalCliData" => (snapshot.LocalSummary.DetectedClaudeFiles + snapshot.LocalSummary.DetectedCodexFiles + snapshot.LocalSummary.DetectedGeminiFiles) > 0 ? "true" : "false",
            "hasTodayActivity" => (snapshot.LocalSummary.TodayTokens > 0 || snapshot.LocalSummary.TodayCostUsd > 0.004) ? "true" : "false",
            "activeDays28" => snapshot.LocalSummary.EffectiveLast28Days.Count(static b => b.TotalTokens > 0 || b.CostUsd > 0.0005).ToString(CultureInfo.InvariantCulture),
            "daily28Summary" => BuildDaily28DefaultSummary(snapshot, c),
            "walletCurrency" => walletCurrency,
            "walletTargetBalance" => ((long)Math.Round(walletTarget)).ToString(CultureInfo.InvariantCulture),
            "walletTargetSummary" => walletTargetSummary,
            "todayTokens" or "tokens" => FormatCompactTokens(snapshot.LocalSummary.TodayTokens),
            "todayCost" or "cost" => FormattableString.Invariant($"${snapshot.LocalSummary.TodayCostUsd:0.00}"),
            "monthTokens" => FormatCompactTokens(snapshot.LocalSummary.Last30DaysTokens),
            "monthCost" => FormattableString.Invariant($"${snapshot.LocalSummary.Last30DaysCostUsd:0.00}"),
            "cacheHitPercent" => FormattableString.Invariant($"{snapshot.LocalSummary.CacheHitPercent:0}%"),
            "focus.id" => focus?.Id ?? string.Empty,
            "focus.name" => focus?.Name ?? string.Empty,
            "focus.shortName" => focus?.ShortName ?? string.Empty,
            "focus.monogram" => focus?.Monogram ?? string.Empty,
            "focus.color" => focus?.BrandHex ?? string.Empty,
            "focus.value" or "balance" => focus?.BillingKind == AiProviderBillingKind.WindowQuota &&
                                          focus.HasLiveData &&
                                          focus.PrimaryWindow is { } fpw
                ? FormattableString.Invariant($"{Math.Round(showRem ? fpw.RemainingPercent : fpw.UsedPercent):0}%")
                : focus?.RadarValueText ?? string.Empty,
            "focus.subtext" => focus?.RadarSubtext ?? string.Empty,
            "focus.badge" => focus?.PlanBadge ?? string.Empty,
            "focus.progress" => focusPct.ToString("0.0", CultureInfo.InvariantCulture),
            "focus.isWindowQuota" => focus?.BillingKind == AiProviderBillingKind.WindowQuota ? "true" : "false",
            "focus.tone" => focusHealth switch
            {
                AiQuotaHealth.Critical => "danger",
                AiQuotaHealth.Warning => "warning",
                _ => focusTone
            },
            "focus.hasSecondary" => focus?.SecondaryWindow is not null ? "true" : "false",
            "focus.secondaryLabel" => focus?.SecondaryWindow?.Label ?? string.Empty,
            "focus.secondaryValue" => focus?.SecondaryWindow is { } sw
                ? FormatWindowPercentText(sw, showRem, out _, c)
                : string.Empty,
            "focus.secondarySubtext" => focus?.SecondaryWindow?.ResetText ?? string.Empty,
            "focus.secondaryProgress" => focus?.SecondaryWindow is { } swp
                ? (showRem ? swp.RemainingPercent : swp.UsedPercent).ToString("0.0", CultureInfo.InvariantCulture)
                : "0",
            "focus.error" => focus?.ErrorMessage ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string BuildDaily28DefaultSummary(CustomWidgetAiMonitorSnapshot snap, string culture)
    {
        int activeDays = snap.LocalSummary.EffectiveLast28Days.Count(static b => b.TotalTokens > 0 || b.CostUsd > 0.0005);
        if (snap.LocalSummary.TodayTokens > 0)
        {
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.AiMonitor.Daily28SummaryTodayTokensFormat",
                activeDays,
                FormatCompactTokens(snap.LocalSummary.TodayTokens),
                FormattableString.Invariant($"${snap.LocalSummary.TodayCostUsd:0.00}"));
        }

        if (snap.LocalSummary.TodayCostUsd > 0.004)
        {
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.AiMonitor.Daily28SummaryTodayCostFormat",
                activeDays,
                FormattableString.Invariant($"${snap.LocalSummary.TodayCostUsd:0.00}"));
        }

        return LocalizationService.FormatForCulture(
            culture,
            "CustomWidget.AiMonitor.Daily28SummaryIdleFormat",
            activeDays);
    }

    private static string BuildCapsuleTitle(CustomWidgetAiProviderSnapshot? p, bool showRem, string culture)
    {
        if (p is null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleDisconnectedTitle");
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            string pct = FormatWindowPercentText(w, showRem, out string word, culture);
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.AiMonitor.CapsuleWindowTitleFormat",
                p.ShortName,
                word,
                pct);
        }

        return $"{p.ShortName} · {p.WalletFormatted}";
    }

    private static string BuildCapsuleSummary(
        CustomWidgetAiProviderSnapshot? p,
        CustomWidgetAiMonitorSnapshot snap,
        string culture)
    {
        if (p is null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleDisconnectedSummary");
        }

        if (p.ErrorMessage is not null)
        {
            return p.ErrorMessage;
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            return snap.LocalSummary.TodayTokens > 0
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.AiMonitor.CapsuleTodayTokensSummaryFormat",
                    w.ResetText,
                    FormatCompactTokens(snap.LocalSummary.TodayTokens))
                : w.ResetText;
        }

        return p.WalletBreakdown;
    }

    private static string BuildCapsuleBadge(CustomWidgetAiProviderSnapshot? p, bool showRem, string culture)
    {
        if (p is null)
        {
            return string.Empty;
        }

        if (p.ErrorMessage is not null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleErrorBadge");
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            return FormatWindowPercentText(w, showRem, out _, culture);
        }

        return p.WalletFormatted;
    }

    private static long ReadLong(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement el))
        {
            return 0;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
        {
            return v;
        }

        if (el.ValueKind == JsonValueKind.String &&
            long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sv))
        {
            return sv;
        }

        return 0;
    }

    private static double? ReadNullableDouble(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
        {
            return sd;
        }

        return null;
    }

    private static double ParseStringOrNumber(JsonElement obj, string propName) =>
        ReadNullableDouble(obj, propName) ?? 0.0;

    private static DateTimeOffset? ReadEpochMsOrSec(JsonElement obj, string propName)
    {
        long raw = ReadLong(obj, propName);
        if (raw <= 0)
        {
            return null;
        }

        try
        {
            return raw > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                : DateTimeOffset.FromUnixTimeSeconds(raw);
        }
        catch
        {
            return null;
        }
    }
}
