using System.Reflection;
using System.Text.RegularExpressions;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Tests;

/// <summary>
/// Anti-mud architecture ratchets complementing <see cref="ModuleBoundaryContractTests"/>
/// and <see cref="TestSuiteHygieneContractTests"/>:
/// 1. Prevent <c>App.Current</c> / <c>IServiceProvider</c> service-locator spread in Views and Controls.
/// 2. Freeze flat top-level growth in <c>src/DeskBox/Services/</c> (new files belong in vertical
///    <c>Features/&lt;Domain&gt;/</c> or domain subfolders <c>Services/&lt;Domain&gt;/</c>).
/// 3. Prevent hardcoded domain <see cref="DispatcherQueueTimer"/> / <c>*Snapshot</c> bloat on
///    <see cref="CustomWidgetViewModel"/>.
/// 4. Cap imperative UI element fields on <see cref="CustomWidgetStudioWindow"/> and line counts on
///    mega <c>.cs</c> files so large features are decomposed instead of piled into god classes.
/// </summary>
public sealed class ArchitectureAntiMudGuardrailTests
{
    // Exact violation manifest for Views/ and Controls/: file -> call-site count.
    // Entries may only shrink or disappear; new Views/Controls must stay at 0.
    private static readonly IReadOnlyDictionary<string, int> ViewsAndControlsGlobalAccessExpected =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/DeskBox/Controls/DesktopOrganizationPreviewCard.xaml.cs"] = 8,
            ["src/DeskBox/Controls/DesktopOrganizationTaskView.Actions.cs"] = 7,
            ["src/DeskBox/Controls/DesktopOrganizationTaskView.Appearance.cs"] = 8,
            ["src/DeskBox/Controls/DesktopOrganizationTaskView.Presentation.cs"] = 3,
            ["src/DeskBox/Controls/DesktopOrganizationTaskView.Retained.cs"] = 1,
            ["src/DeskBox/Controls/DesktopOrganizationTaskView.xaml.cs"] = 6,
            ["src/DeskBox/Controls/WidgetBackgroundCustomizer.xaml.cs"] = 3,
            ["src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml.cs"] = 10,
            ["src/DeskBox/Controls/WidgetContents/ClipboardWidgetContentAdapter.cs"] = 4,
            ["src/DeskBox/Controls/WidgetContents/CustomWidgetContent.xaml.cs"] = 6,
            ["src/DeskBox/Controls/WidgetContents/CustomWidgetContentAdapter.cs"] = 2,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ElevatedPointerDrag.cs"] = 4,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"] = 5,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"] = 3,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"] = 10,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopoverRename.cs"] = 3,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"] = 17,
            ["src/DeskBox/Controls/WidgetContents/FileWidgetContentAdapter.cs"] = 2,
            ["src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.Faces.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"] = 5,
            ["src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.BatchAndExport.cs"] = 9,
            ["src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Attachments.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ClipboardSelection.cs"] = 5,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailLifecycle.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs"] = 13,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"] = 4,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Export.cs"] = 4,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ListInteraction.cs"] = 1,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Menus.cs"] = 15,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"] = 13,
            ["src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs"] = 4,
            ["src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml.cs"] = 4,
            ["src/DeskBox/Controls/WidgetFeedbackPresenter.xaml.cs"] = 1,
            ["src/DeskBox/Controls/WidgetGroupTitleSwitcher.Interaction.cs"] = 1,
            ["src/DeskBox/Controls/WidgetGroupTitleSwitcher.xaml.cs"] = 1,
            ["src/DeskBox/Controls/WidgetIconCustomizer.xaml.cs"] = 4,
            ["src/DeskBox/Controls/WidgetShell.xaml.cs"] = 5,
            ["src/DeskBox/Controls/WidgetTitleIcon.xaml.cs"] = 1,
            ["src/DeskBox/Views/ClipboardQuickPasteWindow.xaml.cs"] = 2,
            ["src/DeskBox/Views/ContentWidgetWindow.Commands.cs"] = 60,
            ["src/DeskBox/Views/ContentWidgetWindow.ElevatedPointerDrag.cs"] = 1,
            ["src/DeskBox/Views/ContentWidgetWindow.File.cs"] = 2,
            ["src/DeskBox/Views/ContentWidgetWindow.GroupNavigation.cs"] = 1,
            ["src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"] = 18,
            ["src/DeskBox/Views/ContentWidgetWindow.QuickCapture.cs"] = 3,
            ["src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"] = 19,
            ["src/DeskBox/Views/ContentWidgetWindow.xaml.cs"] = 12,
            ["src/DeskBox/Views/DesktopOrganizationWindow.xaml.cs"] = 5,
            ["src/DeskBox/Views/DesktopToastHudWindow.cs"] = 2,
            ["src/DeskBox/Views/DisplayIdentifyOverlayWindow.cs"] = 2,
            ["src/DeskBox/Views/ElevationNoticeWindow.xaml.cs"] = 1,
            ["src/DeskBox/Views/OnboardingWindow.Steps.cs"] = 8,
            ["src/DeskBox/Views/OnboardingWindow.xaml.cs"] = 2,
            ["src/DeskBox/Views/SearchPopupWindow.xaml.cs"] = 8,
            ["src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs"] = 17,
            ["src/DeskBox/Views/SettingsSections/DisplaySettingsSection.xaml.cs"] = 10,
            ["src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"] = 4,
            ["src/DeskBox/Views/SettingsWindow.xaml.cs"] = 3,
            ["src/DeskBox/Views/StackPopoverHostWindow.cs"] = 1,
            ["src/DeskBox/Views/TrayContextMenuWindow.cs"] = 2,
            ["src/DeskBox/Views/WidgetEditorPopoverWindow.cs"] = 7,
            ["src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"] = 1,
            ["src/DeskBox/Views/WidgetWindowBase.Bounds.cs"] = 11,
            ["src/DeskBox/Views/WidgetWindowBase.Collapse.cs"] = 25,
            ["src/DeskBox/Views/WidgetWindowBase.cs"] = 2,
            ["src/DeskBox/Views/WidgetWindowBase.Foreground.cs"] = 1,
            ["src/DeskBox/Views/WidgetWindowBase.Grouping.cs"] = 14,
            ["src/DeskBox/Views/WidgetWindowBase.Interaction.cs"] = 33,
            ["src/DeskBox/Views/WidgetWindowBase.TitleIconCustomization.cs"] = 4,
        };

    // Frozen inventory of top-level .cs files in src/DeskBox/Services/.
    // Files may be deleted or moved into Features/<Domain>/ or Services/<Domain>/,
    // never added flat at Services/ root.
    private static readonly HashSet<string> LegacyTopLevelServiceFiles = new(StringComparer.Ordinal)
    {
        "ActiveDeskBoxDragRegistry.cs",
        "AppDiagnosticsService.cs",
        "AppDistributionChannel.cs",
        "AppDistributionService.cs",
        "AppearanceSettingsCoordinator.cs",
        "AppExecutablePath.cs",
        "AppLifecycleRecoverySignalClassifier.cs",
        "AppLifecycleRecoveryWatcher.cs",
        "AppRelaunchService.cs",
        "AppUpdateDownloadProgressThrottle.cs",
        "AppUpdateService.cs",
        "AppUpdateServiceFactory.cs",
        "ArchiveZipService.cs",
        "AttachmentStorageService.cs",
        "AutoStartDefaultPolicy.cs",
        "BackgroundUpdateCheckPolicy.cs",
        "BackupBackend.cs",
        "BackupRestoreActions.cs",
        "BackupSettingsCoordinator.cs",
        "BeddingLeakConfirmationPolicy.cs",
        "BoundedPathChangeBuffer.cs",
        "CapsuleOverrideResetter.cs",
        "CapsuleSettingsCoordinator.cs",
        "CitySearchService.cs",
        "ClipboardClassifier.cs",
        "ClipboardHistoryService.cs",
        "ClipboardHotkeyService.cs",
        "ClipboardOcrService.cs",
        "ClipboardStore.cs",
        "ClipboardTextTransformService.cs",
        "ClipboardWidgetContentProvider.cs",
        "CloudBackupDomains.cs",
        "CloudBackupService.cs",
        "CloudBackupSettingsPolicy.cs",
        "CompositorClockBoostCoordinator.cs",
        "ContentWidgetWindowFactory.cs",
        "ContentWidgetWindowPool.cs",
        "ContentWindowRegistration.cs",
        "DataBackupSettingsPolicy.cs",
        "DeskBoxAttachmentHealthService.cs",
        "DeskBoxClipboardWriteScope.cs",
        "DeskBoxDataBackupService.cs",
        "DeskBoxDataPathService.cs",
        "DeskBoxDiagnosticsBundleService.cs",
        "DeskBoxDragData.cs",
        "DeskBoxProtocolActivationService.cs",
        "DesktopAutoOrganizationActivityTracker.cs",
        "DesktopAutoOrganizationBaseline.cs",
        "DesktopAutoOrganizationPolicy.cs",
        "DesktopAutoOrganizationStateMachine.cs",
        "DesktopAutoOrganizationSuppressionRegistry.cs",
        "DesktopAutoOrganizationWatcher.cs",
        "DesktopBlankHitTest.cs",
        "DesktopDoubleClickActivationService.cs",
        "DesktopDoubleClickStateMachine.cs",
        "DesktopLayerBeddingPolicy.cs",
        "DesktopOrganizationClassifier.cs",
        "DesktopOrganizationCoordinator.cs",
        "DesktopOrganizationHistoryStore.cs",
        "DesktopOrganizationPlacementPlanner.cs",
        "DesktopOrganizationPlanner.cs",
        "DesktopOrganizationPreviewReconciliation.cs",
        "DesktopOrganizationPreviewSummary.cs",
        "DesktopOrganizationRecoveryStore.cs",
        "DesktopOrganizationRuleResolver.cs",
        "DesktopOrganizationScanner.cs",
        "DesktopOrganizationTargetAvailability.cs",
        "DesktopOrganizationTransaction.cs",
        "DesktopOrganizationTransaction.Restore.cs",
        "DesktopOrganizationTransfer.cs",
        "DesktopToastHudService.cs",
        "DeviceIdentity.cs",
        "DirectStartupService.cs",
        "DirectStartupService.Modes.cs",
        "DirectStartupTaskBackend.cs",
        "DirectStartupTaskXmlReader.cs",
        "DispatcherBackupTimer.cs",
        "DisplayAreaWatcherService.cs",
        "DisplayIdentityTokens.cs",
        "DisplayPlacementResolver.cs",
        "DisplayTopologyGate.cs",
        "DisplayTopologyTransitionCoordinator.cs",
        "DoubleControlHotkeyStateMachine.cs",
        "DoubleModifierHotkeyStateMachine.cs",
        "DragDropPermissionService.cs",
        "EverythingInstallationDetector.cs",
        "EverythingSearchService.cs",
        "ExternalActivationPolicy.cs",
        "ExternalSettingsAppliers.cs",
        "FeatureRuntimeRegistry.cs",
        "FeatureWidgetEntry.cs",
        "FeatureWidgetEntryFactory.cs",
        "FeatureWidgetSettings.cs",
        "FeatureWidgetsSettingsCoordinator.cs",
        "FeedbackService.cs",
        "FileDisplaySettingsCoordinator.cs",
        "FileMetaService.cs",
        "FileOpenPickerService.cs",
        "FileOpenRequestGate.cs",
        "FileOpenTrace.cs",
        "FileService.CaseOnlyRename.cs",
        "FileService.cs",
        "FileService.OpenItem.cs",
        "FileService.PathResolution.cs",
        "FileService.ShellTransfer.cs",
        "FileService.StorageCopy.cs",
        "FileService.TransferProgress.cs",
        "FileSessionRegistration.cs",
        "FileStackSettingsCoordinator.cs",
        "FileSurfaceRefreshPolicy.cs",
        "FileTransferSessionRegistry.cs",
        "FileWidgetFolderOpenBehaviorNames.cs",
        "FileWidgetHostDiagnostics.cs",
        "FileWidgetIconLayout.cs",
        "FileWidgetSession.cs",
        "FlyoutBridgeBandPromoter.cs",
        "FolderNavigationPathPolicy.cs",
        "FolderPickerService.cs",
        "FolderWatcherService.cs",
        "GlanceBackgroundPresenterState.cs",
        "GlanceCalendarDayChangePolicy.cs",
        "GlanceCalendarDayDecorationBuilder.cs",
        "GlanceCalendarLayoutCalculator.cs",
        "GlanceCalendarNavigationResolver.cs",
        "GlanceFestivalService.cs",
        "GlanceHolidayMarkService.cs",
        "GlanceImageDecodeSizeCalculator.cs",
        "GlanceImagePaletteService.cs",
        "GlanceImageService.cs",
        "GlanceImageStreamLoader.cs",
        "GlanceStyleLayoutCalculator.cs",
        "GlanceTodoAgendaSource.cs",
        "GlanceTraditionalCalendarService.cs",
        "GlanceWidgetContentProvider.cs",
        "GlanceWidgetContextMenuBuilder.cs",
        "GlanceWidgetSettingsPolicy.cs",
        "GlanceWidgetStore.cs",
        "GlobalHotkeyService.cs",
        "GroupNavigationSettingsCoordinator.cs",
        "HiddenWorkingSetTrimTracker.cs",
        "HookHealthWatchdog.cs",
        "HotkeyRecorderHookService.cs",
        "IAppUpdateService.cs",
        "ICloudBackupTransport.cs",
        "ICredentialStore.cs",
        "IdleWidgetZOrderPolicy.cs",
        "InfoTip.cs",
        "InitialFileWidgetPlacementPolicy.cs",
        "InitialFileWidgetSetupPolicy.cs",
        "InProcessSettingsAppActions.cs",
        "InProcessSettingsDataActions.cs",
        "InProcessSettingsHotkeyActions.cs",
        "InProcessSettingsRestoreActions.cs",
        "InProcessSettingsWidgetActions.cs",
        "InteractionBackdropSimplificationPolicy.cs",
        "InteractionSettingsCoordinator.cs",
        "IStartupService.cs",
        "ItemDropBehaviorPolicy.cs",
        "IWidgetContentProvider.cs",
        "JumpListService.cs",
        "LegacySearchIndexCleanupService.cs",
        "LocalCalendarPresentationSource.cs",
        "LocalizationService.cs",
        "Localized.cs",
        "MaintenanceSettingsCoordinator.cs",
        "ManagedStorageDesktopShortcutService.cs",
        "ManagedStorageMigrationDialog.cs",
        "ManagedStorageMigrationModels.cs",
        "ManagedStorageMigrationPresentation.cs",
        "ManagedStorageMigrationService.cs",
        "ManagedStoragePathService.cs",
        "ManagedStorageSettingsCoordinator.cs",
        "MarkdownDocumentService.cs",
        "MarkdownEditCommandEngine.cs",
        "MarkdownInlineImageExtractor.cs",
        "MaterialCapabilityAdvisor.cs",
        "MemoryCleanupPolicy.cs",
        "MemoryReclaimer.cs",
        "MonitorOcclusionTracker.cs",
        "MusicSessionService.cs",
        "MusicSourceAppIconResolver.cs",
        "MusicVolumeService.cs",
        "MusicWidgetContentProvider.cs",
        "MusicWidgetContextMenuBuilder.cs",
        "NativeAppNotificationService.cs",
        "NativeNotificationActivationBootstrap.cs",
        "NativeNotificationActivationEnvelopeStore.cs",
        "OrganizationHistoryPolicy.cs",
        "OrganizerService.cs",
        "PasswordVaultCredentialStore.cs",
        "PerformanceLogger.cs",
        "PerformanceLogger.InteractionResources.cs",
        "PerformanceSettingsCoordinator.cs",
        "PerformanceSettingsPolicy.cs",
        "PickerOwnerResolutionPolicy.cs",
        "PlaceholderWidgetContentProvider.cs",
        "PostRestoreRecoverySummary.cs",
        "PowerToysPeekPreviewService.cs",
        "QuickCaptureClipboardCopyPolicy.cs",
        "QuickCaptureClipboardFormatter.cs",
        "QuickCaptureMarkdownMirrorService.cs",
        "QuickCaptureService.cs",
        "QuickCaptureService.Optimization.cs",
        "QuickCaptureSettingsCoordinator.cs",
        "QuickCaptureStore.cs",
        "QuickLookPreviewService.cs",
        "QuickRevealDesktopDismissTracker.cs",
        "QuickRevealOutsideInteractionPolicy.cs",
        "QuickRevealTrayRaisePolicy.cs",
        "QuiescenceWorkingSetTrimTracker.cs",
        "RaisedBandGuestPolicy.cs",
        "RaisedGroupReassertPolicy.cs",
        "RecoveryOfferStore.cs",
        "RecoverySnapshotPeek.cs",
        "RelativeLayerRestorePolicy.cs",
        "ReleaseNotesAfterUpdatePolicy.cs",
        "ReleaseNotesService.cs",
        "RemoteAppUpdateService.cs",
        "RemoteBackupCommands.cs",
        "RemoteBackupRestoreActions.cs",
        "RemoteBackupSettings.cs",
        "RemoteSearchConnectionClient.cs",
        "RemoteSearchHotkeyController.cs",
        "RemoteSettingsAppActions.cs",
        "RemoteSettingsDataActions.cs",
        "RemoteSettingsHotkeyActions.cs",
        "RemoteSettingsPersistenceSink.cs",
        "RemoteSettingsRestoreActions.cs",
        "RemoteSettingsWidgetActions.cs",
        "ReorderDropIndexCalculator.cs",
        "ReorderInsertionIndicatorAnimator.cs",
        "ReservedHotkeyHookService.cs",
        "ResilientJsonStore.cs",
        "ResizeGuideOverlayService.cs",
        "RestoreAutoStartReconcilePolicy.cs",
        "RestoreMachineLocalPathRebase.cs",
        "RoundedBackdropMaskService.cs",
        "SearchCalculatorEvaluator.cs",
        "SearchDetailsPreviewLoader.cs",
        "SearchEngineService.cs",
        "SearchHistoryService.cs",
        "SearchHotkeyService.cs",
        "SearchPopupSurfacePolicy.cs",
        "SearchQuerySyntaxParser.cs",
        "SearchRecommendationLayoutService.cs",
        "SearchResultActionService.cs",
        "SearchResultCollectionReconciler.cs",
        "SearchResultRanker.cs",
        "SearchResultSelectionPolicy.cs",
        "SearchSettingsCoordinator.cs",
        "SearchWebAndUrlEvaluator.cs",
        "ServiceRegistry.cs",
        "SettingsIpcClient.cs",
        "SettingsIpcMethodRouter.cs",
        "SettingsIpcProtocol.cs",
        "SettingsIpcServer.cs",
        "SettingsIpcShell.cs",
        "SettingsMigrationService.cs",
        "SettingsPersistenceSink.cs",
        "SettingsSearchCatalog.cs",
        "SettingsSearchChannelPolicy.cs",
        "SettingsSearchMatcher.cs",
        "SettingsSearchMatcher.PinyinData.cs",
        "SettingsService.cs",
        "SettingsWindowPlacementStore.cs",
        "ShortcutLaunchPolicy.cs",
        "ShortcutTargetProbe.cs",
        "ShutdownSequence.cs",
        "SimpleMarkdownRenderer.cs",
        "SpacePreviewBroker.cs",
        "StackInputActivationArbiter.cs",
        "StackPopoverLayoutCalculator.cs",
        "StartupLaunchPolicy.cs",
        "StartupPipeline.cs",
        "StartupService.cs",
        "StartupServiceFactory.cs",
        "StartupSessionLedger.cs",
        "StartupWidgetRestoreRunner.cs",
        "StoreAppUpdateService.cs",
        "StoreStartupService.cs",
        "SyncOutboxStore.cs",
        "SyncRevisionsStore.cs",
        "SyncStateStore.cs",
        "SystemFontCatalogService.cs",
        "TemplateDocumentVerbPolicy.cs",
        "ThemeService.cs",
        "TitleActivationRaisePolicy.cs",
        "TodoClipboardFormatter.cs",
        "TodoExportService.cs",
        "TodoMasterDetailSettings.cs",
        "TodoNotificationActivationRouter.cs",
        "TodoRecurrenceService.cs",
        "TodoReminderService.cs",
        "TodoSettingsCoordinator.cs",
        "TodoTitleEditorHeightPolicy.cs",
        "TodoWidgetContentProvider.cs",
        "TodoWidgetStore.cs",
        "TransferIntentEngine.cs",
        "TrayAnimationInterruptionCoordinator.cs",
        "TrayToggleDecisionPolicy.cs",
        "TrayToggleRequestQueue.cs",
        "UpdateAutoDownloadPolicy.cs",
        "UpdateNetworkCostPolicy.cs",
        "UpdateStatusBadgePolicy.cs",
        "VirtualDisplayAdvisor.cs",
        "VirtualDropFileNameResolver.cs",
        "VisibleIdleMemoryTracker.cs",
        "WeatherCacheStore.cs",
        "WeatherRefreshBackoffPolicy.cs",
        "WeatherService.cs",
        "WeatherSettingsPolicy.cs",
        "WeatherWidgetContentProvider.cs",
        "WeatherWidgetViewModeSettings.cs",
        "WebDavBackupTransport.cs",
        "WidgetActionIconHelper.cs",
        "WidgetAnimationDisplayTiming.cs",
        "WidgetAnimationFramePacingPolicy.cs",
        "WidgetAnimationSettings.cs",
        "WidgetBackgroundCustomization.cs",
        "WidgetBackgroundPanoramaCalculator.cs",
        "WidgetBorderCustomization.cs",
        "WidgetBorderMenuBuilder.cs",
        "WidgetBorderVisualCalculator.cs",
        "WidgetCapsuleArrangementCalculator.cs",
        "WidgetCapsuleOrderCalculator.cs",
        "WidgetChromeMenuBuilder.cs",
        "WidgetChromeMode.cs",
        "WidgetChromeModeResolver.cs",
        "WidgetCollapseBehavior.cs",
        "WidgetCollapseMenuBuilder.cs",
        "WidgetCompactAnimationCoordinator.cs",
        "WidgetCompactAnimationFrameTracker.cs",
        "WidgetCompactBoundsCalculator.cs",
        "WidgetCompactConfirmationMenuBuilder.cs",
        "WidgetCompactExpansionCalculator.cs",
        "WidgetCompactExpansionDirectionPolicy.cs",
        "WidgetCompactFrameSkipPolicy.cs",
        "WidgetCompactInteractionPolicy.cs",
        "WidgetCompactPrivacyPolicy.cs",
        "WidgetCompactTransitionPolicy.cs",
        "WidgetCompactWarmupPolicy.cs",
        "WidgetCompactWarmupSchedulePolicy.cs",
        "WidgetCompositionResources.cs",
        "WidgetContentDescriptor.cs",
        "WidgetContentFactory.cs",
        "WidgetCoordinatedMoveCalculator.cs",
        "WidgetDangerActionStyle.cs",
        "WidgetDisplayChangeWatcher.cs",
        "WidgetElasticLayout.cs",
        "WidgetExpandedLayerLeasePolicy.cs",
        "WidgetFileStackSettings.cs",
        "WidgetFirstRunGuideFactory.cs",
        "WidgetForegroundMenuBuilder.cs",
        "WidgetForegroundSettings.cs",
        "WidgetFrameClockHealthPolicy.cs",
        "WidgetGroupChromePolicy.cs",
        "WidgetGroupDropHitTestPolicy.cs",
        "WidgetGroupFailureProbe.cs",
        "WidgetGroupMenuBuilder.cs",
        "WidgetGroupOrder.cs",
        "WidgetGroupPersistedTopologyRecovery.cs",
        "WidgetGroupSettings.cs",
        "WidgetGroupSwitchRequestCoordinator.cs",
        "WidgetGroupSwitchTimeline.cs",
        "WidgetImageStreamLoader.cs",
        "WidgetInputMetrics.cs",
        "WidgetLayerService.cs",
        "WidgetLayoutStore.cs",
        "WidgetLockMenuBuilder.cs",
        "WidgetManager.CapsuleArrangement.cs",
        "WidgetManager.CoordinatedMove.cs",
        "WidgetManager.cs",
        "WidgetManager.CustomWidgetAgent.cs",
        "WidgetManager.Diagnostics.cs",
        "WidgetManager.FeatureWidgets.cs",
        "WidgetManager.GroupDragPerformance.cs",
        "WidgetManager.Groups.cs",
        "WidgetManager.Memory.cs",
        "WidgetManager.MoveAll.cs",
        "WidgetManager.NewWidgetPlacement.cs",
        "WidgetManager.QuickLook.cs",
        "WidgetManager.ScreenHome.cs",
        "WidgetManager.Storage.cs",
        "WidgetManager.StorageMigration.cs",
        "WidgetManager.SurfaceContent.cs",
        "WidgetManager.Surfaces.cs",
        "WidgetManager.TrayAnimation.cs",
        "WidgetManager.ZOrder.cs",
        "WidgetMaterialSystemBackdrop.cs",
        "WidgetMaterialVisualCalculator.cs",
        "WidgetPathRebaseHelper.cs",
        "WidgetPlacementPreviewResolver.cs",
        "WidgetPositioningService.cs",
        "WidgetRegistry.cs",
        "WidgetScreenCatalog.cs",
        "WidgetScreenMenuBuilder.cs",
        "WidgetSegmentedLayoutHelper.cs",
        "WidgetSegmentedMetrics.cs",
        "WidgetSegmentedStyleHelper.cs",
        "WidgetSessionManager.cs",
        "WidgetSettingsMenuHelper.cs",
        "WidgetSharedImageSourceCache.cs",
        "WidgetSlideBoundaryPolicy.cs",
        "WidgetSnapCalculator.cs",
        "WidgetStackGroupingService.cs",
        "WidgetStartupRestorePolicy.cs",
        "WidgetStyleBackupProjection.cs",
        "WidgetSurfaceActivityTracker.cs",
        "WidgetSurfacePromotionTransaction.cs",
        "WidgetSurfaceRegistry.cs",
        "WidgetSurfaceSnapshotCache.cs",
        "WidgetSurfaceSwitchGatePool.cs",
        "WidgetTemporaryRaiseLeasePolicy.cs",
        "WidgetTitleBarMetrics.cs",
        "WidgetTitleIconAssetStore.cs",
        "WidgetTitleIconCustomization.cs",
        "WidgetTitleIconEmojiCatalog.cs",
        "WidgetTitleIconMode.cs",
        "WidgetTopologyLayoutService.cs",
        "WidgetTrayAnimationController.cs",
        "WidgetTrayAnimationFrameTracker.cs",
        "WidgetTrayAnimationPreparation.cs",
        "WidgetTrayBatchAnimationDriver.cs",
        "WidgetWindowDiagnostics.cs",
        "WindowsCompatibilityService.cs",
        "WindowsCrashEventCollector.cs",
        "WindowsTapHotkeyStateMachine.cs",
        "WindowsTapStartMenuLeakPolicy.cs",
        "WindowTrackingRegistry.cs",
        "WinSpaceHotkeyStateMachine.cs",
    };

    private static readonly HashSet<string> AllowedLegacyCustomWidgetSnapshotTypes = new(StringComparer.Ordinal);

    // Any production .cs file not in this legacy list must stay <= 3,500 lines.
    // Legacy mega file baselines are FROZEN at their 2026-11-21 measured size (the
    // pre-merge review flagged the old +40 headroom as ratchet self-approval); only
    // shrinkage may lower them. TODO(v1.6.1-split): decompose the mega partials and
    // delete entries from this list one by one.
    private const int DefaultMaxSourceFileLines = 3500;
    private const int LegacyMegaFileGrowthHeadroom = 3;

    private static readonly IReadOnlyDictionary<string, int> LegacyMegaSourceFiles =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"] = 8424,
            ["src/DeskBox/App.xaml.cs"] = 8027,
            ["src/DeskBox/Controls/WidgetShell.xaml.cs"] = 7592,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"] = 6086,
            ["src/DeskBox/Views/WidgetWindowBase.Collapse.cs"] = 5942,
            ["src/DeskBox/Services/SettingsService.cs"] = 5063,
            ["src/DeskBox/Services/DeskBoxDataBackupService.cs"] = 4748,
            ["src/DeskBox/Platform/Win32Helper.cs"] = 4429,
            ["src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"] = 4428,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ElevatedPointerDrag.cs"] = 4098,
            ["src/DeskBox/Services/FileService.cs"] = 4173,
            ["src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"] = 3545,
            ["src/DeskBox/ViewModels/WidgetViewModel.Stacks.cs"] = 3889,
            ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"] = 3802,
            ["src/DeskBox/Services/WidgetManager.Groups.cs"] = 3710,
            ["src/DeskBox/Services/QuickCaptureService.cs"] = 3626,
        };

    // Baseline count of instance UIElement/FlyoutBase fields on CustomWidgetStudioWindow
    // (XAML x:Name controls + imperative C# UI element fields). Must never grow.
    private const int MaxCustomWidgetStudioWindowUiFields = 14;

    [Fact]
    public void ViewsAndControls_GlobalAppAccessDoesNotGrow()
    {
        Regex access = new(@"\bApp\.(?:Current|UiDispatcherQueue)\b|\bIServiceProvider\b", RegexOptions.Compiled);
        List<string> violations = new();

        foreach ((string path, string source) in EnumerateProductSources()
                     .Where(item => (item.Path.StartsWith("src/DeskBox/Views/", StringComparison.Ordinal) ||
                                     item.Path.StartsWith("src/DeskBox/Controls/", StringComparison.Ordinal)) &&
                                    !IsAotSmokeOrFixture(item.Path)))
        {
            int count = access.Matches(source).Count;
            if (count == 0)
            {
                continue;
            }

            if (!ViewsAndControlsGlobalAccessExpected.TryGetValue(path, out int budget))
            {
                violations.Add($"  NEW {path}: {count}");
            }
            else if (count > budget)
            {
                violations.Add($"  GREW {path}: {count} (manifest {budget})");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Inject contracts/services into Views and Controls instead of adding App.Current/IServiceProvider accesses:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void FlatServicesRootDirectory_DoesNotAcceptNewFiles()
    {
        string servicesDir = TestPaths.FromRepository("src/DeskBox/Services");
        string[] unexpectedFiles = Directory
            .EnumerateFiles(servicesDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) &&
                           !name.Contains(".Aot", StringComparison.Ordinal) &&
                           !name.StartsWith("Aot", StringComparison.Ordinal) &&
                           !LegacyTopLevelServiceFiles.Contains(name!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        Assert.True(
            unexpectedFiles.Length == 0,
            "Do not add new flat files directly under src/DeskBox/Services/. Place new code inside src/DeskBox/Features/<Domain>/ or src/DeskBox/Services/<Domain>/:\n" +
            string.Join('\n', unexpectedFiles.Select(name => $"  NEW src/DeskBox/Services/{name}")));
    }

    [Fact]
    public void CustomWidgetViewModel_DoesNotAccumulateHardcodedTimersOrDomainSnapshots()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo[] declaredFields = typeof(CustomWidgetViewModel).GetFields(flags);

        FieldInfo[] timerFields = declaredFields
            .Where(field => field.FieldType == typeof(DispatcherQueueTimer))
            .ToArray();

        Assert.True(
            timerFields.Length == 0,
            $"CustomWidgetViewModel must hold 0 hardcoded DispatcherQueueTimer fields (found {timerFields.Length}). " +
            "Route template timing through capability providers instead.");

        string[] unexpectedSnapshots = declaredFields
            .Where(field => field.FieldType.Name.EndsWith("Snapshot", StringComparison.Ordinal) &&
                            !AllowedLegacyCustomWidgetSnapshotTypes.Contains(field.FieldType.Name))
            .Select(field => $"{field.Name}: {field.FieldType.Name}")
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unexpectedSnapshots.Length == 0,
            "CustomWidgetViewModel must not add new hardcoded domain *Snapshot fields; use a capability provider instead:\n" +
            string.Join('\n', unexpectedSnapshots.Select(item => $"  {item}")));
    }

    [Fact]
    public void MegaSourceFilesAndWindowUiFieldCounts_DoNotGrowUnbounded()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo[] studioUiFields = typeof(CustomWidgetStudioWindow)
            .GetFields(flags)
            .Where(field => typeof(UIElement).IsAssignableFrom(field.FieldType) ||
                            typeof(FlyoutBase).IsAssignableFrom(field.FieldType))
            .ToArray();

        Assert.True(
            studioUiFields.Length <= MaxCustomWidgetStudioWindowUiFields,
            $"CustomWidgetStudioWindow imperative UI field count grew to {studioUiFields.Length} (ceiling {MaxCustomWidgetStudioWindowUiFields}). " +
            "Extract new panels as independent UserControls rather than adding UI element fields to the window.");

        List<string> oversizedFiles = new();
        foreach ((string path, _) in EnumerateProductSources()
                     .Where(item => !IsAotSmokeOrFixture(item.Path)))
        {
            int lineCount = File.ReadLines(TestPaths.FromRepository(path)).Count();
            int ceiling = LegacyMegaSourceFiles.TryGetValue(path, out int baseline)
                ? baseline + LegacyMegaFileGrowthHeadroom
                : DefaultMaxSourceFileLines;

            if (lineCount > ceiling)
            {
                oversizedFiles.Add($"  {path}: {lineCount} lines (ceiling {ceiling})");
            }
        }

        Assert.True(
            oversizedFiles.Count == 0,
            "Source files exceeded anti-mud line ceilings. Decompose new functionality into cohesive controls/services:\n" +
            string.Join('\n', oversizedFiles));
    }

    private static bool IsAotSmokeOrFixture(string repoRelativePath)
    {
        string fileName = Path.GetFileName(repoRelativePath);
        return repoRelativePath.Contains(".Aot", StringComparison.Ordinal) ||
               fileName.StartsWith("Aot", StringComparison.Ordinal);
    }

    private static IEnumerable<(string Path, string Source)> EnumerateProductSources()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string repoRoot = TestPaths.FromRepository(".");
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => (
                Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(path)));
    }
}
