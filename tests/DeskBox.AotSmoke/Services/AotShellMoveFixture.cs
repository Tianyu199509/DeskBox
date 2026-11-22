#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using DeskBox.Helpers;

namespace DeskBox.Services;

internal static class AotShellMoveFixture
{
    internal const string Scenario = "ShellMovePersistenceRestart";
    internal const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE";
    internal const string RunIdEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_RUN_ID";
    internal const string OwnedWidgetId = "aot-5b4c1b2a-file";
    internal const string FixtureDirectoryName = "shell-move";
    internal const string WidgetRootDirectoryName = "widget-root";
    internal const string DesktopRootDirectoryName = "desktop-root";
    internal const string BaselineName = "baseline.txt";
    internal const string RealMode = "Real";
    internal const string PartialMode = "Partial";
    internal const string CancelMode = "Cancel";
    internal const string LateMode = "Late";
    internal const string ReturnedOutcome = "Returned";
    /// <summary>The watchdog declared the engine dead while the filesystem
    /// part had already completed — the modern engine's persistence
    /// scenario (the retired legacy bridge's "late return" equivalent).
    /// </summary>
    internal const string AbandonedButCompletedOutcome = "AbandonedButCompleted";

    private static readonly object s_sync = new();
    private static readonly List<AotShellMoveInvocationState> s_invocations = [];
    private static int s_sequence;

    internal static bool TryGetOwnedDesktopPath(out string desktopPath)
    {
        desktopPath = string.Empty;
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DESKBOX_AOT_MANAGED_UI_SMOKE"),
                Scenario,
                StringComparison.Ordinal))
        {
            return false;
        }

        desktopPath = GetOwnedPaths(DeskBoxDataPathService.Current).DesktopRoot;
        return true;
    }

    internal static AotShellMoveFixturePaths GetOwnedPaths(
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
            phase is not "Mutate" and
                not "VerifyRestore" and
                not "Postflight" and
                not "Compensate" ||
            !IsValidRunId(runId) ||
            !dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !PathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            throw new InvalidOperationException(
                "The owned Shell move fixture is unavailable outside its exact AOT scenario, phase, run identity, and preview root.");
        }

        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        string desktopRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            DesktopRootDirectoryName));
        if (!Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !Directory.Exists(desktopRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                dataPaths.RootPath,
                fixtureRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                fixtureRoot,
                widgetRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                fixtureRoot,
                desktopRoot) ||
            PathsEqual(widgetRoot, desktopRoot))
        {
            throw new InvalidOperationException(
                "The owned Shell move fixture escaped or is missing from the isolated preview root.");
        }

        string realName = $"real-{runId}.txt";
        string partialFirstName = $"partial-first-{runId}.txt";
        string partialSecondName = $"partial-second-{runId}.txt";
        string cancelName = $"cancel-{runId}.txt";
        string lateName = $"late-{runId}.txt";
        return new AotShellMoveFixturePaths(
            runId!,
            fixtureRoot,
            widgetRoot,
            desktopRoot,
            Path.Combine(widgetRoot, BaselineName),
            realName,
            Path.Combine(widgetRoot, realName),
            Path.Combine(desktopRoot, realName),
            partialFirstName,
            Path.Combine(widgetRoot, partialFirstName),
            Path.Combine(desktopRoot, partialFirstName),
            partialSecondName,
            Path.Combine(widgetRoot, partialSecondName),
            Path.Combine(desktopRoot, partialSecondName),
            cancelName,
            Path.Combine(widgetRoot, cancelName),
            Path.Combine(desktopRoot, cancelName),
            lateName,
            Path.Combine(widgetRoot, lateName),
            Path.Combine(desktopRoot, lateName));
    }

    /// <summary>
    /// Entry hook for the modern Shell engine: records the invocation for
    /// every owned transfer and, for the simulated persistence modes,
    /// performs the mode's own filesystem part. Returns true when the
    /// harness owns this transfer; <paramref name="proceedWithRealEngine"/>
    /// tells the engine whether the real Shell operation path should still
    /// run (Real mode) or the simulated outcome will be thrown instead.
    /// </summary>
    internal static bool TryCaptureModernTransfer(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        IntPtr ownerWindowHandle,
        out bool proceedWithRealEngine)
    {
        proceedWithRealEngine = false;
        if (!IsExactScenario())
        {
            return false;
        }

        AotShellMoveFixturePaths paths = GetOwnedPaths(
            DeskBoxDataPathService.Current);
        string mode = ValidateAndResolveMode(paths, plans, requireMutatePhase: true);
        if (ownerWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The owned Shell move fixture requires the real File Widget owner HWND.");
        }

        var state = new AotShellMoveInvocationState
        {
            Sequence = Interlocked.Increment(ref s_sequence),
            Mode = mode,
            OwnerWindowHandle = ownerWindowHandle.ToInt64(),
            SourcePaths = plans.Select(plan => plan.SourcePath).ToList(),
            DestinationPaths = plans.Select(plan => plan.DestinationPath).ToList(),
            PlannedCount = plans.Count,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        switch (mode)
        {
            case RealMode:
                state.ActualShellOperation = true;
                proceedWithRealEngine = true;
                break;

            case PartialMode:
                MoveExactFile(
                    paths.PartialFirstSourcePath,
                    paths.PartialFirstDestinationPath);
                state.SimulatedOperationsAborted = true;
                state.FilesystemCompletedAtUtc = DateTimeOffset.UtcNow;
                break;

            case CancelMode:
                state.SimulatedOperationsAborted = true;
                break;

            case LateMode:
                // The watchdog-abandoned scenario: the filesystem part is
                // already complete when the engine is declared dead.
                MoveExactFile(paths.LateSourcePath, paths.LateDestinationPath);
                state.FilesystemCompletedAtUtc = DateTimeOffset.UtcNow;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported owned Shell move mode '{mode}'.");
        }

        lock (s_sync)
        {
            s_invocations.Add(state);
        }

        return true;
    }

    /// <summary>
    /// Throws the simulated mode's outcome through the modern engine's own
    /// typed exceptions so the product's cancel adapter, failure reporting
    /// and persistence reconciliation are exercised exactly as they would
    /// be for a real partial cancel or watchdog abandonment.
    /// </summary>
    [DoesNotReturn]
    internal static void ThrowSimulatedModernOutcome(
        IReadOnlyList<FileService.FileTransferPlan> plans)
    {
        AotShellMoveInvocationState state = RequireLatestInvocation(plans);
        state.CompletedCount = plans.Count(plan =>
            FileService.IsCompletedShellMove(
                plan.SourcePath,
                plan.DestinationPath));
        state.CompletedCountAtProductReturn = state.CompletedCount;
        state.NativeTaskReturned = true;
        state.NativeTaskReturnedAtUtc = DateTimeOffset.UtcNow;
        state.ProductReturnedAtUtc = DateTimeOffset.UtcNow;

        if (state.Mode == PartialMode || state.Mode == CancelMode)
        {
            // The view model's cancel adapter turns the completed set into
            // the same partial-count feedback the legacy bridge produced.
            state.FileServiceOutcome = ReturnedOutcome;
            throw new FileService.FileTransferCanceledException(
                plans
                    .Where(plan => FileService.IsCompletedShellMove(
                        plan.SourcePath,
                        plan.DestinationPath))
                    .Select(plan => new FileService.FileTransferResult(
                        plan.SourcePath,
                        plan.DestinationPath))
                    .ToList(),
                CancellationToken.None);
        }

        if (state.Mode == LateMode)
        {
            state.FileServiceOutcome = AbandonedButCompletedOutcome;
            throw new StaOperationAbandonedException(
                TimeSpan.Zero,
                TimeSpan.Zero,
                processAbandonmentCount: 0,
                workerName: "AotShellMoveFixture simulated abandonment");
        }

        throw new InvalidOperationException(
            $"The owned Shell move fixture cannot simulate mode '{state.Mode}'.");
    }

    /// <summary>
    /// Completion report for the Real mode: records the returned completed
    /// count and the Returned outcome at the product's success return
    /// point.
    /// </summary>
    internal static void RecordModernRealCompletion(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        int completedCount)
    {
        if (!IsExactScenario())
        {
            return;
        }

        lock (s_sync)
        {
            AotShellMoveInvocationState state = RequireLatestInvocation(plans);
            if (!state.ActualShellOperation)
            {
                return;
            }

            state.CompletedCount = completedCount;
            state.CompletedCountAtProductReturn = completedCount;
            state.NativeTaskReturned = true;
            state.NativeTaskReturnedAtUtc = DateTimeOffset.UtcNow;
            state.ProductReturnedAtUtc = DateTimeOffset.UtcNow;
            state.FileServiceOutcome = ReturnedOutcome;
        }
    }

    private static AotShellMoveInvocationState RequireLatestInvocation(
        IReadOnlyList<FileService.FileTransferPlan> plans)
    {
        string[] sources = plans.Select(plan => plan.SourcePath).ToArray();
        lock (s_sync)
        {
            return s_invocations
                .LastOrDefault(candidate =>
                    candidate.SourcePaths.SequenceEqual(
                        sources,
                        StringComparer.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(candidate.FileServiceOutcome)) ??
                throw new InvalidOperationException(
                    "The owned Shell move simulation has no matching invocation.");
        }
    }

    internal static IReadOnlyList<AotShellMoveInvocationSnapshot> CaptureInvocations()
    {
        lock (s_sync)
        {
            return s_invocations
                .OrderBy(state => state.Sequence)
                .Select(state => state.ToSnapshot())
                .ToArray();
        }
    }

    private static string ValidateAndResolveMode(
        AotShellMoveFixturePaths paths,
        IReadOnlyList<FileService.FileTransferPlan> plans,
        bool requireMutatePhase)
    {
        if (requireMutatePhase &&
            !string.Equals(
                Environment.GetEnvironmentVariable(PhaseEnvironmentVariable),
                "Mutate",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Controlled Shell move execution is available only during the mutate phase.");
        }
        if (plans.Count == 0)
        {
            throw new InvalidOperationException(
                "The controlled Shell move received an empty transfer plan.");
        }

        foreach (FileService.FileTransferPlan plan in plans)
        {
            string name = Path.GetFileName(plan.SourcePath);
            string expectedSource = Path.Combine(paths.WidgetRoot, name);
            string expectedDestination = Path.Combine(paths.DesktopRoot, name);
            if (!PathsEqual(plan.SourcePath, expectedSource) ||
                !PathsEqual(plan.DestinationPath, expectedDestination) ||
                !paths.OwnedNames.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The controlled Shell move refused a path outside its exact owned source/destination identity.");
            }
        }

        string[] names = plans
            .Select(plan => Path.GetFileName(plan.SourcePath))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.SequenceEqual([paths.RealName], StringComparer.Ordinal))
        {
            return RealMode;
        }
        if (names.SequenceEqual(
                new[] { paths.PartialFirstName, paths.PartialSecondName }
                    .OrderBy(name => name, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return PartialMode;
        }
        if (names.SequenceEqual([paths.CancelName], StringComparer.Ordinal))
        {
            return CancelMode;
        }
        if (names.SequenceEqual([paths.LateName], StringComparer.Ordinal))
        {
            return LateMode;
        }

        throw new InvalidOperationException(
            "The controlled Shell move refused an unsupported owned selection shape.");
    }

    private static void MoveExactFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
        {
            throw new InvalidOperationException(
                "The controlled Shell move file was not in its exact baseline state.");
        }
        File.Move(sourcePath, destinationPath);
    }

    private static bool IsExactScenario() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DESKBOX_AOT_MANAGED_UI_SMOKE"),
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

    private sealed class AotShellMoveInvocationState
    {
        internal int Sequence { get; init; }
        internal string Mode { get; init; } = string.Empty;
        internal long OwnerWindowHandle { get; init; }
        internal List<string> SourcePaths { get; init; } = [];
        internal List<string> DestinationPaths { get; init; } = [];
        internal int PlannedCount { get; init; }
        internal bool ActualShellOperation { get; set; }
        internal bool SimulatedOperationsAborted { get; set; }
        internal int CompletedCount { get; set; }
        internal int CompletedCountAtProductReturn { get; set; }
        internal bool NativeTaskReturned { get; set; }
        internal string FileServiceOutcome { get; set; } = string.Empty;
        internal DateTimeOffset StartedAtUtc { get; init; }
        internal DateTimeOffset? FilesystemCompletedAtUtc { get; set; }
        internal DateTimeOffset? ProductReturnedAtUtc { get; set; }
        internal DateTimeOffset? NativeTaskReturnedAtUtc { get; set; }

        internal AotShellMoveInvocationSnapshot ToSnapshot() => new()
        {
            Sequence = Sequence,
            Mode = Mode,
            OwnerWindowHandle = OwnerWindowHandle,
            SourcePaths = SourcePaths.ToList(),
            DestinationPaths = DestinationPaths.ToList(),
            PlannedCount = PlannedCount,
            ActualShellOperation = ActualShellOperation,
            SimulatedOperationsAborted = SimulatedOperationsAborted,
            CompletedCount = CompletedCount,
            CompletedCountAtProductReturn = CompletedCountAtProductReturn,
            NativeTaskReturned = NativeTaskReturned,
            FileServiceOutcome = FileServiceOutcome,
            StartedAtUtc = StartedAtUtc,
            FilesystemCompletedAtUtc = FilesystemCompletedAtUtc,
            ProductReturnedAtUtc = ProductReturnedAtUtc,
            NativeTaskReturnedAtUtc = NativeTaskReturnedAtUtc
        };
    }
}

internal sealed record AotShellMoveFixturePaths(
    string RunId,
    string FixtureRoot,
    string WidgetRoot,
    string DesktopRoot,
    string BaselinePath,
    string RealName,
    string RealSourcePath,
    string RealDestinationPath,
    string PartialFirstName,
    string PartialFirstSourcePath,
    string PartialFirstDestinationPath,
    string PartialSecondName,
    string PartialSecondSourcePath,
    string PartialSecondDestinationPath,
    string CancelName,
    string CancelSourcePath,
    string CancelDestinationPath,
    string LateName,
    string LateSourcePath,
    string LateDestinationPath)
{
    internal IReadOnlyList<string> OwnedNames =>
    [
        RealName,
        PartialFirstName,
        PartialSecondName,
        CancelName,
        LateName
    ];

    internal IReadOnlyList<AotShellMoveOwnedFile> OwnedFiles =>
    [
        new(RealName, RealSourcePath, RealDestinationPath),
        new(PartialFirstName, PartialFirstSourcePath, PartialFirstDestinationPath),
        new(PartialSecondName, PartialSecondSourcePath, PartialSecondDestinationPath),
        new(CancelName, CancelSourcePath, CancelDestinationPath),
        new(LateName, LateSourcePath, LateDestinationPath)
    ];
}

internal sealed record AotShellMoveOwnedFile(
    string Name,
    string SourcePath,
    string DestinationPath)
{
    internal string DisplayName => Path.GetFileNameWithoutExtension(Name);
}

internal sealed class AotShellMoveInvocationSnapshot
{
    public int Sequence { get; set; }
    public string Mode { get; set; } = string.Empty;
    public long OwnerWindowHandle { get; set; }
    public List<string> SourcePaths { get; set; } = [];
    public List<string> DestinationPaths { get; set; } = [];
    public int PlannedCount { get; set; }
    public bool ActualShellOperation { get; set; }
    public bool SimulatedOperationsAborted { get; set; }
    public int CompletedCount { get; set; }
    public int CompletedCountAtProductReturn { get; set; }
    public bool NativeTaskReturned { get; set; }
    public string FileServiceOutcome { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FilesystemCompletedAtUtc { get; set; }
    public DateTimeOffset? ProductReturnedAtUtc { get; set; }
    public DateTimeOffset? NativeTaskReturnedAtUtc { get; set; }
}
#endif
