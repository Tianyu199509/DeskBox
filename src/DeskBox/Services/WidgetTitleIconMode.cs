namespace DeskBox.Services;

using DeskBox.Models;

public enum WidgetTitleIconMode
{
    FilledMono,
    LineMono,
    Color,
    Hidden,
    TextLabel
}

public static class WidgetTitleIconModeNames
{
    public const string FilledMono = nameof(WidgetTitleIconMode.FilledMono);
    public const string LineMono = nameof(WidgetTitleIconMode.LineMono);
    public const string Color = nameof(WidgetTitleIconMode.Color);
    public const string Hidden = nameof(WidgetTitleIconMode.Hidden);
    public const string TextLabel = nameof(WidgetTitleIconMode.TextLabel);

    public static WidgetTitleIconMode NormalizeMode(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WidgetTitleIconMode mode)
            ? mode
            : WidgetTitleIconMode.Color;
    }

    public static string NormalizeSettingValue(string? value)
    {
        return ToSettingValue(NormalizeMode(value));
    }

    public static string ToSettingValue(WidgetTitleIconMode mode)
    {
        return mode switch
        {
            WidgetTitleIconMode.FilledMono => FilledMono,
            WidgetTitleIconMode.LineMono => LineMono,
            WidgetTitleIconMode.Color => Color,
            WidgetTitleIconMode.Hidden => Hidden,
            WidgetTitleIconMode.TextLabel => TextLabel,
            _ => Color
        };
    }
}

public enum WidgetTitleIconKind
{
    Default,
    ManagedStorage,
    MappedFolder,
    QuickCapture,
    Todo,
    Music,
    Weather,
    Tags,
    Search,
    SystemMonitor
}

public static class WidgetTitleIconKindNames
{
    public const string Default = nameof(WidgetTitleIconKind.Default);
    public const string ManagedStorage = nameof(WidgetTitleIconKind.ManagedStorage);
    public const string MappedFolder = nameof(WidgetTitleIconKind.MappedFolder);
    public const string QuickCapture = nameof(WidgetTitleIconKind.QuickCapture);
    public const string Todo = nameof(WidgetTitleIconKind.Todo);
    public const string Music = nameof(WidgetTitleIconKind.Music);
    public const string Weather = nameof(WidgetTitleIconKind.Weather);
    public const string Tags = nameof(WidgetTitleIconKind.Tags);
    public const string Search = nameof(WidgetTitleIconKind.Search);
    public const string SystemMonitor = nameof(WidgetTitleIconKind.SystemMonitor);

    public static WidgetTitleIconKind NormalizeKind(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WidgetTitleIconKind kind)
            ? kind
            : WidgetTitleIconKind.Default;
    }

    public static string ToSettingValue(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => ManagedStorage,
            WidgetTitleIconKind.MappedFolder => MappedFolder,
            WidgetTitleIconKind.QuickCapture => QuickCapture,
            WidgetTitleIconKind.Todo => Todo,
            WidgetTitleIconKind.Music => Music,
            WidgetTitleIconKind.Weather => Weather,
            WidgetTitleIconKind.Tags => Tags,
            WidgetTitleIconKind.Search => Search,
            WidgetTitleIconKind.SystemMonitor => SystemMonitor,
            _ => Default
        };
    }

    public static string FromFileWidget(bool followsDefaultStoragePath)
    {
        return followsDefaultStoragePath ? ManagedStorage : MappedFolder;
    }

    public static string FromWidgetKind(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.File => ManagedStorage,
            WidgetKind.QuickCapture => QuickCapture,
            WidgetKind.Todo => Todo,
            WidgetKind.Music => Music,
            WidgetKind.Weather => Weather,
            WidgetKind.Tags => Tags,
            WidgetKind.Search => Search,
            WidgetKind.SystemMonitor => SystemMonitor,
            _ => Default
        };
    }

    public static string FromLegacyGlyph(string? glyph)
    {
        return glyph switch
        {
            "\uE8B7" => ManagedStorage,
            "\uE71B" => MappedFolder,
            "\uE70F" => QuickCapture,
            "\uE9D5" => Todo,
            "\uEC4F" => Music,
            "\uE706" => Weather,
            "\uE8EC" => Tags,
            "\uE721" => Search,
            "\uE9D9" => SystemMonitor,
            _ => Default
        };
    }

    public static string GetLocalizationKey(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => "WidgetTitleIcon.Label.ManagedStorage",
            WidgetTitleIconKind.MappedFolder => "WidgetTitleIcon.Label.MappedFolder",
            WidgetTitleIconKind.QuickCapture => "WidgetTitleIcon.Label.QuickCapture",
            WidgetTitleIconKind.Todo => "WidgetTitleIcon.Label.Todo",
            WidgetTitleIconKind.Music => "WidgetTitleIcon.Label.Music",
            WidgetTitleIconKind.Weather => "WidgetTitleIcon.Label.Weather",
            WidgetTitleIconKind.Tags => "WidgetTitleIcon.Label.Tags",
            WidgetTitleIconKind.Search => "WidgetTitleIcon.Label.Search",
            WidgetTitleIconKind.SystemMonitor => "WidgetTitleIcon.Label.SystemMonitor",
            _ => "WidgetTitleIcon.Label.Default"
        };
    }

    public static string GetColorAssetName(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => "managed-storage",
            WidgetTitleIconKind.MappedFolder => "mapped-folder",
            WidgetTitleIconKind.QuickCapture => "quick-capture",
            WidgetTitleIconKind.Todo => "todo",
            WidgetTitleIconKind.Music => "music",
            WidgetTitleIconKind.Weather => "weather",
            WidgetTitleIconKind.Tags => "tags",
            WidgetTitleIconKind.Search => "search",
            WidgetTitleIconKind.SystemMonitor => "system-monitor",
            _ => "default"
        };
    }
}
