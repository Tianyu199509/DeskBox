#if DESKBOX_NATIVE_AOT
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

internal static class AotPickerClipboardFixture
{
    internal const string Scenario =
        "PickerClipboardStorageItemsPersistenceRestart";
    internal const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE";
    internal const string RunIdEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_RUN_ID";
    internal const string OwnedWidgetId = "aot-5b4c1c1-file";
    internal const string FixtureDirectoryName =
        "picker-clipboard-storage-items";
    internal const string WidgetRootDirectoryName = "widget-root";
    internal const string SourceDirectoryName = "sources";
    internal const string PickerSourceDirectoryName = "picker";
    internal const string ClipboardSourceDirectoryName = "clipboard";

    internal static AotPickerClipboardFixturePaths GetOwnedPaths(
        DeskBoxDataPathService dataPaths)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);

        string? scenario = Environment.GetEnvironmentVariable(
            "DESKBOX_AOT_MANAGED_UI_SMOKE");
        string? phase = Environment.GetEnvironmentVariable(
            PhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            RunIdEnvironmentVariable);
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!string.Equals(scenario, Scenario, StringComparison.Ordinal) ||
            phase is not "Mutate" and not "VerifyRestore" and not "Postflight" ||
            runId is not { Length: 32 } ||
            runId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            !dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !PathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            throw new InvalidOperationException(
                "The owned picker/StorageItems fixture is unavailable outside its exact isolated AOT scenario, phase, and run ID.");
        }

        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        string sourceRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            SourceDirectoryName));
        string pickerSourceRoot = Path.GetFullPath(Path.Combine(
            sourceRoot,
            PickerSourceDirectoryName));
        string clipboardSourceRoot = Path.GetFullPath(Path.Combine(
            sourceRoot,
            ClipboardSourceDirectoryName));
        string pickerFileName = $"picker-{runId}.txt";
        string clipboardFileName = $"storage-file-{runId}.txt";
        string clipboardFolderName = $"storage-folder-{runId}";
        string nestedFileName = $"nested-{runId}.txt";

        if (!Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !Directory.Exists(sourceRoot) ||
            !Directory.Exists(pickerSourceRoot) ||
            !Directory.Exists(clipboardSourceRoot) ||
            !IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot) ||
            !IsPathEqualOrInside(fixtureRoot, widgetRoot) ||
            !IsPathEqualOrInside(fixtureRoot, sourceRoot) ||
            !IsPathEqualOrInside(sourceRoot, pickerSourceRoot) ||
            !IsPathEqualOrInside(sourceRoot, clipboardSourceRoot))
        {
            throw new InvalidOperationException(
                "The owned picker/StorageItems fixture escaped or is missing from the isolated preview root.");
        }

        string pickerSourceFile = Path.Combine(
            pickerSourceRoot,
            pickerFileName);
        string clipboardSourceFile = Path.Combine(
            clipboardSourceRoot,
            clipboardFileName);
        string clipboardSourceFolder = Path.Combine(
            clipboardSourceRoot,
            clipboardFolderName);
        string clipboardNestedSourceFile = Path.Combine(
            clipboardSourceFolder,
            nestedFileName);
        if (!File.Exists(pickerSourceFile) ||
            !File.Exists(clipboardSourceFile) ||
            !Directory.Exists(clipboardSourceFolder) ||
            !File.Exists(clipboardNestedSourceFile))
        {
            throw new InvalidOperationException(
                "The exact owned picker/StorageItems sources are missing.");
        }

        return new AotPickerClipboardFixturePaths(
            runId,
            phase,
            fixtureRoot,
            widgetRoot,
            sourceRoot,
            pickerSourceRoot,
            clipboardSourceRoot,
            pickerFileName,
            clipboardFileName,
            clipboardFolderName,
            nestedFileName,
            pickerSourceFile,
            clipboardSourceFile,
            clipboardSourceFolder,
            clipboardNestedSourceFile,
            Path.Combine(widgetRoot, pickerFileName),
            Path.Combine(widgetRoot, clipboardFileName),
            Path.Combine(widgetRoot, clipboardFolderName),
            Path.Combine(widgetRoot, clipboardFolderName, nestedFileName));
    }

    internal static HashSet<long> CaptureVisibleTopLevelWindowHandles()
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

    internal static async Task<AotPickerDialogSnapshot>
        ObservePickerDialogAsync(
            long expectedOwnerWindowHandle,
            IReadOnlySet<long> baselineWindowHandles,
            string action,
            CancellationToken cancellationToken = default)
    {
        if (expectedOwnerWindowHandle == 0 ||
            !Win32Helper.IsWindow(new IntPtr(expectedOwnerWindowHandle)))
        {
            throw new InvalidOperationException(
                "The picker observer requires the real File Widget owner HWND.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AotPickerWindowCandidate? candidate = FindPickerWindowCandidate(
                expectedOwnerWindowHandle,
                baselineWindowHandles);
            if (candidate is not null)
            {
                DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
                AotPickerObservedWindowSnapshot expectedOwner =
                    CaptureObservedWindow(new IntPtr(expectedOwnerWindowHandle));
                AotPickerObservedWindowSnapshot directOwner =
                    CaptureObservedWindow(candidate.DirectOwnerWindowHandle);
                AotPickerObservedWindowSnapshot rootOwner =
                    CaptureObservedWindow(candidate.RootOwnerWindowHandle);

                DateTimeOffset closeDeadline =
                    DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < closeDeadline &&
                    IsSamePickerWindow(candidate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(50, cancellationToken);
                }

                bool destroyed = !IsSamePickerWindow(candidate);
                return new AotPickerDialogSnapshot(
                    action,
                    candidate.WindowHandle.ToInt64(),
                    candidate.DirectOwnerWindowHandle.ToInt64(),
                    candidate.RootOwnerWindowHandle.ToInt64(),
                    expectedOwnerWindowHandle,
                    candidate.WindowThreadId,
                    candidate.ProcessId,
                    candidate.ClassName,
                    candidate.Title,
                    candidate.VisibleBeforeAction,
                    candidate.OwnerChainContainsExpected,
                    destroyed,
                    observedAtUtc,
                    destroyed ? DateTimeOffset.UtcNow : null,
                    candidate.OwnerChainHandles,
                    expectedOwner,
                    directOwner,
                    rootOwner);
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(
            $"The real system file picker for action '{action}' was not observed.");
    }

    internal static bool IsPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static AotPickerWindowCandidate? FindPickerWindowCandidate(
        long expectedOwnerWindowHandle,
        IReadOnlySet<long> baselineWindowHandles)
    {
        AotPickerWindowCandidate? match = null;
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
            if (!string.Equals(
                    className,
                    "#32770",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            IReadOnlyList<long> ownerChain = CaptureOwnerChain(windowHandle);
            bool ownsExpected = ownerChain.Contains(expectedOwnerWindowHandle);
            Win32Helper.GetWindowThreadProcessId(
                windowHandle,
                out uint processId);
            if (!ownsExpected && processId != (uint)Environment.ProcessId)
            {
                return true;
            }

            match = new AotPickerWindowCandidate(
                windowHandle,
                Win32Helper.GetWindow(windowHandle, Win32Helper.GW_OWNER),
                Win32Helper.GetAncestor(windowHandle, Win32Helper.GA_ROOTOWNER),
                Win32Helper.GetWindowThreadProcessId(
                    windowHandle,
                    out processId),
                processId,
                className,
                GetWindowTitle(windowHandle),
                Win32Helper.IsWindowVisible(windowHandle),
                ownsExpected,
                ownerChain);
            return false;
        }, IntPtr.Zero);
        return match;
    }

    private static IReadOnlyList<long> CaptureOwnerChain(IntPtr windowHandle)
    {
        var handles = new List<long>();
        var visited = new HashSet<long>();
        IntPtr current = windowHandle;
        for (int index = 0; index < 12; index++)
        {
            current = Win32Helper.GetWindow(current, Win32Helper.GW_OWNER);
            if (current == IntPtr.Zero || !visited.Add(current.ToInt64()))
            {
                break;
            }

            handles.Add(current.ToInt64());
        }

        return handles;
    }

    private static bool IsSamePickerWindow(
        AotPickerWindowCandidate candidate)
    {
        if (!Win32Helper.IsWindow(candidate.WindowHandle))
        {
            return false;
        }

        uint windowThreadId = Win32Helper.GetWindowThreadProcessId(
            candidate.WindowHandle,
            out uint processId);
        return windowThreadId == candidate.WindowThreadId &&
            processId == candidate.ProcessId &&
            Win32Helper.GetWindow(
                candidate.WindowHandle,
                Win32Helper.GW_OWNER) == candidate.DirectOwnerWindowHandle &&
            string.Equals(
                GetWindowClassName(candidate.WindowHandle),
                candidate.ClassName,
                StringComparison.Ordinal);
    }

    private static AotPickerObservedWindowSnapshot CaptureObservedWindow(
        IntPtr windowHandle)
    {
        uint threadId = 0;
        uint processId = 0;
        bool isWindow = windowHandle != IntPtr.Zero &&
            Win32Helper.IsWindow(windowHandle);
        if (isWindow)
        {
            threadId = Win32Helper.GetWindowThreadProcessId(
                windowHandle,
                out processId);
        }

        return new AotPickerObservedWindowSnapshot(
            windowHandle.ToInt64(),
            isWindow,
            isWindow && Win32Helper.IsWindowVisible(windowHandle),
            threadId,
            processId,
            isWindow ? GetWindowClassName(windowHandle) : string.Empty,
            isWindow ? GetWindowTitle(windowHandle) : string.Empty);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var builder = new StringBuilder(512);
        int length = Win32Helper.GetWindowText(
            windowHandle,
            builder,
            builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var builder = new StringBuilder(256);
        int length = Win32Helper.GetClassName(
            windowHandle,
            builder,
            builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }

    private sealed record AotPickerWindowCandidate(
        IntPtr WindowHandle,
        IntPtr DirectOwnerWindowHandle,
        IntPtr RootOwnerWindowHandle,
        uint WindowThreadId,
        uint ProcessId,
        string ClassName,
        string Title,
        bool VisibleBeforeAction,
        bool OwnerChainContainsExpected,
        IReadOnlyList<long> OwnerChainHandles);
}

internal sealed record AotPickerClipboardFixturePaths(
    string RunId,
    string Phase,
    string FixtureRoot,
    string WidgetRoot,
    string SourceRoot,
    string PickerSourceRoot,
    string ClipboardSourceRoot,
    string PickerFileName,
    string ClipboardFileName,
    string ClipboardFolderName,
    string NestedFileName,
    string PickerSourceFile,
    string ClipboardSourceFile,
    string ClipboardSourceFolder,
    string ClipboardNestedSourceFile,
    string PickerDestinationFile,
    string ClipboardDestinationFile,
    string ClipboardDestinationFolder,
    string ClipboardNestedDestinationFile);

internal sealed record AotPickerDialogSnapshot(
    string Action,
    long WindowHandle,
    long DirectOwnerWindowHandle,
    long RootOwnerWindowHandle,
    long ExpectedOwnerWindowHandle,
    uint WindowThreadId,
    uint ProcessId,
    string ClassName,
    string Title,
    bool VisibleBeforeAction,
    bool OwnerChainContainsExpected,
    bool WindowDestroyedAfterAction,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    IReadOnlyList<long> OwnerChainHandles,
    AotPickerObservedWindowSnapshot ExpectedOwner,
    AotPickerObservedWindowSnapshot DirectOwner,
    AotPickerObservedWindowSnapshot RootOwner);

internal sealed record AotPickerObservedWindowSnapshot(
    long WindowHandle,
    bool IsWindow,
    bool Visible,
    uint WindowThreadId,
    uint ProcessId,
    string ClassName,
    string Title);
#endif
