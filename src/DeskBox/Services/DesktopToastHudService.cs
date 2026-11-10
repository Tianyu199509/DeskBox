using System.Text;
using DeskBox.Platform;
using DeskBox.Views;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

internal enum DesktopToastSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
}

internal enum DesktopToastPriority
{
    Routine = 0,
    ContextualGuide = 1,
    SafetyWarning = 2,
}

internal sealed record DesktopToastRequest(
    string HintId,
    string Message,
    DesktopToastSeverity Severity = DesktopToastSeverity.Info,
    DesktopToastPriority Priority = DesktopToastPriority.Routine,
    string? ActionButtonText = null,
    Func<Task>? OnActionAsync = null,
    Func<Task>? OnDismissAsync = null,
    bool PersistOnceOnShow = false,
    bool PersistDismissOnClose = false,
    bool ShowDismissButton = true,
    int DurationMs = 4500);

/// <summary>
/// Coordinates global desktop bottom-center pill notifications for drag-and-drop
/// interactions, handling priority preemption, deferred UI-thread dispatch
/// outside OLE callbacks, and lightweight text-based dismissal persistence.
/// </summary>
internal sealed class DesktopToastHudService
{
    internal const string DismissedHintsFileName = "dismissed-drag-hints.txt";

    internal const string HintIdWin10ExplorerMenu = "win10-explorer-menu-guide";
    internal const string HintIdWin10DefaultCopy = "win10-default-copy-guide";
    internal const string HintIdInboundExternalCopy = "inbound-external-copy-protected";
    internal const string HintIdInboundCrossVolumeDesktopCopy = "inbound-cross-volume-desktop-copy";
    internal const string HintIdWin11CrossVolumeCopy = "win11-cross-volume-desktop-copy";
    internal const string HintIdWin10MoveRejected = "win10-move-rejected-by-app";
    internal const string HintIdWin10MoveRemovedWarning = "win10-move-removed-warning";

    public static DesktopToastHudService Shared { get; } = new();

    private readonly record struct DismissedCacheEntry(
        HashSet<string> Ids,
        bool FileExisted,
        long LastWriteUtcTicks,
        long FileLength);

    private readonly object _gate = new();
    private readonly Dictionary<string, DismissedCacheEntry> _dismissedByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    private DesktopToastHudWindow? _window;
    private Action<string>? _openSettingsSection;
    private Func<ElementTheme?>? _resolveEffectiveTheme;
    private DesktopToastPriority? _activePriority;
    private long _activeExpiresTick64;
    private long _dispatchGeneration;
    private long _activeHudWindowHandle;

    internal DesktopToastRequest? LastEvaluatedRequestForTests { get; private set; }

    internal IntPtr ActiveHudWindowHandle => new(Volatile.Read(ref _activeHudWindowHandle));

    internal void SetActiveHudWindowHandle(IntPtr hwnd) =>
        Volatile.Write(ref _activeHudWindowHandle, hwnd.ToInt64());

    public void ConfigureOpenSettings(
        Action<string> openSettingsSection,
        Func<ElementTheme?>? resolveEffectiveTheme = null)
    {
        _openSettingsSection = openSettingsSection;
        _resolveEffectiveTheme = resolveEffectiveTheme;
    }

    public bool TryShow(
        DesktopToastRequest request,
        SettingsService? settingsService = null,
        string? dataDirectoryOverride = null)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return false;
        }

        string dataDir = ResolveDataDirectory(settingsService, dataDirectoryOverride);

        // Safety warnings are never suppressed by prior dismissals.
        if (request.Priority != DesktopToastPriority.SafetyWarning &&
            !string.IsNullOrWhiteSpace(request.HintId) &&
            IsHintDismissed(request.HintId, dataDir))
        {
            return false;
        }

        long now = Environment.TickCount64;
        long generation;
        lock (_gate)
        {
            bool windowHovered = _window is { IsShown: true, IsPointerHovered: true };
            if (_activePriority is { } currentPriority &&
                (now < _activeExpiresTick64 || windowHovered) &&
                request.Priority < currentPriority)
            {
                return false;
            }

            _activePriority = request.Priority;
            _activeExpiresTick64 = now + Math.Clamp(request.DurationMs, 1800, 12_000);
            generation = ++_dispatchGeneration;
        }

        if (request.PersistOnceOnShow && !string.IsNullOrWhiteSpace(request.HintId))
        {
            RecordHintDismissed(request.HintId, dataDir);
        }

        Func<Task>? wrappedDismiss = request.OnDismissAsync;
        if (request.PersistDismissOnClose && !string.IsNullOrWhiteSpace(request.HintId))
        {
            string hintId = request.HintId;
            Func<Task>? innerDismiss = request.OnDismissAsync;
            wrappedDismiss = async () =>
            {
                RecordHintDismissed(hintId, dataDir);
                if (innerDismiss is not null)
                {
                    await innerDismiss();
                }
            };
        }

        DesktopToastRequest effectiveRequest = ReferenceEquals(wrappedDismiss, request.OnDismissAsync)
            ? request
            : request with { OnDismissAsync = wrappedDismiss };

        // Retain metadata only so unit tests can inspect the last request without
        // rooting live view/service closures on the process-wide singleton.
        LastEvaluatedRequestForTests = effectiveRequest with
        {
            OnActionAsync = null,
            OnDismissAsync = null,
        };
        // Headless test hosts never run App.OnLaunched / ConfigureOpenSettings:
        // do not enqueue a XAML window callback onto UiDispatch (which would
        // interfere with UiDispatchTests or touch uninitialized WinUI XAML).
        if (_openSettingsSection is null)
        {
            return true;
        }

        bool preferLight = ResolvePreferLightTheme(settingsService);

        UiDispatch.PostAfterCallback(() =>
        {
            lock (_gate)
            {
                if (_dispatchGeneration != generation)
                {
                    return;
                }
            }

            if (App.IsShuttingDown || _openSettingsSection is null)
            {
                return;
            }

            try
            {
                _window ??= new DesktopToastHudWindow();
                _window.ShowToast(effectiveRequest, preferLight);
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Failed to show toast '{effectiveRequest.HintId}': {ex.Message}");
            }
        });

        return true;
    }

    public void Hide()
    {
        long generation;
        lock (_gate)
        {
            _activePriority = null;
            _activeExpiresTick64 = 0;
            generation = ++_dispatchGeneration;
        }

        if (_window is null)
        {
            return;
        }

        UiDispatch.PostAfterCallback(() =>
        {
            lock (_gate)
            {
                if (_dispatchGeneration != generation)
                {
                    return;
                }
            }

            try
            {
                _window?.HideImmediate();
            }
            catch
            {
            }
        });
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            _activePriority = null;
            _activeExpiresTick64 = 0;
            ++_dispatchGeneration;
        }

        SetActiveHudWindowHandle(IntPtr.Zero);
        DesktopToastHudWindow? window = _window;
        _window = null;
        if (window is not null)
        {
            try
            {
                window.Destroy();
            }
            catch
            {
            }
        }
    }

    internal void NotifyWindowHidden()
    {
        SetActiveHudWindowHandle(IntPtr.Zero);
        lock (_gate)
        {
            _activePriority = null;
            _activeExpiresTick64 = 0;
        }
    }

    internal void ResetForTests()
    {
        SetActiveHudWindowHandle(IntPtr.Zero);
        lock (_gate)
        {
            _dismissedByDirectory.Clear();
            _activePriority = null;
            _activeExpiresTick64 = 0;
            ++_dispatchGeneration;
            LastEvaluatedRequestForTests = null;
        }
    }

    public bool IsHintDismissed(string hintId, string? dataDirectoryOverride = null)
    {
        if (string.IsNullOrWhiteSpace(hintId))
        {
            return false;
        }

        string dir = ResolveDataDirectory(null, dataDirectoryOverride);
        lock (_gate)
        {
            HashSet<string> set = GetOrLoadDismissedSetLocked(dir);
            return set.Contains(hintId.Trim());
        }
    }

    public void RecordHintDismissed(string hintId, string? dataDirectoryOverride = null)
    {
        if (string.IsNullOrWhiteSpace(hintId))
        {
            return;
        }

        string dir = ResolveDataDirectory(null, dataDirectoryOverride);
        string normalized = hintId.Trim();
        lock (_gate)
        {
            HashSet<string> set = GetOrLoadDismissedSetLocked(dir);
            if (!set.Add(normalized))
            {
                return;
            }

            PersistDismissedSetLocked(dir, set);
        }
    }

    public static void ClearDismissedHints(string? dataDirectoryOverride = null) =>
        Shared.ClearDismissedHintsCore(dataDirectoryOverride);

    private void ClearDismissedHintsCore(string? dataDirectoryOverride)
    {
        string dir = ResolveDataDirectory(null, dataDirectoryOverride);
        lock (_gate)
        {
            _dismissedByDirectory.Remove(dir);
            DesktopToastHudInterop.DeleteDismissedHintsFile(
                Path.Combine(dir, DismissedHintsFileName));
        }
    }

    internal const string DragBehaviorSettingsDeepLink =
        "AppearanceDetail@Settings.DragOutAction.Title";

    public static Task OpenDragBehaviorSettingsAsync()
    {
        UiDispatch.PostAfterCallback(() =>
        {
            try
            {
                Shared._openSettingsSection?.Invoke(DragBehaviorSettingsDeepLink);
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Failed to open drag settings: {ex.Message}");
            }
        });
        return Task.CompletedTask;
    }

    public static async Task SwitchDragOutActionAsync(
        SettingsService settingsService,
        string action)
    {
        if (string.Equals(
                settingsService.Settings.FileWidget.ManagedDragOutAction,
                action,
                StringComparison.Ordinal))
        {
            return;
        }

        settingsService.Settings.FileWidget.ManagedDragOutAction = action;
        await settingsService.SaveAsync();
    }

    public static async Task SwitchDropActionAsync(
        SettingsService settingsService,
        string action)
    {
        if (string.Equals(
                settingsService.Settings.FileWidget.ManagedDropAction,
                action,
                StringComparison.Ordinal))
        {
            return;
        }

        settingsService.Settings.FileWidget.ManagedDropAction = action;
        await settingsService.SaveAsync();
    }

    private HashSet<string> GetOrLoadDismissedSetLocked(string dataDirectory)
    {
        string filePath = Path.Combine(dataDirectory, DismissedHintsFileName);
        (bool exists, long lastWriteUtcTicks, long length) =
            DesktopToastHudInterop.GetDismissedHintsFileStamp(filePath);
        if (_dismissedByDirectory.TryGetValue(dataDirectory, out DismissedCacheEntry existing) &&
            existing.FileExisted == exists &&
            existing.LastWriteUtcTicks == lastWriteUtcTicks &&
            existing.FileLength == length)
        {
            return existing.Ids;
        }

        HashSet<string> loaded = exists
            ? DesktopToastHudInterop.ReadDismissedHintsFile(filePath)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _dismissedByDirectory[dataDirectory] = new DismissedCacheEntry(
            loaded,
            exists,
            lastWriteUtcTicks,
            length);
        return loaded;
    }

    private void PersistDismissedSetLocked(string dataDirectory, HashSet<string> items)
    {
        string filePath = Path.Combine(dataDirectory, DismissedHintsFileName);
        DesktopToastHudInterop.WriteDismissedHintsFile(
            dataDirectory,
            DismissedHintsFileName,
            items.OrderBy(static id => id, StringComparer.Ordinal));
        (bool exists, long lastWriteUtcTicks, long length) =
            DesktopToastHudInterop.GetDismissedHintsFileStamp(filePath);
        _dismissedByDirectory[dataDirectory] = new DismissedCacheEntry(
            items,
            exists,
            lastWriteUtcTicks,
            length);
    }

    private static string ResolveDataDirectory(
        SettingsService? settingsService,
        string? dataDirectoryOverride)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
        {
            return Path.GetFullPath(dataDirectoryOverride);
        }

        if (settingsService is not null &&
            !string.IsNullOrWhiteSpace(settingsService.DataDirectoryPath))
        {
            return Path.GetFullPath(settingsService.DataDirectoryPath);
        }

        return Path.GetFullPath(DeskBoxDataPathService.Current.DataDirectory);
    }

    private bool ResolvePreferLightTheme(SettingsService? settingsService)
    {
        string? theme = settingsService?.Settings.Core.Theme;
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            ElementTheme? effectiveTheme = _resolveEffectiveTheme?.Invoke();
            if (effectiveTheme == ElementTheme.Light)
            {
                return true;
            }

            if (effectiveTheme == ElementTheme.Dark)
            {
                return false;
            }

            return !Win32Helper.IsSystemDarkMode();
        }
        catch
        {
            return false;
        }
    }
}
