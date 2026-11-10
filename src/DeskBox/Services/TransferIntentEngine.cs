using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

internal enum TransferDomain
{
    ExternalInbound = 0,
    InternalDeskBox = 1,
    Outbound = 2,
}

internal enum TransferTargetKind
{
    DirectorySurface = 0,
    RecycleBinTile = 1,
    ApplicationShortcutTile = 2,
    AssociationOnlySurface = 3,
    UnsupportedSurface = 4,
}

internal readonly record struct TransferIntentRequest(
    TransferDomain Domain,
    TransferTargetKind TargetKind,
    bool HasFileData,
    bool HasVirtualFileData,
    bool HasShellApplicationOrNamespaceData,
    bool ForceCopy,
    bool HasMappedFolder,
    bool ControlDown,
    bool ShiftDown,
    bool AltDown,
    uint AllowedEffects,
    bool NoOperationMetadata,
    bool DefaultMove,
    bool FollowWindows,
    bool SameVolume,
    bool CanLink = true,
    SourceOriginSnapshot SourceOrigin = default);

internal readonly record struct TransferIntentDecision(
    FileDropIntent Intent,
    uint FeedbackEffect,
    bool SourceCanCopy,
    bool SourceCanMove);

/// <summary>
/// Single pure-function transfer intent and OLE feedback engine shared across
/// native OLE <c>IDropTarget</c>, XAML routed drag-over/drop, group-tab hover,
/// and elevated pointer drag. Decouples target-tile action semantics (Recycle
/// Bin deletion, application shortcut launch, directory import) from source
/// medium protection and keeps internal DeskBox organization (Domain B)
/// independent of external drag-out preferences (Domain C).
/// </summary>
internal static class TransferIntentEngine
{
    internal static TransferIntentDecision Evaluate(in TransferIntentRequest request)
    {
        if (request.TargetKind == TransferTargetKind.UnsupportedSurface)
        {
            return new TransferIntentDecision(
                FileDropIntent.None,
                NativeDropEffectPolicy.None,
                SourceCanCopy: false,
                SourceCanMove: false);
        }

        if (request.TargetKind == TransferTargetKind.AssociationOnlySurface)
        {
            return EvaluateAssociationOnlyTarget(in request);
        }

        if (request.TargetKind == TransferTargetKind.ApplicationShortcutTile)
        {
            return EvaluateApplicationShortcutTarget(in request);
        }

        if (request.TargetKind == TransferTargetKind.RecycleBinTile)
        {
            return EvaluateRecycleBinTarget(in request);
        }

        if (request.HasShellApplicationOrNamespaceData)
        {
            uint shellEffect = (request.AllowedEffects & NativeDropEffectPolicy.Link) != 0
                ? NativeDropEffectPolicy.Link
                : (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0
                    ? NativeDropEffectPolicy.Copy
                    : NativeDropEffectPolicy.None;
            FileDropIntent shellIntent = shellEffect != NativeDropEffectPolicy.None
                ? FileDropIntent.Shortcut
                : FileDropIntent.None;
            return new TransferIntentDecision(
                shellIntent,
                shellEffect,
                SourceCanCopy: (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0,
                SourceCanMove: false);
        }

        if (!request.HasFileData)
        {
            return new TransferIntentDecision(
                FileDropIntent.None,
                NativeDropEffectPolicy.None,
                SourceCanCopy: false,
                SourceCanMove: false);
        }

        bool effectiveForceCopy = request.ForceCopy || request.HasVirtualFileData;
        bool sourceCanCopy;
        bool sourceCanMove;
        if (request.Domain == TransferDomain.InternalDeskBox)
        {
            // Domain B (intra-box, Stack Popover, cross-box): internal DeskBox
            // drags always carry full file paths and are never artificially
            // locked to Copy by ManagedDragOutAction == Copy or Win10 single-bit
            // AllowedOperations. Only OS/shared roots (Public Desktop, Start Menu,
            // Program Files, Windows) forbid Move.
            sourceCanCopy = true;
            sourceCanMove = !effectiveForceCopy &&
                !request.SourceOrigin.IsMoveProtectedSystemOrSharedSource;
        }
        else
        {
            // Webpage <a href> drags provide a virtual .url descriptor with
            // AllowedEffects == DROPEFFECT_LINK (no Copy bit); materializing the
            // .url stream is safe and matches Windows Explorer's link drop.
            sourceCanCopy = request.NoOperationMetadata ||
                (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0 ||
                (request.HasVirtualFileData &&
                 (request.AllowedEffects & NativeDropEffectPolicy.Link) != 0);
            sourceCanMove = !effectiveForceCopy &&
                !request.SourceOrigin.IsMoveProtectedSystemOrSharedSource &&
                (request.NoOperationMetadata ||
                 (request.AllowedEffects & NativeDropEffectPolicy.Move) != 0);
        }

        bool protectDefaultMoveAsCopy =
            request.Domain != TransferDomain.Outbound &&
            request.SourceOrigin.IsProtectedCopyByDefault;
        bool prefersShortcutByDefault =
            request.Domain == TransferDomain.ExternalInbound &&
            request.SourceOrigin.PrefersShortcutByDefault;

        FileDropIntent intent = ResolveDirectoryIntent(
            request.HasMappedFolder,
            effectiveForceCopy,
            request.ControlDown,
            request.ShiftDown,
            request.AltDown,
            request.DefaultMove,
            request.FollowWindows,
            request.SameVolume,
            sourceCanCopy,
            sourceCanMove,
            request.CanLink,
            protectDefaultMoveAsCopy,
            prefersShortcutByDefault);

        uint feedbackEffect = MapIntentToWireEffect(
            intent,
            request.Domain,
            request.AllowedEffects,
            request.NoOperationMetadata,
            request.HasVirtualFileData);

        return new TransferIntentDecision(
            intent,
            feedbackEffect,
            sourceCanCopy,
            sourceCanMove);
    }

    internal static string ResolveProspectiveManagedDestination(
        string? currentFolderPath,
        string? mappedFolderPath,
        string? defaultManagedStorageRoot)
    {
        if (!string.IsNullOrWhiteSpace(currentFolderPath))
        {
            return currentFolderPath;
        }

        if (!string.IsNullOrWhiteSpace(mappedFolderPath))
        {
            return mappedFolderPath;
        }

        return SettingsService.NormalizeManagedStorageRootPath(defaultManagedStorageRoot);
    }

    private static TransferIntentDecision EvaluateAssociationOnlyTarget(
        in TransferIntentRequest request)
    {
        if (!request.HasFileData && !request.HasShellApplicationOrNamespaceData)
        {
            return new TransferIntentDecision(
                FileDropIntent.None,
                NativeDropEffectPolicy.None,
                SourceCanCopy: false,
                SourceCanMove: false);
        }

        // Todo, QuickCapture, Clipboard, and drop-enabled Custom widgets attach
        // or copy files without relocating external Explorer sources, so Shift
        // must never report DROPEFFECT_MOVE back to an external drag source.
        uint effect = (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0 || request.NoOperationMetadata
            ? NativeDropEffectPolicy.Copy
            : (request.AllowedEffects & NativeDropEffectPolicy.Link) != 0
                ? NativeDropEffectPolicy.Link
                : request.Domain == TransferDomain.InternalDeskBox &&
                  (request.AllowedEffects & NativeDropEffectPolicy.Move) != 0
                    ? NativeDropEffectPolicy.Move
                    : NativeDropEffectPolicy.None;
        FileDropIntent intent = effect == NativeDropEffectPolicy.None
            ? FileDropIntent.None
            : effect == NativeDropEffectPolicy.Link
                ? FileDropIntent.Shortcut
                : FileDropIntent.Copy;
        return new TransferIntentDecision(
            intent,
            effect,
            SourceCanCopy: effect != NativeDropEffectPolicy.None,
            SourceCanMove: false);
    }

    private static TransferIntentDecision EvaluateApplicationShortcutTarget(
        in TransferIntentRequest request)
    {
        // Passing file paths to an application shortcut never relocates or
        // deletes source files, so Public Desktop / USB / Shift constraints do
        // not block launch feedback.
        if (!request.HasFileData || request.HasShellApplicationOrNamespaceData)
        {
            return new TransferIntentDecision(
                FileDropIntent.None,
                NativeDropEffectPolicy.None,
                SourceCanCopy: false,
                SourceCanMove: false);
        }

        uint effect = (request.AllowedEffects & NativeDropEffectPolicy.Link) != 0
            ? NativeDropEffectPolicy.Link
            : (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0
                ? NativeDropEffectPolicy.Copy
                : (request.AllowedEffects & NativeDropEffectPolicy.Move) != 0
                    ? NativeDropEffectPolicy.Move
                    : request.NoOperationMetadata
                        ? NativeDropEffectPolicy.Link
                        : NativeDropEffectPolicy.None;
        return new TransferIntentDecision(
            effect != NativeDropEffectPolicy.None
                ? FileDropIntent.Shortcut
                : FileDropIntent.None,
            effect,
            SourceCanCopy: true,
            SourceCanMove: false);
    }

    private static TransferIntentDecision EvaluateRecycleBinTarget(
        in TransferIntentRequest request)
    {
        // Virtual streams, provider-staged temp archives, and Shell app tiles
        // cannot be sent to the Recycle Bin. Real filesystem items (including
        // Public Desktop or USB items, where IFileOperation handles UAC/confirm
        // prompts natively) always advertise deletion (Move) rather than Copy.
        if (!request.HasFileData ||
            request.HasVirtualFileData ||
            request.HasShellApplicationOrNamespaceData ||
            request.ForceCopy)
        {
            return new TransferIntentDecision(
                FileDropIntent.None,
                NativeDropEffectPolicy.None,
                SourceCanCopy: false,
                SourceCanMove: false);
        }

        uint effect = (request.AllowedEffects & NativeDropEffectPolicy.Move) != 0
            ? NativeDropEffectPolicy.Move
            : (request.AllowedEffects & NativeDropEffectPolicy.Copy) != 0
                ? NativeDropEffectPolicy.Copy
                : (request.AllowedEffects & NativeDropEffectPolicy.Link) != 0
                    ? NativeDropEffectPolicy.Link
                    : request.NoOperationMetadata
                        ? NativeDropEffectPolicy.Move
                        : NativeDropEffectPolicy.None;
        return new TransferIntentDecision(
            effect != NativeDropEffectPolicy.None
                ? FileDropIntent.Move
                : FileDropIntent.None,
            effect,
            SourceCanCopy: false,
            SourceCanMove: effect != NativeDropEffectPolicy.None);
    }

    private static FileDropIntent ResolveDirectoryIntent(
        bool hasMappedFolder,
        bool forceCopy,
        bool controlDown,
        bool shiftDown,
        bool altDown,
        bool defaultMove,
        bool followWindows,
        bool sameVolume,
        bool canCopy,
        bool canMove,
        bool canLink,
        bool protectDefaultMoveAsCopy,
        bool prefersShortcutByDefault = false)
    {
        if (forceCopy)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if ((altDown || (controlDown && shiftDown)) && canLink)
        {
            return FileDropIntent.Shortcut;
        }

        if (controlDown)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if (shiftDown)
        {
            return canMove ? FileDropIntent.Move : FileDropIntent.None;
        }

        if (prefersShortcutByDefault && canLink)
        {
            return FileDropIntent.Shortcut;
        }

        if (protectDefaultMoveAsCopy)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if (!hasMappedFolder)
        {
            return FileDropIntent.Reference;
        }

        if (followWindows)
        {
            if (sameVolume && canMove)
            {
                return FileDropIntent.Move;
            }

            return canCopy
                ? FileDropIntent.Copy
                : canMove
                    ? FileDropIntent.Move
                    : FileDropIntent.None;
        }

        if (defaultMove)
        {
            return canMove
                ? FileDropIntent.Move
                : canCopy
                    ? FileDropIntent.Copy
                    : FileDropIntent.None;
        }

        return canCopy
            ? FileDropIntent.Copy
            : FileDropIntent.None;
    }

    private static uint MapIntentToWireEffect(
        FileDropIntent intent,
        TransferDomain domain,
        uint allowedEffects,
        bool noOperationMetadata,
        bool hasVirtualFileData = false)
    {
        if (noOperationMetadata)
        {
            return intent switch
            {
                FileDropIntent.Copy => NativeDropEffectPolicy.Copy,
                FileDropIntent.Move => NativeDropEffectPolicy.Move,
                FileDropIntent.Shortcut or FileDropIntent.Reference => NativeDropEffectPolicy.Link,
                _ => NativeDropEffectPolicy.None,
            };
        }

        return intent switch
        {
            FileDropIntent.Copy when (allowedEffects & NativeDropEffectPolicy.Copy) != 0 =>
                NativeDropEffectPolicy.Copy,
            FileDropIntent.Copy when hasVirtualFileData &&
                                     (allowedEffects & NativeDropEffectPolicy.Link) != 0 =>
                NativeDropEffectPolicy.Link,
            FileDropIntent.Copy when domain == TransferDomain.InternalDeskBox &&
                                     (allowedEffects & NativeDropEffectPolicy.Move) != 0 =>
                NativeDropEffectPolicy.Move,

            FileDropIntent.Move when (allowedEffects & NativeDropEffectPolicy.Move) != 0 =>
                NativeDropEffectPolicy.Move,
            FileDropIntent.Move when domain == TransferDomain.InternalDeskBox &&
                                     (allowedEffects & NativeDropEffectPolicy.Copy) != 0 =>
                NativeDropEffectPolicy.Copy,

            FileDropIntent.Shortcut or FileDropIntent.Reference
                when (allowedEffects & NativeDropEffectPolicy.Link) != 0 =>
                NativeDropEffectPolicy.Link,
            FileDropIntent.Shortcut or FileDropIntent.Reference
                when (allowedEffects & NativeDropEffectPolicy.Copy) != 0 =>
                NativeDropEffectPolicy.Copy,
            FileDropIntent.Shortcut or FileDropIntent.Reference
                when (allowedEffects & NativeDropEffectPolicy.Move) != 0 =>
                NativeDropEffectPolicy.Move,

            _ => NativeDropEffectPolicy.None,
        };
    }
}
