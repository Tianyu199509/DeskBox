namespace DeskBox.Tests;

/// <summary>
/// Opens must stay diagnosable and observation-only when the machine's Shell
/// COM layer is wedged (feedback #9, 2026-09-15: every Explorer-hosted launch
/// failed with aggregate HRESULT 0x00000000 while the real failure sat in the
/// desktop phase, and .lnk resolution hung local ShellExecuteEx forever).
/// </summary>
public sealed class OpenFileResilienceContractTests
{
    /// <summary>
    /// The Explorer-shell failure detail must carry every phase HRESULT: the
    /// aggregate OperationHResult is regularly 0x00000000 while the failing
    /// phase holds the real code, so a single aggregate cannot locate the
    /// broken step.
    /// </summary>
    [Fact]
    public void ExplorerShellLaunch_FailureDetailCarriesPerPhaseHResults()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Helpers/ExplorerShellLaunchNativeBackend.cs"));

        Assert.Contains("phaseHr com=", source, StringComparison.Ordinal);
        Assert.Contains("windows=0x", source, StringComparison.Ordinal);
        Assert.Contains("desktop=0x", source, StringComparison.Ordinal);
        Assert.Contains("document=0x", source, StringComparison.Ordinal);
        Assert.Contains("application=0x", source, StringComparison.Ordinal);
        Assert.Contains("execute=0x", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The local ShellExecuteEx fallback may only observe a hang, never abort
    /// it or release the caller's slot — native Shell calls cannot be safely
    /// aborted, and aborting risks late completion after a reported failure
    /// (double opens). The pending marker after the threshold is the whole
    /// mechanism.
    /// </summary>
    [Fact]
    public void LocalShellExecuteFallback_ObservesPendingLaunchesWithoutAborting()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Platform/Win32Helper.cs"));

        Assert.Contains(
            "local ShellExecuteEx still pending for",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Observation only: never abort the call or release the caller's slot",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The STA runner's documented invariant — native Shell calls cannot be
    /// safely aborted, so a cancelled caller never releases a running call's
    /// slot — is the safety decision the pending-observation fallback relies
    /// on. Rewriting the runner without it would reintroduce the abort risk.
    /// </summary>
    [Fact]
    public void BoundedStaOperationRunner_KeepsTheNoAbortSlotInvariant()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Helpers/BoundedStaOperationRunner.cs"));

        Assert.Contains(
            "native Shell calls cannot be safely aborted",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Busy feedback must keep pointing at the recovery path: on a wedged
    /// Shell every open lands on Busy, and without the restart-Explorer hint
    /// the message reads as a DeskBox defect instead of an actionable state.
    /// </summary>
    [Fact]
    public void OpenItemBusyCopy_KeepsTheShellRecoveryHint()
    {
        string en = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Strings/en-US.json"));
        string zh = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Strings/zh-CN.json"));

        Assert.Contains("restarting File Explorer restores it", en, StringComparison.Ordinal);
        Assert.Contains("重启资源管理器即可恢复", zh, StringComparison.Ordinal);
    }

    private static string GetRepoFile(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
    }
}
