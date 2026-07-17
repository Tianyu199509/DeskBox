namespace DeskBox.Models;

/// <summary>
/// A user-defined automatic stack rule. Rules are evaluated in list order and
/// each file is assigned to the first matching rule.
/// </summary>
public sealed class FileStackCustomRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<string> Extensions { get; set; } = [];
}
