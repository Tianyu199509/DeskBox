using DeskBox.Helpers;
using DeskBox.Models;
using System.Text.Json;

namespace DeskBox.Tests;

public sealed class FileInteractionPolicyTests
{
    [Fact]
    public void DropIntent_ReferenceGridNeverTurnsIntoAFileTransfer()
    {
        Assert.Equal(
            FileDropIntent.Reference,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: false,
                forceCopy: false,
                controlDown: false,
                shiftDown: true,
                defaultMove: true));
    }

    [Fact]
    public void DropIntent_VirtualPayloadAlwaysCopies()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: true,
                controlDown: false,
                shiftDown: true,
                defaultMove: true));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void DropIntent_AltOrCtrlShiftCreatesShortcutForMappedFiles(
        bool altDown,
        bool controlDown,
        bool shiftDown)
    {
        Assert.Equal(
            FileDropIntent.Shortcut,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown,
                shiftDown,
                defaultMove: true,
                altDown: altDown,
                canLink: true));
    }

    [Fact]
    public void DropIntent_FollowWindowsUsesCopyAcrossVolumes()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                followWindows: true,
                sameVolume: false));
        Assert.Equal(
            FileDropIntent.Move,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: false,
                followWindows: true,
                sameVolume: true));
    }

    [Theory]
    [InlineData(24, 1, 28)]
    [InlineData(32, 1, 36)]
    [InlineData(40, -1, 36)]
    [InlineData(56, 1, 56)]
    [InlineData(24, -1, 24)]
    public void IconSizePolicy_UsesDiscreteBoundedSteps(
        double current,
        int direction,
        double expected)
    {
        Assert.Equal(expected, FileWidgetIconSizePolicy.GetNext(current, direction));
    }

    [Fact]
    public void DropIntent_ShortcutOutsideDesktop_MovesDesktopResidentSources()
    {
        Assert.Equal(
            FileDropIntent.Move,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
    }

    [Fact]
    public void DropIntent_ShortcutOutsideDesktop_LinksNonDesktopSources()
    {
        Assert.Equal(
            FileDropIntent.Shortcut,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false,
                canLink: true));
    }

    [Fact]
    public void DropIntent_ShortcutOutsideDesktop_ModifierGesturesStillWin()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: true,
                shiftDown: false,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        Assert.Equal(
            FileDropIntent.Shortcut,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                altDown: true,
                canLink: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
    }

    [Fact]
    public void DropIntent_ShortcutOutsideDesktop_PreservesDefaultWhenDisabled()
    {
        Assert.Equal(
            FileDropIntent.Move,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                shortcutOutsideDesktop: false,
                sourcesOnDesktop: false));
    }

    [Fact]
    public void DropIntent_ShortcutOutsideDesktop_ForceCopyStillWins()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: true,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
    }

    [Theory]
    [InlineData(@"C:\Users\Me\Desktop\file.txt", @"C:\Users\Me\Desktop", true)]
    [InlineData(@"C:\Users\Me\Desktop", @"C:\Users\Me\Desktop", true)]
    [InlineData(@"C:\Users\Me\DesktopBackup\file.txt", @"C:\Users\Me\Desktop", false)]
    [InlineData(@"C:\Users\Me\Downloads\file.txt", @"C:\Users\Me\Desktop", false)]
    public void IsUnderDirectory_TreatsBoundariesAndSiblingsCorrectly(
        string path,
        string directory,
        bool expected)
    {
        Assert.Equal(expected, FileDropIntentPolicy.IsUnderDirectory(path, directory));
    }

    [Fact]
    public void AreAllUnderDirectories_RequiresEverySourceUnderARoot()
    {
        var roots = new[]
        {
            @"C:\Users\Me\Desktop",
            @"C:\Users\Public\Desktop"
        };

        Assert.True(FileDropIntentPolicy.AreAllUnderDirectories(
            [@"C:\Users\Me\Desktop\a.txt", @"C:\Users\Public\Desktop\b.txt"],
            roots));
        Assert.False(FileDropIntentPolicy.AreAllUnderDirectories(
            [@"C:\Users\Me\Desktop\a.txt", @"C:\Users\Me\Downloads\b.txt"],
            roots));
    }

    [Fact]
    public void AreAllUnderDirectories_UnknownPayloadCountsAsDesktopResident()
    {
        Assert.True(FileDropIntentPolicy.AreAllUnderDirectories(
            [],
            [@"C:\Users\Me\Desktop"]));
    }

    [Fact]
    public void WidgetIconSizeOverride_RoundTripsWithoutChangingTheGlobalSetting()
    {
        var config = new WidgetConfig { IconSizeOverride = 48 };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.Equal(48, restored?.IconSizeOverride);
    }
}
