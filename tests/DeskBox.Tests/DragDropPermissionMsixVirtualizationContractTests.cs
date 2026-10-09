namespace DeskBox.Tests;

/// <summary>
/// Pins the MSIX registry-virtualization fix for the AppCompat repair path:
/// under a package identity, HKCU writes/deletes land in the copy-on-write
/// private hive, so the per-user clear AND read must route through HKU&lt;SID&gt;
/// (the real hive). A regression back to Registry.CurrentUser makes Store
/// builds "repair" a layer that never goes away while reporting success —
/// and a legacy virtualized delete can even hide the layer from diagnose.
/// </summary>
public sealed class DragDropPermissionMsixVirtualizationContractTests
{
    [Fact]
    public void AppCompatClear_RoutesPerUserWritesThroughHkuSid()
    {
        string source = Read("src/DeskBox/Services/DragDropPermissionService.cs");

        string clear = Slice(
            source,
            "public static int ClearElevationCompatibilityFlags",
            "public static DragDropPermissionRepairResult Repair");
        Assert.Contains("OpenPerUserAppCompatLayersKey(writable: true)", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.CurrentUser", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Root.OpenSubKey", clear, StringComparison.Ordinal);

        // HKLM stays on the real hive: it only accepts writes when the
        // process is elevated, which the failure list already reports.
        Assert.Contains(
            "Registry.LocalMachine.OpenSubKey(AppCompatLayersKey, writable: true)",
            clear,
            StringComparison.Ordinal);

        string helper = Slice(
            source,
            "private static RegistryKey? OpenPerUserAppCompatLayersKey",
            "private static void AddAppCompatEntries");
        Assert.Contains("RegistryHive.Users", helper, StringComparison.Ordinal);
        Assert.Contains("identity.User", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void AppCompatDiagnose_RoutesPerUserReadsThroughHkuSid()
    {
        string source = Read("src/DeskBox/Services/DragDropPermissionService.cs");

        string collect = Slice(
            source,
            "private static List<AppCompatEntry> GetRelevantAppCompatEntries",
            "private const string PerUserRootName");
        Assert.Contains("OpenPerUserAppCompatLayersKey(writable: false)", collect, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.CurrentUser", collect, StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        return File.ReadAllText(TestPaths.FromRepository(path));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
