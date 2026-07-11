// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
        private void CopySelectedPathsToClipboard()
        {
            var selectedPaths = GetSelectedItems()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    
            if (selectedPaths.Length == 0)
            {
                return;
            }
    
            string clipboardText = string.Join(Environment.NewLine, selectedPaths);
            var package = new DataPackage();
            package.SetText(clipboardText);
            DeskBoxClipboardWriteScope.MarkWrite(text: clipboardText, paths: selectedPaths);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowStatusToast(_localizationService.Format("Widget.CopyPathCount", selectedPaths.Length));
        }
    
        private async Task CopyImageTextAsync(WidgetItem item)
        {
            if (!CanCopyImageText(item))
            {
                ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
                return;
            }
    
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
    
                var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (ocrEngine is null)
                {
                    ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
                    return;
                }
    
                var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    ShowStatusToast(_localizationService.T("Widget.CopyImageTextNoText"));
                    return;
                }
    
                var package = new DataPackage();
                package.SetText(ocrResult.Text);
                DeskBoxClipboardWriteScope.MarkWrite(text: ocrResult.Text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                ShowStatusToast(_localizationService.T("Widget.CopyImageTextCopied"));
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetWindow] Failed to copy image text path='{item.Path}': {ex}");
                ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
            }
        }
    
        private static bool CanCopyImageText(WidgetItem item)
        {
            return !item.IsFolder &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path) &&
                   IsImageFile(item.Path);
        }
    
        private async Task CopySelectionToClipboardAsync(bool cut)
        {
            var selectedItems = GetSelectedItems();
            if (selectedItems.Count == 0)
            {
                return;
            }
    
            var sourcePaths = selectedItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourcePaths.Length == 0)
            {
                ShowStatusToast(_localizationService.T("Widget.NoCopyableItems"));
                return;
            }
    
            string clipboardText = string.Join(Environment.NewLine, sourcePaths);
            DeskBoxClipboardWriteScope.MarkWrite(text: clipboardText, paths: sourcePaths);
    
            var package = new DataPackage();
            package.RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy;
            bool shellClipboardSet = ShellClipboardHelper.TrySetFileDropList(sourcePaths, cut);
            if (!shellClipboardSet)
            {
                var storageItems = await App.Current.FileService.GetStorageItemsAsync(sourcePaths);
                if (storageItems.Count > 0)
                {
                    package.SetStorageItems(storageItems);
                }
                else
                {
                    package.SetText(clipboardText);
                }
    
                package.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
                package.Properties["DeskBoxSourcePaths"] = sourcePaths;
                package.Properties["DeskBoxInternalDragToken"] = DeskBoxInternalDragToken;
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
    
            _cutClipboardPaths = cut
                ? sourcePaths
                : [];
    
            ApplyCutState();
            UpdateInteractiveSurfaces();
            ShowStatusToast(cut
                ? _localizationService.Format("Widget.CutCount", sourcePaths.Length)
                : _localizationService.Format("Widget.CopyCount", sourcePaths.Length));
        }
    
        private async Task PasteFromClipboardAsync()
        {
            var clipboard = Clipboard.GetContent();
            IReadOnlyList<string> sourcePaths = TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths")
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    
            if (sourcePaths.Count == 0 && clipboard.Contains(StandardDataFormats.StorageItems))
            {
                var items = await clipboard.GetStorageItemsAsync();
                sourcePaths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();
            }
    
            if (sourcePaths.Count == 0)
            {
                return;
            }
    
            int itemCount = sourcePaths.Count;
    
            bool? moveWhenMapped = clipboard.RequestedOperation != DataPackageOperation.Copy;
    
            await ViewModel.ImportPathsAsync(sourcePaths, moveWhenMapped, useShellProgress: moveWhenMapped == true);
    
            if (moveWhenMapped == true)
            {
                await SyncMoveSourceAsync(
                    TryGetPackageString(clipboard.Properties, "DeskBoxSourceWidgetId"),
                    TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths"));
            }
    
            ClearCutState();
            ShowStatusToast(moveWhenMapped == true
                ? _localizationService.Format("Widget.MovedCount", itemCount)
                : _localizationService.Format("Widget.PastedCount", itemCount));
        }
    
        private void ClearItemSelectionCore(bool clearCutState)
        {
            if (clearCutState)
            {
                ClearCutState();
            }
    
            var listView = GetActiveItemsView();
            if (listView is not null)
            {
                SynchronizeListViewSelection(listView, []);
            }
    
            foreach (var item in ViewModel.Items)
            {
                item.IsSelected = false;
            }
    
            _lastSelectionAnchorIndex = -1;
            UpdateInteractiveSurfaces();
        }
    
        private void ApplyCutState()
        {
            foreach (var item in ViewModel.Items)
            {
                item.IsCut = _cutClipboardPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
            }
        }
    
        private void ClearRemovedCutPaths()
        {
            if (_cutClipboardPaths.Length == 0)
            {
                return;
            }
    
            var currentPaths = ViewModel.Items
                .Select(item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cutClipboardPaths = _cutClipboardPaths
                .Where(currentPaths.Contains)
                .ToArray();
            ApplyCutState();
            UpdateInteractiveSurfaces();
        }
    
        private static bool CanPasteFromClipboard()
        {
            try
            {
                return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
            }
            catch
            {
                return false;
            }
        }
}
