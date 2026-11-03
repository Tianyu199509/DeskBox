namespace DeskBox.Tests;

/// <summary>
/// P1-1 regression pins (incident 2026-09-30 §4): on NativeAOT retail builds
/// the cloud-backup PasswordBox was the one deferred-section control no XAML
/// codegen references, so its projected type was trimmed out of reflection and
/// <c>SettingsWindow.get_CloudBackupPasswordBox()</c> threw
/// InvalidCastException on 测试连接/保存密码. This is a STRUCTURAL source
/// contract: it pins that (a) the typed lookup roots the projected PasswordBox
/// type for WinRT.Runtime's FindTypeByName resolution via DynamicDependency,
/// (b) the accessor is null-safe instead of null-forgiving, (c) both click
/// handlers guard the element and log rather than crash when it is missing,
/// and (d) the AOT deep-settings smoke resolves the typed element on the real
/// (retail-only) build. It cannot execute the marshaling path itself.
/// </summary>
public sealed class CloudBackupPasswordBoxAotGuardTests
{
    private const string SectionElementsPath =
        "src/DeskBox/Views/SettingsWindow.SectionElements.cs";
    private const string CloudBackupPath =
        "src/DeskBox/Views/SettingsWindow.CloudBackup.cs";
    private const string DeepSmokePath =
        "src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs";

    [Fact]
    public void TypedLookup_RootsTheProjectedPasswordBoxForAotNameResolution()
    {
        string source = ReadRepositoryFile(SectionElementsPath);

        // The root is what makes WinRT.Runtime's FindTypeByName resolve the
        // exact projected type before the first (identity-cached) RCW wrap;
        // without it the wrap falls back to a base class and every cast path
        // throws InvalidCastException (retail AOT only — JIT builds resolve
        // through full reflection, which is why Debug never saw it).
        Assert.Contains("using System.Diagnostics.CodeAnalysis;", source, StringComparison.Ordinal);
        Assert.Contains("[DynamicDependency(", source, StringComparison.Ordinal);
        Assert.Contains("DynamicallyAccessedMemberTypes.All,", source, StringComparison.Ordinal);
        Assert.Contains(
            "typeof(global::Microsoft.UI.Xaml.Controls.PasswordBox))]",
            source,
            StringComparison.Ordinal);

        // The rooted acquisition keeps the frozen one-line typed lookup shape
        // also pinned by SettingsSectionElementAotContractTests.
        Assert.Contains(
            "private global::Microsoft.UI.Xaml.Controls.PasswordBox? FindCloudBackupPasswordBox() =>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.PasswordBox>(\"Maintenance\", \"CloudBackupPasswordBox\");",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CloudBackupPasswordBoxAccessor_IsNullSafe_NotNullForgiving()
    {
        string source = ReadRepositoryFile(SectionElementsPath);

        // The deferred section is created on demand, so the accessor's
        // contract is nullable; the old `!` turned a not-yet-created section
        // into a guaranteed NullReferenceException at the consumer.
        Assert.Contains(
            "private global::Microsoft.UI.Xaml.Controls.PasswordBox? CloudBackupPasswordBox =>",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"CloudBackupPasswordBox\")!;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordBoxConsumers_GuardTheElementAndStayObservable()
    {
        string source = ReadRepositoryFile(CloudBackupPath);

        // No direct dereference of the accessor's result: every read/write of
        // .Password goes through a null-guarded local.
        Assert.DoesNotContain("CloudBackupPasswordBox.Password", source, StringComparison.Ordinal);

        Assert.Contains(
            "CloudBackupSavePasswordButton_Click(object sender, RoutedEventArgs e)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CloudBackupTestConnectionButton_Click(object sender, RoutedEventArgs e)",
            source,
            StringComparison.Ordinal);
        // Both handlers must keep the guard (teardown race) — and it must log,
        // so a degraded path stays visible instead of silently no-oping.
        Assert.Equal(
            2,
            CountOccurrences(source, "CloudBackupPasswordBox is not { } passwordBox"));
        Assert.Equal(
            2,
            CountOccurrences(source, "App.Log(\"[CloudBackup]"));
    }

    [Fact]
    public void AotDeepSettingsSmoke_ResolvesTheTypedPasswordBoxOnTheCloudPage()
    {
        string source = ReadRepositoryFile(DeepSmokePath);

        // The retail-only deep smoke is the executable regression net: it
        // visits the cloud-backup page and resolves the typed element, which
        // throws InvalidCastException on an unfixed NativeAOT build and
        // returns null if the typed lookup is ever weakened again.
        Assert.Contains(
            "NavigateToSettingsSection(sectionTag)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (string.Equals(sectionTag, \"Maintenance\", StringComparison.Ordinal))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (CloudBackupPasswordBox is null)",
            source,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
