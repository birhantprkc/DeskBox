using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class MusicWidgetContentAdapter : IWidgetContent, IDisposable
{
    private readonly Func<MusicWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;
    private bool _isDisposed;

    public MusicWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        MusicSessionService? musicSessionService = null,
        Func<MusicWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Music)
        {
            throw new ArgumentException("Music content requires a Music widget config.", nameof(config));
        }

        Config = config;
        ViewModel = new MusicWidgetViewModel(
            config,
            musicSessionService ?? new MusicSessionService(),
            localizationService,
            settingsService);
        _viewFactory = viewFactory ?? (vm => new MusicWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View
    {
        get
        {
            if (_view is null && !_isDisposed)
            {
                _view = _viewFactory(ViewModel);
            }
            return _view!;
        }
    }

    public MusicWidgetViewModel ViewModel { get; }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public void OnDeactivated()
    {
        ViewModel.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
        if (_view is MusicWidgetContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Detach the View's PropertyChanged subscription by clearing the
        // ViewModel reference.  The setter removes the event handler.
        if (_view is MusicWidgetContent musicContent)
        {
            musicContent.ViewModel = null;
        }

        // Dispose the ViewModel first (stops timers, detaches service events).
        ViewModel.Dispose();

        // Clear the VisualizerBars collection so the MusicBarViewModel
        // instances (and their EqualizerSegments) become eligible for GC.
        // Without this, 28+ bar objects with 10 segments each stay alive
        // in the collection even after Dispose.
        ViewModel.VisualizerBars.Clear();

        _view = null;
    }
}
