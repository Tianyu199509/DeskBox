using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private readonly WidgetSurfaceRegistry<IDesktopWidgetWindow> _widgetSurfaces =
        new();

    internal int LoadedSurfaceCount => _widgetSurfaces.Count;

    private WidgetSurfaceDefinition CreateSurfaceDefinition(
        WidgetConfig config,
        string? activeMemberId = null)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);
        if (group is null)
        {
            return new WidgetSurfaceDefinition(
                config.Id,
                GroupId: null,
                [config.Id],
                config.Id);
        }

        return CreateSurfaceDefinition(
            group,
            activeMemberId ?? group.ActiveMemberId);
    }

    private static WidgetSurfaceDefinition CreateSurfaceDefinition(
        WidgetGroupConfig group,
        string? activeMemberId = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new WidgetSurfaceDefinition(
            group.SurfaceId,
            group.Id,
            group.MemberIds.ToArray(),
            activeMemberId ?? group.ActiveMemberId);
    }

    private void RegisterCreatedSurfaceHost(
        WidgetConfig config,
        IDesktopWidgetWindow window,
        bool prepareSurfacePromotionCandidate = false)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);
        WidgetSurfaceDefinition definition = group is null
            ? CreateSurfaceDefinition(config)
            : CreateSurfaceDefinition(group);

        if (prepareSurfacePromotionCandidate)
        {
            if (group is null ||
                !string.Equals(
                    definition.ActiveMemberId,
                    config.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Widget '{config.Id}' is not the active member of a group Surface.");
            }

            // A newly formed group has no active Surface declaration yet. Keep
            // the legacy member's claim until the candidate is presented and
            // CommitSurfaceHost transfers it to the group identity.
            if (_widgetSurfaces.TryGet(definition.SurfaceId, out _) &&
                !_widgetSurfaces.StageCandidate(
                    definition.SurfaceId,
                    config.Id,
                    window))
            {
                throw new InvalidOperationException(
                    $"Unable to stage promotion candidate for Surface '{definition.SurfaceId}'.");
            }
            return;
        }

        if (group is not null &&
            !string.Equals(
                definition.ActiveMemberId,
                config.Id,
                StringComparison.Ordinal))
        {
            EnsureSurfaceSession(group);
            if (!_widgetSurfaces.StageCandidate(
                    definition.SurfaceId,
                    config.Id,
                    window))
            {
                throw new InvalidOperationException(
                    $"Unable to stage candidate for Surface '{definition.SurfaceId}' " +
                    $"member '{config.Id}'.");
            }
            return;
        }

        if (_widgetSurfaces.TryGet(definition.SurfaceId, out var existing) &&
            !ReferenceEquals(existing!.Host, window))
        {
            if (group is not null)
            {
                throw new InvalidOperationException(
                    $"Surface '{definition.SurfaceId}' already owns another host; " +
                    "replacement requires a promotion transaction.");
            }
            _widgetSurfaces.SynchronizeActive(definition, window);
        }
        else
        {
            _widgetSurfaces.RegisterActive(definition, window);
        }
    }

    private WidgetSurfaceSession<IDesktopWidgetWindow>? EnsureSurfaceSession(
        WidgetGroupConfig group)
    {
        if (_widgetSurfaces.TryGet(group.SurfaceId, out var existing))
        {
            _widgetSurfaces.UpdateDefinition(CreateSurfaceDefinition(group));
            return existing;
        }

        IDesktopWidgetWindow? activeHost =
            GetLegacyLoadedWindow(group.ActiveMemberId);
        if (activeHost is null)
        {
            return null;
        }

        WidgetSurfaceDefinition definition = CreateSurfaceDefinition(group);
        IReadOnlyList<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>> retiring =
            _widgetSurfaces.CaptureGroupClaimTransfers(
                definition,
                onlyRetireHost: activeHost);
        return _widgetSurfaces.RegisterActive(
            definition,
            activeHost,
            expectedRetiringClaims: retiring);
    }

    private SemaphoreSlim GetWidgetSurfaceSwitchGate(WidgetGroupConfig group)
    {
        return _widgetSurfaceSwitchGates.Get(group.SurfaceId);
    }

    private void CommitSurfaceHost(
        WidgetGroupConfig group,
        IDesktopWidgetWindow window,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>>?
            expectedRetiringClaims = null)
    {
        WidgetSurfaceSession<IDesktopWidgetWindow> session =
            _widgetSurfaces.CommitActive(
                CreateSurfaceDefinition(group),
                window,
                expectedRetiringClaims);
        // Standalone file sessions are aliases for a single member. Once the
        // HWND becomes a persistent group surface, content switching owns the
        // active member and the standalone alias must not outlive that change.
        RemoveFileWidgetSessionsForHost(window);
        App.LogVerbose(
            $"[WidgetSurface] Commit surface={session.SurfaceId} " +
            $"member={session.ActiveMemberId} " +
            $"hwnd=0x{session.Host.WindowHandle.ToInt64():X}");
    }

    private void LogWidgetSurfaceEvidence(
        WidgetGroupConfig group,
        string phase)
    {
        if (!_widgetSurfaces.TryGet(group.SurfaceId, out var session) ||
            session is null)
        {
            App.Log(
                $"[WidgetSurfaceEvidence] phase={phase} " +
                $"surface={group.SurfaceId} registryEntries=0");
            return;
        }

        int hostAliases = _contentWidgets.Count(entry =>
            ReferenceEquals(entry.Value, session.Host));
        int liveMembers = session.Host is ContentWidgetWindow content
            ? content.LiveMemberCount
            : 1;
        bool hasPresentableFrame =
            session.Host is not ContentWidgetWindow contentHost ||
            contentHost.HasPresentableContentFrame;
        App.Log(
            $"[WidgetSurfaceEvidence] phase={phase} " +
            $"surface={session.SurfaceId} active={session.ActiveMemberId} " +
            $"hwnd=0x{session.Host.WindowHandle.ToInt64():X} " +
            $"registryEntries=1 hostAliases={hostAliases} " +
            $"liveMembers={liveMembers} presentable={hasPresentableFrame}");
    }

    private void UnregisterSurfaceHost(IDesktopWidgetWindow window)
    {
        _temporaryRaiseLease = WidgetTemporaryRaiseLeasePolicy.Forget(
            _temporaryRaiseLease,
            window.WindowHandle);
        _widgetSurfaces.UnregisterHost(window);
        if (App.UiDispatcherQueue?.TryEnqueue(() =>
                ReconcileBackgroundMemoryCleanupForWidgetVisibility(
                    "surface-unregistered")) != true)
        {
            ReconcileBackgroundMemoryCleanupForWidgetVisibility(
                "surface-unregistered");
        }
    }

    private void SynchronizeLoadedSurfaceDefinitions()
    {
        foreach (WidgetGroupConfig group in
                 _settingsService.Settings.WidgetGroups)
        {
            SynchronizeGroupSurfaceHost(group);
        }
    }

    private void SynchronizeGroupSurfaceHost(WidgetGroupConfig group)
    {
        IDesktopWidgetWindow? activeHost =
            GetLegacyLoadedWindow(group.ActiveMemberId);
        if (activeHost is null)
        {
            return;
        }

        WidgetSurfaceDefinition definition = CreateSurfaceDefinition(group);
        if (_widgetSurfaces.TryGet(group.SurfaceId, out var existing) &&
            ReferenceEquals(existing!.Host, activeHost))
        {
            _widgetSurfaces.UpdateDefinition(definition);
            return;
        }

        // Reconcile a standalone claim and the group declaration in one
        // registry operation. A conflicting source leaves both claims intact.
        IReadOnlyList<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>> retiring =
            _widgetSurfaces.CaptureGroupClaimTransfers(
                definition,
                onlyRetireHost: activeHost);
        _widgetSurfaces.SynchronizeActive(
            definition,
            activeHost,
            expectedRetiringClaims: retiring);
    }

    private IDesktopWidgetWindow? GetLegacyLoadedWindow(string widgetId)
    {
        if (_fileWidgets.TryGetValue(widgetId, out var file))
        {
            return file.Host;
        }

        return _contentWidgets.TryGetValue(widgetId, out var content)
            ? content
            : null;
    }

    private void CancelAllWidgetSurfaceSwitches()
    {
        _widgetGroupSwitchRequests.CancelAll();
    }

    internal void CancelWidgetSurfaceSwitch(string widgetId)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            _widgetGroupSwitchRequests.Cancel(group.SurfaceId);
        }
    }
}
