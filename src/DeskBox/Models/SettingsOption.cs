namespace DeskBox.Models;

public sealed class SettingsOption
{
    public SettingsOption(object value, string displayName)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        DisplayName = displayName ?? string.Empty;
    }

    public object Value { get; }

    public string DisplayName { get; }
}
