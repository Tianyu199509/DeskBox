namespace DeskBox.Models;

/// <summary>
/// Settings slice for the first-party Clipboard feature widget and its
/// background capture daemon.
/// </summary>
public sealed class ClipboardSettingsSlice
{
    public const string ClickCopyOnSinglePasteOnDouble = "CopyOnSinglePasteOnDouble";
    public const string ClickPasteOnSingle = "PasteOnSingle";
    public const string ClickCopyOnDouble = "CopyOnDouble";
    public const string ClickCopyOnSingle = "CopyOnSingle";

    public const string DensityCompact = "Compact";
    public const string DensityStandard = "Standard";
    public const string DensityRelaxed = "Relaxed";

    public const string SecretProtectionMask = "Mask";
    public const string SecretProtectionSkip = "Skip";
    public const string SecretProtectionNone = "None";

    public bool ClipboardCaptureText { get; set; } = true;

    public bool ClipboardCaptureImages { get; set; } = true;

    public bool ClipboardCaptureFiles { get; set; } = true;

    public int ClipboardMaxImageSizeMb { get; set; } = 32;

    public int ClipboardMaxTextSizeMb { get; set; } = 4;

    public string ClipboardClickBehavior { get; set; } = ClickCopyOnSinglePasteOnDouble;

    public bool ClipboardDefaultPastePlainText { get; set; }

    public bool ClipboardMoveToTopOnReuse { get; set; } = true;

    public string ClipboardCardDensity { get; set; } = DensityStandard;

    public bool ClipboardShowSourceApp { get; set; } = true;

    public bool ClipboardIgnoreSystemPrivacyFormats { get; set; } = true;

    public string ClipboardSensitiveProtectionMode { get; set; } = SecretProtectionMask;

    public List<string> ClipboardExcludedApps { get; set; } = [];

    public int ClipboardMaxItems { get; set; } = 500;

    public int ClipboardRetentionDays { get; set; } = 30;
}
