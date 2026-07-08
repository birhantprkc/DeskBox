using System.ComponentModel;
using DeskBox.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent : UserControl
{
    private const int UndoToastMs = 4200;
    private const int CopyToastMs = 900;
    private const int CopyTapDelayMs = 210;

    private string? _draggedTodoItemId;
    private TodoItemViewModel? _editingItem;
    private TodoItemViewModel? _customDueDateItem;
    private MenuFlyout? _pendingConfirmFlyout;
    private TimeSpan _customDueTime = new(23, 59, 0);
    private string? _copySelectionAnchorId;
    private long _undoToastGeneration;
    private long _copyTapGeneration;
    private bool _isAddingFromInlineEditor;

    private TextBox TodoEditTextBox => TodoInlineEditor.EditorTextBox;

    private Button TodoEditCancelButton => TodoInlineEditor.CancelButton;

    private Button TodoEditSaveButton => TodoInlineEditor.SaveButton;

    private Button TodoEditCloseButton => TodoInlineEditor.CloseButton;

    public TodoWidgetContent()
    {
        InitializeComponent();
        Loaded += TodoWidgetContent_Loaded;
        Unloaded += TodoWidgetContent_Unloaded;
        ActualThemeChanged += (_, _) => ApplyEditorVisualStyle();
    }

    public TodoWidgetContent(TodoWidgetViewModel viewModel)
        : this()
    {
        ViewModel = viewModel;
    }

    public void RevealReminderItem(string? itemId, bool preferTodayFilter)
    {
        if (ViewModel is null)
        {
            return;
        }

        var item = ViewModel.FocusReminderItem(itemId, preferTodayFilter);
        RefreshFilterButtons();
        TodoListView.Focus(FocusState.Programmatic);

        if (item is not null)
        {
            _copySelectionAnchorId = item.Id;
            TodoListView.ScrollIntoView(item);
        }
    }

    public TodoWidgetViewModel? ViewModel
    {
        get => DataContext as TodoWidgetViewModel;
        set
        {
            if (DataContext is TodoWidgetViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            DataContext = value;

            if (value is not null)
            {
                value.PropertyChanged += ViewModel_PropertyChanged;
            }

            RefreshFilterButtons();
        }
    }

    private void TodoWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        RefreshFilterButtons();
    }

    private void TodoWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _copyTapGeneration++;
        CloseTodoEdit();
        CloseCustomDueDateOverlay();
    }

    private void TodoFilterSegmented_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
    }

    private void TodoFilterSegmented_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySegmentedLayout();
    }

    private void ApplySegmentedLayout()
    {
        if (ViewModel?.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(TodoFilterSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(TodoFilterSegmented);
        }
    }

    private void ApplySegmentedStyle()
    {
        if (TodoFilterSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(TodoFilterSegmented, ViewModel?.TabStyle);
        ApplySegmentedLayout();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedFilter))
        {
            ClearCopySelection();
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.TabStyle))
        {
            ApplySegmentedStyle();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedColorFilter))
        {
            ClearCopySelection();
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.UndoText) &&
            ViewModel is { CanUndoLastAction: true })
        {
            ShowUndoToast(ViewModel.UndoText, ViewModel.UndoActionText);
        }
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        if (TodoInlineEditor is null)
        {
            return;
        }

        var localization = App.Current.LocalizationService;
        TodoInlineEditor.Title = _isAddingFromInlineEditor
            ? localization.T("Todo.AddPlaceholder")
            : localization.T("Todo.Menu.Edit");
        TodoInlineEditor.CancelText = localization.T("Common.Cancel");
        TodoInlineEditor.SaveText = localization.T("Common.Save");
        CustomDueDateTitleText.Text = localization.T("Todo.Due.Custom");
        CustomDueDatePicker.PlaceholderText = localization.T("Todo.Due.Custom");
        CustomDueDateCancelButton.Content = localization.T("Common.Cancel");
        CustomDueDateSaveButton.Content = localization.T("Common.Ok");
    }

    private async void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel is not null)
        {
            await ViewModel.AddInputAsync();
        }
    }

    public void OpenAddEditor()
    {
        ClearCopySelection();
        CloseCustomDueDateOverlay();
        _editingItem = null;
        _isAddingFromInlineEditor = true;
        TodoInlineEditor.Title = App.Current.LocalizationService.T("Todo.AddPlaceholder");
        TodoInlineEditor.Text = ViewModel?.InputText ?? string.Empty;
        ApplyEditorVisualStyle();
        TodoInlineEditor.Visibility = Visibility.Visible;
        TodoInlineEditor.FocusEditor(moveCaretToEnd: true);
    }

    private void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAddEditor();
    }

    private void TodoFilterSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectFilter(GetSelectedSegmentFilter());
    }

    private void RedColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Red);
    }

    private void OrangeColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Orange);
    }

    private void YellowColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Yellow);
    }

    private void GreenColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Green);
    }

    private void BlueColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Blue);
    }

    private void PurpleColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Purple);
    }

    private void SelectColorFilter(TodoColorFilter filter)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SetColorFilter(ViewModel.SelectedColorFilter == filter
            ? TodoColorFilter.All
            : filter);
        RefreshFilterButtons();
    }

    private void SelectFilter(TodoFilter filter)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.SelectedFilter == filter)
        {
            RefreshFilterButtons();
            return;
        }

        ViewModel.SetFilter(filter);
        RefreshFilterButtons();
    }

    private void RefreshFilterButtons()
    {
        if (TodoFilterSegmented is null || ViewModel is null)
        {
            return;
        }

        int selectedIndex = GetFilterSegmentIndex(ViewModel.SelectedFilter);
        if (TodoFilterSegmented.SelectedIndex != selectedIndex)
        {
            TodoFilterSegmented.SelectedIndex = selectedIndex;
        }

        ApplyColorFilterButtonState(RedColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Red);
        ApplyColorFilterButtonState(OrangeColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Orange);
        ApplyColorFilterButtonState(YellowColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Yellow);
        ApplyColorFilterButtonState(GreenColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Green);
        ApplyColorFilterButtonState(BlueColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Blue);
        ApplyColorFilterButtonState(PurpleColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Purple);
    }

    private TodoFilter GetSelectedSegmentFilter()
    {
        return TodoFilterSegmented?.SelectedIndex switch
        {
            1 => TodoFilter.Today,
            2 => TodoFilter.Important,
            3 => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static int GetFilterSegmentIndex(TodoFilter filter)
    {
        return filter switch
        {
            TodoFilter.Today => 1,
            TodoFilter.Important => 2,
            TodoFilter.Completed => 3,
            _ => 0
        };
    }

    private void ApplyColorFilterButtonState(Button button, bool isSelected)
    {
        button.Style = (Style)Resources[isSelected
            ? "TodoColorFilterSelectedButtonStyle"
            : "TodoColorFilterButtonStyle"];
    }

    private async void ItemCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
    }

    private async void ImportantItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await DeleteItemAsync(item, element);
    }

    private void TodoListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (HasCopySelection())
        {
            e.Cancel = true;
            _draggedTodoItemId = null;
            return;
        }

        var draggedItem = e.Items.OfType<TodoItemViewModel>().FirstOrDefault();
        _draggedTodoItemId = draggedItem?.Id;
        if (draggedItem is not null)
        {
            DeskBoxDragData.SetText(e.Data, draggedItem.Text, DeskBoxDragData.SourceTodo);
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
    }

    private void TodoItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        item.PropertyChanged -= TodoItem_PropertyChanged;
        item.PropertyChanged += TodoItem_PropertyChanged;
        ApplyTodoItemTooltips(sender, item);
        SetTodoItemHoverState(sender, false);
    }

    private async void TodoListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            _draggedTodoItemId = null;
            return;
        }

        try
        {
            var movedItem = ViewModel.VisibleItems.FirstOrDefault(item =>
                string.Equals(item.Id, _draggedTodoItemId, StringComparison.Ordinal));
            if (movedItem is not null)
            {
                await ViewModel.MoveItemAsync(movedItem.Id, ViewModel.VisibleItems.IndexOf(movedItem));
            }
        }
        finally
        {
            _draggedTodoItemId = null;
        }
    }

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        await HandleExternalTextDragOverAsync(e);
    }

    private async void TodoListView_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        await HandleExternalTextDragOverAsync(e);
    }

    private async Task HandleExternalTextDragOverAsync(DragEventArgs e)
    {
        if (ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = await HasDroppedTodoTextAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = e.AcceptedOperation != DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            string? text = await TryGetDroppedTodoTextAsync(e.DataView);
            if (string.IsNullOrWhiteSpace(text) || ViewModel is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            await ViewModel.AddItemAsync(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Dropped"),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import dropped text: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.DropFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async Task<bool> HasDroppedTodoTextAsync(DataPackageView dataView)
    {
        string? text = await TryGetDroppedTodoTextAsync(dataView);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static async Task<string?> TryGetDroppedTodoTextAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        string? text = await DeskBoxDragData.TryGetTextAsync(dataView);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
            ? text
            : text[..QuickCaptureClipboardService.MaxClipboardTextCharacters].Trim();
    }

    private void TodoListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        if (isCtrlPressed && e.Key == VirtualKey.A)
        {
            SelectAllVisibleTodoItems();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == VirtualKey.C)
        {
            CopySelectedTodoItems();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && HasCopySelection())
        {
            ClearCopySelection();
            e.Handled = true;
        }
    }

    private void TodoItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, true);
    }

    private void TodoItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, false);
    }

    private void TodoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TodoItemViewModel item)
        {
            return;
        }

        if (e.PropertyName is not (nameof(TodoItemViewModel.IsCompleted) or nameof(TodoItemViewModel.ColorMarker)))
        {
            return;
        }

        foreach (var visibleItem in TodoListView.Items)
        {
            if (ReferenceEquals(visibleItem, item) &&
                TodoListView.ContainerFromItem(visibleItem) is FrameworkElement container)
            {
                ApplyTodoItemTooltips(container, item);
                SetTodoItemHoverState(container, false);
                break;
            }
        }
    }

    private static void ApplyTodoItemTooltips(DependencyObject itemRoot, TodoItemViewModel item)
    {
        var localization = App.Current.LocalizationService;

        _ = localization;
    }

    private void TodoItemContent_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _copyTapGeneration++;
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        BeginItemEdit(item);
        e.Handled = true;
    }

    private async void TodoItemContent_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        TodoListView.Focus(FocusState.Programmatic);
        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        if (isCtrlPressed || isShiftPressed)
        {
            _copyTapGeneration++;
            if (isShiftPressed)
            {
                SelectTodoRange(item);
            }
            else
            {
                ToggleTodoSelection(item);
            }

            e.Handled = true;
            return;
        }

        if (HasCopySelection())
        {
            ClearCopySelection();
        }

        long generation = ++_copyTapGeneration;
        await Task.Delay(CopyTapDelayMs);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _copyTapGeneration)
            {
                return;
            }

            CopyTodoItemText(item);
        });

        e.Handled = true;
    }

    private void TodoItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        TodoListView.Focus(FocusState.Programmatic);
        if (!item.IsCopySelected)
        {
            ClearCopySelection();
            item.IsCopySelected = true;
            _copySelectionAnchorId = item.Id;
        }

        CreateItemFlyout(item, element).ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private MenuFlyout CreateItemFlyout(TodoItemViewModel item, FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;
        var selectedItems = GetSelectedCopyItemsInVisibleOrder();

        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            var copySelectedItem = new MenuFlyoutItem
            {
                Text = localization.Format("Todo.Menu.CopySelected", selectedItems.Count),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copySelectedItem.Click += (_, _) => CopySelectedTodoItems(selectedItems);
            flyout.Items.Add(copySelectedItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var editItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Menu.Edit"),
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        editItem.Click += (_, _) => BeginItemEdit(item);
        flyout.Items.Add(editItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Menu.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) => CopyTodoItemText(item);
        flyout.Items.Add(copyItem);

        var completeItem = new MenuFlyoutItem
        {
            Text = item.IsCompleted
                ? localization.T("Todo.Menu.MarkActive")
                : localization.T("Todo.Menu.MarkCompleted"),
            Icon = new FontIcon { Glyph = item.IsCompleted ? "\uE73A" : "\uE73E" }
        };
        completeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
            }
        };
        flyout.Items.Add(completeItem);

        var importantItem = new MenuFlyoutItem
        {
            Text = item.IsImportant
                ? localization.T("Todo.Menu.UnmarkImportant")
                : localization.T("Todo.Menu.MarkImportant"),
            Icon = new FontIcon { Glyph = item.IsImportant ? "\uE735" : "\uE734" }
        };
        importantItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
            }
        };
        flyout.Items.Add(importantItem);

        var colorSubItem = new MenuFlyoutSubItem
        {
            Text = localization.T("Todo.Menu.ColorMarker"),
            Icon = new FontIcon { Glyph = "\uE915" }
        };
        colorSubItem.Items.Add(CreateColorMarkerItem(item, null));
        colorSubItem.Items.Add(new MenuFlyoutSeparator());
        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            colorSubItem.Items.Add(CreateColorMarkerItem(item, colorMarker));
        }

        flyout.Items.Add(colorSubItem);

        var dueSubItem = new MenuFlyoutSubItem
        {
            Text = localization.T("Todo.Menu.DueDate"),
            Icon = new FontIcon { Glyph = "\uE787" }
        };
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Today, localization.T("Todo.Due.Today")));
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
        dueSubItem.Items.Add(new MenuFlyoutSeparator());
        var customDueItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customDueItem.Click += async (_, _) => await PickCustomDueDateAsync(item);
        dueSubItem.Items.Add(customDueItem);
        var clearDueItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Clear"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        clearDueItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(item.Id, TodoDuePreset.Clear);
            }
        };
        dueSubItem.Items.Add(clearDueItem);
        flyout.Items.Add(dueSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => ShowDeleteItemConfirmation(item, anchor);
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateColorMarkerItem(TodoItemViewModel item, string? colorMarker)
    {
        var localization = App.Current.LocalizationService;
        bool isSelected = string.Equals(
            item.ColorMarker,
            TodoItem.NormalizeColorMarker(colorMarker),
            StringComparison.Ordinal);
        var colorItem = new MenuFlyoutItem
        {
            Text = localization.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker)),
            Icon = colorMarker is null
                ? new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE711" }
                : CreateColorMarkerIcon(colorMarker, isSelected)
        };
        colorItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
            }
        };

        return colorItem;
    }

    private static IconElement CreateColorMarkerIcon(string colorMarker, bool isSelected)
    {
        var color = ParseColor(TodoItem.GetColorMarkerHex(colorMarker));
        return new FontIcon
        {
            Glyph = isSelected ? "\uE73E" : "\u25CF",
            FontFamily = isSelected ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
            FontSize = isSelected ? 12 : 10,
            Foreground = new SolidColorBrush(color)
        };
    }

    private MenuFlyoutItem CreateDuePresetItem(TodoItemViewModel item, TodoDuePreset preset, string text)
    {
        var dueItem = new MenuFlyoutItem
        {
            Text = text
        };
        dueItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(item.Id, preset);
            }
        };
        return dueItem;
    }

    private void CopyTodoItemText(TodoItemViewModel item)
    {
        string text = TodoClipboardFormatter.FormatSingleText(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Copied"),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy item {item.Id}: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.CopyFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
    }

    private void CopySelectedTodoItems()
    {
        CopySelectedTodoItems(GetSelectedCopyItemsInVisibleOrder());
    }

    private void CopySelectedTodoItems(IReadOnlyList<TodoItemViewModel> selectedItems)
    {
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1)
        {
            CopyTodoItemText(selectedItems[0]);
            return;
        }

        var localization = App.Current.LocalizationService;
        string text = TodoClipboardFormatter.FormatBatch(selectedItems, localization);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                localization.Format("Todo.CopiedCount", selectedItems.Count),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy {selectedItems.Count} selected items: {ex}");
            ShowUndoToast(
                localization.T("Todo.CopyFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    private void ToggleTodoSelection(TodoItemViewModel item)
    {
        item.IsCopySelected = !item.IsCopySelected;
        _copySelectionAnchorId = item.Id;
    }

    private void SelectTodoRange(TodoItemViewModel item)
    {
        if (ViewModel is null || ViewModel.VisibleItems.Count == 0)
        {
            return;
        }

        int endIndex = ViewModel.VisibleItems.IndexOf(item);
        if (endIndex < 0)
        {
            return;
        }

        int startIndex = endIndex;
        if (!string.IsNullOrWhiteSpace(_copySelectionAnchorId))
        {
            for (int index = 0; index < ViewModel.VisibleItems.Count; index++)
            {
                if (string.Equals(ViewModel.VisibleItems[index].Id, _copySelectionAnchorId, StringComparison.Ordinal))
                {
                    startIndex = index;
                    break;
                }
            }
        }

        int first = Math.Min(startIndex, endIndex);
        int last = Math.Max(startIndex, endIndex);
        ClearCopySelection();
        for (int index = first; index <= last; index++)
        {
            ViewModel.VisibleItems[index].IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems[startIndex].Id;
    }

    private void SelectAllVisibleTodoItems()
    {
        if (ViewModel is null)
        {
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        foreach (var item in ViewModel.VisibleItems)
        {
            item.IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems.FirstOrDefault()?.Id;
    }

    private void ClearCopySelection()
    {
        if (ViewModel is null)
        {
            _copySelectionAnchorId = null;
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        _copySelectionAnchorId = null;
    }

    private bool HasCopySelection()
    {
        return ViewModel?.Items.Any(item => item.IsCopySelected) == true;
    }

    private IReadOnlyList<TodoItemViewModel> GetSelectedCopyItemsInVisibleOrder()
    {
        if (ViewModel is null)
        {
            return [];
        }

        return ViewModel.VisibleItems
            .Where(item => item.IsCopySelected)
            .ToList();
    }

    private Task PickCustomDueDateAsync(TodoItemViewModel item)
    {
        if (ViewModel is null)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        _pendingConfirmFlyout?.Hide();
        _customDueDateItem = item;
        DateTimeOffset dueDate = item.DueDate ?? GetDefaultCustomDueDate();
        CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
        CustomDueDatePicker.Date = dueDate;
        SetCustomDueTime(dueDate);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay.Visibility = Visibility.Visible;
        CustomDueDatePicker.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    private void CustomDueDateCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCustomDueDateOverlay();
    }

    private void CustomDueTimeButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new TimePickerFlyout
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 1,
            Time = _customDueTime
        };
        flyout.TimePicked += (_, args) =>
        {
            _customDueTime = args.NewTime;
            UpdateCustomDueTimeText();
        };

        flyout.ShowAt(CustomDueTimeButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Top
        });
    }

    private async void CustomDueDateSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _customDueDateItem is null)
        {
            CloseCustomDueDateOverlay();
            return;
        }

        string itemId = _customDueDateItem.Id;
        DateTimeOffset selectedDate = CustomDueDatePicker.Date ?? DateTimeOffset.Now;
        DateTimeOffset selectedDueDate = CombineCustomDueDateAndTime(selectedDate);
        CloseCustomDueDateOverlay();
        await ViewModel.SetDueDateAsync(itemId, selectedDueDate);
    }

    private void CloseCustomDueDateOverlay()
    {
        _customDueDateItem = null;
        if (CustomDueDateOverlay is not null)
        {
            CustomDueDateOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCustomDueTime(DateTimeOffset dueDate)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        _customDueTime = new TimeSpan(localDueDate.Hour, localDueDate.Minute, 0);
        UpdateCustomDueTimeText();
    }

    private void UpdateCustomDueTimeText()
    {
        if (CustomDueTimeText is not null)
        {
            CustomDueTimeText.Text = $"{_customDueTime.Hours:00}:{_customDueTime.Minutes:00}";
        }
    }

    private DateTimeOffset CombineCustomDueDateAndTime(DateTimeOffset selectedDate)
    {
        DateTimeOffset localDate = selectedDate.ToLocalTime();
        var localDateTime = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            _customDueTime.Hours,
            _customDueTime.Minutes,
            0,
            DateTimeKind.Local);

        return new DateTimeOffset(localDateTime);
    }

    private static DateTimeOffset GetDefaultCustomDueDate()
    {
        DateTime today = DateTime.Now.Date;
        return new DateTimeOffset(new DateTime(today.Year, today.Month, today.Day, 23, 59, 0, DateTimeKind.Local));
    }

    private async Task DeleteItemAsync(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
                App.Current.LocalizationService.T("Common.Delete"),
                async () => await ViewModel.DeleteItemAsync(item.Id));
            return;
        }

        await ViewModel.DeleteItemAsync(item.Id);
    }

    private void ShowDeleteItemConfirmation(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.ConfirmBeforeDelete)
        {
            _ = ViewModel.DeleteItemAsync(item.Id);
            return;
        }

        ShowTodoConfirmMenu(
            anchor,
            App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
            App.Current.LocalizationService.T("Common.Delete"),
            async () => await ViewModel.DeleteItemAsync(item.Id));
    }

    private void BeginItemEdit(TodoItemViewModel item)
    {
        ClearCopySelection();
        CloseCustomDueDateOverlay();
        _isAddingFromInlineEditor = false;
        _editingItem = item;
        TodoInlineEditor.Title = App.Current.LocalizationService.T("Todo.Menu.Edit");
        TodoInlineEditor.Text = item.Text;
        ApplyEditorVisualStyle();
        TodoInlineEditor.Visibility = Visibility.Visible;
        TodoInlineEditor.FocusEditor(moveCaretToEnd: true);
    }

    private async void TodoEditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveTodoEditAsync();
    }

    private void TodoEditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseTodoEdit();
    }

    private async void TodoEditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CloseTodoEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            await SaveTodoEditAsync();
            e.Handled = true;
        }
    }

    private async Task SaveTodoEditAsync()
    {
        if (ViewModel is null)
        {
            CloseTodoEdit();
            return;
        }

        if (_isAddingFromInlineEditor)
        {
            ViewModel.InputText = TodoInlineEditor.Text;
            var addedItem = await ViewModel.AddInputAsync();
            if (addedItem is null)
            {
                TodoEditTextBox.Focus(FocusState.Programmatic);
                TodoEditTextBox.SelectAll();
                return;
            }

            CloseTodoEdit();
            AddTextBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseTodoEdit();
            return;
        }

        bool updated = await ViewModel.UpdateItemTextAsync(item.Id, TodoInlineEditor.Text);
        if (!updated)
        {
            TodoEditTextBox.Focus(FocusState.Programmatic);
            TodoEditTextBox.SelectAll();
            return;
        }

        CloseTodoEdit();
    }

    private void CloseTodoEdit()
    {
        _editingItem = null;
        _isAddingFromInlineEditor = false;
        if (TodoInlineEditor is null)
        {
            return;
        }

        TodoInlineEditor.Visibility = Visibility.Collapsed;
        TodoInlineEditor.Text = string.Empty;
        TodoInlineEditor.Title = App.Current.LocalizationService.T("Todo.Menu.Edit");
    }

    private static void SetTodoItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "TodoItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "TodoItemActionHost") is { } actions)
        {
            actions.Opacity = isHovered ? 1 : 0;
            actions.IsHitTestVisible = isHovered;
            actions.Background = new SolidColorBrush(WithAlpha(
                BuildAccentSurfaceColor(
                    isDark,
                    accentColor,
                    isDark ? ColorHelper.FromArgb(0xFF, 0x1E, 0x23, 0x29) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    accentMix: isDark ? 0.18 : 0.08,
                    overlayMix: isDark ? 0.03 : 0.02),
                0xFF));
            actions.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x4A : (byte)0x30));
            actions.BorderThickness = new Thickness(1);
            foreach (var button in FindVisualChildren<Button>(actions))
            {
                ApplyActionButtonTheme(button, isDark, accentColor);
            }
        }

        bool isCompleted = itemRoot is FrameworkElement { DataContext: TodoItemViewModel { IsCompleted: true } };
        if (FindVisualChild<Border>(itemRoot, "TodoCompletionBox") is { } completionBox)
        {
            completionBox.Background = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(Colors.Transparent);
            completionBox.BorderBrush = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(isHovered
                    ? WithAlpha(accentColor, isDark ? (byte)0xFF : (byte)0xE8)
                    : isDark
                        ? WithAlpha(accentColor, 0xD8)
                        : WithAlpha(accentColor, 0xB8));
        }
    }

    private void ApplyEditorVisualStyle()
    {
        if (TodoInlineEditor is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        TodoInlineEditor.OverlaySurface.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        TodoInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        TodoEditTextBox.Background = new SolidColorBrush(GetNeutralInputSurfaceColor(isDark));
        TodoEditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoEditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);

        CustomDueDateOverlay.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        CustomDueDateOverlay.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        CustomDueDateOverlay.BorderThickness = new Thickness(0.8);
    }

    private static Windows.UI.Color GetNeutralOverlaySurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x2A, 0x30, 0x38)
            : ColorHelper.FromArgb(0xFF, 0xFB, 0xFC, 0xFD);
    }

    private static Windows.UI.Color GetNeutralInputSurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x22, 0x28, 0x30)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private static Brush GetNeutralOverlayBorderBrush(bool isDark)
    {
        return GetBrushResourceOrFallback(
            "CardStrokeColorDefaultBrush",
            isDark ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    private static void ApplyActionButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var foreground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF2 : (byte)0xE2));

        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length != 6 ||
            !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) ||
            !byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) ||
            !byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return Colors.Gray;
        }

        return ColorHelper.FromArgb(0xFF, red, green, blue);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed &&
                (name is null || string.Equals(typed.Name, name, StringComparison.Ordinal)))
            {
                return typed;
            }

            if (FindVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static FrameworkElement? FindTodoItemContainer(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Grid grid &&
                grid.DataContext is TodoItemViewModel)
            {
                return grid;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement anchor)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearCompletedConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearCompleted"),
                async () => await ViewModel.ClearCompletedAsync());
            return;
        }

        await ViewModel.ClearCompletedAsync();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.UndoLastActionAsync();
            ShowUndoToast(App.Current.LocalizationService.T("Common.Undone"));
        }
    }

    private void DismissUndoButton_Click(object sender, RoutedEventArgs e)
    {
        HideUndoToast(clearUndo: true);
    }

    private void ShowUndoToast(
        string text,
        string? actionText = null,
        int durationMs = UndoToastMs,
        bool clearUndoOnHide = true)
    {
        long generation = ++_undoToastGeneration;
        UndoToastText.Text = text;
        UndoToastActionButton.Content = actionText;
        UndoToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        UndoToast.Opacity = 1;
        _ = HideUndoToastAfterDelayAsync(generation, durationMs, clearUndoOnHide);
    }

    private async Task HideUndoToastAfterDelayAsync(long generation, int durationMs, bool clearUndo)
    {
        await Task.Delay(durationMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _undoToastGeneration)
            {
                HideUndoToast(clearUndo);
            }
        });
    }

    private void HideUndoToast(bool clearUndo)
    {
        _undoToastGeneration++;
        UndoToast.Opacity = 0;
        UndoToast.IsHitTestVisible = false;
        UndoToastActionButton.Visibility = Visibility.Collapsed;
        if (clearUndo)
        {
            ViewModel?.DismissUndo();
        }
    }

    public void ShowClearAllConfirmation(FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearAllConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearAll"),
                async () => await ViewModel.ClearAllAsync());
            return;
        }

        _ = ViewModel.ClearAllAsync();
    }

    private void ShowTodoConfirmMenu(
        FrameworkElement anchor,
        string title,
        string actionText,
        Func<Task> confirmedAction)
    {
        _pendingConfirmFlyout?.Hide();

        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            title,
            actionText,
            confirmedAction);
        _pendingConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pendingConfirmFlyout, flyout))
            {
                _pendingConfirmFlyout = null;
            }
        };
        flyout.ShowAt(anchor);
    }

    private static string GetDeleteConfirmPreviewText(string text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "Task";
        }

        return normalized.Length <= 34 ? normalized : $"{normalized[..34].Trim()}...";
    }
}
