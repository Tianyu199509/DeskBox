using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.FileStack;

/// <summary>
/// File-stack section editor. The legacy settings shell keeps every
/// XAML/AOT binding, the restoring-defaults and snapshot-application
/// guards, the rule-editor collection (add/remove/reorder and per-rule
/// edits map to <see cref="FileStackCustomRule"/> models with the
/// existing extension-list normalization) and forwards its persisted
/// writes here; this editor is the feature seam over
/// <see cref="IFileStackSettings"/> and owns no duplicated state. All
/// persistence rules live in the coordinator.
/// </summary>
public sealed class FileStackSettingsViewModel
{
    private readonly IFileStackSettings _settings;

    public FileStackSettingsViewModel(IFileStackSettings settings)
    {
        _settings = settings;
    }

    public FileStackSettingsSnapshot ReadAll() => _settings.ReadAll();

    public void SetFileStacksEnabled(bool value) =>
        _settings.SetFileStacksEnabled(value);

    public void SetFileStackAutoStacking(bool value) =>
        _settings.SetFileStackAutoStacking(value);

    public void SetFileStackGroupBy(string? value) =>
        _settings.SetFileStackGroupBy(value);

    public void SetFileStackThreshold(int value) =>
        _settings.SetFileStackThreshold(value);

    public void SetFileStackOrderBy(string? value) =>
        _settings.SetFileStackOrderBy(value);

    public void SetFileStackOpenMode(string? value) =>
        _settings.SetFileStackOpenMode(value);

    public void SetFileStackPopoverLayout(string? value) =>
        _settings.SetFileStackPopoverLayout(value);

    public void SetFileStackPopoverStyle(string? value) =>
        _settings.SetFileStackPopoverStyle(value);

    public void SetFileStackUnmatchedBehavior(string? value) =>
        _settings.SetFileStackUnmatchedBehavior(value);

    public void SetFileStackCustomRules(IReadOnlyList<FileStackCustomRule>? rules) =>
        _settings.SetFileStackCustomRules(rules);
}
