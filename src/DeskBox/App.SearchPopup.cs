// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private Task ToggleSearchPopupAsync()
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => _ = ToggleSearchPopupAsync());
            return Task.CompletedTask;
        }

        if (_searchPopupWindow?.IsPopupVisible == true)
        {
            _searchPopupWindow.HidePopup();
            return Task.CompletedTask;
        }

        OpenSearchPopupCore(initialQuery: null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Public entry point used by the search widget (and other direct UI callers).
    /// Unlike the global hotkey, this is intentionally idempotent: queued repeat clicks
    /// can only focus the popup and can never hide the window that the first click opened.
    /// </summary>
    public void OpenSearchPopup()
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(OpenSearchPopup);
            return;
        }

        OpenSearchPopupCore(initialQuery: null);
    }

    /// <summary>
    /// Opens the search popup with a pre-filled query and immediately executes the search.
    /// Used by search history items in the widget.
    /// </summary>
    public void OpenSearchPopupWithQuery(string query)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => OpenSearchPopupWithQuery(query));
            return;
        }

        OpenSearchPopupCore(query);
    }

    private void OpenSearchPopupCore(string? initialQuery)
    {
        EnsureSearchServices();
        if (_searchEngineService is null)
        {
            return;
        }

        CancelBackgroundMemoryCleanup();

        if (_searchPopupWindow is null)
        {
            CreateSearchPopupWindow();
        }

        if (_searchPopupWindow is not { } popup)
        {
            return;
        }

        PerformanceLogger.Mark(
            "SearchPopupOpenRequested",
            $"shellReady=true visible={popup.IsPopupVisible} " +
            $"everythingState={_everythingSearchService?.CurrentSnapshot.State}");

        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            popup.ShowPopup();
        }
        else
        {
            popup.ShowPopupWithQuery(initialQuery);
        }

        // Everything is queried only after the user types and has explicitly enabled it.
    }

    private IReadOnlyList<IntPtr> GetRaisedBandAuxiliaryWindowHandles()
    {
        List<IntPtr> handles = new(3);
        if (GetVisibleSettingsWindowHandle() is var settings && settings != IntPtr.Zero)
        {
            handles.Add(settings);
        }

        if (_clipboardQuickPasteWindow is { IsPopupVisible: true } clipPopup)
        {
            handles.Add(clipPopup.WindowHandle);
        }
        if (_searchPopupWindow is { IsPopupVisible: true } popup)
        {
            handles.Add(popup.WindowHandle);
        }

        if (_desktopOrganizationWindow is { } organize)
        {
            handles.Add(organize.WindowHandle);
        }

        if (_onboardingWindow is { } onboarding)
        {
            IntPtr onboardingHwnd = WinRT.Interop.WindowNative.GetWindowHandle(onboarding);
            if (onboardingHwnd != IntPtr.Zero)
            {
                handles.Add(onboardingHwnd);
            }
        }

        if (GetStudioHostWindowHandle() is var studioHostHwnd && studioHostHwnd != IntPtr.Zero)
        {
            handles.Add(studioHostHwnd);
        }

        AppendQuickCapturePopoutWindowHandles(handles);

        return handles;
    }

    private void CreateSearchPopupWindow()
    {
        if (_searchEngineService is null || _searchHistoryService is null)
        {
            return;
        }

        EnsureXamlLoopLifelineIfNoOtherWindows();
        var stopwatch = Stopwatch.StartNew();
        _fileMetaService?.Dispose();
        _fileMetaService = new FileMetaService();
        var viewModel = new ViewModels.SearchPopupViewModel(
            _searchEngineService,
            SettingsService,
            LocalizationService,
            _searchHistoryService,
            _fileMetaService);
        var popup = new SearchPopupWindow(viewModel, SettingsService, LocalizationService);
        _searchPopupWindow = popup;
        EventHandler popupShownHandler = (_, _) => CancelBackgroundMemoryCleanup();
        EventHandler popupHiddenHandler = (_, _) =>
        {
            NotifyMemoryCleanupActivity();
            ScheduleBackgroundMemoryCleanup("search-popup-hidden");
        };
        EventHandler popupDeactivatedHandler = (_, _) =>
        {
            NotifyMemoryCleanupActivity();
            ScheduleBackgroundMemoryCleanup("search-popup-deactivated");
        };
        popup.ActionRequested += OnSearchActionRequested;
        popup.ContentRequested += OnSearchContentRequested;
        popup.PopupShown += popupShownHandler;
        popup.PopupHidden += popupHiddenHandler;
        popup.PopupDeactivated += popupDeactivatedHandler;
        SubscribeWindowClosedOnce(popup, () =>
        {
            popup.ActionRequested -= OnSearchActionRequested;
            popup.ContentRequested -= OnSearchContentRequested;
            popup.PopupShown -= popupShownHandler;
            popup.PopupHidden -= popupHiddenHandler;
            popup.PopupDeactivated -= popupDeactivatedHandler;
            if (ReferenceEquals(_searchPopupWindow, popup))
            {
                _searchPopupWindow = null;
                _searchEngineService?.Dispose();
                _searchEngineService = null;
                _everythingSearchService = null;
                _searchActionService = null;
                _searchSettings?.OnRuntimeChanged();
                Log("[Search] Popup shell and search engine discarded; next open will recreate them");
            }

            _fileMetaService?.Dispose();
            _fileMetaService = null;
            PerformanceLogger.SampleMemory("search-popup-closed");
            // Closing a transient popup already detaches its visual tree. Do not
            // promote that normal release into a compacting GC while visible
            // widgets are still warm.
            ScheduleLightMemoryCleanup();
            NotifyMemoryCleanupActivity();
            ScheduleBackgroundMemoryCleanup("search-popup-closed");
        });
        viewModel.HidePopupCallback = () => popup.HidePopup();
        stopwatch.Stop();
        Log($"[Search] Popup shell created in {stopwatch.ElapsedMilliseconds} ms");
        PerformanceLogger.SampleMemory("search-popup-created");
    }

    private void OnSearchActionRequested(object? sender, string actionId)
    {
        _ = HandleSearchActionAsync(actionId);
    }

    private void OnSearchContentRequested(object? sender, Models.SearchResultItem item)
    {
        _ = HandleSearchContentAsync(item);
    }

    private async Task HandleSearchContentAsync(Models.SearchResultItem item)
    {
        if (WidgetManager is null)
        {
            return;
        }

        switch (item.Kind)
        {
            case Models.SearchResultKind.Todo:
                await WidgetManager.ShowTodoReminderTargetAsync(
                    item.TodoWidgetId,
                    item.TodoItemId,
                    preferTodayFilter: false);
                break;

            case Models.SearchResultKind.QuickCapture:
                var window = await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                await window.RevealItemAsync(item.QuickCaptureItemId);
                break;

            case Models.SearchResultKind.CustomWidget:
                await WidgetManager.ShowCustomWidgetTargetAsync(item.CustomWidgetId);
                break;
        }
    }

    private async Task HandleSearchActionAsync(string actionId)
    {
        switch (actionId)
        {
            case "new-todo":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateTodoWidgetAsync(focusNewInput: true);
                }
                break;

            case "new-note":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateOrShowQuickCaptureWidgetAsync(focusNewInput: true);
                }
                break;

            case "open-settings":
                ShowSettings("SearchSettings");
                break;

            case "toggle-widgets":
                await ToggleTrayWidgetsAsync("action-command");
                break;

            case "toggle-theme":
                ToggleTheme();
                break;

            case "open-todo":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateTodoWidgetAsync();
                }
                break;

            case "open-quickcapture":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                }
                break;

            default:
                await HandleQuickCaptureSearchActionAsync(actionId);
                break;
        }
    }

    internal async Task ExecuteSearchActionFromWidgetAsync(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => _ = ExecuteSearchActionFromWidgetAsync(actionId));
            return;
        }

        await HandleSearchActionAsync(actionId);
    }

    internal async Task OpenSearchRecommendationFromWidgetAsync(Models.SearchRecommendationItem recommendation)
    {
        if (recommendation is null)
        {
            return;
        }

        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => _ = OpenSearchRecommendationFromWidgetAsync(recommendation));
            return;
        }

        var item = new Models.SearchResultItem
        {
            Kind = recommendation.Kind,
            Title = recommendation.Title,
            Subtitle = recommendation.Subtitle,
            Glyph = recommendation.Glyph,
            DetailPath = recommendation.DetailPath,
            ActionId = recommendation.ActionId,
            TodoWidgetId = recommendation.TodoWidgetId,
            TodoItemId = recommendation.TodoItemId,
            QuickCaptureItemId = recommendation.QuickCaptureItemId,
            CustomWidgetId = recommendation.CustomWidgetId,
            CustomWidgetItemId = recommendation.CustomWidgetItemId
        };

        switch (recommendation.Kind)
        {
            case Models.SearchResultKind.Todo:
            case Models.SearchResultKind.QuickCapture:
            case Models.SearchResultKind.CustomWidget:
                await HandleSearchContentAsync(item);
                if (SettingsService.Settings.Search.SearchSaveHistory)
                {
                    EnsureSearchHistoryService()?.RecordResult(item);
                }
                break;

            case Models.SearchResultKind.Action when !string.IsNullOrWhiteSpace(recommendation.ActionId):
                await HandleSearchActionAsync(recommendation.ActionId);
                break;

            case Models.SearchResultKind.File:
            case Models.SearchResultKind.Folder:
                if (!string.IsNullOrWhiteSpace(recommendation.DetailPath))
                {
                    DeskBox.Services.ShellLaunchService.FireAndForgetOpenPath(recommendation.DetailPath);
                    if (SettingsService.Settings.Search.SearchSaveHistory)
                    {
                        EnsureSearchHistoryService()?.RecordResult(item);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(recommendation.Title))
                {
                    OpenSearchPopupWithQuery(recommendation.Title);
                }
                break;

            default:
                string fallbackQuery = !string.IsNullOrWhiteSpace(recommendation.HistoryQuery)
                    ? recommendation.HistoryQuery
                    : recommendation.Title;
                if (!string.IsNullOrWhiteSpace(fallbackQuery))
                {
                    OpenSearchPopupWithQuery(fallbackQuery);
                }
                else
                {
                    OpenSearchPopup();
                }
                break;
        }
    }

    internal SearchWidgetHostCallbacks CreateSearchWidgetHostCallbacks(SettingsService settingsService)
    {
        SearchHistoryService? historyService = EnsureSearchHistoryService();

        return new SearchWidgetHostCallbacks(
            HistoryService: historyService,
            IsEverythingConnected: () =>
                TryGetActiveEverythingSearchService()?.CurrentSnapshot.State == Models.EverythingConnectionState.Connected,
            SubscribeEverythingConnectionChanged: handler =>
            {
                EverythingSearchService? service = TryGetActiveEverythingSearchService();
                if (service is null)
                {
                    return null;
                }

                void OnConnectionChanged(DeskBox.Contracts.EverythingConnectionSnapshot snapshot)
                {
                    if (UiDispatcherQueue is { HasThreadAccess: false } dq)
                    {
                        dq.TryEnqueue(() => handler());
                        return;
                    }

                    handler();
                }

                service.ConnectionChanged += OnConnectionChanged;
                return new SearchWidgetSubscriptionHandle(() => service.ConnectionChanged -= OnConnectionChanged);
            },
            ProbeEverythingConnectionAsync: async ct =>
            {
                if (!settingsService.Settings.Search.SearchEverythingEnabled)
                {
                    return;
                }

                EverythingSearchService? service = TryGetActiveEverythingSearchService();
                if (service is null)
                {
                    var dq = UiDispatcherQueue;
                    if (dq is null || dq.HasThreadAccess)
                    {
                        service = EnsureEverythingSearchServiceForIpc();
                    }
                    else
                    {
                        var tcs = new TaskCompletionSource<EverythingSearchService?>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        if (dq.TryEnqueue(() =>
                        {
                            try
                            {
                                tcs.TrySetResult(EnsureEverythingSearchServiceForIpc());
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetException(ex);
                            }
                        }))
                        {
                            service = await tcs.Task.ConfigureAwait(false);
                        }
                    }
                }

                if (service is not null &&
                    service.CurrentSnapshot.State == Models.EverythingConnectionState.Unknown)
                {
                    await service.RefreshConnectionAsync(allowIpcProbe: true, ct).ConfigureAwait(false);
                }
            },
            SubscribePreferencesChanged: handler =>
            {
                SearchSettingsCoordinator? coordinator = SearchSettingsCoordinator;
                if (coordinator is null)
                {
                    return null;
                }

                coordinator.PreferencesChanged += handler;
                return new SearchWidgetSubscriptionHandle(() => coordinator.PreferencesChanged -= handler);
            },
            OpenSearchPopup: OpenSearchPopup,
            OpenSearchPopupWithQuery: OpenSearchPopupWithQuery,
            ExecuteSearchAction: ExecuteSearchActionFromWidgetAsync,
            OpenRecommendation: OpenSearchRecommendationFromWidgetAsync,
            OpenSettings: sectionTag => ShowSettings(sectionTag),
            CopyText: static text =>
            {
                try
                {
                    var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    pkg.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                }
                catch
                {
                    // Ignore transient clipboard lock errors on copy feedback.
                }
            },
            SetPrivacyMaskPreference: masked =>
            {
                if (SearchSettingsCoordinator is { } coordinator)
                {
                    coordinator.UpdatePreferences(
                        new DeskBox.Contracts.SearchPreferenceChange(DesktopPrivacyMask: masked));
                }
                else
                {
                    settingsService.Settings.Search.SearchDesktopPrivacyMask = masked;
                    settingsService.SaveDebounced();
                }
            },
            UiDispatch: action =>
            {
                var dq = UiDispatcherQueue;
                if (dq is null || dq.HasThreadAccess)
                {
                    action();
                }
                else
                {
                    dq.TryEnqueue(() => action());
                }
            });
    }

    private sealed class SearchWidgetSubscriptionHandle(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
