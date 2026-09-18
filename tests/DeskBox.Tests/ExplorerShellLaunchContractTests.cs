using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class ExplorerShellLaunchContractTests
{
    [Fact]
    public void ExplorerLaunch_UsesDesktopHostedShellInsteadOfLocalShellApplication()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ExplorerShellLaunchService.cs"));

        Assert.Contains("FindWindowSW", source, StringComparison.Ordinal);
        Assert.Contains("ShellWindowClassDesktop", source, StringComparison.Ordinal);
        Assert.Contains("desktop.Document", source, StringComparison.Ordinal);
        Assert.Contains("document.Application", source, StringComparison.Ordinal);
        Assert.Contains("explorerShell.ShellExecute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("shell.ShellExecute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerLaunch_TransfersTheExplicitUserForegroundPrivilege()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ExplorerShellLaunchService.cs"));
        string rust = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-native/src/explorer_shell_launch.rs"));

        int processGrant = service.IndexOf(
            "TryGrantExplorerForegroundActivation();",
            StringComparison.Ordinal);
        int nativeLaunch = service.IndexOf(
            "ExplorerShellLaunchNativeBackend.TryOpen(",
            StringComparison.Ordinal);

        Assert.True(processGrant >= 0 && processGrant < nativeLaunch);
        Assert.Contains("AllowSetForegroundWindow(explorerProcessId)", service, StringComparison.Ordinal);
        Assert.Contains("Marshal.GetIUnknownForObject", service, StringComparison.Ordinal);
        Assert.Contains("CoAllowSetForegroundWindow(", service, StringComparison.Ordinal);
        Assert.Contains(
            "CoAllowSetForegroundWindow(&explorer_shell, None)",
            rust,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenFile_TriesExplorerEnvironmentBeforeLocalShellFallback()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        string method = Slice(
            source,
            "public static bool OpenFileOrChooseApp",
            "internal static string ResolveShellLaunchDirectory");

        int explorerLaunch = method.IndexOf(
            "ExplorerShellLaunchService.TryOpen",
            StringComparison.Ordinal);
        int localFallback = method.IndexOf("Process.Start(startInfo)", StringComparison.Ordinal);

        Assert.True(explorerLaunch >= 0, "Missing Explorer-hosted launch.");
        Assert.True(localFallback > explorerLaunch, "Local launch must remain a fallback.");
        Assert.Contains("SHOpenWithDialog(ownerWindow", method, StringComparison.Ordinal);
    }

    [Fact]
    public void UriLaunch_DoesNotTreatUriSchemeAsAWorkingDirectory()
    {
        Assert.Equal(
            string.Empty,
            Win32Helper.ResolveShellLaunchDirectory("https://deskbox.fun/features"));
        Assert.Equal(
            string.Empty,
            Win32Helper.ResolveShellLaunchDirectory(
                "ms-windows-store://pdp/?productid=9PBZSNB4D69H"));
        Assert.Equal(
            @"C:\Apps\Hermes",
            Win32Helper.ResolveShellLaunchDirectory(@"C:\Apps\Hermes\Hermes.exe"));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
