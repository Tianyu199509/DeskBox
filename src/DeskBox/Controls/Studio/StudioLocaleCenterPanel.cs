using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioLocaleCenterPanel : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioLocaleCenterPanel(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private CancellationTokenSource? _i18nQueueCts;
    private Button? _i18nHeaderButton;
    private FontIcon? _i18nHeaderIcon;
    private TextBlock? _i18nHeaderLabel;
    private Button? _previewCultureOverridePillButton;
    private Border? _localeCenterHostBorder;
    private TextBlock? _localeCenterSummaryText;
    private TextBlock? _localeCenterHintText;
    private ProgressBar? _localeCenterOverallProgressBar;
    private Button? _localeCenterSyncAllButton;
    private Button? _localeCenterSync2Button;
    private Button? _localeCenterRetryFailedButton;
    private Button? _localeCenterStopButton;
    private Button? _localeCenterCloseButton;
    private ScrollViewer? _localeCenterMatrixScroll;
    private readonly Dictionary<string, LocaleCardUiRefs> _localeCenterCardRefs =
        new(StringComparer.OrdinalIgnoreCase);

    internal Button? I18nHeaderButton { get => _i18nHeaderButton; set => _i18nHeaderButton = value; }
    internal FontIcon? I18nHeaderIcon { get => _i18nHeaderIcon; set => _i18nHeaderIcon = value; }
    internal TextBlock? I18nHeaderLabel { get => _i18nHeaderLabel; set => _i18nHeaderLabel = value; }
    internal Button? PreviewCultureOverridePillButton { get => _previewCultureOverridePillButton; set => _previewCultureOverridePillButton = value; }
    internal Border? LocaleCenterHostBorder { get => _localeCenterHostBorder; set => _localeCenterHostBorder = value; }
    internal TextBlock? LocaleCenterSummaryText { get => _localeCenterSummaryText; set => _localeCenterSummaryText = value; }
    internal TextBlock? LocaleCenterHintText { get => _localeCenterHintText; set => _localeCenterHintText = value; }
    internal ProgressBar? LocaleCenterOverallProgressBar { get => _localeCenterOverallProgressBar; set => _localeCenterOverallProgressBar = value; }
    internal Button? LocaleCenterSyncAllButton { get => _localeCenterSyncAllButton; set => _localeCenterSyncAllButton = value; }
    internal Button? LocaleCenterSync2Button { get => _localeCenterSync2Button; set => _localeCenterSync2Button = value; }
    internal Button? LocaleCenterRetryFailedButton { get => _localeCenterRetryFailedButton; set => _localeCenterRetryFailedButton = value; }
    internal Button? LocaleCenterStopButton { get => _localeCenterStopButton; set => _localeCenterStopButton = value; }
    internal Button? LocaleCenterCloseButton { get => _localeCenterCloseButton; set => _localeCenterCloseButton = value; }
    internal ScrollViewer? LocaleCenterMatrixScroll { get => _localeCenterMatrixScroll; set => _localeCenterMatrixScroll = value; }
    internal Dictionary<string, LocaleCardUiRefs> LocaleCenterCardRefs => _localeCenterCardRefs;

    internal void OpenLocaleCenter(bool toggleIfAlreadyOpen = false)
    {
        _session.IsLocaleCenterOpen = toggleIfAlreadyOpen ? !_session.IsLocaleCenterOpen : true;
        RefreshLocaleCenterChrome();
    }

    internal void SetActivePreviewCultureOverride(string? cultureCode)
    {
        string? normalized = string.IsNullOrWhiteSpace(cultureCode)
            ? null
            : CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(cultureCode);
        if (normalized is not null &&
            (string.Equals(normalized, _loc.CurrentCultureName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, _session.ActivePreviewCultureOverride, StringComparison.OrdinalIgnoreCase)))
        {
            normalized = null;
        }

        _session.ActivePreviewCultureOverride = normalized;
        _shell.StageViewport.PreviewVm?.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        UpdatePreviewTitlesForActiveCulture(
            _session.ActiveI18nWorkingManifestJson ?? _session.CurrentManifestJson,
            _session.ActiveI18nWorkingCatalog ?? _session.CurrentLocales);
        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        _shell.StageViewport.RefreshStageToolbarChrome();
        RefreshLocaleCenterChrome();
    }

    private void UpdatePreviewTitlesForActiveCulture(string? manifestJson, CustomWidgetLocaleCatalog? locales)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(manifestJson!).Manifest;
        if (manifest is not null && locales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(locales);
        }

        string title = manifest?.ResolveLocalizedName(_session.EffectivePreviewCultureName)
                       ?? _session.TargetWidgetName
                       ?? _loc.T("CustomWidget.Title");
        if (_shell.StageViewport.NameText is not null)
        {
            _shell.StageViewport.NameText.Text = title;
        }

        if (_shell.StageViewport.FrameTitleText is not null)
        {
            _shell.StageViewport.FrameTitleText.Text = title;
        }
    }

    internal void CancelI18nQueue()
    {
        try
        {
            _i18nQueueCts?.Cancel();
        }
        catch
        {
        }
    }

    internal async Task RunIncrementalI18nBatchAsync(IReadOnlyList<string>? onlyCultures = null)
    {
        if (_session.IsAnyStudioJobRunning || string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            return;
        }

        _session.IsLocaleCenterOpen = true;
        string originalManifestJson = _session.CurrentManifestJson!;
        string? currentLogicJs = _session.CurrentLogicJs;
        CustomWidgetLocaleCatalog? initialCatalog = _session.CurrentLocales;

        CustomWidgetI18nSyncPlan plan;
        try
        {
            plan = CustomWidgetI18nEngine.BuildSyncPlan(
                originalManifestJson,
                currentLogicJs,
                initialCatalog,
                fallbackCulture: _loc.CurrentCultureName);
            _session.CachedI18nSyncPlan = plan;
            _session.CachedI18nSyncPlanKey =
                $"{CustomWidgetStudioSessionStore.ComputeManifestSha256(originalManifestJson, currentLogicJs, initialCatalog)}|{_loc.CurrentCultureName}";
        }
        catch (Exception ex)
        {
            RefreshLocaleCenterChrome();
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.I18nTitle",
                "CustomWidget.Workshop.Failure.I18nBody",
                ex);
            return;
        }

        if (plan.RequiredKeys.Count == 0)
        {
            RefreshLocaleCenterChrome();
            _shell.ShowPageMessage(
                InfoBarSeverity.Informational,
                string.Empty,
                _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText"),
                null);
            return;
        }

        HashSet<string>? scopeSet = onlyCultures is { Count: > 0 }
            ? new HashSet<string>(
                onlyCultures.Select(CustomWidgetLocaleCatalog.NormalizeLocaleFileCode),
                StringComparer.OrdinalIgnoreCase)
            : null;
        int scopedPendingCount = plan.LocalePlans.Count(lp =>
            (scopeSet is null || scopeSet.Contains(lp.Culture)) && lp.NeedsTranslation);

        string workingManifestJson = plan.CompiledManifestJson;
        bool seedCatalogChanged = !string.Equals(
            initialCatalog?.ComputeSha256() ?? string.Empty,
            plan.SeededCatalog.ComputeSha256(),
            StringComparison.Ordinal);
        bool manifestChanged = !string.Equals(originalManifestJson, workingManifestJson, StringComparison.Ordinal);

        if (scopedPendingCount == 0)
        {
            if (scopeSet is null)
            {
                _session.LastFailedLocaleErrors.Clear();
            }
            else
            {
                foreach (string c in scopeSet)
                {
                    _session.LastFailedLocaleErrors.Remove(c);
                }
            }

            if (manifestChanged || seedCatalogChanged)
            {
                _session.AddRevision(
                    workingManifestJson,
                    _loc.T("CustomWidget.Workshop.I18n.CompiledBaseSummary"),
                    isAiChange: false,
                    source: "manual",
                    isCommitted: false,
                    markDirty: true,
                    logicJs: currentLogicJs,
                    hasExplicitLogicJs: true,
                    locales: plan.SeededCatalog,
                    hasExplicitLocales: true);
                _session.PersistActiveDraftIfDirty();
                if (_session.Page == StudioPage.Preview && _shell.StageViewport.PreviewVm is not null && _shell.StageViewport.PreviewContent is not null)
                {
                    _shell.StageViewport.ApplyLivePreviewUpdateInPlace();
                }
            }

            RefreshLocaleCenterChrome();
            _shell.ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.I18n.AlreadyUpToDate"),
                null);
            return;
        }

        string? providerId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
            CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
            _prefs.PreferredAiProviderId);
        if (providerId is null)
        {
            RefreshLocaleCenterChrome();
            await _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, null);
            providerId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                _prefs.PreferredAiProviderId);
            if (providerId is null)
            {
                RefreshLocaleCenterChrome();
                return;
            }
        }

        CancelI18nQueue();
        var cts = new CancellationTokenSource();
        _i18nQueueCts = cts;
        _session.IsI18nQueueRunning = true;
        _session.ActiveI18nWorkingManifestJson = workingManifestJson;
        _session.ActiveI18nWorkingCatalog = plan.SeededCatalog;
        _session.InvalidateI18nSyncPlanCache();
        _session.LiveLocaleJobUpdates.Clear();

        if (scopeSet is null)
        {
            _session.LastFailedLocaleErrors.Clear();
        }
        else
        {
            foreach (string c in scopeSet)
            {
                _session.LastFailedLocaleErrors.Remove(c);
            }
        }

        if (!_session.IsEditMode)
        {
            try
            {
                string draftDir = _sessionStore.GetCreateDraftDirectory();
                Directory.CreateDirectory(draftDir);
                _session.TouchDraftWriteTick();
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
                    Path.Combine(draftDir, "manifest.json"),
                    workingManifestJson);
                plan.SeededCatalog.WriteToDirectoryAtomically(
                    Path.Combine(draftDir, CustomWidgetDataStore.LocalesDirectoryName),
                    removeStaleFiles: false,
                    preferredKeyOrder: plan.RequiredKeys);
                _session.TouchDraftWriteTick();
            }
            catch (Exception seedEx)
            {
                _hostBridge.Log($"[CustomWidgetStudio] Draft locale seed failed: {seedEx.Message}");
            }
        }

        if (_shell.StageViewport.PreviewVm is not null)
        {
            _shell.StageViewport.PreviewVm.ApplyPreviewBundle(
                workingManifestJson,
                currentLogicJs,
                plan.SeededCatalog,
                preserveRuntimeData: true);
            _shell.StageViewport.PreviewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        }

        UpdatePreviewTitlesForActiveCulture(workingManifestJson, plan.SeededCatalog);
        _shell.ChatPanel.RefreshInPlaceGenerationChrome();
        RefreshLocaleCenterChrome();

        try
        {
            WidgetIncrementalI18nBatchResult batchResult =
                await CustomWidgetAiGeneratorService.Shared.RunIncrementalI18nQueueAsync(
                    providerId,
                    plan,
                    onlyCultures: onlyCultures,
                    onLocaleProgress: update =>
                    {
                        if (_shell.IsClosed)
                        {
                            return;
                        }

                        _shell.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_shell.IsClosed)
                            {
                                return;
                            }

                            _session.LiveLocaleJobUpdates[update.Culture] = update;
                            if (update.State == LocaleTranslationJobState.Failed &&
                                !string.IsNullOrWhiteSpace(update.ErrorMessage))
                            {
                                _session.LastFailedLocaleErrors[update.Culture] = update.ErrorMessage!;
                            }
                            else if (update.State == LocaleTranslationJobState.Completed)
                            {
                                _session.LastFailedLocaleErrors.Remove(update.Culture);
                            }

                            RefreshLocaleCenterChrome();
                        });
                    },
                    onLocaleCompletedAsync: (culture, entries, snapshotCatalog) =>
                    {
                        if (_shell.IsClosed)
                        {
                            return Task.CompletedTask;
                        }

                        if (!_session.IsEditMode)
                        {
                            try
                            {
                                string draftDir = _sessionStore.GetCreateDraftDirectory();
                                string localesDir = Path.Combine(draftDir, CustomWidgetDataStore.LocalesDirectoryName);
                                _session.TouchDraftWriteTick();
                                CustomWidgetLocaleCatalog.WriteSingleLocaleAtomically(
                                    localesDir,
                                    culture,
                                    entries,
                                    snapshotCatalog.Meta,
                                    plan.RequiredKeys);
                                _session.TouchDraftWriteTick();
                            }
                            catch (Exception diskEx)
                            {
                                _hostBridge.Log($"[CustomWidgetStudio] Draft locale checkpoint write failed ({culture}): {diskEx.Message}");
                            }
                        }

                        _shell.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_shell.IsClosed)
                            {
                                return;
                            }

                            _session.ActiveI18nWorkingCatalog = snapshotCatalog;
                            _session.InvalidateI18nSyncPlanCache();
                            _session.PersistI18nCheckpointDraft(workingManifestJson, currentLogicJs, snapshotCatalog);
                            if (_shell.StageViewport.PreviewVm is not null)
                            {
                                _shell.StageViewport.PreviewVm.ApplyPreviewBundle(
                                    workingManifestJson,
                                    currentLogicJs,
                                    snapshotCatalog,
                                    preserveRuntimeData: true);
                                _shell.StageViewport.PreviewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
                            }

                            UpdatePreviewTitlesForActiveCulture(workingManifestJson, snapshotCatalog);
                            RefreshLocaleCenterChrome();
                        });
                        return Task.CompletedTask;
                    },
                    cancellationToken: cts.Token);

            if (_shell.IsClosed)
            {
                return;
            }

            _session.IsI18nQueueRunning = false;
            _session.ActiveI18nWorkingManifestJson = null;
            _session.ActiveI18nWorkingCatalog = null;
            _session.LiveLocaleJobUpdates.Clear();
            _session.InvalidateI18nSyncPlanCache();

            foreach (string doneCulture in batchResult.CompletedLocales)
            {
                _session.LastFailedLocaleErrors.Remove(doneCulture);
            }

            foreach ((string failedCulture, string errMsg) in batchResult.FailedLocales)
            {
                _session.LastFailedLocaleErrors[failedCulture] = errMsg;
            }

            bool anyCompleted = batchResult.CompletedLocales.Count > 0;
            bool finalCatalogChanged = !string.Equals(
                initialCatalog?.ComputeSha256() ?? string.Empty,
                batchResult.UpdatedCatalog.ComputeSha256(),
                StringComparison.Ordinal);

            if (anyCompleted || finalCatalogChanged || manifestChanged)
            {
                CustomWidgetParseResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                    workingManifestJson,
                    currentLogicJs,
                    batchResult.UpdatedCatalog);
                string finalJsonToStore = validated.Manifest is not null && !validated.Diagnostics.Any(static d => d.IsError)
                    ? workingManifestJson
                    : originalManifestJson;

                CustomWidgetI18nSyncPlan postPlan = CustomWidgetI18nEngine.BuildSyncPlan(
                    finalJsonToStore,
                    currentLogicJs,
                    batchResult.UpdatedCatalog,
                    fallbackCulture: _loc.CurrentCultureName);

                string summary = batchResult.Cancelled
                    ? _loc.Format("CustomWidget.Workshop.I18n.SummaryCancelled", batchResult.CompletedLocales.Count)
                    : batchResult.FailedLocales.Count > 0
                        ? _loc.Format(
                            "CustomWidget.Workshop.I18n.SummaryPartialFailed",
                            batchResult.CompletedLocales.Count,
                            batchResult.FailedLocales.Count)
                        : _loc.Format(
                            "CustomWidget.Workshop.I18n.SummaryDone",
                            postPlan.ReadyLocaleCount,
                            postPlan.TotalLocaleCount);

                string replyText = batchResult.Cancelled
                    ? _loc.Format("CustomWidget.Workshop.I18n.ReplyCancelled", batchResult.CompletedLocales.Count)
                    : batchResult.FailedLocales.Count > 0
                        ? _loc.Format(
                            "CustomWidget.Workshop.I18n.ReplyPartialFailed",
                            batchResult.CompletedLocales.Count,
                            batchResult.FailedLocales.Count)
                        : _loc.Format(
                            "CustomWidget.Workshop.I18n.ReplyDone",
                            batchResult.CompletedLocales.Count,
                            postPlan.ReadyLocaleCount,
                            postPlan.TotalLocaleCount);

                StudioPersistedRevision rev = _session.AddRevision(
                    finalJsonToStore,
                    summary,
                    isAiChange: anyCompleted,
                    source: anyCompleted ? "ai" : "manual",
                    isCommitted: false,
                    markDirty: true,
                    logicJs: currentLogicJs,
                    hasExplicitLogicJs: true,
                    locales: batchResult.UpdatedCatalog,
                    hasExplicitLocales: true);

                _session.AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-i18n-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "assistant",
                    Kind = "edit",
                    Text = replyText,
                    LinkedRevisionId = rev.RevisionId,
                    DiffPills = [summary],
                    Suggestions = []
                });

                _session.PersistActiveDraftIfDirty();
                if (_session.Page == StudioPage.Preview && _shell.StageViewport.PreviewVm is not null && _shell.StageViewport.PreviewContent is not null)
                {
                    _shell.StageViewport.ApplyLivePreviewUpdateInPlace();
                }
                else
                {
                    _shell.StageViewport.ShowPreviewPage(justLoaded: true);
                }
            }
            else
            {
                if (_shell.StageViewport.PreviewVm is not null && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
                {
                    _shell.StageViewport.PreviewVm.ApplyPreviewBundle(
                        _session.CurrentManifestJson!,
                        _session.CurrentLogicJs,
                        _session.CurrentLocales,
                        preserveRuntimeData: true);
                    _shell.StageViewport.PreviewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
                }

                UpdatePreviewTitlesForActiveCulture(_session.CurrentManifestJson, _session.CurrentLocales);
                _shell.ChatPanel.RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
            }

            if (!batchResult.Cancelled && batchResult.FailedLocales.Count > 0 && !string.IsNullOrWhiteSpace(batchResult.ErrorMessage))
            {
                _shell.ShowPageMessage(InfoBarSeverity.Warning, string.Empty, batchResult.ErrorMessage!, null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_shell.IsClosed)
            {
                _session.IsI18nQueueRunning = false;
                _session.ActiveI18nWorkingManifestJson = null;
                _session.ActiveI18nWorkingCatalog = null;
                _session.LiveLocaleJobUpdates.Clear();
                _session.InvalidateI18nSyncPlanCache();
                _shell.ChatPanel.RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
            }
        }
        catch (Exception ex)
        {
            if (!_shell.IsClosed)
            {
                _session.IsI18nQueueRunning = false;
                _session.ActiveI18nWorkingManifestJson = null;
                _session.ActiveI18nWorkingCatalog = null;
                _session.LiveLocaleJobUpdates.Clear();
                _session.InvalidateI18nSyncPlanCache();
                _shell.ChatPanel.RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
                _shell.ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
            }
        }
        finally
        {
            if (ReferenceEquals(_i18nQueueCts, cts))
            {
                _i18nQueueCts = null;
            }

            cts.Dispose();
        }
    }

    internal Border BuildLocaleCenterPanel()
    {
        _localeCenterCardRefs.Clear();
        var rootStack = new StackPanel { Spacing = 8 };

        var topRow = new Grid { ColumnSpacing = 8 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleCol = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            IsTapEnabled = true
        };
        titleCol.Tapped += (_, _) =>
        {
            if (_session.IsI18nQueueRunning && !_session.IsLocaleCenterOpen)
            {
                _session.IsLocaleCenterOpen = true;
                RefreshLocaleCenterChrome();
            }
        };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uF2B7",
            FontSize = 13,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.CenterTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleCol.Children.Add(titleLine);

        _localeCenterSummaryText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        titleCol.Children.Add(_localeCenterSummaryText);
        topRow.Children.Add(titleCol);

        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _localeCenterSync2Button = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = FormatBilingualSyncLabel(shortLabel: true)
        };
        _localeCenterSync2Button.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "LocaleCenterSync2",
            () => RunIncrementalI18nBatchAsync(ResolveActiveBilingualCultureCodes()));
        actionsRow.Children.Add(_localeCenterSync2Button);

        _localeCenterRetryFailedButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Visibility = Visibility.Collapsed
        };
        _localeCenterRetryFailedButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "LocaleCenterRetryFailed",
            async () =>
            {
                if (_session.LastFailedLocaleErrors.Count > 0)
                {
                    await RunIncrementalI18nBatchAsync(_session.LastFailedLocaleErrors.Keys.ToArray());
                }
            });
        actionsRow.Children.Add(_localeCenterRetryFailedButton);

        _localeCenterSyncAllButton = new Button
        {
            Height = 28,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Style = StudioUiPrimitives.AccentButtonStyle(),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE774", _loc.T("CustomWidget.Workshop.I18n.SyncAllShort"))
        };
        _localeCenterSyncAllButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "LocaleCenterSyncAll",
            () => RunIncrementalI18nBatchAsync(null));
        actionsRow.Children.Add(_localeCenterSyncAllButton);

        _localeCenterStopButton = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Visibility = Visibility.Collapsed,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE71A", _loc.T("CustomWidget.Workshop.Stream.StopButton"))
        };
        _localeCenterStopButton.Click += (_, _) => CancelI18nQueue();
        actionsRow.Children.Add(_localeCenterStopButton);

        _localeCenterCloseButton = StudioUiPrimitives.CreateIconButton("\uE711", _loc.T("Common.Close"));
        _localeCenterCloseButton.Width = 28;
        _localeCenterCloseButton.Height = 28;
        _localeCenterCloseButton.CornerRadius = new CornerRadius(6);
        _localeCenterCloseButton.Click += (_, _) =>
        {
            _session.IsLocaleCenterOpen = false;
            RefreshLocaleCenterChrome();
        };
        actionsRow.Children.Add(_localeCenterCloseButton);

        Grid.SetColumn(actionsRow, 1);
        topRow.Children.Add(actionsRow);
        rootStack.Children.Add(topRow);

        _localeCenterOverallProgressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 4,
            IsIndeterminate = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        rootStack.Children.Add(_localeCenterOverallProgressBar);

        var matrixGrid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            Padding = new Thickness(0, 2, 4, 2)
        };
        matrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        matrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        IReadOnlyList<CustomWidgetSupportedLocaleInfo> supported = CustomWidgetLocaleCatalog.SupportedLocales;
        int rowCount = (supported.Count + 1) / 2;
        for (int r = 0; r < rowCount; r++)
        {
            matrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int idx = 0; idx < supported.Count; idx++)
        {
            CustomWidgetSupportedLocaleInfo info = supported[idx];
            string capturedCode = info.Code;

            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush")
            };

            var innerGrid = new Grid { RowSpacing = 4, Padding = new Thickness(9, 6, 9, 6) };
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerLine = new Grid { ColumnSpacing = 6 };
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var previewEye = new FontIcon
            {
                Glyph = "\uE890",
                FontSize = 10.5,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            headerLine.Children.Add(previewEye);

            var nameText = new TextBlock
            {
                Text = $"{info.NativeName}  {info.Code}",
                FontSize = 11.5,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 1);
            headerLine.Children.Add(nameText);

            var statusText = new TextBlock
            {
                FontSize = 11,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
            };
            Grid.SetColumn(statusText, 2);
            headerLine.Children.Add(statusText);

            var retrySingleBtn = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed,
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 9.5 }
            };
            ToolTipService.SetToolTip(retrySingleBtn, _loc.T("CustomWidget.Workshop.I18n.RetrySingleTip"));
            retrySingleBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                _shell,
                "LocaleCenterRetrySingle",
                async () =>
                {
                    if (!_session.IsAnyStudioJobRunning)
                    {
                        await RunIncrementalI18nBatchAsync([capturedCode]);
                    }
                });
            Grid.SetColumn(retrySingleBtn, 3);
            headerLine.Children.Add(retrySingleBtn);

            innerGrid.Children.Add(headerLine);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Height = 3,
                IsIndeterminate = false,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(bar, 1);
            innerGrid.Children.Add(bar);

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = innerGrid
            };
            AutomationProperties.SetName(selectBtn, $"{info.NativeName} ({info.Code})");
            selectBtn.Click += (_, _) => SetActivePreviewCultureOverride(capturedCode);

            cardBorder.Child = selectBtn;
            Grid.SetRow(cardBorder, idx / 2);
            Grid.SetColumn(cardBorder, idx % 2);
            matrixGrid.Children.Add(cardBorder);

            _localeCenterCardRefs[info.Code] = new LocaleCardUiRefs(
                info.Code,
                info.NativeName,
                cardBorder,
                selectBtn,
                nameText,
                previewEye,
                statusText,
                retrySingleBtn,
                bar);
        }

        var matrixScroll = new ScrollViewer
        {
            MaxHeight = 224,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = matrixGrid
        };
        _localeCenterMatrixScroll = matrixScroll;
        rootStack.Children.Add(matrixScroll);

        _localeCenterHintText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.ClickToPreviewHint"),
            FontSize = 11,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        rootStack.Children.Add(_localeCenterHintText);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerFillColorAltBrush"),
            Visibility = Visibility.Collapsed,
            Child = rootStack
        };
    }

    internal void RefreshLocaleCenterChrome()
    {
        bool hasManifest = !string.IsNullOrWhiteSpace(_session.ActiveI18nWorkingManifestJson ?? _session.CurrentManifestJson);
        CustomWidgetI18nSyncPlan? plan = hasManifest ? _session.GetOrBuildCurrentI18nSyncPlan() : null;
        int totalLocales = CustomWidgetLocaleCatalog.SupportedLocales.Count;
        int requiredKeys = plan?.RequiredKeys.Count ?? 0;

        int readyLocaleCount = 0;
        int pendingLocaleCount = 0;
        int overallDoneUnits = 0;
        int overallTotalUnits = Math.Max(1, totalLocales * Math.Max(1, requiredKeys));

        Dictionary<string, CustomWidgetLocaleTaskPlan> planByCulture =
            plan?.LocalePlans.ToDictionary(static p => p.Culture, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CustomWidgetLocaleTaskPlan>(StringComparer.OrdinalIgnoreCase);

        string activePreviewCulture = _session.EffectivePreviewCultureName;

        foreach (CustomWidgetSupportedLocaleInfo info in CustomWidgetLocaleCatalog.SupportedLocales)
        {
            planByCulture.TryGetValue(info.Code, out CustomWidgetLocaleTaskPlan? lp);
            _session.LiveLocaleJobUpdates.TryGetValue(info.Code, out LocaleTranslationProgressUpdate? liveUpdate);
            _session.LastFailedLocaleErrors.TryGetValue(info.Code, out string? lastErr);

            bool isLocaleReady = false;
            int doneKeysForLocale = 0;
            int maxKeysForLocale = Math.Max(1, requiredKeys);
            string statusLabel;
            Brush statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush");
            bool showRetrySingle = false;
            string? errorTip = null;

            if (requiredKeys == 0)
            {
                isLocaleReady = true;
                doneKeysForLocale = 1;
                statusLabel = "—";
            }
            else if (liveUpdate is not null)
            {
                maxKeysForLocale = Math.Max(1, liveUpdate.RequiredTotalKeys);
                switch (liveUpdate.State)
                {
                    case LocaleTranslationJobState.Completed:
                    case LocaleTranslationJobState.SkippedUpToDate:
                        isLocaleReady = true;
                        doneKeysForLocale = maxKeysForLocale;
                        statusLabel = lp?.Status == CustomWidgetLocaleSyncStatus.BaseReady
                            ? _loc.T("CustomWidget.Workshop.I18n.StatusBase")
                            : _loc.T("CustomWidget.Workshop.I18n.StatusReady");
                        statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush");
                        break;
                    case LocaleTranslationJobState.Running:
                        doneKeysForLocale = Math.Clamp(
                            liveUpdate.ReadyTotalKeys + liveUpdate.TranslatedCount,
                            0,
                            maxKeysForLocale);
                        statusLabel = _loc.Format(
                            "CustomWidget.Workshop.I18n.StatusRunning",
                            Math.Min(liveUpdate.TranslatedCount, liveUpdate.TotalDeltaCount),
                            liveUpdate.TotalDeltaCount);
                        statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Retrying:
                        doneKeysForLocale = Math.Clamp(
                            liveUpdate.ReadyTotalKeys + liveUpdate.TranslatedCount,
                            0,
                            maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusRetrying");
                        statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCautionBrush");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Queued:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusQueued");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Failed:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusFailed");
                        statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush");
                        errorTip = liveUpdate.ErrorMessage ?? lastErr;
                        showRetrySingle = !_session.IsAnyStudioJobRunning;
                        pendingLocaleCount++;
                        break;
                    default:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", liveUpdate.TotalDeltaCount);
                        pendingLocaleCount++;
                        break;
                }
            }
            else if (lp is not null)
            {
                maxKeysForLocale = Math.Max(1, lp.TotalRequiredKeys);
                bool requiresCompileForNonBase = plan?.ManifestChanged == true &&
                                                 lp.Status != CustomWidgetLocaleSyncStatus.BaseReady;
                if (!lp.NeedsTranslation && !requiresCompileForNonBase)
                {
                    isLocaleReady = true;
                    doneKeysForLocale = maxKeysForLocale;
                    statusLabel = lp.Status == CustomWidgetLocaleSyncStatus.BaseReady
                        ? _loc.T("CustomWidget.Workshop.I18n.StatusBase")
                        : _loc.T("CustomWidget.Workshop.I18n.StatusReady");
                    statusBrush = lp.Status == CustomWidgetLocaleSyncStatus.BaseReady
                        ? StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
                        : StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush");
                }
                else if (requiresCompileForNonBase && !lp.NeedsTranslation)
                {
                    doneKeysForLocale = Math.Max(0, maxKeysForLocale - 1);
                    statusLabel = _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", 1);
                    pendingLocaleCount++;
                }
                else if (!string.IsNullOrWhiteSpace(lastErr))
                {
                    doneKeysForLocale = Math.Clamp(lp.ReadyKeyCount, 0, maxKeysForLocale);
                    statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusFailed");
                    statusBrush = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush");
                    errorTip = lastErr;
                    showRetrySingle = !_session.IsAnyStudioJobRunning;
                    pendingLocaleCount++;
                }
                else
                {
                    doneKeysForLocale = Math.Clamp(lp.ReadyKeyCount, 0, maxKeysForLocale);
                    statusLabel = lp.StaleKeys.Count > 0 && lp.MissingKeys.Count == 0
                        ? _loc.Format("CustomWidget.Workshop.I18n.StatusStale", lp.DeltaKeys.Count)
                        : _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", lp.DeltaKeys.Count);
                    pendingLocaleCount++;
                }
            }
            else
            {
                statusLabel = "—";
            }

            if (isLocaleReady)
            {
                readyLocaleCount++;
            }

            overallDoneUnits += doneKeysForLocale;

            if (_localeCenterCardRefs.TryGetValue(info.Code, out LocaleCardUiRefs? ui))
            {
                bool isActivePreview = string.Equals(activePreviewCulture, info.Code, StringComparison.OrdinalIgnoreCase);
                ui.PreviewEyeIcon.Visibility = isActivePreview ? Visibility.Visible : Visibility.Collapsed;
                ui.NameText.FontWeight = isActivePreview ? FontWeights.SemiBold : FontWeights.Normal;
                ui.NameText.Foreground = isActivePreview
                    ? StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                    : StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush");
                ui.CardBorder.BorderBrush = isActivePreview
                    ? StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush")
                    : StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush");
                ui.CardBorder.Background = isActivePreview
                    ? StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
                    : StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush");

                ui.StatusText.Text = statusLabel;
                ui.StatusText.Foreground = statusBrush;
                ui.RetrySingleButton.Visibility = showRetrySingle ? Visibility.Visible : Visibility.Collapsed;
                ui.ProgressBar.Maximum = maxKeysForLocale;
                ui.ProgressBar.Value = doneKeysForLocale;
                ui.SelectButton.IsEnabled = hasManifest;

                string baseTip = _loc.Format("CustomWidget.Workshop.I18n.PreviewLocaleCardTip", info.NativeName, info.Code);
                ToolTipService.SetToolTip(
                    ui.SelectButton,
                    string.IsNullOrWhiteSpace(errorTip) ? baseTip : $"{errorTip}\n{baseTip}");
            }
        }

        if (_i18nHeaderButton is not null && _i18nHeaderLabel is not null && _i18nHeaderIcon is not null)
        {
            _i18nHeaderButton.IsEnabled = hasManifest;
            bool openOrRunning = _session.IsLocaleCenterOpen || _session.IsI18nQueueRunning;
            _i18nHeaderButton.Visibility = openOrRunning ? Visibility.Visible : Visibility.Collapsed;
            string pillText = !hasManifest || requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.HeaderButton")
                : (readyLocaleCount >= totalLocales
                    ? $"{readyLocaleCount}/{totalLocales} ✓"
                    : $"{readyLocaleCount}/{totalLocales}");
            _i18nHeaderLabel.Text = pillText;
            _i18nHeaderButton.Background = StudioUiPrimitives.ThemeBrush(_shell, openOrRunning ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
            _i18nHeaderButton.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, openOrRunning ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            _i18nHeaderIcon.Foreground = StudioUiPrimitives.ThemeBrush(_shell, openOrRunning ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

            string headerTip = requiredKeys > 0
                ? _loc.Format("CustomWidget.Workshop.I18n.HeaderTooltipFormat", readyLocaleCount, totalLocales, requiredKeys)
                : _loc.T("CustomWidget.Workshop.I18n.CenterTitle");
            ToolTipService.SetToolTip(_i18nHeaderButton, headerTip);
            AutomationProperties.SetName(_i18nHeaderButton, headerTip);
        }

        if (_localeCenterHostBorder is null)
        {
            _shell.ComposerBuilder.RefreshComposerAsyncStatus();
            return;
        }

        bool showPanel = hasManifest && (_session.IsLocaleCenterOpen || _session.IsI18nQueueRunning);
        _localeCenterHostBorder.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        _shell.ComposerBuilder.RefreshComposerAsyncStatus();
        if (!showPanel)
        {
            return;
        }

        bool showFullMatrix = _session.IsLocaleCenterOpen;
        if (_localeCenterMatrixScroll is not null)
        {
            _localeCenterMatrixScroll.Visibility = showFullMatrix ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_localeCenterSummaryText is not null)
        {
            _localeCenterSummaryText.Text = requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText")
                : (_session.IsI18nQueueRunning
                    ? _loc.Format("CustomWidget.Workshop.I18n.SummaryRunningFormat", readyLocaleCount, totalLocales, requiredKeys)
                    : _loc.Format("CustomWidget.Workshop.I18n.SummaryIdleFormat", readyLocaleCount, totalLocales, requiredKeys));
        }

        if (_localeCenterOverallProgressBar is not null)
        {
            double pct = requiredKeys == 0
                ? 100.0
                : Math.Clamp(overallDoneUnits * 100.0 / overallTotalUnits, 0.0, 100.0);
            _localeCenterOverallProgressBar.Value = pct;
        }

        if (_localeCenterHintText is not null)
        {
            _localeCenterHintText.Text = requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText")
                : _loc.T("CustomWidget.Workshop.I18n.ClickToPreviewHint");
            _localeCenterHintText.Visibility = showFullMatrix ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_localeCenterSyncAllButton is not null)
        {
            _localeCenterSyncAllButton.Visibility = _session.IsI18nQueueRunning ? Visibility.Collapsed : Visibility.Visible;
            _localeCenterSyncAllButton.IsEnabled = !_session.IsAnyStudioJobRunning && requiredKeys > 0;
        }

        if (_localeCenterSync2Button is not null)
        {
            _localeCenterSync2Button.Content = FormatBilingualSyncLabel(shortLabel: true);
            _localeCenterSync2Button.Visibility = _session.IsI18nQueueRunning ? Visibility.Collapsed : Visibility.Visible;
            _localeCenterSync2Button.IsEnabled = !_session.IsAnyStudioJobRunning && requiredKeys > 0;
        }

        if (_localeCenterRetryFailedButton is not null)
        {
            int failedCount = _session.LastFailedLocaleErrors.Count;
            if (!_session.IsI18nQueueRunning && failedCount > 0)
            {
                _localeCenterRetryFailedButton.Content = _loc.Format("CustomWidget.Workshop.I18n.RetryFailedButton", failedCount);
                _localeCenterRetryFailedButton.IsEnabled = !_session.IsAnyStudioJobRunning;
                _localeCenterRetryFailedButton.Visibility = Visibility.Visible;
            }
            else
            {
                _localeCenterRetryFailedButton.Visibility = Visibility.Collapsed;
            }
        }

        if (_localeCenterStopButton is not null)
        {
            _localeCenterStopButton.Visibility = _session.IsI18nQueueRunning ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_localeCenterCloseButton is not null)
        {
            _localeCenterCloseButton.IsEnabled = true;
            _localeCenterCloseButton.Visibility = showFullMatrix ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal IReadOnlyList<string> ResolveActiveBilingualCultureCodes() =>
        CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes(
            _loc.CurrentCultureName,
            _session.CurrentLocales?.Meta?.BaseLocale);

    internal string FormatBilingualSyncLabel(bool shortLabel)
    {
        IReadOnlyList<string> pair = ResolveActiveBilingualCultureCodes();
        bool isZhEnPair = pair.Count == 2 &&
            ((pair[0].StartsWith("zh-", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(pair[1], "en-US", StringComparison.OrdinalIgnoreCase)) ||
             (string.Equals(pair[0], "en-US", StringComparison.OrdinalIgnoreCase) &&
              pair[1].StartsWith("zh-", StringComparison.OrdinalIgnoreCase)));
        if (isZhEnPair)
        {
            return _loc.T(shortLabel
                ? "CustomWidget.Workshop.I18n.Sync2Short"
                : "CustomWidget.Workshop.More.I18nSync2");
        }

        string first = string.Equals(pair[0], "en-US", StringComparison.OrdinalIgnoreCase)
            ? "EN"
            : CustomWidgetLocaleCatalog.GetLocaleInfo(pair[0]).NativeName;
        string second = string.Equals(pair[1], "en-US", StringComparison.OrdinalIgnoreCase)
            ? "EN"
            : CustomWidgetLocaleCatalog.GetLocaleInfo(pair[1]).NativeName;
        return _loc.Format(
            shortLabel
                ? "CustomWidget.Workshop.I18n.SyncBilingualPairFormat"
                : "CustomWidget.Workshop.More.I18nSyncBilingualPairFormat",
            first,
            second);
    }

    internal void ClearPreviewElementRefs()
    {
        _i18nHeaderButton = null;
        _i18nHeaderIcon = null;
        _i18nHeaderLabel = null;
        _previewCultureOverridePillButton = null;
        _localeCenterHostBorder = null;
        _localeCenterSummaryText = null;
        _localeCenterHintText = null;
        _localeCenterOverallProgressBar = null;
        _localeCenterSyncAllButton = null;
        _localeCenterSync2Button = null;
        _localeCenterRetryFailedButton = null;
        _localeCenterStopButton = null;
        _localeCenterCloseButton = null;
        _localeCenterMatrixScroll = null;
        _localeCenterCardRefs.Clear();
    }

    public void Dispose()
    {
        CancelI18nQueue();
        ClearPreviewElementRefs();
    }
}

internal sealed record LocaleCardUiRefs(
    string Code,
    string NativeName,
    Border CardBorder,
    Button SelectButton,
    TextBlock NameText,
    FontIcon PreviewEyeIcon,
    TextBlock StatusText,
    Button RetrySingleButton,
    ProgressBar ProgressBar);
