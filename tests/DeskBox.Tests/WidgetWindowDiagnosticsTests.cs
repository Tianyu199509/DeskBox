using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetWindowDiagnosticsTests
{
    [Fact]
    public void AnimationBounds_UsesConfigAndKeepsMinimumExtent()
    {
        var config = new WidgetConfig
        {
            Id = "1234567890",
            Name = "Test",
            X = 12,
            Y = 34,
            Width = 0,
            Height = -5
        };
        var diagnostics = new WidgetWindowDiagnostics("File", config, () => new IntPtr(0x1234));

        var bounds = diagnostics.AnimationBounds;

        Assert.Equal(12, bounds.X);
        Assert.Equal(34, bounds.Y);
        Assert.Equal(1, bounds.Width);
        Assert.Equal(1, bounds.Height);
    }

    [Fact]
    public void FormatTrayWindowMessage_UsesStableKindNameIdAndHandle()
    {
        var config = new WidgetConfig
        {
            Id = "abcdef123456",
            Name = "Documents",
            WidgetKind = WidgetKind.QuickCapture
        };
        var diagnostics = new WidgetWindowDiagnostics("Quick", config, () => new IntPtr(0xBEEF));

        string message = diagnostics.FormatTrayWindowMessage("PrepareHide gen=2");

        Assert.Equal("[TrayWindow] Quick Documents#abcdef12 hwnd=0xBEEF PrepareHide gen=2", message);
    }

    [Fact]
    public void Identity_ExposesReadOnlyWindowContext()
    {
        var config = new WidgetConfig
        {
            Id = "file-widget-123",
            Name = "Work",
            WidgetKind = WidgetKind.File,
            X = 3,
            Y = 4,
            Width = 320,
            Height = 180
        };
        var diagnostics = new WidgetWindowDiagnostics("File", config, () => new IntPtr(0xCAFE));

        var identity = diagnostics.Identity;

        Assert.Equal("file-widget-123", identity.WidgetId);
        Assert.Equal(WidgetKind.File, identity.WidgetKind);
        Assert.Equal("Work", identity.Name);
        Assert.Equal("File", identity.LogKind);
        Assert.Equal("file-wid", identity.ShortWidgetId);
        Assert.Equal(new IntPtr(0xCAFE), identity.WindowHandle);
        Assert.Equal("Work#file-wid", identity.DisplayName);
        Assert.Equal("File Work#file-wid", identity.LogDisplayName);
        Assert.Equal(3, identity.AnimationBounds.X);
        Assert.Equal(4, identity.AnimationBounds.Y);
        Assert.Equal(320, identity.AnimationBounds.Width);
        Assert.Equal(180, identity.AnimationBounds.Height);
    }

    [Theory]
    [InlineData("", "none")]
    [InlineData("abc", "abc")]
    [InlineData("12345678", "12345678")]
    [InlineData("123456789", "12345678")]
    public void ShortId_HandlesEmptyShortAndLongIds(string id, string expected)
    {
        Assert.Equal(expected, WidgetWindowDiagnostics.ShortId(id));
    }
}
