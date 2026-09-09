namespace DeskBox.Tests;

/// <summary>
/// Write-through channel (HostApi v3, audit round 19): native setting
/// mutations must commit into the authoritative built-in store. The patch
/// parser is type-strict (a malformed payload is rejected before touching
/// authority) and the apply path goes through the store's own update
/// pipeline so built-in listeners refresh live.
/// </summary>
public class NativeInstanceConfigPatchTests
{
    private static string Root() => Directory.CreateTempSubdirectory("deskbox-config-patch").FullName;

    [Fact]
    public async Task PatchCommitsThroughTheAuthoritativeStore()
    {
        string root = Root();
        try
        {
            string widgetId = Guid.NewGuid().ToString();
            var store = new DeskBox.Services.GlanceWidgetStore(root, widgetId);
            await store.SaveAsync(new DeskBox.Models.GlanceWidgetData
            {
                RotationIntervalMinutes = 30,
                ShowChineseFestivals = true,
            });

            DeskBox.Services.GlanceInstanceConfigPatch? patch =
                DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
                    """{"showChineseFestivals":false,"rotationIntervalMinutes":5}""");
            Assert.NotNull(patch);
            await store.UpdateAsync(data => patch!.ApplyTo(data));

            DeskBox.Models.GlanceWidgetData reloaded = await store.LoadAsync();
            Assert.False(reloaded.ShowChineseFestivals);
            Assert.Equal(5, reloaded.RotationIntervalMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TypeInvalidPatchIsRejected()
    {
        // A string where a number belongs: syntactically valid JSON that the
        // built-in deserializer would reject - the channel must reject it
        // too instead of committing garbage (audit round 19).
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
            """{"rotationIntervalMinutes":"abc"}"""));
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
            """{"localImagePaths":["a.png",5]}"""));
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse("[1,2,3]"));
    }

    [Fact]
    public void IntegerEnumPatchIsAccepted()
    {
        DeskBox.Services.GlanceInstanceConfigPatch? patch =
            DeskBox.Services.GlanceInstanceConfigPatch.TryParse("""{"traditionalCalendarMode":9}""");
        Assert.NotNull(patch);
        Assert.Equal(DeskBox.Models.GlanceTraditionalCalendarMode.Hebrew, patch!.TraditionalCalendarMode);
    }

    [Fact]
    public void UndefinedEnumValuesAreRejected()
    {
        // TryParse accepts numeric STRINGS into undefined values; the patch
        // channel must not commit those (audit round 20).
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
            """{"traditionalCalendarMode":"999"}"""));
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
            """{"backgroundSource":"NotAMode"}"""));
        Assert.Null(DeskBox.Services.GlanceInstanceConfigPatch.TryParse(
            """{"backgroundSource":{}}"""));
    }

    [Fact]
    public void ClearingTheLocalFolderMapsEmptyStringToNull()
    {
        DeskBox.Services.GlanceInstanceConfigPatch? patch =
            DeskBox.Services.GlanceInstanceConfigPatch.TryParse("""{"localFolderPath":""}""");
        Assert.NotNull(patch);
        var data = new DeskBox.Models.GlanceWidgetData { LocalFolderPath = @"C:\old" };
        patch!.ApplyTo(data);
        Assert.Null(data.LocalFolderPath);
    }
}
