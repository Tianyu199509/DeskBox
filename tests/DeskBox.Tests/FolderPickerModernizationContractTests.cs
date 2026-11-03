using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FolderPickerModernizationContractTests
{
    [Fact]
    public void FolderPickerService_ShowsTheNativeStaDialogAndLogsEveryOutcome()
    {
        // Decision reversal (2026-10-27, WeChat map-folder diagnostics) plus
        // dedicated-STA isolation (2026-10-30): the WinAppSDK FolderPicker runs
        // IFileOpenDialog::Show on a background MTA thread and folds failures
        // into a silent null, while running Show directly on the main XAML UI
        // thread freezes all of DeskBox whenever Shell/NAS/Quick Access stalls.
        // FolderPickerService runs the native dialog on a dedicated background
        // STA thread with a same-thread owner HWND and logs every outcome.
        string source = ReadSource("src/DeskBox/Services/FolderPickerService.cs");

        Assert.Contains("NativeFileOpenDialog.RunOnDedicatedStaAsync(", source, StringComparison.Ordinal);
        Assert.Contains("NativeFileOpenDialog.ShowPickFolder(", source, StringComparison.Ordinal);
        Assert.Contains(
            "public static Task<FolderPickResult> PickFolderWithOutcomeAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[FolderPicker] outcome=", source, StringComparison.Ordinal);
        Assert.Contains("owner={owner}", source, StringComparison.Ordinal);
        Assert.Contains("elapsedMs={stopwatch.ElapsedMilliseconds}", source, StringComparison.Ordinal);
        Assert.Contains("hr=0x{ex.HResult:X8}", source, StringComparison.Ordinal);
        Assert.Contains("FolderPickOutcome.Failed", source, StringComparison.Ordinal);

        Assert.DoesNotContain("using Microsoft.Windows.Storage.Pickers;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PickSingleFolderAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Interop.GetWindowIdFromWindow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PickFolderOptions_IncludesPathMustExistWithoutDisablingLinkDereference()
    {
        const uint fosPickFolders = 0x00000020;
        const uint fosForceFileSystem = 0x00000040;
        const uint fosPathMustExist = 0x00000800;
        const uint fosNoDereferenceLinks = 0x00100000;

        Assert.NotEqual(0u, NativeFileOpenDialog.PickFolderOptions & fosPickFolders);
        Assert.NotEqual(0u, NativeFileOpenDialog.PickFolderOptions & fosForceFileSystem);
        Assert.NotEqual(0u, NativeFileOpenDialog.PickFolderOptions & fosPathMustExist);
        Assert.Equal(0u, NativeFileOpenDialog.PickFolderOptions & fosNoDereferenceLinks);
    }

    [Fact]
    public async Task RunOnDedicatedStaAsync_UsesBackgroundStaThreadAndSameThreadOwnerWindow()
    {
        // Entry-point probe for DestroyWindow on a zero handle (winui-pitfalls P/Invoke gate).
        Assert.False(Win32Helper.DestroyWindow(IntPtr.Zero));

        uint callerThreadId = Win32Helper.GetCurrentThreadId();
        IntPtr referenceOwnerHwnd;
        using (var referenceOwner = new ThreadOwnedTestWindow("DeskBox Test Reference Owner", 100, 100))
        {
            referenceOwnerHwnd = referenceOwner.Hwnd;

            (ApartmentState Apartment, uint WorkerThreadId, IntPtr StaOwner, uint OwnerWindowThreadId, uint OwnerProcessId, bool WasWindowDuringCall) probe =
                await NativeFileOpenDialog.RunOnDedicatedStaAsync(
                    referenceOwnerHwnd,
                    staOwnerHwnd =>
                    {
                        uint workerTid = Win32Helper.GetCurrentThreadId();
                        uint ownerWindowTid = Win32Helper.GetWindowThreadProcessId(
                            staOwnerHwnd,
                            out uint ownerPid);
                        return (
                            Thread.CurrentThread.GetApartmentState(),
                            workerTid,
                            staOwnerHwnd,
                            ownerWindowTid,
                            ownerPid,
                            Win32Helper.IsWindow(staOwnerHwnd));
                    });

            Assert.Equal(ApartmentState.STA, probe.Apartment);
            Assert.NotEqual(callerThreadId, probe.WorkerThreadId);
            Assert.NotEqual(IntPtr.Zero, probe.StaOwner);
            Assert.True(probe.WasWindowDuringCall);
            Assert.Equal(probe.WorkerThreadId, probe.OwnerWindowThreadId);
            Assert.Equal((uint)Environment.ProcessId, probe.OwnerProcessId);
            Assert.False(Win32Helper.IsWindow(probe.StaOwner));
        }

        Assert.False(Win32Helper.IsWindow(referenceOwnerHwnd));
    }

    [Fact]
    public async Task PickFolderWithOutcomeCoreAsync_EnforcesSingleFlightAndRecoversAfterCompletion()
    {
        using var owner = new ThreadOwnedTestWindow("DeskBox Test Picker Owner", 120, 120);
        IntPtr ownerHwnd = owner.Hwnd;

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;

        Task<FolderPickResult> firstPickTask = FolderPickerService.PickFolderWithOutcomeCoreAsync(
            ownerHwnd,
            "test-first",
            _ =>
            {
                Interlocked.Increment(ref invocationCount);
                firstEntered.TrySetResult();
                return releaseFirst.Task;
            });

        await firstEntered.Task;
        Assert.True(FolderPickerService.IsPickInFlight);

        FolderPickResult concurrentPick = await FolderPickerService.PickFolderWithOutcomeCoreAsync(
            ownerHwnd,
            "test-concurrent",
            _ =>
            {
                Interlocked.Increment(ref invocationCount);
                return Task.FromResult<string?>("C:\\ShouldNotRun");
            });

        Assert.Equal(FolderPickOutcome.Cancelled, concurrentPick.Outcome);
        Assert.Null(concurrentPick.Folder);
        Assert.Equal(1, invocationCount);

        releaseFirst.TrySetResult(@"C:\PickedFolder");
        FolderPickResult firstResult = await firstPickTask;
        Assert.Equal(FolderPickOutcome.Picked, firstResult.Outcome);
        Assert.Equal(@"C:\PickedFolder", firstResult.Folder);
        Assert.False(FolderPickerService.IsPickInFlight);

        FolderPickResult failedResult = await FolderPickerService.PickFolderWithOutcomeCoreAsync(
            ownerHwnd,
            "test-failed",
            _ => Task.FromException<string?>(new COMException("boom", unchecked((int)0x80004005))));
        Assert.Equal(FolderPickOutcome.Failed, failedResult.Outcome);
        Assert.Equal(unchecked((int)0x80004005), failedResult.HResult);
    }

    [Fact]
    public void FolderPickerService_RejectsZeroOwnerBeforeWinRtActivation()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => FolderPickerService.ValidateOwnerWindowHandle(IntPtr.Zero));

        Assert.Equal("ownerHwnd", exception.ParamName);
    }

    [Fact]
    public void FileOpenPickerService_OffersOwnerAwareSingleAndMultipleFilePicks()
    {
        string service = ReadSource("src/DeskBox/Services/FileOpenPickerService.cs");

        Assert.Contains(
            "public static async Task<string?> PickSingleFileAsync(",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(",
            service,
            StringComparison.Ordinal);
        Assert.Contains("await picker.PickSingleFileAsync()", service, StringComparison.Ordinal);
        Assert.Contains("await picker.PickMultipleFilesAsync()", service, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenPickerService_RejectsZeroOwnerBeforeWinRtActivation()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => FileOpenPickerService.ValidateOwnerWindowHandle(IntPtr.Zero));

        Assert.Equal("ownerHwnd", exception.ParamName);
    }

    [Fact]
    public void ProductCode_RetiresTheLegacyWinRtPickerStack()
    {
        // The legacy Windows.Storage.Pickers stack needs InitializeWithWindow
        // bridging and shows a differently-behaved dialog; every product
        // entrance now goes through the owner-aware WinAppSDK pickers.
        string[] legacyLiterals =
        [
            "using Windows.Storage.Pickers;",
            "Windows.Storage.Pickers.FileOpenPicker",
            "Windows.Storage.Pickers.FileSavePicker",
            "Windows.Storage.Pickers.FolderPicker",
        ];
        string sourceRoot = TestPaths.FromRepository("src/DeskBox");
        foreach (string file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (string literal in legacyLiterals)
            {
                Assert.False(
                    source.Contains(literal, StringComparison.Ordinal),
                    $"{file} still uses the legacy picker stack: {literal}");
            }
        }
    }

    [Fact]
    public void AllSevenProductEntrances_AwaitTheOwnerAwarePicker()
    {
        // The five settings-side entrances keep awaiting the task wrapper.
        var expectedCalls = new Dictionary<string, int>
        {
            ["src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"] = 1,
            ["src/DeskBox/Views/SettingsWindow.Maintenance.cs"] = 3,
            ["src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"] = 1
        };

        int totalCalls = 0;
        foreach ((string path, int expectedCount) in expectedCalls)
        {
            string source = ReadSource(path);
            int callCount = Regex.Matches(
                source,
                @"await\s+FolderPickerService\.PickFolderAsync\s*\(").Count;

            Assert.Equal(expectedCount, callCount);
            Assert.DoesNotContain("FolderPickerService.PickFolder(", source, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(
                    source,
                    @"FolderPickerService\.PickFolderAsync\s*\(\s*IntPtr\.Zero"),
                $"{path} still opens FolderPicker without an owner window.");
            totalCalls += callCount;
        }

        Assert.Equal(5, totalCalls);

        // Tray and jump-list entrances route through the shared
        // CreateFolderWidgetFromPickerAsync (with the owner-source label and
        // the failure toast) instead of duplicating the pick inline.
        string tray = ReadSource("src/DeskBox/App.Tray.cs");
        Assert.Contains(
            "await FolderPickerService.PickFolderWithOutcomeAsync(",
            tray,
            StringComparison.Ordinal);
        Assert.DoesNotContain("await FolderPickerService.PickFolderAsync(", tray, StringComparison.Ordinal);

        string jumpList = ReadSource("src/DeskBox/Services/JumpListService.cs");
        Assert.DoesNotContain("FolderPickerService.PickFolder", jumpList, StringComparison.Ordinal);
        Assert.Contains("app.CreateFolderWidgetFromPickerAsync()", jumpList, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayAndJumpListEntrances_UseTheStableTrayOwnerWindow()
    {
        string tray = ReadSource("src/DeskBox/App.Tray.cs");
        string jumpList = ReadSource("src/DeskBox/Services/JumpListService.cs");

        Assert.Contains("internal IntPtr GetFolderPickerOwner(out PickerOwnerSource source)", tray, StringComparison.Ordinal);
        Assert.Contains("IntPtr trayHwnd = TrayHostWindowHandle;", tray, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.IsWindow(ownerHwnd)", tray, StringComparison.Ordinal);
        Assert.Contains("GetFolderPickerOwner(out PickerOwnerSource ownerSource)", tray, StringComparison.Ordinal);

        // The owner source is a diagnostic discriminator for map-folder
        // failures, and the jump-list folder-map pick must leave its
        // activation frame before showing the modal dialog so a cold-start
        // activation cannot hold the launch pipeline hostage.
        Assert.Contains("DescribePickerOwner(", tray, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback(", jumpList, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayOwnerResolution_RejectsCloakedOwnersAndFallsBackToAFreshHelper()
    {
        string tray = ReadSource("src/DeskBox/App.Tray.cs");

        // A cloaked owner (its virtual desktop is not the active one) makes
        // the owned picker dialog invisible, so the tray entrance must gate
        // every candidate on DWMWA_CLOAKED and keep a self-healing helper.
        Assert.Contains("PickerOwnerResolutionPolicy.Resolve(", tray, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.IsWindowCloaked(", tray, StringComparison.Ordinal);
        Assert.Contains("RecreateFolderPickerHelperWindow()", tray, StringComparison.Ordinal);
        Assert.Contains("_folderPickerHelperWindowHandle", tray, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private sealed class ThreadOwnedTestWindow : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _ownerThread;
        private Exception? _creationException;

        public ThreadOwnedTestWindow(string title, int x, int y)
        {
            _ownerThread = new Thread(() => RunOwnerThread(title, x, y))
            {
                IsBackground = true,
                Name = "DeskBox Test Owner Window"
            };
            _ownerThread.SetApartmentState(ApartmentState.STA);
            _ownerThread.Start();
            _ready.Wait();

            if (_creationException is not null)
            {
                throw _creationException;
            }

            Assert.NotEqual(IntPtr.Zero, Hwnd);
        }

        public IntPtr Hwnd { get; private set; }

        public void Dispose()
        {
            _release.Set();
            _ownerThread.Join();
            _ready.Dispose();
            _release.Dispose();
        }

        private void RunOwnerThread(string title, int x, int y)
        {
            try
            {
                Hwnd = Win32Helper.CreateWindowEx(
                    Win32Helper.WS_EX_TOOLWINDOW,
                    "STATIC",
                    title,
                    Win32Helper.WS_POPUP,
                    x,
                    y,
                    1,
                    1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Win32Helper.GetModuleHandle(null),
                    IntPtr.Zero);
            }
            catch (Exception ex)
            {
                _creationException = ex;
            }
            finally
            {
                _ready.Set();
            }

            if (Hwnd == IntPtr.Zero)
            {
                return;
            }

            _release.Wait();
            _ = Win32Helper.DestroyWindow(Hwnd);
            while (Win32Helper.PeekMessage(out Win32Helper.MSG msg, IntPtr.Zero, 0, 0, Win32Helper.PM_REMOVE))
            {
                Win32Helper.TranslateMessage(ref msg);
                Win32Helper.DispatchMessage(ref msg);
            }
        }
    }
}
