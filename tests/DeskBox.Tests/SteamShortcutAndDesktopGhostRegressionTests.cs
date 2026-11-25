using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SteamShortcutAndDesktopGhostRegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        $"steam-url-desktop-ghost-{Guid.NewGuid():N}");

    public SteamShortcutAndDesktopGhostRegressionTests()
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
    public void FilterMovedDesktopItems_CapturesPersonalAndPublicDesktopMovesOnly()
    {
        string personalDesktop = Path.Combine(_tempRoot, "UserDesktop");
        string publicDesktop = Path.Combine(_tempRoot, "PublicDesktop");
        string managedFolder = Path.Combine(_tempRoot, "ManagedBox");
        string downloads = Path.Combine(_tempRoot, "Downloads");

        string terrariaUrl = Path.Combine(personalDesktop, "Terraria.url");
        string sharedAppLnk = Path.Combine(publicDesktop, "SharedApp.lnk");
        string movedFolder = Path.Combine(personalDesktop, "PortableGame");
        string renamedOnDesktop = Path.Combine(personalDesktop, "Renamed.url");
        string nonDesktopFile = Path.Combine(downloads, "Archive.zip");

        IReadOnlyList<MovedDesktopItem> filtered = DesktopIconPositionInterop.FilterMovedDesktopItems(
            [
                (terrariaUrl, Path.Combine(managedFolder, "Terraria.url"), false),
                (terrariaUrl.ToUpperInvariant(), Path.Combine(managedFolder, "Terraria.url"), false),
                (sharedAppLnk, Path.Combine(managedFolder, "SharedApp.lnk"), false),
                (movedFolder, Path.Combine(managedFolder, "PortableGame"), true),
                (renamedOnDesktop, Path.Combine(personalDesktop, "Renamed2.url"), false),
                (renamedOnDesktop, Path.Combine(publicDesktop, "CrossDesktop.url"), false),
                (nonDesktopFile, Path.Combine(managedFolder, "Archive.zip"), false),
            ],
            personalDesktop,
            publicDesktop);

        Assert.Equal(3, filtered.Count);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(terrariaUrl), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DesktopDirectory, Path.GetFullPath(personalDesktop), StringComparison.OrdinalIgnoreCase) &&
            !item.WasDirectory);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(sharedAppLnk), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DesktopDirectory, Path.GetFullPath(publicDesktop), StringComparison.OrdinalIgnoreCase) &&
            !item.WasDirectory);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(movedFolder), StringComparison.OrdinalIgnoreCase) &&
            item.WasDirectory);
    }

    [Fact]
    public void ClassifyShellMoveNotification_UsesDeleteAndCreateWhenMovingOffDesktopOrAcrossVolumes()
    {
        const string personalDesktop = @"C:\Users\zcxcq\Desktop";
        const string redirectedPersonalDesktop = @"D:\OneDrive\Desktop";
        const string publicDesktop = @"C:\Users\Public\Desktop";
        const string sameVolumeBox = @"C:\Users\zcxcq\AppData\Local\DeskBox\Boxes\Docs";
        const string crossVolumeBox = @"D:\DeskBox\Boxes\Docs";

        string? AliasResolver(string dir) =>
            string.Equals(dir, personalDesktop, StringComparison.OrdinalIgnoreCase)
                ? redirectedPersonalDesktop
                : null;

        // Same-volume file move off Personal Desktop must emit DELETE + CREATE (never cross-folder RENAMEITEM).
        Assert.Equal(
            ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"C:\Users\zcxcq\Desktop\会议纪要.pdf",
                Path.Combine(sameVolumeBox, "会议纪要.pdf"),
                isDirectory: false,
                personalDesktop,
                publicDesktop,
                AliasResolver));

        // Move off redirected/junctioned Desktop target alias is also recognized as leaving the Desktop.
        Assert.Equal(
            ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"D:\OneDrive\Desktop\会议纪要.pdf",
                Path.Combine(crossVolumeBox, "会议纪要.pdf"),
                isDirectory: false,
                personalDesktop,
                publicDesktop,
                AliasResolver));

        // Cross-volume non-desktop move also emits DELETE + CREATE.
        Assert.Equal(
            ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"C:\Users\zcxcq\Downloads\report.pdf",
                Path.Combine(crossVolumeBox, "report.pdf"),
                isDirectory: false,
                personalDesktop,
                publicDesktop,
                AliasResolver));

        // Same-volume rename staying on Desktop (including across its own resolved alias) stays a file rename.
        Assert.Equal(
            ShellMoveNotificationPlanKind.SameVolumeFileRename,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"C:\Users\zcxcq\Desktop\新建 文本文档.txt",
                @"C:\Users\zcxcq\Desktop\会议记录.txt",
                isDirectory: false,
                personalDesktop,
                publicDesktop,
                AliasResolver));
        Assert.Equal(
            ShellMoveNotificationPlanKind.SameVolumeFileRename,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"C:\Users\zcxcq\Desktop\note.txt",
                @"D:\OneDrive\Desktop\note-renamed.txt",
                isDirectory: false,
                personalDesktop,
                publicDesktop,
                AliasResolver));

        // Same-volume non-desktop folder move uses SHCNE_RENAMEFOLDER.
        Assert.Equal(
            ShellMoveNotificationPlanKind.SameVolumeFolderRename,
            DesktopIconPositionInterop.ClassifyShellMoveNotification(
                @"C:\Users\zcxcq\Downloads\ProjectFolder",
                Path.Combine(sameVolumeBox, "ProjectFolder"),
                isDirectory: true,
                personalDesktop,
                publicDesktop,
                AliasResolver));
    }

    [Fact]
    public void BuildGhostCandidateLookup_IncludesSameScopeDesktopAliasesWithoutCrossScopeLeak()
    {
        string personalLexical = Path.Combine(_tempRoot, "LexicalDesktop");
        string personalPhysical = Path.Combine(_tempRoot, "PhysicalOneDriveDesktop");
        string publicDesktop = Path.Combine(_tempRoot, "PublicDesktop");
        Directory.CreateDirectory(personalLexical);
        Directory.CreateDirectory(personalPhysical);
        Directory.CreateDirectory(publicDesktop);

        // Existing twin on Public Desktop must never be matched or purged when Personal Desktop item moved.
        string publicExistingTwin = Path.Combine(publicDesktop, "会议纪要.pdf");
        File.WriteAllText(publicExistingTwin, "keep-public");

        string movedPersonalSource = Path.Combine(personalLexical, "会议纪要.pdf");
        var item = new MovedDesktopItem(
            movedPersonalSource,
            personalLexical,
            WasDirectory: false,
            IsPublicDesktop: false);

        Dictionary<string, MovedDesktopItem> lookup = DesktopIconPositionInterop.BuildGhostCandidateLookup(
            [item],
            [personalLexical, personalPhysical],
            [publicDesktop]);

        Assert.True(lookup.ContainsKey(movedPersonalSource));
        Assert.True(lookup.ContainsKey(Path.Combine(personalPhysical, "会议纪要.pdf")));
        Assert.False(lookup.ContainsKey(publicExistingTwin));
    }

    [Fact]
    public void ExecuteGhostPurgeSchedule_DoesNotExitEarlyOnCleanFirstPass_AndEscalatesWhenGhostSurvives()
    {
        const string ghostPath = @"C:\Users\zcxcq\Desktop\会议纪要.pdf";
        var sleeps = new List<int>();
        var escalatedByPass = new List<bool>();
        int passIndex = 0;

        DesktopIconPositionInterop.ExecuteGhostPurgeSchedule(
            DesktopIconPositionInterop.GhostPurgePollDelaysMs,
            DesktopIconPositionInterop.GhostPurgeEscalationFollowUpDelayMs,
            sleeps.Add,
            hasAnyMissingSource: static () => true,
            runPass: previouslyPurged =>
            {
                int current = passIndex++;
                if (current == 0)
                {
                    // Pass 0 (100 ms): Explorer's async CDefView / PDF thumbnail batch has not committed yet.
                    escalatedByPass.Add(false);
                    return GhostPurgePassOutcome.None;
                }

                // Pass 1 (300 ms) sees the late ghost; Pass 2 (750 ms) sees it survived SHCNE_DELETE and escalates.
                bool shouldEscalate = DesktopIconPositionInterop.ShouldEscalateToShellViewRefresh(
                    [ghostPath],
                    previouslyPurged);
                escalatedByPass.Add(shouldEscalate);
                return new GhostPurgePassOutcome([ghostPath], EscalatedRefresh: shouldEscalate);
            });

        Assert.Equal([100, 300, 750], sleeps);
        Assert.Equal([false, false, true], escalatedByPass);
    }

    [Fact]
    public void ExecuteGhostPurgeSchedule_RunsFollowUpEscalationPassWhenGhostFirstAppearsOnFinalScheduledPass()
    {
        const string lateGhost = @"C:\Users\zcxcq\Desktop\late-download.pdf";
        var sleeps = new List<int>();
        var escalatedByPass = new List<bool>();
        int passIndex = 0;

        DesktopIconPositionInterop.ExecuteGhostPurgeSchedule(
            [100, 300, 750],
            escalationFollowUpDelayMs: 600,
            sleeps.Add,
            hasAnyMissingSource: static () => true,
            runPass: previouslyPurged =>
            {
                int current = passIndex++;
                if (current < 2)
                {
                    escalatedByPass.Add(false);
                    return GhostPurgePassOutcome.None;
                }

                bool shouldEscalate = DesktopIconPositionInterop.ShouldEscalateToShellViewRefresh(
                    [lateGhost],
                    previouslyPurged);
                escalatedByPass.Add(shouldEscalate);
                return new GhostPurgePassOutcome([lateGhost], EscalatedRefresh: shouldEscalate);
            });

        Assert.Equal([100, 300, 750, 600], sleeps);
        Assert.Equal([false, false, false, true], escalatedByPass);
    }

    [Fact]
    public void ExecuteGhostPurgeSchedule_StopsImmediatelyOnceSourcePathReturnsToDesktop()
    {
        var sleeps = new List<int>();
        int passesRun = 0;
        bool stillMissing = true;

        DesktopIconPositionInterop.ExecuteGhostPurgeSchedule(
            [100, 300, 750],
            escalationFollowUpDelayMs: 600,
            sleeps.Add,
            hasAnyMissingSource: () => stillMissing,
            runPass: _ =>
            {
                passesRun++;
                stillMissing = false; // Undo restored the file to the Desktop after pass 0.
                return GhostPurgePassOutcome.None;
            });

        Assert.Equal([100, 300], sleeps);
        Assert.Equal(1, passesRun);
    }

    [Fact]
    public void FilterRemovedDesktopItems_CapturesDirectDesktopDeletionsAcrossPersonalPublicAndAliases()
    {
        const string personalDesktop = @"C:\Users\zcxcq\Desktop";
        const string redirectedPersonal = @"D:\OneDrive\Desktop";
        const string publicDesktop = @"C:\Users\Public\Desktop";
        const string downloads = @"C:\Users\zcxcq\Downloads";

        string? AliasResolver(string dir) =>
            string.Equals(dir, personalDesktop, StringComparison.OrdinalIgnoreCase)
                ? redirectedPersonal
                : null;

        IReadOnlyList<MovedDesktopItem> matched = DesktopIconPositionInterop.FilterRemovedDesktopItems(
            [
                (@"C:\Users\zcxcq\Desktop\DeskBox Files.lnk", false),
                (@"c:\users\zcxcq\desktop\deskbox files.lnk", false),
                (@"D:\OneDrive\Desktop\TempFolder", true),
                (@"C:\Users\Public\Desktop\SharedTool.lnk", false),
                (Path.Combine(downloads, "Unrelated.zip"), false),
            ],
            personalDesktop,
            publicDesktop,
            AliasResolver);

        Assert.Equal(3, matched.Count);
        Assert.Contains(matched, item =>
            string.Equals(item.SourcePath, @"C:\Users\zcxcq\Desktop\DeskBox Files.lnk", StringComparison.OrdinalIgnoreCase) &&
            !item.IsPublicDesktop &&
            !item.WasDirectory);
        Assert.Contains(matched, item =>
            string.Equals(item.SourcePath, @"D:\OneDrive\Desktop\TempFolder", StringComparison.OrdinalIgnoreCase) &&
            !item.IsPublicDesktop &&
            item.WasDirectory);
        Assert.Contains(matched, item =>
            string.Equals(item.SourcePath, @"C:\Users\Public\Desktop\SharedTool.lnk", StringComparison.OrdinalIgnoreCase) &&
            item.IsPublicDesktop &&
            !item.WasDirectory);
    }

    [Fact]
    public void CanEscalateShellViewRefresh_BlocksDuringInlineRenameActiveRestoreOrCooldownWindow()
    {
        const long cooldown = DesktopIconPositionInterop.ShellViewRefreshCooldownMs;

        // First-ever refresh (long.MinValue sentinel) with idle rename and zero active restores is permitted.
        Assert.True(DesktopIconPositionInterop.CanEscalateShellViewRefresh(
            inlineRenameActive: false,
            activePositionRestoreCount: 0,
            nowTick64: 0,
            lastRefreshTick64: long.MinValue));

        // Inline rename active -> blocked.
        Assert.False(DesktopIconPositionInterop.CanEscalateShellViewRefresh(
            inlineRenameActive: true,
            activePositionRestoreCount: 0,
            nowTick64: 10_000,
            lastRefreshTick64: long.MinValue));

        // Active desktop icon position restore (Undo / Move-to-Desktop) -> blocked so Refresh cannot clobber coordinates.
        Assert.False(DesktopIconPositionInterop.CanEscalateShellViewRefresh(
            inlineRenameActive: false,
            activePositionRestoreCount: 1,
            nowTick64: 10_000,
            lastRefreshTick64: long.MinValue));

        // Adjacent inside cooldown window (1199 ms < 1200 ms) -> blocked; exact boundary (1200 ms) -> permitted.
        Assert.False(DesktopIconPositionInterop.CanEscalateShellViewRefresh(
            inlineRenameActive: false,
            activePositionRestoreCount: 0,
            nowTick64: 5_000 + cooldown - 1,
            lastRefreshTick64: 5_000));
        Assert.True(DesktopIconPositionInterop.CanEscalateShellViewRefresh(
            inlineRenameActive: false,
            activePositionRestoreCount: 0,
            nowTick64: 5_000 + cooldown,
            lastRefreshTick64: 5_000));
    }

    [Fact]
    public void CoalescedGhostPurgeQueue_MergesConcurrentMovesAndRearmsOnlyForMidCycleArrivals()
    {
        var queue = new DesktopIconPositionInterop.CoalescedGhostPurgeBatchQueue();
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemA = new MovedDesktopItem(@"C:\Users\zcxcq\Desktop\a.pdf", @"C:\Users\zcxcq\Desktop", false);
        var itemB = new MovedDesktopItem(@"C:\Users\zcxcq\Desktop\b.pdf", @"C:\Users\zcxcq\Desktop", false);
        var itemC = new MovedDesktopItem(@"C:\Users\zcxcq\Desktop\c.pdf", @"C:\Users\zcxcq\Desktop", false);

        // First enqueue starts the single background worker; burst follow-ups coalesce into the active worker.
        Assert.True(queue.Enqueue([itemA], [@"C:\Users\zcxcq\Desktop"], [@"C:\Users\Public\Desktop"]));
        Assert.False(queue.Enqueue([itemB], [@"C:\Users\zcxcq\Desktop"], [@"C:\Users\Public\Desktop"]));

        int firstCycleGen = queue.BeginCycle();
        (MovedDesktopItem[] snapshot1, _, _) = queue.SnapshotPendingMissing(existingPaths.Contains);
        Assert.Equal(2, snapshot1.Length);

        // Mid-cycle arrival (itemC) lands while cycle 1 is already mid-flight; itemB is simultaneously undone back to Desktop.
        Assert.False(queue.Enqueue([itemC], [@"C:\Users\zcxcq\Desktop"], [@"C:\Users\Public\Desktop"]));
        existingPaths.Add(itemB.SourcePath);

        (MovedDesktopItem[] snapshotMid, _, _) = queue.SnapshotPendingMissing(existingPaths.Contains);
        Assert.Equal(2, snapshotMid.Length);
        Assert.DoesNotContain(snapshotMid, candidate =>
            string.Equals(candidate.SourcePath, itemB.SourcePath, StringComparison.OrdinalIgnoreCase));

        // Completing cycle 1 retires itemA (completed full cycle) and re-arms one cycle strictly for late-arriving itemC.
        Assert.True(queue.CompleteCycleAndShouldContinue(firstCycleGen, existingPaths.Contains));

        int secondCycleGen = queue.BeginCycle();
        (MovedDesktopItem[] snapshot2, _, _) = queue.SnapshotPendingMissing(existingPaths.Contains);
        MovedDesktopItem remaining = Assert.Single(snapshot2);
        Assert.Equal(itemC.SourcePath, remaining.SourcePath);

        // Completing cycle 2 with no new arrivals marks the worker idle so a future move starts a fresh worker.
        Assert.False(queue.CompleteCycleAndShouldContinue(secondCycleGen, existingPaths.Contains));
        Assert.True(queue.Enqueue([itemA], [@"C:\Users\zcxcq\Desktop"], [@"C:\Users\Public\Desktop"]));
    }

    [Fact]
    public async Task DesktopOrganizationScanner_ExcludesManagedStorageDesktopShortcutWhileKeepingUserShortcutsEligible()
    {
        string desktopDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ScannerDesktop")).FullName;
        string publicDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ScannerPublicDesktop")).FullName;
        string managedRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DeskBoxManagedRoot")).FullName;
        string userFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "UserProject")).FullName;

        string deskBoxShortcutPath = Path.Combine(desktopDir, ManagedStorageDesktopShortcutService.ShortcutFileName);
        string userShortcutPath = Path.Combine(desktopDir, "UserProject.lnk");

        ShortcutHelper.CreateOrUpdateFolderShortcut(
            deskBoxShortcutPath,
            managedRoot,
            ManagedStorageDesktopShortcutService.ShortcutDescription);
        ShortcutHelper.CreateOrUpdateFolderShortcut(
            userShortcutPath,
            userFolder,
            "User project folder");

        Assert.True(ManagedStorageDesktopShortcutService.IsManagedStorageShortcutFile(deskBoxShortcutPath));
        Assert.False(ManagedStorageDesktopShortcutService.IsManagedStorageShortcutFile(userShortcutPath));

        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => desktopDir,
            () => publicDir);

        DesktopOrganizationFileSnapshot autoDeskBoxSnap = scanner.CreateAutoOrganizationSnapshot(deskBoxShortcutPath);
        DesktopOrganizationFileSnapshot autoUserSnap = scanner.CreateAutoOrganizationSnapshot(userShortcutPath);
        Assert.False(autoDeskBoxSnap.IsEligible);
        Assert.Equal(DesktopOrganizationExclusionReason.HiddenOrSystem, autoDeskBoxSnap.ExclusionReason);
        Assert.True(autoUserSnap.IsEligible);
        Assert.Equal(DesktopOrganizationExclusionReason.None, autoUserSnap.ExclusionReason);

        DesktopOrganizationScanResult manualScan = await scanner.ScanAsync(includeSlowItems: true);
        DesktopOrganizationFileSnapshot manualDeskBox = Assert.Single(
            manualScan.Items,
            item => string.Equals(item.SourcePath, deskBoxShortcutPath, StringComparison.OrdinalIgnoreCase));
        DesktopOrganizationFileSnapshot manualUser = Assert.Single(
            manualScan.Items,
            item => string.Equals(item.SourcePath, userShortcutPath, StringComparison.OrdinalIgnoreCase));
        Assert.False(manualDeskBox.IsEligible);
        Assert.Equal(DesktopOrganizationExclusionReason.HiddenOrSystem, manualDeskBox.ExclusionReason);
        Assert.True(manualUser.IsEligible);
    }

    [Fact]
    public void ResolveInternetShortcut_HandlesForwardSlashesUtf16BomAndNonUtf8AnsiBytes()
    {
        string utf8Url = Path.Combine(_tempRoot, "ForwardSlash.url");
        File.WriteAllText(
            utf8Url,
            "[InternetShortcut]\r\nURL=steam://rungameid/105600\r\nIconIndex=0\r\nIconFile=D:/Steam/steam/games/terraria.ico\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ShortcutInfo? parsedForward = ShortcutHelper.ResolveInternetShortcut(utf8Url);
        Assert.NotNull(parsedForward);
        Assert.Equal("steam://rungameid/105600", parsedForward!.TargetPath);
        Assert.Equal(@"D:\Steam\steam\games\terraria.ico", parsedForward.IconLocation);

        string utf16Url = Path.Combine(_tempRoot, "ChineseUtf16.url");
        File.WriteAllText(
            utf16Url,
            "[InternetShortcut]\r\nURL=steam://rungameid/1669980\r\nIconIndex=2\r\nIconFile=\"D:/游戏库/Steam/steam/games/volcano.ico\"\r\n",
            Encoding.Unicode);

        ShortcutInfo? parsedUtf16 = ShortcutHelper.ResolveInternetShortcut(utf16Url);
        Assert.NotNull(parsedUtf16);
        Assert.Equal(@"D:\游戏库\Steam\steam\games\volcano.ico", parsedUtf16!.IconLocation);
        Assert.Equal(2, parsedUtf16.IconIndex);

        // Non-UTF8 high byte in another INI comment/key must not break parsing via UTF-8 replacement corruption.
        string ansiUrl = Path.Combine(_tempRoot, "AnsiEncoded.url");
        byte[] prefix = Encoding.ASCII.GetBytes("[InternetShortcut]\r\nURL=steam://rungameid/646570\r\nAuthor=");
        byte[] nonUtf8Tail = [0xC4, 0xE3, 0xBA, 0xC3, (byte)'\r', (byte)'\n'];
        byte[] iconLine = Encoding.ASCII.GetBytes("IconFile=C:\\Steam\\steam\\games\\spire.ico\r\nIconIndex=0\r\n");
        File.WriteAllBytes(ansiUrl, [.. prefix, .. nonUtf8Tail, .. iconLine]);

        ShortcutInfo? parsedAnsi = ShortcutHelper.ResolveInternetShortcut(ansiUrl);
        Assert.NotNull(parsedAnsi);
        Assert.Equal("steam://rungameid/646570", parsedAnsi!.TargetPath);
        Assert.Equal(@"C:\Steam\steam\games\spire.ico", parsedAnsi.IconLocation);
    }

    [Theory]
    [InlineData("steam://rungameid/105600", true, "105600")]
    [InlineData("STEAM://RUNGAMEID/646570", true, "646570")]
    [InlineData("steam://launch/322330/Dialog", true, "322330")]
    [InlineData("steam://open/main", false, "")]
    [InlineData("steam://rungameid/notanumber", false, "")]
    [InlineData("https://store.steampowered.com/app/105600", false, "")]
    [InlineData("", false, "")]
    public void TryParseSteamAppId_ExtractsNumericAppIdOnlyForGameLaunchUrls(
        string url,
        bool expectedSuccess,
        string expectedAppId)
    {
        bool actual = SteamShortcutIconResolver.TryParseSteamAppId(url, out string appId);
        Assert.Equal(expectedSuccess, actual);
        Assert.Equal(expectedAppId, appId);
    }

    [Fact]
    public void SteamShortcutIconResolver_RebasesMovedSteamGamesIcoBeforeOtherFallbacks()
    {
        string newSteamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "NewSteam")).FullName;
        string gamesDir = Directory.CreateDirectory(Path.Combine(newSteamRoot, "steam", "games")).FullName;
        string rebasedIco = Path.Combine(gamesDir, "65161992978d9f0ba3be83f6db88b92bc61a5b9b.ico");
        File.WriteAllBytes(rebasedIco, CreateSolidIcoBytes(32));

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/105600",
            @"C:\OldDrive\Steam\steam\games\65161992978d9f0ba3be83f6db88b92bc61a5b9b.ico",
            steamRootsOverride: [newSteamRoot]);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(rebasedIco), resolved!.Value.Path);
        Assert.Equal(0, resolved.Value.IconIndex);
        Assert.True(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void SteamShortcutIconResolver_FindsPrimaryGameExeAcrossVdfLibraryAndSkipsCrashAndAnticheatHelpers()
    {
        string steamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "SteamMain")).FullName;
        string mainSteamApps = Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps")).FullName;
        string externalLibrary = Directory.CreateDirectory(Path.Combine(_tempRoot, "ExternalSteamLib")).FullName;
        string extSteamApps = Directory.CreateDirectory(Path.Combine(externalLibrary, "steamapps")).FullName;

        string escapedExternal = externalLibrary.Replace(@"\", @"\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(mainSteamApps, "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + escapedExternal + "\"\n\t}\n}\n");

        File.WriteAllText(
            Path.Combine(extSteamApps, "appmanifest_646570.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"646570\"\n\t\"name\"\t\t\"Slay the Spire\"\n\t\"installdir\"\t\t\"SlayTheSpire\"\n}\n");

        string gameDir = Directory.CreateDirectory(Path.Combine(extSteamApps, "common", "SlayTheSpire")).FullName;
        string crashExe = Path.Combine(gameDir, "UnityCrashHandler64.exe");
        string antiCheatExe = Path.Combine(gameDir, "start_protected_game.exe");
        string mainGameExe = Path.Combine(gameDir, "SlayTheSpire.exe");
        File.WriteAllBytes(crashExe, [0x4D, 0x5A, 1, 2]);
        File.WriteAllBytes(antiCheatExe, [0x4D, 0x5A, 1, 2]);
        File.WriteAllBytes(mainGameExe, [0x4D, 0x5A, 1, 2, 3, 4]);

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/646570",
            @"C:\Missing\Steam\steam\games\missing.ico",
            steamRootsOverride: [steamRoot],
            executableHasIconProbe: static _ => true);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(mainGameExe), resolved!.Value.Path);
        Assert.True(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void SteamShortcutIconResolver_SelectsSquareLibraryCacheIconAndRejectsWideBanners()
    {
        string steamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "SteamCacheOnly")).FullName;
        string appCacheDir = Directory.CreateDirectory(
            Path.Combine(steamRoot, "appcache", "librarycache", "2835570")).FullName;

        // Wide banners and non-square assets that must NEVER be chosen as a square app icon:
        File.WriteAllBytes(Path.Combine(appCacheDir, "header.jpg"), CreateMinimalJpegHeaderBytes(460, 215));
        File.WriteAllBytes(Path.Combine(appCacheDir, "library_600x900.jpg"), CreateMinimalJpegHeaderBytes(600, 900));
        File.WriteAllBytes(
            Path.Combine(appCacheDir, "1111111111111111111111111111111111111111.jpg"),
            CreateMinimalJpegHeaderBytes(184, 69));

        // Actual 32x32 square clienticon JPEG cached by Steam:
        string squareIconJpg = Path.Combine(
            appCacheDir,
            "abcdef0123456789abcdef0123456789abcdef01.jpg");
        File.WriteAllBytes(squareIconJpg, CreateMinimalJpegHeaderBytes(32, 32));

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/2835570",
            @"C:\Missing\abcdef0123456789abcdef0123456789abcdef01.ico",
            steamRootsOverride: [steamRoot],
            executableHasIconProbe: static _ => false);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(squareIconJpg), resolved!.Value.Path);
        Assert.False(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void InternetShortcutLoadDecision_BypassesUrlDllBlueGlobeWhenIconFileMissingAndKeepsProxyForWebBookmarks()
    {
        MethodInfo? evaluateMethod = typeof(IconHelper).GetMethod(
            "EvaluateInternetShortcutLoadDecision",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(evaluateMethod);

        string brokenSteamUrl = Path.Combine(_tempRoot, "BrokenSteam.url");
        string existingIco = Path.Combine(_tempRoot, "ExistingGame.ico");
        File.WriteAllBytes(existingIco, CreateSolidIcoBytes(32));
        string validSteamUrl = Path.Combine(_tempRoot, "ValidSteam.url");
        string webBookmarkUrl = Path.Combine(_tempRoot, "GitHub.url");

        File.WriteAllText(
            brokenSteamUrl,
            $"[InternetShortcut]\r\nURL=steam://rungameid/999999\r\nIconFile=notepad.exe\r\nIconIndex=0\r\n");
        File.WriteAllText(
            validSteamUrl,
            $"[InternetShortcut]\r\nURL=steam://rungameid/105600\r\nIconFile={existingIco}\r\nIconIndex=0\r\n");
        File.WriteAllText(
            webBookmarkUrl,
            "[InternetShortcut]\r\nURL=https://github.com\r\n");

        // Missing relative IconFile ("notepad.exe" not beside .url, recovered via PATH) -> bypasses url.dll blue globe!
        object recoveredDecision = evaluateMethod!.Invoke(null, [brokenSteamUrl, false])!;
        Assert.False(GetBoolProperty(recoveredDecision, "UseShellProxyFirst"));
        Assert.NotNull(GetObjectProperty(recoveredDecision, "FallbackSource"));

        // Valid IconFile + hidden shortcut arrow -> direct extraction (UseShellProxyFirst = false).
        object validHiddenDecision = evaluateMethod.Invoke(null, [validSteamUrl, true])!;
        Assert.False(GetBoolProperty(validHiddenDecision, "UseShellProxyFirst"));
        Assert.NotNull(GetObjectProperty(validHiddenDecision, "FallbackSource"));

        // Valid IconFile + visible shortcut arrow -> Shell overlay composite (UseShellProxyFirst = true).
        object validOverlayDecision = evaluateMethod.Invoke(null, [validSteamUrl, false])!;
        Assert.True(GetBoolProperty(validOverlayDecision, "UseShellProxyFirst"));

        // Web bookmark with no IconFile -> Shell proxy first to preserve :favicon:$DATA.
        object webDecision = evaluateMethod.Invoke(null, [webBookmarkUrl, true])!;
        Assert.True(GetBoolProperty(webDecision, "UseShellProxyFirst"));
    }

    [Theory]
    [InlineData(96, 48, 48, true)]   // Exact Windows 11 32/48 px Steam .url payload at requestedSize=96 (ratio 0.50)
    [InlineData(96, 32, 32, true)]   // 32 px glyph centered inside 96 px canvas (ratio 0.33)
    [InlineData(256, 128, 128, true)] // 128 px glyph centered inside 256 px Jumbo canvas (ratio 0.50)
    [InlineData(96, 52, 86, false)]  // Tall asymmetric game icon (e.g. Dead Cells flame: height 0.90) must NOT crop
    [InlineData(96, 78, 78, false)]  // Standard full-bleed rounded-square icon must NOT crop
    public void PaddedShellCanvasDetection_CropsCenteredSmallGameIconsWithoutTrimmingNormalArtwork(
        int canvasSize,
        int visibleWidth,
        int visibleHeight,
        bool expectedPadded)
    {
        int left = (canvasSize - visibleWidth) / 2;
        int top = (canvasSize - visibleHeight) / 2;
        byte[] payload = CreateBmpV5PayloadWithArtwork(
            canvasSize,
            canvasSize,
            left,
            top,
            visibleWidth,
            visibleHeight);

        Assert.Equal(expectedPadded, ShellThumbnailProxy.IsLikelyPaddedIconPayload(payload));

        byte[] normalized = Assert.IsType<byte[]>(ShellThumbnailProxy.NormalizeIconPayload(payload));
        int outWidth = BitConverter.ToInt32(normalized, 18);
        int outHeight = Math.Abs(BitConverter.ToInt32(normalized, 22));
        if (expectedPadded)
        {
            Assert.Equal(visibleWidth, outWidth);
            Assert.Equal(visibleHeight, outHeight);
            Assert.False(ShellThumbnailProxy.IsLikelyPaddedIconPayload(normalized));
        }
        else
        {
            Assert.Equal(canvasSize, outWidth);
            Assert.Equal(canvasSize, outHeight);
        }
    }

    private static bool GetBoolProperty(object instance, string propertyName) =>
        (bool)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance)!;

    private static object? GetObjectProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance);

    private static byte[] CreateSolidIcoBytes(int size)
    {
        int andStride = ((size + 31) / 32) * 4;
        int xorBytes = size * size * 4;
        int andBytes = andStride * size;
        int imageBytes = 40 + xorBytes + andBytes;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(imageBytes);
        bw.Write(22);
        bw.Write(40);
        bw.Write(size);
        bw.Write(size * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(imageBytes);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        for (int i = 0; i < size * size; i++)
        {
            bw.Write((byte)0x30);
            bw.Write((byte)0x90);
            bw.Write((byte)0xF0);
            bw.Write((byte)0xFF);
        }

        bw.Write(new byte[andBytes]);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] CreateMinimalJpegHeaderBytes(ushort width, ushort height)
    {
        // SOI (FF D8) + APP0 JFIF (16B) + SOF0 (FF C0, length 11, precision 8, height, width, 1 component) + EOI (FF D9)
        byte[] bytes = new byte[2 + 18 + 13 + 2];
        int o = 0;
        bytes[o++] = 0xFF;
        bytes[o++] = 0xD8;

        // APP0 segment
        bytes[o++] = 0xFF;
        bytes[o++] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), 16);
        o += 16;

        // SOF0 segment
        bytes[o++] = 0xFF;
        bytes[o++] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), 11);
        o += 2;
        bytes[o++] = 8; // precision
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), height);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), width);
        o += 2;
        bytes[o++] = 1; // components
        bytes[o++] = 1;
        bytes[o++] = 0x11;
        bytes[o++] = 0;

        // EOI
        bytes[o++] = 0xFF;
        bytes[o] = 0xD9;
        return bytes;
    }

    private static byte[] CreateBmpV5PayloadWithArtwork(
        int width,
        int height,
        int visibleLeft,
        int visibleTop,
        int visibleWidth,
        int visibleHeight)
    {
        const int pixelOffset = 138;
        byte[] payload = new byte[pixelOffset + (width * height * 4)];
        payload[0] = (byte)'B';
        payload[1] = (byte)'M';
        BitConverter.GetBytes(payload.Length).CopyTo(payload, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(payload, 10);
        BitConverter.GetBytes(124).CopyTo(payload, 14);
        BitConverter.GetBytes(width).CopyTo(payload, 18);
        BitConverter.GetBytes(-height).CopyTo(payload, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(payload, 26);
        BitConverter.GetBytes((ushort)32).CopyTo(payload, 28);

        for (int y = visibleTop; y < visibleTop + visibleHeight; y++)
        {
            for (int x = visibleLeft; x < visibleLeft + visibleWidth; x++)
            {
                int offset = pixelOffset + (((y * width) + x) * 4);
                payload[offset] = 0x22;
                payload[offset + 1] = 0x66;
                payload[offset + 2] = 0xCC;
                payload[offset + 3] = 0xFF;
            }
        }

        return payload;
    }
}
