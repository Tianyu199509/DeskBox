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
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?QuickCapture(?:DefaultView|ShowTabBar|ShowRecordsTab|ShowPinnedTab|ShowRecentTab|TabStyle|ShowCreatedTime|ItemPreviewLineCount|RecentLimit)\s*=(?!=)");
        // Batch 29: the appearance section (material/density/typography/window
        // chrome/animation/foreground/tray icon/default size) writes through
        // AppearanceSettingsCoordinator; the settings shell must not regain
        // direct assignment sites. WidgetLayerMode/collapse/compact fields
        // stay out until their capsule/interaction batches.
        Regex appearanceWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:WidgetShell\s*\.\s*)?(?:WidgetOpacity|WidgetMaterialIntensity|IconSize|TextSize|LayoutDensityScale|HorizontalSpacingScale|VerticalSpacingScale|FileNameWidthScale|FileNameLineCount|TrayIconStyle|WidgetMaterialType|WidgetCornerPreference|WidgetBorderColorMode|WidgetBorderStyle|LayoutDensity|WidgetAnimationEffect|WidgetAnimationSpeed|WidgetAnimationSlideDirection|WidgetAnimationEasingIntensity|DisplayWidgetChromeMode|InteractiveWidgetChromeMode|WidgetTitleIconMode|DefaultWidgetWidth|DefaultWidgetHeight|WidgetForegroundMode|WidgetForegroundColor)\s*=(?!=)");
        (string Path, string Source)[] pages = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .ToArray();
        string[] violations = pages.SelectMany(item =>
                todoWrite.Matches(item.Source).Cast<Match>()
                    .Concat(quickCaptureWrite.Matches(item.Source).Cast<Match>())
                    .Concat(appearanceWrite.Matches(item.Source).Cast<Match>())
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
