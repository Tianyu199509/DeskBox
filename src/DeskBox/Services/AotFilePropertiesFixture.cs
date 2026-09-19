#if DESKBOX_NATIVE_AOT
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

internal static class AotFilePropertiesFixture
{
    internal const string Scenario = "FilePropertiesReadOnly";
    internal const string RunIdEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID";
    internal const string OwnedWidgetId = "aot-5b4c1b2b-file";
    internal const string FixtureDirectoryName = "file-properties";
    internal const string WidgetRootDirectoryName = "widget-root";

    private const uint WmClose = 0x0010;
    private static readonly object s_sync = new();
    private static AotFilePropertiesInvocationState? s_invocation;

    internal static AotFilePropertiesFixturePaths GetOwnedPaths(
        DeskBoxDataPathService dataPaths)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);

        string? scenario = Environment.GetEnvironmentVariable(
            "DESKBOX_AOT_MANAGED_UI_SMOKE");
        string? runId = Environment.GetEnvironmentVariable(
            RunIdEnvironmentVariable);
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!string.Equals(scenario, Scenario, StringComparison.Ordinal) ||
            !IsValidRunId(runId) ||
            !dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !PathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            throw new InvalidOperationException(
                "The owned file Properties fixture is unavailable outside its exact AOT scenario, run identity, and preview root.");
        }

        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        string targetName = $"properties-{runId}.txt";
        string targetPath = Path.GetFullPath(Path.Combine(
            widgetRoot,
            targetName));
        if (!Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !File.Exists(targetPath) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                dataPaths.RootPath,
                fixtureRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                fixtureRoot,
                widgetRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                widgetRoot,
                targetPath))
        {
            throw new InvalidOperationException(
                "The owned file Properties fixture escaped or is missing from the isolated preview root.");
        }

        return new AotFilePropertiesFixturePaths(
            runId!,
            fixtureRoot,
            widgetRoot,
            targetName,
            targetPath);
    }

    internal static bool TryBeginInvocation(
        IntPtr ownerWindowHandle,
        string targetPath)
    {
        if (!IsExactScenario())
        {
            return false;
        }

        AotFilePropertiesFixturePaths paths = GetOwnedPaths(
            DeskBoxDataPathService.Current);
        if (ownerWindowHandle == IntPtr.Zero ||
            !PathsEqual(targetPath, paths.TargetPath))
        {
            throw new InvalidOperationException(
                "The owned file Properties fixture requires the exact target and a non-zero File Widget owner HWND.");
        }

        lock (s_sync)
        {
            if (s_invocation is not null)
            {
                throw new InvalidOperationException(
                    "The owned file Properties fixture permits exactly one product invocation.");
            }

            s_invocation = new AotFilePropertiesInvocationState
            {
                OwnerWindowHandle = ownerWindowHandle.ToInt64(),
                TargetPath = Path.GetFullPath(targetPath),
                StartedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return true;
    }

    internal static void RecordInvocationResult(bool invoked, string? error)
    {
        if (!IsExactScenario())
        {
            return;
        }

        lock (s_sync)
        {
            AotFilePropertiesInvocationState state = s_invocation ??
                throw new InvalidOperationException(
                    "The owned file Properties result has no matching invocation.");
            if (state.ResultRecorded)
            {
                throw new InvalidOperationException(
                    "The owned file Properties result was recorded more than once.");
            }

            state.Invoked = invoked;
            state.Error = error ?? string.Empty;
            state.ResultRecorded = true;
            state.ReturnedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    internal static async Task<AotFilePropertiesInvocationSnapshot>
        WaitForInvocationResultAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (s_sync)
            {
                if (s_invocation is { ResultRecorded: true } state)
                {
                    return state.ToSnapshot();
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The real SHObjectProperties invocation did not return a tracked result.");
    }

    internal static IReadOnlySet<long> CaptureVisibleTopLevelWindowHandles()
    {
        var handles = new HashSet<long>();
        Win32Helper.EnumWindows((windowHandle, _) =>
        {
            if (windowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(windowHandle))
            {
                handles.Add(windowHandle.ToInt64());
            }

            return true;
        }, IntPtr.Zero);
        return handles;
    }

    internal static async Task<AotFilePropertiesDialogSnapshot>
        ObserveAndCloseOwnedDialogAsync(
            IReadOnlySet<long> baselineWindowHandles,
            IntPtr expectedOwnerWindowHandle,
            string targetName,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baselineWindowHandles);
        if (expectedOwnerWindowHandle == IntPtr.Zero ||
            string.IsNullOrWhiteSpace(targetName))
        {
            throw new InvalidOperationException(
                "The file Properties dialog observer requires an owner and target name.");
        }

        for (int attempt = 0; attempt < 400; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AotFilePropertiesWindowCandidate? candidate =
                FindOwnedDialogCandidate(
                    baselineWindowHandles,
                    targetName);
            if (candidate is not null)
            {
                DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
                AotFilePropertiesObservedWindowSnapshot expectedOwner =
                    CaptureObservedWindow(expectedOwnerWindowHandle);
                AotFilePropertiesObservedWindowSnapshot directOwner =
                    CaptureObservedWindow(
                        candidate.DirectOwnerWindowHandle);
                AotFilePropertiesObservedWindowSnapshot rootOwner =
                    CaptureObservedWindow(
                        candidate.RootOwnerWindowHandle);
                bool closePosted = Win32Helper.PostMessage(
                    candidate.WindowHandle,
                    WmClose,
                    UIntPtr.Zero,
                    IntPtr.Zero);
                bool destroyed = false;
                DateTimeOffset? closedAtUtc = null;
                for (int closeAttempt = 0; closeAttempt < 200; closeAttempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Win32Helper.IsWindow(candidate.WindowHandle))
                    {
                        destroyed = true;
                        closedAtUtc = DateTimeOffset.UtcNow;
                        break;
                    }

                    await Task.Delay(50, cancellationToken)
                        .ConfigureAwait(false);
                }

                return new AotFilePropertiesDialogSnapshot(
                    candidate.WindowHandle.ToInt64(),
                    candidate.DirectOwnerWindowHandle.ToInt64(),
                    candidate.RootOwnerWindowHandle.ToInt64(),
                    expectedOwnerWindowHandle.ToInt64(),
                    candidate.WindowThreadId,
                    candidate.ProcessId,
                    candidate.ClassName,
                    candidate.Title,
                    candidate.Visible,
                    closePosted,
                    destroyed,
                    observedAtUtc,
                    closedAtUtc,
                    expectedOwner,
                    directOwner,
                    rootOwner);
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The real system file Properties dialog did not appear for the unique owned target.");
    }

    internal static int CountVisibleMatchingDialogs(string targetName)
    {
        int count = 0;
        Win32Helper.EnumWindows((windowHandle, _) =>
        {
            if (windowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(windowHandle) &&
                string.Equals(
                    GetWindowClassName(windowHandle),
                    "#32770",
                    StringComparison.OrdinalIgnoreCase) &&
                GetWindowTitle(windowHandle).Contains(
                    targetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }

            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static AotFilePropertiesWindowCandidate? FindOwnedDialogCandidate(
        IReadOnlySet<long> baselineWindowHandles,
        string targetName)
    {
        AotFilePropertiesWindowCandidate? match = null;
        Win32Helper.EnumWindows((windowHandle, _) =>
        {
            if (match is not null ||
                windowHandle == IntPtr.Zero ||
                baselineWindowHandles.Contains(windowHandle.ToInt64()) ||
                !Win32Helper.IsWindowVisible(windowHandle))
            {
                return true;
            }

            string className = GetWindowClassName(windowHandle);
            string title = GetWindowTitle(windowHandle);
            if (!string.Equals(
                    className,
                    "#32770",
                    StringComparison.OrdinalIgnoreCase) ||
                !title.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            uint threadId = Win32Helper.GetWindowThreadProcessId(
                windowHandle,
                out uint processId);
            match = new AotFilePropertiesWindowCandidate(
                windowHandle,
                Win32Helper.GetWindow(windowHandle, Win32Helper.GW_OWNER),
                Win32Helper.GetAncestor(windowHandle, Win32Helper.GA_ROOTOWNER),
                threadId,
                processId,
                className,
                title,
                Visible: true);
            return false;
        }, IntPtr.Zero);
        return match;
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(512);
        int length = Win32Helper.GetWindowText(
            windowHandle,
            buffer,
            buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        int length = Win32Helper.GetClassName(
            windowHandle,
            buffer,
            buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static AotFilePropertiesObservedWindowSnapshot
        CaptureObservedWindow(IntPtr windowHandle)
    {
        bool isWindow = windowHandle != IntPtr.Zero &&
            Win32Helper.IsWindow(windowHandle);
        uint processId = 0;
        uint threadId = isWindow
            ? Win32Helper.GetWindowThreadProcessId(
                windowHandle,
                out processId)
            : 0;
        return new AotFilePropertiesObservedWindowSnapshot(
            windowHandle.ToInt64(),
            isWindow,
            isWindow && Win32Helper.IsWindowVisible(windowHandle),
            threadId,
            processId,
            isWindow ? GetWindowClassName(windowHandle) : string.Empty,
            isWindow ? GetWindowTitle(windowHandle) : string.Empty);
    }

    private static bool IsExactScenario() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "DESKBOX_AOT_MANAGED_UI_SMOKE"),
            Scenario,
            StringComparison.Ordinal);

    private static bool IsValidRunId(string? value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed class AotFilePropertiesInvocationState
    {
        internal long OwnerWindowHandle { get; init; }
        internal string TargetPath { get; init; } = string.Empty;
        internal bool Invoked { get; set; }
        internal bool ResultRecorded { get; set; }
        internal string Error { get; set; } = string.Empty;
        internal DateTimeOffset StartedAtUtc { get; init; }
        internal DateTimeOffset? ReturnedAtUtc { get; set; }

        internal AotFilePropertiesInvocationSnapshot ToSnapshot() => new(
            OwnerWindowHandle,
            TargetPath,
            Invoked,
            ResultRecorded,
            Error,
            StartedAtUtc,
            ReturnedAtUtc);
    }

    private sealed record AotFilePropertiesWindowCandidate(
        IntPtr WindowHandle,
        IntPtr DirectOwnerWindowHandle,
        IntPtr RootOwnerWindowHandle,
        uint WindowThreadId,
        uint ProcessId,
        string ClassName,
        string Title,
        bool Visible);
}

internal sealed record AotFilePropertiesFixturePaths(
    string RunId,
    string FixtureRoot,
    string WidgetRoot,
    string TargetName,
    string TargetPath);

internal sealed record AotFilePropertiesInvocationSnapshot(
    long OwnerWindowHandle,
    string TargetPath,
    bool Invoked,
    bool ResultRecorded,
    string Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? ReturnedAtUtc);

internal sealed record AotFilePropertiesDialogSnapshot(
    long WindowHandle,
    long DirectOwnerWindowHandle,
    long RootOwnerWindowHandle,
    long ExpectedOwnerWindowHandle,
    uint WindowThreadId,
    uint ProcessId,
    string ClassName,
    string Title,
    bool VisibleBeforeClose,
    bool ClosePosted,
    bool WindowDestroyedAfterClose,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    AotFilePropertiesObservedWindowSnapshot ExpectedOwner,
    AotFilePropertiesObservedWindowSnapshot DirectOwner,
    AotFilePropertiesObservedWindowSnapshot RootOwner);

internal sealed record AotFilePropertiesObservedWindowSnapshot(
    long WindowHandle,
    bool IsWindow,
    bool Visible,
    uint WindowThreadId,
    uint ProcessId,
    string ClassName,
    string Title);
#endif
