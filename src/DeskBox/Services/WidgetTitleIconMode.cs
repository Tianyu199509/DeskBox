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
    public const string FilledMono = Contracts.WidgetTitleIconKinds.FilledMono;
    public const string LineMono = Contracts.WidgetTitleIconKinds.LineMono;
    public const string Color = Contracts.WidgetTitleIconKinds.Color;
    public const string Hidden = Contracts.WidgetTitleIconKinds.Hidden;
    public const string TextLabel = Contracts.WidgetTitleIconKinds.TextLabel;

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
    Glance,
    Tags,
    Search,
    SystemMonitor,
    Pomodoro,
    Countdown,
    AiMonitor,
    Custom,
    Clipboard
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
    public const string Glance = nameof(WidgetTitleIconKind.Glance);
    public const string Tags = nameof(WidgetTitleIconKind.Tags);
    public const string Search = nameof(WidgetTitleIconKind.Search);
    public const string SystemMonitor = nameof(WidgetTitleIconKind.SystemMonitor);
    public const string Pomodoro = nameof(WidgetTitleIconKind.Pomodoro);
    public const string Countdown = nameof(WidgetTitleIconKind.Countdown);
    public const string AiMonitor = nameof(WidgetTitleIconKind.AiMonitor);
    public const string Custom = nameof(WidgetTitleIconKind.Custom);
    public const string Clipboard = nameof(WidgetTitleIconKind.Clipboard);

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
            WidgetTitleIconKind.Glance => Glance,
            WidgetTitleIconKind.Tags => Tags,
            WidgetTitleIconKind.Search => Search,
            WidgetTitleIconKind.SystemMonitor => SystemMonitor,
            WidgetTitleIconKind.Pomodoro => Pomodoro,
            WidgetTitleIconKind.Countdown => Countdown,
            WidgetTitleIconKind.AiMonitor => AiMonitor,
            WidgetTitleIconKind.Custom => Custom,
            WidgetTitleIconKind.Clipboard => Clipboard,
            _ => Default
        };
    }

    public static string FromFileWidget(bool followsDefaultStoragePath)
    {
        return followsDefaultStoragePath ? ManagedStorage : MappedFolder;
    }

    public static string? FromCustomTemplateId(string? templateOrManifestId)
    {
        if (string.IsNullOrWhiteSpace(templateOrManifestId))
        {
            return null;
        }

        string trimmed = templateOrManifestId.Trim();
        string normalized = trimmed.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? trimmed["deskbox.".Length..]
            : trimmed;

        return normalized.ToLowerInvariant() switch
        {
            CustomWidgetTemplates.SystemPulseTemplateId => SystemMonitor,
            CustomWidgetTemplates.PomodoroTemplateId => Pomodoro,
            CustomWidgetTemplates.CountdownTemplateId => Countdown,
            CustomWidgetTemplates.AiPulseTemplateId => AiMonitor,
            _ => null
        };
    }

    public static string FromWidgetConfig(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.WidgetKind == WidgetKind.File)
        {
            return FromFileWidget(config.FollowsDefaultStoragePath);
        }

        if (config.WidgetKind == WidgetKind.Custom &&
            config.Metadata is not null &&
            config.Metadata.TryGetValue("CustomTemplateId", out string? templateId) &&
            FromCustomTemplateId(templateId) is { } presetIconKind)
        {
            return presetIconKind;
        }

        return FromWidgetKind(config.WidgetKind);
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
            WidgetKind.Glance => Glance,
            WidgetKind.Tags => Tags,
            WidgetKind.Search => Search,
            WidgetKind.SystemMonitor => SystemMonitor,
            WidgetKind.Custom => Custom,
            WidgetKind.Clipboard => Clipboard,
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
            "\uE916" => Pomodoro,
            "\uE787" => Countdown,
            "\uE950" => AiMonitor,
            "\uE77F" => Clipboard,
            "\uF0E3" => Clipboard,
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
            WidgetTitleIconKind.Glance => "Glance.Title",
            WidgetTitleIconKind.Tags => "WidgetTitleIcon.Label.Tags",
            WidgetTitleIconKind.Search => "WidgetTitleIcon.Label.Search",
            WidgetTitleIconKind.SystemMonitor => "WidgetTitleIcon.Label.SystemMonitor",
            WidgetTitleIconKind.Pomodoro => "CustomWidget.Template.Pomodoro.Name",
            WidgetTitleIconKind.Countdown => "CustomWidget.Template.Countdown.Name",
            WidgetTitleIconKind.AiMonitor => "CustomWidget.Template.AiPulse.Name",
            WidgetTitleIconKind.Custom => "CustomWidget.Title",
            WidgetTitleIconKind.Clipboard => "Clipboard.Title",
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
            WidgetTitleIconKind.Glance => "glance",
            WidgetTitleIconKind.Tags => "tags",
            WidgetTitleIconKind.Search => "search",
            WidgetTitleIconKind.SystemMonitor => "system-monitor",
            WidgetTitleIconKind.Pomodoro => "pomodoro",
            WidgetTitleIconKind.Countdown => "countdown",
            WidgetTitleIconKind.AiMonitor => "ai-monitor",
            WidgetTitleIconKind.Custom => "custom",
            WidgetTitleIconKind.Clipboard => "clipboard",
            _ => "default"
        };
    }
}
