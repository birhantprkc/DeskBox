using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;

namespace DeskBox.ViewModels;

public sealed class TodoStepViewModel : ObservableObject
{
    private readonly TodoStep _step;
    private string _text;
    private bool _isCompleted;

    public TodoStepViewModel(TodoStep step)
    {
        _step = step;
        _text = step.Text;
        _isCompleted = step.IsCompleted;
    }

    public TodoStep Step => _step;

    public string Id => _step.Id;

    public string Text
    {
        get => _text;
        internal set
        {
            if (SetProperty(ref _text, value))
            {
                _step.Text = value;
                OnPropertyChanged(nameof(TextDecorations));
                OnPropertyChanged(nameof(ContentOpacity));
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
                _step.IsCompleted = value;
                OnPropertyChanged(nameof(TextDecorations));
                OnPropertyChanged(nameof(ContentOpacity));
            }
        }
    }

    public int SortOrder
    {
        get => _step.SortOrder;
        internal set
        {
            if (_step.SortOrder != value)
            {
                _step.SortOrder = value;
                OnPropertyChanged();
            }
        }
    }

    public Windows.UI.Text.TextDecorations TextDecorations => IsCompleted
        ? Windows.UI.Text.TextDecorations.Strikethrough
        : Windows.UI.Text.TextDecorations.None;

    public double ContentOpacity => IsCompleted ? 0.58 : 1;
}
