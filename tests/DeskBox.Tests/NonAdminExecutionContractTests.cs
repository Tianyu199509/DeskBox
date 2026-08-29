namespace DeskBox.Tests;

public sealed class NonAdminExecutionContractTests
{
    [Fact]
    public void Manifest_StaysAtInvokerWithoutUiAccess()
    {
        string manifest = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/app.manifest"));

        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
        Assert.Contains("uiAccess=\"false\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_OffersBothScopesAndLaunchesTheAppAsOriginalUser()
    {
        string installer = File.ReadAllText(TestPaths.FromRepository("installer/DeskBox.iss"));

        Assert.Contains("PrivilegesRequired=admin", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousPrivileges=yes", installer, StringComparison.Ordinal);
        Assert.Contains("runasoriginaluser", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectStartupTask_UsesInboxClientWithoutShellOrElevation()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DirectStartupTaskBackend.cs"));

        Assert.Contains("Environment.SystemDirectory", source, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", source, StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow = true", source, StringComparison.Ordinal);
        Assert.Contains("InteractiveToken", source, StringComparison.Ordinal);
        Assert.Contains("LeastPrivilege", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HighestAvailable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runas", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", source, StringComparison.OrdinalIgnoreCase);
    }
}
