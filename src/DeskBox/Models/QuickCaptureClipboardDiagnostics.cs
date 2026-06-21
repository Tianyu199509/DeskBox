namespace DeskBox.Models;

public sealed record QuickCaptureClipboardDiagnostics(
    bool IsRecording,
    bool IsListening,
    DateTimeOffset? LastCapturedAt,
    string LastReason,
    DateTimeOffset? LastReasonAt);
