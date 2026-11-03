using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class ElevatedShellOutboundDropTests
{
    private static readonly string[] ExpectedLocales =
    [
        "ar-SA",
        "bn-BD",
        "de-DE",
        "en-US",
        "es-ES",
        "fr-FR",
        "hi-IN",
        "ja-JP",
        "ko-KR",
        "pt-BR",
        "ru-RU",
        "zh-CN",
        "zh-TW",
    ];

    [Theory]
    [InlineData(new[] { "SysListView32", "SHELLDLL_DefView", "Progman" }, "Progman", true, false, nameof(ElevatedOutboundTargetKind.Desktop))]
    [InlineData(new[] { "SysListView32", "SHELLDLL_DefView", "WorkerW" }, "WorkerW", true, false, nameof(ElevatedOutboundTargetKind.Desktop))]
    [InlineData(new[] { "WorkerW" }, "WorkerW", false, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "DirectUIHWND", "SHELLDLL_DefView", "#32770" }, "#32770", false, false, nameof(ElevatedOutboundTargetKind.ForeignWindow))]
    [InlineData(new[] { "DirectUIHWND", "SHELLDLL_DefView", "ShellTabWindowClass", "CabinetWClass" }, "CabinetWClass", true, false, nameof(ElevatedOutboundTargetKind.ExplorerFolder))]
    [InlineData(new[] { "ExploreWClass" }, "ExploreWClass", true, false, nameof(ElevatedOutboundTargetKind.ExplorerFolder))]
    [InlineData(new[] { "MSTaskListWClass", "Shell_TrayWnd" }, "Shell_TrayWnd", true, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "Shell_SecondaryTrayWnd" }, "Shell_SecondaryTrayWnd", true, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "NotifyIconOverflowWindow" }, "NotifyIconOverflowWindow", true, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "TopLevelWindowForOverflowXamlIsland" }, "TopLevelWindowForOverflowXamlIsland", true, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "TaskListThumbnailWnd" }, "TaskListThumbnailWnd", true, false, nameof(ElevatedOutboundTargetKind.None))]
    [InlineData(new[] { "Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1" }, "Chrome_WidgetWin_1", false, false, nameof(ElevatedOutboundTargetKind.ForeignWindow))]
    [InlineData(new[] { "WeChatMainWndForPC" }, "WeChatMainWndForPC", false, false, nameof(ElevatedOutboundTargetKind.ForeignWindow))]
    [InlineData(new[] { "Chrome_WidgetWin_1" }, "Chrome_WidgetWin_1", false, true, nameof(ElevatedOutboundTargetKind.None))]
    public void ClassifyWindowClasses_DistinguishesDesktopExplorerChromeAndForeignApps(
        string[] ancestors,
        string rootClass,
        bool matchesShellPid,
        bool isCurrentProcess,
        string expectedKindName)
    {
        ElevatedOutboundTargetKind expected =
            Enum.Parse<ElevatedOutboundTargetKind>(expectedKindName);
        ElevatedOutboundTargetKind actual =
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ancestors,
                rootClass,
                matchesShellPid,
                isCurrentProcess);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LiveNativeInterop_ResolvesZeroAndShellWindowWithoutEntryPointErrors()
    {
        ElevatedOutboundTargetInfo zeroResult =
            ElevatedShellOutboundDropInterop.ResolveOutboundTarget(
                IntPtr.Zero,
                IntPtr.Zero);
        Assert.Equal(ElevatedOutboundTargetKind.None, zeroResult.Kind);

        Assert.False(
            ElevatedShellOutboundDropInterop.TryPositionDesktopItemsNearCursor(
                [],
                screenX: 120,
                screenY: 120));

        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow != IntPtr.Zero && Win32Helper.IsWindowVisible(shellWindow))
        {
            ElevatedOutboundTargetInfo desktopResult =
                ElevatedShellOutboundDropInterop.ResolveOutboundTarget(
                    shellWindow,
                    shellWindow);
            Assert.Equal(ElevatedOutboundTargetKind.Desktop, desktopResult.Kind);
            Assert.False(string.IsNullOrWhiteSpace(desktopResult.DestinationFolderPath));
            Assert.True(Directory.Exists(desktopResult.DestinationFolderPath));
        }
    }

    [Theory]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true, false, false, false, nameof(FileDropIntent.Move))]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, false, false, false, false, nameof(FileDropIntent.Copy))]
    [InlineData(SettingsService.ManagedDragOutActionMove, false, false, false, false, nameof(FileDropIntent.Move))]
    [InlineData(SettingsService.ManagedDragOutActionCopy, true, false, false, false, nameof(FileDropIntent.Copy))]
    [InlineData(SettingsService.ManagedDragOutActionMove, true, true, false, false, nameof(FileDropIntent.Copy))]
    [InlineData(SettingsService.ManagedDragOutActionCopy, false, false, true, false, nameof(FileDropIntent.Move))]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true, false, false, true, nameof(FileDropIntent.Shortcut))]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true, true, true, false, nameof(FileDropIntent.Shortcut))]
    public void ResolveElevatedOutboundDropIntent_HonorsSettingsVolumeAndModifiers(
        string action,
        bool sameVolume,
        bool ctrl,
        bool shift,
        bool alt,
        string expectedIntentName)
    {
        FileDropIntent expected = Enum.Parse<FileDropIntent>(expectedIntentName);
        FileDropIntent actual = FileSurfaceContent.ResolveElevatedOutboundDropIntent(
            action,
            sameVolume,
            ctrl,
            shift,
            alt);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveUserAccountControlSettingsLauncherPath_PrefersFullSystemBinary()
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "DeskBoxUacLauncherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Equal(
                "UserAccountControlSettings.exe",
                DragDropPermissionService.ResolveUserAccountControlSettingsLauncherPath(tempDir));

            string fakeBinary = Path.Combine(tempDir, "UserAccountControlSettings.exe");
            File.WriteAllBytes(fakeBinary, [0x4D, 0x5A]);

            Assert.Equal(
                fakeBinary,
                DragDropPermissionService.ResolveUserAccountControlSettingsLauncherPath(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        string settingsMaintenance = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Maintenance.cs"));
        string noticeWindow = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ElevationNoticeWindow.xaml.cs"));
        Assert.Contains(
            "DragDropPermissionService.OpenUserAccountControlSettings",
            settingsMaintenance,
            StringComparison.Ordinal);
        Assert.Contains(
            "DragDropPermissionService.OpenUserAccountControlSettings",
            noticeWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShowElevationNoticeIfNeeded_UsesNonBlockingToastForFullyElevatedSessions()
    {
        string appSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        int start = appSource.IndexOf(
            "private void ShowElevationNoticeIfNeeded()",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "ShowElevationNoticeIfNeeded not found in App.xaml.cs.");

        int end = appSource.IndexOf(
            "private void ShowFullyElevatedNoticeToast()",
            start,
            StringComparison.Ordinal);
        Assert.True(end > start, "ShowFullyElevatedNoticeToast boundary not found.");

        string methodBody = appSource[start..end];
        int sameLevelMarker = methodBody.IndexOf(
            "FullyElevatedAttribution == FullyElevatedSessionAttribution.None",
            StringComparison.Ordinal);
        Assert.True(sameLevelMarker >= 0);

        string fullyElevatedTail = methodBody[sameLevelMarker..];
        Assert.Contains(
            "ShowFullyElevatedNoticeToast();",
            fullyElevatedTail,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CreateTrackedElevationNoticeWindow()",
            fullyElevatedTail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AllLocales_DefineElevatedOutboundStringsWithConsistentPlaceholders()
    {
        foreach (string locale in ExpectedLocales)
        {
            string path = TestPaths.FromRepository($"src/DeskBox/Strings/{locale}.json");
            Assert.True(File.Exists(path), $"Missing locale file: {locale}.json");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            Assert.True(
                root.TryGetProperty("Widget.ElevatedDragOut.BridgeClipboardCaption", out JsonElement caption) &&
                !string.IsNullOrWhiteSpace(caption.GetString()),
                $"Missing BridgeClipboardCaption in {locale}.json");

            Assert.True(
                root.TryGetProperty("Widget.ElevatedDragOut.BridgeClipboardReady", out JsonElement ready) &&
                !string.IsNullOrWhiteSpace(ready.GetString()),
                $"Missing BridgeClipboardReady in {locale}.json");
            Assert.Contains("{0}", ready.GetString()!, StringComparison.Ordinal);

            Assert.True(
                root.TryGetProperty("Widget.ElevatedDragOut.UnsupportedShellTarget", out JsonElement unsupported) &&
                !string.IsNullOrWhiteSpace(unsupported.GetString()),
                $"Missing UnsupportedShellTarget in {locale}.json");

            Assert.True(
                root.TryGetProperty("Tray.FullyElevatedNotice.Message", out JsonElement trayMessage) &&
                !string.IsNullOrWhiteSpace(trayMessage.GetString()),
                $"Missing Tray.FullyElevatedNotice.Message in {locale}.json");
        }
    }

    [Fact]
    public void ElevatedShellOutboundDropInterop_RespectsPlatformAndAotContracts()
    {
        string source = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Platform/ElevatedShellOutboundDropInterop.cs"));

        Assert.Contains("namespace DeskBox.Platform;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[ComImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKBOX_STORE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.UI", source, StringComparison.Ordinal);

        MatchCollection imports = Regex.Matches(source, @"\[LibraryImport\s*\(([^)]*)\)\]");
        Assert.NotEmpty(imports);
        foreach (Match match in imports)
        {
            Assert.Contains(
                "EntryPoint =",
                match.Groups[1].Value,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LexicalUnsafeDirectoryTransfer_And_ImmediateOutboundTarget_AvoidBlockingUiHover()
    {
        Assert.True(FileService.IsLexicallyUnsafeDirectoryTransfer(
            [@"C:\Work\Docs"],
            @"C:\Work\Docs"));
        Assert.True(FileService.IsLexicallyUnsafeDirectoryTransfer(
            [@"C:\Work\Docs"],
            @"C:\Work\Docs\2026\Q4"));
        Assert.False(FileService.IsLexicallyUnsafeDirectoryTransfer(
            [@"C:\Work\Docs\report.pdf"],
            @"C:\Work\Docs"));
        Assert.False(FileService.IsLexicallyUnsafeDirectoryTransfer(
            [@"C:\Work\Docs"],
            @"C:\Work\DocsArchive"));

        bool isImmediateZero = ElevatedShellOutboundDropInterop.TryClassifyImmediateOutboundTarget(
            IntPtr.Zero,
            IntPtr.Zero,
            out IntPtr resolvedRoot,
            out IntPtr targetTab,
            out ElevatedOutboundTargetInfo immediate);
        Assert.True(isImmediateZero);
        Assert.Equal(IntPtr.Zero, resolvedRoot);
        Assert.Equal(IntPtr.Zero, targetTab);
        Assert.Equal(ElevatedOutboundTargetKind.None, immediate.Kind);
    }
}
