namespace DeskBox.Models;

/// <summary>
/// Quick Capture widget preferences, the shared attachment storage default, and the last-target file widget.
/// </summary>
public sealed class QuickCaptureSettingsSlice
{
    /// <summary>Whether the built-in Quick Capture widget is enabled.</summary>
    public bool QuickCaptureEnabled { get; set; }

    /// <summary>Whether Quick Capture should record recent clipboard text and links.</summary>
    public bool QuickCaptureClipboardEnabled { get; set; }

    /// <summary>Whether Quick Capture should record clipboard images.</summary>
    public bool QuickCaptureImageClipboardEnabled { get; set; }

    /// <summary>Maximum number of recent clipboard text/link entries kept by Quick Capture.</summary>
    public int QuickCaptureRecentLimit { get; set; } = 30;

    /// <summary>Whether Quick Capture cards show their creation time.</summary>
    public bool QuickCaptureShowCreatedTime { get; set; } = true;

    /// <summary>Maximum number of text lines shown for each Quick Capture item in the list.</summary>
    public int QuickCaptureItemPreviewLineCount { get; set; } = 3;

    /// <summary>Text size for Quick Capture list cards. Zero keeps the global appearance size for legacy settings.</summary>
    public double QuickCaptureListTextSize { get; set; }

    /// <summary>Text size for Quick Capture detail content. Zero keeps the global appearance size for legacy settings.</summary>
    public double QuickCaptureContentTextSize { get; set; }

    /// <summary>Enter-key behavior used by Quick Capture multiline editors.</summary>
    public string QuickCaptureEditorEnterBehavior { get; set; } = "CtrlEnterSaves";

    /// <summary>
    /// Preferred Quick Capture editor format. The property name is retained for compatibility with
    /// existing settings files. Valid values: <c>Markdown</c>, <c>PlainText</c>.
    /// </summary>
    public string QuickCaptureDefaultFormat { get; set; } = "Markdown";

    /// <summary>Responsive layout preference. Valid values: <c>Auto</c>, <c>SinglePane</c>, <c>DualPane</c>.</summary>
    public string QuickCaptureWideLayout { get; set; } = "Auto";

    /// <summary>Initial mode for saved notes in a wide detail pane. Valid values: <c>Reading</c>, <c>Editing</c>.</summary>
    public string QuickCaptureWideOpenMode { get; set; } = "Reading";

    /// <summary>Whether remote HTTP(S) images may be loaded by the Markdown reader.</summary>
    public bool QuickCaptureAllowRemoteImages { get; set; }

    /// <summary>Default storage behavior for Todo and Quick Capture attachments. Valid values: <c>Link</c>, <c>Copy</c>.</summary>
    public string AttachmentStorageMode { get; set; } = "Link";

    /// <summary>Default Quick Capture view used when the widget opens. Valid values: <c>"Records"</c>, <c>"Pinned"</c>, <c>"Recent"</c>.</summary>
    public string QuickCaptureDefaultView { get; set; } = "Records";

    /// <summary>Quick Capture tab style. Valid values: <c>"Pivot"</c>, <c>"Button"</c>.</summary>
    public string QuickCaptureTabStyle { get; set; } = "Button";

    public bool QuickCaptureShowTabBar { get; set; } = true;

    public bool QuickCaptureShowRecordsTab { get; set; } = true;

    public bool QuickCaptureShowPinnedTab { get; set; } = true;

    public bool QuickCaptureShowRecentTab { get; set; } = true;

    /// <summary>Last file widget used as the target for saving Quick Capture content.</summary>
    public string LastQuickCaptureFileWidgetId { get; set; } = string.Empty;
}
