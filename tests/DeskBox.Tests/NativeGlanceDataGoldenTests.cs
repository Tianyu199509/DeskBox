extern alias GlancePkg;

using System.Text.Json;

namespace DeskBox.Tests;

using GlanceDataFile = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDataFile;
using GlanceData = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceData;
using PackageData = GlancePkg::DeskBox.Models.GlanceWidgetData;

/// <summary>
/// Golden data tests for the native package's persistence layer (audit
/// round 18: source-scan ratchets could not see data loss, so these run the
/// real code). The package must round-trip migrated files LOSSLESSLY -
/// settings it has not wired yet and unknown fields from future hosts
/// survive every save - and it must read the legacy wire formats the host
/// stores historically wrote.
/// </summary>
public class NativeGlanceDataGoldenTests
{
    private static string Root() => Directory.CreateTempSubdirectory("deskbox-glance-golden").FullName;

    private static void WriteData(string root, string json) =>
        File.WriteAllText(Path.Combine(root, GlanceDataFile.FileName), json);

    private static JsonDocument ReadSaved(string root) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(root, GlanceDataFile.FileName)));

    [Fact]
    public void RoundTripPreservesUnknownAndUnwiredFields()
    {
        string root = Root();
        try
        {
            WriteData(root, """
                {
                  "version": 7,
                  "showTime": false,
                  "layout": "Editorial",
                  "transition": "SlideFade",
                  "readability": "Strong",
                  "backgroundImageTransparency": 0.35,
                  "timeScale": 1.2,
                  "futureField": 123,
                  "showChineseFestivals": true,
                  "rotationIntervalMinutes": 30
                }
                """);            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);

            // The user flips one package-owned toggle and the rotation.
            loaded!.Settings.ShowChineseFestivals = false;
            loaded.Settings.RotationIntervalMinutes = 45;
            GlanceDataFile.Save(loaded, root);

            using JsonDocument saved = ReadSaved(root);
            JsonElement o = saved.RootElement;
            // Owned fields: updated.
            Assert.False(o.GetProperty("showChineseFestivals").GetBoolean());
            Assert.Equal(45, o.GetProperty("rotationIntervalMinutes").GetInt32());
            // Everything the package does not own: byte-identical values.
            Assert.False(o.GetProperty("showTime").GetBoolean());
            Assert.Equal("Editorial", o.GetProperty("layout").GetString());
            Assert.Equal("SlideFade", o.GetProperty("transition").GetString());
            Assert.Equal("Strong", o.GetProperty("readability").GetString());
            Assert.Equal(0.35, o.GetProperty("backgroundImageTransparency").GetDouble(), precision: 5);
            Assert.Equal(1.2, o.GetProperty("timeScale").GetDouble(), precision: 5);
            Assert.Equal(123, o.GetProperty("futureField").GetInt32());
            // The schema version is NOT owned by the partial writer (audit
            // 19): the original travels untouched - no false v10 stamp, no
            // downgrade of a future host's newer version.
            Assert.Equal(7, o.GetProperty("version").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsLegacyIntegerEnums()
    {
        string root = Root();
        try
        {
            // Old stores wrote numbers; the host still reads them back, so
            // the package must too (repo golden: write names, read integers).
            WriteData(root, """
                {
                  "version": 7,
                  "traditionalCalendarMode": 9,
                  "backgroundSource": 1,
                  "imageFit": 1
                }
                """);
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.Hebrew,
                loaded!.Settings.TraditionalCalendarMode);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceBackgroundSource.LocalFiles,
                loaded.Settings.BackgroundSource);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceImageFitMode.Fit,
                loaded.Settings.ImageFit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UndefinedEnumValuesAreRejectedEverywhere()
    {
        // Regression pass: Enum.TryParse accepts numeric STRINGS like "999"
        // into undefined values. The patch parser and the migration validator
        // already reject them; the package's own data-file reader must too
        // (all three entry points share one strictness contract).
        string root = Root();
        try
        {
            WriteData(root, """{ "traditionalCalendarMode": "999" }""");
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            // Undefined parse rejected -> model default (None) survives.
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.None,
                loaded!.Settings.TraditionalCalendarMode);

            WriteData(root, """{ "backgroundSource": "NotASource" }""");
            loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceBackgroundSource.Bing,
                loaded!.Settings.BackgroundSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFieldsKeepModelDefaults()
    {
        string root = Root();
        try
        {
            // A real v7-shaped file: no showChineseFestivals, no photo
            // controls, no image fit - defaults must apply.
            WriteData(root, """
                {
                  "version": 7,
                  "backgroundSource": "Bing",
                  "rotationIntervalMinutes": 30
                }
                """);
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Settings.ShowChineseFestivals);
            Assert.Equal(GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.None, loaded.Settings.TraditionalCalendarMode);
            Assert.True(loaded.Settings.ShowPhotoControls);
            Assert.Equal(GlancePkg::DeskBox.Models.GlanceImageFitMode.Fill, loaded.Settings.ImageFit);
            Assert.Equal(30, loaded.Settings.RotationIntervalMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptPrimaryFallsBackToBackup()
    {
        string root = Root();
        try
        {
            // First save creates the primary; a second save rotates the
            // previous content into the .bak via File.Replace.
            GlanceDataFile.Save(new GlanceData(new PackageData { RotationIntervalMinutes = 77 }, default), root);
            GlanceDataFile.Save(new GlanceData(new PackageData { RotationIntervalMinutes = 88 }, default), root);
            Assert.True(File.Exists(Path.Combine(root, GlanceDataFile.FileName + ".bak")));

            File.WriteAllText(Path.Combine(root, GlanceDataFile.FileName), "{ torn write");
            GlanceData? recovered = GlanceDataFile.Load(root);
            Assert.NotNull(recovered);
            Assert.Equal(77, recovered!.Settings.RotationIntervalMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptFileDegradesToNull()
    {
        string root = Root();
        try
        {
            WriteData(root, "{ not json at all");
            Assert.Null(GlanceDataFile.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MinimalPatchCarriesOnlyTheMutatedField()
    {
        // Audit round 20 (lost update): a toggle must send ONLY its own
        // field - a full owned snapshot would overwrite concurrent host-side
        // changes with the widget's stale cache.
        var settings = new PackageData
        {
            ShowChineseFestivals = false, // the mutated field
            RandomOrder = true,
            RotationIntervalMinutes = 30,
        };
        string minimal = GlanceDataFile.BuildOwnedPatch(settings, "showChineseFestivals");
        using (JsonDocument document = JsonDocument.Parse(minimal))
        {
            Assert.Equal(1, document.RootElement.EnumerateObject().Count());
            Assert.False(document.RootElement.GetProperty("showChineseFestivals").GetBoolean());
        }

        string full = GlanceDataFile.BuildOwnedPatch(settings);
        using (JsonDocument document = JsonDocument.Parse(full))
        {
            // 9 interaction + 6 display/time + layout + 4 playback-appearance
            // fields.
            Assert.Equal(20, document.RootElement.EnumerateObject().Count());
        }
    }

    [Fact]
    public void RuntimeStateTrySaveNeverThrows()
    {
        string root = Root();
        try
        {
            // A FILE where the data root directory should be: every write
            // must fail, and the failure must be contained (audit 20 -
            // destroy can never fail because of ephemeral state).
            string fileAsRoot = Path.Combine(root, "not-a-directory");
            File.WriteAllText(fileAsRoot, "x");
            var state = new GlancePkg::DeskBox.GlancePackage.Rendering.GlanceRuntimeState
            {
                Paused = true,
                ImageIndex = 3,
            };
            Assert.False(GlancePkg::DeskBox.GlancePackage.Rendering.GlanceRuntimeState.TrySave(state, fileAsRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DestroyAndUnloadSemanticsStayStrict()
    {
        // Audit round 20 contracts: dispose-before-remove (the destroy
        // transaction) and Unloaded-as-visual-pause only.
        string exports = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Abi/Exports.cs"));
        int dispose = exports.IndexOf("handle.Dispose();", StringComparison.Ordinal);
        int remove = exports.IndexOf("_handles.Remove(widgetHandle)", StringComparison.Ordinal);
        Assert.True(dispose >= 0 && remove > dispose,
            "destroy must dispose the controller BEFORE removing the handle");

        string controller = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/GlanceWidgetController.cs"));
        Assert.DoesNotContain("Unloaded += (_, _) => Dispose()", controller);
        Assert.Contains("StopVisualResources", controller);
        // Hidden-across-midnight recovery: reveal must re-derive the date.
        Assert.Contains("EnsureCurrentDate", controller);
    }
}
