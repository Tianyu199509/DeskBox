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
    int DurationMs = 4500,
    bool KeepVisibleDuringDrag = false);

/// <summary>
/// Authoritative lifetime ledger for one active toast, owned by the service.
/// The window performs no lifetime arithmetic of its own: every timer remount
/// value it uses is computed here and handed back through the report APIs,
/// eliminating the two-clock drift between the window timer and the service
/// priority lease.
/// </summary>
internal readonly record struct ToastLifetimeState(
    long StartedAtTick64,
    int BudgetMs,
    long PausedAccumulatedMs,
    long? PausedSinceTick64,
    long HardCapDeadlineTick64)
{
    public static ToastLifetimeState Start(long nowTick64, int budgetMs, int hardCapMs) => new(
        nowTick64,
        budgetMs,
        PausedAccumulatedMs: 0,
        PausedSinceTick64: null,
        HardCapDeadlineTick64: nowTick64 + hardCapMs);
}

/// <summary>Pure functions over the lifetime ledger; nowTick64 is injected.</summary>
internal static class ToastLifetimeMath
{
    /// <summary>
    /// Active (non-drag-frozen) elapsed time. Hovering burns the budget — the
    /// user is reading, and the lease stays alive through the window's hover
    /// probe instead of the clock.
    /// </summary>
    public static long ElapsedActiveMs(in ToastLifetimeState state, long nowTick64)
    {
        long pausedExtra = state.PausedSinceTick64 is { } since
            ? Math.Max(0, nowTick64 - since)
            : 0;
        long wallElapsed = Math.Max(0, nowTick64 - state.StartedAtTick64);
        return Math.Max(0, wallElapsed - state.PausedAccumulatedMs - pausedExtra);
    }

    public static int RemainingMs(in ToastLifetimeState state, long nowTick64) =>
        (int)Math.Max(0, state.BudgetMs - ElapsedActiveMs(state, nowTick64));

    /// <summary>Idempotent drag freeze: repeated reports never double-count.</summary>
    public static ToastLifetimeState PauseForDrag(in ToastLifetimeState state, long nowTick64) =>
        state.PausedSinceTick64 is null
            ? state with { PausedSinceTick64 = nowTick64 }
            : state;

    /// <summary>Idempotent drag resume: accumulates the frozen span once.</summary>
    public static ToastLifetimeState ResumeFromDrag(in ToastLifetimeState state, long nowTick64)
    {
        if (state.PausedSinceTick64 is not { } since)
        {
            return state;
        }

        return state with
        {
            PausedSinceTick64 = null,
            PausedAccumulatedMs = state.PausedAccumulatedMs + Math.Max(0, nowTick64 - since),
        };
    }

    /// <summary>
    /// Hover-exit remount: never shorter than the remaining budget (fixes
    /// early-exit truncation), never shorter than the fixed grace (reading
    /// departure tail), bounded by the budget itself.
    /// </summary>
    public static int HoverExitRemountMs(in ToastLifetimeState state, long nowTick64, int graceMs) =>
        Math.Min(state.BudgetMs, Math.Max(RemainingMs(state, nowTick64), graceMs));

    /// <summary>
    /// Authoritative hover-exit transition: computes the remount duration and
    /// rebases the active budget to cover the departure tail so the service
    /// priority lease (<see cref="IsLeaseAlive"/>) stays alive until the
    /// remounted timer expires, while preserving <see cref="ToastLifetimeState.HardCapDeadlineTick64"/>.
    /// </summary>
    public static ToastLifetimeState ResumeFromHover(
        in ToastLifetimeState state,
        long nowTick64,
        int graceMs,
        out int remountMs)
    {
        remountMs = HoverExitRemountMs(state, nowTick64, graceMs);
        return state with
        {
            StartedAtTick64 = nowTick64,
            BudgetMs = remountMs,
            PausedAccumulatedMs = 0,
            PausedSinceTick64 = null,
        };
    }

    public static bool IsHardCapped(in ToastLifetimeState state, long nowTick64) =>
        nowTick64 >= state.HardCapDeadlineTick64;

    /// <summary>
    /// Priority-gate predicate: the lease lives while budget (plus the
    /// dispatcher-hop pad, pause aware) remains, or while the window reports
    /// the pointer hovering. The pad extends the deadline; it must NOT be a
    /// constant offset on a clamped remaining value — that would keep the
    /// lease alive forever after the budget empties.
    /// </summary>
    public static bool IsLeaseAlive(
        in ToastLifetimeState state,
        long nowTick64,
        bool windowHovered,
        int leasePadMs) =>
        state.BudgetMs + leasePadMs - ElapsedActiveMs(state, nowTick64) > 0 || windowHovered;
}

/// <summary>
/// Immutable per-show spec handed to the window alongside the request.
/// </summary>
internal readonly record struct ToastLifetimeSpec(
    long Generation,
    string HintId,
    int BudgetMs,
    bool KeepVisibleDuringDrag,
    long HardCapDeadlineTick64,
    int HoverExitGraceMs);

/// <summary>
/// Coordinates global desktop bottom-center pill notifications for drag-and-drop
/// interactions, handling priority preemption, deferred UI-thread dispatch
/// outside OLE callbacks, and lightweight text-based dismissal persistence.
/// </summary>
internal sealed class DesktopToastHudService
{
    internal const string DismissedHintsFileName = "dismissed-drag-hints.txt";

    /// <summary>
    /// Uniform on-screen time for every HUD toast. Per-request durations are
    /// deliberately normalized away (2026-11-12 product call): all hints —
    /// plain receipts and action-bearing guides alike — show for 5 seconds.
    /// </summary>
    internal const int StandardHintDurationMs = 5000;

    internal static int ResolveEffectiveDurationMs(DesktopToastRequest request) =>
        StandardHintDurationMs;

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

    /// <summary>
    /// Lease padding for the TryShow-to-ShowToast dispatcher hop, so a visible
    /// toast is never displaced by its own lease expiry mid-flight.
    /// </summary>
    internal const int LeasePadMs = 250;

    /// <summary>Fixed departure grace after a hover exit, kept from the original design.</summary>
    internal const int HoverExitGraceMs = 2200;

    /// <summary>
    /// Total wall-clock ceiling per toast including all drag freezes. Bounds
    /// the pathological freeze source (a lost drag registry entry lives 10
    /// minutes) to 1/10th of that; ordinary interactive sessions never reach it.
    /// </summary>
    internal const int HardCapMs = 60_000;

    private DesktopToastHudWindow? _window;
    private Action<string>? _openSettingsSection;
    private Func<ElementTheme?>? _resolveEffectiveTheme;
    private DesktopToastPriority? _activePriority;
    private string? _activeHintId;
    private ToastLifetimeState _activeLifetime;
    private bool _activeKeepVisibleDuringDrag;
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
        ToastLifetimeSpec spec;
        lock (_gate)
        {
            bool windowHovered = _window is { IsShown: true, IsPointerHovered: true };
            bool leaseAlive = _activePriority is { } &&
                ToastLifetimeMath.IsLeaseAlive(_activeLifetime, now, windowHovered, LeasePadMs);
            if (_activePriority is { } currentPriority &&
                leaseAlive &&
                request.Priority < currentPriority)
            {
                return false;
            }

            _activePriority = request.Priority;
            _activeHintId = request.HintId;
            _activeKeepVisibleDuringDrag = request.KeepVisibleDuringDrag ||
                request.Priority == DesktopToastPriority.SafetyWarning;
            _activeLifetime = ToastLifetimeState.Start(
                now,
                ResolveEffectiveDurationMs(request),
                HardCapMs);
            generation = ++_dispatchGeneration;
            spec = new ToastLifetimeSpec(
                generation,
                request.HintId,
                _activeLifetime.BudgetMs,
                _activeKeepVisibleDuringDrag,
                _activeLifetime.HardCapDeadlineTick64,
                HoverExitGraceMs);
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
            if (request.PersistOnceOnShow && !string.IsNullOrWhiteSpace(request.HintId))
            {
                RecordHintDismissed(request.HintId, dataDir);
            }

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
                // A reused HUD window belongs to the virtual desktop it was
                // created on; after the user switches away, the shell cloaks
                // it and every toast would be invisible. Recreate on the
                // (now-active) desktop — same repair as the tray-owner
                // precedent (issue 489 family).
                if (_window is { } existing &&
                    existing.WindowHandle != IntPtr.Zero &&
                    Win32Helper.IsWindowShellCloaked(existing.WindowHandle))
                {
                    App.Log($"[DragHud] HUD window stranded on another virtual desktop; recreating for hint '{effectiveRequest.HintId}'");
                    try
                    {
                        existing.Destroy();
                    }
                    catch
                    {
                    }

                    _window = null;
                }

                _window ??= new DesktopToastHudWindow();
                _window.ShowToast(effectiveRequest, preferLight, spec);
                if (effectiveRequest.PersistOnceOnShow && !string.IsNullOrWhiteSpace(effectiveRequest.HintId))
                {
                    RecordHintDismissed(effectiveRequest.HintId, dataDir);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Failed to show toast '{effectiveRequest.HintId}': {ex.Message}");
            }
        });

        return true;
    }

    public void HideIfMatchingHint(string hintId)
    {
        if (string.IsNullOrWhiteSpace(hintId))
        {
            return;
        }

        // Atomic match-and-clear: checking the id under the lock and hiding
        // outside it (the old shape) let a TryShow slip between the two and a
        // stale hide kill the brand-new toast.
        long generation;
        lock (_gate)
        {
            if (!string.Equals(_activeHintId, hintId, StringComparison.Ordinal))
            {
                return;
            }

            _activePriority = null;
            _activeHintId = null;
            _activeLifetime = default;
            generation = ++_dispatchGeneration;
        }

        PostWindowHide(generation);
    }

    public void Hide()
    {
        long generation;
        lock (_gate)
        {
            _activePriority = null;
            _activeHintId = null;
            _activeLifetime = default;
            generation = ++_dispatchGeneration;
        }

        PostWindowHide(generation);
    }

    private void PostWindowHide(long generation)
    {
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
            _activeHintId = null;
            _activeLifetime = default;
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

    /// <summary>
    /// Window-side edge report: a KeepVisibleDuringDrag toast froze for an
    /// active drag. Idempotent; stale generation/hint pairs are ignored so a
    /// dying toast's callback can never freeze its successor's ledger.
    /// </summary>
    internal void ReportDragPaused(long generation, string hintId)
    {
        lock (_gate)
        {
            if (_dispatchGeneration != generation ||
                !string.Equals(_activeHintId, hintId, StringComparison.Ordinal))
            {
                return;
            }

            _activeLifetime = ToastLifetimeMath.PauseForDrag(
                _activeLifetime,
                Environment.TickCount64);
        }
    }

    /// <summary>
    /// Window-side edge report: the drag freeze ended. Returns the
    /// authoritative remaining budget the window must remount its timer with
    /// (not the full budget), or 0 for a stale/replaced toast.
    /// </summary>
    internal int ReportDragResumed(long generation, string hintId)
    {
        lock (_gate)
        {
            if (_dispatchGeneration != generation ||
                !string.Equals(_activeHintId, hintId, StringComparison.Ordinal))
            {
                return 0;
            }

            long now = Environment.TickCount64;
            _activeLifetime = ToastLifetimeMath.ResumeFromDrag(_activeLifetime, now);
            return ToastLifetimeMath.RemainingMs(_activeLifetime, now);
        }
    }

    /// <summary>
    /// Window-side hover-exit report. Returns the authoritative remount value
    /// (max of remaining budget and the fixed grace) and extends the priority
    /// lease to cover it, so a reader is never displaced by a lower-priority
    /// toast during the grace tail.
    /// </summary>
    internal int ReportPointerExited(long generation, string hintId)
    {
        lock (_gate)
        {
            if (_dispatchGeneration != generation ||
                !string.Equals(_activeHintId, hintId, StringComparison.Ordinal))
            {
                return 0;
            }

            long now = Environment.TickCount64;
            _activeLifetime = ToastLifetimeMath.ResumeFromHover(
                _activeLifetime,
                now,
                HoverExitGraceMs,
                out int remountMs);
            return remountMs;
        }
    }

    internal void NotifyWindowHidden(long generation = 0, string hintId = "")
    {
        SetActiveHudWindowHandle(IntPtr.Zero);
        lock (_gate)
        {
            // generation 0 = legacy unconditional clear (test hosts and any
            // pre-spec callers); real generations start at 1.
            if (generation != 0 &&
                (_dispatchGeneration != generation ||
                 !string.Equals(_activeHintId, hintId, StringComparison.Ordinal)))
            {
                return;
            }

            _activePriority = null;
            _activeHintId = null;
            _activeLifetime = default;
        }
    }

    internal void ResetForTests()
    {
        SetActiveHudWindowHandle(IntPtr.Zero);
        lock (_gate)
        {
            _dismissedByDirectory.Clear();
            _activePriority = null;
            _activeHintId = null;
            _activeLifetime = default;
            _activeKeepVisibleDuringDrag = false;
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
        "AppearanceDetail@Settings.DragBehavior.Group.Title";
    internal const string DropBehaviorSettingsDeepLink =
        "AppearanceDetail@Settings.DragBehavior.Group.Title";

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

    public static Task OpenDropBehaviorSettingsAsync()
    {
        UiDispatch.PostAfterCallback(() =>
        {
            try
            {
                Shared._openSettingsSection?.Invoke(DropBehaviorSettingsDeepLink);
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Failed to open drop settings: {ex.Message}");
            }
        });
        return Task.CompletedTask;
    }

    public static Task SwitchDragOutActionAsync(
        SettingsService settingsService,
        string action) =>
        SwitchUnifiedDragBehaviorActionAsync(settingsService, action);

    public static Task SwitchDropActionAsync(
        SettingsService settingsService,
        string action) =>
        SwitchUnifiedDragBehaviorActionAsync(settingsService, action);

    private static async Task SwitchUnifiedDragBehaviorActionAsync(
        SettingsService settingsService,
        string action)
    {
        var fileWidget = settingsService.Settings.FileWidget;
        if (string.Equals(fileWidget.ManagedDropAction, action, StringComparison.Ordinal) &&
            string.Equals(fileWidget.ManagedDragOutAction, action, StringComparison.Ordinal))
        {
            return;
        }

        fileWidget.ManagedDropAction = action;
        fileWidget.ManagedDragOutAction = action;
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
