﻿// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void InstallFileDropSubclass()
    {
        if (_isFileDropSubclassInstalled)
        {
            return;
        }

        _isFileDropSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _fileDropSubclassProc,
            FileDropSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeSubclassInstall " +
            $"hwnd=0x{_hWnd.ToInt64():X} installed={_isFileDropSubclassInstalled}");

        // Register IDropTarget for richer drag-over feedback.
        // This supersedes WM_DROPFILES for OLE-aware drag sources (Explorer).
        // WM_DROPFILES is kept as fallback for legacy sources.
        try
        {
            _nativeDropTarget = new NativeDropTarget(_hWnd);
            _nativeDropTarget.DragEnterEvent += OnNativeDragEnter;
            _nativeDropTarget.DragOverEvent += OnNativeDragOver;
            _nativeDropTarget.DragLeaveEvent += OnNativeDragLeave;
            _nativeDropTarget.DropEvent += OnNativeDrop;
            _nativeDropTarget.Register();
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to register IDropTarget: {ex.Message}");
            _nativeDropTarget = null;
        }
    }

    private void RemoveFileDropSubclass()
    {
        if (!_isFileDropSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _fileDropSubclassProc, FileDropSubclassId);
        _isFileDropSubclassInstalled = false;

        if (_nativeDropTarget is not null)
        {
            _nativeDropTarget.DragEnterEvent -= OnNativeDragEnter;
            _nativeDropTarget.DragOverEvent -= OnNativeDragOver;
            _nativeDropTarget.DragLeaveEvent -= OnNativeDragLeave;
            _nativeDropTarget.DropEvent -= OnNativeDrop;
            _nativeDropTarget.Dispose();
            _nativeDropTarget = null;
        }

        ClearNativeDragHighlight();
    }

    private IntPtr FileDropSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == Win32Helper.WM_DROPFILES)
        {
            var paths = Win32Helper.GetDroppedFilePaths((IntPtr)wParam);
            App.Log(
                $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropFiles " +
                $"count={paths.Count} mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} " +
                $"managed={ViewModel.FollowsDefaultStoragePath}");
            if (paths.Count > 0)
            {
                DispatcherQueue.TryEnqueue(async () => await ImportNativeDropPathsAsync(paths));
            }

            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task ImportNativeDropPathsAsync(
        IReadOnlyList<string> paths,
        bool cleanupTemporaryFiles = false)
    {
        App.Log($"[DropDiagnostic] ImportNativeDropPathsAsync enter count={paths.Count} migrationBusy={_isMigrationBusy} paths=[{string.Join("|", paths)}]");
        if (_isMigrationBusy || paths.Count == 0)
        {
            App.Log($"[DropDiagnostic] ImportNativeDropPathsAsync skipping: migrationBusy={_isMigrationBusy} count={paths.Count}");
            return;
        }

        bool? moveWhenMapped = null;
        try
        {
            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            var acceptedOperation = NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder);
            if (acceptedOperation == DataPackageOperation.None)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropRejectedOperation " +
                    $"count={paths.Count} mapped={movesIntoFolder}");
                return;
            }

            moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] NativeDropPrecheck failed widget='{ViewModel.Name}' id={ViewModel.Config.Id}: {ex}");
            return;
        }

        // Show the import overlay for large transfers, same as the WinUI drop path.
        bool showOverlay = ShouldShowImportOverlay(paths);
        if (showOverlay)
        {
            SetImportBusy(true);
        }
        try
        {
            App.Log($"[DropDiagnostic] ImportNativeDropPathsAsync calling ImportPathsAsync moveWhenMapped={moveWhenMapped} useShellProgress={moveWhenMapped == true}");
            await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);
            App.Log("[DropDiagnostic] ImportNativeDropPathsAsync ImportPathsAsync completed successfully");
            ClearCutState();
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] NativeDropFailed widget='{ViewModel.Name}' id={ViewModel.Config.Id}: {ex}");
        }
        finally
        {
            if (showOverlay)
            {
                SetImportBusy(false);
            }
            if (cleanupTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(paths);
            }
        }
    }

    // ── IDropTarget event handlers (native OLE drag-drop) ──

    private void OnNativeDragEnter(int screenX, int screenY, bool hasFileData)
    {
        if (!hasFileData || _isMigrationBusy)
        {
            return;
        }

        _isNativeDragActive = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            StartDragHighlight();
            ShowNativeDragHighlight(screenX, screenY);
        });
    }

    private void OnNativeDragOver(int screenX, int screenY)
    {
        if (!_isNativeDragActive)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ShowNativeDragHighlight(screenX, screenY);
        });
    }

    private void OnNativeDragLeave()
    {
        _isNativeDragActive = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearNativeDragHighlight();
            StopDragHighlight();
        });
    }

    private void OnNativeDrop(
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        bool containsTemporaryFiles)
    {
        _isNativeDragActive = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearNativeDragHighlight();
            StopDragHighlight();
        });

        if (paths.Count == 0)
        {
            return;
        }

        App.Log(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeIDropTargetDrop " +
            $"count={paths.Count} mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} " +
            $"managed={ViewModel.FollowsDefaultStoragePath}");

        // Check if the drop is over a folder item
        if (TryGetFolderItemAtScreenPoint(screenX, screenY, out var folderBorder, out var folderItem))
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                bool showOverlay = ShouldShowImportOverlay(paths);
                if (showOverlay)
                {
                    SetImportBusy(true);
                }
                try
                {
                    bool move = ShouldMoveForAcceptedOperation(
                        NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder: true));
                    var results = await App.Current.FileService.TransferItemsWithResultAsync(
                        paths, folderItem.Path, move);

                    if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                    {
                        await ViewModel.RefreshFromConfigAsync();
                    }

                    ShowStatusToast(_localizationService.Format(
                        move ? "Widget.MovedToFolder" : "Widget.CopiedToFolder",
                        folderItem.Name,
                        results.Count));
                }
                catch (Exception ex)
                {
                    App.Log($"[DropTarget] Folder drop failed: {ex.Message}");
                }
                finally
                {
                    if (showOverlay)
                    {
                        SetImportBusy(false);
                    }
                    if (containsTemporaryFiles)
                    {
                        CleanupNativeTemporaryDropFiles(paths);
                    }
                }
            });
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
            await ImportNativeDropPathsAsync(paths, containsTemporaryFiles));
    }

    private static void CleanupNativeTemporaryDropFiles(IReadOnlyList<string> paths)
    {
        string temporaryRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string directory in paths
                     .Select(Path.GetDirectoryName)
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(directory => Path.GetFullPath(directory!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string normalizedDirectory = directory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!normalizedDirectory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropTarget] Failed to clean virtual drop directory: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shows visual highlight during native drag-over.
    /// Highlights the folder item under the cursor, or the root grid as fallback.
    /// </summary>
    private void ShowNativeDragHighlight(int screenX, int screenY)
    {
        if (TryGetFolderItemAtScreenPoint(screenX, screenY, out var border, out _))
        {
            if (!ReferenceEquals(_nativeDragHighlightBorder, border))
            {
                ClearNativeDragHighlight();
                _nativeDragHighlightBorder = border;
                ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
            }
            return;
        }

        // No folder under cursor — clear folder highlight, show root-level feedback
        ClearNativeDragHighlight();
        // Root-level feedback is handled by the existing _isNativeDragActive flag
        // which could be used to show a subtle border glow on the window.
    }

    private void ClearNativeDragHighlight()
    {
        if (_nativeDragHighlightBorder is not null)
        {
            var border = _nativeDragHighlightBorder;
            _nativeDragHighlightBorder = null;
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }

        // Also clear the shared folder drop target if set
        ClearFolderDropTarget();
    }

    /// <summary>
    /// Hit-tests a screen coordinate against interactive folder surfaces.
    /// Returns the border and WidgetItem if the point is over a folder item.
    /// </summary>
    private bool TryGetFolderItemAtScreenPoint(int screenX, int screenY, out Border border, out WidgetItem folder)
    {
        foreach (var surface in _interactiveSurfaces.ToArray())
        {
            if (surface.DataContext is not WidgetItem { IsFolder: true } item ||
                !Directory.Exists(item.Path) ||
                surface.XamlRoot is null ||
                surface.ActualWidth <= 0 ||
                surface.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = surface.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var left = _appWindow.Position.X + (topLeft.X * scale);
            var top = _appWindow.Position.Y + (topLeft.Y * scale);
            var right = left + (surface.ActualWidth * scale);
            var bottom = top + (surface.ActualHeight * scale);

            if (screenX >= left && screenX <= right &&
                screenY >= top && screenY <= bottom)
            {
                border = surface;
                folder = item;
                return true;
            }
        }

        border = null!;
        folder = null!;
        return false;
    }
}
