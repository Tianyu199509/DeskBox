using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RestoreMachineLocalPathRebaseTests
{
    private const string AliceDataPath = @"C:\Users\alice\AppData\Local\DeskBox\data";
    private const string AliceProfile = @"C:\Users\alice";
    private const string BobProfile = @"C:\Users\bob";

    [Theory]
    [InlineData(RestoreMachineLocalPathRebaseTests.AliceDataPath, @"C:\Users\alice")]
    [InlineData(
        @"C:\Users\alice\AppData\Local\Packages\Corp.DeskBox_abc\LocalCache\DeskBox\data",
        @"C:\Users\alice")]
    [InlineData(@"D:\profiles\alice\AppData\Local\DeskBox\data", @"D:\profiles\alice")]
    [InlineData(
        @"C:\Users\host\AppData\Local\Temp\sandbox\Users\alice\AppData\Local\DeskBox\data",
        @"C:\Users\host\AppData\Local\Temp\sandbox\Users\alice")]
    public void TryDeriveSourceProfileRoot_ExtractsTheProfileAboveTheAppDataSegment(
        string sourceDataPath,
        string expected)
    {
        Assert.Equal(
            expected,
            RestoreMachineLocalPathRebase.TryDeriveSourceProfileRoot(sourceDataPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"D:\dev-data\DeskBox\data")]
    [InlineData(@"AppData\Local\DeskBox\data")]
    public void TryDeriveSourceProfileRoot_ReturnsNullWithoutAnAppDataSegment(
        string? sourceDataPath)
    {
        Assert.Null(RestoreMachineLocalPathRebase.TryDeriveSourceProfileRoot(sourceDataPath));
    }

    [Fact]
    public void Rebase_SkipsWhenTheProfilesMatch()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"C:\Users\alice\DeskBox",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            AliceProfile);

        Assert.False(result.Performed);
        Assert.False(result.Changed);
        Assert.Equal(@"C:\Users\alice\DeskBox", settings.DefaultManagedStorageRootPath);
    }

    [Fact]
    public void Rebase_SkipsWithoutASourceDataPath()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"C:\Users\alice\DeskBox",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            null,
            BobProfile);

        Assert.False(result.Performed);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Rebase_MovesProfileLocalSettingsPathsToTheCurrentProfile()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"C:\Users\alice\DeskBox",
            AutomaticBackupDirectory = @"C:\Users\alice\Backups\DeskBox",
            ManagedStorageDesktopShortcutPath = @"C:\Users\alice\Desktop",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.True(result.Performed);
        Assert.Equal(3, result.SettingsPathsRebased);
        Assert.Equal(@"C:\Users\bob\DeskBox", settings.DefaultManagedStorageRootPath);
        Assert.Equal(@"C:\Users\bob\Backups\DeskBox", settings.AutomaticBackupDirectory);
        Assert.Equal(@"C:\Users\bob\Desktop", settings.ManagedStorageDesktopShortcutPath);
        Assert.Equal(
            @"C:\Users\bob\DeskBox",
            result.RebasedManagedStorageRootPath);
    }

    [Fact]
    public void Rebase_LeavesPathsOutsideTheSourceProfileAlone()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"D:\DeskBox",
            AutomaticBackupDirectory = @"D:\Backups",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.True(result.Performed);
        Assert.Equal(0, result.SettingsPathsRebased);
        Assert.False(result.Changed);
        Assert.Equal(@"D:\DeskBox", settings.DefaultManagedStorageRootPath);
        Assert.Equal(@"D:\Backups", settings.AutomaticBackupDirectory);
        Assert.Null(result.RebasedManagedStorageRootPath);
    }

    [Fact]
    public void Rebase_RepointsOnlyDefaultStorageFileWidgets()
    {
        var followsDefault = new WidgetConfig
        {
            Id = "follows",
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = "Work",
            MappedFolderPath = @"C:\Users\alice\DeskBox\Work",
        };
        var followsDefaultElsewhere = new WidgetConfig
        {
            Id = "elsewhere",
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = true,
            MappedFolderPath = @"D:\Shared",
        };
        var customUnderSourceProfile = new WidgetConfig
        {
            Id = "custom",
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = false,
            MappedFolderPath = @"C:\Users\alice\Documents\Projects",
        };
        var todoWidget = new WidgetConfig
        {
            Id = "todo",
            WidgetKind = WidgetKind.Todo,
            FollowsDefaultStoragePath = true,
            MappedFolderPath = @"C:\Users\alice\DeskBox\Todo",
        };
        var settings = new AppSettings();
        settings.WidgetLayout.Widgets.AddRange(
            [followsDefault, followsDefaultElsewhere, customUnderSourceProfile, todoWidget]);

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.Equal(1, result.WidgetsRebased);
        Assert.Equal(@"C:\Users\bob\DeskBox\Work", followsDefault.MappedFolderPath);
        Assert.Equal(@"D:\Shared", followsDefaultElsewhere.MappedFolderPath);
        // Custom folders are the user's own locations: rebasing them would
        // manufacture plausible-looking empty folders on this machine.
        Assert.Equal(@"C:\Users\alice\Documents\Projects", customUnderSourceProfile.MappedFolderPath);
        Assert.Equal(@"C:\Users\alice\DeskBox\Todo", todoWidget.MappedFolderPath);
    }

    [Fact]
    public void Rebase_DoesNotMatchSiblingAccountPrefixes()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"C:\Users\alice2\DeskBox",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.True(result.Performed);
        Assert.Equal(0, result.SettingsPathsRebased);
        Assert.Equal(@"C:\Users\alice2\DeskBox", settings.DefaultManagedStorageRootPath);
    }

    [Fact]
    public void Rebase_MatchesTheSourceProfileCaseInsensitively()
    {
        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"c:\users\ALICE\DeskBox",
        };

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.Equal(1, result.SettingsPathsRebased);
        Assert.Equal(@"C:\Users\bob\DeskBox", settings.DefaultManagedStorageRootPath);
    }

    [Fact]
    public void Rebase_MovesMusicLocalLyricsFolderUnderSourceProfile()
    {
        var settings = new AppSettings();
        settings.Music.MusicLocalLyricsFolder = @"C:\Users\alice\Music\Lyrics";

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(
            settings,
            AliceDataPath,
            BobProfile);

        Assert.True(result.Performed);
        Assert.True(result.Changed);
        Assert.Equal(1, result.SettingsPathsRebased);
        Assert.Equal(@"C:\Users\bob\Music\Lyrics", settings.Music.MusicLocalLyricsFolder);
        Assert.Equal(@"C:\Users\bob\Music\Lyrics", settings.MusicLocalLyricsFolder);
    }
}
