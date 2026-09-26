using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Views;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    // Both factories are immutable after construction (the provider map is
    // static and every delegate captures stable services), so one instance
    // serves every surface/group switch instead of being rebuilt per call.
    private ContentWidgetWindowFactory? _surfaceContentWindowFactory;

    /// <summary>
    /// Creates content plans for every member kind already migrated off a
    /// top-level, type-specific host. Quick Capture is injected here because
    /// its store is process-wide and owned by WidgetManager/App services.
    /// </summary>
    private ContentWidgetWindowFactory CreateSurfaceContentWindowFactory()
    {
        if (_surfaceContentWindowFactory is not null)
        {
            return _surfaceContentWindowFactory;
        }

        _surfaceContentWindowFactory = new ContentWidgetWindowFactory(
            new WidgetContentFactory(_localizationService),
            _settingsService,
            quickCaptureContentFactory: CreateQuickCaptureSurfaceContent,
            fileContentFactory: CreateFileSurfaceContent);
        return _surfaceContentWindowFactory;
    }

    private IWidgetContent CreateQuickCaptureSurfaceContent(WidgetConfig config)
    {
        return new QuickCaptureWidgetContentAdapter(
            config,
            _quickCaptureService,
            _settingsService,
            _localizationService,
            DispatcherQueue.GetForCurrentThread());
    }

    private IWidgetContent CreateFileSurfaceContent(WidgetConfig config)
    {
        return new FileSurfaceContent(
            config,
            _fileService,
            _organizerService,
            _settingsService,
            _localizationService,
            DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>
    /// A topology change may start from a loaded standalone legacy window.
    /// Promote it once, while the group is being established, so every later
    /// member switch is a content transaction on the same Surface HWND.
    /// </summary>
    private async Task PromoteGroupToUnifiedSurfaceHostAsync(
        WidgetGroupConfig group,
        Func<Task>? beforeRetireAsync = null,
        bool preserveRaisedLayer = false,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>>?
            expectedRetiringClaims = null)
    {
        IDesktopWidgetWindow? loaded = GetLoadedWindow(group.ActiveMemberId);
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>>
            retiringSurfaceClaims = expectedRetiringClaims ??
                (loaded is null
                    ? Array.Empty<WidgetSurfaceClaimTransfer<IDesktopWidgetWindow>>()
                    : _widgetSurfaces.CaptureGroupClaimTransfers(
                        CreateSurfaceDefinition(group),
                        onlyRetireHost: loaded));
        if (loaded is ContentWidgetWindow contentWindow)
        {
            if (beforeRetireAsync is not null)
            {
                await beforeRetireAsync();
            }

            CommitSurfaceHost(
                group,
                contentWindow,
                retiringSurfaceClaims);
            return;
        }

        if (loaded is null && !group.IsVisible)
        {
            if (beforeRetireAsync is not null)
            {
                await beforeRetireAsync();
            }
            return;
        }

        WidgetConfig? config = FindConfig(group.ActiveMemberId);
        if (config is null)
        {
            throw new InvalidOperationException(
                $"The active member '{group.ActiveMemberId}' has no widget configuration.");
        }

        bool showCandidateRaised = preserveRaisedLayer ||
                                   ShouldPreserveRaisedWidgetLayer(
                                       group.ActiveMemberId);

        // CreateContentWidgetFromConfigAsync registers the new Surface host,
        // so retiring by member id afterwards could resolve to the new window;
        // retain and retire the exact legacy instance in the commit callback.
        ContentWidgetWindow unifiedHost =
            await WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () => CreateContentWidgetFromConfigAsync(
                    config,
                    keepPreparedForAnimation: true,
                    prepareSurfacePromotionCandidate: true),
                presentCandidateAsync: async candidate =>
                {
                    if (!group.IsVisible)
                    {
                        return;
                    }

                    if (showCandidateRaised)
                    {
                        candidate.ShowPreparedRaisedFromTray(
                            persistVisibility: false);
                    }
                    else
                    {
                        candidate.ShowPreparedAtDesktopLayer(
                            persistVisibility: false);
                    }

                    candidate.CompleteTrayShowWithoutAnimation();
                    if (showCandidateRaised && !_widgetsRaisedFromTray)
                    {
                        candidate.RaiseTemporarilyFromManager();
                    }

                    using var frameTimeout = new CancellationTokenSource(
                        WidgetGroupFirstFrameTimeout);
                    await candidate.WaitForFirstPresentedFrameAsync(
                        frameTimeout.Token);
                },
                commitAndRetireLegacyAsync: async candidate =>
                {
                    if (beforeRetireAsync is not null)
                    {
                        await beforeRetireAsync();
                    }
                    CommitSurfaceHost(
                        group,
                        candidate,
                        retiringSurfaceClaims);
                    if (loaded is not null)
                    {
                        try
                        {
                            RetireSpecificLoadedWindowForGroup(
                                group.ActiveMemberId,
                                loaded,
                                keepConfigVisible: group.IsVisible);
                        }
                        catch (Exception retirementError)
                        {
                            // The new host is already committed. A failure to
                            // retire the old HWND must not roll back and close
                            // the committed candidate.
                            App.Log(
                                $"[WidgetSurface] Legacy host retirement " +
                                $"failed after commit surface={group.SurfaceId}: " +
                                retirementError);
                            try
                            {
                                CloseFailedGroupReplacement(
                                    group.ActiveMemberId,
                                    loaded);
                            }
                            catch (Exception cleanupError)
                            {
                                App.Log(
                                    $"[WidgetSurface] Legacy host cleanup " +
                                    $"failed surface={group.SurfaceId}: " +
                                    cleanupError);
                            }
                        }
                    }
                },
                rollbackCandidate: candidate =>
                {
                    _contentWindowRegistration.Unregister(candidate);
                    RemoveFileWidgetSessionsForHost(candidate);
                    UnregisterSurfaceHost(candidate);
                    CloseFailedCreatedWindow(
                        config.Id,
                        candidate,
                        preserveVisibility: true);
                });

        App.Log(
            $"[WidgetSurface] Promoted group={group.Id} " +
            $"surface={group.SurfaceId} member={group.ActiveMemberId}");
    }
}
