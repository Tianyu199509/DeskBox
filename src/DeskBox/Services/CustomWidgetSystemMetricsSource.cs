using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed record CustomWidgetDiskMetrics(
    string DriveName,
    double Percent,
    double FreeGb,
    double TotalGb);

public sealed record CustomWidgetProcessEntry(
    string Name,
    int InstanceCount,
    double CpuPercent,
    long MemoryBytes,
    string CpuText,
    string MemoryText,
    double CpuBarPercent,
    double MemoryBarPercent,
    string GroupKey = "")
{
    public string GetLocalizedName(string? cultureName = null) =>
        !string.IsNullOrEmpty(GroupKey)
            ? CustomWidgetSystemMetricsSource.FormatFriendlyAppName(GroupKey, cultureName)
            : Name;
}

public readonly record struct CustomWidgetMetricsSubscriptionOptions(
    int IntervalMs = 2000,
    bool EnableGpu = true,
    bool EnableProcesses = true);

public sealed record CustomWidgetSystemMetricsSnapshot
{
    public static CustomWidgetSystemMetricsSnapshot Empty { get; } = new();

    public double CpuPercent { get; init; }
    public IReadOnlyList<double> CpuHistory { get; init; } = [];
    public double MemoryPercent { get; init; }
    public double MemoryUsedGb { get; init; }
    public double MemoryTotalGb { get; init; }
    public IReadOnlyList<double> MemoryHistory { get; init; } = [];

    public string GpuName { get; init; } = "GPU";
    public double GpuPercent { get; init; }
    public IReadOnlyList<double> GpuHistory { get; init; } = [];
    public double GpuVramUsedGb { get; init; }
    public double GpuVramTotalGb { get; init; }
    public double GpuVramPercent { get; init; }
    public bool GpuIsSharedMemory { get; init; }

    public double DiskPrimaryPercent { get; init; }
    public double DiskPrimaryFreeGb { get; init; }
    public double DiskPrimaryTotalGb { get; init; }
    public string DiskPrimaryName { get; init; } = "C:";
    public IReadOnlyDictionary<string, CustomWidgetDiskMetrics> DiskByDrive { get; init; } =
        new Dictionary<string, CustomWidgetDiskMetrics>(StringComparer.OrdinalIgnoreCase);

    public string NetDownText { get; init; } = "0 KB/s";
    public string NetUpText { get; init; } = "0 KB/s";
    public IReadOnlyList<double> NetHistory { get; init; } = [];

    public int BatteryPercent { get; init; } = 100;
    public bool BatteryOnAc { get; init; } = true;
    public bool HasBattery { get; init; }
    public long UptimeMs { get; init; }
    public string UptimeText { get; init; } = string.Empty;

    public IReadOnlyList<CustomWidgetProcessEntry> TopProcessesByMemory { get; init; } = [];
    public IReadOnlyList<CustomWidgetProcessEntry> TopProcessesByCpu { get; init; } = [];

    public string ResolveToken(
        string path,
        IReadOnlyDictionary<string, string>? settings = null,
        string? cultureName = null)
    {
        CustomWidgetDiskMetrics disk = ResolveSelectedDisk(settings);
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        string vramUsedText = GpuVramUsedGb.ToString("0.0", CultureInfo.InvariantCulture);
        string vramTotalText = GpuVramTotalGb >= 10
            ? GpuVramTotalGb.ToString("0", CultureInfo.InvariantCulture)
            : GpuVramTotalGb.ToString("0.0", CultureInfo.InvariantCulture);

        return path switch
        {
            "cpu.percent" => Math.Round(CpuPercent).ToString(CultureInfo.InvariantCulture),
            "cpu.percentExact" => CpuPercent.ToString("0.0", CultureInfo.InvariantCulture),

            "memory.percent" or "ram.percent" => Math.Round(MemoryPercent).ToString(CultureInfo.InvariantCulture),
            "memory.usedGb" or "ram.usedGb" => MemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture),
            "memory.totalGb" or "ram.totalGb" => MemoryTotalGb.ToString("0", CultureInfo.InvariantCulture),
            "memory.summaryText" or "ram.summaryText" => FormattableString.Invariant($"{MemoryUsedGb:0.0} / {MemoryTotalGb:0} GB"),
            "memory.compactText" or "ram.compactText" => FormattableString.Invariant($"{MemoryUsedGb:0.0}/{MemoryTotalGb:0}G"),

            "gpu.name" => GpuName,
            "gpu.percent" => Math.Round(GpuPercent).ToString(CultureInfo.InvariantCulture),
            "gpu.vramUsedGb" => vramUsedText,
            "gpu.vramTotalGb" => vramTotalText,
            "gpu.vramPercent" => Math.Round(GpuVramPercent).ToString(CultureInfo.InvariantCulture),
            "gpu.vramSummary" => GpuVramTotalGb > 0
                ? (GpuIsSharedMemory
                    ? LocalizationService.FormatForCulture(
                        culture,
                        "CustomWidget.Sys.SharedVramSummaryFormat",
                        vramUsedText,
                        vramTotalText)
                    : $"{vramUsedText} / {vramTotalText} GB")
                : "--",
            "gpu.vramCompact" => GpuVramTotalGb > 0
                ? $"{vramUsedText}/{vramTotalText}G"
                : "--",

            "disk.primaryPercent" or "disk.percent" or "disk.selectedPercent" => Math.Round(disk.Percent).ToString(CultureInfo.InvariantCulture),
            "disk.primaryFreeGb" or "disk.freeGb" or "disk.selectedFreeGb" => disk.FreeGb.ToString("0", CultureInfo.InvariantCulture),
            "disk.primaryTotalGb" or "disk.totalGb" or "disk.selectedTotalGb" => disk.TotalGb.ToString("0", CultureInfo.InvariantCulture),
            "disk.primaryName" or "disk.name" or "disk.selectedName" => disk.DriveName,
            "disk.primarySummary" or "disk.summary" or "disk.selectedSummary" =>
                LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Sys.DiskSummaryFormat",
                    disk.FreeGb.ToString("0", CultureInfo.InvariantCulture),
                    disk.Percent.ToString("0", CultureInfo.InvariantCulture)),

            "net.downText" or "network.downText" => NetDownText,
            "net.upText" or "network.upText" => NetUpText,
            "net.summaryText" or "network.summaryText" => $"↓ {NetDownText}  ↑ {NetUpText}",

            "battery.percent" => BatteryPercent.ToString(CultureInfo.InvariantCulture),
            "battery.hasBattery" => HasBattery ? "true" : "false",
            "battery.statusText" => !HasBattery
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.BatteryAcPower")
                : BatteryOnAc
                    ? LocalizationService.FormatForCulture(culture, "CustomWidget.Sys.BatteryChargingFormat", BatteryPercent)
                    : $"{BatteryPercent}%",
            "uptime.text" => ResolveUptimeText(culture),

            "status.label" => HasBattery
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.StatusBatteryLabel")
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.StatusUptimeLabel"),
            "status.value" => HasBattery
                ? (BatteryOnAc
                    ? LocalizationService.FormatForCulture(culture, "CustomWidget.Sys.BatteryChargingFormat", BatteryPercent)
                    : LocalizationService.FormatForCulture(culture, "CustomWidget.Sys.BatteryDischargingFormat", BatteryPercent))
                : ResolveUptimeText(culture),

            _ => string.Empty
        };
    }

    private string ResolveUptimeText(string culture) =>
        UptimeMs > 0
            ? CustomWidgetSystemMetricsSource.FormatUptime(UptimeMs, culture)
            : (!string.IsNullOrEmpty(UptimeText)
                ? UptimeText
                : CustomWidgetSystemMetricsSource.FormatUptime(0, culture));

    public CustomWidgetDiskMetrics ResolveSelectedDisk(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is not null &&
            settings.TryGetValue("targetDrive", out string? targetDrive) &&
            !string.IsNullOrWhiteSpace(targetDrive) &&
            !string.Equals(targetDrive, "auto", StringComparison.OrdinalIgnoreCase))
        {
            string normalized = NormalizeDriveKey(targetDrive);
            if (DiskByDrive.TryGetValue(normalized, out CustomWidgetDiskMetrics? selected))
            {
                return selected;
            }
        }

        return new CustomWidgetDiskMetrics(
            DiskPrimaryName,
            DiskPrimaryPercent,
            DiskPrimaryFreeGb,
            DiskPrimaryTotalGb);
    }

    public IReadOnlyList<double> ResolveHistory(string path)
    {
        if (path.Contains("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return CpuHistory;
        }
        if (path.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("ram", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryHistory;
        }
        if (path.Contains("gpu", StringComparison.OrdinalIgnoreCase))
        {
            return GpuHistory;
        }
        if (path.Contains("net", StringComparison.OrdinalIgnoreCase))
        {
            return NetHistory;
        }
        return [];
    }

    internal static string NormalizeDriveKey(string raw)
    {
        string trimmed = raw.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:";
        }
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:";
        }
        return trimmed;
    }
}

/// <summary>
/// Lightweight, reference-counted host system metrics sampler for Custom Widgets
/// that declare <c>{ "type": "systemMetrics" }</c>. Zero timers run when no
/// custom widget is actively subscribed, and GPU/Process snapshots only run when
/// at least one active subscriber enables those modules.
/// </summary>
public sealed class CustomWidgetSystemMetricsSource
{
    public static CustomWidgetSystemMetricsSource Shared { get; } = new();

    private const int HistoryCapacity = 28;
    private const int MaxTopProcesses = 12;

    private readonly object _gate = new();
    private readonly Dictionary<object, CustomWidgetMetricsSubscriptionOptions> _subscribers = [];
    private readonly List<double> _cpuHistory = new(HistoryCapacity);
    private readonly List<double> _memoryHistory = new(HistoryCapacity);
    private readonly List<double> _gpuHistory = new(HistoryCapacity);
    private readonly List<double> _netHistory = new(HistoryCapacity);
    private readonly Dictionary<string, CustomWidgetDiskMetrics> _cachedDisks =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, CustomWidgetDiskMetrics> _immutableDiskSnapshot =
        new Dictionary<string, CustomWidgetDiskMetrics>(StringComparer.OrdinalIgnoreCase);

    private readonly CustomWidgetNativeMetrics.GpuEngineCounterSampler _gpuCounterSampler = new();
    private readonly CustomWidgetNativeMetrics.DxgiVramSampler _dxgiVramSampler = new();
    private readonly CustomWidgetNativeMetrics.SystemProcessSnapshotReader _processReader = new();
    private readonly List<CustomWidgetNativeMetrics.RawProcessSample> _rawProcesses = new(256);
    private readonly Dictionary<uint, ulong> _prevPidCpuTicks = new(256);
    private readonly Dictionary<uint, ulong> _nextPidCpuTicks = new(256);
    private readonly Dictionary<string, AggregatedProcessBucket> _appBuckets =
        new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private int _currentIntervalMs = 2000;
    private bool _hasCpuBaseline;
    private bool _hasNetBaseline;
    private ulong _prevIdleTicks;
    private ulong _prevTotalTicks;
    private ulong _prevProcSystemTotalTicks;
    private long _prevNetBytesReceived;
    private long _prevNetBytesSent;
    private long _prevNetTimestampTicks;
    private int _diskSampleTickCounter;
    private double _cachedDiskPercent;
    private double _cachedDiskFreeGb;
    private double _cachedDiskTotalGb;
    private string _cachedDiskName = "C:";
    private string _cachedGpuName = "GPU";
    private CustomWidgetSystemMetricsSnapshot _latest = CustomWidgetSystemMetricsSnapshot.Empty;

    private sealed class AggregatedProcessBucket
    {
        public string GroupKey = string.Empty;
        public string DisplayName = string.Empty;
        public int Count;
        public double CpuPercent;
        public long PrivateBytes;
    }

    private CustomWidgetSystemMetricsSource()
    {
        // Do not eagerly open PDH queries, DXGI factories, or network/disk tables
        // on singleton construction. Sampling begins only when a custom widget with
        // a "systemMetrics" source actively subscribes or explicitly calls SampleNow().
    }

    internal int ActiveSubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    internal bool HasOpenGpuSamplers
    {
        get
        {
            lock (_gate)
            {
                return _gpuCounterSampler.IsOpen || _dxgiVramSampler.IsOpen;
            }
        }
    }

    public CustomWidgetSystemMetricsSnapshot Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public event Action<CustomWidgetSystemMetricsSnapshot>? MetricsUpdated;

    public static IReadOnlyList<(string DriveKey, string Label)> GetAvailableFixedDrives(string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var result = new List<(string DriveKey, string Label)>();
        try
        {
            const double OneGb = 1024.0 * 1024.0 * 1024.0;
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0)
                {
                    continue;
                }

                string key = CustomWidgetSystemMetricsSnapshot.NormalizeDriveKey(d.Name);
                double freeGb = d.AvailableFreeSpace / OneGb;
                string label = LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Sys.DriveOptionFormat",
                    key,
                    freeGb.ToString("0", CultureInfo.InvariantCulture));
                result.Add((key, label));
            }
        }
        catch
        {
            // Ignore transient IO failures.
        }

        if (result.Count == 0)
        {
            result.Add(("C:", LocalizationService.FormatForCulture(culture, "CustomWidget.Sys.DriveFallbackLabel", "C:")));
        }

        return result;
    }

    public void Subscribe(object subscriber, CustomWidgetMetricsSubscriptionOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        if (options.IntervalMs <= 0)
        {
            options = options with { IntervalMs = 2000 };
        }

        CustomWidgetSystemMetricsSnapshot? immediateSnapshot = null;
        lock (_gate)
        {
            bool wasEmpty = _subscribers.Count == 0;
            bool hadProcessesBefore = HasActiveProcessSubscriberLocked();
            _subscribers[subscriber] = options;

            if (wasEmpty)
            {
                PrimeBaseline();
            }

            bool needGpu = HasActiveGpuSubscriberLocked();
            bool needProcesses = HasActiveProcessSubscriberLocked();
            if (wasEmpty || (!hadProcessesBefore && needProcesses))
            {
                if (!hadProcessesBefore && needProcesses)
                {
                    PrimeProcessBaselineLocked();
                }
                immediateSnapshot = SampleNowLocked(needGpu, needProcesses);
            }

            ReconcileTimerLocked();
        }

        if (immediateSnapshot is not null)
        {
            MetricsUpdated?.Invoke(immediateSnapshot);
        }
    }

    public void Unsubscribe(object subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        lock (_gate)
        {
            if (!_subscribers.Remove(subscriber))
            {
                return;
            }

            if (!HasActiveGpuSubscriberLocked())
            {
                _gpuCounterSampler.Dispose();
                _dxgiVramSampler.Dispose();
                _gpuHistory.Clear();
            }

            if (!HasActiveProcessSubscriberLocked())
            {
                _processReader.Dispose();
                _prevPidCpuTicks.Clear();
            }

            if (_subscribers.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
                _cpuHistory.Clear();
                _memoryHistory.Clear();
                _gpuHistory.Clear();
                _netHistory.Clear();
                _hasCpuBaseline = false;
                _hasNetBaseline = false;
            }
            else
            {
                ReconcileTimerLocked();
            }
        }
    }

    public CustomWidgetSystemMetricsSnapshot SampleNow()
    {
        lock (_gate)
        {
            return SampleNowLocked(
                HasActiveGpuSubscriberLocked(),
                HasActiveProcessSubscriberLocked());
        }
    }

    private bool HasActiveGpuSubscriberLocked()
    {
        foreach (CustomWidgetMetricsSubscriptionOptions opt in _subscribers.Values)
        {
            if (opt.EnableGpu)
            {
                return true;
            }
        }
        return false;
    }

    private bool HasActiveProcessSubscriberLocked()
    {
        foreach (CustomWidgetMetricsSubscriptionOptions opt in _subscribers.Values)
        {
            if (opt.EnableProcesses)
            {
                return true;
            }
        }
        return false;
    }

    private void ReconcileTimerLocked()
    {
        if (_subscribers.Count == 0)
        {
            return;
        }

        int minInterval = 5000;
        foreach (CustomWidgetMetricsSubscriptionOptions opt in _subscribers.Values)
        {
            int clamped = Math.Clamp(opt.IntervalMs, 1000, 10000);
            if (clamped < minInterval)
            {
                minInterval = clamped;
            }
        }

        if (_timer is null)
        {
            _currentIntervalMs = minInterval;
            _timer = new Timer(
                OnTimerTick,
                null,
                TimeSpan.FromMilliseconds(minInterval),
                TimeSpan.FromMilliseconds(minInterval));
        }
        else if (_currentIntervalMs != minInterval)
        {
            _currentIntervalMs = minInterval;
            _timer.Change(
                TimeSpan.FromMilliseconds(minInterval),
                TimeSpan.FromMilliseconds(minInterval));
        }
    }

    private void OnTimerTick(object? state)
    {
        CustomWidgetSystemMetricsSnapshot snapshot;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            snapshot = SampleNowLocked(
                HasActiveGpuSubscriberLocked(),
                HasActiveProcessSubscriberLocked());
        }

        MetricsUpdated?.Invoke(snapshot);
    }

    private void PrimeBaseline()
    {
        if (TryGetCpuTimes(out ulong idle, out ulong total))
        {
            _prevIdleTicks = idle;
            _prevTotalTicks = total;
            _prevProcSystemTotalTicks = total;
            _hasCpuBaseline = true;
        }
        else
        {
            _hasCpuBaseline = false;
        }

        (long rx, long tx) = ReadTotalNetworkBytes();
        _prevNetBytesReceived = rx;
        _prevNetBytesSent = tx;
        _prevNetTimestampTicks = Environment.TickCount64;
        _hasNetBaseline = true;
        _diskSampleTickCounter = 0;
    }

    private void PrimeProcessBaselineLocked()
    {
        if (TryGetCpuTimes(out _, out ulong total))
        {
            _prevProcSystemTotalTicks = total;
        }

        _prevPidCpuTicks.Clear();
        if (_processReader.TryReadProcesses(_rawProcesses))
        {
            foreach (CustomWidgetNativeMetrics.RawProcessSample p in _rawProcesses)
            {
                _prevPidCpuTicks[p.ProcessId] = p.CpuTicks;
            }
        }
    }

    private CustomWidgetSystemMetricsSnapshot SampleNowLocked(bool sampleGpu, bool sampleProcesses)
    {
        (double cpuPercent, ulong currentTotalCpuTicks, bool hasCpuDelta) = SampleCpuPercent();
        if (hasCpuDelta)
        {
            AppendHistory(_cpuHistory, cpuPercent);
        }

        (double memPercent, double memUsedGb, double memTotalGb) = SampleMemory();
        AppendHistory(_memoryHistory, memPercent);

        if (_diskSampleTickCounter++ % 5 == 0)
        {
            SampleFixedDisks();
        }

        (string netDown, string netUp, double downMbps, bool hasNetDelta) = SampleNetworkThroughput();
        if (hasNetDelta)
        {
            AppendHistory(_netHistory, Math.Clamp(downMbps, 0, 100));
        }

        (bool hasBattery, bool onAc, int batteryPct) = SampleBattery();
        long uptimeMs = Environment.TickCount64;
        string uptime = FormatUptime(uptimeMs);

        double gpuPercent = _latest.GpuPercent;
        double vramUsedGb = _latest.GpuVramUsedGb;
        double vramTotalGb = _latest.GpuVramTotalGb;
        double vramPercent = _latest.GpuVramPercent;
        bool gpuIsShared = _latest.GpuIsSharedMemory;

        if (sampleGpu)
        {
            if (_dxgiVramSampler.TrySampleVideoMemory(
                    out string gpuName,
                    out ulong usedVramBytes,
                    out ulong totalVramBytes,
                    out bool isShared))
            {
                _cachedGpuName = gpuName;
                const double OneGb = 1024.0 * 1024.0 * 1024.0;
                vramUsedGb = usedVramBytes / OneGb;
                vramTotalGb = totalVramBytes / OneGb;
                vramPercent = vramTotalGb > 0
                    ? Math.Clamp((vramUsedGb / vramTotalGb) * 100.0, 0.0, 100.0)
                    : 0;
                gpuIsShared = isShared;
            }

            if (_gpuCounterSampler.TrySampleGpuPercent(
                    _dxgiVramSampler.HasAdapterLuid,
                    _dxgiVramSampler.AdapterLuid,
                    out double sampledGpuPct,
                    out bool hasGpuRateDelta))
            {
                gpuPercent = sampledGpuPct;
                if (hasGpuRateDelta)
                {
                    AppendHistory(_gpuHistory, gpuPercent);
                }
            }
        }

        IReadOnlyList<CustomWidgetProcessEntry> topMem = _latest.TopProcessesByMemory;
        IReadOnlyList<CustomWidgetProcessEntry> topCpu = _latest.TopProcessesByCpu;
        if (sampleProcesses)
        {
            (topMem, topCpu) = SampleTopProcessesLocked(currentTotalCpuTicks, memTotalGb);
        }

        _latest = new CustomWidgetSystemMetricsSnapshot
        {
            CpuPercent = cpuPercent,
            CpuHistory = _cpuHistory.Count == 0 ? [] : _cpuHistory.ToArray(),
            MemoryPercent = memPercent,
            MemoryUsedGb = memUsedGb,
            MemoryTotalGb = memTotalGb,
            MemoryHistory = _memoryHistory.Count == 0 ? [] : _memoryHistory.ToArray(),
            GpuName = _cachedGpuName,
            GpuPercent = gpuPercent,
            GpuHistory = _gpuHistory.Count == 0 ? [] : _gpuHistory.ToArray(),
            GpuVramUsedGb = vramUsedGb,
            GpuVramTotalGb = vramTotalGb,
            GpuVramPercent = vramPercent,
            GpuIsSharedMemory = gpuIsShared,
            DiskPrimaryPercent = _cachedDiskPercent,
            DiskPrimaryFreeGb = _cachedDiskFreeGb,
            DiskPrimaryTotalGb = _cachedDiskTotalGb,
            DiskPrimaryName = _cachedDiskName,
            DiskByDrive = _immutableDiskSnapshot,
            NetDownText = netDown,
            NetUpText = netUp,
            NetHistory = _netHistory.Count == 0 ? [] : _netHistory.ToArray(),
            HasBattery = hasBattery,
            BatteryOnAc = onAc,
            BatteryPercent = batteryPct,
            UptimeMs = uptimeMs,
            UptimeText = uptime,
            TopProcessesByMemory = topMem,
            TopProcessesByCpu = topCpu
        };

        return _latest;
    }

    private (IReadOnlyList<CustomWidgetProcessEntry> ByMemory, IReadOnlyList<CustomWidgetProcessEntry> ByCpu)
        SampleTopProcessesLocked(ulong currentSystemTotalTicks, double totalMemoryGb)
    {
        if (!_processReader.TryReadProcesses(_rawProcesses))
        {
            return (_latest.TopProcessesByMemory, _latest.TopProcessesByCpu);
        }

        ulong sysDelta = currentSystemTotalTicks > _prevProcSystemTotalTicks
            ? currentSystemTotalTicks - _prevProcSystemTotalTicks
            : 0;
        _prevProcSystemTotalTicks = currentSystemTotalTicks;

        _nextPidCpuTicks.Clear();
        _appBuckets.Clear();

        foreach (CustomWidgetNativeMetrics.RawProcessSample proc in _rawProcesses)
        {
            _nextPidCpuTicks[proc.ProcessId] = proc.CpuTicks;

            double procCpuPct = 0;
            if (sysDelta > 0 && _prevPidCpuTicks.TryGetValue(proc.ProcessId, out ulong prevTicks))
            {
                ulong procDelta = proc.CpuTicks >= prevTicks ? proc.CpuTicks - prevTicks : 0;
                procCpuPct = Math.Clamp(((double)procDelta / sysDelta) * 100.0, 0.0, 100.0);
            }

            string groupKey = NormalizeAppGroupKey(proc.ExeName);
            if (!_appBuckets.TryGetValue(groupKey, out AggregatedProcessBucket? bucket))
            {
                bucket = new AggregatedProcessBucket
                {
                    GroupKey = groupKey,
                    DisplayName = FormatFriendlyAppName(groupKey)
                };
                _appBuckets[groupKey] = bucket;
            }

            bucket.Count++;
            bucket.CpuPercent += procCpuPct;
            bucket.PrivateBytes += proc.PrivateMemoryBytes;
        }

        _prevPidCpuTicks.Clear();
        foreach ((uint pid, ulong ticks) in _nextPidCpuTicks)
        {
            _prevPidCpuTicks[pid] = ticks;
        }

        List<AggregatedProcessBucket> all = new(_appBuckets.Count);
        long maxMemBytes = 1;
        double maxCpuPct = 1.0;

        foreach (AggregatedProcessBucket b in _appBuckets.Values)
        {
            b.CpuPercent = Math.Clamp(b.CpuPercent, 0.0, 100.0);
            if (b.PrivateBytes > maxMemBytes)
            {
                maxMemBytes = b.PrivateBytes;
            }
            if (b.CpuPercent > maxCpuPct)
            {
                maxCpuPct = b.CpuPercent;
            }
            all.Add(b);
        }

        double totalMemBytes = Math.Max(1.0, totalMemoryGb * 1024.0 * 1024.0 * 1024.0);

        all.Sort((a, b) => b.PrivateBytes.CompareTo(a.PrivateBytes));
        var byMem = new List<CustomWidgetProcessEntry>(Math.Min(MaxTopProcesses, all.Count));
        for (int i = 0; i < all.Count && i < MaxTopProcesses; i++)
        {
            AggregatedProcessBucket b = all[i];
            byMem.Add(BuildProcessEntry(b, maxMemBytes, maxCpuPct, totalMemBytes));
        }

        all.Sort((a, b) =>
        {
            int cmp = b.CpuPercent.CompareTo(a.CpuPercent);
            return cmp != 0 ? cmp : b.PrivateBytes.CompareTo(a.PrivateBytes);
        });
        var byCpu = new List<CustomWidgetProcessEntry>(Math.Min(MaxTopProcesses, all.Count));
        for (int i = 0; i < all.Count && i < MaxTopProcesses; i++)
        {
            AggregatedProcessBucket b = all[i];
            byCpu.Add(BuildProcessEntry(b, maxMemBytes, maxCpuPct, totalMemBytes));
        }

        return (byMem, byCpu);
    }

    private static CustomWidgetProcessEntry BuildProcessEntry(
        AggregatedProcessBucket b,
        long maxMemBytes,
        double maxCpuPct,
        double totalMemBytes)
    {
        double memBar = maxMemBytes > 0
            ? Math.Clamp(((double)b.PrivateBytes / Math.Max(maxMemBytes, totalMemBytes * 0.18)) * 100.0, 4.0, 100.0)
            : 4.0;
        double cpuBar = b.CpuPercent <= 0.05
            ? 2.0
            : Math.Clamp((b.CpuPercent / Math.Max(maxCpuPct, 12.0)) * 100.0, 4.0, 100.0);

        return new CustomWidgetProcessEntry(
            b.DisplayName,
            b.Count,
            b.CpuPercent,
            b.PrivateBytes,
            FormattableString.Invariant($"{b.CpuPercent:0.0}%"),
            FormatMemoryBytes(b.PrivateBytes),
            cpuBar,
            memBar,
            b.GroupKey);
    }

    private static string NormalizeAppGroupKey(string exeName)
    {
        string trimmed = exeName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    internal static string FormatFriendlyAppName(string baseName, string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return baseName.ToLowerInvariant() switch
        {
            "msedge" => "Edge",
            "msedgewebview2" => "WebView2",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "code" => "VS Code",
            "cursor" => "Cursor",
            "devenv" => "Visual Studio",
            "claude" => "Claude Code",
            "codex" => "Codex",
            "antigravity" => "Antigravity",
            "windowsterminal" => "Terminal",
            "pwsh" or "powershell" => "PowerShell",
            "explorer" => "Explorer",
            "dwm" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.Dwm"),
            "taskmgr" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.TaskMgr"),
            "wechat" or "weixin" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.WeChat"),
            "wxwork" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.WeCom"),
            "dingtalk" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.DingTalk"),
            "feishu" or "lark" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.Feishu"),
            "qq" => "QQ",
            "cloudmusic" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.NetEaseMusic"),
            "qqmusic" => LocalizationService.TranslateForCulture(culture, "CustomWidget.Sys.App.QqMusic"),
            "deskbox" => "DeskBox",
            _ => baseName
        };
    }

    private static string FormatMemoryBytes(long bytes)
    {
        const double Mb = 1024.0 * 1024.0;
        const double Gb = 1024.0 * 1024.0 * 1024.0;
        if (bytes >= Gb)
        {
            return $"{(bytes / Gb):0.00} GB";
        }
        return $"{Math.Max(1.0, bytes / Mb):0} MB";
    }

    private (double CpuPercent, ulong CurrentTotalTicks, bool HasDelta) SampleCpuPercent()
    {
        if (!TryGetCpuTimes(out ulong idle, out ulong total))
        {
            return (_latest.CpuPercent, _prevTotalTicks, false);
        }

        if (!_hasCpuBaseline)
        {
            _prevIdleTicks = idle;
            _prevTotalTicks = total;
            _hasCpuBaseline = true;
            return (0, total, false);
        }

        ulong idleDelta = idle >= _prevIdleTicks ? idle - _prevIdleTicks : 0;
        ulong totalDelta = total >= _prevTotalTicks ? total - _prevTotalTicks : 0;
        _prevIdleTicks = idle;
        _prevTotalTicks = total;

        if (totalDelta == 0)
        {
            return (_latest.CpuPercent, total, false);
        }

        double busyRatio = 1.0 - ((double)idleDelta / totalDelta);
        return (Math.Clamp(busyRatio * 100.0, 0.0, 100.0), total, true);
    }

    private static (double Percent, double UsedGb, double TotalGb) SampleMemory()
    {
        var status = new CustomWidgetNativeMetrics.MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<CustomWidgetNativeMetrics.MemoryStatusEx>()
        };
        if (!CustomWidgetNativeMetrics.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return (0, 0, 0);
        }

        const double OneGb = 1024.0 * 1024.0 * 1024.0;
        double totalGb = status.ullTotalPhys / OneGb;
        double availGb = status.ullAvailPhys / OneGb;
        double usedGb = Math.Max(0, totalGb - availGb);
        double pct = Math.Clamp((usedGb / totalGb) * 100.0, 0.0, 100.0);
        return (pct, usedGb, totalGb);
    }

    private void SampleFixedDisks()
    {
        try
        {
            const double OneGb = 1024.0 * 1024.0 * 1024.0;
            string sysRoot = CustomWidgetSystemMetricsSnapshot.NormalizeDriveKey(
                Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");

            _cachedDisks.Clear();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                string key = CustomWidgetSystemMetricsSnapshot.NormalizeDriveKey(drive.Name);
                double totalGb = drive.TotalSize / OneGb;
                double freeGb = drive.AvailableFreeSpace / OneGb;
                double usedPct = Math.Clamp(((totalGb - freeGb) / totalGb) * 100.0, 0.0, 100.0);
                var metrics = new CustomWidgetDiskMetrics(key, usedPct, freeGb, totalGb);
                _cachedDisks[key] = metrics;

                if (string.Equals(key, sysRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedDiskName = key;
                    _cachedDiskTotalGb = totalGb;
                    _cachedDiskFreeGb = freeGb;
                    _cachedDiskPercent = usedPct;
                }
            }

            if (!_cachedDisks.ContainsKey(sysRoot) && _cachedDisks.Values.FirstOrDefault() is { } fallback)
            {
                _cachedDiskName = fallback.DriveName;
                _cachedDiskTotalGb = fallback.TotalGb;
                _cachedDiskFreeGb = fallback.FreeGb;
                _cachedDiskPercent = fallback.Percent;
            }

            _immutableDiskSnapshot = new Dictionary<string, CustomWidgetDiskMetrics>(
                _cachedDisks,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Keep cached values on transient IO failure.
        }
    }

    private (string DownText, string UpText, double DownMbps, bool HasDelta) SampleNetworkThroughput()
    {
        long nowTicks = Environment.TickCount64;
        (long rx, long tx) = ReadTotalNetworkBytes();

        if (!_hasNetBaseline)
        {
            _prevNetBytesReceived = rx;
            _prevNetBytesSent = tx;
            _prevNetTimestampTicks = nowTicks;
            _hasNetBaseline = true;
            return (FormatRate(0), FormatRate(0), 0, false);
        }

        long rawElapsedMs = nowTicks - _prevNetTimestampTicks;
        if (rawElapsedMs < 100)
        {
            return (
                string.IsNullOrEmpty(_latest.NetDownText) ? FormatRate(0) : _latest.NetDownText,
                string.IsNullOrEmpty(_latest.NetUpText) ? FormatRate(0) : _latest.NetUpText,
                0,
                false);
        }

        long rxDelta = Math.Max(0, rx - _prevNetBytesReceived);
        long txDelta = Math.Max(0, tx - _prevNetBytesSent);
        _prevNetBytesReceived = rx;
        _prevNetBytesSent = tx;
        _prevNetTimestampTicks = nowTicks;

        double downBytesPerSec = (rxDelta * 1000.0) / rawElapsedMs;
        double upBytesPerSec = (txDelta * 1000.0) / rawElapsedMs;
        double downMbps = (downBytesPerSec * 8.0) / (1024.0 * 1024.0);
        return (FormatRate(downBytesPerSec), FormatRate(upBytesPerSec), downMbps, true);
    }

    private static (long Received, long Sent) ReadTotalNetworkBytes()
    {
        if (CustomWidgetNativeMetrics.TryReadActiveNetworkTotals(out long nativeRx, out long nativeTx))
        {
            return (nativeRx, nativeTx);
        }
        try
        {
            long rx = 0;
            long tx = 0;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            return (rx, tx);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (bool HasBattery, bool OnAc, int Percent) SampleBattery()
    {
        if (!CustomWidgetNativeMetrics.GetSystemPowerStatus(out CustomWidgetNativeMetrics.SystemPowerStatus status))
        {
            return (false, true, 100);
        }

        bool hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
        bool onAc = status.ACLineStatus == 1;
        int pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;
        return (hasBattery, onAc, pct);
    }

    private static string FormatRate(double bytesPerSec)
    {
        const double Kb = 1024.0;
        const double Mb = 1024.0 * 1024.0;
        if (bytesPerSec >= Mb)
        {
            return $"{(bytesPerSec / Mb):0.0} MB/s";
        }
        return $"{Math.Max(0, bytesPerSec / Kb):0} KB/s";
    }

    internal static string FormatUptime(long ms, string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        TimeSpan span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (span.TotalDays >= 1)
        {
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Sys.UptimeDaysHoursFormat",
                (int)span.TotalDays,
                span.Hours);
        }
        if (span.TotalHours >= 1)
        {
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Sys.UptimeHoursMinutesFormat",
                span.Hours,
                span.Minutes);
        }
        return LocalizationService.FormatForCulture(
            culture,
            "CustomWidget.Sys.UptimeMinutesFormat",
            Math.Max(1, span.Minutes));
    }

    private static void AppendHistory(List<double> list, double value)
    {
        if (list.Count >= HistoryCapacity)
        {
            list.RemoveAt(0);
        }
        list.Add(Math.Round(value, 1));
    }

    private static bool TryGetCpuTimes(out ulong idleTicks, out ulong totalTicks)
    {
        if (CustomWidgetNativeMetrics.GetSystemTimes(
                out CustomWidgetNativeMetrics.SystemFileTime idle,
                out CustomWidgetNativeMetrics.SystemFileTime kernel,
                out CustomWidgetNativeMetrics.SystemFileTime user))
        {
            idleTicks = ToUInt64(idle);
            totalTicks = ToUInt64(kernel) + ToUInt64(user);
            return true;
        }

        idleTicks = 0;
        totalTicks = 0;
        return false;
    }

    private static ulong ToUInt64(CustomWidgetNativeMetrics.SystemFileTime ft) =>
        ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}
