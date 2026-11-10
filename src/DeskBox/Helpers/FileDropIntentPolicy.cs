using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Helpers;

internal enum FileDropIntent
{
    None,
    Copy,
    Move,
    Shortcut,
    Reference,
    Reorder,
    Organize
}

/// <summary>
/// Resolves the semantic intent of a file drop before individual XAML and OLE
/// adapters translate it into their platform-specific operation flags.
/// </summary>
internal static class FileDropIntentPolicy
{
    public static FileDropIntent ResolveMappedTransfer(
        bool hasMappedFolder,
        bool forceCopy,
        bool controlDown,
        bool shiftDown,
        bool defaultMove,
        bool canCopy = true,
        bool canMove = true,
        bool altDown = false,
        bool followWindows = false,
        bool sameVolume = true,
        bool canLink = true,
        bool protectDefaultMoveAsCopy = false,
        SourceOriginSnapshot? sourceOrigin = null,
        TransferDomain domain = TransferDomain.ExternalInbound)
    {
        uint allowedEffects =
            (canCopy ? NativeDropEffectPolicy.Copy : 0u) |
            (canMove ? NativeDropEffectPolicy.Move : 0u) |
            (canLink ? NativeDropEffectPolicy.Link : 0u);
        SourceOriginSnapshot origin = sourceOrigin ?? new SourceOriginSnapshot(
            protectDefaultMoveAsCopy
                ? InboundSourceProtectionKind.ExternalRemovableOrNetwork
                : InboundSourceProtectionKind.None,
            HasMoveProtectedRoot: !canMove,
            HasUnhydratedCloudPlaceholder: false,
            HasApplicationCacheSource: false,
            HasTransientTempOrMsixCacheSource: false);
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: domain,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: forceCopy,
            HasMappedFolder: hasMappedFolder,
            ControlDown: controlDown,
            ShiftDown: shiftDown,
            AltDown: altDown,
            AllowedEffects: allowedEffects,
            NoOperationMetadata: false,
            DefaultMove: defaultMove,
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: canLink,
            SourceOrigin: origin)).Intent;
    }

    public static bool AreSameVolume(string sourcePath, string destinationPath) =>
        PathOriginClassifier.AreSameVolume(sourcePath, destinationPath);

    public static bool AreAllOnSameVolume(
        IEnumerable<string> sourcePaths,
        string destinationPath) =>
        PathOriginClassifier.AreAllOnSameVolume(sourcePaths, destinationPath);
}
