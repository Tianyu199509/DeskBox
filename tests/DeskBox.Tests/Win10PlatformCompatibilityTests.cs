using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Distribution;
using DeskBox.Services.DragDrop;
using DeskBox.ViewModels;
using DeskBox.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Geolocation;
using Windows.System;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox.Tests;

public sealed class Win10PlatformCompatibilityTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxWin10CompatTests",
        Guid.NewGuid().ToString("N"));

    public Win10PlatformCompatibilityTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void CustomWidgetGlyphs_NormalizeWin11OnlyAndInvalidCodepointsForWin10()
    {
        Assert.Equal("\uE890", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("eye-off", "\uE71D", isWindows11OrLater: false));
        Assert.Equal("\uED1A", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("eye-off", "\uE71D", isWindows11OrLater: true));
        Assert.Equal("\uE890", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback(@"\uED1A", "\uE71D", isWindows11OrLater: false));
        Assert.Equal("\uE8AB", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("\uE7C2", "\uE71D", isWindows11OrLater: false));
        Assert.Equal("\uE753", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("\uE9C4", "\uE71D", isWindows11OrLater: false));
        Assert.Equal("\uE8A1", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("news", "\uE71D", isWindows11OrLater: false));
        Assert.Equal("\uE8A1", CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback("news", "\uE71D", isWindows11OrLater: true));
    }

    [Fact]
    public void MusicTransportIcon_UsesClassicMdl2GlyphsOnWin10AndBoldFluentOnWin11()
    {
        Assert.Equal(MusicTransportIcon.Win10PreviousGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Previous, isWindows11OrLater: false));
        Assert.Equal(MusicTransportIcon.Win10PlayGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Play, isWindows11OrLater: false));
        Assert.Equal(MusicTransportIcon.Win10PauseGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Pause, isWindows11OrLater: false));
        Assert.Equal(MusicTransportIcon.Win10NextGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Next, isWindows11OrLater: false));

        Assert.Equal(MusicTransportIcon.PreviousGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Previous, isWindows11OrLater: true));
        Assert.Equal(MusicTransportIcon.PlayGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Play, isWindows11OrLater: true));
        Assert.Equal(MusicTransportIcon.PauseGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Pause, isWindows11OrLater: true));
        Assert.Equal(MusicTransportIcon.NextGlyph, MusicTransportIcon.ResolveGlyph(MusicTransportIconKind.Next, isWindows11OrLater: true));
    }

    [Fact]
    public void TrayTaskbarEdgeAndFallbackPlacement_HandleWin10OverflowPopupAndAllFourTaskbarEdges()
    {
        var monitor = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // Win10 NotifyIconOverflowWindow on a bottom taskbar sits inside workArea near right edge.
        var bottomWorkArea = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var overflowIconRect = new Win32Helper.RECT { Left = 1860, Top = 960, Right = 1892, Bottom = 992 };
        Assert.Equal(
            App.TaskbarEdge.Bottom,
            App.ResolveTaskbarEdge(overflowIconRect, monitor, bottomWorkArea, App.TaskbarEdge.Bottom));

        // Win10 auto-hide Left taskbar icon near bottom-left corner stays Left.
        var autoHideWorkArea = monitor;
        var leftAutoHideIconRect = new Win32Helper.RECT { Left = 16, Top = 1040, Right = 48, Bottom = 1072 };
        Assert.Equal(
            App.TaskbarEdge.Left,
            App.ResolveTaskbarEdge(leftAutoHideIconRect, monitor, autoHideWorkArea, App.TaskbarEdge.Left));

        // Win10 Top-docked taskbar fallback must open downward (VerticalAlignment.Top).
        var topWorkArea = new Win32Helper.RECT { Left = 0, Top = 40, Right = 1920, Bottom = 1080 };
        App.TrayMenuPlacement topPlacement = App.ResolveFallbackTrayMenuPlacement(
            new DrawingPoint(1750, 20),
            isRightToLeft: false,
            (monitor, topWorkArea),
            App.TaskbarEdge.Top);
        Assert.Equal(TrayMenuVerticalAlignment.Top, topPlacement.VerticalAlignment);
        Assert.Equal(40, topPlacement.Anchor.Y);

        // Win10 Right-docked taskbar fallback keeps unshifted X edge and opens leftward.
        var rightWorkArea = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1858, Bottom = 1080 };
        App.TrayMenuPlacement rightPlacement = App.ResolveFallbackTrayMenuPlacement(
            new DrawingPoint(1880, 950),
            isRightToLeft: false,
            (monitor, rightWorkArea),
            App.TaskbarEdge.Right);
        Assert.Equal(TrayMenuHorizontalAlignment.Right, rightPlacement.HorizontalAlignment);
        Assert.Equal(TrayMenuVerticalAlignment.Center, rightPlacement.VerticalAlignment);
        Assert.Equal(1857, rightPlacement.Anchor.X);

        // Win10 Left-docked taskbar fallback opens rightward.
        var leftWorkArea = new Win32Helper.RECT { Left = 62, Top = 0, Right = 1920, Bottom = 1080 };
        App.TrayMenuPlacement leftPlacement = App.ResolveFallbackTrayMenuPlacement(
            new DrawingPoint(30, 950),
            isRightToLeft: false,
            (monitor, leftWorkArea),
            App.TaskbarEdge.Left);
        Assert.Equal(TrayMenuHorizontalAlignment.Left, leftPlacement.HorizontalAlignment);
        Assert.Equal(TrayMenuVerticalAlignment.Center, leftPlacement.VerticalAlignment);
        Assert.Equal(62, leftPlacement.Anchor.X);
    }

    [Fact]
    public async Task WindowsLocationHelper_RequestAccessWithTimeout_PreventsIndefiniteHang()
    {
        var neverCompletes = new TaskCompletionSource<GeolocationAccessStatus>();
        GeolocationAccessStatus? timedOut = await WindowsLocationHelper.RequestAccessWithTimeoutAsync(
            () => neverCompletes.Task,
            TimeSpan.FromMilliseconds(40));
        Assert.Null(timedOut);

        GeolocationAccessStatus? allowed = await WindowsLocationHelper.RequestAccessWithTimeoutAsync(
            () => Task.FromResult(GeolocationAccessStatus.Allowed),
            TimeSpan.FromSeconds(1));
        Assert.Equal(GeolocationAccessStatus.Allowed, allowed);
    }

    [Fact]
    public void ClipboardPlatformInterop_ExcludesWin10AndWin11TrayOverflowClasses()
    {
        Assert.True(ClipboardPlatformInterop.IsExcludedExternalPasteTargetClass("NotifyIconOverflowWindow"));
        Assert.True(ClipboardPlatformInterop.IsExcludedExternalPasteTargetClass("TopLevelWindowForOverflowXamlIsland"));
        Assert.True(ClipboardPlatformInterop.IsExcludedExternalPasteTargetClass("Shell_TrayWnd"));
        Assert.False(ClipboardPlatformInterop.IsExcludedExternalPasteTargetClass("Notepad"));
    }

    [Fact]
    public void CustomWidgetNetworkSecurity_RecognizesConfiguredSystemProxyWhileBlockingSsrfTargets()
    {
        var publicUri = new Uri("https://deskbox.fun/catalog/widgets.v1.json");
        var loopbackProxy = new WebProxy("http://127.0.0.1:7890", BypassOnLocal: true);
        var corpLanProxy = new WebProxy("http://10.20.30.40:8080", BypassOnLocal: true);

        Assert.True(CustomWidgetNetworkSecurity.IsConfiguredSystemProxyEndpoint(
            publicUri,
            new DnsEndPoint("127.0.0.1", 7890),
            loopbackProxy));
        Assert.True(CustomWidgetNetworkSecurity.IsConfiguredSystemProxyEndpoint(
            publicUri,
            new DnsEndPoint("10.20.30.40", 8080),
            corpLanProxy));
        Assert.False(CustomWidgetNetworkSecurity.IsConfiguredSystemProxyEndpoint(
            publicUri,
            new DnsEndPoint("127.0.0.1", 9999),
            loopbackProxy));
        Assert.False(CustomWidgetNetworkSecurity.IsConfiguredSystemProxyEndpoint(
            new Uri("http://localhost:11434/api/tags"),
            new DnsEndPoint("127.0.0.1", 7890),
            loopbackProxy));

        // Public target passes proxy handoff validation; loopback/metadata targets are still blocked.
        CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoff(publicUri, requirePublicIpOnly: true);
        Assert.Throws<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoff(
                new Uri("https://127.0.0.1/secret"),
                requirePublicIpOnly: true));
        Assert.Throws<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoff(
                new Uri("https://169.254.169.254/latest/meta-data/"),
                requirePublicIpOnly: true));
        Assert.Throws<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoff(
                new Uri("https://169.254.169.254/latest/meta-data/"),
                requirePublicIpOnly: false));
    }

    [Fact]
    public async Task CustomWidgetFileTransformService_WebpFailsCleanlyInsteadOfWritingTiff()
    {
        string pngPath = Path.Combine(_tempRoot, "sample.png");
        // Minimal valid 1x1 RGBA PNG
        byte[] minimalPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2dIAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(pngPath, minimalPng);

        CustomWidgetTransformResult result = await CustomWidgetFileTransformService.TransformFileAsync(
            pngPath,
            "webp");

        Assert.False(result.Succeeded);
        Assert.Contains("WebP", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "sample.webp")));
    }

    [Fact]
    public void ElevatedExplorerChromeGuard_ExcludesWin10RibbonSearchAndDuiPanes()
    {
        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["NetUIHWND", "NUIPane", "UIRibbonWorkPane", "UIRibbonCommandBar", "UIRibbonCommandBarDock", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["DirectUIHWND", "SearchEditBoxWrapperClass", "Search Box", "UniversalSearchBand", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["DirectUIHWND", "DUIViewWndClassName", "ShellTabWindowClass", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        Assert.Equal(
            ElevatedOutboundTargetKind.ExplorerFolder,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["DirectUIHWND", "SHELLDLL_DefView", "DUIViewWndClassName", "ShellTabWindowClass", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));
    }

    [Fact]
    public void NativeDropTarget_AugmentsZeroKeyStateOnlyOnWin10()
    {
        uint win10Ctrl = NativeDropTarget.AugmentWin10BrokeredModifierKeyState(
            keyState: 0,
            isWindows11OrLater: false,
            isKeyPressed: key => key == VirtualKey.Control);
        Assert.Equal(NativeDropEffectPolicy.ControlKeyState, win10Ctrl);

        uint win10ShiftAlt = NativeDropTarget.AugmentWin10BrokeredModifierKeyState(
            keyState: 0,
            isWindows11OrLater: false,
            isKeyPressed: key => key is VirtualKey.Shift or VirtualKey.Menu);
        Assert.Equal(
            NativeDropEffectPolicy.ShiftKeyState | NativeDropEffectPolicy.AltKeyState,
            win10ShiftAlt);

        // Non-zero keyState (classic Win32 OLE MK_LBUTTON = 0x0001) and Win11 are left untouched.
        const uint MkLButton = 0x0001;
        Assert.Equal(
            MkLButton,
            NativeDropTarget.AugmentWin10BrokeredModifierKeyState(
                keyState: MkLButton,
                isWindows11OrLater: false,
                isKeyPressed: _ => true));
        Assert.Equal(
            0u,
            NativeDropTarget.AugmentWin10BrokeredModifierKeyState(
                keyState: 0,
                isWindows11OrLater: true,
                isKeyPressed: _ => true));
    }

    [Fact]
    public void StoreUserAssetMigration_RewritesJsonPathPrefixesWithNonAsciiAndHtmlEscapes()
    {
        string oldRoot = @"C:\Users\张三 & Co+1's\AppData\Local\DeskBox\data\";
        string newRoot = @"C:\Users\张三 & Co+1's\DeskBox\.appdata\data\";

        // Default System.Text.Json serialization emits \uXXXX for CJK and &, +, '.
        string serialized = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["bg"] = oldRoot + @"backgrounds\photo.jpg",
                ["slash"] = (oldRoot + @"attachments/doc.pdf").Replace('\\', '/')
            },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });

        Assert.Contains(@"\u5F20\u4E09", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\u0026", serialized, StringComparison.OrdinalIgnoreCase);

        string rewritten = StoreUserAssetMigrationFileOps.RewriteJsonPathPrefixesContent(
            serialized,
            [oldRoot],
            newRoot);

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(rewritten);
        Assert.NotNull(parsed);
        Assert.Equal(newRoot + @"backgrounds\photo.jpg", parsed!["bg"]);
        Assert.Equal((newRoot + @"attachments/doc.pdf").Replace('\\', '/'), parsed["slash"]);
    }

    [Fact]
    public void ChannelMigration_ProbesSettingsFileRatherThanEmptyLocalCacheDirectory()
    {
        string directRoot = Path.Combine(_tempRoot, "DirectProfile");
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        string directSettings = Path.Combine(directData, "settings.json");
        string storeRoot = Path.Combine(_tempRoot, "StoreDurable");
        File.WriteAllText(directSettings, "{\"schemaVersion\":999}", Encoding.UTF8);

        // Simulate Win10 MSIX bindflt where legacyDataDir resolves to LocalCache
        // (empty dir), while settings.json resolves to Direct's real unvirtualized file.
        ChannelMigrationOutcome outcome = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_tempRoot, "DirectRecovery"),
            Path.Combine(_tempRoot, "StoreRecovery"),
            isInteractiveLaunch: false,
            cultureName: "en-US",
            confirmPrompt: (_, _) => false,
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path =>
                path.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)
                    ? directSettings
                    : @"C:\Users\alice\AppData\Local\Packages\D1FC332A.DeskBoxWidgets_abc\LocalCache\Local\DeskBox\data",
            maxSupportedSchemaVersion: 18);

        Assert.Equal(ChannelMigrationOutcomeKind.SkippedNewerSchema, outcome.Kind);
    }

    [Fact]
    public void ShellUnelevatedLaunch_IncludesBothExplorerAndAdminSidsForOverTheShoulderUac()
    {
        string distinct = ShellUnelevatedLaunch.BuildProtectedProcessSddl(
            "S-1-5-21-100-200-300-1001",
            "S-1-5-21-100-200-300-500");
        Assert.Equal(
            "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;S-1-5-21-100-200-300-1001)(A;;GA;;;S-1-5-21-100-200-300-500)S:(ML;;NW;;;S-1-16-8192)",
            distinct);

        string same = ShellUnelevatedLaunch.BuildProtectedProcessSddl(
            "S-1-5-21-100-200-300-1001",
            "S-1-5-21-100-200-300-1001");
        Assert.Equal(
            "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;S-1-5-21-100-200-300-1001)S:(ML;;NW;;;S-1-16-8192)",
            same);
    }

    [Fact]
    public void ResolveDragOutAllowedOperations_Win10ProtectsSpecialMediaWhilePreservingNormalMove()
    {
        string localFile = Path.Combine(_tempRoot, "normal.txt");

        // Win10 Move on normal local files stays single-bit Move.
        Assert.Equal(
            DataPackageOperation.Move,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                sourcePaths: [localFile],
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: false));

        // Win10 Move on D3 special media collapses to single-bit Copy (P0-1).
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                sourcePaths: [localFile],
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: true));

        // Auto-classification from UNC and WeChat chat cache paths also collapses on Win10.
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                sourcePaths: [@"\\nas-server\team\report.docx"],
                isProtectedCopyOnly: false));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                sourcePaths: [@"C:\Users\demo\Documents\WeChat Files\wxid_demo\FileStorage\File\2026-11\invoice.pdf"],
                isProtectedCopyOnly: false));

        // Win11 keeps Copy | Move so Shift+Drag remains available while Preferred DropEffect defaults to Copy.
        Assert.Equal(
            FileItemDragPackage.SupportedOperations,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: true,
                sourcePaths: [localFile],
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: true));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionMove,
                sourcePaths: [localFile],
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: true));
    }

    [Fact]
    public void FileDragSourceGuardDataObject_Win10MoveTargetGateBlocksUntrustedFileFormatsAndAllowsTrustedTargets()
    {
        const ushort CfHdrop = 15;
        const int DvEFormatEtc = unchecked((int)0x80040064);
        ushort shellIdListFormat = Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array");
        ushort dragContextFormat = Win32Helper.GetRegisteredClipboardFormat("DragContext");
        ushort dragImageBitsFormat = Win32Helper.GetRegisteredClipboardFormat("DragImageBits");
        Assert.True(FileDragSourceGuardDataObject.IsFilePayloadFormatId(CfHdrop));
        Assert.True(FileDragSourceGuardDataObject.IsFilePayloadFormatId(shellIdListFormat));
        Assert.False(FileDragSourceGuardDataObject.IsFilePayloadFormatId(dragContextFormat));
        Assert.False(FileDragSourceGuardDataObject.IsFilePayloadFormatId(dragImageBitsFormat));

        string sampleFile = Path.Combine(_tempRoot, "win10-gate-target.txt");
        File.WriteAllText(sampleFile, "payload", Encoding.UTF8);

        Exception? staError = null;
        var staThread = new Thread(() =>
        {
            nint inner = 0;
            nint guardPtr = 0;
            try
            {
                OutboundMoveTargetTrust simulatedTrust = OutboundMoveTargetTrust.Untrusted;
                bool throwInProbe = false;

                inner = NativeShellFileDragProvider.CreateShellDataObject([sampleFile]);
                Assert.NotEqual(0, inner);
                guardPtr = FileDragSourceGuardDataObject.CreateTargetAwareInterfacePointer(
                    inner,
                    moveTargetTrustProbe: () =>
                    {
                        if (throwInProbe)
                        {
                            throw new InvalidOperationException("probe failure");
                        }

                        return simulatedTrust;
                    });
                var receiver = new NativeOleDataObject(guardPtr);

                NativeFormatEtc hdropFmt = new()
                {
                    ClipboardFormat = CfHdrop,
                    Aspect = 1,
                    Index = -1,
                    MediumType = 1,
                };
                NativeFormatEtc pidlFmt = new()
                {
                    ClipboardFormat = shellIdListFormat,
                    Aspect = 1,
                    Index = -1,
                    MediumType = 1,
                };

                // 1. Gate OFF (default: Win11 or Win10 Copy): Untrusted target receives CF_HDROP normally.
                Assert.Equal(0, receiver.QueryGetData(ref hdropFmt));
                Assert.Equal(0, receiver.QueryGetData(ref pidlFmt));
                Assert.False(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());

                // 2. Gate ON over Untrusted target (WeChat / browser): all file payload formats blocked.
                FileDragSourceGuardDataObject.ConfigureActiveWin10MoveTargetGate(enabled: true);
                Assert.Equal(DvEFormatEtc, receiver.QueryGetData(ref hdropFmt));
                Assert.Equal(DvEFormatEtc, receiver.QueryGetData(ref pidlFmt));
                Assert.Equal(DvEFormatEtc, receiver.GetData(ref hdropFmt, out _));
                Assert.True(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());

                // Direct GetDataHere check via allocated native structs.
                var directGuard = new FileDragSourceGuardDataObject(
                    inner,
                    ownerDispatcher: null,
                    preferredDropEffectResolver: null,
                    moveTargetTrustProbe: () => OutboundMoveTargetTrust.Untrusted);
                directGuard.SetWin10MoveTargetGateEnabled(true);
                nint fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeFormatEtc>());
                nint medPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeStorageMedium>());
                try
                {
                    Marshal.StructureToPtr(hdropFmt, fmtPtr, fDeleteOld: false);
                    Marshal.StructureToPtr(default(NativeStorageMedium), medPtr, fDeleteOld: false);
                    Assert.Equal(DvEFormatEtc, directGuard.GetDataHere(fmtPtr, medPtr));
                    Assert.True(directGuard.WasLastWin10MoveTargetGateBlocked);
                    Assert.Equal(1, directGuard.Win10MoveTargetGateBlockedCount);
                }
                finally
                {
                    Marshal.FreeHGlobal(fmtPtr);
                    Marshal.FreeHGlobal(medPtr);
                }

                HashSet<ushort> untrustedFormats = receiver.EnumerateGetFormatIds();
                Assert.DoesNotContain(CfHdrop, untrustedFormats);
                Assert.DoesNotContain(shellIdListFormat, untrustedFormats);
                Assert.All(untrustedFormats, f => Assert.False(FileDragSourceGuardDataObject.IsFilePayloadFormatId(f)));

                // 3. Moving onto Desktop/DeskBox dynamically restores file payload formats and clears blocked state.
                simulatedTrust = OutboundMoveTargetTrust.DesktopOrDeskBox;
                Assert.Equal(0, receiver.QueryGetData(ref hdropFmt));
                Assert.Equal(0, receiver.QueryGetData(ref pidlFmt));
                Assert.False(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());

                Assert.Equal(0, receiver.GetData(ref hdropFmt, out NativeStorageMedium allowedMedium));
                Win32Helper.ReleaseStorageMedium(ref allowedMedium);

                HashSet<ushort> desktopFormats = receiver.EnumerateGetFormatIds();
                Assert.Contains(CfHdrop, desktopFormats);

                // 4. Explorer folder window is also trusted for Move.
                simulatedTrust = OutboundMoveTargetTrust.ExplorerFolderWindow;
                Assert.Equal(0, receiver.QueryGetData(ref hdropFmt));
                Assert.False(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());

                // 5. Probe failure fails closed.
                throwInProbe = true;
                Assert.Equal(DvEFormatEtc, receiver.QueryGetData(ref hdropFmt));
                Assert.True(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());
            }
            catch (Exception ex)
            {
                staError = ex;
            }
            finally
            {
                FileDragSourceGuardDataObject.ReleaseInterfacePointer(guardPtr);
                FileDragSourceGuardDataObject.ClearActiveManagedRoot();
                NativeShellFileDragProvider.ReleaseInterface(inner);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(staError);
    }

    [Fact]
    public void FileDragSourceGuardDataObject_ConfigureActiveWin10MoveTargetGateTracksBlockedFlag()
    {
        const ushort CfHdrop = 15;
        const int DvEFormatEtc = unchecked((int)0x80040064);
        string sampleFile = Path.Combine(_tempRoot, "win10-active-gate.txt");
        File.WriteAllText(sampleFile, "active", Encoding.UTF8);

        Exception? staError = null;
        var staThread = new Thread(() =>
        {
            nint inner = 0;
            nint guardPtr = 0;
            try
            {
                inner = NativeShellFileDragProvider.CreateShellDataObject([sampleFile]);
                guardPtr = FileDragSourceGuardDataObject.CreateTargetAwareInterfacePointer(
                    inner,
                    moveTargetTrustProbe: static () => OutboundMoveTargetTrust.Untrusted);

                var oleView = new NativeOleDataObject(guardPtr);
                NativeFormatEtc hdrop = new()
                {
                    ClipboardFormat = CfHdrop,
                    Aspect = 1,
                    Index = -1,
                    MediumType = 1,
                };

                // Before ConfigureActiveWin10MoveTargetGate(true), local DragUI / TryAttach self-check passes.
                Assert.Equal(0, oleView.QueryGetData(ref hdrop));
                Assert.False(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());

                // Once armed, untrusted target probe blocks CF_HDROP and sets active blocked flag.
                FileDragSourceGuardDataObject.ConfigureActiveWin10MoveTargetGate(enabled: true);
                Assert.Equal(DvEFormatEtc, oleView.QueryGetData(ref hdrop));
                Assert.True(FileDragSourceGuardDataObject.ConsumeActiveWin10MoveTargetGateBlockedFlag());
            }
            catch (Exception ex)
            {
                staError = ex;
            }
            finally
            {
                FileDragSourceGuardDataObject.ReleaseInterfacePointer(guardPtr);
                FileDragSourceGuardDataObject.ClearActiveManagedRoot();
                NativeShellFileDragProvider.ReleaseInterface(inner);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(staError);
    }

    [Fact]
    public void ShouldSuppressDragOutResultHint_SuppressesFalseMovedToastWhenWin10MoveGateBlockedDrop()
    {
        Assert.True(FileSurfaceContent.ShouldSuppressDragOutResultHint(
            handledAsStackMembership: false,
            observation: FileSurfaceContent.ExternalDragObservation.Full,
            targetKind: DropTargetWindowKind.NonExplorerWindow,
            win10MoveRejectedByNonExplorer: true));

        Assert.False(FileSurfaceContent.ShouldSuppressDragOutResultHint(
            handledAsStackMembership: false,
            observation: FileSurfaceContent.ExternalDragObservation.Full,
            targetKind: DropTargetWindowKind.ExplorerFileView,
            win10MoveRejectedByNonExplorer: false));
    }

    [Fact]
    public void ResolveOutboundModifierTipResourceKey_UsesCtrlBeforeDragOnWin10MoveAndDynamicModifiersOnWin11()
    {
        Assert.Equal(
            "Widget.DragOutTip.Modifiers",
            FileSurfaceContent.ResolveOutboundModifierTipResourceKey(
                DataPackageOperation.Copy | DataPackageOperation.Move,
                isWindows11OrLater: true,
                SettingsService.ManagedDragOutActionMove));

        Assert.Equal(
            "Widget.DragOutTip.Win10MoveCtrlCopy",
            FileSurfaceContent.ResolveOutboundModifierTipResourceKey(
                DataPackageOperation.Move,
                isWindows11OrLater: false,
                SettingsService.ManagedDragOutActionMove));

        Assert.Null(
            FileSurfaceContent.ResolveOutboundModifierTipResourceKey(
                DataPackageOperation.Copy,
                isWindows11OrLater: false,
                SettingsService.ManagedDragOutActionCopy));

        Assert.Null(
            FileSurfaceContent.ResolveOutboundModifierTipResourceKey(
                DataPackageOperation.Copy,
                isWindows11OrLater: false,
                SettingsService.ManagedDragOutActionFollowWindows));
    }

    [Fact]
    public void ShouldSuppressNativeShellDragVisual_SuppressesTargetWidgetHelperDuringActiveCrossWidgetXamlDrag()
    {
        Assert.True(FileSurfaceContent.ShouldSuppressNativeShellDragVisual(
            isWin10NativeShellDragActive: false,
            hasActiveDragSession: false,
            isSurfaceReorderDragActive: false,
            isStackPopoverDragActive: false,
            isDeskBoxFileDragSnapshot: false,
            hasActiveProcessDeskBoxDrag: true));

        Assert.False(FileSurfaceContent.ShouldSuppressNativeShellDragVisual(
            isWin10NativeShellDragActive: false,
            hasActiveDragSession: false,
            isSurfaceReorderDragActive: false,
            isStackPopoverDragActive: false,
            isDeskBoxFileDragSnapshot: false,
            hasActiveProcessDeskBoxDrag: false));
    }
}
