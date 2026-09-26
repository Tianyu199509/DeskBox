using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Views;
using Windows.Graphics;

namespace DeskBox.Services;

public sealed record WidgetGroupJoinTarget(
    string TargetWidgetId,
    string DisplayName,
    int MemberCount,
    bool CanJoin = true,
    string? RejectionReasonKey = null);

public sealed partial class WidgetManager
{
    private static readonly TimeSpan WidgetGroupFirstFrameTimeout =
        TimeSpan.FromMilliseconds(900);
    private readonly SemaphoreSlim _widgetGroupGate = new(1, 1);
    private readonly WidgetSurfaceSwitchGatePool _widgetSurfaceSwitchGates =
        new();
    internal int SurfaceSwitchGateCount => _widgetSurfaceSwitchGates.Count;
    private readonly WidgetGroupSwitchRequestCoordinator _widgetGroupSwitchRequests = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _groupDragDwellTimer;
    private string? _groupDragSourceId;
    private string? _groupDragTargetId;
    private bool _groupDragDropReady;
    private WidgetDetachPlacementPreviewWindow? _widgetDetachPlacementPreview;
    private readonly Dictionary<string, WidgetGroupTransientState> _widgetGroupTransientStates = [];
    // Residency P0: Stopwatch timestamp of when each member last left the
    // active slot, so a switch back can log how long it sat inactive.
    private readonly Dictionary<string, long> _widgetGroupMemberInactiveSince = [];
    private string _lastWidgetGroupDefaultNavigationStyle =
        WidgetGroupNavigationStyles.Stack;
    private string _lastWidgetGroupDefaultTitleDisplayMode =
        WidgetGroupTitleDisplayModes.IconAndText;
    private bool _lastWidgetGroupWheelSwitchEnabled = true;
    private bool _lastWidgetGroupHoverSwitchEnabled;

    internal void PrewarmWidgetDetachPlacementPreview(
        string caption,
        double cornerRadius)
    {
        if (_widgetDetachPlacementPreview is not null)
        {
            return;
        }

        try
        {
            _widgetDetachPlacementPreview =
                new WidgetDetachPlacementPreviewWindow(caption, cornerRadius);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Failed to prewarm detach preview: {ex}");
        }
    }

    internal WidgetDetachPlacementPreviewWindow AcquireWidgetDetachPlacementPreview(
        string caption,
        double cornerRadius)
    {
        PrewarmWidgetDetachPlacementPreview(caption, cornerRadius);
        WidgetDetachPlacementPreviewWindow preview =
            _widgetDetachPlacementPreview ??
            throw new InvalidOperationException(
                "The widget detach placement preview could not be created.");
        preview.BeginTracking(caption, cornerRadius);
        return preview;
    }

    private void DisposeWidgetDetachPlacementPreview()
    {
        WidgetDetachPlacementPreviewWindow? preview =
            _widgetDetachPlacementPreview;
        _widgetDetachPlacementPreview = null;
        preview?.Dispose();
    }

    public event Action? WidgetGroupsChanged;

    public bool IsWidgetGroupingEnabled => true;

    public void NotifyWidgetGroupingAvailabilityChanged()
    {
        // Retained for compatibility with older callers. Grouping is always
        // available now, so there is no runtime capability to refresh.
    }

    public async Task<bool> DissolveAllWidgetGroupsAsync()
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                DissolveAllWidgetGroupsAsync);
        }

        ClearGroupDragPreview();
        while (_settingsService.Settings.WidgetGroups.Count > 0)
        {
            WidgetGroupConfig group =
                _settingsService.Settings.WidgetGroups[0];
            string? memberId = group.MemberIds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(memberId))
            {
                _settingsService.Settings.WidgetGroups.Remove(group);
                _widgetSurfaceSwitchGates.Remove(group.SurfaceId);
                continue;
            }

            try
            {
                if (!await DissolveWidgetGroupContainingAsync(memberId))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetGroup] Failed to dissolve all groups " +
                    $"group={group.Id}: {ex}");
                return false;
            }
        }

        return true;
    }

    public void NotifyWidgetGroupPresentationSettingsChanged()
    {
        RaiseWidgetGroupsChanged();
    }

    private void InitializeWidgetGroupPresentationDefaults()
    {
        _lastWidgetGroupDefaultNavigationStyle =
            WidgetGroupNavigationStyles.Normalize(
                _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
                allowFollowDefault: false);
        _lastWidgetGroupDefaultTitleDisplayMode =
            WidgetGroupTitleDisplayModes.Normalize(
                _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
                allowFollowDefault: false);
        _lastWidgetGroupWheelSwitchEnabled =
            _settingsService.Settings.WidgetGroupWheelSwitchEnabled;
        _lastWidgetGroupHoverSwitchEnabled =
            _settingsService.Settings.WidgetGroupHoverSwitchEnabled;
    }

    private void RefreshWidgetGroupPresentationDefaultsIfChanged()
    {
        string navigationStyle = WidgetGroupNavigationStyles.Normalize(
            _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
            allowFollowDefault: false);
        string titleMode = WidgetGroupTitleDisplayModes.Normalize(
            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
            allowFollowDefault: false);
        bool wheelEnabled =
            _settingsService.Settings.WidgetGroupWheelSwitchEnabled;
        bool hoverEnabled =
            _settingsService.Settings.WidgetGroupHoverSwitchEnabled;
        if (string.Equals(
                navigationStyle,
                _lastWidgetGroupDefaultNavigationStyle,
                StringComparison.Ordinal) &&
            string.Equals(
                titleMode,
                _lastWidgetGroupDefaultTitleDisplayMode,
                StringComparison.Ordinal) &&
            wheelEnabled == _lastWidgetGroupWheelSwitchEnabled &&
            hoverEnabled == _lastWidgetGroupHoverSwitchEnabled)
        {
            return;
        }

        _lastWidgetGroupDefaultNavigationStyle = navigationStyle;
        _lastWidgetGroupDefaultTitleDisplayMode = titleMode;
        _lastWidgetGroupWheelSwitchEnabled = wheelEnabled;
        _lastWidgetGroupHoverSwitchEnabled = hoverEnabled;
        RaiseWidgetGroupsChanged();
    }

    public WidgetGroupPresentation? GetWidgetGroupPresentation(string widgetId)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is null)
        {
            return null;
        }

        var contentFactory = new WidgetContentFactory(_localizationService);
        var members = new List<WidgetGroupMemberPresentation>(group.MemberIds.Count);
        foreach (string memberId in group.MemberIds)
        {
            WidgetConfig? config = FindConfig(memberId);
            if (config is null)
            {
                continue;
            }

            string glyph;
            try
            {
                glyph = contentFactory.GetDescriptor(config.WidgetKind).DefaultGlyph;
            }
            catch
            {
                glyph = "\uE8A5";
            }

            members.Add(new WidgetGroupMemberPresentation(
                config.Id,
                ResolveGroupMemberDisplayName(config),
                config.WidgetKind,
                glyph,
                config.WidgetKind == WidgetKind.File
                    ? WidgetTitleIconKindNames.FromFileWidget(
                        config.FollowsDefaultStoragePath)
                    : WidgetTitleIconKindNames.FromWidgetKind(
                        config.WidgetKind),
                string.Equals(group.ActiveMemberId, config.Id, StringComparison.Ordinal)));
        }

        return members.Count < 2
            ? null
            : new WidgetGroupPresentation(
                group.Id,
                group.SurfaceId,
                group.ActiveMemberId,
                WidgetGroupNavigationStyles.Resolve(
                    group.NavigationStyle,
                    _settingsService.Settings.WidgetGroupDefaultNavigationStyle),
                WidgetGroupTitleDisplayModes.Resolve(
                    group.TitleDisplayMode,
                    _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode),
                group.WheelSwitchEnabled ??
                    _settingsService.Settings.WidgetGroupWheelSwitchEnabled,
                group.HoverSwitchEnabled ??
                    _settingsService.Settings.WidgetGroupHoverSwitchEnabled,
                members);
    }

    public async Task<bool> SetWidgetGroupNavigationStyleAsync(
        string widgetId,
        string navigationStyle)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => SetWidgetGroupNavigationStyleAsync(widgetId, navigationStyle));
        }

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is null)
        {
            return false;
        }

        string normalized = WidgetGroupNavigationStyles.Normalize(
            navigationStyle,
            allowFollowDefault: true);
        if (!string.Equals(group.NavigationStyle, normalized, StringComparison.Ordinal))
        {
            string previous = WidgetGroupNavigationStyles.Normalize(
                group.NavigationStyle,
                allowFollowDefault: true);
            group.NavigationStyle = normalized;
            if (string.Equals(
                    previous,
                    WidgetGroupNavigationStyles.Tabs,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    normalized,
                    WidgetGroupNavigationStyles.Tabs,
                    StringComparison.Ordinal) &&
                group.WheelSwitchEnabled == false)
            {
                // Tabs historically wrote an implicit wheel-off override.
                // Once the group leaves Tabs it should follow the current
                // application default again.
                group.WheelSwitchEnabled = null;
            }
            await _settingsService.SaveAsync();
            RaiseWidgetGroupsChanged();
        }

        return true;
    }

    public string? GetWidgetGroupNavigationStyle(string widgetId)
    {
        return WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId)?.NavigationStyle;
    }

    public string GetWidgetGroupDefaultNavigationStyle() =>
        WidgetGroupNavigationStyles.Normalize(
            _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
            allowFollowDefault: false);

    public async Task<bool> SetWidgetGroupTitleDisplayModeAsync(
        string widgetId,
        string displayMode)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => SetWidgetGroupTitleDisplayModeAsync(
                    widgetId,
                    displayMode));
        }

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is null)
        {
            return false;
        }

        string normalized = WidgetGroupTitleDisplayModes.Normalize(
            displayMode,
            allowFollowDefault: true);
        if (!string.Equals(
                group.TitleDisplayMode,
                normalized,
                StringComparison.Ordinal))
        {
            group.TitleDisplayMode = normalized;
            await _settingsService.SaveAsync();
            RaiseWidgetGroupsChanged();
        }

        return true;
    }

    public string? GetWidgetGroupTitleDisplayMode(string widgetId)
    {
        return WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId)?.TitleDisplayMode;
    }

    public string GetWidgetGroupDefaultTitleDisplayMode() =>
        WidgetGroupTitleDisplayModes.Normalize(
            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
            allowFollowDefault: false);

    public async Task<bool> SetWidgetGroupWheelSwitchEnabledAsync(
        string widgetId,
        bool? enabled)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => SetWidgetGroupWheelSwitchEnabledAsync(
                    widgetId,
                    enabled));
        }

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is null)
        {
            return false;
        }

        if (group.WheelSwitchEnabled != enabled)
        {
            group.WheelSwitchEnabled = enabled;
            await _settingsService.SaveAsync();
            RaiseWidgetGroupsChanged();
        }

        return true;
    }

    public bool? GetWidgetGroupWheelSwitchEnabled(string widgetId)
    {
        return WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId)?.WheelSwitchEnabled;
    }

    public bool GetWidgetGroupDefaultWheelSwitchEnabled() =>
        _settingsService.Settings.WidgetGroupWheelSwitchEnabled;

    public async Task<bool> SetWidgetGroupHoverSwitchEnabledAsync(
        string widgetId,
        bool? enabled)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => SetWidgetGroupHoverSwitchEnabledAsync(
                    widgetId,
                    enabled));
        }

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is null)
        {
            return false;
        }

        if (group.HoverSwitchEnabled != enabled)
        {
            group.HoverSwitchEnabled = enabled;
            await _settingsService.SaveAsync();
            RaiseWidgetGroupsChanged();
        }

        return true;
    }

    public bool? GetWidgetGroupHoverSwitchEnabled(string widgetId)
    {
        return WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId)?.HoverSwitchEnabled;
    }

    public bool GetWidgetGroupDefaultHoverSwitchEnabled() =>
        _settingsService.Settings.WidgetGroupHoverSwitchEnabled;

    public WidgetChromeMode? GetWidgetGroupChromeMode(string widgetId)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        return group is null
            ? null
             : WidgetGroupChromePolicy.NormalizePersistedMode(group.ChromeMode);
    }

    public bool IsWidgetGrouped(string widgetId)
    {
        return WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId) is not null;
    }

    public WidgetChromeMode ResolveWidgetChromeMode(
        WidgetConfig config,
        WidgetContentDescriptor descriptor)
    {
        return GetWidgetGroupChromeMode(config.Id) ??
               new WidgetChromeModeResolver(_settingsService).Resolve(
                   config,
                   descriptor);
    }

    private bool TryResolveGroupingChromeMode(
        WidgetConfig config,
        WidgetGroupConfig? group,
        out WidgetChromeMode mode)
    {
        try
        {
            if (group is not null)
            {
                mode = WidgetGroupChromePolicy.NormalizePersistedMode(
                    group.ChromeMode);
                return true;
            }

            WidgetContentDescriptor descriptor =
                new WidgetContentFactory(_localizationService)
                    .GetDescriptor(config.WidgetKind);
            mode = new WidgetChromeModeResolver(_settingsService).Resolve(
                config,
                descriptor);
            return mode != WidgetChromeMode.System;
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetGroup] Failed to resolve chrome id={config.Id}: " +
                ex.Message);
            mode = WidgetChromeMode.System;
            return false;
        }
    }

    private async Task SaveWidgetGroupSettingsCheckedAsync()
    {
        if (!await _settingsService.SaveCheckedAsync(
                notifySubscribers: false))
        {
            throw new IOException(
                "Failed to persist widget-group settings atomically.");
        }
    }

    private void SaveWidgetGroupActiveMemberDeferred()
    {
        // The selected member is runtime navigation state, not a topology
        // mutation. Coalescing rapid tab/stack/hotkey switches avoids a full
        // settings serialization and atomic file replacement per click.
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    /// <summary>
    /// A group topology mutation can replace the HWND that the user is
    /// interacting with. Preserve its raised layer for the replacement so a
    /// detached member cannot appear above the group that produced it.
    /// </summary>
    private bool ShouldPreserveRaisedWidgetLayer(params string?[] widgetIds)
    {
        if (_widgetsRaisedFromTray)
        {
            return true;
        }

        foreach (string? widgetId in widgetIds)
        {
            if (string.IsNullOrWhiteSpace(widgetId))
            {
                continue;
            }

            if (GetLoadedWindow(widgetId) is { Visible: true } window &&
                window.IsRaisedAboveDesktopLayer)
            {
                return true;
            }
        }

        return false;
    }

    private void RaiseVisibleWidgetTransitionWindows(
        IEnumerable<string> widgetIds,
        string reason)
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        var seenHandles = new HashSet<IntPtr>();
        var windows = new List<IDesktopWidgetWindow>();
        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (GetLoadedWindow(widgetId) is not { Visible: true } window ||
                window.WindowHandle == IntPtr.Zero ||
                !seenHandles.Add(window.WindowHandle))
            {
                continue;
            }

            windows.Add(window);
        }

        foreach (IDesktopWidgetWindow window in windows)
        {
            window.RaiseTemporarilyFromManager();
        }

        if (windows.Count > 0)
        {
            App.Log(
                $"[WidgetGroup] Preserved raised layer reason={reason} " +
                $"count={windows.Count} handles={string.Join(',', windows.Select(window => $"0x{window.WindowHandle.ToInt64():X}"))}");
        }
    }

    public IReadOnlyList<WidgetGroupJoinTarget> GetWidgetGroupJoinTargets(string sourceWidgetId)
    {
        if (!IsWidgetGroupingEnabled)
        {
            return [];
        }

        WidgetConfig? sourceConfig = FindConfig(sourceWidgetId);
        if (sourceConfig is null)
        {
            return [];
        }

        WidgetGroupConfig? sourceGroup = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            sourceWidgetId);
        int sourceMemberCount = sourceGroup?.MemberIds.Count ?? 1;
        var seenGroupIds = new HashSet<string>(StringComparer.Ordinal);
        bool sourceModeResolved = TryResolveGroupingChromeMode(
            sourceConfig,
            sourceGroup,
            out WidgetChromeMode sourceMode);

        var targets = new List<WidgetGroupJoinTarget>();

        foreach (WidgetConfig config in _settingsService.Settings.Widgets)
        {
            if (string.Equals(config.Id, sourceWidgetId, StringComparison.Ordinal) ||
                IsDeleted(config.Id) ||
                config.IsDisabled ||
                !_widgetRegistry.IsAvailableForSession(config, _settingsService.Settings))
            {
                continue;
            }

            WidgetGroupConfig? targetGroup = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                config.Id);
            if (targetGroup is not null)
            {
                if ((sourceGroup is not null &&
                     string.Equals(sourceGroup.Id, targetGroup.Id, StringComparison.Ordinal)) ||
                    !seenGroupIds.Add(targetGroup.Id) ||
                    targetGroup.MemberIds.Count + sourceMemberCount >
                    WidgetGroupSettings.MaximumMemberCount)
                {
                    continue;
                }

                WidgetConfig? active = FindConfig(targetGroup.ActiveMemberId);
                string name = ResolveGroupMemberDisplayName(active ?? config);
                bool targetModeResolved = TryResolveGroupingChromeMode(
                    config,
                    targetGroup,
                    out WidgetChromeMode targetMode);
                bool canJoin = sourceModeResolved &&
                               targetModeResolved &&
                               WidgetGroupChromePolicy.EvaluateMerge(
                                   sourceMode,
                                   targetMode).IsAllowed;
                targets.Add(new WidgetGroupJoinTarget(
                    targetGroup.ActiveMemberId,
                    name,
                    targetGroup.MemberIds.Count,
                    canJoin,
                    canJoin ? null : "Widget.Group.RequiresVisibleTitle"));
                continue;
            }

            if (!WidgetGroupSettings.IsActiveMember(_settingsService.Settings, config.Id))
            {
                continue;
            }

            if (sourceMemberCount + 1 > WidgetGroupSettings.MaximumMemberCount)
            {
                continue;
            }

            bool standaloneModeResolved = TryResolveGroupingChromeMode(
                config,
                group: null,
                out WidgetChromeMode standaloneMode);
            bool standaloneCanJoin = sourceModeResolved &&
                                     standaloneModeResolved &&
                                     WidgetGroupChromePolicy.EvaluateMerge(
                                         sourceMode,
                                         standaloneMode).IsAllowed;
            targets.Add(new WidgetGroupJoinTarget(
                config.Id,
                ResolveGroupMemberDisplayName(config),
                1,
                standaloneCanJoin,
                standaloneCanJoin ? null : "Widget.Group.RequiresVisibleTitle"));
        }

        return targets
            .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string ResolveGroupMemberDisplayName(WidgetConfig config)
    {
        if (!config.IsDefaultTitle)
        {
            return string.IsNullOrWhiteSpace(config.Name)
                ? config.WidgetKind.ToString()
                : config.Name;
        }

        string? localizationKey = config.WidgetKind switch
        {
            WidgetKind.QuickCapture => "QuickCapture.Name",
            WidgetKind.Todo => "Todo.Title",
            WidgetKind.Weather => "Weather.Title",
            WidgetKind.Music => "Music.Title",
            WidgetKind.Search => "Search.Title",
            WidgetKind.Glance => "Glance.Title",
            WidgetKind.Tags => "Tags.Title",
            WidgetKind.SystemMonitor => "SystemMonitor.Title",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(localizationKey))
        {
            string localized = _localizationService.T(localizationKey);
            if (!string.IsNullOrWhiteSpace(localized) &&
                !string.Equals(
                    localized,
                    localizationKey,
                    StringComparison.Ordinal))
            {
                return localized;
            }
        }

        return string.IsNullOrWhiteSpace(config.Name)
            ? config.WidgetKind.ToString()
            : config.Name;
    }

    public async Task<bool> MergeWidgetsAsync(string sourceWidgetId, string targetWidgetId)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => MergeWidgetsAsync(sourceWidgetId, targetWidgetId));
        }

        if (string.IsNullOrWhiteSpace(sourceWidgetId) ||
            string.IsNullOrWhiteSpace(targetWidgetId) ||
            string.Equals(sourceWidgetId, targetWidgetId, StringComparison.Ordinal) ||
            !IsWidgetGroupingEnabled)
        {
            return false;
        }

        await _widgetGroupGate.WaitAsync();
        try
        {
            AppSettings settings = _settingsService.Settings;
            WidgetConfig? sourceConfig = FindConfig(sourceWidgetId);
            WidgetConfig? targetConfig = FindConfig(targetWidgetId);
            if (sourceConfig is null || targetConfig is null ||
                IsDeleted(sourceWidgetId) || IsDeleted(targetWidgetId))
            {
                return false;
            }

            WidgetGroupSettings.Normalize(_settingsService.Settings);
            WidgetGroupConfig? sourceGroup = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                sourceWidgetId);
            WidgetGroupConfig? targetGroup = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                targetWidgetId);
            if (sourceGroup is not null &&
                targetGroup is not null &&
                string.Equals(sourceGroup.Id, targetGroup.Id, StringComparison.Ordinal))
            {
                return false;
            }

            // A member switch uses Surface gates rather than the topology
            // gate. Let any in-flight switch settle before reading the active
            // member, and keep both surfaces stable through the merge commit.
            using IDisposable sourceAndTargetSwitches =
                await _widgetSurfaceSwitchGates.AcquireManyAsync(
                    [sourceGroup?.SurfaceId, targetGroup?.SurfaceId]);

            bool preserveRaisedLayer = ShouldPreserveRaisedWidgetLayer(
                sourceGroup?.ActiveMemberId ?? sourceWidgetId,
                targetGroup?.ActiveMemberId ?? targetWidgetId);

            if (!TryResolveGroupingChromeMode(
                    sourceConfig,
                    sourceGroup,
                    out WidgetChromeMode sourceMode) ||
                !TryResolveGroupingChromeMode(
                    targetConfig,
                    targetGroup,
                    out WidgetChromeMode targetMode))
            {
                App.Log(
                    $"[WidgetGroup] Merge rejected because chrome could not " +
                    $"be resolved source={sourceWidgetId} target={targetWidgetId}");
                return false;
            }

            WidgetGroupChromeDecision chromeDecision =
                WidgetGroupChromePolicy.EvaluateMerge(
                    sourceMode,
                    targetMode);
            if (!chromeDecision.IsAllowed ||
                chromeDecision.GroupMode is not { } groupMode)
            {
                App.Log(
                    $"[WidgetGroup] Merge rejected source={sourceWidgetId} " +
                    $"target={targetWidgetId} mode={chromeDecision.RejectedMode} " +
                    $"reason={chromeDecision.RejectionReason}");
                return false;
            }

            List<string> targetMembers = targetGroup?.MemberIds.ToList() ?? [targetWidgetId];
            List<string> sourceMembers = sourceGroup?.MemberIds.ToList() ?? [sourceWidgetId];
            List<string> combinedMembers = targetMembers
                .Concat(sourceMembers)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (combinedMembers.Count > WidgetGroupSettings.MaximumMemberCount)
            {
                App.Log(
                    $"[WidgetGroup] Merge rejected source={sourceWidgetId} target={targetWidgetId} " +
                    $"members={combinedMembers.Count} max={WidgetGroupSettings.MaximumMemberCount}");
                return false;
            }

            string activeTargetId = targetGroup?.ActiveMemberId ?? targetWidgetId;
            WidgetConfig activeTargetConfig = FindConfig(activeTargetId) ?? targetConfig;
            List<WidgetGroupConfig> groupSnapshot =
                _settingsService.Settings.WidgetGroups
                    .Select(CloneWidgetGroupConfig)
                    .ToList();
            WidgetGroupConfig mergedGroup = targetGroup ??
                CreateGroupFromTarget(activeTargetConfig, groupMode);
            using IDisposable? newGroupSwitches = targetGroup is null
                ? await _widgetSurfaceSwitchGates.AcquireManyAsync(
                    [mergedGroup.SurfaceId])
                : null;
            IReadOnlyList<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>>
                retiringSurfaceClaims;
            try
            {
                retiringSurfaceClaims = _widgetSurfaces.CaptureGroupClaimTransfers(
                    new WidgetSurfaceDefinition(
                        mergedGroup.SurfaceId,
                        mergedGroup.Id,
                        combinedMembers,
                        activeTargetId),
                    sourceGroupId: sourceGroup?.Id,
                    sourceSurfaceId: sourceGroup?.SurfaceId);
            }
            catch (InvalidOperationException ex)
            {
                App.Log(
                    $"[WidgetGroup] Merge rejected because a member Surface " +
                    $"claim changed source={sourceWidgetId} target={targetWidgetId}: {ex}");
                return false;
            }
            if (targetGroup is not null)
            {
                WidgetConfig? loadedTargetConfig = GetLoadedWindow(activeTargetId)?.Config;
                if (loadedTargetConfig is not null)
                {
                    CaptureGroupLayout(mergedGroup, loadedTargetConfig);
                }
            }

            mergedGroup.MemberIds = combinedMembers;
            mergedGroup.ActiveMemberId = activeTargetId;
            mergedGroup.IsVisible = GetLoadedWindow(activeTargetId)?.Visible ??
                                    targetGroup?.IsVisible ??
                                    activeTargetConfig.IsVisible;

            if (sourceGroup is not null)
            {
                _settingsService.Settings.WidgetGroups.Remove(sourceGroup);
            }
            if (targetGroup is null)
            {
                _settingsService.Settings.WidgetGroups.Add(mergedGroup);
            }
            List<WidgetGroupConfig> committedGroups =
                settings.WidgetLayout.WidgetGroups.ToList();
            bool topologySaveAttempted = false;
            bool topologyPersisted = false;

            // Establish a ready, visible unified host before retiring any
            // member window. A construction/readiness failure must leave the
            // target and source windows available instead of making the group
            // disappear.
            try
            {
                await PromoteGroupToUnifiedSurfaceHostAsync(
                    mergedGroup,
                    beforeRetireAsync: async () =>
                    {
                        topologySaveAttempted = true;
                        await SaveWidgetGroupSettingsCheckedAsync();
                        topologyPersisted = true;
                        WidgetGroupFailureProbe.ThrowIfRequested("merge-post-save-commit");
                    },
                    preserveRaisedLayer: preserveRaisedLayer,
                    expectedRetiringClaims: retiringSurfaceClaims);
            }
            catch (Exception ex)
            {
                if (!topologyPersisted)
                {
                    settings.WidgetLayout.WidgetGroups = groupSnapshot;
                    if (topologySaveAttempted)
                    {
                        try
                        {
                            await SaveWidgetGroupSettingsCheckedAsync();
                        }
                        catch (Exception rollbackSaveException)
                        {
                            App.Log(
                                $"[WidgetGroup] Merge pre-commit rollback save " +
                                $"failed source={sourceWidgetId} " +
                                $"target={targetWidgetId}: {rollbackSaveException}");
                        }
                    }
                    try
                    {
                        SynchronizeLoadedSurfaceDefinitions();
                    }
                    catch (Exception reconcileError)
                    {
                        App.Log(
                            $"[WidgetGroup] Merge pre-commit host " +
                            $"reconciliation failed source={sourceWidgetId} " +
                            $"target={targetWidgetId}: {reconcileError}");
                    }
                    App.Log(
                        $"[WidgetGroup] Merge candidate rolled back before " +
                        $"topology save source={sourceWidgetId} target={targetWidgetId}: {ex}");
                    return false;
                }

                bool rollbackSaved = false;
                try
                {
                    rollbackSaved = await WidgetGroupPersistedTopologyRecovery
                        .TryRestorePreviousAsync(
                             () => settings.WidgetLayout.WidgetGroups = groupSnapshot,
                             () => WidgetGroupFailureProbe.Consume("merge-rollback-save")
                                 ? Task.FromResult(false)
                                 : _settingsService.SaveCheckedAsync(
                                     notifySubscribers: false),
                            () => settings.WidgetLayout.WidgetGroups = committedGroups);
                }
                catch (Exception rollbackSaveException)
                {
                    App.Log(
                        $"[WidgetGroup] Merge rollback save failed; " +
                        $"retaining committed topology " +
                        $"source={sourceWidgetId} target={targetWidgetId}: " +
                        rollbackSaveException);
                }
                if (rollbackSaved)
                {
                    try
                    {
                        SynchronizeLoadedSurfaceDefinitions();
                    }
                    catch (Exception reconcileError)
                    {
                        App.Log(
                            $"[WidgetGroup] Merge rollback host reconciliation " +
                            $"failed source={sourceWidgetId} " +
                            $"target={targetWidgetId}: {reconcileError}");
                    }
                    App.Log(
                        $"[WidgetGroup] Merge promotion failed after durable rollback " +
                        $"source={sourceWidgetId} target={targetWidgetId}: {ex}");
                    return false;
                }

                App.Log(
                    $"[WidgetGroup] Merge rollback could not be persisted; " +
                    $"recovering committed topology " +
                    $"source={sourceWidgetId} target={targetWidgetId}: {ex}");
                if (!await TryRecoverCommittedMergeAfterRollbackFailureAsync(
                        mergedGroup,
                        sourceGroup,
                        combinedMembers,
                        activeTargetId,
                        preserveRaisedLayer))
                {
                    App.Log(
                        $"[WidgetGroup] Merge committed topology is unavailable " +
                        $"source={sourceWidgetId} target={targetWidgetId}");
                    return false;
                }
                App.Log(
                    $"[WidgetGroup] Merge recovered committed topology " +
                    $"source={sourceWidgetId} target={targetWidgetId}");
            }

            ApplyGroupLayoutToMembers(mergedGroup);
            NormalizeCapsuleIdentityForGroup(mergedGroup);
            try
            {
                await SaveWidgetGroupSettingsCheckedAsync();
            }
            catch (Exception postCommitSaveException)
            {
                // The topology was durably saved before the legacy host was
                // retired. Keep the ready unified host alive if this secondary
                // member-layout save fails; the group layout remains the
                // authoritative source and will be reapplied on the next load.
                App.Log(
                    $"[WidgetGroup] Post-commit member layout save failed " +
                    $"group={mergedGroup.Id}: {postCommitSaveException}");
            }

            foreach (string memberId in combinedMembers)
            {
                if (string.Equals(memberId, activeTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                RetireLoadedWindowForGroup(memberId, keepConfigVisible: mergedGroup.IsVisible);
            }

            if (sourceGroup is not null &&
                !string.Equals(
                    sourceGroup.SurfaceId,
                    mergedGroup.SurfaceId,
                    StringComparison.Ordinal))
            {
                _widgetSurfaceSwitchGates.Remove(sourceGroup.SurfaceId);
            }

            if (preserveRaisedLayer && mergedGroup.IsVisible)
            {
                RaiseVisibleWidgetTransitionWindows(
                    new[] { mergedGroup.ActiveMemberId },
                    "group-merge");
            }

            App.Log(
                $"[WidgetGroup] Merged source={sourceWidgetId} target={targetWidgetId} " +
                $"group={mergedGroup.Id} active={mergedGroup.ActiveMemberId} members={mergedGroup.MemberIds.Count}");
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return true;
        }
        finally
        {
            _widgetGroupGate.Release();
        }
    }

    private async Task<bool> TryRecoverCommittedMergeAfterRollbackFailureAsync(
        WidgetGroupConfig committedGroup,
        WidgetGroupConfig? sourceGroup,
        IReadOnlyList<string> memberIds,
        string activeTargetId,
        bool preserveRaisedLayer)
    {
        WidgetSurfaceDefinition definition = CreateSurfaceDefinition(committedGroup);
        IDesktopWidgetWindow? survivingTarget =
            WidgetGroupFailureProbe.Consume("merge-recovery-target-unavailable")
                ? null
                : GetLegacyLoadedWindow(activeTargetId);
        if (survivingTarget is not null)
        {
            try
            {
                WidgetGroupPersistedTopologyRecovery.ReconcileCommittedMergeClaims(
                    _widgetSurfaces,
                    definition,
                    survivingTarget,
                    sourceGroup?.Id,
                    sourceGroup?.SurfaceId);
                RemoveFileWidgetSessionsForHost(survivingTarget);
                App.Log(
                    $"[WidgetGroup] Committed merge adopted surviving target " +
                    $"group={committedGroup.Id} hwnd=0x{survivingTarget.WindowHandle.ToInt64():X}");
                return true;
            }
            catch (Exception reconciliationError)
            {
                App.Log(
                    $"[WidgetGroup] Committed merge target reconciliation failed " +
                    $"group={committedGroup.Id}: {reconciliationError}");
            }
        }

        if (committedGroup.IsVisible)
        {
            try
            {
                WidgetGroupFailureProbe.ThrowIfRequested("merge-recovery-promotion");
                IReadOnlyList<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>> claims =
                    _widgetSurfaces.CaptureGroupClaimTransfers(
                        definition,
                        sourceGroupId: sourceGroup?.Id,
                        sourceSurfaceId: sourceGroup?.SurfaceId);
                await PromoteGroupToUnifiedSurfaceHostAsync(
                    committedGroup,
                    preserveRaisedLayer: preserveRaisedLayer,
                    expectedRetiringClaims: claims);
                if (GetLegacyLoadedWindow(activeTargetId) is null)
                    throw new InvalidOperationException(
                        "Recovered group promotion did not retain an active host.");
                App.Log($"[WidgetGroup] Committed merge re-created active host group={committedGroup.Id}");
                return true;
            }
            catch (Exception promotionError)
            {
                App.Log(
                    $"[WidgetGroup] Committed merge re-promotion failed " +
                    $"group={committedGroup.Id}: {promotionError}");
            }
        }

        return await QuarantineCommittedMergeAsync(
            committedGroup, sourceGroup, memberIds, preserveRaisedLayer);
    }

    private async Task<bool> QuarantineCommittedMergeAsync(
        WidgetGroupConfig committedGroup,
        WidgetGroupConfig? sourceGroup,
        IReadOnlyList<string> memberIds,
        bool preserveRaisedLayer)
    {
        var affectedHosts = new HashSet<IDesktopWidgetWindow>(
            ReferenceEqualityComparer.Instance);
        foreach (string memberId in memberIds)
        {
            if (GetLegacyLoadedWindow(memberId) is { } loaded)
                affectedHosts.Add(loaded);
            if (_widgetSurfaces.TryGetByMember(memberId, out var session) &&
                session is not null)
                affectedHosts.Add(session.Host);
        }

        foreach (IDesktopWidgetWindow host in affectedHosts)
        {
            try
            {
                RetireSpecificLoadedWindowForGroup(
                    host.Config.Id, host, keepConfigVisible: committedGroup.IsVisible);
            }
            catch (Exception retirementError)
            {
                App.Log(
                    $"[WidgetGroup] Committed merge host quarantine failed " +
                    $"group={committedGroup.Id}: {retirementError}");
                try
                {
                    CloseFailedGroupReplacement(host.Config.Id, host);
                }
                catch (Exception cleanupError)
                {
                    App.Log(
                        $"[WidgetGroup] Committed merge fallback close failed " +
                        $"group={committedGroup.Id}: {cleanupError}");
                }
            }
            try
            {
                _widgetSurfaces.UnregisterHost(host);
            }
            catch (Exception quarantineError)
            {
                App.Log(
                    $"[WidgetGroup] Committed merge host unregister failed " +
                    $"group={committedGroup.Id}: {quarantineError}");
            }
        }

        foreach (string memberId in memberIds)
        {
            if (_widgetSurfaces.TryGetByMember(memberId, out var residual) &&
                residual is not null)
            {
                try
                {
                    _widgetSurfaces.UnregisterHost(residual.Host);
                }
                catch (Exception quarantineError)
                {
                    App.Log(
                        $"[WidgetGroup] Committed merge residual unregister " +
                        $"failed group={committedGroup.Id}: {quarantineError}");
                }
            }
        }

        if (sourceGroup is not null &&
            !string.Equals(sourceGroup.SurfaceId, committedGroup.SurfaceId,
                StringComparison.Ordinal))
            _widgetSurfaceSwitchGates.Remove(sourceGroup.SurfaceId);

        if (committedGroup.IsVisible)
        {
            try
            {
                WidgetGroupFailureProbe.ThrowIfRequested("merge-recovery-rebuild");
                if (await ShowGroupActiveWindowAsync(
                        committedGroup, preserveRaisedLayer) is not null)
                {
                    App.Log(
                        $"[WidgetGroup] Committed merge rebuilt after quarantine " +
                        $"group={committedGroup.Id}");
                    return true;
                }
            }
            catch (Exception rebuildError)
            {
                App.Log(
                    $"[WidgetGroup] Committed merge remains without a live host " +
                    $"group={committedGroup.Id}: {rebuildError}");
            }
        }

        ApplyGroupLayoutToMembers(committedGroup);
        NormalizeCapsuleIdentityForGroup(committedGroup);
        try
        {
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
        }
        catch (Exception notificationError)
        {
            App.Log(
                $"[WidgetGroup] Committed merge quarantine notification failed " +
                $"group={committedGroup.Id}: {notificationError}");
        }
        return !committedGroup.IsVisible;
    }

    public async Task<bool> SwitchWidgetGroupMemberAsync(
        string targetWidgetId,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => SwitchWidgetGroupMemberAsync(targetWidgetId, origin));
        }

        long switchStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        WidgetGroupConfig? requestedGroup = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            targetWidgetId);
        if (requestedGroup is null)
        {
            return false;
        }

        if (_widgetGroupSwitchRequests.IsCurrentTarget(
                requestedGroup.SurfaceId,
                targetWidgetId))
        {
            // Do not cancel and rebuild the exact same slow switch. The
            // in-flight request already represents the latest user intent.
            App.LogVerbose(
                $"[WidgetGroup] Coalesced duplicate switch " +
                $"surface={requestedGroup.SurfaceId} target={targetWidgetId} " +
                $"origin={origin}");
            return true;
        }

        WidgetGroupSwitchRequest request = _widgetGroupSwitchRequests.Begin(
            requestedGroup.SurfaceId,
            targetWidgetId,
            origin);
        bool gateEntered = false;
        SemaphoreSlim? switchGate = null;
        try
        {
            try
            {
                switchGate = GetWidgetSurfaceSwitchGate(requestedGroup);
                await switchGate.WaitAsync(request.CancellationToken);
                gateEntered = true;
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                targetWidgetId);
            if (group is null ||
                !string.Equals(group.SurfaceId, request.GroupId, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(group.ActiveMemberId, targetWidgetId, StringComparison.Ordinal))
            {
                return true;
            }

            WidgetConfig? targetConfig = FindConfig(targetWidgetId);
            if (targetConfig is null ||
                !_widgetRegistry.IsAvailableForSession(targetConfig, _settingsService.Settings))
            {
                return false;
            }

            string previousActiveId = group.ActiveMemberId;
            WidgetConfig? previousConfig = FindConfig(previousActiveId);
            if (previousConfig is not null)
            {
                CaptureGroupLayout(group, previousConfig);
            }

            // The target needs the group's effective layout while it is being
            // prepared, but the active identity remains unchanged until the
            // target has initialized and produced a presentable frame.
            ApplyGroupLayoutToMember(group, targetConfig);

            if (GetLoadedWindow(previousActiveId) is not ContentWidgetWindow)
            {
                await PromoteGroupToUnifiedSurfaceHostAsync(group);
            }

            ContentWidgetWindow? persistentContentWindow =
                GetLoadedWindow(previousActiveId) as ContentWidgetWindow;
            if (persistentContentWindow is null && group.IsVisible)
            {
                await ShowGroupActiveWindowAsync(group);
                persistentContentWindow =
                    GetLoadedWindow(previousActiveId) as ContentWidgetWindow;
            }

            if (persistentContentWindow is null)
            {
                // A fully hidden group has no runtime Surface. Updating its
                // active identity is sufficient; the unified host will be
                // created for this member on the next reveal.
                TransferCapsuleIdentity(previousActiveId, targetWidgetId);
                group.ActiveMemberId = targetWidgetId;
                SaveWidgetGroupActiveMemberDeferred();

                RaiseWidgetGroupsChanged();
                ApplyCapsuleArrangementIfChanged(force: true);
                return true;
            }

            ContentWidgetWindowFactory contentWindowFactory =
                CreateSurfaceContentWindowFactory();
            if (!contentWindowFactory.CanCreateContentWindow(targetConfig.WidgetKind))
            {
                throw new NotSupportedException(
                    $"Grouped widget kind '{targetConfig.WidgetKind}' has no unified Surface content.");
            }

            try
            {
                return await SwitchContentWidgetGroupMemberInPlaceAsync(
                    request,
                    group,
                    previousActiveId,
                    previousConfig,
                    targetConfig,
                    persistentContentWindow,
                    contentWindowFactory,
                    switchStartedTimestamp);
            }
            catch (OperationCanceledException)
                when (request.CancellationToken.IsCancellationRequested)
            {
                App.LogVerbose(
                    $"[WidgetGroup] Surface switch superseded group={group.Id} " +
                    $"target={targetWidgetId}");
                return false;
            }

        }
        finally
        {
            if (gateEntered)
            {
                switchGate!.Release();
            }
            _widgetGroupSwitchRequests.Complete(request);
        }
    }

    private async Task<bool> SwitchContentWidgetGroupMemberInPlaceAsync(
        WidgetGroupSwitchRequest request,
        WidgetGroupConfig group,
        string previousActiveId,
        WidgetConfig? previousConfig,
        WidgetConfig targetConfig,
        ContentWidgetWindow persistentWindow,
        ContentWidgetWindowFactory contentWindowFactory,
        long switchStartedTimestamp)
    {
        if (!_contentWindowRegistration.CanRebind(targetConfig.Id, persistentWindow))
        {
            App.Log($"[WidgetGroup] In-place switch rejected: content registration missing or conflicting id={targetConfig.Id}");
            return false;
        }

        IWidgetContent? cachedContent =
            persistentWindow.TakeCachedGroupContent(targetConfig.Id);
        var timeline = new WidgetGroupSwitchTimeline(
            cachedContent is null
                ? WidgetGroupSwitchTimeline.SourceFresh
                : WidgetGroupSwitchTimeline.SourceCached,
            switchStartedTimestamp);
        ContentWidgetWindowPlan plan =
            contentWindowFactory.CreateContentWindowPlan(targetConfig, cachedContent);
        PreviewWidgetGroupTransientState(
            targetConfig.Id,
            plan.Content as IWidgetTransientStateContent);
        using var loadingDelayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
        Task loadingDelay = ShowGroupMemberLoadingAfterDelayAsync(
            persistentWindow,
            targetConfig.Id,
            loadingDelayCancellation.Token);
        ContentWidgetWindow.ContentWidgetSwitchPreparation? preparedContent;
        try
        {
            preparedContent = await persistentWindow.PrepareContentSwitchAsync(
                plan.Config,
                plan.Content,
                plan.Descriptor,
                request.CancellationToken);
        }
        finally
        {
            loadingDelayCancellation.Cancel();
            persistentWindow.SetGroupMemberLoading(
                targetConfig.Id,
                isLoading: false);
            try
            {
                await loadingDelay;
            }
            catch (OperationCanceledException)
            {
            }
        }

        using ContentWidgetWindow.ContentWidgetSwitchPreparation? preparation =
            preparedContent;
        if (preparation is null)
        {
            return false;
        }

        timeline.MarkPrepared(System.Diagnostics.Stopwatch.GetTimestamp());
        PreviewWidgetGroupTransientState(
            targetConfig.Id,
            plan.Content as IWidgetTransientStateContent);

        request.CancellationToken.ThrowIfCancellationRequested();
        using ContentWidgetWindow.ContentWidgetSwitchTransition? transition =
            preparation.BeginTransition();
        if (transition is null)
        {
            return false;
        }
        if (!_contentWindowRegistration.CanRebind(targetConfig.Id, persistentWindow))
        {
            transition.Rollback();
            App.Log($"[WidgetGroup] In-place switch rejected after preparation: content registration changed id={targetConfig.Id}");
            return false;
        }
        LogWidgetSurfaceEvidence(group, "transition");

        if (group.IsVisible && persistentWindow.Visible)
        {
            // Once the prepared view has entered the live presenter this
            // transaction must settle atomically. A later navigation request
            // waits on the surface gate; it must not cancel us between the
            // visual swap and the group identity commit.
            using var frameTimeout = new CancellationTokenSource();
            frameTimeout.CancelAfter(WidgetGroupFirstFrameTimeout);
            try
            {
                await persistentWindow.WaitForFirstPresentedFrameAsync(
                    frameTimeout.Token);
                timeline.MarkFirstFrame(System.Diagnostics.Stopwatch.GetTimestamp());
            }
            catch (OperationCanceledException)
                when (frameTimeout.IsCancellationRequested)
            {
                App.Log(
                    $"[WidgetGroup] In-place first-frame wait timed out; " +
                    $"keeping previous member group={group.Id} " +
                    $"target={targetConfig.Id} previous={previousActiveId} " +
                    timeline.Describe(sinceLastActive: null));
                transition.Rollback();
                LogWidgetSurfaceEvidence(group, "timeout-rollback");
                return false;
            }
        }

        TransferCapsuleIdentity(previousActiveId, targetConfig.Id);
        group.ActiveMemberId = targetConfig.Id;
        try
        {
            CommitSurfaceHost(group, persistentWindow);
        }
        catch
        {
            group.ActiveMemberId = previousActiveId;
            TransferCapsuleIdentity(targetConfig.Id, previousActiveId);
            if (previousConfig is not null)
            {
                ApplyGroupLayoutToMember(group, previousConfig);
            }
            LogWidgetSurfaceEvidence(group, "rollback");
            throw;
        }

        IWidgetContent? outgoingContent = transition.OutgoingContent;
        try
        {
            CaptureWidgetGroupTransientState(
                previousActiveId,
                outgoingContent as IWidgetTransientStateContent);
            int previousIndex = group.MemberIds.IndexOf(previousActiveId);
            int targetIndex = group.MemberIds.IndexOf(targetConfig.Id);
            await transition.CompleteAsync(
                request.Origin,
                forward: targetIndex >= previousIndex,
                CancellationToken.None);
        }
        catch
        {
            ClearWidgetGroupTransientState(previousActiveId);
            group.ActiveMemberId = previousActiveId;
            TransferCapsuleIdentity(targetConfig.Id, previousActiveId);
            if (previousConfig is not null)
            {
                ApplyGroupLayoutToMember(group, previousConfig);
            }

            try
            {
                CommitSurfaceHost(group, persistentWindow);
            }
            catch (Exception registryException)
            {
                App.Log(
                    $"[WidgetSurface] Failed to restore registry during rollback " +
                    $"surface={group.SurfaceId}: {registryException}");
            }

            throw;
        }

        _contentWindowRegistration.Rebind(targetConfig.Id, persistentWindow);
        RestoreWidgetGroupTransientState(targetConfig.Id);
        SaveWidgetGroupActiveMemberDeferred();

        if (group.IsVisible && !persistentWindow.Visible)
        {
            persistentWindow.PrepareTrayShowAnimation();
            if (_widgetsRaisedFromTray)
            {
                persistentWindow.ShowPreparedRaisedFromTray(
                    persistVisibility: false);
            }
            else
            {
                persistentWindow.ShowPreparedAtDesktopLayer(
                    persistVisibility: false);
            }

            persistentWindow.CompleteTrayShowWithoutAnimation();
        }

        long settledTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        timeline.MarkSettled(settledTimestamp);
        TimeSpan? sinceLastActive =
            _widgetGroupMemberInactiveSince.Remove(targetConfig.Id, out long inactiveSince)
                ? System.Diagnostics.Stopwatch.GetElapsedTime(inactiveSince, settledTimestamp)
                : null;
        _widgetGroupMemberInactiveSince[previousActiveId] = settledTimestamp;
        App.Log(
            $"[WidgetGroup] Switched persistent surface={group.SurfaceId} " +
            $"group={group.Id} origin={request.Origin} " +
            $"{previousActiveId} -> {targetConfig.Id} " +
            $"hwnd=0x{persistentWindow.WindowHandle.ToInt64():X} " +
            timeline.Describe(sinceLastActive));
        LogWidgetSurfaceEvidence(group, "settled");
        RaiseWidgetGroupsChanged();
        ApplyCapsuleArrangementIfChanged(force: true);
        return true;
    }

    private static async Task ShowGroupMemberLoadingAfterDelayAsync(
        ContentWidgetWindow window,
        string widgetId,
        CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        window.SetGroupMemberLoading(widgetId, isLoading: true);
    }

    public async Task<bool> RemoveWidgetFromGroupAsync(
        string widgetId,
        bool revealStandalone,
        PointInt32? detachedPosition = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => RemoveWidgetFromGroupAsync(
                    widgetId,
                    revealStandalone,
                    detachedPosition));
        }

        // Hand-edited settings can leave a blank SurfaceId; the switch-gate
        // pool rejects those, so repair the topology before any lookup.
        WidgetGroupSettings.Normalize(_settingsService.Settings);
        WidgetGroupConfig? pendingGroup = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (pendingGroup is not null)
        {
            _widgetGroupSwitchRequests.Cancel(pendingGroup.SurfaceId);
        }

        await _widgetGroupGate.WaitAsync();
        try
        {
            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                widgetId);
            WidgetConfig? removedConfig = FindConfig(widgetId);
            if (group is null || removedConfig is null)
            {
                return false;
            }

            using IDisposable surfaceSwitch =
                await _widgetSurfaceSwitchGates.AcquireManyAsync(
                    [group.SurfaceId]);

            var detachCommitStopwatch = System.Diagnostics.Stopwatch.StartNew();
            string previousActiveId = group.ActiveMemberId;
            IDesktopWidgetWindow? originalHost =
                GetLoadedWindow(previousActiveId);
            bool preserveRaisedLayer =
                ShouldPreserveRaisedWidgetLayer(previousActiveId);
            bool raiseTransitionWindows =
                detachedPosition.HasValue || preserveRaisedLayer;
            ContentWidgetWindow? reusableDetachedHost =
                detachedPosition.HasValue &&
                string.Equals(previousActiveId, widgetId, StringComparison.Ordinal)
                    ? GetLoadedWindow(previousActiveId) as ContentWidgetWindow
                    : null;

            WidgetGroupMutationSnapshot rollbackSnapshot =
                WidgetGroupMutationSnapshot.Capture(this, group);
            int removedIndex = group.MemberIds.IndexOf(widgetId);
            bool removedWasActive = string.Equals(
                group.ActiveMemberId,
                widgetId,
                StringComparison.Ordinal);
            List<string> previousMembers = group.MemberIds.ToList();
            group.MemberIds.Remove(widgetId);
            // No longer an inactive group member — drop its residency
            // timestamp so a never-reactivated id cannot linger in the map.
            _widgetGroupMemberInactiveSince.Remove(widgetId);

            PlaceDetachedMember(
                removedConfig,
                group,
                Math.Max(1, removedIndex + 1),
                detachedPosition);
            removedConfig.IsVisible = revealStandalone && group.IsVisible;

            WidgetGroupConfig? survivingGroup = group.MemberIds.Count >= 2 ? group : null;
            if (survivingGroup is null)
            {
                _settingsService.Settings.WidgetGroups.Remove(group);
                foreach (string exMemberId in group.MemberIds)
                {
                    _widgetGroupMemberInactiveSince.Remove(exMemberId);
                }

                if (group.MemberIds.FirstOrDefault() is { } remainingId &&
                    FindConfig(remainingId) is { } remainingConfig)
                {
                    ApplyGroupLayoutToMember(group, remainingConfig);
                    remainingConfig.IsVisible = group.IsVisible;
                }
            }
            else
            {
                if (removedWasActive)
                {
                    int nextIndex = Math.Clamp(removedIndex, 0, group.MemberIds.Count - 1);
                    TransferCapsuleIdentity(widgetId, group.MemberIds[nextIndex]);
                    group.ActiveMemberId = group.MemberIds[nextIndex];
                }
                ApplyGroupLayoutToMembers(group);
                NormalizeCapsuleIdentityForGroup(group);
            }

            if (survivingGroup is null &&
                removedWasActive &&
                group.MemberIds.FirstOrDefault() is { } remainingMemberId)
            {
                TransferCapsuleIdentity(widgetId, remainingMemberId);
            }

            bool persisted;
            var persistenceStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                persisted = await _settingsService.SaveCheckedAsync();
            }
            catch
            {
                rollbackSnapshot.Restore(this);
                throw;
            }
            double persistenceElapsedMs = persistenceStopwatch.Elapsed.TotalMilliseconds;

            if (!persisted)
            {
                rollbackSnapshot.Restore(this);
                App.Log(
                    $"[WidgetGroup] Rolled back detach because settings save failed " +
                    $"group={group.Id} member={widgetId}");
                RaiseWidgetGroupsChanged();
                ApplyCapsuleArrangementIfChanged(force: true);
                return false;
            }

            WidgetGroupMutationSnapshot committedSnapshot =
                WidgetGroupMutationSnapshot.Capture(
                    this,
                    group,
                    additionalMemberIds: [removedConfig.Id]);

            WidgetGroupDetachSurfaceReuseResult reuseResult =
                await TryCompleteDetachedActiveSurfaceReuseAsync(
                    group,
                    survivingGroup,
                    removedConfig,
                    reusableDetachedHost,
                    rollbackSnapshot,
                    committedSnapshot,
                    raiseTransitionWindows);
            if (reuseResult == WidgetGroupDetachSurfaceReuseResult.Failed)
            {
                return false;
            }

            bool reusedSurface =
                reuseResult == WidgetGroupDetachSurfaceReuseResult.Completed;
            if (!reusedSurface)
            {
                var created = new List<(
                    string MemberId,
                    IDesktopWidgetWindow Host)>();
                try
                {
                    foreach (string memberId in previousMembers)
                    {
                        RetireLoadedWindowForGroup(
                            memberId,
                            keepConfigVisible: FindConfig(memberId)?.IsVisible == true);
                    }

                    // Closed may arrive after the replacement starts creating.
                    // The old host has been retired explicitly; release its
                    // claim before standalone members take their aliases.
                    _widgetSurfaces.RemoveSurface(group.SurfaceId);

                    if (survivingGroup is not null && survivingGroup.IsVisible)
                    {
                        IDesktopWidgetWindow replacement =
                            await ShowGroupActiveWindowAsync(
                                survivingGroup,
                                raiseTransitionWindows) ??
                            throw new InvalidOperationException(
                                "The surviving group host could not be created.");
                        created.Add((survivingGroup.ActiveMemberId, replacement));
                        await WaitForGroupReplacementFirstFrameAsync(replacement);
                    }
                    else if (survivingGroup is null && group.IsVisible)
                    {
                        foreach (string remainingId in group.MemberIds)
                        {
                            if (FindConfig(remainingId) is { IsVisible: true } remainingConfig)
                            {
                                IDesktopWidgetWindow replacement =
                                    await ShowStandaloneWindowAsync(
                                        remainingConfig,
                                        raiseTransitionWindows);
                                created.Add((remainingId, replacement));
                                await WaitForGroupReplacementFirstFrameAsync(replacement);
                            }
                        }
                    }

                    if (removedConfig.IsVisible)
                    {
                        IDesktopWidgetWindow replacement =
                            await ShowStandaloneWindowAsync(
                                removedConfig,
                                raiseTransitionWindows);
                        created.Add((removedConfig.Id, replacement));
                        await WaitForGroupReplacementFirstFrameAsync(replacement);
                    }
                }
                catch (Exception replacementError)
                {
                    try
                    {
                        await RecoverFailedGroupReplacementAsync(
                            group,
                            rollbackSnapshot,
                            committedSnapshot,
                            created,
                            originalHost,
                            raiseTransitionWindows,
                            "detach",
                            replacementError);
                    }
                    catch (Exception recoveryError)
                    {
                        App.Log(
                            $"[WidgetGroup] Detach recovery failed " +
                            $"group={group.Id}: {recoveryError}");
                    }
                    return false;
                }
            }

            if (survivingGroup is null)
            {
                _widgetSurfaceSwitchGates.Remove(group.SurfaceId);
            }

            App.Log(
                $"[WidgetGroup] Removed member={widgetId} group={group.Id} " +
                $"remaining={group.MemberIds.Count} reveal={revealStandalone} " +
                $"reusedSurface={reusedSurface} " +
                $"persistMs={persistenceElapsedMs:F1} " +
                $"totalMs={detachCommitStopwatch.Elapsed.TotalMilliseconds:F1}");
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return true;
        }
        finally
        {
            _widgetGroupGate.Release();
        }
    }

    public async Task<bool> DissolveWidgetGroupContainingAsync(string widgetId)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => DissolveWidgetGroupContainingAsync(widgetId));
        }

        // Hand-edited settings can leave a blank SurfaceId; the switch-gate
        // pool rejects those, so repair the topology before any lookup.
        WidgetGroupSettings.Normalize(_settingsService.Settings);
        WidgetGroupConfig? pendingGroup = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (pendingGroup is not null)
        {
            _widgetGroupSwitchRequests.Cancel(pendingGroup.SurfaceId);
        }

        await _widgetGroupGate.WaitAsync();
        try
        {
            WidgetGroupSettings.Normalize(_settingsService.Settings);
            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                widgetId);
            if (group is null)
            {
                return false;
            }

            using IDisposable surfaceSwitch =
                await _widgetSurfaceSwitchGates.AcquireManyAsync(
                    [group.SurfaceId]);

            bool preserveRaisedLayer =
                ShouldPreserveRaisedWidgetLayer(group.ActiveMemberId);
            IDesktopWidgetWindow? originalHost =
                GetLoadedWindow(group.ActiveMemberId);

            List<WidgetConfig> members = group.MemberIds
                .Select(FindConfig)
                .Where(config => config is not null)
                .Cast<WidgetConfig>()
                .ToList();
            WidgetGroupMutationSnapshot rollbackSnapshot =
                WidgetGroupMutationSnapshot.Capture(this, group);
            for (int index = 0; index < members.Count; index++)
            {
                ApplyGroupLayoutToMember(group, members[index]);
                if (index > 0)
                {
                    PlaceDetachedMember(members[index], group, index);
                }
                members[index].IsVisible = group.IsVisible;
            }

            _settingsService.Settings.WidgetGroups.Remove(group);
            bool persisted;
            try
            {
                persisted = await _settingsService.SaveCheckedAsync();
            }
            catch
            {
                rollbackSnapshot.Restore(this);
                throw;
            }

            if (!persisted)
            {
                rollbackSnapshot.Restore(this);
                App.Log(
                    $"[WidgetGroup] Rolled back dissolve because settings save failed " +
                    $"group={group.Id}");
                RaiseWidgetGroupsChanged();
                ApplyCapsuleArrangementIfChanged(force: true);
                return false;
            }

            WidgetGroupMutationSnapshot committedSnapshot =
                WidgetGroupMutationSnapshot.Capture(this, group);

            var created = new List<(
                string MemberId,
                IDesktopWidgetWindow Host)>();
            try
            {
                foreach (WidgetConfig member in members)
                {
                    RetireLoadedWindowForGroup(
                        member.Id,
                        keepConfigVisible: member.IsVisible);
                }
                _widgetSurfaces.RemoveSurface(group.SurfaceId);
                foreach (WidgetConfig member in members.Where(member => member.IsVisible))
                {
                    IDesktopWidgetWindow replacement =
                        await ShowStandaloneWindowAsync(member);
                    created.Add((member.Id, replacement));
                    await WaitForGroupReplacementFirstFrameAsync(replacement);
                }
            }
            catch (Exception replacementError)
            {
                try
                {
                    await RecoverFailedGroupReplacementAsync(
                        group,
                        rollbackSnapshot,
                        committedSnapshot,
                        created,
                        originalHost,
                        preserveRaisedLayer,
                        "dissolve",
                        replacementError);
                }
                catch (Exception recoveryError)
                {
                    App.Log(
                        $"[WidgetGroup] Dissolve recovery failed " +
                        $"group={group.Id}: {recoveryError}");
                }
                return false;
            }

            _widgetSurfaceSwitchGates.Remove(group.SurfaceId);

            if (preserveRaisedLayer)
            {
                RaiseVisibleWidgetTransitionWindows(
                    members.Select(member => member.Id),
                    "group-dissolve");
            }

            App.Log($"[WidgetGroup] Dissolved group={group.Id} members={members.Count}");
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return true;
        }
        finally
        {
            _widgetGroupGate.Release();
        }
    }

    public async Task<bool> ReorderWidgetGroupMemberAsync(
        string sourceWidgetId,
        string targetWidgetId,
        string? expectedGroupId = null,
        IReadOnlyList<string>? expectedMemberIds = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(
                () => ReorderWidgetGroupMemberAsync(
                    sourceWidgetId, targetWidgetId, expectedGroupId, expectedMemberIds));
        }

        await _widgetGroupGate.WaitAsync();
        try
        {
            WidgetGroupSettings.Normalize(_settingsService.Settings);
            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                sourceWidgetId);
            if (group is null ||
                !group.MemberIds.Contains(targetWidgetId, StringComparer.Ordinal) ||
                string.Equals(sourceWidgetId, targetWidgetId, StringComparison.Ordinal) ||
                (expectedGroupId is not null && group.Id != expectedGroupId) ||
                (expectedMemberIds is not null &&
                 !group.MemberIds.SequenceEqual(expectedMemberIds, StringComparer.Ordinal)))
            {
                return false;
            }

            using IDisposable surfaceSwitch =
                await _widgetSurfaceSwitchGates.AcquireManyAsync(
                    [group.SurfaceId]);

            // Move the source into the target's original slot. This makes
            // adjacent keyboard/menu moves symmetric in both directions and
            // gives drag/drop a stable, deterministic destination.
            try
            {
                return await WidgetGroupOrder.MoveAndSaveAsync(
                    group.MemberIds,
                    sourceWidgetId,
                    targetWidgetId,
                    () => _settingsService.SaveCheckedAsync());
            }
            finally
            {
                RaiseWidgetGroupsChanged();
            }
        }
        finally
        {
            _widgetGroupGate.Release();
        }
    }

    public void SynchronizeGroupLayoutFromMember(WidgetConfig member)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            member.Id);
        if (group is null ||
            !string.Equals(group.ActiveMemberId, member.Id, StringComparison.Ordinal))
        {
            return;
        }

        CaptureGroupLayout(group, member);
        ApplyGroupLayoutToMembers(group);
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    public bool SetWidgetGroupChromeMode(
        WidgetConfig member,
        WidgetChromeMode mode)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            member.Id);
        if (group is null)
        {
            return false;
        }

        WidgetGroupChromeDecision decision =
            WidgetGroupChromePolicy.EvaluateGroupMode(mode);
        if (!decision.IsAllowed)
        {
            App.Log(
                $"[WidgetGroup] Rejected group chrome group={group.Id} " +
                $"mode={mode} reason={decision.RejectionReason}");
            return false;
        }

        group.ChromeMode = WidgetChromeModeNames.ToSettingValue(mode);
        _settingsService.SaveDebounced(notifySubscribers: false);
        RaiseWidgetGroupsChanged();
        return true;
    }

    public void SetWidgetGroupCollapseBehavior(
        WidgetConfig member,
        WidgetCollapseBehavior behavior)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            member.Id);
        if (group is null)
        {
            return;
        }

        group.CollapseBehavior = WidgetCollapseBehaviorNames.ToSettingValue(behavior);
        foreach (string memberId in group.MemberIds)
        {
            if (FindConfig(memberId) is { } config)
            {
                WidgetCollapseBehaviorNames.SetOverride(config, behavior);
            }
        }
        _settingsService.SaveDebounced(notifySubscribers: false);
        RaiseWidgetGroupsChanged();
    }

    public bool SetWidgetGroupVisibility(
        WidgetConfig member,
        bool isVisible,
        bool persist = true)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            member.Id);
        if (group is null ||
            !string.Equals(group.ActiveMemberId, member.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (!isVisible)
        {
            _widgetGroupSwitchRequests.Cancel(group.SurfaceId);
        }

        group.IsVisible = isVisible;
        foreach (string memberId in group.MemberIds)
        {
            if (FindConfig(memberId) is { } config)
            {
                config.IsVisible = isVisible;
            }
        }
        if (persist)
        {
            _settingsService.SaveDebounced(notifySubscribers: false);
        }

        return true;
    }

    public void UpdateWidgetGroupDragPreview(string sourceWidgetId)
    {
        if (!IsWidgetGroupingEnabled)
        {
            ClearGroupDragPreview();
            return;
        }

        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            ClearGroupDragPreview();
            return;
        }

        _groupDragSourceId = sourceWidgetId;
        WidgetGroupDragCandidate? candidate = FindWidgetGroupDragCandidateAtPoint(
            sourceWidgetId,
            cursor.X,
            cursor.Y);
        string? targetId = candidate?.Window.Config.Id;

        if (string.Equals(targetId, _groupDragTargetId, StringComparison.Ordinal))
        {
            return;
        }

        ClearGroupDragTargetPreview();
        if (targetId is null)
        {
            return;
        }

        _groupDragSourceId = sourceWidgetId;
        _groupDragTargetId = targetId;
        _groupDragDropReady = false;
        WidgetGroupJoinTarget targetRule = candidate!.Value.Rule;
        if (targetRule is { CanJoin: false })
        {
            GetLoadedWindow(targetId)?.SetGroupDropPreview(
                visible: true,
                ready: false,
                targetRule.RejectionReasonKey ??
                    "Widget.Group.RequiresVisibleTitle");
            return;
        }

        GetLoadedWindow(targetId)?.SetGroupDropPreview(visible: true, ready: false);

        _groupDragDwellTimer ??= CreateGroupDragDwellTimer();
        _groupDragDwellTimer.Stop();
        _groupDragDwellTimer.Start();
    }

    public async Task<bool> CompleteWidgetGroupDragAsync(string sourceWidgetId)
    {
        if (!IsWidgetGroupingEnabled)
        {
            ClearGroupDragPreview();
            return false;
        }

        string? targetId = string.Equals(
            _groupDragSourceId,
            sourceWidgetId,
            StringComparison.Ordinal) &&
            _groupDragDropReady &&
            IsGroupDragTargetUnderCursor()
            ? _groupDragTargetId
            : null;
        ClearGroupDragPreview();
        return targetId is not null &&
               await MergeWidgetsAsync(sourceWidgetId, targetId);
    }

    public void CancelWidgetGroupDrag(string sourceWidgetId)
    {
        if (string.Equals(_groupDragSourceId, sourceWidgetId, StringComparison.Ordinal))
        {
            ClearGroupDragPreview();
        }
    }

    private WidgetGroupConfig CreateGroupFromTarget(
        WidgetConfig target,
        WidgetChromeMode groupMode)
    {
        var group = new WidgetGroupConfig
        {
            SurfaceId = Guid.NewGuid().ToString(),
            Name = target.Name,
            ActiveMemberId = target.Id,
            MemberIds = [target.Id],
            NavigationStyle = WidgetGroupNavigationStyles.FollowDefault,
            TitleDisplayMode = WidgetGroupTitleDisplayModes.FollowDefault,
            WheelSwitchEnabled = null,
            HoverSwitchEnabled = null,
            IsVisible = target.IsVisible,
            ChromeMode = WidgetChromeModeNames.ToSettingValue(
                groupMode),
            CollapseBehavior = WidgetCollapseBehaviorNames.ToSettingValue(
                WidgetCollapseBehavior.System)
        };
        CaptureGroupLayout(group, target);
        return group;
    }

    private static WidgetGroupConfig CloneWidgetGroupConfig(
        WidgetGroupConfig source)
    {
        return new WidgetGroupConfig
        {
            Id = source.Id,
            SurfaceId = source.SurfaceId,
            Name = source.Name,
            MemberIds = source.MemberIds.ToList(),
            ActiveMemberId = source.ActiveMemberId,
            IsVisible = source.IsVisible,
            X = source.X,
            Y = source.Y,
            PositionAnchor = source.PositionAnchor,
            PositionMarginX = source.PositionMarginX,
            PositionMarginY = source.PositionMarginY,
            PositionMonitorKey = source.PositionMonitorKey,
            PositionMonitorDeviceName = source.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = source.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = source.BoundsCoordinateVersion,
            Width = source.Width,
            Height = source.Height,
            IsPositionLocked = source.IsPositionLocked,
            IsSizeLocked = source.IsSizeLocked,
            IsCollapsed = source.IsCollapsed,
            CompactPlacement = CloneCompactPlacement(source.CompactPlacement),
            CompactWidth = source.CompactWidth,
            NavigationStyle = source.NavigationStyle,
            TitleDisplayMode = source.TitleDisplayMode,
            WheelSwitchEnabled = source.WheelSwitchEnabled,
            HoverSwitchEnabled = source.HoverSwitchEnabled,
            ChromeMode = source.ChromeMode,
            CollapseBehavior = source.CollapseBehavior
        };
    }

    private static void CaptureGroupLayout(WidgetGroupConfig group, WidgetConfig member)
    {
        group.X = member.X;
        group.Y = member.Y;
        group.PositionAnchor = member.PositionAnchor;
        group.PositionMarginX = member.PositionMarginX;
        group.PositionMarginY = member.PositionMarginY;
        group.PositionMonitorKey = member.PositionMonitorKey;
        group.PositionMonitorDeviceName = member.PositionMonitorDeviceName;
        group.PositionMonitorWasPrimary = member.PositionMonitorWasPrimary;
        group.BoundsCoordinateVersion = member.BoundsCoordinateVersion;
        group.Width = member.Width;
        group.Height = member.Height;
        group.IsPositionLocked = member.IsPositionLocked;
        group.IsSizeLocked = member.IsSizeLocked;
        group.IsCollapsed = member.IsCollapsed;
        group.CompactPlacement = CloneCompactPlacement(member.CompactPlacement);
        group.CompactWidth = member.CompactWidth;
    }

    private void ApplyGroupLayoutToMembers(WidgetGroupConfig group)
    {
        foreach (string memberId in group.MemberIds)
        {
            if (FindConfig(memberId) is { } member)
            {
                ApplyGroupLayoutToMember(group, member);
            }
        }
    }

    private static void ApplyGroupLayoutToMember(WidgetGroupConfig group, WidgetConfig member)
    {
        member.X = group.X;
        member.Y = group.Y;
        member.PositionAnchor = group.PositionAnchor;
        member.PositionMarginX = group.PositionMarginX;
        member.PositionMarginY = group.PositionMarginY;
        member.PositionMonitorKey = group.PositionMonitorKey;
        member.PositionMonitorDeviceName = group.PositionMonitorDeviceName;
        member.PositionMonitorWasPrimary = group.PositionMonitorWasPrimary;
        member.BoundsCoordinateVersion = group.BoundsCoordinateVersion;
        member.Width = group.Width;
        member.Height = group.Height;
        member.IsPositionLocked = group.IsPositionLocked;
        member.IsSizeLocked = group.IsSizeLocked;
        member.IsCollapsed = group.IsCollapsed;
        member.CompactPlacement = CloneCompactPlacement(group.CompactPlacement);
        member.CompactWidth = group.CompactWidth;
        member.IsVisible = group.IsVisible;

        WidgetCollapseBehaviorNames.SetOverride(
            member,
            WidgetCollapseBehaviorNames.Normalize(
                group.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true));
    }

    private static WidgetCompactPlacement? CloneCompactPlacement(WidgetCompactPlacement? source)
    {
        return source is null
            ? null
            : new WidgetCompactPlacement
            {
                X = source.X,
                Y = source.Y,
                PositionAnchor = source.PositionAnchor,
                PositionMarginX = source.PositionMarginX,
                PositionMarginY = source.PositionMarginY,
                PositionMonitorKey = source.PositionMonitorKey,
                PositionMonitorDeviceName = source.PositionMonitorDeviceName,
                PositionMonitorWasPrimary = source.PositionMonitorWasPrimary,
                BoundsCoordinateVersion = source.BoundsCoordinateVersion
            };
    }

    private static void PlaceDetachedMember(
        WidgetConfig member,
        WidgetGroupConfig group,
        int cascadeIndex,
        PointInt32? detachedPosition = null)
    {
        if (detachedPosition is { } position)
        {
            member.X = position.X;
            member.Y = position.Y;
        }
        else
        {
            double offset = Math.Clamp(cascadeIndex, 1, 5) * 24;
            member.X = group.X + offset;
            member.Y = group.Y + offset;
        }
        member.PositionAnchor = null;
        member.PositionMarginX = 0;
        member.PositionMarginY = 0;
        member.PositionMonitorKey = null;
        member.PositionMonitorDeviceName = null;
        member.PositionMonitorWasPrimary = null;
        member.CompactPlacement = null;
    }

    private async Task<WidgetGroupDetachSurfaceReuseResult>
        TryCompleteDetachedActiveSurfaceReuseAsync(
            WidgetGroupConfig originalGroup,
            WidgetGroupConfig? survivingGroup,
            WidgetConfig removedConfig,
            ContentWidgetWindow? detachedHost,
            WidgetGroupMutationSnapshot rollbackSnapshot,
            WidgetGroupMutationSnapshot committedSnapshot,
            bool showRaised)
    {
        if (detachedHost is null ||
            detachedHost.WindowHandle == IntPtr.Zero ||
            !detachedHost.Visible ||
            !_widgetSurfaces.TryGet(originalGroup.SurfaceId, out var session) ||
            session is null ||
            !ReferenceEquals(session.Host, detachedHost))
        {
            return WidgetGroupDetachSurfaceReuseResult.NotApplicable;
        }

        IDesktopWidgetWindow? replacementHost = null;
        try
        {
            // The visible group HWND already contains the member being dragged.
            // Give that physical Surface the member's new standalone identity,
            // and create only the remaining side of the split.
            _widgetSurfaces.RemoveSurface(originalGroup.SurfaceId);
            _widgetSurfaces.RegisterActive(
                CreateSurfaceDefinition(removedConfig),
                detachedHost);
            RegisterStandaloneUnifiedFileSessionIfNeeded(
                removedConfig,
                detachedHost,
                detachedHost.CurrentContent);

            if (survivingGroup is { IsVisible: true })
            {
                WidgetGroupFailureProbe.ThrowIfRequested(
                    "reused-detach-create");
                replacementHost = await ShowGroupActiveWindowAsync(
                    survivingGroup,
                    showRaised) ??
                    throw new InvalidOperationException(
                        "The surviving widget-group surface could not be created.");
            }
            else if (survivingGroup is null && originalGroup.IsVisible)
            {
                string remainingId = originalGroup.MemberIds.FirstOrDefault() ??
                    throw new InvalidOperationException(
                        "A detached group must retain at least one member.");
                WidgetConfig remainingConfig = FindConfig(remainingId) ??
                    throw new InvalidOperationException(
                        $"The remaining widget '{remainingId}' is unavailable.");
                if (remainingConfig.IsVisible)
                {
                    WidgetGroupFailureProbe.ThrowIfRequested(
                        "reused-detach-create");
                    replacementHost = await ShowStandaloneWindowAsync(
                        remainingConfig,
                        showRaised);
                }
            }

            if (replacementHost is ContentWidgetWindow replacementContent)
            {
                WidgetGroupFailureProbe.ThrowIfRequested(
                    "reused-detach-first-frame");
                using var frameTimeout = new CancellationTokenSource(
                    WidgetGroupFirstFrameTimeout);
                await replacementContent.WaitForFirstPresentedFrameAsync(
                    frameTimeout.Token);
            }

            if (removedConfig.IsVisible)
            {
                // Keep the old group visible until its replacement has a frame,
                // then cloak, move and reveal the same HWND at the drop target.
                if (!detachedHost.PrepareTrayShowAnimationForCurrentTopology())
                {
                    throw new InvalidOperationException(
                        "The detached widget Surface could not be moved to its standalone bounds.");
                }
                if (_widgetsRaisedFromTray || showRaised)
                {
                    detachedHost.ShowPreparedRaisedFromTray(
                        persistVisibility: false);
                }
                else
                {
                    detachedHost.ShowPreparedAtDesktopLayer(
                        persistVisibility: false);
                }
                detachedHost.CompleteTrayShowWithoutAnimation();
                if (showRaised && !_widgetsRaisedFromTray)
                {
                    detachedHost.AdoptManagerRaisedStateAfterPreparedShow();
                }
            }
            else
            {
                detachedHost.HideWindow();
            }

            App.Log(
                $"[WidgetGroup] Reused active Surface for detach " +
                $"group={originalGroup.Id} member={removedConfig.Id} " +
                $"detachedHwnd=0x{detachedHost.WindowHandle.ToInt64():X} " +
                $"replacementHwnd=0x{replacementHost?.WindowHandle.ToInt64() ?? 0:X}");
            return WidgetGroupDetachSurfaceReuseResult.Completed;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Detach Surface reuse failed; rolling back " +
                $"group={originalGroup.Id} member={removedConfig.Id}: {ex}");

            bool rollbackSaved = false;
            try
            {
                rollbackSaved = await WidgetGroupPersistedTopologyRecovery
                    .TryRestorePreviousAsync(
                        () => rollbackSnapshot.Restore(this),
                        () => WidgetGroupFailureProbe.Consume(
                            "reused-detach-rollback-save")
                            ? Task.FromResult(false)
                            : _settingsService.SaveCheckedAsync(
                                notifySubscribers: false),
                        () => committedSnapshot.Restore(this));
            }
            catch (Exception rollbackSaveException)
            {
                App.Log(
                    $"[WidgetGroup] Detach Surface rollback save failed " +
                    $"group={originalGroup.Id}: {rollbackSaveException}");
            }

            if (!rollbackSaved)
            {
                await ReconcileCommittedDetachedSurfaceAsync(
                    originalGroup,
                    survivingGroup,
                    removedConfig,
                    detachedHost,
                    replacementHost,
                    showRaised);
                return WidgetGroupDetachSurfaceReuseResult.Failed;
            }

            if (replacementHost is not null &&
                !ReferenceEquals(replacementHost, detachedHost))
            {
                try
                {
                    bool restoredVisibility = replacementHost.Config.IsVisible;
                    RetireSpecificLoadedWindowForGroup(
                        replacementHost.Config.Id,
                        replacementHost,
                        keepConfigVisible: restoredVisibility);
                }
                catch (Exception retirementError)
                {
                    App.Log(
                        $"[WidgetGroup] Detach replacement retirement failed " +
                        $"group={originalGroup.Id}: {retirementError}");
                    try
                    {
                        CloseFailedGroupReplacement(
                            replacementHost.Config.Id,
                            replacementHost);
                    }
                    catch (Exception cleanupError)
                    {
                        App.Log(
                            $"[WidgetGroup] Detach replacement cleanup failed " +
                            $"group={originalGroup.Id}: {cleanupError}");
                    }
                }
                _widgetSurfaces.UnregisterHost(replacementHost);
            }

            _widgetSurfaces.UnregisterHost(detachedHost);
            CommitSurfaceHost(originalGroup, detachedHost);
            bool rollbackBoundsRestored =
                detachedHost.PrepareTrayShowAnimationForCurrentTopology();
            if (_widgetsRaisedFromTray || showRaised)
            {
                detachedHost.ShowPreparedRaisedFromTray(
                    persistVisibility: false);
            }
            else
            {
                detachedHost.ShowPreparedAtDesktopLayer(
                    persistVisibility: false);
            }
            detachedHost.CompleteTrayShowWithoutAnimation();
            if (showRaised && !_widgetsRaisedFromTray)
            {
                detachedHost.AdoptManagerRaisedStateAfterPreparedShow();
            }

            App.Log(
                $"[WidgetGroup] Detach Surface rollback completed " +
                $"group={originalGroup.Id} saved={rollbackSaved} " +
                $"boundsRestored={rollbackBoundsRestored}");
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return WidgetGroupDetachSurfaceReuseResult.Failed;
        }
    }

    private async Task ReconcileCommittedDetachedSurfaceAsync(
        WidgetGroupConfig originalGroup,
        WidgetGroupConfig? survivingGroup,
        WidgetConfig removedConfig,
        ContentWidgetWindow detachedHost,
        IDesktopWidgetWindow? replacementHost,
        bool showRaised)
    {
        App.Log(
            $"[WidgetGroup] Detach rollback was not durable; keeping the " +
            $"saved split group={originalGroup.Id} member={removedConfig.Id}");

        try
        {
            WidgetGroupFailureProbe.ThrowIfRequested("detach-reconcile-registry");
            if (_widgetSurfaces.TryGet(originalGroup.SurfaceId, out var original) &&
                ReferenceEquals(original!.Host, detachedHost))
            {
                _widgetSurfaces.RemoveSurface(originalGroup.SurfaceId);
            }

            _widgetSurfaces.RegisterActive(
                CreateSurfaceDefinition(removedConfig),
                detachedHost);
            RegisterStandaloneUnifiedFileSessionIfNeeded(
                removedConfig,
                detachedHost,
                detachedHost.CurrentContent);
        }
        catch (Exception registryError)
        {
            App.Log(
                $"[WidgetGroup] Saved detach Surface reconciliation failed " +
                $"group={originalGroup.Id}: {registryError}");
            // The saved split must not leave declarations that make every
            // later group notification throw; quarantine the stale claims
            // and retry the standalone declaration once.
            if (WidgetGroupPersistedTopologyRecovery
                .QuarantineCommittedDetachClaims(
                    _widgetSurfaces,
                    originalGroup.SurfaceId,
                    removedConfig.Id,
                    detachedHost,
                    CreateSurfaceDefinition(removedConfig),
                    App.Log))
            {
                RegisterStandaloneUnifiedFileSessionIfNeeded(
                    removedConfig,
                    detachedHost,
                    detachedHost.CurrentContent);
            }
        }

        try
        {
            if (survivingGroup is { IsVisible: true })
            {
                replacementHost ??= await ShowGroupActiveWindowAsync(
                    survivingGroup,
                    showRaised) ??
                    throw new InvalidOperationException(
                        "The saved surviving group host could not be created.");
            }
            else if (survivingGroup is null && originalGroup.IsVisible)
            {
                string? remainingId = originalGroup.MemberIds.FirstOrDefault();
                if (remainingId is not null &&
                    FindConfig(remainingId) is { IsVisible: true } remainingConfig)
                {
                    replacementHost ??= await ShowStandaloneWindowAsync(
                        remainingConfig,
                        showRaised);
                }
            }

            if (replacementHost is not null)
            {
                await WaitForGroupReplacementFirstFrameAsync(replacementHost);
            }
        }
        catch (Exception replacementError)
        {
            App.Log(
                $"[WidgetGroup] Saved detach replacement is still unavailable " +
                $"group={originalGroup.Id}: {replacementError}");
        }

        try
        {
            if (removedConfig.IsVisible)
            {
                bool boundsRestored =
                    detachedHost.PrepareTrayShowAnimationForCurrentTopology();
                if (_widgetsRaisedFromTray || showRaised)
                {
                    detachedHost.ShowPreparedRaisedFromTray(
                        persistVisibility: false);
                }
                else
                {
                    detachedHost.ShowPreparedAtDesktopLayer(
                        persistVisibility: false);
                }
                detachedHost.CompleteTrayShowWithoutAnimation();
                if (showRaised && !_widgetsRaisedFromTray)
                {
                    detachedHost.AdoptManagerRaisedStateAfterPreparedShow();
                }
                App.LogVerbose(
                    $"[WidgetGroup] Saved detached HWND restored to standalone " +
                    $"boundsRestored={boundsRestored} member={removedConfig.Id}");
            }
            else
            {
                detachedHost.HideWindow();
            }
        }
        catch (Exception presentationError)
        {
            App.Log(
                $"[WidgetGroup] Saved detached HWND presentation failed " +
                $"group={originalGroup.Id}: {presentationError}");
        }

        try
        {
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
        }
        catch (Exception reconcileError)
        {
            App.Log(
                $"[WidgetGroup] Saved detach notification failed " +
                $"group={originalGroup.Id}: {reconcileError}");
        }
    }

    private async Task<IDesktopWidgetWindow?> ShowGroupActiveWindowAsync(
        WidgetGroupConfig group,
        bool showRaised = false)
    {
        if (FindConfig(group.ActiveMemberId) is not { } config)
        {
            return null;
        }

        ApplyGroupLayoutToMember(group, config);
        return await ShowStandaloneWindowAsync(config, showRaised);
    }

    private async Task<IDesktopWidgetWindow> ShowStandaloneWindowAsync(
        WidgetConfig config,
        bool showRaised = false)
    {
        IDesktopWidgetWindow? existingBeforeCreate =
            GetLegacyLoadedWindow(config.Id);
        IDesktopWidgetWindow window = await CreateRegisteredWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: true);
        try
        {
            if (_widgetsRaisedFromTray || showRaised)
            {
                window.ShowPreparedRaisedFromTray(persistVisibility: false);
            }
            else
            {
                window.ShowPreparedAtDesktopLayer(persistVisibility: false);
            }
            window.CompleteTrayShowWithoutAnimation();
            if (showRaised &&
                !_widgetsRaisedFromTray &&
                window is WidgetWindowBase widgetWindow)
            {
                widgetWindow.AdoptManagerRaisedStateAfterPreparedShow();
            }
            return window;
        }
        catch
        {
            if (!ReferenceEquals(window, existingBeforeCreate))
            {
                CloseFailedGroupReplacement(config.Id, window);
            }
            throw;
        }
    }

    private async Task WaitForGroupReplacementFirstFrameAsync(
        IDesktopWidgetWindow window)
    {
        if (window is not ContentWidgetWindow content)
        {
            return;
        }
        if (!window.Visible)
        {
            throw new InvalidOperationException(
                "A replacement content Surface did not become visible.");
        }

        using var frameTimeout = new CancellationTokenSource(
            WidgetGroupFirstFrameTimeout);
        await content.WaitForFirstPresentedFrameAsync(frameTimeout.Token);
    }

    private void CloseFailedGroupReplacement(
        string widgetId,
        IDesktopWidgetWindow window)
    {
        try
        {
            if (window is ContentWidgetWindow content)
            {
                _contentWindowRegistration.Unregister(content);
            }
            RemoveFileWidgetSessionsForHost(window);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Failed to unregister replacement content " +
                $"widget={widgetId}: {ex}");
        }
        try
        {
            UnregisterSurfaceHost(window);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Failed to unregister replacement " +
                $"widget={widgetId}: {ex}");
        }

        try
        {
            Win32Helper.ShowWindow(window.WindowHandle, Win32Helper.SW_HIDE);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Failed to hide replacement " +
                $"widget={widgetId}: {ex}");
        }
        CloseFailedCreatedWindow(widgetId, window, preserveVisibility: true);
    }

    private async Task RecoverFailedGroupReplacementAsync(
        WidgetGroupConfig originalGroup,
        WidgetGroupMutationSnapshot previous,
        WidgetGroupMutationSnapshot committed,
        IReadOnlyList<(string MemberId, IDesktopWidgetWindow Host)> created,
        IDesktopWidgetWindow? originalHost,
        bool showRaised,
        string operation,
        Exception failure)
    {
        App.Log(
            $"[WidgetGroup] {operation} replacement failed after save; " +
            $"attempting durable rollback group={originalGroup.Id}: {failure}");

        bool rollbackSaved;
        try
        {
            rollbackSaved = await WidgetGroupPersistedTopologyRecovery
                .TryRestorePreviousAsync(
                    () => previous.Restore(this),
                    () => _settingsService.SaveCheckedAsync(
                        notifySubscribers: false),
                    () => committed.Restore(this));
        }
        catch (Exception rollbackError)
        {
            App.Log(
                $"[WidgetGroup] {operation} rollback save failed; " +
                $"retaining committed topology group={originalGroup.Id}: " +
                rollbackError);
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return;
        }

        if (!rollbackSaved)
        {
            App.Log(
                $"[WidgetGroup] {operation} rollback save was rejected; " +
                $"retaining committed topology group={originalGroup.Id}");
            RaiseWidgetGroupsChanged();
            ApplyCapsuleArrangementIfChanged(force: true);
            return;
        }

        var closed = new HashSet<IDesktopWidgetWindow>(
            ReferenceEqualityComparer.Instance);
        foreach ((string memberId, IDesktopWidgetWindow host) in created)
        {
            if (!ReferenceEquals(host, originalHost) && closed.Add(host))
            {
                CloseFailedGroupReplacement(memberId, host);
            }
        }

        bool originalStillActive = originalHost is not null &&
            originalHost.Visible &&
            _widgetSurfaces.TryGet(originalGroup.SurfaceId, out var active) &&
            ReferenceEquals(active!.Host, originalHost) &&
            ReferenceEquals(
                GetLegacyLoadedWindow(originalGroup.ActiveMemberId),
                originalHost);
        if (!originalStillActive)
        {
            if (originalHost is not null && closed.Add(originalHost))
            {
                CloseFailedGroupReplacement(
                    originalGroup.ActiveMemberId,
                    originalHost);
            }
            _widgetSurfaces.RemoveSurface(originalGroup.SurfaceId);

            if (originalGroup.IsVisible)
            {
                try
                {
                    IDesktopWidgetWindow restored =
                        await ShowGroupActiveWindowAsync(
                            originalGroup,
                            showRaised) ??
                        throw new InvalidOperationException(
                            "The original group host could not be recreated.");
                    await WaitForGroupReplacementFirstFrameAsync(restored);
                }
                catch (Exception restoreError)
                {
                    App.Log(
                        $"[WidgetGroup] {operation} settings were restored, " +
                        $"but the original host could not be shown " +
                        $"group={originalGroup.Id}: {restoreError}");
                }
            }
        }

        RaiseWidgetGroupsChanged();
        ApplyCapsuleArrangementIfChanged(force: true);
    }

    private enum WidgetGroupDetachSurfaceReuseResult
    {
        NotApplicable,
        Completed,
        Failed
    }

    private IDesktopWidgetWindow? GetLoadedWindow(string widgetId)
    {
        if (_widgetSurfaces.TryGetByMember(widgetId, out var surface) &&
            string.Equals(
                surface!.ActiveMemberId,
                widgetId,
                StringComparison.Ordinal))
        {
            return surface.Host;
        }

        return GetLegacyLoadedWindow(widgetId);
    }

    private void RetireLoadedWindowForGroup(string widgetId, bool keepConfigVisible)
    {
        IDesktopWidgetWindow? window = GetLoadedWindow(widgetId);
        if (window is null)
        {
            return;
        }

        RetireSpecificLoadedWindowForGroup(
            widgetId,
            window,
            keepConfigVisible);
    }

    private void RetireSpecificLoadedWindowForGroup(
        string widgetId,
        IDesktopWidgetWindow window,
        bool keepConfigVisible)
    {
        IWidgetTransientStateContent? transientStateSource =
            window as IWidgetTransientStateContent ??
            (window as ContentWidgetWindow)?.CurrentContent
                as IWidgetTransientStateContent;
        try
        {
            CaptureWidgetGroupTransientState(widgetId, transientStateSource);
        }
        catch (Exception ex)
        {
            // Capturing optional view state must not abort retirement after
            // the new group topology has already been persisted.
            App.Log(
                $"[WidgetGroup] Transient state capture failed during " +
                $"retirement id={widgetId}: {ex}");
        }

        _suppressClosedVisibilityPersistence.Add(widgetId);
        try
        {
            RemoveFileWidgetSessionsForHost(window);
            if (window is ContentWidgetWindow content &&
                _contentWindowRegistration.Unregister(content).Count > 0)
            {
                try
                {
                    (content.CurrentContent as IDisposable)?.Dispose();
                }
                catch
                {
                }
            }

            window.Config.IsVisible = keepConfigVisible;
            try
            {
                Win32Helper.ShowWindow(window.WindowHandle, Win32Helper.SW_HIDE);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Hide failed id={widgetId}: {ex}");
            }
            try
            {
                window.CloseWindow();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Close failed id={widgetId}: {ex}");
            }
        }
        finally
        {
            _suppressClosedVisibilityPersistence.Remove(widgetId);
        }
    }

    private void RaiseWidgetGroupsChanged()
    {
        SynchronizeLoadedSurfaceDefinitions();
        WidgetGroupsChanged?.Invoke();
    }

    private void NormalizeWidgetGroupsForRuntime()
    {
        bool changed = WidgetGroupSettings.Normalize(_settingsService.Settings);
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups.ToList())
        {
            string previousActiveId = group.ActiveMemberId;
            List<string> unavailableMemberIds = group.MemberIds
                .Where(memberId =>
                    FindConfig(memberId) is not { } config ||
                    !_widgetRegistry.IsAvailableForSession(
                        config,
                        _settingsService.Settings))
                .ToList();

            // Session availability is transient (for example a feature may
            // still be warming up, or a mapped location may be unavailable).
            // It must never mutate the persisted group topology. Removing a
            // member here can dissolve a valid group during startup and make
            // it appear as if the user's group was deleted. Keep the member
            // ids and let RestoreWidgetsAsync create whichever member is
            // currently available; the next lifecycle pass can restore the
            // original active member without data loss.
            if (unavailableMemberIds.Count > 0)
            {
                App.Log(
                    $"[WidgetGroup] Preserving unavailable members during runtime normalization " +
                    $"group={group.Id} members={string.Join(',', unavailableMemberIds)}");
            }

            string? restorableActiveId = WidgetGroupSettings.ResolveRestorableActiveMemberId(
                _settingsService.Settings,
                group,
                config =>
                    !IsDeleted(config.Id) &&
                    !config.IsDisabled &&
                    _widgetRegistry.IsAvailableForSession(
                        config,
                        _settingsService.Settings));
            List<string> availableMemberIds = group.MemberIds
                .Where(memberId => string.Equals(memberId, restorableActiveId, StringComparison.Ordinal) ||
                    FindConfig(memberId) is { } config &&
                    !IsDeleted(config.Id) &&
                    !config.IsDisabled &&
                    _widgetRegistry.IsAvailableForSession(
                        config,
                        _settingsService.Settings))
                .ToList();

            if (!group.MemberIds.Contains(group.ActiveMemberId, StringComparer.Ordinal) ||
                (restorableActiveId is not null &&
                 !string.Equals(group.ActiveMemberId, restorableActiveId, StringComparison.Ordinal)))
            {
                group.ActiveMemberId = restorableActiveId ??
                    availableMemberIds.FirstOrDefault() ??
                    group.MemberIds.FirstOrDefault() ??
                    string.Empty;
                if (!string.IsNullOrWhiteSpace(group.ActiveMemberId))
                {
                    TransferCapsuleIdentity(previousActiveId, group.ActiveMemberId);
                }
                changed = true;
            }

            // WidgetGroupSettings.Normalize has already removed truly missing
            // or deleted members. A group with fewer than two persisted
            // members is therefore a real invalid group and may be dissolved;
            // a temporarily unavailable member must not reach this branch.
            if (group.MemberIds.Count < 2)
            {
                _settingsService.Settings.WidgetGroups.Remove(group);
                _widgetSurfaceSwitchGates.Remove(group.SurfaceId);
                if (group.MemberIds.FirstOrDefault() is { } remainingId &&
                    FindConfig(remainingId) is { } remainingConfig)
                {
                    ApplyGroupLayoutToMember(group, remainingConfig);
                    remainingConfig.IsVisible = group.IsVisible;
                }
                changed = true;
                continue;
            }

            ApplyGroupLayoutToMembers(group);
            if (availableMemberIds.Count > 0)
            {
                changed |= NormalizeCapsuleIdentityForGroup(group);
            }
        }

        if (changed)
        {
            _settingsService.SaveDebounced(notifySubscribers: false);
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateGroupDragDwellTimer()
    {
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(360);
        timer.Tick += (_, _) =>
        {
            if (_groupDragTargetId is not { } targetId ||
                !IsGroupDragTargetUnderCursor())
            {
                ClearGroupDragPreview();
                return;
            }

            _groupDragDropReady = true;
            GetLoadedWindow(targetId)?.SetGroupDropPreview(visible: true, ready: true);
        };
        return timer;
    }

    private bool IsGroupDragTargetUnderCursor()
    {
        return _groupDragTargetId is { } targetId &&
               Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) &&
               GetLoadedWindow(targetId) is { Visible: true } targetWindow &&
               WidgetGroupDropHitTestPolicy.Contains(
                   targetWindow.GetGroupMergeTitleScreenBounds(),
                   cursor.X,
                   cursor.Y);
    }

    private void ClearGroupDragPreview()
    {
        ClearGroupDragTargetPreview();
        _groupDragSourceId = null;
        ClearWidgetGroupDragCandidateCache();
    }

    private void ClearGroupDragTargetPreview()
    {
        _groupDragDwellTimer?.Stop();
        if (_groupDragTargetId is not null)
        {
            GetLoadedWindow(_groupDragTargetId)?.SetGroupDropPreview(visible: false, ready: false);
        }
        _groupDragTargetId = null;
        _groupDragDropReady = false;
    }

    private void CaptureWidgetGroupTransientState(
        string widgetId,
        IWidgetTransientStateContent? transientStateSource)
    {
        object? opaqueState = transientStateSource?.CaptureTransientState();
        WidgetGroupTransientState? state = opaqueState is null
            ? null
            : new WidgetGroupTransientState(opaqueState);

        if (state is null || state.IsEmpty)
        {
            _widgetGroupTransientStates.Remove(widgetId);
            return;
        }

        _widgetGroupTransientStates[widgetId] = state;
    }

    private void RestoreWidgetGroupTransientState(string widgetId)
    {
        if (!_widgetGroupTransientStates.Remove(
                widgetId,
                out WidgetGroupTransientState? state))
        {
            return;
        }

        IWidgetTransientStateContent? transientStateTarget =
            _contentWidgets.TryGetValue(widgetId, out var contentWindow)
                ? contentWindow.CurrentContent as IWidgetTransientStateContent
                : _fileWidgets.TryGetValue(widgetId, out var file)
                    ? file.Content
                    : null;
        if (transientStateTarget is not null)
        {
            transientStateTarget.RestoreTransientState(
                state.OpaqueContentState);
        }
    }

    private void PreviewWidgetGroupTransientState(
        string widgetId,
        IWidgetTransientStateContent? transientStateTarget)
    {
        if (transientStateTarget is null ||
            !_widgetGroupTransientStates.TryGetValue(
                widgetId,
                out WidgetGroupTransientState? state))
        {
            return;
        }

        // The switch calls this both before initialization and again after it.
        // That keeps the first data projection and the staged XAML tree on the
        // same member-specific state. The state remains cached until commit,
        // so cancellation or rollback can safely retry it later.
        transientStateTarget.RestoreTransientState(state.OpaqueContentState);
    }

    private void ClearWidgetGroupTransientState(string widgetId)
    {
        _widgetGroupTransientStates.Remove(widgetId);
        _widgetGroupMemberInactiveSince.Remove(widgetId);
    }

    private bool NormalizeCapsuleIdentityForGroup(WidgetGroupConfig group)
    {
        if (group.MemberIds.Count == 0 ||
            !group.MemberIds.Contains(group.ActiveMemberId, StringComparer.Ordinal))
        {
            return false;
        }

        bool changed = false;
        AppSettings settings = _settingsService.Settings;
        var memberIds = group.MemberIds.ToHashSet(StringComparer.Ordinal);

        int preferredIndex = settings.WidgetCapsuleBarOrder.FindIndex(id =>
            string.Equals(id, group.ActiveMemberId, StringComparison.Ordinal));
        int firstMemberIndex = settings.WidgetCapsuleBarOrder.FindIndex(memberIds.Contains);
        int insertionIndex = preferredIndex >= 0
            ? preferredIndex - settings.WidgetCapsuleBarOrder
                .Take(preferredIndex)
                .Count(memberIds.Contains)
            : firstMemberIndex;
        int removedCount = settings.WidgetCapsuleBarOrder.RemoveAll(memberIds.Contains);
        if (removedCount > 0)
        {
            insertionIndex = Math.Clamp(insertionIndex, 0, settings.WidgetCapsuleBarOrder.Count);
            settings.WidgetCapsuleBarOrder.Insert(insertionIndex, group.ActiveMemberId);
            changed = removedCount != 1 || preferredIndex < 0;
        }

        WidgetCompactPlacement? freePlacement = null;
        if (settings.WidgetCapsuleFreePlacements.TryGetValue(
                group.ActiveMemberId,
                out WidgetCompactPlacement? activePlacement))
        {
            freePlacement = CloneCompactPlacement(activePlacement);
        }
        else
        {
            foreach (string memberId in group.MemberIds)
            {
                if (settings.WidgetCapsuleFreePlacements.TryGetValue(
                        memberId,
                        out WidgetCompactPlacement? memberPlacement))
                {
                    freePlacement = CloneCompactPlacement(memberPlacement);
                    break;
                }
            }
        }

        bool activeHadFreePlacement =
            settings.WidgetCapsuleFreePlacements.ContainsKey(group.ActiveMemberId);
        int existingPlacementCount = group.MemberIds.Count(
            settings.WidgetCapsuleFreePlacements.ContainsKey);
        foreach (string memberId in group.MemberIds)
        {
            settings.WidgetCapsuleFreePlacements.Remove(memberId);
        }
        if (freePlacement is not null)
        {
            settings.WidgetCapsuleFreePlacements[group.ActiveMemberId] = freePlacement;
        }
        changed |= existingPlacementCount > 1 ||
                   (existingPlacementCount == 1 && !activeHadFreePlacement);

        return changed;
    }

    private void TransferCapsuleIdentity(string previousWidgetId, string targetWidgetId)
    {
        if (string.Equals(previousWidgetId, targetWidgetId, StringComparison.Ordinal))
        {
            return;
        }

        AppSettings settings = _settingsService.Settings;
        int previousIndex = settings.WidgetCapsuleBarOrder.FindIndex(id =>
            string.Equals(id, previousWidgetId, StringComparison.Ordinal));
        settings.WidgetCapsuleBarOrder.RemoveAll(id =>
            string.Equals(id, targetWidgetId, StringComparison.Ordinal));
        if (previousIndex >= 0)
        {
            previousIndex = Math.Clamp(previousIndex, 0, settings.WidgetCapsuleBarOrder.Count - 1);
            settings.WidgetCapsuleBarOrder[previousIndex] = targetWidgetId;
        }

        if (settings.WidgetCapsuleFreePlacements.Remove(
                previousWidgetId,
                out WidgetCompactPlacement? placement))
        {
            settings.WidgetCapsuleFreePlacements[targetWidgetId] =
                CloneCompactPlacement(placement)!;
        }

        if (_lastCapsuleBarBounds.Remove(previousWidgetId, out RectInt32 bounds))
        {
            _lastCapsuleBarBounds[targetWidgetId] = bounds;
        }
    }

    /// <summary>
    /// Captures the in-memory state touched by detach/dissolve before the
    /// settings file is written.  Those operations retire windows only after a
    /// successful save, so restoring these values is sufficient to make a
    /// failed persistence attempt transactionally invisible to the UI.
    /// </summary>
    private sealed class WidgetGroupMutationSnapshot
    {
        private readonly WidgetGroupConfig _group;
        private readonly int _groupIndex;
        private readonly List<string> _memberIds;
        private readonly string _activeMemberId;
        private readonly List<MemberState> _members;
        private readonly List<string> _capsuleBarOrder;
        private readonly Dictionary<string, WidgetCompactPlacement> _freePlacements;
        private readonly Dictionary<string, RectInt32> _capsuleBounds;
        private readonly Dictionary<string, long> _inactiveSince;

        private WidgetGroupMutationSnapshot(
            WidgetGroupConfig group,
            int groupIndex,
            List<MemberState> members,
            List<string> capsuleBarOrder,
            Dictionary<string, WidgetCompactPlacement> freePlacements,
            Dictionary<string, RectInt32> capsuleBounds,
            Dictionary<string, long> inactiveSince)
        {
            _group = group;
            _groupIndex = groupIndex;
            _memberIds = group.MemberIds.ToList();
            _activeMemberId = group.ActiveMemberId;
            _members = members;
            _capsuleBarOrder = capsuleBarOrder;
            _freePlacements = freePlacements;
            _capsuleBounds = capsuleBounds;
            _inactiveSince = inactiveSince;
        }

        public static WidgetGroupMutationSnapshot Capture(
            WidgetManager manager,
            WidgetGroupConfig group,
            IEnumerable<string>? additionalMemberIds = null)
        {
            AppSettings settings = manager._settingsService.Settings;
            var members = group.MemberIds
                .Concat(additionalMemberIds ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .Select(manager.FindConfig)
                .Where(config => config is not null)
                .Select(config => new MemberState(config!))
                .ToList();

            return new WidgetGroupMutationSnapshot(
                group,
                settings.WidgetGroups.IndexOf(group),
                members,
                settings.WidgetCapsuleBarOrder.ToList(),
                settings.WidgetCapsuleFreePlacements.ToDictionary(
                    entry => entry.Key,
                    entry => CloneCompactPlacement(entry.Value)!,
                    StringComparer.Ordinal),
                manager._lastCapsuleBarBounds.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal),
                manager._widgetGroupMemberInactiveSince.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal));
        }

        public void Restore(WidgetManager manager)
        {
            AppSettings settings = manager._settingsService.Settings;
            settings.WidgetGroups.RemoveAll(group => ReferenceEquals(group, _group));
            if (_groupIndex >= 0)
            {
                int insertIndex = Math.Clamp(
                    _groupIndex,
                    0,
                    settings.WidgetGroups.Count);
                settings.WidgetGroups.Insert(insertIndex, _group);
            }

            _group.MemberIds.Clear();
            _group.MemberIds.AddRange(_memberIds);
            _group.ActiveMemberId = _activeMemberId;

            foreach (MemberState member in _members)
            {
                member.Restore();
            }

            settings.WidgetCapsuleBarOrder.Clear();
            settings.WidgetCapsuleBarOrder.AddRange(_capsuleBarOrder);
            settings.WidgetCapsuleFreePlacements.Clear();
            foreach (var placement in _freePlacements)
            {
                settings.WidgetCapsuleFreePlacements[placement.Key] =
                    CloneCompactPlacement(placement.Value)!;
            }

            manager._lastCapsuleBarBounds.Clear();
            foreach (var bounds in _capsuleBounds)
            {
                manager._lastCapsuleBarBounds[bounds.Key] = bounds.Value;
            }

            manager._widgetGroupMemberInactiveSince.Clear();
            foreach (var entry in _inactiveSince)
            {
                manager._widgetGroupMemberInactiveSince[entry.Key] = entry.Value;
            }
        }

        private sealed class MemberState
        {
            private readonly WidgetConfig _config;
            private readonly double _x;
            private readonly double _y;
            private readonly string? _positionAnchor;
            private readonly double _positionMarginX;
            private readonly double _positionMarginY;
            private readonly string? _positionMonitorKey;
            private readonly string? _positionMonitorDeviceName;
            private readonly bool? _positionMonitorWasPrimary;
            private readonly int _boundsCoordinateVersion;
            private readonly double _width;
            private readonly double _height;
            private readonly bool _isVisible;
            private readonly bool _isPositionLocked;
            private readonly bool _isSizeLocked;
            private readonly bool _isCollapsed;
            private readonly WidgetCompactPlacement? _compactPlacement;
            private readonly double? _compactWidth;
            private readonly Dictionary<string, string> _metadata;

            public MemberState(WidgetConfig config)
            {
                _config = config;
                _x = config.X;
                _y = config.Y;
                _positionAnchor = config.PositionAnchor;
                _positionMarginX = config.PositionMarginX;
                _positionMarginY = config.PositionMarginY;
                _positionMonitorKey = config.PositionMonitorKey;
                _positionMonitorDeviceName = config.PositionMonitorDeviceName;
                _positionMonitorWasPrimary = config.PositionMonitorWasPrimary;
                _boundsCoordinateVersion = config.BoundsCoordinateVersion;
                _width = config.Width;
                _height = config.Height;
                _isVisible = config.IsVisible;
                _isPositionLocked = config.IsPositionLocked;
                _isSizeLocked = config.IsSizeLocked;
                _isCollapsed = config.IsCollapsed;
                _compactPlacement = CloneCompactPlacement(config.CompactPlacement);
                _compactWidth = config.CompactWidth;
                _metadata = new Dictionary<string, string>(
                    config.Metadata,
                    StringComparer.Ordinal);
            }

            public void Restore()
            {
                _config.X = _x;
                _config.Y = _y;
                _config.PositionAnchor = _positionAnchor;
                _config.PositionMarginX = _positionMarginX;
                _config.PositionMarginY = _positionMarginY;
                _config.PositionMonitorKey = _positionMonitorKey;
                _config.PositionMonitorDeviceName = _positionMonitorDeviceName;
                _config.PositionMonitorWasPrimary = _positionMonitorWasPrimary;
                _config.BoundsCoordinateVersion = _boundsCoordinateVersion;
                _config.Width = _width;
                _config.Height = _height;
                _config.IsVisible = _isVisible;
                _config.IsPositionLocked = _isPositionLocked;
                _config.IsSizeLocked = _isSizeLocked;
                _config.IsCollapsed = _isCollapsed;
                _config.CompactPlacement = CloneCompactPlacement(_compactPlacement);
                _config.CompactWidth = _compactWidth;
                _config.Metadata.Clear();
                foreach (var metadata in _metadata)
                {
                    _config.Metadata[metadata.Key] = metadata.Value;
                }
            }
        }
    }

    private sealed record WidgetGroupTransientState(
        object OpaqueContentState)
    {
        public bool IsEmpty => false;
    }
}
