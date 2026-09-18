using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Ownership contract for the 2A settings slice refactor. AppSettings is a
/// serialization facade: every public settable property must delegate to
/// exactly one *SettingsSlice object, and the wire shape must stay identical.
/// </summary>
public sealed class SettingsSliceOwnershipContractTests
{
    private static readonly PropertyInfo[] SliceProperties =
        typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType.Name.EndsWith("SettingsSlice", StringComparison.Ordinal))
            .ToArray();

    private static readonly PropertyInfo[] FacadeProperties =
        typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p is { CanRead: true, CanWrite: true }
                        && p.Name != nameof(AppSettings.SchemaVersion)
                        && !p.PropertyType.Name.EndsWith("SettingsSlice", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void EverySlice_IsExposedAsGetOnlyAndJsonIgnored()
    {
        Assert.Equal(13, SliceProperties.Length);
        Assert.All(SliceProperties, p =>
        {
            Assert.False(p.CanWrite, $"{p.Name} must be get-only");
            Assert.NotNull(p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>());
        });
    }

    [Fact]
    public void EveryFacadeProperty_MapsToExactlyOneSliceProperty()
    {
        Assert.Equal(217, FacadeProperties.Length);

        foreach (PropertyInfo facade in FacadeProperties)
        {
            var matches = SliceProperties
                .Select(s => s.PropertyType.GetProperty(facade.Name))
                .Where(candidate => candidate is { CanRead: true, CanWrite: true }
                                    && candidate.PropertyType == facade.PropertyType)
                .ToArray();
            Assert.Single(matches);
        }
    }

    [Fact]
    public void EverySliceProperty_IsReachableThroughTheFacade()
    {
        var facadeNames = FacadeProperties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (PropertyInfo slice in SliceProperties)
        {
            foreach (PropertyInfo prop in slice.PropertyType.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.Contains(prop.Name, facadeNames);
            }
        }
    }

    [Fact]
    public void FacadePassthroughs_ReadAndWriteSliceState()
    {
        var settings = new AppSettings();

        foreach (PropertyInfo facade in FacadeProperties)
        {
            object? sentinel = NonDefaultValue(facade.PropertyType);
            facade.SetValue(settings, sentinel);

            PropertyInfo? sliceProp = SliceProperties
                .Select(s => s.PropertyType.GetProperty(facade.Name))
                .Single(candidate => candidate is { CanRead: true, CanWrite: true }
                                     && candidate.PropertyType == facade.PropertyType);
            Assert.NotNull(sliceProp);
            PropertyInfo slice = SliceProperties
                .Single(s => s.PropertyType == sliceProp.DeclaringType);

            Assert.Equal(
                SerializeSettingValue(sentinel, facade.PropertyType),
                SerializeSettingValue(sliceProp.GetValue(slice.GetValue(settings)), facade.PropertyType));
        }
    }

    [Fact]
    public void Serialization_PopulatedRoundTrip_IsStable()
    {
        var settings = new AppSettings { LegacyWidgetCapsuleModeEnabled = true };
        foreach (PropertyInfo facade in FacadeProperties)
        {
            facade.SetValue(settings, NonDefaultValue(facade.PropertyType));
        }

        string first = JsonSerializer.Serialize(
            settings, SettingsJsonContext.Default.AppSettings);
        AppSettings? restored = JsonSerializer.Deserialize(
            first, SettingsJsonContext.Default.AppSettings);
        Assert.NotNull(restored);
        string second = JsonSerializer.Serialize(
            restored, SettingsJsonContext.Default.AppSettings);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Serialization_DisabledFeatureSections_RoundTripUnchanged()
    {
        // Simulate a profile where feature widgets are disabled but their
        // section data must still round-trip (disable→save→enable must not
        // reset the section).
        var settings = new AppSettings
        {
            QuickCaptureEnabled = false,
            TodoEnabled = false,
            FeatureWidgetEnabledStates = new Dictionary<string, bool>
            {
                ["Music"] = false,
                ["Weather"] = false,
            },
        };
        settings.QuickCapture.QuickCaptureRecentLimit = 87;
        settings.Todo.TodoDefaultReminderOffsetMinutes = 42;
        settings.Weather.WeatherCityName = "Hanoi";
        settings.Search.SearchDefaultTab = "file";

        string json = JsonSerializer.Serialize(
            settings, SettingsJsonContext.Default.AppSettings);
        AppSettings? restored = JsonSerializer.Deserialize(
            json, SettingsJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        Assert.Equal(87, restored.QuickCapture.QuickCaptureRecentLimit);
        Assert.Equal(42, restored.Todo.TodoDefaultReminderOffsetMinutes);
        Assert.Equal("Hanoi", restored.Weather.WeatherCityName);
        Assert.Equal("file", restored.Search.SearchDefaultTab);
        Assert.Equal(
            json,
            JsonSerializer.Serialize(restored, SettingsJsonContext.Default.AppSettings));
    }

    private static object? NonDefaultValue(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { IsEnum: true } enumType)
            return Enum.GetValues(enumType).Cast<object>().First();
        if (type == typeof(string))
            return "sentinel";
        if (type == typeof(bool))
            return true;
        if (type == typeof(bool?))
            return true;
        if (type == typeof(int))
            return 17;
        if (type == typeof(int?))
            return 17;
        if (type == typeof(double))
            return 0.137;
        if (type == typeof(long))
            return 638000000000000000L;
        if (type == typeof(DateTimeOffset?))
            return new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().First();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            Type itemType = type.GetGenericArguments()[0];
            list.Add(itemType == typeof(string)
                ? "sentinel"
                : Activator.CreateInstance(itemType)!);
            return list;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dict = (System.Collections.IDictionary)Activator.CreateInstance(type)!;
            Type[] args = type.GetGenericArguments();
            object key = args[0] == typeof(string)
                ? "sentinel"
                : Activator.CreateInstance(args[0])!;
            object value = args[1] == typeof(bool)
                ? true
                : Activator.CreateInstance(args[1])!;
            dict.Add(key, value);
            return dict;
        }
        throw new NotSupportedException($"No sentinel factory for {type}.");
    }

    // Facade-access ratchet: exact per-file counts of `X.settings.<passthrough>`
    // accesses measured on 2026-10-02 — case-insensitive on `settings` so
    // `settings.`/`_settings.` locals are counted too (letter-lookbehind keeps
    // `WidgetSettings.`/`appSettings.` out). Entries may only shrink or
    // disappear: a growing file or a new file means new facade coupling — new
    // code should read the slices (Settings.<Slice>.Prop). Deleting a
    // passthrough forces its call sites to migrate or stop compiling, so the
    // name set self-maintains as the facade collapses.
    private static readonly IReadOnlyDictionary<string, int> FacadeAccessManifest =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/DeskBox/App.AotHotkeySmoke.cs"] = 2,
        ["src/DeskBox/App.AotManagedUiSmoke.cs"] = 6,
        ["src/DeskBox/App.AotTodoNotificationActivationSmoke.cs"] = 5,
        ["src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs"] = 4,
        ["src/DeskBox/App.AotTodoNotificationSurfaceSmoke.cs"] = 5,
        ["src/DeskBox/App.AotTodoNotificationUserClickSmoke.cs"] = 8,
        ["src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs"] = 6,
        ["src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs"] = 17,
        ["src/DeskBox/App.AotWeatherSurfacePersistenceSmoke.cs"] = 17,
        ["src/DeskBox/App.DiagnosticsBundle.cs"] = 6,
        ["src/DeskBox/App.ImmediateHiddenWorkingSetTrim.cs"] = 2,
        ["src/DeskBox/App.Tray.cs"] = 3,
        ["src/DeskBox/App.xaml.cs"] = 31,
        ["src/DeskBox/Controls/DesktopOrganizationPreviewCard.xaml.cs"] = 11,
        ["src/DeskBox/Controls/DesktopOrganizationTaskView.Appearance.cs"] = 1,
        ["src/DeskBox/Controls/DesktopOrganizationTaskView.xaml.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.IconSizing.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.KeyboardNavigation.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ShortcutDrop.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"] = 16,
        ["src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"] = 5,
        ["src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"] = 6,
        ["src/DeskBox/Controls/WidgetContents/SearchWidgetContent.xaml.cs"] = 6,
        ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Menus.cs"] = 1,
        ["src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"] = 1,
        ["src/DeskBox/Controls/WidgetShell.xaml.cs"] = 2,
        ["src/DeskBox/Helpers/QuickCaptureClipboardActivationHelper.cs"] = 1,
        ["src/DeskBox/Services/AutoStartDefaultPolicy.cs"] = 1,
        ["src/DeskBox/Services/DataBackupSettingsPolicy.cs"] = 15,
        ["src/DeskBox/Services/DesktopAutoOrganizationWatcher.cs"] = 13,
        ["src/DeskBox/Services/DesktopDoubleClickActivationService.cs"] = 4,
        ["src/DeskBox/Services/DesktopOrganizationCoordinator.cs"] = 13,
        ["src/DeskBox/Services/DesktopOrganizationTransaction.Restore.cs"] = 3,
        ["src/DeskBox/Services/DesktopOrganizationTransaction.cs"] = 9,
        ["src/DeskBox/Services/DirectStartupService.cs"] = 2,
        ["src/DeskBox/Services/DragDropPermissionService.cs"] = 3,
        ["src/DeskBox/Services/EverythingSearchService.cs"] = 12,
        ["src/DeskBox/Services/FeatureWidgetSettings.cs"] = 13,
        ["src/DeskBox/Services/FileWidgetFolderOpenBehaviorNames.cs"] = 1,
        ["src/DeskBox/Services/FileWidgetIconLayout.cs"] = 6,
        ["src/DeskBox/Services/GlobalHotkeyService.cs"] = 17,
        ["src/DeskBox/Services/InitialFileWidgetSetupPolicy.cs"] = 2,
        ["src/DeskBox/Services/JumpListService.cs"] = 1,
        ["src/DeskBox/Services/LocalizationService.cs"] = 3,
        ["src/DeskBox/Services/ManagedStorageDesktopShortcutService.cs"] = 12,
        ["src/DeskBox/Services/PerformanceSettingsPolicy.cs"] = 78,
        ["src/DeskBox/Services/QuickCaptureClipboardService.cs"] = 6,
        ["src/DeskBox/Services/SearchEngineService.cs"] = 7,
        ["src/DeskBox/Services/SearchHotkeyService.cs"] = 12,
        ["src/DeskBox/Services/SearchResultActionService.cs"] = 2,
        ["src/DeskBox/Services/SettingsMigrationService.cs"] = 35,
        ["src/DeskBox/Services/SettingsSearchCatalog.cs"] = 20,
        ["src/DeskBox/Services/SettingsService.cs"] = 605,
        ["src/DeskBox/Services/ThemeService.cs"] = 11,
        ["src/DeskBox/Services/TodoReminderService.cs"] = 8,
        ["src/DeskBox/Services/WeatherService.cs"] = 1,
        ["src/DeskBox/Services/WeatherSettingsPolicy.cs"] = 17,
        ["src/DeskBox/Services/WidgetAnimationSettings.cs"] = 4,
        ["src/DeskBox/Services/WidgetChromeMenuBuilder.cs"] = 5,
        ["src/DeskBox/Services/WidgetChromeModeResolver.cs"] = 2,
        ["src/DeskBox/Services/WidgetForegroundSettings.cs"] = 8,
        ["src/DeskBox/Services/WidgetGroupMenuBuilder.cs"] = 1,
        ["src/DeskBox/Services/WidgetGroupSettings.cs"] = 19,
        ["src/DeskBox/Services/WidgetManager.CapsuleArrangement.cs"] = 44,
        ["src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"] = 48,
        ["src/DeskBox/Services/WidgetManager.Groups.cs"] = 57,
        ["src/DeskBox/Services/WidgetManager.Storage.cs"] = 19,
        ["src/DeskBox/Services/WidgetManager.Surfaces.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.TrayAnimation.cs"] = 3,
        ["src/DeskBox/Services/WidgetManager.cs"] = 27,
        ["src/DeskBox/Services/WidgetStartupRestorePolicy.cs"] = 2,
        ["src/DeskBox/Services/WidgetTopologyLayoutService.cs"] = 22,
        ["src/DeskBox/ViewModels/GlanceWidgetViewModel.cs"] = 4,
        ["src/DeskBox/ViewModels/MusicWidgetViewModel.Lifecycle.cs"] = 1,
        ["src/DeskBox/ViewModels/MusicWidgetViewModel.cs"] = 5,
        ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.ItemSync.cs"] = 2,
        ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs"] = 3,
        ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.SettingsAndRefresh.cs"] = 14,
        ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.cs"] = 17,
        ["src/DeskBox/ViewModels/SearchPopupViewModel.cs"] = 13,
        ["src/DeskBox/ViewModels/SettingsViewModel.AboutAndUpdates.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.AppearanceCallbacks.cs"] = 16,
        ["src/DeskBox/ViewModels/SettingsViewModel.AppearanceOptions.cs"] = 18,
        ["src/DeskBox/ViewModels/SettingsViewModel.CapsuleOptions.cs"] = 34,
        ["src/DeskBox/ViewModels/SettingsViewModel.ContentEditorOptions.cs"] = 24,
        ["src/DeskBox/ViewModels/SettingsViewModel.DataBackupOptions.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.DesktopOrganization.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.DisplayNames.cs"] = 6,
        ["src/DeskBox/ViewModels/SettingsViewModel.FeatureCallbacks.cs"] = 28,
        ["src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs"] = 71,
        ["src/DeskBox/ViewModels/SettingsViewModel.FeatureTextSize.cs"] = 5,
        ["src/DeskBox/ViewModels/SettingsViewModel.FileStackOptions.cs"] = 32,
        ["src/DeskBox/ViewModels/SettingsViewModel.GroupNavigation.cs"] = 28,
        ["src/DeskBox/ViewModels/SettingsViewModel.HotkeyAndStorage.cs"] = 6,
        ["src/DeskBox/ViewModels/SettingsViewModel.HoverActions.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.Performance.cs"] = 11,
        ["src/DeskBox/ViewModels/SettingsViewModel.PreferenceCallbacks.cs"] = 21,
        ["src/DeskBox/ViewModels/SettingsViewModel.PreferenceCommands.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.QuickCaptureDiagnostics.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.RuntimeDiagnostics.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs"] = 133,
        ["src/DeskBox/ViewModels/SettingsViewModel.WeatherOptions.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.WidgetForeground.cs"] = 6,
        ["src/DeskBox/ViewModels/SettingsViewModel.cs"] = 141,
        ["src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs"] = 1,
        ["src/DeskBox/ViewModels/TodoWidgetViewModel.FilteringAndAppearance.cs"] = 21,
        ["src/DeskBox/ViewModels/TodoWidgetViewModel.cs"] = 12,
        ["src/DeskBox/ViewModels/WeatherWidgetViewModel.DataProcessing.cs"] = 21,
        ["src/DeskBox/ViewModels/WeatherWidgetViewModel.RefreshAndLayout.cs"] = 1,
        ["src/DeskBox/ViewModels/WeatherWidgetViewModel.cs"] = 22,
        ["src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"] = 3,
        ["src/DeskBox/ViewModels/WidgetViewModel.LayoutAndSettings.cs"] = 17,
        ["src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"] = 2,
        ["src/DeskBox/ViewModels/WidgetViewModel.Stacks.cs"] = 13,
        ["src/DeskBox/ViewModels/WidgetViewModel.cs"] = 7,
        ["src/DeskBox/Views/ContentWidgetWindow.Commands.cs"] = 3,
        ["src/DeskBox/Views/ContentWidgetWindow.File.cs"] = 1,
        ["src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"] = 3,
        ["src/DeskBox/Views/ContentWidgetWindow.QuickCapture.cs"] = 1,
        ["src/DeskBox/Views/ContentWidgetWindow.TrayAnimations.cs"] = 4,
        ["src/DeskBox/Views/ContentWidgetWindow.xaml.cs"] = 16,
        ["src/DeskBox/Views/OnboardingWindow.Appearance.cs"] = 10,
        ["src/DeskBox/Views/OnboardingWindow.Completion.cs"] = 5,
        ["src/DeskBox/Views/OnboardingWindow.DesktopOrganization.cs"] = 1,
        ["src/DeskBox/Views/OnboardingWindow.Features.cs"] = 1,
        ["src/DeskBox/Views/OnboardingWindow.Hotkey.cs"] = 15,
        ["src/DeskBox/Views/OnboardingWindow.Storage.cs"] = 5,
        ["src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"] = 5,
        ["src/DeskBox/Views/OnboardingWindow.xaml.cs"] = 4,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.Appearance.cs"] = 3,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.Detail.cs"] = 2,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.Editing.cs"] = 1,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"] = 1,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.ResponsiveDetail.cs"] = 2,
        ["src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs"] = 12,
        ["src/DeskBox/Views/SearchPopupWindow.xaml.cs"] = 22,
        ["src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs"] = 22,
        ["src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs"] = 24,
        ["src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs"] = 4,
        ["src/DeskBox/Views/SettingsWindow.Maintenance.cs"] = 3,
        ["src/DeskBox/Views/SettingsWindow.Navigation.cs"] = 3,
        ["src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"] = 10,
        ["src/DeskBox/Views/WidgetWindowBase.Bounds.cs"] = 3,
        ["src/DeskBox/Views/WidgetWindowBase.Collapse.cs"] = 35,
    };

    private static readonly Regex FacadePassthroughAccess = new(
        @"(?<![A-Za-z])settings\.(?:" +
        string.Join('|', FacadeProperties.Select(p => p.Name).OrderByDescending(n => n.Length)) +
        @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void FacadePassthroughAccess_OnlyShrinks()
    {
        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            int count = FacadePassthroughAccess.Matches(source).Count;
            if (count == 0)
            {
                continue;
            }

            if (!FacadeAccessManifest.TryGetValue(path, out int budget))
            {
                violations.Add($"  NEW {path}: {count}");
            }
            else if (count > budget)
            {
                violations.Add($"  GREW {path}: {count} (manifest {budget})");
            }
        }

        Assert.True(
            violations.Count == 0,
            "New facade passthrough accesses appeared. New settings code reads " +
            "the slices (Settings.<Slice>.Prop); extend the manifest only " +
            "consciously:\n" + string.Join('\n', violations));
    }

    private static IEnumerable<(string Path, string Source)> ProductionSource()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => (RepositoryRelativePath(path), File.ReadAllText(path)));
    }

    private static string RepositoryRelativePath(string path) =>
        Path.GetRelativePath(TestPaths.FromRepository("."), path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string SerializeSettingValue(object? value, Type type) =>
        JsonSerializer.Serialize(value, type, s_jsonOptions);
}
