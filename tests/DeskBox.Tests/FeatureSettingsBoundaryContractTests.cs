using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Tests;

/// <summary>Boundary laws that ship with the feature-runtime/settings segment.</summary>
public sealed class FeatureSettingsBoundaryContractTests
{
    [Fact]
    public void FeatureBusinessCode_DoesNotResolveGlobalApplicationServices()
    {
        Type[] types = typeof(DeskBox.App).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("DeskBox.Features.", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.TodoSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.SearchSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupRestoreActions", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.QuickCaptureSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupBackend", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.ShutdownSequence", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Views.SettingsSections.SearchSettingsSection", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(types);

        var violations = new List<string>();
        foreach (Type type in types)
        {
            bool searchView = type.FullName?.StartsWith(
                "DeskBox.Views.SettingsSections.SearchSettingsSection", StringComparison.Ordinal) == true;
            foreach (Type reference in ReferencedTypes(type).SelectMany(ExpandType).Distinct())
            {
                string name = reference.FullName ?? string.Empty;
                bool globalAccess = name is "DeskBox.App" or "System.IServiceProvider" or
                    "CommunityToolkit.Mvvm.DependencyInjection.Ioc" ||
                    (name == "Microsoft.UI.Xaml.Application" && !searchView) ||
                    name.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal);
                bool featureUsesAdapter = type.Namespace?.StartsWith(
                    "DeskBox.Features.", StringComparison.Ordinal) == true &&
                    (name.StartsWith("DeskBox.Services.", StringComparison.Ordinal) ||
                     name.StartsWith("DeskBox.Platform.", StringComparison.Ordinal) ||
                     name == "DeskBox.Models.AppSettings");
                bool searchViewUsesRuntime = searchView &&
                    name is "DeskBox.Services.SettingsService" or
                        "DeskBox.Services.SearchHotkeyService" or
                        "DeskBox.Services.EverythingSearchService";
                if (globalAccess || featureUsesAdapter || searchViewUsesRuntime)
                    violations.Add($"{type.FullName} -> {name}");
            }
        }

        Assert.True(violations.Count == 0,
            "Feature code must use explicit contracts:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly()
    {
        string todoFields = string.Join("|", typeof(TodoSettingsSlice)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => Regex.Escape(property.Name)));
        Regex todoWrite = new(
            $@"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:Todo\s*\.\s*)?(?:{todoFields})\s*=(?!=)");
        Regex quickCaptureWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?(?:QuickCapture(?:DefaultView|ShowTabBar|ShowRecordsTab|ShowPinnedTab|ShowRecentTab|TabStyle|ShowCreatedTime|ItemPreviewLineCount|RecentLimit|EditorEnterBehavior|DefaultFormat|WideLayout|WideOpenMode|AllowRemoteImages)|LastQuickCaptureFileWidgetId)\s*=(?!=)");
        // Batch 29: the appearance section (material/density/typography/window
        // chrome/animation/foreground/tray icon/default size) writes through
        // AppearanceSettingsCoordinator; the settings shell must not regain
        // direct assignment sites. WidgetLayerMode moved to the interaction
        // gate below with batch 34.
        Regex appearanceWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:WidgetShell\s*\.\s*)?(?:WidgetOpacity|WidgetMaterialIntensity|IconSize|TextSize|LayoutDensityScale|HorizontalSpacingScale|VerticalSpacingScale|FileNameWidthScale|FileNameLineCount|TrayIconStyle|WidgetMaterialType|WidgetCornerPreference|WidgetBorderColorMode|WidgetBorderStyle|LayoutDensity|WidgetAnimationEffect|WidgetAnimationSpeed|WidgetAnimationSlideDirection|WidgetAnimationEasingIntensity|DisplayWidgetChromeMode|InteractiveWidgetChromeMode|WidgetTitleIconMode|DefaultWidgetWidth|DefaultWidgetHeight|WidgetForegroundMode|WidgetForegroundColor)\s*=(?!=)");
        // Batch 33: the capsule/compact-mode section (collapse behavior,
        // compact content mode, sensitive-content hiding, capsule geometry and
        // bar arrangement, compact animation plus duration, hover delays,
        // compact media corner) writes through CapsuleSettingsCoordinator.
        Regex capsuleWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:WidgetShell\s*\.\s*)?(?:WidgetCollapseBehavior|WidgetCompactContentMode|WidgetCompactHideSensitiveContent|WidgetCompactWidthMode|WidgetCompactExpansionDirection|WidgetCapsuleArrangementMode|WidgetCapsuleBarPlacement|WidgetCapsuleBarDirection|WidgetCapsuleBarSpacing|WidgetCompactAnimationEffect|WidgetCompactAnimationDurationMs|WidgetCompactExpandDelayMs|WidgetCompactCollapseDelayMs|WidgetCompactMediaCornerMode)\s*=(?!=)");
        // Batch 34: the interaction section (autostart reflection, update
        // auto-check, open method, file-item context menu, resize snap plus
        // spacing, show-desktop visibility, widget layer mode, hover buttons
        // plus the selected action set, idle/hidden working-set trims) writes
        // through InteractionSettingsCoordinator.
        Regex interactionWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:WidgetShell\s*\.\s*)?(?:AutoStart|AutoCheckForUpdates|DoubleClickToOpen|FileItemSystemContextMenuEnabled|ResizeSnapEnabled|WidgetSnapSpacing|KeepWidgetsVisibleOnShowDesktop|ShowHoverButtons|WidgetHoverButtonActions|WidgetLayerMode|IdleWorkingSetTrimEnabled|ImmediateHiddenWorkingSetTrimEnabled)\s*=(?!=)");
        // Batch 35: the file-display section (file-name extension visibility
        // plus the shortcut-extension hiding rule, shortcut link-arrow
        // overlay, image-file icon projection, list-item detail lines,
        // file-item path tooltips) writes through
        // FileDisplaySettingsCoordinator.
        Regex fileDisplayWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:FileWidget\s*\.\s*)?(?:ShowFileExtensions|HideShortcutExtensionWhenShowingFileExtensions|HideShortcutArrowOverlay|ShowImageFilesAsIcons|ShowListItemDetails|ShowFileItemPathTooltips)\s*=(?!=)");
        // Batch 36: the file-stack section (stack master switch, auto-stacking,
        // grouping mode plus the custom-rule collection, auto-stack threshold,
        // stack ordering, open mode, popover layout/style and the unmatched-file
        // behavior) writes through FileStackSettingsCoordinator.
        Regex fileStackWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:FileWidget\s*\.\s*)?(?:FileStacksEnabled|FileStackAutoStacking|FileStackGroupBy|FileStackThreshold|FileStackOrderBy|FileStackOpenMode|FileStackPopoverLayout|FileStackPopoverStyle|FileStackCustomRules|FileStackUnmatchedBehavior)\s*=(?!=)");
        // Batch 37: the group-navigation defaults (wheel switch, hover switch,
        // default title display mode, default navigation style) write through
        // GroupNavigationSettingsCoordinator.
        Regex groupNavigationWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:WidgetLayout\s*\.\s*)?(?:WidgetGroupWheelSwitchEnabled|WidgetGroupHoverSwitchEnabled|WidgetGroupDefaultTitleDisplayMode|WidgetGroupDefaultNavigationStyle)\s*=(?!=)");
        // Batch 38: the feature-section writes — music presentation, the
        // weather options (data source, location, units, default view, skin,
        // display toggles, refresh interval; the policy path used to write
        // through WeatherSettingsPolicy from the shell), the feature-card
        // reset defaults and the section's misc presentation picks
        // (attachment storage, managed-drop action, folder-open behavior) —
        // go through FeatureWidgetsSettingsCoordinator. The Quick Capture
        // editor group (enter behavior, format, wide layout, wide-open mode,
        // remote images, last file widget id) writes through the existing
        // QuickCaptureSettingsCoordinator and is covered by the extended
        // quickCaptureWrite gate above.
        Regex featureSectionWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:Music\s*\.\s*|Weather\s*\.\s*|QuickCapture\s*\.\s*|FileWidget\s*\.\s*)?(?:MusicUseArtworkBackdrop|MusicEnableCoverHoverMotion|MusicDisplayMode|WeatherDataSource|WeatherAutoLocation|WeatherCityName|WeatherLatitude|WeatherLongitude|WeatherTemperatureUnit|WeatherWindSpeedUnit|WeatherDefaultView|WeatherSkin|WeatherShowForecast|WeatherShowSunrise|WeatherShowUvIndex|WeatherShowPrecipitation|WeatherShowHumidity|WeatherShowWind|WeatherShowPressure|WeatherRefreshIntervalMinutes|AttachmentStorageMode|ManagedDropAction|FileWidgetFolderOpenBehavior)\s*=(?!=)");
        // Batch 39: the managed-storage root-path commit — the write that
        // follows the host's WidgetManager storage migration chain — and the
        // maintenance-domain update-check timestamp stamp write through
        // ManagedStorageSettingsCoordinator and
        // MaintenanceSettingsCoordinator respectively.
        Regex managedStorageWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:FileWidget\s*\.\s*)?DefaultManagedStorageRootPath\s*=(?!=)");
        Regex maintenanceWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:Core\s*\.\s*)?LastUpdateCheckAt\s*=(?!=)");
        (string Path, string Source)[] pages = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .ToArray();
        string[] violations = pages.SelectMany(item =>
                todoWrite.Matches(item.Source).Cast<Match>()
                    .Concat(quickCaptureWrite.Matches(item.Source).Cast<Match>())
                    .Concat(appearanceWrite.Matches(item.Source).Cast<Match>())
                    .Concat(capsuleWrite.Matches(item.Source).Cast<Match>())
                    .Concat(interactionWrite.Matches(item.Source).Cast<Match>())
                    .Concat(fileDisplayWrite.Matches(item.Source).Cast<Match>())
                    .Concat(fileStackWrite.Matches(item.Source).Cast<Match>())
                    .Concat(groupNavigationWrite.Matches(item.Source).Cast<Match>())
                    .Concat(featureSectionWrite.Matches(item.Source).Cast<Match>())
                    .Concat(managedStorageWrite.Matches(item.Source).Cast<Match>())
                    .Concat(maintenanceWrite.Matches(item.Source).Cast<Match>())
                    .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
        Assert.DoesNotContain("App.Current.QuickCaptureService.TrimRecentItemsAsync",
            string.Join('\n', pages.Select(item => item.Source)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupSettingsPages_DoNotResolveGlobalApp()
    {
        string[] paths =
        [
            "src/DeskBox/ViewModels/SettingsViewModel.DataBackupOptions.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.CloudBackupOptions.cs",
            "src/DeskBox/Views/SettingsWindow.CloudBackup.cs",
            "src/DeskBox/Features/Backup/BackupSettingsViewModel.cs",
            "src/DeskBox/Services/BackupSettingsCoordinator.cs",
            "src/DeskBox/Services/BackupRestoreActions.cs"
        ];
        foreach (string path in paths)
        {
            string source = ProductionSource().Single(item => item.Path == path).Source;
            Assert.DoesNotContain("App.Current", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (Type element in ExpandType(type.GetElementType()!)) yield return element;
        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments().SelectMany(ExpandType)) yield return argument;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (type.BaseType is { } parent) yield return parent;
        foreach (Type contract in type.GetInterfaces()) yield return contract;
        foreach (FieldInfo field in type.GetFields(flags)) yield return field.FieldType;
        IEnumerable<MethodBase> methods = type.GetMethods(flags).Cast<MethodBase>()
            .Concat(type.GetConstructors(flags));
        foreach (MethodBase method in methods)
        {
            if (method is MethodInfo methodInfo) yield return methodInfo.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters()) yield return parameter.ParameterType;
            MethodBody? body = method.GetMethodBody();
            if (body is null) continue;
            foreach (LocalVariableInfo local in body.LocalVariables) yield return local.LocalType;
            byte[] il = body.GetILAsByteArray()!;
            for (int offset = 0; offset < il.Length;)
            {
                short code = il[offset++];
                if (code == 0xfe) code = unchecked((short)(0xfe00 | il[offset++]));
                OpCode op = IlOpCodes[code];
                if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or
                    OperandType.InlineType or OperandType.InlineTok)
                {
                    MemberInfo? member = method.Module.ResolveMember(
                        BitConverter.ToInt32(il, offset),
                        type.IsGenericType ? type.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (member is Type target) yield return target;
                    else if (member?.DeclaringType is { } declaring) yield return declaring;
                    if (member is MethodInfo called && called.IsGenericMethod)
                        foreach (Type argument in called.GetGenericArguments()) yield return argument;
                }
                offset += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                    _ => 4
                };
            }
        }
    }

    private static readonly IReadOnlyDictionary<short, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

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
            .Select(path => (Path.GetRelativePath(TestPaths.FromRepository("."), path)
                .Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path)));
    }
}
