namespace DeskBox.Tests;

/// <summary>
/// Pins the structural invariant "the settings-host UI thread never blocks
/// on a cross-process IPC roundtrip" (R10, dump-proven wedge family: a
/// sync-over-async wait on the STA thread parks it in the COM modal loop,
/// which stops pumping window messages — the X button and WM_CLOSE die).
/// The Remote facades' remaining synchronous members are the R11-graded
/// B-level set scheduled for the R14 async-ification batch; this ratchet
/// freezes their number so no NEW sync-over-async call can creep in, and
/// only shrinks as R14 lands.
/// </summary>
public sealed class SettingsIpcThreadingContractTests
{
    // 32 at deep-review R13 (R11 graded the family ~26 by member count;
    // this counts every call site, including overloads).
    private const int FrozenSyncCallCeiling = 32;

    [Fact]
    public void RemoteFacades_SyncOverAsyncCalls_OnlyShrink()
    {
        string root = FindRepositoryRoot();
        int total = Directory.EnumerateFiles(
                Path.Combine(root, "src/DeskBox/Services"),
                "Remote*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .Select(source => CountOccurrences(source, ".GetAwaiter().GetResult()"))
            .Sum();

        Assert.True(
            total <= FrozenSyncCallCeiling,
            $"Remote facades grew to {total} synchronous IPC call sites " +
            $"(ceiling {FrozenSyncCallCeiling}). Sync-over-async on the " +
            "settings-host UI thread is the R10 wedge family — add an " +
            "async path or a fire-and-forget notification instead, or " +
            "lower the ceiling if R14 removed some.");
    }

    private static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DeskBox.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
