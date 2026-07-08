using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.ViewModels;

public sealed partial class TodoItemViewModel : ObservableObject
{
    private readonly TodoItem _item;
    private readonly LocalizationService? _localizationService;
    private string _text;
    private bool _isCompleted;
    private bool _isImportant;
    private string? _colorMarker;
    private DateTimeOffset? _dueDate;
    private DateTimeOffset? _completedAt;
    private bool _isEditing;
    private bool _isCopySelected;
    private string _editText = string.Empty;

    public TodoItemViewModel(TodoItem item, LocalizationService? localizationService = null)
    {
        _item = item;
        _localizationService = localizationService;
        _text = item.Text;
        _isCompleted = item.IsCompleted;
        _isImportant = item.IsImportant;
        _colorMarker = TodoItem.NormalizeColorMarker(item.ColorMarker);
        item.ColorMarker = _colorMarker;
        _dueDate = item.DueDate;
        _completedAt = item.CompletedAt;
    }

    public TodoItem Item => _item;

    public string Id => _item.Id;

    public int SortOrder
    {
        get => _item.SortOrder;
        internal set
        {
            if (_item.SortOrder != value)
            {
                _item.SortOrder = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTimeOffset CreatedAt => _item.CreatedAt;

    public DateTimeOffset UpdatedAt
    {
        get => _item.UpdatedAt;
        internal set
        {
            if (_item.UpdatedAt != value)
            {
                _item.UpdatedAt = value;
                OnPropertyChanged();
            }
        }
    }

    public string Text
    {
        get => _text;
        internal set
        {
            if (SetProperty(ref _text, value))
            {
                _item.Text = value;
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        internal set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                _item.IsCompleted = value;
                OnPropertyChanged(nameof(CompletionGlyph));
                OnPropertyChanged(nameof(CompletionGlyphOpacity));
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                OnPropertyChanged(nameof(ContentOpacity));
                OnPropertyChanged(nameof(TextDecorations));
            }
        }
    }

    public bool IsImportant
    {
        get => _isImportant;
        internal set
        {
            if (SetProperty(ref _isImportant, value))
            {
                _item.IsImportant = value;
                OnPropertyChanged(nameof(ImportantGlyph));
            }
        }
    }

    public string? ColorMarker
    {
        get => _colorMarker;
        internal set
        {
            string? normalizedValue = TodoItem.NormalizeColorMarker(value);
            if (SetProperty(ref _colorMarker, normalizedValue))
            {
                _item.ColorMarker = normalizedValue;
                OnPropertyChanged(nameof(HasRedMarker));
                OnPropertyChanged(nameof(HasColorMarker));
                OnPropertyChanged(nameof(ColorMarkerVisibility));
                OnPropertyChanged(nameof(ColorMarkerBrush));
                OnPropertyChanged(nameof(MarkerGlyph));
            }
        }
    }

    public DateTimeOffset? DueDate
    {
        get => _dueDate;
        internal set
        {
            if (SetProperty(ref _dueDate, value))
            {
                _item.DueDate = value;
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(DueStatusVisibility));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
            }
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        internal set
        {
            if (SetProperty(ref _completedAt, value))
            {
                _item.CompletedAt = value;
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        internal set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(TextVisibility));
                OnPropertyChanged(nameof(EditVisibility));
            }
        }
    }

    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    public bool IsCopySelected
    {
        get => _isCopySelected;
        set
        {
            if (SetProperty(ref _isCopySelected, value))
            {
                OnPropertyChanged(nameof(CopySelectionVisibility));
            }
        }
    }

    public string ImportantGlyph => IsImportant ? "\uE735" : "\uE734";

    public bool HasRedMarker => string.Equals(ColorMarker, TodoItem.RedColorMarker, StringComparison.Ordinal);

    public bool HasColorMarker => ColorMarker is not null;

    public Visibility ColorMarkerVisibility => HasColorMarker ? Visibility.Visible : Visibility.Collapsed;

    public Brush ColorMarkerBrush => new SolidColorBrush(ParseColor(TodoItem.GetColorMarkerHex(ColorMarker)));

    public string MarkerGlyph => HasRedMarker ? "\uE915" : "\uE915";

    public string CompletionGlyph => IsCompleted ? "\uE73E" : string.Empty;

    public double CompletionGlyphOpacity => IsCompleted ? 1d : 0d;

    public double ContentOpacity => IsCompleted ? 0.62d : 1d;

    public Windows.UI.Text.TextDecorations TextDecorations => IsCompleted
        ? Windows.UI.Text.TextDecorations.Strikethrough
        : Windows.UI.Text.TextDecorations.None;

    public bool IsOverdue => DueDate is { } dueDate &&
                             dueDate.ToLocalTime() < DateTimeOffset.Now &&
                             !IsCompleted;

    public string DueStatusText
    {
        get
        {
            if (DueDate is not { } dueDate)
            {
                return string.Empty;
            }

            DateTimeOffset localDueDate = dueDate.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            var due = localDueDate.Date;
            string formattedDue = FormatDueDateTime(localDueDate);
            string formattedTime = FormatDueTime(localDueDate);
            string statusText;

            if (due == today)
            {
                statusText = Format("Todo.Due.TodayAt", formattedTime);
            }
            else if (due == today.AddDays(1))
            {
                statusText = Format("Todo.Due.TomorrowAt", formattedTime);
            }
            else
            {
                statusText = Format("Todo.Due.Date", formattedDue);
            }

            return IsOverdue
                ? $"{statusText} \u00B7 {LocalizedText("Todo.Due.OverdueSuffix")}"
                : statusText;
        }
    }

    public Visibility DueStatusVisibility => DueDate is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DueStatusNormalVisibility => DueDate is not null && !IsOverdue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DueStatusOverdueVisibility => IsOverdue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CopySelectionVisibility => IsCopySelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TextVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EditVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    internal void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    internal void CancelEdit()
    {
        EditText = Text;
        IsEditing = false;
    }

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DueStatusText));
        OnPropertyChanged(nameof(DueStatusNormalVisibility));
        OnPropertyChanged(nameof(DueStatusOverdueVisibility));
    }

    private string LocalizedText(string key)
    {
        return _localizationService?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private string Format(string key, params object[] args)
    {
        return _localizationService?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static string FormatDueDateTime(DateTimeOffset dueDate)
    {
        return dueDate.Second == 0
            ? dueDate.ToString("yyyy/M/d HH:mm")
            : dueDate.ToString("yyyy/M/d HH:mm:ss");
    }

    private static string FormatDueTime(DateTimeOffset dueDate)
    {
        return dueDate.Second == 0
            ? dueDate.ToString("HH:mm")
            : dueDate.ToString("HH:mm:ss");
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
}
