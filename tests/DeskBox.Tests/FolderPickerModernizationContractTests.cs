using System.Text.RegularExpressions;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FolderPickerModernizationContractTests
{
    [Fact]
    public void FolderPickerService_ShowsTheNativeStaDialogAndLogsEveryOutcome()
    {
        // Decision reversal (2026-10-27, WeChat map-folder diagnostics): the
        // WinAppSDK FolderPicker runs IFileOpenDialog::Show on a background
        // MTA thread and folds every failure — including the dialog never
        // appearing — into a silent null, which users saw as "clicking does
        // nothing". The native STA dialog replaces it; every pick must leave
        // an outcome line (owner source + HRESULT + elapsed separate an
        // environment-killed dialog from a human-paced one).
        string source = ReadSource("src/DeskBox/Services/FolderPickerService.cs");

        Assert.Contains("NativeFileOpenDialog.ShowPickFolder(", source, StringComparison.Ordinal);
        Assert.Contains(
            "public static FolderPickResult PickFolderWithOutcome(",
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
            "FolderPickerService.PickFolderWithOutcome(",
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
        Assert.Contains("WindowNative.GetWindowHandle(_trayWindow)", tray, StringComparison.Ordinal);
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
        Assert.Contains("_folderPickerHelperWindow", tray, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
