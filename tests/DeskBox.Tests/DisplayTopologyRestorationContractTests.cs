namespace DeskBox.Tests;

public sealed class DisplayTopologyRestorationContractTests
{
    [Fact]
    public void AutomaticDisplayRestore_DoesNotFeedPhysicalBoundsBackIntoConfig()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string method = ExtractMethod(source, "public bool TryRestoreBoundsForDisplayTopology()");

        Assert.Contains("updateConfig: false", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveDebounced", method, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateConfigBoundsFromPhysical", method, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowDisplayMessages_QueueTheCentralCoordinator()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string method = ExtractMethod(source, "protected void RestoreBoundsAfterDisplayChange()");

        Assert.Contains("RequestDisplayTopologyRestore", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRestoreBoundsForCurrentTopology", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactDisplayRestore_UsesTheTargetWorkAreaDpi()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractMethod(source, "private RectInt32 GetCompactBounds(RectInt32 expandedOrCurrent)");

        Assert.Contains("ResolveCompactWorkArea(expandedOrCurrent)", method, StringComparison.Ordinal);
        Assert.Contains("WidgetPositioningService.GetDpiScale(targetWorkArea)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDpiScaleForWindow", method, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowInitialConstruction_PreservesMonitorIdentityAndReconcilesAfterLoaded()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string method = ExtractMethod(source, "protected void ConfigureWindowCore()");

        Assert.Contains("WidgetPositioningService.ResolveBoundsForCurrentTopology", method, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetPositioningService.GetAvailableWorkAreas()", method, StringComparison.Ordinal);
        Assert.Contains("TryRestoreBoundsForCurrentTopology(allowHidden: true, updateConfig: false)", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method signature: {signature}");

        int brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Missing method body: {signature}");
        int depth = 0;
        for (int index = brace; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unterminated method body: {signature}");
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
