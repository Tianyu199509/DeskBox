using System.Globalization;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal sealed class SystemMetricsCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private CustomWidgetSystemMetricsSnapshot _systemMetrics = CustomWidgetSystemMetricsSnapshot.Empty;
    private bool _isSubscribedToSystemMetrics;
    private bool _isSystemMetricsStandIn;

    internal SystemMetricsCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "systemMetrics";
    public IReadOnlyList<string> CapabilityIds => ["systemMetrics"];

    internal CustomWidgetSystemMetricsSnapshot Snapshot => _systemMetrics;

    public void UpdateSubscription()
    {
        bool usesSystemMetrics = _vm.ManifestInternal.Sources.Any(s =>
            string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase));
        bool activeWhileCollapsed = !_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesSystemMetrics(_vm.ManifestInternal.Capsule);
        bool wantsMetrics = !_vm.IsDisposedInternal &&
                            _vm.IsWindowVisibleInternal &&
                            !_vm.IsStudioGalleryThumbnailPreview &&
                            activeWhileCollapsed &&
                            usesSystemMetrics;

        if (wantsMetrics)
        {
            _isSystemMetricsStandIn = false;
            CustomWidgetMetricsSubscriptionOptions options = BuildMetricsSubscriptionOptions();
            if (!_isSubscribedToSystemMetrics)
            {
                _isSubscribedToSystemMetrics = true;
                CustomWidgetSystemMetricsSource.Shared.MetricsUpdated += OnSystemMetricsUpdated;
                CustomWidgetSystemMetricsSource.Shared.Subscribe(_vm, options);
                _systemMetrics = CustomWidgetSystemMetricsSource.Shared.Latest;
                _vm.RefreshCapsuleProjection(bumpLiveState: false);
                _vm.NotifyMetricsRefreshed();
            }
            else
            {
                CustomWidgetSystemMetricsSource.Shared.Subscribe(_vm, options);
            }
        }
        else
        {
            if (_isSubscribedToSystemMetrics)
            {
                _isSubscribedToSystemMetrics = false;
                CustomWidgetSystemMetricsSource.Shared.MetricsUpdated -= OnSystemMetricsUpdated;
                CustomWidgetSystemMetricsSource.Shared.Unsubscribe(_vm);
            }

            if (_vm.IsStudioGalleryThumbnailPreview &&
                !_vm.IsDisposedInternal &&
                usesSystemMetrics &&
                !_isSystemMetricsStandIn)
            {
                _systemMetrics = BuildStudioPreviewSystemMetricsStandIn();
                _isSystemMetricsStandIn = true;
                _vm.RefreshCapsuleProjection(bumpLiveState: false);
            }
        }
    }

    private static CustomWidgetSystemMetricsSnapshot BuildStudioPreviewSystemMetricsStandIn() => new()
    {
        CpuPercent = 28,
        CpuHistory = [18, 22, 25, 31, 28],
        MemoryPercent = 54,
        MemoryUsedGb = 17.3,
        MemoryTotalGb = 32,
        MemoryHistory = [52, 53, 54, 54, 54],
        GpuName = "GPU",
        GpuPercent = 21,
        GpuHistory = [14, 19, 24, 20, 21],
        GpuVramUsedGb = 3.2,
        GpuVramTotalGb = 12,
        GpuVramPercent = 27,
        DiskPrimaryName = "C:",
        DiskPrimaryPercent = 62,
        DiskPrimaryFreeGb = 190,
        DiskPrimaryTotalGb = 500,
        DiskByDrive = new Dictionary<string, CustomWidgetDiskMetrics>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:"] = new CustomWidgetDiskMetrics("C:", 62, 190, 500)
        },
        NetDownText = "1.8 MB/s",
        NetUpText = "240 KB/s",
        NetHistory = [22, 35, 28, 40, 26],
        BatteryPercent = 86,
        BatteryOnAc = true,
        HasBattery = true,
        UptimeMs = 14_400_000,
        UptimeText = "4h 00m"
    };

    public void Stop()
    {
        if (_isSubscribedToSystemMetrics)
        {
            _isSubscribedToSystemMetrics = false;
            CustomWidgetSystemMetricsSource.Shared.MetricsUpdated -= OnSystemMetricsUpdated;
            CustomWidgetSystemMetricsSource.Shared.Unsubscribe(_vm);
        }
    }

    public void Dispose() => Stop();

    internal CustomWidgetMetricsSubscriptionOptions BuildMetricsSubscriptionOptions()
    {
        int defaultInterval = _vm.ManifestInternal.Sources
            .FirstOrDefault(s => string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase))
            ?.IntervalMs ?? 2000;

        int intervalMs = defaultInterval;
        if (_vm.SettingsInternal.TryGetValue("refreshRate", out string? rateRaw) &&
            int.TryParse(rateRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate))
        {
            intervalMs = Math.Clamp(parsedRate, 500, 60000);
        }

        bool capsuleUsesGpu = CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "sys.gpu.");
        if (_vm.IsCompactCollapsedInternal)
        {
            return new CustomWidgetMetricsSubscriptionOptions(
                IntervalMs: intervalMs,
                EnableGpu: capsuleUsesGpu,
                EnableProcesses: false);
        }

        CustomWidgetSettingDefinition? moduleDef = _vm.ManifestInternal.Settings.FirstOrDefault(
            static d => string.Equals(d.Type, "moduleList", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));

        bool enableGpu;
        bool enableProcesses;
        if (moduleDef is not null)
        {
            _vm.SettingsInternal.TryGetValue(moduleDef.Key, out string? rawModules);
            bool hasGpuOption = moduleDef.Options.Any(static o =>
                string.Equals(o.Value, "gpu", StringComparison.OrdinalIgnoreCase));
            bool hasProcOption = moduleDef.Options.Any(static o =>
                string.Equals(o.Value, "processes", StringComparison.OrdinalIgnoreCase));

            enableGpu = capsuleUsesGpu ||
                        (hasGpuOption
                            ? CustomWidgetModuleListHelper.IsModuleEnabled(rawModules, "gpu", moduleDef.Options)
                            : CustomWidgetViewModel.NodeTreeReferencesGpu(_vm.ManifestInternal.View));
            enableProcesses = hasProcOption
                ? CustomWidgetModuleListHelper.IsModuleEnabled(rawModules, "processes", moduleDef.Options)
                : CustomWidgetViewModel.NodeTreeReferencesProcesses(_vm.ManifestInternal.View);
        }
        else
        {
            enableGpu = capsuleUsesGpu || CustomWidgetViewModel.NodeTreeReferencesGpu(_vm.ManifestInternal.View);
            enableProcesses = CustomWidgetViewModel.NodeTreeReferencesProcesses(_vm.ManifestInternal.View);
        }

        return new CustomWidgetMetricsSubscriptionOptions(intervalMs, enableGpu, enableProcesses);
    }


    internal void OnSystemMetricsUpdated(CustomWidgetSystemMetricsSnapshot snapshot)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        void ApplyOnUi()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }
            _systemMetrics = snapshot;
            _vm.RefreshCapsuleProjection(bumpLiveState: false);
            _vm.NotifyMetricsRefreshed();
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(ApplyOnUi);
        }
        else
        {
            ApplyOnUi();
        }
    }

}

internal sealed class AiMonitorCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private CustomWidgetAiMonitorSnapshot _aiMonitorSnapshot = CustomWidgetAiMonitorSnapshot.Empty;
    private bool _isSubscribedToAiMonitor;
    private bool _isAiMonitorStandIn;

    internal AiMonitorCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "aiMonitor";
    public IReadOnlyList<string> CapabilityIds => ["aiMonitor"];

    internal CustomWidgetAiMonitorSnapshot Snapshot => _aiMonitorSnapshot;
    internal bool IsSubscribed => _isSubscribedToAiMonitor;

    public void UpdateSubscription() => UpdateAiMonitorSubscription();

    public void Stop()
    {
        if (_isSubscribedToAiMonitor)
        {
            _isSubscribedToAiMonitor = false;
            CustomWidgetAiMonitorSource.Shared.SnapshotUpdated -= OnAiMonitorSnapshotUpdated;
            CustomWidgetAiMonitorSource.Shared.Unsubscribe(_vm);
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesAiMonitor() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase)) ||
        NodeTreeReferencesAiMonitor(_vm.ManifestInternal.View);

    internal static bool NodeTreeReferencesAiMonitor(CustomWidgetNodeSpec node) =>
        CustomWidgetViewModel.NodeTreeReferencesSubstring(node, "$ai.");

    internal void UpdateAiMonitorSubscription()
    {
        bool usesAi = ManifestUsesAiMonitor();
        bool activeWhileCollapsed = !_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "ai.");
        bool wantsAi = !_vm.IsDisposedInternal &&
                       _vm.IsWindowVisibleInternal &&
                       !_vm.IsStudioGalleryThumbnailPreview &&
                       activeWhileCollapsed &&
                       usesAi;

        if (wantsAi)
        {
            _isAiMonitorStandIn = false;
            int intervalMs = _vm.ManifestInternal.Sources
                .FirstOrDefault(static s => string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase))
                ?.IntervalMs ?? 60_000;
            if (_vm.SettingsInternal.TryGetValue("refreshIntervalMs", out string? rawRate) &&
                int.TryParse(rawRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate))
            {
                intervalMs = Math.Clamp(parsedRate, 15_000, 900_000);
            }

            if (!_isSubscribedToAiMonitor)
            {
                _isSubscribedToAiMonitor = true;
                CustomWidgetAiMonitorSource.Shared.SnapshotUpdated += OnAiMonitorSnapshotUpdated;
                _aiMonitorSnapshot = CustomWidgetAiMonitorSource.Shared.Latest;
                CustomWidgetAiMonitorSource.Shared.Subscribe(_vm, intervalMs);
                _vm.RefreshCapsuleProjection(bumpLiveState: false);
            }
            else
            {
                CustomWidgetAiMonitorSource.Shared.Subscribe(_vm, intervalMs);
            }
        }
        else
        {
            if (_isSubscribedToAiMonitor)
            {
                _isSubscribedToAiMonitor = false;
                CustomWidgetAiMonitorSource.Shared.SnapshotUpdated -= OnAiMonitorSnapshotUpdated;
                CustomWidgetAiMonitorSource.Shared.Unsubscribe(_vm);
            }

            if ((_vm.IsStudioGalleryThumbnailPreview ||
                 (_vm.IsPreviewOnlyInternal && CustomWidgetAiMonitorSource.Shared.Latest.ActiveProviders.Count == 0)) &&
                !_vm.IsDisposedInternal &&
                usesAi &&
                !_isAiMonitorStandIn)
            {
                _aiMonitorSnapshot = BuildStudioPreviewAiMonitorStandIn(_vm.ActiveCultureName);
                _isAiMonitorStandIn = true;
                _vm.RefreshCapsuleProjection(bumpLiveState: false);
            }
        }
    }

    private static CustomWidgetAiMonitorSnapshot BuildStudioPreviewAiMonitorStandIn(string cultureName)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var daily28 = new List<CustomWidgetAiDailyBucket>(28);
        long[] sample28 =
        [
            84_000, 142_000, 198_000, 0, 112_000, 165_000, 520_000,
            136_000, 188_000, 224_000, 148_000, 0, 0, 174_000,
            210_000, 265_000, 580_000, 194_000, 152_000, 118_000, 0,
            184_000, 241_000, 152_000, 318_000, 275_000, 210_000, 294_000
        ];
        for (int i = 0; i < 28; i++)
        {
            long tokens = sample28[i];
            var bucket = new CustomWidgetAiDailyBucket(today.AddDays(-(27 - i)), tokens, Math.Round(tokens * 0.000011, 2));
            daily28.Add(bucket);
        }

        string resetIn2h = LocalizationService.FormatForCulture(cultureName, "CustomWidget.AiMonitor.ResetHoursMinutesFormat", 2, 18);
        string healthyPct = LocalizationService.FormatForCulture(cultureName, "CustomWidget.AiMonitor.RemainingPctFormat", 72);

        var claude = new CustomWidgetAiProviderSnapshot(
            Id: "claude",
            Name: "Claude Code",
            ShortName: "Claude",
            Monogram: "CC",
            BrandHex: "#D97757",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: true,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "Pro",
            Health: AiQuotaHealth.Healthy,
            PrimaryWindow: new CustomWidgetAiWindowQuota("5h", 28, now.AddHours(2).AddMinutes(18), resetIn2h, AiQuotaHealth.Healthy),
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: "72%",
            RadarSubtext: healthyPct,
            ErrorMessage: null,
            UpdatedAt: now);

        var codex = new CustomWidgetAiProviderSnapshot(
            Id: "codex",
            Name: "OpenAI Codex",
            ShortName: "Codex",
            Monogram: "CX",
            BrandHex: "#10A37F",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: true,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "Plus",
            Health: AiQuotaHealth.Healthy,
            PrimaryWindow: new CustomWidgetAiWindowQuota("5h", 8, now.AddHours(3).AddMinutes(40), resetIn2h, AiQuotaHealth.Healthy),
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: "92%",
            RadarSubtext: resetIn2h,
            ErrorMessage: null,
            UpdatedAt: now);

        var deepseek = new CustomWidgetAiProviderSnapshot(
            Id: "deepseek",
            Name: "DeepSeek",
            ShortName: "DeepSeek",
            Monogram: "DS",
            BrandHex: "#3B82F6",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "CNY",
            Health: AiQuotaHealth.Warning,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "¥",
            WalletBalance: 18.50,
            WalletFormatted: "¥ 18.50",
            WalletBreakdown: LocalizationService.FormatForCulture(cultureName, "CustomWidget.AiMonitor.DeepSeekBreakdownTopupFormat", "CNY"),
            WalletRemainingPercent: null,
            RadarValueText: "¥ 18.50",
            RadarSubtext: LocalizationService.TranslateForCulture(cultureName, "CustomWidget.AiMonitor.DeepSeekSubtextOfficial"),
            ErrorMessage: null,
            UpdatedAt: now);

        var summary = new CustomWidgetAiLocalSummary(
            TodayTokens: sample28[^1],
            TodayCostUsd: Math.Round(sample28[^1] * 0.000011, 2),
            CacheHitPercent: 78.4,
            Last30DaysTokens: 5_290_000,
            Last30DaysCostUsd: 42.6,
            DetectedClaudeFiles: 6,
            DetectedCodexFiles: 4,
            Last28Days: daily28,
            DetectedGeminiFiles: 2);

        return new CustomWidgetAiMonitorSnapshot(
            AllProviders: [claude, codex, deepseek],
            ActiveProviders: [claude, codex, deepseek],
            LocalSummary: summary,
            UpdatedAt: now);
    }

    internal void OnAiMonitorSnapshotUpdated(CustomWidgetAiMonitorSnapshot snapshot)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        void ApplyOnUi()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            CustomWidgetAiMonitorSnapshot previous = _aiMonitorSnapshot;
            bool visuallyUnchanged = AreAiMonitorSnapshotsVisuallyEqual(previous, snapshot);
            bool structurallyEqual = AreAiMonitorSnapshotsStructurallyEqual(previous, snapshot, _vm.StateInternal);
            _aiMonitorSnapshot = snapshot;
            if (visuallyUnchanged)
            {
                return;
            }

            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            if (structurallyEqual)
            {
                _vm.NotifyMetricsRefreshed();
            }
            else
            {
                _vm.NotifyViewRebuildRequested();
            }
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(ApplyOnUi);
        }
        else
        {
            ApplyOnUi();
        }
    }

    internal static bool AreAiMonitorSnapshotsStructurallyEqual(
        CustomWidgetAiMonitorSnapshot a,
        CustomWidgetAiMonitorSnapshot b,
        IReadOnlyDictionary<string, string>? state = null)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.ActiveProviders.Count != b.ActiveProviders.Count ||
            a.LocalSummary.EffectiveLast28Days.Count != b.LocalSummary.EffectiveLast28Days.Count)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> effectiveState = state ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CustomWidgetAiProviderSnapshot? focusA = CustomWidgetAiMonitorEngine.SelectFocusProvider(a, effectiveState);
        CustomWidgetAiProviderSnapshot? focusB = CustomWidgetAiMonitorEngine.SelectFocusProvider(b, effectiveState);
        if (!string.Equals(focusA?.Id, focusB?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 0; i < a.ActiveProviders.Count; i++)
        {
            CustomWidgetAiProviderSnapshot pa = a.ActiveProviders[i];
            CustomWidgetAiProviderSnapshot pb = b.ActiveProviders[i];
            if (!string.Equals(pa.Id, pb.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pa.Name, pb.Name, StringComparison.Ordinal) ||
                !string.Equals(pa.ShortName, pb.ShortName, StringComparison.Ordinal) ||
                !string.Equals(pa.Monogram, pb.Monogram, StringComparison.Ordinal) ||
                !string.Equals(pa.BrandHex, pb.BrandHex, StringComparison.Ordinal) ||
                pa.BillingKind != pb.BillingKind ||
                (pa.ErrorMessage is not null) != (pb.ErrorMessage is not null) ||
                pa.WalletRemainingPercent.HasValue != pb.WalletRemainingPercent.HasValue)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreAiMonitorSnapshotsVisuallyEqual(
        CustomWidgetAiMonitorSnapshot a,
        CustomWidgetAiMonitorSnapshot b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.AllProviders.Count != b.AllProviders.Count ||
            a.ActiveProviders.Count != b.ActiveProviders.Count)
        {
            return false;
        }

        for (int i = 0; i < a.AllProviders.Count; i++)
        {
            if (!AreAiProvidersVisuallyEqual(a.AllProviders[i], b.AllProviders[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < a.ActiveProviders.Count; i++)
        {
            if (!string.Equals(a.ActiveProviders[i].Id, b.ActiveProviders[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        CustomWidgetAiLocalSummary la = a.LocalSummary;
        CustomWidgetAiLocalSummary lb = b.LocalSummary;
        if (la.TodayTokens != lb.TodayTokens ||
            Math.Round(la.TodayCostUsd, 2) != Math.Round(lb.TodayCostUsd, 2) ||
            Math.Round(la.CacheHitPercent, 0) != Math.Round(lb.CacheHitPercent, 0) ||
            la.Last30DaysTokens != lb.Last30DaysTokens ||
            Math.Round(la.Last30DaysCostUsd, 2) != Math.Round(lb.Last30DaysCostUsd, 2) ||
            la.DetectedClaudeFiles != lb.DetectedClaudeFiles ||
            la.DetectedCodexFiles != lb.DetectedCodexFiles ||
            la.DetectedGeminiFiles != lb.DetectedGeminiFiles ||
            la.EffectiveLast28Days.Count != lb.EffectiveLast28Days.Count)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetAiDailyBucket> b28A = la.EffectiveLast28Days;
        IReadOnlyList<CustomWidgetAiDailyBucket> b28B = lb.EffectiveLast28Days;
        for (int i = 0; i < b28A.Count; i++)
        {
            if (b28A[i].Date != b28B[i].Date ||
                b28A[i].TotalTokens != b28B[i].TotalTokens ||
                Math.Round(b28A[i].CostUsd, 2) != Math.Round(b28B[i].CostUsd, 2))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreAiProvidersVisuallyEqual(
        CustomWidgetAiProviderSnapshot a,
        CustomWidgetAiProviderSnapshot b)
    {
        if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
            a.BillingKind != b.BillingKind ||
            a.IsConfigured != b.IsConfigured ||
            a.HasLiveData != b.HasLiveData ||
            !string.Equals(a.PlanBadge, b.PlanBadge, StringComparison.Ordinal) ||
            a.Health != b.Health ||
            !AreAiWindowsVisuallyEqual(a.PrimaryWindow, b.PrimaryWindow) ||
            !AreAiWindowsVisuallyEqual(a.SecondaryWindow, b.SecondaryWindow) ||
            !string.Equals(a.CurrencySymbol, b.CurrencySymbol, StringComparison.Ordinal) ||
            Math.Round(a.WalletBalance, 2) != Math.Round(b.WalletBalance, 2) ||
            !string.Equals(a.WalletFormatted, b.WalletFormatted, StringComparison.Ordinal) ||
            !string.Equals(a.WalletBreakdown, b.WalletBreakdown, StringComparison.Ordinal) ||
            Math.Round(a.WalletRemainingPercent ?? -1.0, 1) != Math.Round(b.WalletRemainingPercent ?? -1.0, 1) ||
            Math.Round(a.WalletTodaySpend ?? -1.0, 2) != Math.Round(b.WalletTodaySpend ?? -1.0, 2) ||
            a.IsPaceHot != b.IsPaceHot ||
            !string.Equals(a.RadarValueText, b.RadarValueText, StringComparison.Ordinal) ||
            !string.Equals(a.RadarSubtext, b.RadarSubtext, StringComparison.Ordinal) ||
            !string.Equals(a.ErrorMessage, b.ErrorMessage, StringComparison.Ordinal))
        {
            return false;
        }

        if (a.BillingKind == AiProviderBillingKind.WalletBalance)
        {
            string timeA = a.UpdatedAt > DateTimeOffset.MinValue
                ? a.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            string timeB = b.UpdatedAt > DateTimeOffset.MinValue
                ? b.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            if (!string.Equals(timeA, timeB, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreAiWindowsVisuallyEqual(
        CustomWidgetAiWindowQuota? a,
        CustomWidgetAiWindowQuota? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(a.Label, b.Label, StringComparison.Ordinal) &&
               Math.Round(a.UsedPercent, 1) == Math.Round(b.UsedPercent, 1) &&
               string.Equals(a.ResetText, b.ResetText, StringComparison.Ordinal) &&
               a.Health == b.Health;
    }

}

internal sealed class DeviceStateCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _deviceStateTimer;
    private IDisposable? _recycleBinSubscription;
    private CustomWidgetDeviceStateSnapshot _deviceStateSnapshot = CustomWidgetDeviceStateSnapshot.Empty;
    private int _deviceRefreshInFlight;
    private CustomWidgetManifest? _cachedFieldsManifest;
    private CustomWidgetCompiledScript? _cachedFieldsScript;
    private bool _cachedFieldsCollapsed;
    private DeviceStateQueryFields _cachedRequiredFields;

    internal DeviceStateCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "deviceState";
    public IReadOnlyList<string> CapabilityIds => ["deviceState"];

    internal CustomWidgetDeviceStateSnapshot Snapshot
    {
        get => _deviceStateSnapshot;
        set => _deviceStateSnapshot = value;
    }

    public void UpdateSubscription() => UpdateDeviceStateSubscription();

    public void Stop()
    {
        _recycleBinSubscription?.Dispose();
        _recycleBinSubscription = null;
        if (_deviceStateTimer is not null)
        {
            _deviceStateTimer.Stop();
            _deviceStateTimer.Tick -= OnDeviceStateTimerTick;
            _deviceStateTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesDeviceState() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "deviceState", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.NodeTreeContainsType(_vm.ManifestInternal.View, "ControlTile") ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "device.");

    internal void UpdateDeviceStateSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "device.")) &&
                         ManifestUsesDeviceState() &&
                         _vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard);

        if (!shouldRun)
        {
            _recycleBinSubscription?.Dispose();
            _recycleBinSubscription = null;
            if (_deviceStateTimer is not null)
            {
                _deviceStateTimer.Stop();
                _deviceStateTimer.Tick -= OnDeviceStateTimerTick;
                _deviceStateTimer = null;
            }
            if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard) &&
                !ReferenceEquals(_deviceStateSnapshot, CustomWidgetDeviceStateSnapshot.Empty))
            {
                _deviceStateSnapshot = CustomWidgetDeviceStateSnapshot.Empty;
                _vm.RefreshCapsuleProjection(bumpLiveState: true);
                _vm.NotifyViewRebuildRequested();
            }
            return;
        }

        DeviceStateQueryFields requiredFields = GetRequiredDeviceFields();
        if ((requiredFields & DeviceStateQueryFields.RecycleBin) != 0 && _vm.DispatcherQueueInternal is not null)
        {
            _recycleBinSubscription ??= CustomWidgetRecycleBinService.Shared.Subscribe(
                state =>
                {
                    _ = state;
                    if (!_vm.IsDisposedInternal)
                    {
                        _ = RefreshDeviceStateNowAsync();
                    }
                },
                _vm.DispatcherQueueInternal,
                attachNativeWatcher: true);
        }
        else if (_recycleBinSubscription is not null)
        {
            _recycleBinSubscription.Dispose();
            _recycleBinSubscription = null;
        }

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "deviceState", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 3000, 2000, 60_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_deviceStateTimer is null)
            {
                _deviceStateTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _deviceStateTimer.IsRepeating = true;
                _deviceStateTimer.Tick += OnDeviceStateTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_deviceStateTimer.Interval != interval)
            {
                _deviceStateTimer.Interval = interval;
            }
            if (!_deviceStateTimer.IsRunning)
            {
                _deviceStateTimer.Start();
            }
        }

        _ = RefreshDeviceStateNowAsync();
    }

    internal void OnDeviceStateTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshDeviceStateNowAsync();
        }
    }

    internal async Task RefreshDeviceStateNowAsync()
    {
        if (_vm.IsDisposedInternal || !ManifestUsesDeviceState() || !_vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _deviceRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            DeviceStateQueryFields requiredFields = GetRequiredDeviceFields();
            CustomWidgetDeviceStateSnapshot snap = await _vm.SystemControlServiceInternal.RefreshSnapshotAsync(requiredFields);
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            void ApplyDeviceOnUi()
            {
                if (_vm.IsDisposedInternal || Equals(_deviceStateSnapshot, snap))
                {
                    return;
                }

                _deviceStateSnapshot = snap;
                _vm.RefreshCapsuleProjection(bumpLiveState: true);
                if (!_vm.ManifestInternal.EnumerateAllNodes().Any(static n => string.Equals(n.Type, "ControlTile", StringComparison.OrdinalIgnoreCase)) &&
                    !CapabilityProviderHelpers.ManifestWhenReferencesToken(_vm.ManifestInternal, "device."))
                {
                    _vm.NotifyMetricsRefreshed();
                }
                else
                {
                    _vm.NotifyViewRebuildRequested();
                }
            }

            if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
            {
                _vm.DispatcherQueueInternal.TryEnqueue(ApplyDeviceOnUi);
            }
            else
            {
                ApplyDeviceOnUi();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _deviceRefreshInFlight, 0);
        }
    }

    internal DeviceStateQueryFields GetRequiredDeviceFields()
    {
        CustomWidgetManifest manifest = _vm.ManifestInternal;
        CustomWidgetCompiledScript? script = _vm.CompiledScriptInternal;
        bool collapsed = _vm.IsCompactCollapsedInternal;

        if (ReferenceEquals(manifest, _cachedFieldsManifest) &&
            ReferenceEquals(script, _cachedFieldsScript) &&
            collapsed == _cachedFieldsCollapsed)
        {
            return _cachedRequiredFields;
        }

        DeviceStateQueryFields fields = ComputeRequiredDeviceFields(manifest, script, collapsed);
        _cachedFieldsManifest = manifest;
        _cachedFieldsScript = script;
        _cachedFieldsCollapsed = collapsed;
        _cachedRequiredFields = fields;
        return fields;
    }

    internal static DeviceStateQueryFields ComputeRequiredDeviceFields(
        CustomWidgetManifest manifest,
        CustomWidgetCompiledScript? compiledScript,
        bool collapsedOnly)
    {
        DeviceStateQueryFields fields = DeviceStateQueryFields.KeepAwake | DeviceStateQueryFields.Theme;

        if (manifest.Capsule is { } cap)
        {
            ScanDeviceExpression(cap.Title, ref fields);
            ScanDeviceExpression(cap.Summary, ref fields);
            ScanDeviceExpression(cap.Glyph, ref fields);
            ScanDeviceExpression(cap.Badge, ref fields);
            ScanDeviceExpression(cap.BadgeIsWarning, ref fields);
            ScanDeviceExpression(cap.Progress, ref fields);
            if (cap.PrimaryAction is { } pa)
            {
                ScanDeviceExpression(pa.Icon, ref fields);
                ScanDeviceExpression(pa.Tooltip, ref fields);
                foreach (CustomWidgetActionSpec act in CustomWidgetNodeSpec.ExpandActionList(pa.OnClick))
                {
                    ScanSystemControlCommand(act, ref fields);
                }
            }
        }

        if (collapsedOnly)
        {
            if (manifest.Script is not null &&
                (CustomWidgetViewModel.CapsuleReferencesToken(manifest.Capsule, "computed.") ||
                 CustomWidgetViewModel.CapsuleReferencesToken(manifest.Capsule, "state.")))
            {
                fields |= compiledScript?.ReferencedDeviceFields ?? DeviceStateQueryFields.All;
            }

            return fields;
        }

        fields |= manifest.ParsedBodyDeviceFields ?? DeviceStateQueryFields.All;
        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
        {
            ScanSystemControlCommand(act, ref fields);
        }

        if (manifest.Script is not null)
        {
            fields |= compiledScript?.ReferencedDeviceFields ?? DeviceStateQueryFields.All;
        }

        return fields;
    }

    internal static DeviceStateQueryFields ScanDeviceTokensInManifestText(string? rawManifestJson)
    {
        DeviceStateQueryFields fields = DeviceStateQueryFields.None;
        ScanDeviceExpression(rawManifestJson, ref fields);
        return fields;
    }

    private static void ScanSystemControlCommand(CustomWidgetActionSpec act, ref DeviceStateQueryFields fields)
    {
        if (!string.Equals(act.Type, "system.control", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string cmd = (act.TargetId ?? act.Key ?? string.Empty).Trim();
        if (cmd.StartsWith("volume.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Volume;
        }
        else if (cmd.StartsWith("brightness.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Brightness;
        }
        else if (cmd.StartsWith("audio.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("sound.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.AudioDevice;
        }
        else if (cmd.StartsWith("radio.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("wifi.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("bluetooth.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Radios;
        }
        else if (cmd.StartsWith("media.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Media;
        }
        else if (cmd.StartsWith("recyclebin.", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.RecycleBin;
        }
    }

    private static void ScanDeviceExpression(string? expr, ref DeviceStateQueryFields fields)
    {
        if (string.IsNullOrEmpty(expr))
        {
            return;
        }

        if (expr.Contains("device[", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.All;
            return;
        }

        if (!expr.Contains("device.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (expr.Contains("device.volume", StringComparison.OrdinalIgnoreCase) ||
            expr.Contains("device.muted", StringComparison.OrdinalIgnoreCase) ||
            expr.Contains("device.isMuted", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Volume;
        }
        if (expr.Contains("device.brightness", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Brightness;
        }
        if (expr.Contains("device.audioDevice", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.AudioDevice;
        }
        if (expr.Contains("device.wifi", StringComparison.OrdinalIgnoreCase) ||
            expr.Contains("device.bluetooth", StringComparison.OrdinalIgnoreCase) ||
            expr.Contains("device.networkConnected", StringComparison.OrdinalIgnoreCase) ||
            expr.Contains("device.online", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Radios;
        }
        if (expr.Contains("device.media", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.Media;
        }
        if (expr.Contains("device.recycleBin", StringComparison.OrdinalIgnoreCase))
        {
            fields |= DeviceStateQueryFields.RecycleBin;
        }
    }

}

internal sealed class StorageDrivesCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _storageDrivesTimer;
    private CustomWidgetStorageDrivesSnapshot _storageDrivesSnapshot = CustomWidgetStorageDrivesSnapshot.Empty;
    private int _storageDrivesRefreshInFlight;
    private bool _isStorageDrivesStandIn;

    internal StorageDrivesCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "storageDrives";
    public IReadOnlyList<string> CapabilityIds => ["storageDrives"];

    internal CustomWidgetStorageDrivesSnapshot Snapshot => _storageDrivesSnapshot;

    public void UpdateSubscription() => UpdateStorageDrivesSubscription();

    public void Stop()
    {
        if (_storageDrivesTimer is not null)
        {
            _storageDrivesTimer.Stop();
            _storageDrivesTimer.Tick -= OnStorageDrivesTimerTick;
            _storageDrivesTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesStorageDrives() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "drives.");

    internal void UpdateStorageDrivesSubscription()
    {
        bool usesDrives = ManifestUsesStorageDrives();
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "drives.")) &&
                         usesDrives;

        if (!shouldRun)
        {
            if (_storageDrivesTimer is not null)
            {
                _storageDrivesTimer.Stop();
                _storageDrivesTimer.Tick -= OnStorageDrivesTimerTick;
                _storageDrivesTimer = null;
            }

            if (_vm.IsStudioGalleryThumbnailPreview &&
                !_vm.IsDisposedInternal &&
                usesDrives &&
                !_isStorageDrivesStandIn)
            {
                CustomWidgetStorageDrivesSnapshot standIn = BuildStudioPreviewStorageDrivesStandIn(_vm.ActiveCultureName);
                ApplyStorageDrivesSnapshot(standIn, triggerRebuild: false, isStandIn: true);
                _isStorageDrivesStandIn = true;
            }
            return;
        }

        _isStorageDrivesStandIn = false;

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 15_000, 5000, 300_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_storageDrivesTimer is null)
            {
                _storageDrivesTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _storageDrivesTimer.IsRepeating = true;
                _storageDrivesTimer.Tick += OnStorageDrivesTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_storageDrivesTimer.Interval != interval)
            {
                _storageDrivesTimer.Interval = interval;
            }
            if (!_storageDrivesTimer.IsRunning)
            {
                _storageDrivesTimer.Start();
            }
        }

        RefreshStorageDrivesNow(triggerRebuild: false);
    }

    internal void OnStorageDrivesTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            RefreshStorageDrivesNow(triggerRebuild: true);
        }
    }

    internal void RefreshStorageDrivesNow(bool triggerRebuild)
    {
        if (_vm.IsDisposedInternal || !ManifestUsesStorageDrives())
        {
            return;
        }

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (Interlocked.CompareExchange(ref _storageDrivesRefreshInFlight, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    if (_vm.IsDisposedInternal)
                    {
                        return;
                    }

                    CustomWidgetStorageDrivesSnapshot asyncSnap = CustomWidgetStorageDrivesSnapshot.Capture(_vm.ActiveCultureName);
                    _vm.DispatcherQueueInternal.TryEnqueue(() => ApplyStorageDrivesSnapshot(asyncSnap, triggerRebuild));
                }
                finally
                {
                    Interlocked.Exchange(ref _storageDrivesRefreshInFlight, 0);
                }
            });
            return;
        }

        ApplyStorageDrivesSnapshot(CustomWidgetStorageDrivesSnapshot.Capture(_vm.ActiveCultureName), triggerRebuild);
    }

    internal void ApplyStorageDrivesSnapshot(CustomWidgetStorageDrivesSnapshot next, bool triggerRebuild, bool isStandIn = false)
    {
        if (_vm.IsDisposedInternal || !ManifestUsesStorageDrives())
        {
            return;
        }

        string targetCol = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase))
            ?.TargetCollection ?? "drives";
        if (string.IsNullOrWhiteSpace(targetCol))
        {
            targetCol = "drives";
        }

        bool summaryChanged = !string.Equals(_storageDrivesSnapshot.Summary, next.Summary, StringComparison.Ordinal) ||
                              _storageDrivesSnapshot.Count != next.Count;
        _storageDrivesSnapshot = next;
        _vm.CollectionsInternal[targetCol] = new List<CustomWidgetRecord>(next.Records);
        if (isStandIn)
        {
            _vm.SampleSeededCollectionsInternal.Add(targetCol);
        }

        if (triggerRebuild && summaryChanged)
        {
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }
    }

    private static CustomWidgetStorageDrivesSnapshot BuildStudioPreviewStorageDrivesStandIn(string cultureName)
    {
        static CustomWidgetRecord MakeDrive(string id, string root, string label, double usedGb, double totalGb, string culture)
        {
            double freeGb = Math.Max(0, totalGb - usedGb);
            int pct = (int)Math.Round(usedGb * 100.0 / totalGb);
            string usedStr = usedGb.ToString("0.0", CultureInfo.InvariantCulture);
            string freeStr = freeGb.ToString("0.0", CultureInfo.InvariantCulture);
            string totalStr = totalGb.ToString("0.0", CultureInfo.InvariantCulture);
            string pctStr = pct.ToString(CultureInfo.InvariantCulture);
            string freeLabel = LocalizationService.FormatForCulture(culture, "CustomWidget.Renderer.Gpu.FreeGbFormat", freeStr);
            return new CustomWidgetRecord
            {
                Id = id,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = root,
                    ["label"] = label,
                    ["title"] = $"{label} ({root})",
                    ["subtitle"] = $"{freeLabel} · {totalStr} GB",
                    ["usedGb"] = usedStr,
                    ["freeGb"] = freeStr,
                    ["totalGb"] = totalStr,
                    ["percent"] = pctStr,
                    ["value"] = pctStr,
                    ["summary"] = $"{usedStr} / {totalStr} GB ({pctStr}%)"
                }
            };
        }

        IReadOnlyList<CustomWidgetRecord> records =
        [
            MakeDrive("c", "C:", "System", 310, 500, cultureName),
            MakeDrive("d", "D:", "Workspace", 420, 1000, cultureName)
        ];

        return new CustomWidgetStorageDrivesSnapshot
        {
            Count = records.Count,
            Summary = "C: 62% · D: 42%",
            PrimaryPercent = 62,
            PrimaryFreeGb = "190.0",
            PrimaryTotalGb = "500.0",
            Records = records
        };
    }

}
