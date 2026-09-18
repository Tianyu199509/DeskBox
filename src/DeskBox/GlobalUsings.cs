// DeskBox.Platform hosts every P/Invoke / COM-interop declaration
// (module-boundary law: PlatformInterop_StaysInsideThePlatformDomain).
// Imported globally so the moved legacy wrappers — Win32Helper above all —
// keep resolving at their existing call sites without per-file using churn.
global using DeskBox.Platform;
