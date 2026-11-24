using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Tests;

/// <summary>
/// Namespace-shortcut menu contracts: the pure parsing-name → kind mapping
/// behind the target-aware item menu, plus the entry-point probes for the
/// new shell32 recycle-bin P/Invoke family (resolution happens on first
/// call, which plain unit tests can never see — the 1.5.9 lesson).
/// </summary>
public sealed class NamespaceShortcutMenuTests
{
    [Theory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        NamespaceShortcutKind.ThisPC)]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}",
        NamespaceShortcutKind.RecycleBin)]
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}",
        NamespaceShortcutKind.ControlPanel)]
    // Control-panel category pages extend past the root CLSID.
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}\\::{D20EA4E1-3957-11D2-A40B-0C5020524153}",
        NamespaceShortcutKind.ControlPanel)]
    // The legacy control-panel CLSID still resolves in modern Windows.
    [InlineData("::{21EC2020-3AEA-1069-A2DD-08002B30309D}",
        NamespaceShortcutKind.ControlPanel)]
    [InlineData("::{F02C1AA0-4AE4-4BD1-9BF0-8BAA2DBF44C5}",
        NamespaceShortcutKind.OtherNamespace)]
    [InlineData("C:\\Program Files\\app.exe", NamespaceShortcutKind.None)]
    [InlineData("shell:ControlPanelFolder", NamespaceShortcutKind.None)]
    [InlineData("", NamespaceShortcutKind.None)]
    [InlineData("   ", NamespaceShortcutKind.None)]
    public void ClassifyParsingName_MapsCuratedNamespaces(
        string parsingName,
        NamespaceShortcutKind expected)
    {
        Assert.Equal(
            expected,
            NamespaceShortcutHelper.ClassifyParsingName(parsingName));
    }

    [Fact]
    public void ClassifyParsingName_MatchesGuidCaseInsensitively()
    {
        Assert.Equal(
            NamespaceShortcutKind.RecycleBin,
            NamespaceShortcutHelper.ClassifyParsingName(
                "::{645ff040-5081-101b-9f08-00aa002f954e}"));
    }

    // ---- entry-point probes for the new shell32 P/Invoke family ----

    // Classic DllImport on purpose: the test project is not compiled with
    // unsafe blocks for LibraryImport source generation, and the Platform
    // interop ratchet only scans src/DeskBox.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long totalSize;
        public long totalItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(
        string? rootPath,
        ref ShQueryRbInfo queryInfo);

    [Fact]
    public void RecycleBinExports_ResolveThroughGetProcAddress()
    {
        IntPtr shell32 = LoadLibrary("shell32.dll");
        Assert.NotEqual(IntPtr.Zero, shell32);
        Assert.NotEqual(
            IntPtr.Zero,
            GetProcAddress(shell32, "SHQueryRecycleBinW"));
        Assert.NotEqual(
            IntPtr.Zero,
            GetProcAddress(shell32, "SHEmptyRecycleBinW"));
    }

    [Fact]
    public void RecycleBinQuery_MarshalsOnARealCall()
    {
        // x64 pin: cbSize + padding + two 64-bit counters.
        Assert.Equal(24, Marshal.SizeOf<ShQueryRbInfo>());

        var info = new ShQueryRbInfo
        {
            cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>()
        };
        int hr = SHQueryRecycleBin(null, ref info);
        Assert.True(hr >= 0, $"aggregate query failed hr=0x{hr:X8}");
    }

    [Fact]
    public void LinkVtableReader_PinsSlotsAndInterfaceIds()
    {
        // The vtable reader's slot and IID constants must be re-verified
        // against the SDK whenever the file changes; pin the literals so a
        // careless edit fails here first.
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/ShellLinkVtableInterop.cs"));
        Assert.Contains(
            "new(\"00021401-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"000214F9-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"0000010B-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"43826d1e-e718-42ee-bc55-a1e261c37bfe\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int PersistFileLoadSlot = 5;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int ShellLinkGetIdListSlot = 4;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int ShellItemGetDisplayNameSlot = 5;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OrderFixedDriveRootsForProbe_PrioritizesSystemDriveAndExcludesNonFixedOrSubstDrives()
    {
        IReadOnlyList<string> ordered = ShellRecycleBinInterop.OrderFixedDriveRootsForProbe(
        [
            ("D:\\", DriveType.Fixed, false),
            ("Z:\\", DriveType.Network, false),
            ("E:\\", DriveType.Removable, false),
            ("S:\\", DriveType.Fixed, true),
            ("c:\\", DriveType.Fixed, false),
            ("F:\\", DriveType.CDRom, false),
            ("d:\\", DriveType.Fixed, false),
        ],
        systemRoot: "C:\\");

        Assert.Equal(["C:\\", "D:\\"], ordered);
    }

    [Fact]
    public void TryEvaluateTwoStageHasAnyItems_ShortCircuitsOnFirstNonEmptyFixedDriveWithoutTouchingLaterOrAggregateRoots()
    {
        var visitedRoots = new List<string?>();
        bool ok = ShellRecycleBinInterop.TryEvaluateTwoStageHasAnyItems(
            ["C:\\", "D:\\"],
            (string? root, out long count) =>
            {
                visitedRoots.Add(root);
                if (string.Equals(root, "C:\\", StringComparison.OrdinalIgnoreCase))
                {
                    count = 3;
                    return true;
                }

                throw new InvalidOperationException("Secondary or aggregate root must not be queried once C:\\ is non-empty.");
            },
            out bool hasItems);

        Assert.True(ok);
        Assert.True(hasItems);
        Assert.Equal(["C:\\"], visitedRoots);
    }

    [Fact]
    public void TryEvaluateTwoStageHasAnyItems_FallsBackToAggregateNullRootOnlyWhenAllFixedDrivesAreEmpty()
    {
        var visitedRoots = new List<string?>();
        bool ok = ShellRecycleBinInterop.TryEvaluateTwoStageHasAnyItems(
            ["C:\\", "D:\\"],
            (string? root, out long count) =>
            {
                visitedRoots.Add(root);
                count = root is null ? 2 : 0;
                return true;
            },
            out bool hasItems);

        Assert.True(ok);
        Assert.True(hasItems);
        Assert.Equal(new string?[] { "C:\\", "D:\\", null }, visitedRoots);
    }

    [Fact]
    public void TryEvaluateTwoStageHasAnyItems_ReturnsFalseWhenAggregateFallbackFailsSoMenuStaysEnabled()
    {
        bool ok = ShellRecycleBinInterop.TryEvaluateTwoStageHasAnyItems(
            ["C:\\"],
            (string? root, out long count) =>
            {
                count = 0;
                return root is not null;
            },
            out bool hasItems);

        Assert.False(ok);
        Assert.False(hasItems);
    }

    [Theory]
    [InlineData(0, nameof(RecycleBinEmptyOperationOutcome.Emptied))]
    [InlineData(1, nameof(RecycleBinEmptyOperationOutcome.Emptied))]
    [InlineData(unchecked((int)0x800704C7), nameof(RecycleBinEmptyOperationOutcome.Cancelled))]
    [InlineData(unchecked((int)0x80004004), nameof(RecycleBinEmptyOperationOutcome.Cancelled))]
    [InlineData(unchecked((int)0x8000FFFF), nameof(RecycleBinEmptyOperationOutcome.AlreadyEmpty))]
    [InlineData(unchecked((int)0x80070005), nameof(RecycleBinEmptyOperationOutcome.Failed))]
    [InlineData(unchecked((int)0x80004005), nameof(RecycleBinEmptyOperationOutcome.Failed))]
    public void ClassifyEmptyAllBinsHResult_DistinguishesSuccessCancelAlreadyEmptyAndFailure(
        int hr,
        string expectedOutcome)
    {
        Assert.Equal(
            expectedOutcome,
            ShellRecycleBinInterop.ClassifyEmptyAllBinsHResult(hr).ToString());
    }

    [Fact]
    public async Task RecycleBinEmptyStateCache_ColdReadNeverBlocksAndCoalescesSingleFlightProbe()
    {
        using var allowProbeToFinish = new ManualResetEventSlim(false);
        using var probeEntered = new ManualResetEventSlim(false);
        int probeCount = 0;

        using var cache = new RecycleBinEmptyStateCache(
            (out bool hasItems) =>
            {
                Interlocked.Increment(ref probeCount);
                probeEntered.Set();
                allowProbeToFinish.Wait(TimeSpan.FromSeconds(5));
                hasItems = false;
                return true;
            });

        // Cold read returns immediately on the caller thread while the slow
        // Shell probe is blocked on the worker thread.
        Assert.False(cache.TryGetKnownEmptyState(out bool coldEmpty));
        Assert.False(coldEmpty);
        Assert.True(probeEntered.Wait(TimeSpan.FromSeconds(3)));

        // Additional reads and EnsureWarm calls join the single in-flight probe.
        cache.EnsureWarm(uiDispatcher: null);
        Assert.False(cache.TryGetKnownEmptyState(out _));
        Assert.Equal(1, Volatile.Read(ref probeCount));

        allowProbeToFinish.Set();
        bool? refreshed = await cache.StartOrJoinRefreshAsync(forceFollowUp: false);
        Assert.Equal(true, refreshed);
        Assert.True(cache.TryGetKnownEmptyState(out bool warmEmpty));
        Assert.True(warmEmpty);
        Assert.Equal(1, Volatile.Read(ref probeCount));
    }

    [Fact]
    public async Task RecycleBinEmptyStateCache_MarkKnownStatePreventsStaleInFlightOverwriteAndRunsFollowUp()
    {
        using var firstProbeEntered = new ManualResetEventSlim(false);
        using var releaseFirstProbe = new ManualResetEventSlim(false);
        int callIndex = 0;

        using var cache = new RecycleBinEmptyStateCache(
            (out bool hasItems) =>
            {
                int current = Interlocked.Increment(ref callIndex);
                if (current == 1)
                {
                    firstProbeEntered.Set();
                    releaseFirstProbe.Wait(TimeSpan.FromSeconds(5));
                    // Stale probe observed an empty bin before the drop happened.
                    hasItems = false;
                    return true;
                }

                // Follow-up probe observes the newly recycled item.
                hasItems = true;
                return true;
            });

        Task<bool?> inFlight = cache.StartOrJoinRefreshAsync(forceFollowUp: false);
        Assert.True(firstProbeEntered.Wait(TimeSpan.FromSeconds(3)));

        // User recycles an item while the stale empty-bin probe is still running.
        cache.MarkKnownState(isEmpty: false);
        Assert.True(cache.TryGetKnownEmptyState(out bool immediateEmpty));
        Assert.False(immediateEmpty);

        releaseFirstProbe.Set();
        bool? finalEmpty = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(false, finalEmpty);
        Assert.Equal(2, Volatile.Read(ref callIndex));
        Assert.True(cache.TryGetKnownEmptyState(out bool postEmpty));
        Assert.False(postEmpty);
    }

    [Fact]
    public async Task RecycleBinEmptyStateCache_NotifyShellChangedImmediatelyClearsEmptyStateAndRefreshes()
    {
        long nowMs = 1_000;
        bool binHasItems = false;
        var secondPassDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        using var cache = new RecycleBinEmptyStateCache(
            (out bool hasItems) =>
            {
                int n = Interlocked.Increment(ref calls);
                hasItems = Volatile.Read(ref binHasItems);
                if (n >= 2)
                {
                    secondPassDone.TrySetResult(true);
                }
                return true;
            },
            clockTick64: () => Interlocked.Read(ref nowMs),
            unwatchedTtlMs: 10_000,
            changeDebounceMs: 25);

        _ = await cache.StartOrJoinRefreshAsync(forceFollowUp: false);
        Assert.True(cache.TryGetKnownEmptyState(out bool initiallyEmpty));
        Assert.True(initiallyEmpty);

        // External Explorer deletion arrives via SHChangeNotify: must immediately
        // drop the cached empty=true state before the debounce timer even fires.
        Volatile.Write(ref binHasItems, true);
        cache.NotifyShellChanged();
        Assert.False(cache.TryGetKnownEmptyState(out _));

        await secondPassDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SpinWait.SpinUntil(() => cache.TryGetKnownEmptyState(out _), TimeSpan.FromSeconds(2));
        Assert.True(cache.TryGetKnownEmptyState(out bool afterRefreshEmpty));
        Assert.False(afterRefreshEmpty);
    }

    [Fact]
    public async Task RecycleBinEmptyStateCache_UnwatchedCachedEmptyExpiresAfterTtl()
    {
        long nowMs = 5_000;
        using var cache = new RecycleBinEmptyStateCache(
            (out bool hasItems) =>
            {
                hasItems = false;
                return true;
            },
            clockTick64: () => Interlocked.Read(ref nowMs),
            unwatchedTtlMs: 2_000);

        _ = await cache.StartOrJoinRefreshAsync(forceFollowUp: false);
        Assert.True(cache.TryGetKnownEmptyState(out bool freshEmpty));
        Assert.True(freshEmpty);

        Interlocked.Add(ref nowMs, 2_500);
        Assert.False(cache.TryGetKnownEmptyState(out _));
    }

    [Fact]
    public async Task RecycleBinEmptyStateCache_BoundedFreshQueryReturnsFalseOnHungDriveTimeout()
    {
        using var unblockHungDrive = new ManualResetEventSlim(false);
        using var cache = new RecycleBinEmptyStateCache(
            (out bool hasItems) =>
            {
                unblockHungDrive.Wait(TimeSpan.FromSeconds(5));
                hasItems = false;
                return true;
            });

        try
        {
            bool knownEmpty = await cache.QueryFreshIsKnownEmptyBoundedAsync(
                TimeSpan.FromMilliseconds(60));
            Assert.False(knownEmpty);
        }
        finally
        {
            unblockHungDrive.Set();
        }
    }
}
