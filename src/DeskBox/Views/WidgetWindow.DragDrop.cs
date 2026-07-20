﻿﻿﻿// Copyright (c) DeskBox. All rights reserved.

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

/// <summary>
/// Partial class containing drag-and-drop, file drop subclass,
/// folder drop target, and item drag package logic for WidgetWindow.
/// </summary>
public sealed partial class WidgetWindow
{

    private const string DeskBoxInternalDragToken = "DeskBox.WidgetItemDrag.v2";
    private static readonly UIntPtr FileDropSubclassId = new(0xDDB0);
    private readonly Win32Helper.SubclassProc _fileDropSubclassProc;
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private string? _lastRootDragDiagnosticSignature;
    private string? _lastFolderDragDiagnosticSignature;
    private Border? _folderDropTarget;
    private bool _surfaceDragCompletionHandled;
    private bool _isFileDropSubclassInstalled;

    // ── Native IDropTarget (OLE drag-drop) ──
    private NativeDropTarget? _nativeDropTarget;
    private bool _isNativeDragActive;
    private Border? _nativeDragHighlightBorder;

    // ── Real-time reorder state ──
    private bool _isReorderDragActive;
    private string[] _reorderDragPaths = [];

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();

        if (_isMigrationBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            LogDropDiagnostic("RootDragOverBusy", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            LogDropDiagnostic("RootDragOverNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);

        // Same-widget internal drag: real-time reordering.
        // All sort modes allow drag reorder — dragging switches to Manual mode
        // and items move in real-time to show where the drop will land.
        if (HasDeskBoxInternalDragData(e.DataView.Properties))
        {
            string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.Caption = _localizationService.T("Widget.DragCaption.Reorder");

                // Perform real-time reordering for visual feedback.
                HandleRealTimeReorder(e.DataView.Properties, e.GetPosition(GetDropTargetControl()));
                return;
            }
        }

        e.AcceptedOperation = NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder);
        LogDropDiagnostic("RootDragOver", e.DataView, e.AcceptedOperation, movesIntoFolder);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            return;
        }

        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = movesIntoFolder
            ? _localizationService.Format(
                GetRootFolderDropCaptionKey(),
                GetAcceptedOperationCaption(e.AcceptedOperation))
            : _localizationService.T("Widget.DragCaption.Reference");
    }

private void RootGrid_DragEnter(object sender, DragEventArgs e)
{
LogDropDiagnostic("RootDragEnter", e.DataView, e.AcceptedOperation, !string.IsNullOrEmpty(ViewModel.MappedFolderPath));
StartDragHighlight();
}

    private string GetRootFolderDropCaptionKey()
    {
        return ViewModel.FollowsDefaultStoragePath
            ? "Widget.DragCaption.Managed"
            : "Widget.DragCaption.Mapped";
    }

    private DataPackageOperation NormalizePathDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (requestedOperation == DataPackageOperation.None)
        {
            return movesIntoFolder
                ? GetManagedDropOperation()
                : DataPackageOperation.Link;
        }

        var operation = GetAcceptedDropOperation(requestedOperation, movesIntoFolder);
        if (operation != DataPackageOperation.None ||
            !movesIntoFolder ||
            !SupportsOperation(requestedOperation, DataPackageOperation.Link))
        {
            return operation;
        }

        return DataPackageOperation.Link;
    }

    private string GetAcceptedOperationCaption(DataPackageOperation acceptedOperation)
    {
        return ShouldMoveForAcceptedOperation(acceptedOperation)
            ? _localizationService.T("Common.Move")
            : _localizationService.T("Common.Copy");
    }

    private bool ShouldMoveForAcceptedOperation(DataPackageOperation acceptedOperation)
    {
        return acceptedOperation switch
        {
            DataPackageOperation.Copy => false,
            DataPackageOperation.Move => true,
            DataPackageOperation.Link => true,
            _ => true
        };
    }

private void RootGrid_DragLeave(object sender, DragEventArgs e)
{
e.Handled = true;
ClearFolderDropTarget();
StopDragHighlight();
_lastRootDragDiagnosticSignature = null;

// Persist any real-time reordering that was done during DragOver.
if (_isReorderDragActive)
{
_isReorderDragActive = false;
_reorderDragPaths = [];
ViewModel.PersistManualOrder();
}
}

private async void RootGrid_Drop(object sender, DragEventArgs e)
{
e.Handled = true;
ClearFolderDropTarget();
StopDragHighlight();
        _lastRootDragDiagnosticSignature = null;

        var deferral = e.GetDeferral();
        try
        {
            if (_isMigrationBusy)
            {
                return;
            }

            if (!HasPathDropData(e.DataView))
            {
                LogDropDiagnostic("RootDropNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
                return;
            }

            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            LogDropDiagnostic("RootDrop", e.DataView, e.AcceptedOperation, movesIntoFolder);

            var paths = await GetDropPathsAsync(e.DataView);
            if (paths.Length == 0)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=RootDropNoPaths " +
                    $"mapped={movesIntoFolder} requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} " +
                    $"formats={FormatDataPackageFormats(e.DataView.AvailableFormats)}");
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder)
                : e.AcceptedOperation;
            e.AcceptedOperation = acceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                LogDropDiagnostic("RootDropRejectedOperation", e.DataView, acceptedOperation, movesIntoFolder);
                return;
            }

            bool? moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;

            string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");

            // ── Same-widget internal drag: persist real-time reorder ──
            if (HasDeskBoxInternalDragData(e.DataView.Properties) &&
                string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
            {
                // Real-time reordering was done during DragOver.  If the mode
                // wasn't Manual yet, switch now (HandleRealTimeReorder already
                // did this, but this covers edge cases).
                if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
                {
                    ViewModel.SetSortMode(WidgetSortMode.Manual);
                }

                // Do a final reorder to the exact drop position, then persist.
                var dragPaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");
                HandleFinalReorder(dragPaths, e.GetPosition(GetDropTargetControl()));
                ViewModel.PersistManualOrder();

                _isReorderDragActive = false;
                _reorderDragPaths = [];

                // Same-widget drop: no file transfer needed regardless of mode.
                return;
            }

            if (movesIntoFolder &&
                HasDeskBoxInternalDragData(e.DataView.Properties) &&
                string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal) &&
                moveWhenMapped == true)
            {
                return;
            }

            // Extract all needed data from the DataPackageView before completing
            // the deferral — the DataView becomes invalid after Complete().
            string? syncSourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            var syncSourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");

            // Complete the deferral early so the drag glyph disappears immediately.
            // The actual file transfer continues in the background with a visual overlay.
            deferral.Complete();
            deferral = null;

            // Only show the import overlay for large transfers to avoid
            // flashing for small files.
            bool showOverlay = ShouldShowImportOverlay(paths);
            if (showOverlay)
            {
                SetImportBusy(true);
            }
            try
            {
                await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

                if (moveWhenMapped == true)
                {
                    await SyncMoveSourceAsync(syncSourceWidgetId, syncSourcePaths);
                }

                ClearCutState();
            }
            catch (Exception ex)
            {
                App.Log($"[Widget] RootGrid_Drop failed: {ex}");
            }
            finally
            {
                if (showOverlay)
                {
                    SetImportBusy(false);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Widget] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral?.Complete();
        }
    }

    /// <summary>
    /// Shows the native Windows Explorer context menu for a single file item.
    /// Handles Z-order elevation, coordinate conversion, and foreground window management.
    /// </summary>
    /// <returns>True if the native menu was shown (regardless of whether a command was invoked); false if it failed and the caller should fall back.</returns>
    private static bool ShouldShowImportOverlay(IReadOnlyList<string> paths)
    {
        const long ThresholdBytes = 10 * 1024 * 1024; // 10 MB

        long totalSize = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    totalSize += new FileInfo(path).Length;
                }
                else if (Directory.Exists(path))
                {
                    // For directories, enumerate is too expensive — assume
                    // large and show the overlay.
                    return true;
                }
            }
            catch
            {
                // If we can't stat the file, err on the side of showing the overlay.
                return true;
            }

            if (totalSize >= ThresholdBytes)
            {
                return true;
            }
        }

        return totalSize >= ThresholdBytes;
    }

    private static bool IsInvalidFolderDrop(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || sourcePaths.Count == 0)
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource = Path.GetFullPath(sourcePath);
            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Directory.Exists(normalizedSource) &&
                FileService.IsPathUnderDirectory(normalizedDestination, normalizedSource))
            {
                return true;
            }
        }

        return false;
    }

    private async Task MoveDraggedPathsBackToDesktopAsync(IReadOnlyList<string> sourcePaths, bool useShellProgress)
    {
        var pathSet = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        var draggedItems = ViewModel.Items
            .Where(item =>
                pathSet.Contains(Path.GetFullPath(item.Path)) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToList();
        if (draggedItems.Count == 0)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(draggedItems, useShellProgress);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToDesktopFailed"), ex.Message);
            await ViewModel.RefreshFromConfigAsync();
            UpdateEmptyState();
        }
    }

    private DataPackageOperation GetManagedDropOperation()
    {
        return DataPackageOperation.Move;
    }

    private DataPackageOperation GetAcceptedDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (!movesIntoFolder)
        {
            if (SupportsOperation(requestedOperation, DataPackageOperation.Link))
            {
                return DataPackageOperation.Link;
            }

            return SupportsOperation(requestedOperation, DataPackageOperation.Copy) || requestedOperation == DataPackageOperation.None
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (ctrlPressed)
        {
            return DataPackageOperation.Copy;
        }

        var preferredOperation = GetManagedDropOperation();
        if (CanUseRequestedOperation(requestedOperation, preferredOperation))
        {
            return preferredOperation;
        }

        var fallbackOperation = preferredOperation == DataPackageOperation.Move
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
        return CanUseRequestedOperation(requestedOperation, fallbackOperation)
            ? fallbackOperation
            : DataPackageOperation.None;
    }

    private static bool SupportsOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return (requestedOperation & operation) == operation;
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
    }

    // ── Real-time reorder helpers ────────────────────────────────

    /// <summary>
    /// Returns the active items control (GridView or ListView) that
    /// should be used for hit-testing the drop position.
    /// </summary>
    private UIElement GetDropTargetControl()
    {
        return ItemsGridView.Visibility == Visibility.Visible
            ? ItemsGridView
            : ItemsListView;
    }

    /// <summary>
    /// Performs real-time reordering during DragOver.  Moves the dragged
    /// item to the insertion index so other items shift to make room.
    /// Switches to Manual mode on first call.
    /// </summary>
    private void HandleRealTimeReorder(
        DataPackagePropertySetView properties,
        Windows.Foundation.Point position)
    {
        // Skip when file stacks are enabled — VisibleItems != Items.
        if (ViewModel.FileStacksEnabled)
        {
            return;
        }

        var dragPaths = TryGetPackageStringArray(properties, "DeskBoxSourcePaths");
        if (dragPaths.Count == 0)
        {
            return;
        }

        // Switch to Manual mode if needed (only once per drag).
        if (!_isReorderDragActive)
        {
            if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            _isReorderDragActive = true;
            _reorderDragPaths = dragPaths.ToArray();
        }

        // Find the dragged item (single-item drag is most common).
        var pathSet = _reorderDragPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var draggedItem = ViewModel.Items
            .FirstOrDefault(item => pathSet.Contains(Path.GetFullPath(item.Path)));

        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        int targetIndex = ComputeDropInsertionIndex(GetDropTargetControl(), position);

        // Adjust for Move semantics: Move(oldIndex, newIndex) puts the item
        // AT newIndex.  If target > current, we need target-1 so the item
        // ends up visually where the insertion indicator shows.
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        // Skip if no meaningful move.
        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(draggedItem, targetIndex);
    }

    /// <summary>
    /// Final reorder on drop — moves the item to the exact drop position.
    /// </summary>
    private void HandleFinalReorder(
        IReadOnlyList<string> dragPaths,
        Windows.Foundation.Point dropPosition)
    {
        if (dragPaths.Count == 0 || ViewModel.FileStacksEnabled)
        {
            return;
        }

        var pathSet = dragPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var draggedItem = ViewModel.Items
            .FirstOrDefault(item => pathSet.Contains(Path.GetFullPath(item.Path)));

        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        int targetIndex = ComputeDropInsertionIndex(GetDropTargetControl(), dropPosition);

        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(draggedItem, targetIndex);
    }

    /// <summary>
    /// Computes the insertion index at the given position within the
    /// GridView or ListView.  Handles gaps between items correctly.
    /// For GridView: considers both row (Y) and column (X) position.
    /// For ListView: considers row (Y) position only.
    /// </summary>
    private int ComputeDropInsertionIndex(UIElement control, Windows.Foundation.Point position)
    {
        if (control is not ListViewBase listControl || listControl.Items.Count == 0)
        {
            return 0;
        }

        bool isGridView = control is GridView;

        for (int i = 0; i < listControl.Items.Count; i++)
        {
            var container = listControl.ContainerFromIndex(i) as FrameworkElement;
            if (container is null || container.ActualHeight <= 0)
            {
                continue;
            }

            var transform = container.TransformToVisual(control);
            var rect = transform.TransformBounds(new Windows.Foundation.Rect(
                0, 0, container.ActualWidth, container.ActualHeight));

            if (isGridView)
            {
                // Determine if the pointer is "before" this item.
                // "Before" = above the item's row, or in the same row and
                // to the left of the item's horizontal center.
                bool aboveRow = position.Y < rect.Top;
                bool sameRow = position.Y >= rect.Top && position.Y < rect.Bottom;
                bool leftOfCenter = position.X < (rect.X + rect.Width / 2);

                if (aboveRow || (sameRow && leftOfCenter))
                {
                    return i;
                }
            }
            else
            {
                // ListView: check if pointer is above the vertical midpoint.
                if (position.Y < (rect.Top + rect.Height / 2))
                {
                    return i;
                }
            }
        }

        return listControl.Items.Count;
    }

}
