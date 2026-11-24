using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Dependency bag injected from <see cref="SearchWidgetContentProvider"/> into
/// <see cref="DeskBox.Controls.WidgetContents.SearchWidgetContentAdapter"/> so the
/// UI control layer remains free of direct global application or dispatcher lookups.
/// </summary>
public sealed record SearchWidgetHostCallbacks(
    SearchHistoryService? HistoryService = null,
    Func<bool>? IsEverythingConnected = null,
    Func<Action, IDisposable?>? SubscribeEverythingConnectionChanged = null,
    Func<CancellationToken, Task>? ProbeEverythingConnectionAsync = null,
    Func<Action, IDisposable?>? SubscribePreferencesChanged = null,
    Action? OpenSearchPopup = null,
    Action<string>? OpenSearchPopupWithQuery = null,
    Func<string, Task>? ExecuteSearchAction = null,
    Func<SearchRecommendationItem, Task>? OpenRecommendation = null,
    Action<string>? OpenSettings = null,
    Action<string>? CopyText = null,
    Action<bool>? SetPrivacyMaskPreference = null,
    Action<Action>? UiDispatch = null);
