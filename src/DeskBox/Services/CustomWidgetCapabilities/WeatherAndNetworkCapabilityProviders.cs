using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal sealed class WeatherCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _weatherSourceTimer;
    private CustomWidgetWeatherSnapshot _weatherSnapshot = CustomWidgetWeatherSnapshot.Empty;
    private CancellationTokenSource? _weatherFetchCts;
    private int _weatherFetchInFlight;
    private int _pendingWeatherRefreshRequested;
    private int _pendingWeatherForceRefresh;
    private int _pendingWeatherManualAction;
    private long _lastWeatherAttemptUtcTicks;
    private string? _lastFetchedWeatherCityKey;
    private bool _isWeatherStandIn;

    internal WeatherCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "weather";
    public IReadOnlyList<string> CapabilityIds => ["weather"];

    internal CustomWidgetWeatherSnapshot Snapshot => _weatherSnapshot;

    public void UpdateSubscription() => UpdateWeatherSourceSubscription();

    public void Stop()
    {
        if (_weatherSourceTimer is not null)
        {
            _weatherSourceTimer.Stop();
            _weatherSourceTimer.Tick -= OnWeatherSourceTimerTick;
            _weatherSourceTimer = null;
        }
        if (Interlocked.Exchange(ref _weatherFetchCts, null) is { } cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesWeather() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "weather.");

    internal string ResolveEffectiveWeatherCity()
    {
        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase));
        if (spec is not null && !string.IsNullOrWhiteSpace(spec.City))
        {
            string evalCity = CustomWidgetExpressionEvaluator.Interpolate(spec.City, _vm.CreateEvaluationContext()).Trim();
            if (!string.IsNullOrWhiteSpace(evalCity))
            {
                return evalCity;
            }
        }

        if (_vm.StateInternal.TryGetValue("city", out string? stateCity) && !string.IsNullOrWhiteSpace(stateCity))
        {
            return stateCity.Trim();
        }

        if (_vm.SettingsInternal.TryGetValue("city", out string? settingCity) && !string.IsNullOrWhiteSpace(settingCity))
        {
            return settingCity.Trim();
        }

        return string.Empty;
    }

    internal void UpdateWeatherSourceSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsUntrustedStagePreview &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "weather.")) &&
                         ManifestUsesWeather() &&
                         _vm.IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true);

        if (!shouldRun)
        {
            if (_weatherSourceTimer is not null)
            {
                _weatherSourceTimer.Stop();
                _weatherSourceTimer.Tick -= OnWeatherSourceTimerTick;
                _weatherSourceTimer = null;
            }
            if (Interlocked.Exchange(ref _weatherFetchCts, null) is { } wCts)
            {
                try
                {
                    wCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (_vm.IsStudioGalleryThumbnailPreview &&
                !_vm.IsDisposedInternal &&
                ManifestUsesWeather() &&
                !_isWeatherStandIn)
            {
                SeedStudioPreviewWeatherStandIn();
                _isWeatherStandIn = true;
            }
            return;
        }

        _isWeatherStandIn = false;

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 900_000, 300_000, 7_200_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_weatherSourceTimer is null)
            {
                _weatherSourceTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _weatherSourceTimer.IsRepeating = true;
                _weatherSourceTimer.Tick += OnWeatherSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_weatherSourceTimer.Interval != interval)
            {
                _weatherSourceTimer.Interval = interval;
            }
            if (!_weatherSourceTimer.IsRunning)
            {
                _weatherSourceTimer.Start();
            }
        }

        string cityKey = $"{ResolveEffectiveWeatherCity()}|{_vm.ActiveCultureName}";
        bool cityChanged = !string.Equals(_lastFetchedWeatherCityKey, cityKey, StringComparison.OrdinalIgnoreCase);
        long lastAttempt = Interlocked.Read(ref _lastWeatherAttemptUtcTicks);
        bool retryDue = lastAttempt <= 0 ||
                        DateTimeOffset.UtcNow.UtcTicks - lastAttempt >= TimeSpan.FromSeconds(60).Ticks;
        if (cityChanged || (!_weatherSnapshot.HasData && retryDue))
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }

    internal void OnWeatherSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }

    internal async Task<bool> RefreshWeatherNowAsync(bool forceRefresh = false, bool isManualUserAction = false)
    {
        if (_vm.IsDisposedInternal ||
            _vm.IsUntrustedStagePreview ||
            _vm.IsStudioGalleryThumbnailPreview ||
            !ManifestUsesWeather() ||
            !_vm.IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _weatherFetchInFlight, 1, 0) != 0)
        {
            if (forceRefresh)
            {
                Interlocked.Exchange(ref _pendingWeatherForceRefresh, 1);
            }
            if (isManualUserAction)
            {
                Interlocked.Exchange(ref _pendingWeatherManualAction, 1);
            }
            Interlocked.Exchange(ref _pendingWeatherRefreshRequested, 1);
            return false;
        }

        Interlocked.Exchange(ref _pendingWeatherRefreshRequested, 0);
        Interlocked.Exchange(ref _pendingWeatherForceRefresh, 0);
        Interlocked.Exchange(ref _pendingWeatherManualAction, 0);

        Interlocked.Exchange(ref _lastWeatherAttemptUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _weatherFetchCts, cts)?.Cancel();
        try
        {

            string city = ResolveEffectiveWeatherCity();
            string culture = _vm.ActiveCultureName;
            CustomWidgetWeatherSnapshot snap = await _vm.WeatherSourceInternal.FetchSnapshotAsync(
                city,
                culture,
                forceRefresh: forceRefresh,
                cancellationToken: cts.Token);

            if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
            {
                return false;
            }

            string targetCollection = _vm.ManifestInternal.Sources
                .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase))
                ?.TargetCollection ?? "forecast";
            if (string.IsNullOrWhiteSpace(targetCollection))
            {
                targetCollection = "forecast";
            }

            void ApplyWeatherOnUi()
            {
                if (_vm.IsDisposedInternal)
                {
                    return;
                }

                _weatherSnapshot = snap;
                _lastFetchedWeatherCityKey = $"{city}|{culture}";
                if (snap.ForecastRecords.Count > 0)
                {
                    _vm.CollectionsInternal[targetCollection] = new List<CustomWidgetRecord>(snap.ForecastRecords);
                }

                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
                _vm.RefreshCapsuleProjection(bumpLiveState: true);
                _vm.NotifyViewRebuildRequested();
                if (isManualUserAction)
                {
                    if (snap.HasData)
                    {
                        _vm.RaiseFeedback(_vm.LocalizationServiceInternal.T("CustomWidget.Toast.SourceRefreshed"), WidgetFeedbackSeverity.Info);
                    }
                    else
                    {
                        string warnMsg = !string.IsNullOrWhiteSpace(snap.ErrorMessage)
                            ? snap.ErrorMessage
                            : _vm.LocalizationServiceInternal.T("CustomWidget.Weather.Unavailable");
                        _vm.RaiseFeedback(warnMsg, WidgetFeedbackSeverity.Warning);
                    }
                }
            }

            if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
            {
                _vm.DispatcherQueueInternal.TryEnqueue(ApplyWeatherOnUi);
            }
            else
            {
                ApplyWeatherOnUi();
            }

            return snap.HasData;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _weatherFetchCts, null, cts);
            Interlocked.Exchange(ref _weatherFetchInFlight, 0);
            if (Interlocked.Exchange(ref _pendingWeatherRefreshRequested, 0) == 1 && !_vm.IsDisposedInternal)
            {
                bool pendingForce = Interlocked.Exchange(ref _pendingWeatherForceRefresh, 0) == 1;
                bool pendingManual = Interlocked.Exchange(ref _pendingWeatherManualAction, 0) == 1;
                _ = RefreshWeatherNowAsync(forceRefresh: pendingForce, isManualUserAction: pendingManual);
            }
        }
    }

    private void SeedStudioPreviewWeatherStandIn()
    {
        string culture = _vm.ActiveCultureName;
        bool isZh = !string.IsNullOrWhiteSpace(culture) && culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string sampleCity = LocalizationService.TranslateForCulture(
            culture,
            isZh ? "CustomWidget.Weather.DefaultCity" : "CustomWidget.Weather.FallbackCity");

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var sampleData = new WeatherData
        {
            LocationName = sampleCity,
            Current = new WeatherCurrent
            {
                Temperature = 22,
                ApparentTemperature = 21,
                Humidity = 52,
                WindSpeed = 11,
                WeatherCode = 1,
                IsDay = 1
            },
            Daily = new WeatherDaily
            {
                Time =
                [
                    today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    today.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ],
                WeatherCode = [1, 2, 0, 61],
                TemperatureMax = [24, 25, 23, 19],
                TemperatureMin = [15, 16, 14, 13],
                PrecipitationProbabilityMax = [10, 20, 5, 65],
                UvIndexMax = [5, 6, 6, 3],
                Sunrise = ["2026-01-01T06:28", "2026-01-01T06:29", "2026-01-01T06:30", "2026-01-01T06:31"],
                Sunset = ["2026-01-01T18:42", "2026-01-01T18:41", "2026-01-01T18:40", "2026-01-01T18:39"]
            }
        };

        CustomWidgetWeatherSnapshot standIn = CustomWidgetWeatherSource.BuildSnapshotFromWeatherData(sampleData, sampleCity, culture);
        string targetCollection = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase))
            ?.TargetCollection ?? "forecast";
        if (string.IsNullOrWhiteSpace(targetCollection))
        {
            targetCollection = "forecast";
        }

        _vm.RunWithHeadlessStateGate(() =>
        {
            _weatherSnapshot = standIn;
            if (standIn.ForecastRecords.Count > 0)
            {
                _vm.CollectionsInternal[targetCollection] = new List<CustomWidgetRecord>(standIn.ForecastRecords);
                _vm.SampleSeededCollectionsInternal.Add(targetCollection);
            }
        });
        _vm.RefreshCapsuleProjection(bumpLiveState: false);
    }

}

internal sealed class NetworkCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private const int MaxSourceFetchTimestampEntries = 64;
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _networkSourceTimer;
    private readonly ConcurrentDictionary<string, long> _networkSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastManualRefreshUtcTicksByKey = new(StringComparer.Ordinal);

    private static void RecordBoundedTimestamp(ConcurrentDictionary<string, long> map, string key, long ticks)
    {
        if (!map.ContainsKey(key) && map.Count >= MaxSourceFetchTimestampEntries)
        {
            string? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach ((string k, long v) in map)
            {
                if (v < oldestTicks)
                {
                    oldestTicks = v;
                    oldestKey = k;
                }
            }

            if (oldestKey is not null)
            {
                map.TryRemove(oldestKey, out _);
            }
        }

        map[key] = ticks;
    }
    private CancellationTokenSource? _networkFetchCts;
    private int _networkFetchInFlight;
    private int _pendingNetworkRefreshRequested;
    private readonly ConcurrentDictionary<string, string> _lastResolvedSourceUrls = new(StringComparer.Ordinal);
    private bool _isNetworkStandIn;

    private static string GetSourceUrlTrackingKey(CustomWidgetSourceSpec spec) =>
        string.IsNullOrWhiteSpace(spec.Id) ? spec.Type : spec.Id;

    private bool CheckAndTrackSourceUrlChanged(CustomWidgetSourceSpec spec, string resolvedUrl)
    {
        string trackKey = GetSourceUrlTrackingKey(spec);
        if (_lastResolvedSourceUrls.TryGetValue(trackKey, out string? prevUrl))
        {
            if (!string.Equals(prevUrl, resolvedUrl, StringComparison.Ordinal))
            {
                _lastResolvedSourceUrls[trackKey] = resolvedUrl;
                return true;
            }
            return false;
        }

        _lastResolvedSourceUrls[trackKey] = resolvedUrl;
        return false;
    }

    private bool HasPendingUrlChange(CustomWidgetSourceSpec spec)
    {
        string trackKey = GetSourceUrlTrackingKey(spec);
        string resolvedUrl = ResolveNetworkSourceUrl(spec);
        return _lastResolvedSourceUrls.TryGetValue(trackKey, out string? prevUrl) &&
               !string.Equals(prevUrl, resolvedUrl, StringComparison.Ordinal);
    }

    internal NetworkCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "network";
    public IReadOnlyList<string> CapabilityIds => ["rss", "httpJson"];

    internal void ClearFetchTimestamps() => _networkSourceLastFetchUtcTicks.Clear();

    internal void RemoveFetchTimestamp(string key) => _networkSourceLastFetchUtcTicks.TryRemove(key, out _);

    internal bool NeedsFetchAfter(string key, long thresholdUtcTicks) =>
        !_networkSourceLastFetchUtcTicks.TryGetValue(key, out long fetchedTicks) || fetchedTicks < thresholdUtcTicks;

    public void UpdateSubscription() => UpdateNetworkSourcesSubscription();

    public void Stop() => StopNetworkSourcesPolling();

    public void Dispose() => Stop();

    internal bool ManifestUsesNetworkSources() =>
        _vm.ManifestInternal.Sources.Any(static s =>
            string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase));


    internal string ResolveNetworkSourceUrl(CustomWidgetSourceSpec spec) =>
        CustomWidgetExpressionEvaluator.Interpolate(spec.Url, _vm.CreateEvaluationContext().WithAllowSecrets(true)).Trim();

    internal IReadOnlyDictionary<string, string>? ResolveNetworkSourceHeaders(CustomWidgetSourceSpec spec)
    {
        if (spec.Headers is not { Count: > 0 })
        {
            return null;
        }

        CustomWidgetEvaluationContext ctx = _vm.CreateEvaluationContext().WithAllowSecrets(true);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string k, string v) in spec.Headers)
        {
            if (string.IsNullOrWhiteSpace(k))
            {
                continue;
            }

            string resolved = CustomWidgetExpressionEvaluator.Interpolate(v, ctx).Trim();
            if (!string.IsNullOrEmpty(resolved))
            {
                map[k.Trim()] = resolved;
            }
        }

        return map.Count > 0 ? map : null;
    }

    internal bool TryFindMissingReferencedSecretLabel(CustomWidgetSourceSpec spec, out string missingLabel)
    {
        missingLabel = string.Empty;
        static IEnumerable<string> ScanSecretKeys(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr) || !expr.Contains("$secrets.", StringComparison.Ordinal))
            {
                yield break;
            }

            int idx = 0;
            while ((idx = expr.IndexOf("$secrets.", idx, StringComparison.Ordinal)) >= 0)
            {
                int start = idx + "$secrets.".Length;
                int end = start;
                while (end < expr.Length && (char.IsLetterOrDigit(expr[end]) || expr[end] == '_' || expr[end] == '-'))
                {
                    end++;
                }

                if (end > start)
                {
                    yield return expr[start..end];
                }

                idx = end;
            }
        }

        IEnumerable<string> candidates = ScanSecretKeys(spec.Url);
        if (spec.Headers is { Count: > 0 })
        {
            foreach (string hv in spec.Headers.Values)
            {
                candidates = candidates.Concat(ScanSecretKeys(hv));
            }
        }

        foreach (string key in candidates)
        {
            if (!_vm.SecretsInternal.TryGetValue(key, out string? val) || string.IsNullOrWhiteSpace(val))
            {
                CustomWidgetSettingDefinition? def = _vm.ManifestInternal.Settings.FirstOrDefault(
                    d => string.Equals(d.Key, key, StringComparison.Ordinal));
                missingLabel = !string.IsNullOrWhiteSpace(def?.Label) ? def.Label : key;
                return true;
            }
        }

        return false;
    }

    internal void HydrateNetworkSourcesFromDiskCache(bool overwriteExistingCollection = false)
    {
        foreach (CustomWidgetSourceSpec spec in _vm.ManifestInternal.Sources)
        {
            if (!string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(spec.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryFindMissingReferencedSecretLabel(spec, out _))
            {
                continue;
            }

            string resolvedUrl = ResolveNetworkSourceUrl(spec);
            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                continue;
            }

            HydrateSingleNetworkSourceFromDiskCache(spec, resolvedUrl, overwriteExistingCollection);
        }
    }

    internal void HydrateSingleNetworkSourceFromDiskCache(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        bool overwriteExistingCollection)
    {
        string trackKey = GetSourceUrlTrackingKey(spec);
        _lastResolvedSourceUrls.TryAdd(trackKey, resolvedUrl);
        IReadOnlyDictionary<string, string>? resolvedHeaders = ResolveNetworkSourceHeaders(spec);
        CustomWidgetNetworkFetchOutcome? cached = _vm.NetworkSourceEngineInternal.TryLoadCachedOutcome(spec, resolvedUrl, resolvedHeaders);
        if (cached is null)
        {
            return;
        }

        foreach ((string sk, JsonElement sv) in cached.StateUpdates)
        {
            _vm.SetStructuredStateValue(sk, sv);
        }

        if (!string.IsNullOrWhiteSpace(spec.TargetCollection) &&
            cached.Items.Count > 0 &&
            (overwriteExistingCollection ||
             !_vm.CollectionsInternal.TryGetValue(spec.TargetCollection, out List<CustomWidgetRecord>? curList) ||
             curList.Count == 0))
        {
            _vm.CollectionsInternal[spec.TargetCollection] = new List<CustomWidgetRecord>(cached.Items);
            _vm.BumpCollectionsVersion();
        }

        if (cached.UpdatedAtUtcTicks > 0)
        {
            DateTimeOffset localUpdated = new DateTimeOffset(cached.UpdatedAtUtcTicks, TimeSpan.Zero).ToLocalTime();
            _vm.StateInternal["sourceUpdatedAt"] = localUpdated.ToString("HH:mm", CultureInfo.InvariantCulture);
            _vm.StateInternal["sourceStatus"] = "ok";
            _vm.StateInternal["sourceError"] = string.Empty;
        }

        if (_vm.CompiledScriptInternal is not null &&
            _vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
        {
            _vm.InvokeScriptCore(
                functionName: "onSourceData",
                arg: null,
                eventValue: null,
                currentItem: null,
                dropFile: null,
                sourceId: spec.Id ?? spec.Type,
                rawSourcePayload: cached.RawResponseBody,
                timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                requireExport: false,
                allowCopy: false,
                out _,
                out _,
                out _,
                out _,
                callKind: CustomWidgetScriptCallKind.SourceData);
        }
    }

    internal void UpdateNetworkSourcesSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         (!_vm.IsCompactCollapsedInternal || _vm.CapsuleUsesDynamicBindings()) &&
                         !_vm.IsUntrustedStagePreview &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_vm.DiagnosticsInternal) &&
                         ManifestUsesNetworkSources() &&
                         (_vm.IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true) ||
                          _vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalNetwork));

        if (!shouldRun)
        {
            StopNetworkSourcesPolling();
            if (_vm.IsStudioGalleryThumbnailPreview &&
                !_vm.IsDisposedInternal &&
                ManifestUsesNetworkSources() &&
                !_isNetworkStandIn)
            {
                SeedStudioPreviewNetworkStandIn();
                _isNetworkStandIn = true;
            }
            return;
        }

        _isNetworkStandIn = false;

        int minIntervalMs = _vm.ManifestInternal.Sources
            .Where(static s =>
                string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            .Select(static s => CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(s.IntervalMs))
            .DefaultIfEmpty(CustomWidgetNetworkSecurity.DefaultNetworkSourceIntervalMs)
            .Min();

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_networkSourceTimer is null)
            {
                _networkSourceTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _networkSourceTimer.IsRepeating = true;
                _networkSourceTimer.Tick += OnNetworkSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(minIntervalMs);
            if (_networkSourceTimer.Interval != interval)
            {
                _networkSourceTimer.Interval = interval;
            }

            if (!_networkSourceTimer.IsRunning)
            {
                _networkSourceTimer.Start();
            }
        }

        // Trigger initial background refresh if any source is due
        _ = RefreshDueNetworkSourcesAsync();
    }

    internal void StopNetworkSourcesPolling()
    {
        if (_networkSourceTimer is not null)
        {
            _networkSourceTimer.Stop();
            _networkSourceTimer.Tick -= OnNetworkSourceTimerTick;
            _networkSourceTimer = null;
        }

        if (Interlocked.Exchange(ref _networkFetchCts, null) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal void OnNetworkSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        _ = RefreshDueNetworkSourcesAsync();
    }

    internal Task RefreshDueNetworkSourcesAsync()
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        bool anyDue = false;
        foreach (CustomWidgetSourceSpec spec in _vm.ManifestInternal.Sources)
        {
            if (!string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(spec.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string key = BuildRuntimeNetworkSourceKey(spec);
            int intervalMs = CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(spec.IntervalMs);
            if (HasPendingUrlChange(spec) ||
                !_networkSourceLastFetchUtcTicks.TryGetValue(key, out long lastTicks) ||
                nowTicks - lastTicks >= TimeSpan.FromMilliseconds(intervalMs).Ticks)
            {
                anyDue = true;
                break;
            }
        }

        return anyDue
            ? RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false)
            : Task.CompletedTask;
    }

    internal async Task<bool> RefreshNetworkSourcesNowAsync(
        string? targetSourceId = null,
        bool isManualUserAction = false)
    {
        if (_vm.IsDisposedInternal ||
            _vm.IsUntrustedStagePreview ||
            _vm.IsStudioGalleryThumbnailPreview ||
            !ManifestUsesNetworkSources())
        {
            return false;
        }

        CustomWidgetSourceSpec[] specs = _vm.ManifestInternal.Sources
            .Where(s =>
                (string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(targetSourceId) ||
                 string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (specs.Length == 0)
        {
            return false;
        }

        var changedSourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomWidgetSourceSpec spec in specs)
        {
            string resolvedUrl = ResolveNetworkSourceUrl(spec);
            if (CheckAndTrackSourceUrlChanged(spec, resolvedUrl))
            {
                changedSourceKeys.Add(GetSourceUrlTrackingKey(spec));
                ApplyUrlSwitchCacheOrClearOnUi(spec, resolvedUrl);
            }
        }

        if (isManualUserAction)
        {
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            long cooldownTicks = TimeSpan.FromSeconds(CustomWidgetNetworkSecurity.ManualRefreshCooldownSeconds).Ticks;
            bool allOnCooldown = true;
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (changedSourceKeys.Contains(GetSourceUrlTrackingKey(spec)))
                {
                    allOnCooldown = false;
                    break;
                }

                string key = BuildRuntimeNetworkSourceKey(spec);
                if (!_lastManualRefreshUtcTicksByKey.TryGetValue(key, out long prevManual) ||
                    prevManual <= 0 ||
                    nowTicks - prevManual >= cooldownTicks)
                {
                    allOnCooldown = false;
                    break;
                }
            }

            if (allOnCooldown && Volatile.Read(ref _networkFetchInFlight) == 0)
            {
                _vm.RaiseFeedback(
                    _vm.LocalizationServiceInternal.T("CustomWidget.Toast.SourceRefreshCooldown"),
                    WidgetFeedbackSeverity.Info);
                return false;
            }

            foreach (CustomWidgetSourceSpec spec in specs)
            {
                RecordBoundedTimestamp(_lastManualRefreshUtcTicksByKey, BuildRuntimeNetworkSourceKey(spec), nowTicks);
            }
            _vm.DeniedExternalTargetCooldownUtcTicksInternal.Clear();
        }

        if (Interlocked.CompareExchange(ref _networkFetchInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 1);
            return false;
        }

        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _networkFetchCts, cts)?.Cancel();
        try
        {
            SetNetworkLoadingIndicatorOnUi(isLoading: true);

            bool anyUpdated = false;
            long batchNowTicks = DateTimeOffset.UtcNow.UtcTicks;
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                {
                    break;
                }

                bool urlChangedForSpec = changedSourceKeys.Contains(GetSourceUrlTrackingKey(spec));
                string keyAtStart = BuildRuntimeNetworkSourceKey(spec);
                if (!isManualUserAction && !urlChangedForSpec)
                {
                    int minCooldownMs = string.IsNullOrWhiteSpace(targetSourceId)
                        ? CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(spec.IntervalMs)
                        : CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs;
                    if (_networkSourceLastFetchUtcTicks.TryGetValue(keyAtStart, out long prevTicks) &&
                        batchNowTicks - prevTicks < TimeSpan.FromMilliseconds(minCooldownMs).Ticks)
                    {
                        continue;
                    }
                }

                if (TryFindMissingReferencedSecretLabel(spec, out string missingSecretLabel))
                {
                    string authMsg = LocalizationService.FormatForCulture(
                        _vm.ActiveCultureName,
                        "CustomWidget.Secret.FetchAuthRequiredFormat",
                        missingSecretLabel);
                    ApplyNetworkAuthRequiredOnUi(spec.Id, authMsg, preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks);
                    continue;
                }

                string resolvedUrl = ResolveNetworkSourceUrl(spec);
                IReadOnlyDictionary<string, string>? resolvedHeaders = ResolveNetworkSourceHeaders(spec);
                bool allowLocalNet = _vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalNetwork);
                if (!CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
                        resolvedUrl,
                        allowLocalAndHttp: allowLocalNet,
                        out Uri? uri,
                        out bool requiresLocalNet,
                        out string? urlErr) ||
                    uri is null)
                {
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks);
                    ApplyNetworkErrorOnUi(spec.Id, urlErr ?? "Invalid HTTPS URL", preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    continue;
                }

                if (!requiresLocalNet &&
                    !_vm.IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true))
                {
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks);
                    ApplyNetworkErrorOnUi(
                        spec.Id,
                        _vm.LocalizationServiceInternal.T("CustomWidget.Error.NetworkSourceUnauthorized"),
                        preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    continue;
                }

                bool approved = requiresLocalNet
                    ? await _vm.EnsureWidgetPermissionGrantedAsync(
                        CustomWidgetPermissionKind.LocalNetwork,
                        actionKind: "network.fetch",
                        targetDisplay: uri.AbsoluteUri,
                        showBlockedToast: isManualUserAction)
                    : await _vm.EnsureCommunityExternalTargetApprovedAsync(
                        actionKind: "network.fetch",
                        trustToken: $"host:{uri.DnsSafeHost.ToLowerInvariant()}",
                        targetDisplay: uri.AbsoluteUri,
                        respectDenialCooldown: !isManualUserAction);
                if (!approved)
                {
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks);
                    ApplyNetworkErrorOnUi(
                        spec.Id,
                        _vm.LocalizationServiceInternal.T("CustomWidget.Error.NetworkSourceUnauthorized"),
                        preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    continue;
                }

                CustomWidgetNetworkFetchOutcome outcome =
                    await _vm.NetworkSourceEngineInternal.FetchAsync(
                        spec,
                        uri.AbsoluteUri,
                        cts.Token,
                        resolvedHeaders,
                        crossOriginHostApprover: (redirectUri, _) => _vm.EnsureCommunityExternalTargetApprovedAsync(
                            actionKind: "network.fetch",
                            trustToken: $"host:{redirectUri.DnsSafeHost.ToLowerInvariant()}",
                            targetDisplay: redirectUri.AbsoluteUri,
                            respectDenialCooldown: !isManualUserAction),
                        allowLocalAndHttp: allowLocalNet);
                if (outcome.Succeeded)
                {
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks);
                }
                else
                {
                    int fullIntervalMs = CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(spec.IntervalMs);
                    int retryBackoffMs = CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs;
                    long leadTicks = TimeSpan.FromMilliseconds(Math.Max(0, fullIntervalMs - retryBackoffMs)).Ticks;
                    RecordBoundedTimestamp(_networkSourceLastFetchUtcTicks, keyAtStart, DateTimeOffset.UtcNow.UtcTicks - leadTicks);
                }

                if (!string.Equals(BuildRuntimeNetworkSourceKey(spec), keyAtStart, StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 1);
                    continue;
                }

                if (outcome.Succeeded)
                {
                    ApplyNetworkOutcomeOnUi(spec, outcome);
                    anyUpdated = true;
                }
                else
                {
                    ApplyNetworkErrorOnUi(
                        spec.Id,
                        outcome.ErrorMessage ?? "Network fetch failed",
                        preserveOkWhenOtherSourceSucceeded: anyUpdated);
                }
            }

            if (anyUpdated && !_vm.IsDisposedInternal)
            {
                await _vm.PersistStoreAsync();
                if (isManualUserAction)
                {
                    _vm.RaiseFeedback(
                        _vm.LocalizationServiceInternal.T("CustomWidget.Toast.SourceRefreshed"),
                        WidgetFeedbackSeverity.Info);
                }
            }

            return anyUpdated;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _networkFetchCts, null, cts);
            ClearStuckNetworkLoadingOnUi();
            Interlocked.Exchange(ref _networkFetchInFlight, 0);
            if (!_vm.IsDisposedInternal && Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 0) == 1)
            {
                _ = RefreshDueNetworkSourcesAsync();
            }
        }
    }

    internal string BuildRuntimeNetworkSourceKey(CustomWidgetSourceSpec spec)
    {
        string url = ResolveNetworkSourceUrl(spec);
        IReadOnlyDictionary<string, string>? headers = ResolveNetworkSourceHeaders(spec);
        return $"{spec.Id ?? spec.Type}|{CustomWidgetNetworkSourceEngine.BuildCacheKey(spec, url, headers)}";
    }

    internal void ClearStuckNetworkLoadingOnUi()
    {
        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (_vm.StateInternal.TryGetValue("sourceStatus", out string? cur) &&
                string.Equals(cur, "loading", StringComparison.Ordinal))
            {
                _vm.StateInternal["sourceStatus"] = "ok";
                _vm.TryRunScriptLifecycleHook(
                    "onInit",
                    CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                    persistIfMutated: false);
                _vm.RefreshCapsuleProjection(bumpLiveState: false);
                _vm.NotifyViewRebuildRequested();
            }
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal void SetNetworkLoadingIndicatorOnUi(bool isLoading)
    {
        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            _vm.StateInternal["sourceStatus"] = isLoading ? "loading" : "ok";
            _vm.TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
            _vm.RefreshCapsuleProjection(bumpLiveState: false);
            _vm.NotifyViewRebuildRequested();
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal void ApplyNetworkOutcomeOnUi(
        CustomWidgetSourceSpec spec,
        CustomWidgetNetworkFetchOutcome outcome)
    {
        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            foreach ((string sk, JsonElement sv) in outcome.StateUpdates)
            {
                _vm.SetStructuredStateValue(sk, sv);
            }

            if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                int maxItems = _vm.ManifestInternal.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
                    ? schema.MaxItems
                    : Math.Clamp(spec.MaxItems, 1, 100);
                _vm.CollectionsInternal[spec.TargetCollection] = outcome.Items.Take(maxItems).ToList();
                _vm.BumpCollectionsVersion();
            }

            DateTimeOffset localUpdated = outcome.UpdatedAtUtcTicks > 0
                ? new DateTimeOffset(outcome.UpdatedAtUtcTicks, TimeSpan.Zero).ToLocalTime()
                : DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                _vm.StateInternal[$"sourceStatus.{spec.Id}"] = "ok";
                _vm.StateInternal[$"sourceError.{spec.Id}"] = string.Empty;
            }
            _vm.StateInternal["sourceStatus"] = "ok";
            _vm.StateInternal["sourceError"] = string.Empty;
            _vm.StateInternal["sourceUpdatedAt"] = localUpdated.ToString("HH:mm", CultureInfo.InvariantCulture);

            IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
            if (_vm.CompiledScriptInternal is not null &&
                _vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
            {
                _vm.InvokeScriptCore(
                    functionName: "onSourceData",
                    arg: null,
                    eventValue: null,
                    currentItem: null,
                    dropFile: null,
                    sourceId: spec.Id ?? spec.Type,
                    rawSourcePayload: outcome.RawResponseBody,
                    timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                    requireExport: false,
                    allowCopy: false,
                    out bool scriptMutatedPersisted,
                    out _,
                    out _,
                    out dispatched,
                    callKind: CustomWidgetScriptCallKind.SourceData);
                if (scriptMutatedPersisted && !_vm.IsPreviewOnlyInternal)
                {
                    _ = _vm.PersistStoreAsync();
                }
            }

            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
            if (dispatched is { Count: > 0 })
            {
                _ = _vm.ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
            }
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal void ApplyNetworkAuthRequiredOnUi(
        string? sourceId,
        string message,
        bool preserveOkWhenOtherSourceSucceeded = false)
    {
        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                _vm.StateInternal[$"sourceStatus.{sourceId}"] = "auth_required";
                _vm.StateInternal[$"sourceError.{sourceId}"] = message;
            }

            if (!preserveOkWhenOtherSourceSucceeded)
            {
                _vm.StateInternal["sourceStatus"] = "auth_required";
            }

            _vm.StateInternal["sourceError"] = message;
            _vm.TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
            _vm.RefreshCapsuleProjection(bumpLiveState: false);
            _vm.NotifyViewRebuildRequested();
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal void ApplyNetworkErrorOnUi(
        string? sourceId,
        string errorMessage,
        bool preserveOkWhenOtherSourceSucceeded = false)
    {
        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                _vm.StateInternal[$"sourceStatus.{sourceId}"] = "error";
                _vm.StateInternal[$"sourceError.{sourceId}"] = errorMessage;
            }

            if (!preserveOkWhenOtherSourceSucceeded)
            {
                _vm.StateInternal["sourceStatus"] = "error";
            }

            _vm.StateInternal["sourceError"] = !string.IsNullOrWhiteSpace(sourceId)
                ? $"[{sourceId}] {errorMessage}"
                : errorMessage;
            _vm.TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
            _vm.RefreshCapsuleProjection(bumpLiveState: false);
            _vm.NotifyViewRebuildRequested();
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void ApplyUrlSwitchCacheOrClearOnUi(CustomWidgetSourceSpec spec, string resolvedUrl)
    {
        IReadOnlyDictionary<string, string>? resolvedHeaders = ResolveNetworkSourceHeaders(spec);
        CustomWidgetNetworkFetchOutcome? cached = !string.IsNullOrWhiteSpace(resolvedUrl) &&
            !TryFindMissingReferencedSecretLabel(spec, out _)
            ? _vm.NetworkSourceEngineInternal.TryLoadCachedOutcome(spec, resolvedUrl, resolvedHeaders)
            : null;

        void Apply()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (cached is not null)
            {
                HydrateSingleNetworkSourceFromDiskCache(spec, resolvedUrl, overwriteExistingCollection: true);
            }
            else if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                _vm.CollectionsInternal[spec.TargetCollection] = [];
                if (string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase))
                {
                    _vm.StateInternal["feedTitle"] = string.Empty;
                    _vm.StateInternal["topHeadline"] = string.Empty;
                    _vm.StateInternal["feedCount"] = "0";
                }
            }

            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void SeedStudioPreviewNetworkStandIn()
    {
        bool isZh = !string.IsNullOrWhiteSpace(_vm.ActiveCultureName) &&
                    _vm.ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        static CustomWidgetRecord MakeArticle(string id, string title, string summary, string time, string category, string author) =>
            new()
            {
                Id = id,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["title"] = title,
                    ["text"] = title,
                    ["summary"] = summary,
                    ["subtitle"] = summary,
                    ["publishedAt"] = time,
                    ["category"] = category,
                    ["author"] = author,
                    ["url"] = string.Empty,
                    ["link"] = string.Empty
                }
            };

        _vm.RunWithHeadlessStateGate(() =>
        {
            foreach (CustomWidgetSourceSpec spec in _vm.ManifestInternal.Sources)
            {
                bool isRss = string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase);
                bool isJson = string.Equals(spec.Type, "httpJson", StringComparison.OrdinalIgnoreCase);
                if (!isRss && !isJson)
                {
                    continue;
                }

                string targetCol = string.IsNullOrWhiteSpace(spec.TargetCollection)
                    ? (isRss ? "articles" : "items")
                    : spec.TargetCollection;
                if (_vm.CollectionsInternal.TryGetValue(targetCol, out List<CustomWidgetRecord>? existing) && existing.Count > 0)
                {
                    continue;
                }

                List<CustomWidgetRecord> sampleItems = isZh
                    ?
                    [
                        MakeArticle("sample-rss-1", "Windows 桌面小组件渲染性能进一步优化", "本地原生引擎与异步加载管道显著降低内存占用。", "10:25", "Tech", "DeskBox"),
                        MakeArticle("sample-rss-2", "开源社区本周焦点：本地 AI 工具链更新", "支持更清晰的令牌统计与多日增量趋势分析。", "09:10", "AI", "Community"),
                        MakeArticle("sample-rss-3", "设计系统浅色与深色主题自适应指南", "保持高对比度字体排版与毛玻璃层次一致。", "08:40", "Design", "Studio")
                    ]
                    :
                    [
                        MakeArticle("sample-rss-1", "Desktop Widget Rendering Pipeline Highlights", "Native layout host and async sources keep cold start responsive.", "10:25", "Tech", "DeskBox"),
                        MakeArticle("sample-rss-2", "Open Source Weekly: Local AI CLI Tooling", "Updated token telemetry with per-day incremental trend buckets.", "09:10", "AI", "Community"),
                        MakeArticle("sample-rss-3", "Adaptive Light and Dark Surface Contrast Tips", "Consistent typography hierarchy across desktop card sizes.", "08:40", "Design", "Studio")
                    ];

                _vm.CollectionsInternal[targetCol] = sampleItems;
                _vm.SampleSeededCollectionsInternal.Add(targetCol);

                if (isRss)
                {
                    _vm.StateInternal["feedTitle"] = isZh ? "精选科技资讯" : "Tech Digest";
                    _vm.StateInternal["feedCount"] = sampleItems.Count.ToString(CultureInfo.InvariantCulture);
                    _vm.StateInternal["topHeadline"] = sampleItems[0].Fields["title"];
                }
            }
        });
        _vm.RefreshCapsuleProjection(bumpLiveState: false);
    }

}
