using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the file-stack section fields: the stack
/// master switch, auto-stacking, the grouping mode, the auto-stack threshold,
/// stack ordering, the open mode, popover layout and style, the unmatched-file
/// behavior and the custom-rule collection. The section has no live-preview
/// coupling with the appearance machinery: every edit keeps the original
/// semantics of normalize (options and threshold), store and schedule one
/// debounced save, so stack-toggle, grouping and rule changes still reach the
/// stack projection rebuild only through the regular SettingsChanged consumer
/// (WidgetViewModel's queued stack-display rebuild), and the shell keeps its
/// restoring-defaults and snapshot-application guards that decide whether a
/// callback writes at all. Unchanged writes skip the redundant debounced save,
/// matching the callback-driven flow where the observable properties only fire
/// on real changes; the custom-rule replacement compares the projected models
/// (id, trimmed name, normalized extension list) so re-committing the same
/// rule set — e.g. a drag that lands back in the original order — does not
/// trigger a save or a projection rebuild.
/// </summary>
public sealed class FileStackSettingsCoordinator : IFileStackSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public FileStackSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public FileStackSettingsSnapshot ReadAll()
    {
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        return new(
            fileWidget.FileStacksEnabled,
            fileWidget.FileStackAutoStacking,
            fileWidget.FileStackGroupBy,
            fileWidget.FileStackThreshold,
            fileWidget.FileStackOrderBy,
            fileWidget.FileStackOpenMode,
            fileWidget.FileStackPopoverLayout,
            fileWidget.FileStackPopoverStyle,
            fileWidget.FileStackUnmatchedBehavior,
            fileWidget.FileStackCustomRules);
    }

    public void SetFileStacksEnabled(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.FileStacksEnabled == value) return;
        fileWidget.FileStacksEnabled = value;
        _settings.SaveDebounced();
    }

    public void SetFileStackAutoStacking(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.FileStackAutoStacking == value) return;
        fileWidget.FileStackAutoStacking = value;
        _settings.SaveDebounced();
    }

    public void SetFileStackGroupBy(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackGroupBy(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackGroupBy, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackGroupBy = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackThreshold(int value)
    {
        ThrowIfStopped();
        int normalized = SettingsService.NormalizeFileStackThreshold(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.FileStackThreshold == normalized) return;
        fileWidget.FileStackThreshold = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackOrderBy(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackOrderBy(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackOrderBy, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackOrderBy = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackOpenMode(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackOpenMode(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackOpenMode, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackOpenMode = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackPopoverLayout(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackPopoverLayout(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackPopoverLayout, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackPopoverLayout = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackPopoverStyle(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackPopoverStyle(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackPopoverStyle, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackPopoverStyle = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackUnmatchedBehavior(string? value)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeFileStackUnmatchedBehavior(value);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(fileWidget.FileStackUnmatchedBehavior, normalized, StringComparison.Ordinal)) return;
        fileWidget.FileStackUnmatchedBehavior = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileStackCustomRules(IReadOnlyList<FileStackCustomRule>? rules)
    {
        ThrowIfStopped();
        // Store the same projection the rule editors produce on commit
        // (trimmed names, normalized and capped extension lists); the load
        // pipeline applies the identical normalization, so the persisted
        // invariant does not depend on who calls the port.
        List<FileStackCustomRule> replacement = [];
        foreach (FileStackCustomRule rule in rules ?? [])
        {
            replacement.Add(new FileStackCustomRule
            {
                Id = rule.Id,
                Name = (rule.Name ?? string.Empty).Trim(),
                Extensions = SettingsService.NormalizeFileStackExtensions(rule.Extensions)
                    .Take(SettingsService.MaxFileStackExtensionsPerRule)
                    .ToList()
            });
        }

        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (CustomRulesEqual(fileWidget.FileStackCustomRules, replacement)) return;
        fileWidget.FileStackCustomRules = replacement;
        _settings.SaveDebounced();
    }

    // Same projection the settings shell uses when deciding whether a snapshot
    // changed its rule editors: ids and trimmed names compare ordinally, the
    // extension lists compare as sequences; both sides are already normalized
    // (the write entry normalizes replacements, the load pipeline the store).
    private static bool CustomRulesEqual(
        IReadOnlyList<FileStackCustomRule> current,
        IReadOnlyList<FileStackCustomRule> replacement)
    {
        if (current.Count != replacement.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            FileStackCustomRule currentRule = current[index];
            FileStackCustomRule replacementRule = replacement[index];
            if (!string.Equals(currentRule.Id, replacementRule.Id, StringComparison.Ordinal) ||
                !string.Equals(currentRule.Name?.Trim(), replacementRule.Name, StringComparison.Ordinal) ||
                !currentRule.Extensions.SequenceEqual(
                    replacementRule.Extensions,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
