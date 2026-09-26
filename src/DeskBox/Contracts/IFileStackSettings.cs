using DeskBox.Models;

namespace DeskBox.Contracts;

public readonly record struct FileStackSettingsSnapshot(
    bool FileStacksEnabled,
    bool FileStackAutoStacking,
    string FileStackGroupBy,
    int FileStackThreshold,
    string FileStackOrderBy,
    string FileStackOpenMode,
    string FileStackPopoverLayout,
    string FileStackPopoverStyle,
    string FileStackUnmatchedBehavior,
    IReadOnlyList<FileStackCustomRule> FileStackCustomRules);

/// <summary>
/// Settings-page writes for the file-stack section: the stack master switch,
/// auto-stacking, the grouping mode (including the custom-rule collection that
/// only applies in Custom mode), the auto-stack threshold, stack ordering, the
/// open mode, popover layout and style, and the unmatched-file behavior. The
/// settings shell keeps every XAML/AOT binding plus the restoring-defaults and
/// snapshot-application guards that decide whether a callback writes at all,
/// and maps its rule editors to <see cref="FileStackCustomRule"/> models with
/// the existing extension-list normalization; this port owns the persisted
/// values with their original semantics: normalize (options and threshold),
/// store and schedule one debounced save. The stack projection rebuild that
/// follows a stack-toggle or rule change stays outside this port — it is a
/// host-side consumer of the regular SettingsChanged broadcast
/// (WidgetViewModel's stack-display rebuild queue), exactly as before.
/// </summary>
public interface IFileStackSettings
{
    FileStackSettingsSnapshot ReadAll();

    void SetFileStacksEnabled(bool value);

    void SetFileStackAutoStacking(bool value);

    void SetFileStackGroupBy(string? value);

    void SetFileStackThreshold(int value);

    void SetFileStackOrderBy(string? value);

    void SetFileStackOpenMode(string? value);

    void SetFileStackPopoverLayout(string? value);

    void SetFileStackPopoverStyle(string? value);

    void SetFileStackUnmatchedBehavior(string? value);

    void SetFileStackCustomRules(IReadOnlyList<FileStackCustomRule>? rules);
}
