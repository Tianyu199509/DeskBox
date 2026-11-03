using DeskBox;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;
using Windows.Graphics;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox.Tests;

/// <summary>
/// Pins the pure Win32 tray icon &amp; native popup menu host contracts that
/// replaced H.NotifyIcon.WinUI and its two permanent hidden XAML windows.
/// </summary>
public sealed class TrayContextMenuResilienceTests
{
    [Fact]
    public void DeterministicIconGuid_IsCaseInsensitivePerExecutableAndDistinctAcrossPaths()
    {
        Guid installedLower = NativeTrayIconHost.ComputeDeterministicIconGuid(
            @"c:\program files\deskbox\deskbox.exe");
        Guid installedUpper = NativeTrayIconHost.ComputeDeterministicIconGuid(
            @"C:\Program Files\DeskBox\DeskBox.exe");
        Guid debugRepo = NativeTrayIconHost.ComputeDeterministicIconGuid(
            @"D:\project\wingezi\src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe");

        Assert.NotEqual(Guid.Empty, installedLower);
        Assert.Equal(installedLower, installedUpper);
        Assert.NotEqual(installedLower, debugRepo);
    }

    [Fact]
    public void MenuGlyphBitmapRenderer_ProducesValidBitmapAcrossThemesAndStates()
    {
        IntPtr darkEnabled = NativeTrayIconHost.CreateMenuGlyphBitmapForTesting(
            '\uE713',
            dpi: 120,
            isDarkTheme: true,
            isEnabled: true);
        IntPtr lightDisabled = NativeTrayIconHost.CreateMenuGlyphBitmapForTesting(
            '\uE74C',
            dpi: 96,
            isDarkTheme: false,
            isEnabled: false);

        try
        {
            Assert.NotEqual(IntPtr.Zero, darkEnabled);
            Assert.NotEqual(IntPtr.Zero, lightDisabled);
        }
        finally
        {
            NativeTrayIconHost.DeleteBitmapForTesting(darkEnabled);
            NativeTrayIconHost.DeleteBitmapForTesting(lightDisabled);
        }
    }

    [Fact]
    public void BuildTrayMenuEntriesCore_IncludesAllFixedActionsWidgetDescriptorsAndConditionalUpdate()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-tray-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var expectedCreateKinds = new WidgetContentFactory(localization)
                .GetCreateEntryDescriptors()
                .Select(descriptor => descriptor.WidgetKind)
                .ToArray();

            WidgetKind? invokedKind = null;
            IReadOnlyList<NativeTrayMenuEntry> withoutUpdate = App.BuildTrayMenuEntriesCore(
                localization,
                canCreateWidget: true,
                hasUpdateAvailable: false,
                availableUpdateVersion: null,
                () => Task.CompletedTask,
                kind =>
                {
                    invokedKind = kind;
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask);

            IReadOnlyList<NativeTrayMenuEntry> withUpdate = App.BuildTrayMenuEntriesCore(
                localization,
                canCreateWidget: false,
                hasUpdateAvailable: true,
                availableUpdateVersion: "2.0.0",
                () => Task.CompletedTask,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask);

            Assert.Contains(expectedCreateKinds, kind => kind == WidgetKind.File);
            Assert.Equal(withUpdate.Count - 1, withoutUpdate.Count);

            var nonSeparatorItems = withoutUpdate.Where(entry => !entry.IsSeparator).ToList();
            Assert.Equal(6 + expectedCreateKinds.Length, nonSeparatorItems.Count);

            NativeTrayMenuEntry fileCreateEntry = nonSeparatorItems.First(
                entry => entry.CommandId >= 100 && entry.Text == localization.T("Common.NewWidget"));
            fileCreateEntry.Action!().GetAwaiter().GetResult();
            Assert.Equal(WidgetKind.File, invokedKind);

            // When WidgetManager is unavailable, widget-mutating items disable cleanly
            // while Settings and Exit remain clickable.
            var disabledItems = withUpdate.Where(entry => !entry.IsSeparator && !entry.IsEnabled).ToList();
            Assert.Equal(2 + expectedCreateKinds.Length, disabledItems.Count);
            Assert.Contains(withUpdate, entry => !entry.IsSeparator && entry.Text.Contains("2.0.0", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunTraySettingsActionAsync_HasNoSecondWindowSettleDelay()
    {
        string tray = Read("src/DeskBox/App.Tray.cs");
        string settings = Slice(
            tray,
            "private async Task RunTraySettingsActionAsync",
            "internal async Task CreateFolderWidgetFromPickerAsync");

        Assert.Contains("BeginWidgetInteraction(\"tray-settings-opening\")", settings, StringComparison.Ordinal);
        Assert.Contains("EndWidgetInteraction(\"tray-settings-opened\")", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(300)", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondWindowContextMenuOpened", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayContextMenuWindow_ComputeWindowBounds_AlignsToTaskbarEdgesAndClampsInsideWorkArea()
    {
        var workArea = new Win32Helper.RECT
        {
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1032,
        };

        // Bottom taskbar centered above tray icon, clamped inside right screen edge margin (6 * 1.5 = 9px).
        RectInt32 bottomRight = TrayContextMenuWindow.ComputeWindowBounds(
            new DrawingPoint(1880, 1031),
            TrayMenuHorizontalAlignment.Center,
            TrayMenuVerticalAlignment.Bottom,
            desiredWidthPx: 300,
            desiredHeightPx: 540,
            dpiScale: 1.5,
            workArea);
        Assert.Equal(1920 - 9 - 300, bottomRight.X);
        Assert.Equal(1031 - 540 - 9, bottomRight.Y);
        Assert.Equal(300, bottomRight.Width);
        Assert.Equal(540, bottomRight.Height);

        // Top taskbar drops downward with scaled gap; oversized menu height clamps cleanly to workArea.
        RectInt32 topOversized = TrayContextMenuWindow.ComputeWindowBounds(
            new DrawingPoint(960, 48),
            TrayMenuHorizontalAlignment.Center,
            TrayMenuVerticalAlignment.Top,
            desiredWidthPx: 260,
            desiredHeightPx: 1400,
            dpiScale: 1.0,
            new Win32Helper.RECT { Left = 0, Top = 48, Right = 1920, Bottom = 1080 });
        Assert.Equal(960 - 130, topOversized.X);
        Assert.Equal(48 + 6, topOversized.Y);
        Assert.Equal((1080 - 48) - 12, topOversized.Height);
    }

    [Fact]
    public void EphemeralFluentTrayMenu_KeepsColdBootWin32OnlyAndTearsDownBackdropOnDismiss()
    {
        string tray = Read("src/DeskBox/App.Tray.cs");
        string createTray = Slice(
            tray,
            "private void CreateTrayIcon()",
            "private void OnTrayLeftClickRequested()");
        string menuWindow = Read("src/DeskBox/Views/TrayContextMenuWindow.cs");

        // Cold-boot CreateTrayIcon stays 100% pure Win32 with zero XAML Window allocations.
        Assert.DoesNotContain("TrayContextMenuWindow", createTray, StringComparison.Ordinal);
        Assert.DoesNotContain("new Window", createTray, StringComparison.Ordinal);

        // Right-click opens TrayContextMenuWindow on demand, avoids unconstrained Popup islands,
        // and explicitly clears SystemBackdrop + calls Close() on dismissal.
        Assert.Contains("TrayContextMenuWindow.TryShow(", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldConstrainToRootBounds", menuWindow, StringComparison.Ordinal);
        Assert.Contains("SystemBackdrop = null;", menuWindow, StringComparison.Ordinal);
        Assert.Contains("Close();", menuWindow, StringComparison.Ordinal);
        Assert.Contains("_ensureLifelineBeforeClose();", menuWindow, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
