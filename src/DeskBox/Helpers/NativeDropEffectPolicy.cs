using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Helpers;

/// <summary>
/// Chooses OLE drag feedback separately from the completion effect. DeskBox
/// imports native file drops asynchronously and owns the actual move/copy, so
/// the source must never be told to perform move cleanup after Drop returns.
/// </summary>
internal static class NativeDropEffectPolicy
{
    internal const uint None = 0;
    internal const uint Copy = 1;
    internal const uint Move = 2;
    internal const uint Link = 4;
    internal const uint ControlKeyState = 0x0008;
    internal const uint ShiftKeyState = 0x0004;
    // OLE IDropTarget key-state flags (MK_* in oleidl.h).
    internal const uint RightButtonKeyState = 0x0002;
    internal const uint AltKeyState = 0x0020;

    public static bool IsVirtualOnlyFileData(
        bool hasPhysicalPathData,
        bool hasVirtualDescriptorData)
    {
        return !hasPhysicalPathData && hasVirtualDescriptorData;
    }

    public static uint ResolveFeedbackEffect(
        bool hasFileData,
        bool hasVirtualFileData,
        uint keyState,
        uint allowedEffects,
        bool hasShellApplicationData = false,
        bool defaultMove = true,
        bool followWindows = false,
        bool sameVolume = true,
        bool protectDefaultMoveAsCopy = false,
        bool hasMappedFolder = true,
        TransferTargetKind targetKind = TransferTargetKind.DirectorySurface,
        TransferDomain domain = TransferDomain.ExternalInbound,
        SourceOriginSnapshot? sourceOrigin = null)
    {
        SourceOriginSnapshot origin = sourceOrigin ?? new SourceOriginSnapshot(
            protectDefaultMoveAsCopy
                ? InboundSourceProtectionKind.ExternalRemovableOrNetwork
                : InboundSourceProtectionKind.None,
            HasMoveProtectedRoot: false,
            HasUnhydratedCloudPlaceholder: false,
            HasApplicationCacheSource: false,
            HasTransientTempOrMsixCacheSource: false);
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: domain,
            TargetKind: targetKind,
            HasFileData: hasFileData,
            HasVirtualFileData: hasVirtualFileData,
            HasShellApplicationOrNamespaceData: hasShellApplicationData,
            ForceCopy: hasVirtualFileData,
            HasMappedFolder: hasMappedFolder,
            ControlDown: (keyState & ControlKeyState) != 0,
            ShiftDown: (keyState & ShiftKeyState) != 0,
            AltDown: (keyState & AltKeyState) != 0,
            AllowedEffects: allowedEffects,
            NoOperationMetadata: false,
            DefaultMove: defaultMove,
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).FeedbackEffect;
    }

    public static bool ShouldCopyMappedTransfer(
        bool containsTemporaryFiles,
        uint keyState,
        bool defaultMove,
        bool followWindows = false,
        bool sameVolume = true,
        bool canCopy = true,
        bool canMove = true,
        bool protectDefaultMoveAsCopy = false)
    {
        FileDropIntent intent = FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: true,
            forceCopy: containsTemporaryFiles,
            controlDown: (keyState & ControlKeyState) != 0,
            shiftDown: (keyState & ShiftKeyState) != 0,
            defaultMove: defaultMove,
            canCopy: canCopy,
            canMove: canMove,
            altDown: (keyState & AltKeyState) != 0,
            followWindows: followWindows,
            sameVolume: sameVolume,
            protectDefaultMoveAsCopy: protectDefaultMoveAsCopy);
        return intent != FileDropIntent.Move;
    }

    public static bool ShouldCreateMappedShortcut(
        bool containsTemporaryFiles,
        uint keyState)
    {
        return !containsTemporaryFiles &&
               ((keyState & AltKeyState) != 0 ||
                (keyState & (ControlKeyState | ShiftKeyState)) ==
                    (ControlKeyState | ShiftKeyState));
    }

    public static bool IsRightButtonDrag(uint keyState)
    {
        return (keyState & RightButtonKeyState) != 0;
    }

    public static uint ResolveCompletionEffect(
        bool hasExtractedPaths,
        uint allowedEffects,
        bool createdShellApplicationLinks = false,
        bool hasVirtualOnlyData = false)
    {
        if (!hasExtractedPaths)
        {
            return None;
        }

        if (createdShellApplicationLinks ||
            (hasVirtualOnlyData && (allowedEffects & Copy) == 0))
        {
            return (allowedEffects & Link) != 0
                ? Link
                : (allowedEffects & Copy) != 0
                    ? Copy
                    : None;
        }

        // Returning MOVE would authorize the drag source (notably Explorer)
        // to delete its source after this callback returns. DeskBox has only
        // queued its own asynchronous transfer at that point, producing a
        // check-then-disappear race. Report COPY so source cleanup stays off.
        return (allowedEffects & Copy) != 0 ? Copy : None;
    }
}
