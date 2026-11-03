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

    public const string ViewModeStandard = "Standard";
    public const string ViewModeCompact = "Compact";

    public const string TabDisplayIconAndText = "IconAndText";
    public const string TabDisplayIconOnly = "IconOnly";
    public const string TabDisplayTextOnly = "TextOnly";

    public static string NormalizeTabDisplayMode(string? mode)
    {
        if (string.Equals(mode?.Trim(), TabDisplayIconOnly, StringComparison.OrdinalIgnoreCase))
        {
            return TabDisplayIconOnly;
        }

        if (string.Equals(mode?.Trim(), TabDisplayTextOnly, StringComparison.OrdinalIgnoreCase))
        {
            return TabDisplayTextOnly;
        }

        return TabDisplayIconAndText;
    }

    public const string SecretProtectionMask = "Mask";
    public const string SecretProtectionSkip = "Skip";
    public const string SecretProtectionNone = "None";

    public const string PopupFollowCaret = "FollowCaret";
    public const string PopupFollowMouse = "FollowMouse";
    public const string PopupScreenCenter = "ScreenCenter";

    public const string DoubleCopyNone = "None";
    public const string DoubleCopyPin = "Pin";
    public const string DoubleCopyQuickCapture = "SaveToQuickCapture";

    public const int DefaultHotkeyKey = 0x56; // VirtualKey.V (Alt + V)

    public bool ClipboardCaptureText { get; set; } = true;

    public bool ClipboardCaptureImages { get; set; } = true;

    public bool ClipboardCaptureFiles { get; set; } = true;

    public int ClipboardMaxImageSizeMb { get; set; } = 32;

    public int ClipboardMaxTextSizeMb { get; set; } = 4;

    public string ClipboardClickBehavior { get; set; } = ClickCopyOnSinglePasteOnDouble;

    public bool ClipboardDefaultPastePlainText { get; set; }

    public bool ClipboardMoveToTopOnReuse { get; set; }

    public string ClipboardViewMode { get; set; } = ViewModeStandard;

    public string ClipboardTabDisplayMode { get; set; } = TabDisplayIconAndText;

    public string ClipboardCardDensity { get; set; } = DensityStandard;

    public bool ClipboardShowSourceApp { get; set; } = true;

    public bool ClipboardIgnoreSystemPrivacyFormats { get; set; } = true;

    public string ClipboardSensitiveProtectionMode { get; set; } = SecretProtectionMask;

    public List<string> ClipboardExcludedApps { get; set; } = [];

    public int ClipboardMaxItems { get; set; } = 500;

    public int ClipboardRetentionDays { get; set; } = 30;

    public bool ClipboardHotkeyEnabled { get; set; } = true;

    public HotkeyActivationKind ClipboardHotkeyActivationKind { get; set; } = HotkeyActivationKind.Chord;

    public int ClipboardHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.Alt;

    public int ClipboardHotkeyKey { get; set; } = DefaultHotkeyKey;

    public string ClipboardPopupPlacementMode { get; set; } = PopupFollowCaret;

    public string ClipboardDoubleCopyAction { get; set; } = DoubleCopyNone;
}
