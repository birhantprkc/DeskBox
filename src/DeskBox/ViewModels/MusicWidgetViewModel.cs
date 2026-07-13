using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel : ObservableObject, IDisposable
{
    private const int MaxTransientEmptyInfoRetries = 4;
    private const int ProgressRefreshMs = 1000;

    private readonly MusicSessionService _musicSessionService;
    private readonly MusicVolumeService _musicVolumeService;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly WidgetConfig _config;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;

    private string? _preferredSessionId;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _sourceAppUserModelId = string.Empty;
    private string _sourceDisplayName = string.Empty;
    private MusicPlaybackState _playbackState = MusicPlaybackState.Unknown;
    private TimeSpan _position;
    private TimeSpan _duration;
    private BitmapImage? _thumbnailImage;
    private bool _canPlay;
    private bool _canPause;
    private bool _canGoPrevious;
    private bool _canGoNext;
    private bool _canSeek;
    private bool _canChangeShuffle;
    private bool _canChangeRepeat;
    private bool _isRefreshing;
    private int _timelineRefreshGeneration;
    private int _playbackRefreshGeneration;
    private int _coverGeneration;
    private string? _lastCoverSignature;
    private string? _coverRetrySignature;
    private int _coverRetryCount;
    private int _transientEmptyInfoRetryCount;
    private int _transientEmptyInfoGeneration;
    private bool _fullRefreshPending;
    private bool _isWindowVisible;
    private bool _isSeeking;
    private bool _isChangingPlaybackMode;
    private bool _isChangingSystemVolume;
    private bool _isChangingSessionVolume;
    private bool _isRefreshingVolume;
    private double? _pendingSystemVolume;
    private double? _pendingSessionVolume;
    private bool _isDisposed;
    private DateTimeOffset _lastPositionSyncAt;
    private TimeSpan _lastSyncedPosition;
    private double _textSize = SettingsService.DefaultTextSize;
    private double _seekValue;
    private double _systemVolume = 0.5;
    private double _sessionVolume = 0.5;
    private bool _hasSessionVolume;
    private bool _useArtworkBackdrop = true;
    private bool _enableCoverHoverMotion = true;
    private Color _artworkColor = AccentColorHelper.DefaultAccentColor;
    private bool _hasArtworkColor;
    private MusicPlaybackMode _playbackMode = MusicPlaybackMode.Normal;

    public MusicWidgetViewModel(
        WidgetConfig config,
        MusicSessionService musicSessionService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null,
        MusicVolumeService? musicVolumeService = null)
    {
        _config = config;
        _musicSessionService = musicSessionService;
        _musicVolumeService = musicVolumeService ?? new MusicVolumeService();
        _localizationService = localizationService;
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();

        if (_settingsService is not null)
        {
            _textSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
            ApplyMusicSettings(_settingsService.Settings);
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;

        if (_dispatcherQueue is not null)
        {
            _progressTimer = _dispatcherQueue.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(ProgressRefreshMs);
            _progressTimer.IsRepeating = true;
            _progressTimer.Tick += ProgressTimer_Tick;

        }

        AttachServiceEvents();
    }

    private static Microsoft.UI.Dispatching.DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public ObservableCollection<string> SessionDisplayNames { get; } = [];

    public ObservableCollection<string> SessionIds { get; } = [];

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Music.Title")
        : _config.Name;

    public double WidgetX => _config.X;

    public double WidgetY => _config.Y;

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? _localizationService.T("Music.EmptyTitle") : _title;
        private set => SetProperty(ref _title, value);
    }

    public string Artist
    {
        get => string.IsNullOrWhiteSpace(_artist) ? SourceDisplayName : _artist;
        private set => SetProperty(ref _artist, value);
    }

    public string Album
    {
        get => _album;
        private set => SetProperty(ref _album, value);
    }

    public string SourceDisplayName
    {
        get => string.IsNullOrWhiteSpace(_sourceDisplayName) ? _localizationService.T("Music.SourceUnknown") : _sourceDisplayName;
        private set => SetProperty(ref _sourceDisplayName, value);
    }

    public bool UseArtworkBackdrop
    {
        get => _useArtworkBackdrop;
        private set
        {
            if (SetProperty(ref _useArtworkBackdrop, value))
            {
                OnPropertyChanged(nameof(ArtworkBackdropVisibility));
                RaiseMusicAccentPropertiesChanged();
            }
        }
    }

    public bool EnableCoverHoverMotion
    {
        get => _enableCoverHoverMotion;
        private set => SetProperty(ref _enableCoverHoverMotion, value);
    }

    public MusicPlaybackState PlaybackState
    {
        get => _playbackState;
        private set
        {
            if (SetProperty(ref _playbackState, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayPauseGlyph));
                OnPropertyChanged(nameof(PlayIconVisibility));
                OnPropertyChanged(nameof(PauseIconVisibility));
                OnPropertyChanged(nameof(PlayPauseTooltip));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (SetProperty(ref _position, value))
            {
                if (!_isSeeking)
                {
                    SeekValue = Math.Clamp(value.TotalSeconds, 0, SeekMaximum);
                }

                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(SeekMaximum));
                OnPropertyChanged(nameof(HasSeekableTimeline));
                OnPropertyChanged(nameof(CanInteractWithProgress));
                OnPropertyChanged(nameof(DurationText));
                if (!_isSeeking)
                {
                    SeekValue = Math.Clamp(Position.TotalSeconds, 0, SeekMaximum);
                }
            }
        }
    }

    public BitmapImage? ThumbnailImage
    {
        get => _thumbnailImage;
        private set
        {
            if (SetProperty(ref _thumbnailImage, value))
            {
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }
    }

    public double SeekValue
    {
        get => _seekValue;
        set => SetProperty(ref _seekValue, value);
    }

    public double SeekMaximum => Math.Max(1, Duration.TotalSeconds);

    public double TextSize
    {
        get => _textSize;
        private set
        {
            if (SetProperty(ref _textSize, value))
            {
                OnPropertyChanged(nameof(TitleTextSize));
                OnPropertyChanged(nameof(MinimalTitleTextSize));
                OnPropertyChanged(nameof(SecondaryTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 3, TextSize + 4);

    public double MinimalTitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, TextSize + 1.5);

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);

    public bool IsPlaying => PlaybackState == MusicPlaybackState.Playing;

    public bool HasSession => !string.IsNullOrWhiteSpace(_sourceDisplayName) ||
                              !string.IsNullOrWhiteSpace(_title) ||
                              PlaybackState is MusicPlaybackState.Playing or MusicPlaybackState.Paused;

    public bool CanPlayPause => CanPlay || CanPause;

    public bool CanPlay
    {
        get => _canPlay;
        private set
        {
            if (SetProperty(ref _canPlay, value))
            {
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    public bool CanPause
    {
        get => _canPause;
        private set
        {
            if (SetProperty(ref _canPause, value))
            {
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    public bool CanGoPrevious
    {
        get => _canGoPrevious;
        private set => SetProperty(ref _canGoPrevious, value);
    }

    public bool CanGoNext
    {
        get => _canGoNext;
        private set => SetProperty(ref _canGoNext, value);
    }

    public bool CanSeek
    {
        get => _canSeek;
        private set
        {
            if (SetProperty(ref _canSeek, value))
            {
                OnPropertyChanged(nameof(HasSeekableTimeline));
                OnPropertyChanged(nameof(CanInteractWithProgress));
            }
        }
    }

    public bool CanChangeShuffle
    {
        get => _canChangeShuffle;
        private set
        {
            if (SetProperty(ref _canChangeShuffle, value))
            {
                OnPropertyChanged(nameof(CanChangePlaybackMode));
            }
        }
    }

    public bool CanChangeRepeat
    {
        get => _canChangeRepeat;
        private set
        {
            if (SetProperty(ref _canChangeRepeat, value))
            {
                OnPropertyChanged(nameof(CanChangePlaybackMode));
            }
        }
    }

    public bool CanChangePlaybackMode => CanChangeShuffle || CanChangeRepeat;

    public MusicPlaybackMode PlaybackMode
    {
        get => _playbackMode;
        private set
        {
            if (SetProperty(ref _playbackMode, value))
            {
                OnPropertyChanged(nameof(PlaybackModeGlyph));
                OnPropertyChanged(nameof(PlaybackModeTooltip));
                OnPropertyChanged(nameof(PlaybackModeOpacity));
            }
        }
    }

    public bool HasSeekableTimeline => Duration > TimeSpan.Zero && SeekMaximum > 1;

    public bool CanInteractWithProgress => CanSeek && HasSeekableTimeline;

    public Visibility ThumbnailVisibility => ThumbnailImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ThumbnailPlaceholderVisibility => ThumbnailImage is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SessionPickerVisibility => SessionIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ArtworkBackdropVisibility => UseArtworkBackdrop ? Visibility.Visible : Visibility.Collapsed;

    public CornerRadius ArtworkBackdropCornerRadius => new(GetArtworkBackdropCornerRadius());

    public Color ArtworkBackdropStartColor => Color.FromArgb(0x5A, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropMidColor => Color.FromArgb(0x20, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropEndColor => Color.FromArgb(0x00, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public SolidColorBrush MusicAccentBrush => new(GetMusicAccentColor());

    public SolidColorBrush PlayPauseButtonBackgroundBrush => new(GetMusicAccentColor());

    public string StatusText => PlaybackState switch
    {
        MusicPlaybackState.Playing => _localizationService.T("Music.Status.Playing"),
        MusicPlaybackState.Paused => _localizationService.T("Music.Status.Paused"),
        MusicPlaybackState.Stopped => _localizationService.T("Music.Status.Stopped"),
        _ => HasSession ? _localizationService.T("Music.Status.Ready") : _localizationService.T("Music.Status.NoSession")
    };

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE102";

    public Visibility PlayIconVisibility => IsPlaying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PauseIconVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;

    public string PlayPauseTooltip => IsPlaying
        ? _localizationService.T("Music.Control.Pause")
        : _localizationService.T("Music.Control.Play");

    public string PreviousTooltip => _localizationService.T("Music.Control.Previous");

    public string NextTooltip => _localizationService.T("Music.Control.Next");

    public string PlaybackModeGlyph => PlaybackMode switch
    {
        MusicPlaybackMode.Shuffle => "\uE8B1",
        MusicPlaybackMode.Repeat => "\uE8EE",
        _ => "\uE8AB"
    };

    public string PlaybackModeTooltip => PlaybackMode switch
    {
        MusicPlaybackMode.Shuffle => _localizationService.T("Music.Control.Mode.Shuffle"),
        MusicPlaybackMode.Repeat => _localizationService.T("Music.Control.Mode.Repeat"),
        _ => _localizationService.T("Music.Control.Mode.Normal")
    };

    public double PlaybackModeOpacity => PlaybackMode == MusicPlaybackMode.Normal ? 0.74 : 1.0;

    public string VolumeTooltip => _localizationService.T("Music.Control.Volume");

    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (SetProperty(ref _systemVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SystemVolumeText));
            }
        }
    }

    public double SessionVolume
    {
        get => _sessionVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (SetProperty(ref _sessionVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SessionVolumeText));
            }
        }
    }

    public bool HasSessionVolume
    {
        get => _hasSessionVolume;
        private set
        {
            if (SetProperty(ref _hasSessionVolume, value))
            {
                OnPropertyChanged(nameof(SessionVolumeSliderOpacity));
                OnPropertyChanged(nameof(SessionVolumeText));
            }
        }
    }

    public double SessionVolumeSliderOpacity => HasSessionVolume ? 1.0 : 0.45;

    public string SystemVolumeLabel => _localizationService.T("Music.Volume.System");

    public string SessionVolumeLabel => _localizationService.T("Music.Volume.App");

    public string SystemVolumeText => FormatPercent(SystemVolume);

    public double VolumeTextSize => CaptionTextSize + 2;

    public string SessionVolumeText => HasSessionVolume
        ? FormatPercent(SessionVolume)
        : _localizationService.T("Music.Volume.Unavailable");

    public string RefreshTooltip => _localizationService.T("Common.Refresh");

    public string PositionText => FormatTime(Position);

    public string DurationText => Duration > TimeSpan.Zero ? FormatTime(Duration) : "--:--";

    public int SelectedSessionIndex
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_preferredSessionId))
            {
                return 0;
            }

            int index = SessionIds.IndexOf(_preferredSessionId);
            return index < 0 ? 0 : index;
        }
    }

    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await RefreshAsync();
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowVisible)
        {
            _progressTimer?.Start();
        }
        UpdateMusicTimerDiagnostics();
    }

    public async Task RefreshAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isRefreshing)
        {
            _fullRefreshPending = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            do
            {
                _fullRefreshPending = false;
                try
                {
                    await _musicSessionService.InitializeAsync();
                    if (_isDisposed)
                    {
                        return;
                    }

                    RefreshSessionList();
                    var info = await _musicSessionService.GetCurrentSessionInfoAsync(_preferredSessionId);
                    if (_isDisposed)
                    {
                        return;
                    }

                    await ApplyInfoAsync(info);
                }
                catch (Exception ex)
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    App.Log($"[MusicWidget] Refresh failed: {ex}");
                    await ApplyInfoAsync(null);
                }
            }
            while (_fullRefreshPending && !_isDisposed);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public async Task SelectSessionAsync(int index)
    {
        if (_isDisposed)
        {
            return;
        }

        if (index < 0 || index >= SessionIds.Count)
        {
            return;
        }

        _preferredSessionId = SessionIds[index];
        await _musicSessionService.TrySetPreferredSessionAsync(_preferredSessionId);
        OnPropertyChanged(nameof(SelectedSessionIndex));
        await RefreshAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsPlaying)
        {
            await _musicSessionService.TryPauseAsync(_preferredSessionId);
        }
        else
        {
            await _musicSessionService.TryPlayAsync(_preferredSessionId);
        }

        await RefreshAsync();
        await Task.Delay(180);
        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryPreviousAsync(_preferredSessionId);
        await RefreshAsync();
    }

    public async Task NextAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryNextAsync(_preferredSessionId);
        await RefreshAsync();
    }

    public async Task CyclePlaybackModeAsync()
    {
        if (_isDisposed || _isChangingPlaybackMode || !CanChangePlaybackMode)
        {
            return;
        }

        _isChangingPlaybackMode = true;
        try
        {
            MusicPlaybackMode requestedMode = GetNextPlaybackMode();
            bool didChange = await _musicSessionService.TryChangePlaybackModeAsync(_preferredSessionId, requestedMode);
            if (!didChange)
            {
                App.Log($"[MusicWidget] Playback mode request was rejected. requested={requestedMode}");
            }

            await RefreshAsync();
            await Task.Delay(180);
            await RefreshAsync();
        }
        finally
        {
            _isChangingPlaybackMode = false;
        }
    }

    public async Task RefreshVolumeAsync()
    {
        if (_isDisposed || _isRefreshingVolume)
        {
            return;
        }

        _isRefreshingVolume = true;
        try
        {
            var snapshot = await _musicVolumeService.GetVolumeAsync(_sourceAppUserModelId, SourceDisplayName);
            if (_isDisposed)
            {
                return;
            }

            SystemVolume = snapshot.SystemVolume;
            SessionVolume = snapshot.SessionVolume;
            HasSessionVolume = snapshot.HasSessionVolume;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Refresh volume failed: {ex.Message}");
            HasSessionVolume = false;
        }
        finally
        {
            _isRefreshingVolume = false;
        }
    }

    public async Task RefreshSystemVolumeAsync()
    {
        if (_isDisposed || _isRefreshingVolume)
        {
            return;
        }

        _isRefreshingVolume = true;
        try
        {
            double systemVolume = await _musicVolumeService.GetSystemMasterVolumeAsync();
            if (!_isDisposed)
            {
                SystemVolume = systemVolume;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Refresh system volume failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingVolume = false;
        }
    }

    public async Task SetSystemVolumeAsync(double volume)
    {
        if (_isDisposed)
        {
            return;
        }

        SystemVolume = volume;
        _pendingSystemVolume = SystemVolume;
        if (_isChangingSystemVolume)
        {
            return;
        }

        _isChangingSystemVolume = true;
        try
        {
            while (_pendingSystemVolume.HasValue)
            {
                double requestedVolume = _pendingSystemVolume.Value;
                _pendingSystemVolume = null;

                bool didChange = await _musicVolumeService.TrySetSystemMasterVolumeAsync(requestedVolume);
                if (!didChange)
                {
                    App.Log("[MusicWidget] System volume request was rejected.");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Set system volume failed: {ex.Message}");
        }
        finally
        {
            _isChangingSystemVolume = false;
        }
    }

    public async Task SetSessionVolumeAsync(double volume)
    {
        if (_isDisposed || !HasSessionVolume)
        {
            return;
        }

        SessionVolume = volume;
        _pendingSessionVolume = SessionVolume;
        if (_isChangingSessionVolume)
        {
            return;
        }

        _isChangingSessionVolume = true;
        try
        {
            while (_pendingSessionVolume.HasValue)
            {
                double requestedVolume = _pendingSessionVolume.Value;
                _pendingSessionVolume = null;

                bool didChange = await _musicVolumeService.TrySetSessionVolumeAsync(_sourceAppUserModelId, SourceDisplayName, requestedVolume);
                if (!didChange)
                {
                    App.Log("[MusicWidget] Session volume request was rejected.");
                    HasSessionVolume = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Set session volume failed: {ex.Message}");
            HasSessionVolume = false;
        }
        finally
        {
            _isChangingSessionVolume = false;
        }
    }

    public void BeginSeek()
    {
        _isSeeking = true;
    }

    public async Task CommitSeekAsync()
    {
        if (_isDisposed || !_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        if (!CanInteractWithProgress)
        {
            SeekValue = Math.Clamp(Position.TotalSeconds, 0, SeekMaximum);
            return;
        }

        var targetPosition = TimeSpan.FromSeconds(SeekValue);
        bool didSeek = await _musicSessionService.TrySeekAsync(_preferredSessionId, targetPosition);
        if (!didSeek)
        {
            App.Log("[MusicWidget] Seek request was rejected by the active media session.");
            await RefreshAsync();
            return;
        }

        Position = targetPosition;
        _lastSyncedPosition = targetPosition;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
        await Task.Delay(260);
        await RefreshAsync();
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyMusicSettings(_settingsService.Settings);
        RaiseMusicAccentPropertiesChanged();
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowVisible)
        {
            _progressTimer?.Start();
            UpdateMusicTimerDiagnostics();
        }
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Stops all timers when hidden to avoid unnecessary CPU/GPU usage,
    /// and restarts them when the window becomes visible again.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowVisible = visible;
        if (visible)
        {
            _progressTimer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _progressTimer?.Stop();
        }
        UpdateMusicTimerDiagnostics();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _fullRefreshPending = false;
        ++_coverGeneration;
        ++_transientEmptyInfoGeneration;
        ++_timelineRefreshGeneration;
        ++_playbackRefreshGeneration;
        DetachServiceEvents();
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= ProgressTimer_Tick;
        }

        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _musicSessionService.Dispose();

        // Release the thumbnail BitmapImage reference so the native WIC
        // texture can be reclaimed by GC rather than lingering until gen2.
        ThumbnailImage = null;

        PerformanceLogger.ActiveMusicTimerCount = 0;
    }

    private async Task ApplyInfoAsync(MusicSessionInfo? info)
    {
        if (ShouldDeferEmptyInfo(info))
        {
            ScheduleTransientEmptyInfoRetry();
            return;
        }

        ResetTransientEmptyInfoRetry();
        if (info is null)
        {
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
            _sourceAppUserModelId = string.Empty;
            SourceDisplayName = string.Empty;
            PlaybackState = MusicPlaybackState.Unknown;
            Position = TimeSpan.Zero;
        _lastSyncedPosition = TimeSpan.Zero;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
            Duration = TimeSpan.Zero;
            ThumbnailImage = null;
            _lastCoverSignature = null;
            _coverRetrySignature = null;
            _coverRetryCount = 0;
            SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
            CanPlay = false;
            CanPause = false;
            CanGoPrevious = false;
            CanGoNext = false;
            CanSeek = false;
            CanChangeShuffle = false;
            CanChangeRepeat = false;
            PlaybackMode = MusicPlaybackMode.Normal;
            RaiseDisplayPropertiesChanged();
            return;
        }

        _preferredSessionId ??= info.SessionId;
        Title = info.Title;
        Artist = info.Artist;
        Album = info.Album;
        _sourceAppUserModelId = info.SourceAppUserModelId;
        SourceDisplayName = info.SourceDisplayName;
        PlaybackState = info.PlaybackState;
        Position = info.Position;
        Duration = info.Duration;
        _lastSyncedPosition = info.Position;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
        CanPlay = info.CanPlay;
        CanPause = info.CanPause;
        CanGoPrevious = info.CanGoPrevious;
        CanGoNext = info.CanGoNext;
        CanSeek = info.CanSeek && info.Duration > TimeSpan.Zero;
        CanChangeShuffle = info.CanChangeShuffle;
        CanChangeRepeat = info.CanChangeRepeat;
        PlaybackMode = info.PlaybackMode;
        // Cover signature dedup: skip expensive cover reload when the song hasn't changed.
        // Timeline and playback events go through lightweight refresh paths, but
        // MediaPropertiesChanged can fire even when only metadata (not the thumbnail) changes.
        string coverSig = $"{info.SessionId}\u001E{info.Title}\u001E{info.Artist}\u001E{info.Album}";
        bool coverChanged = !string.Equals(_lastCoverSignature, coverSig, StringComparison.Ordinal);

        if (coverChanged)
        {
            int gen = ++_coverGeneration;
            var (cover, artworkColor) = await LoadThumbnailAndColorAsync(info);
            if (_isDisposed || gen != _coverGeneration) return;

            if (cover is not null)
            {
                ThumbnailImage = cover;
                SetArtworkColor(artworkColor ?? AccentColorHelper.DefaultAccentColor, artworkColor.HasValue);
                App.ScheduleLightMemoryCleanup();
                _lastCoverSignature = coverSig;
                _coverRetrySignature = null;
                _coverRetryCount = 0;
            }
            else
            {
                _lastCoverSignature = null;
                if (!ScheduleCoverRetry(coverSig, gen))
                {
                    ThumbnailImage = null;
                    SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
                    App.ScheduleLightMemoryCleanup();
                }
            }
        }

        RaiseDisplayPropertiesChanged();
    }

    private bool ShouldDeferEmptyInfo(MusicSessionInfo? info)
    {
        bool incomingInfoIsEmpty = info is null || string.IsNullOrWhiteSpace(info.Title);
        bool hasStableDisplay = !string.IsNullOrWhiteSpace(_title) || ThumbnailImage is not null;
        return incomingInfoIsEmpty &&
               hasStableDisplay &&
               _transientEmptyInfoRetryCount < MaxTransientEmptyInfoRetries;
    }

    private void ScheduleTransientEmptyInfoRetry()
    {
        if (_transientEmptyInfoRetryCount == 0)
        {
            ++_transientEmptyInfoGeneration;
        }

        int generation = _transientEmptyInfoGeneration;
        int retryNumber = ++_transientEmptyInfoRetryCount;
        _ = RetryTransientEmptyInfoAsync(generation, retryNumber);
    }

    private async Task RetryTransientEmptyInfoAsync(int generation, int retryNumber)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(80 + retryNumber * 60));
        if (_isDisposed || generation != _transientEmptyInfoGeneration)
        {
            return;
        }

        ScheduleFullRefresh();
    }

    private void ResetTransientEmptyInfoRetry()
    {
        _transientEmptyInfoRetryCount = 0;
        ++_transientEmptyInfoGeneration;
    }

    private void SetArtworkColor(Color value, bool hasArtworkColor)
    {
        bool didChangeColor = !_artworkColor.Equals(value);
        bool didChangeAvailability = _hasArtworkColor != hasArtworkColor;

        _artworkColor = value;
        _hasArtworkColor = hasArtworkColor;

        if (didChangeColor)
        {
            OnPropertyChanged(nameof(ArtworkBackdropStartColor));
            OnPropertyChanged(nameof(ArtworkBackdropMidColor));
            OnPropertyChanged(nameof(ArtworkBackdropEndColor));
        }

        if (didChangeColor || didChangeAvailability)
        {
            RaiseMusicAccentPropertiesChanged();
        }
    }

    private Color GetMusicAccentColor()
    {
        if (UseArtworkBackdrop && _hasArtworkColor)
        {
            return _artworkColor;
        }

        try
        {
            return App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor;
        }
        catch
        {
            return AccentColorHelper.DefaultAccentColor;
        }
    }

    private void RaiseMusicAccentPropertiesChanged()
    {
        OnPropertyChanged(nameof(MusicAccentBrush));
        OnPropertyChanged(nameof(PlayPauseButtonBackgroundBrush));
    }

    /// <summary>
    /// Loads the thumbnail BitmapImage and extracts the dominant color in a single
    /// stream pass, avoiding the double OpenReadAsync that the previous separate
    /// methods incurred. Uses DecodePixelWidth to cap memory for large artwork.
    /// </summary>
    private const int CoverDecodePixelWidth = 192;
    private const int MaxCoverRetryCount = 3;

    private static async Task<(BitmapImage? image, Color? color)> LoadThumbnailAndColorAsync(MusicSessionInfo info)
    {
        if (info.Thumbnail is null)
        {
            return (null, null);
        }

        try
        {
            PerformanceLogger.RecordMusicCoverDecode();
            using var stream = await info.Thumbnail.OpenReadAsync();

            // Create a clone for BitmapDecoder so BitmapImage can still read the original stream.
            using var colorStream = stream.CloneStream();

            // Load BitmapImage with DecodePixelWidth to cap memory for high-res artwork.
            var image = new BitmapImage { DecodePixelWidth = CoverDecodePixelWidth };
            await image.SetSourceAsync(stream);

            // Extract dominant color from a tiny downscaled version.
            Color? color = await ExtractDominantColorAsync(colorStream);

            return (image, color);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to load thumbnail/color: {ex.Message}");
            return (null, null);
        }
    }

    private bool ScheduleCoverRetry(string coverSignature, int generation)
    {
        if (!string.Equals(_coverRetrySignature, coverSignature, StringComparison.Ordinal))
        {
            _coverRetrySignature = coverSignature;
            _coverRetryCount = 0;
        }

        if (_coverRetryCount >= MaxCoverRetryCount)
        {
            return false;
        }

        int retryNumber = ++_coverRetryCount;
        _ = RetryCoverAsync(coverSignature, generation, retryNumber);
        return true;
    }

    private async Task RetryCoverAsync(string coverSignature, int generation, int retryNumber)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250 * retryNumber));
        if (_isDisposed ||
            generation != _coverGeneration ||
            !string.Equals(_coverRetrySignature, coverSignature, StringComparison.Ordinal) ||
            string.Equals(_lastCoverSignature, coverSignature, StringComparison.Ordinal))
        {
            return;
        }

        await RefreshAsync();
    }

    private static async Task<Color?> ExtractDominantColorAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint targetWidth = Math.Max(1, Math.Min(32, decoder.PixelWidth));
            uint targetHeight = Math.Max(1, Math.Min(32, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = data.DetachPixelData();
            if (pixels.Length < 4)
            {
                return null;
            }

            double totalR = 0;
            double totalG = 0;
            double totalB = 0;
            double weightTotal = 0;

            for (int i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 24)
                {
                    continue;
                }

                double saturationWeight = 0.55 + (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
                double alphaWeight = a / 255.0;
                double weight = saturationWeight * alphaWeight;
                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0.01)
            {
                return null;
            }

            byte averageR = ClampByte(totalR / weightTotal);
            byte averageG = ClampByte(totalG / weightTotal);
            byte averageB = ClampByte(totalB / weightTotal);
            return Color.FromArgb(0xFF, averageR, averageG, averageB);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to sample thumbnail color: {ex.Message}");
            return null;
        }
    }

    private void RefreshSessionList()
    {
        var ids = _musicSessionService.GetSessionIds();
        SessionIds.Clear();
        SessionDisplayNames.Clear();

        foreach (var id in ids)
        {
            SessionIds.Add(id);
            SessionDisplayNames.Add(string.IsNullOrWhiteSpace(id) ? _localizationService.T("Music.SourceUnknown") : id);
        }

        OnPropertyChanged(nameof(SessionPickerVisibility));
        OnPropertyChanged(nameof(SelectedSessionIndex));
    }

    private void AttachServiceEvents()
    {
        _musicSessionService.SessionsChanged += OnSessionsChanged;
        _musicSessionService.CurrentSessionChanged += OnCurrentSessionChanged;
        _musicSessionService.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _musicSessionService.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _musicSessionService.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void DetachServiceEvents()
    {
        _musicSessionService.SessionsChanged -= OnSessionsChanged;
        _musicSessionService.CurrentSessionChanged -= OnCurrentSessionChanged;
        _musicSessionService.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _musicSessionService.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _musicSessionService.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    /// <summary>
    /// Full refresh: reloads everything including cover art.
    /// Triggered by session list changes or media property changes (song change).
    /// </summary>
    private void OnSessionsChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnCurrentSessionChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnMediaPropertiesChanged(object? sender, EventArgs e) => ScheduleFullRefresh();

    /// <summary>
    /// Lightweight refresh: only updates position/duration without reloading cover.
    /// Timeline events fire very frequently (every ~1s), so we must avoid cover reload.
    /// </summary>
    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshTimelineAsync());
            return;
        }

        _ = RefreshTimelineAsync();
    }

    /// <summary>
    /// Lightweight refresh: only updates playback state/capabilities without reloading cover.
    /// </summary>
    private void OnPlaybackInfoChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshPlaybackAsync());
            return;
        }

        _ = RefreshPlaybackAsync();
    }

    private void ScheduleFullRefresh()
    {
        if (_isDisposed) return;

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshAsync());
            return;
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Lightweight timeline-only refresh. Updates position and duration from SMTC
    /// without reloading cover art or media properties.
    /// </summary>
    private async Task RefreshTimelineAsync()
    {
        if (_isDisposed) return;

        int gen = ++_timelineRefreshGeneration;
        try
        {
            var info = await _musicSessionService.GetCurrentTimelineAsync(_preferredSessionId);
            if (_isDisposed || gen != _timelineRefreshGeneration || info is null) return;

            Position = info.Position;
            Duration = info.Duration;
            _lastSyncedPosition = info.Position;
            _lastPositionSyncAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                App.Log($"[MusicWidget] Timeline refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lightweight playback-only refresh. Updates playback state and capabilities
    /// without reloading cover art or media properties.
    /// </summary>
    private async Task RefreshPlaybackAsync()
    {
        if (_isDisposed) return;

        int gen = ++_playbackRefreshGeneration;
        try
        {
            var info = await _musicSessionService.GetCurrentPlaybackAsync(_preferredSessionId);
            if (_isDisposed || gen != _playbackRefreshGeneration || info is null) return;

            PlaybackState = info.PlaybackState;
            CanPlay = info.CanPlay;
            CanPause = info.CanPause;
            CanGoPrevious = info.CanGoPrevious;
            CanGoNext = info.CanGoNext;
            CanSeek = info.CanSeek && Duration > TimeSpan.Zero;
            CanChangeShuffle = info.CanChangeShuffle;
            CanChangeRepeat = info.CanChangeRepeat;
            PlaybackMode = info.PlaybackMode;
            RaiseDisplayPropertiesChanged();
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                App.Log($"[MusicWidget] Playback refresh failed: {ex.Message}");
        }
    }

    private void ProgressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsPlaying && Duration > TimeSpan.Zero && !_isSeeking)
        {
            // Extrapolate from the last SMTC-synced position to avoid fighting with SMTC updates
            double elapsedSinceSync = (DateTimeOffset.UtcNow - _lastPositionSyncAt).TotalSeconds;
            double newPosition = Math.Min(Duration.TotalSeconds, _lastSyncedPosition.TotalSeconds + elapsedSinceSync);
            Position = TimeSpan.FromSeconds(newPosition);
        }
    }

    private void UpdateMusicTimerDiagnostics()
    {
        PerformanceLogger.ActiveMusicTimerCount = _progressTimer?.IsRunning == true ? 1 : 0;
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        // These properties have localized fallbacks in their getters, so they must
        // be raised explicitly when the language changes (the backing fields haven't
        // changed, but the displayed string may differ).
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(SourceDisplayName));
        RaiseDisplayPropertiesChanged();
        RefreshSessionList();
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ApplyAppearance();
    }

    private void ApplyMusicSettings(AppSettings settings)
    {
        UseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        EnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        OnPropertyChanged(nameof(ArtworkBackdropCornerRadius));
    }

    private double GetArtworkBackdropCornerRadius()
    {
        return _settingsService?.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 6,
            SettingsService.WidgetCornerPreferenceRound => 10,
            _ => 8
        };
    }

    private void RaiseDisplayPropertiesChanged()
    {
        // Title, Artist, SourceDisplayName, and DisplayName are backed by fields
        // whose setters already raise PropertyChanged when the value changes.
        // We must NOT raise them here unconditionally — doing so during playback
        // (when RefreshAsync fires from SMTC events) causes the marquee to restart
        // on every refresh, preventing it from ever scrolling.
        // Localized fallback notifications are handled explicitly in OnLanguageChanged.
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayIconVisibility));
        OnPropertyChanged(nameof(PauseIconVisibility));
        OnPropertyChanged(nameof(PlayPauseTooltip));
        OnPropertyChanged(nameof(PreviousTooltip));
        OnPropertyChanged(nameof(NextTooltip));
        OnPropertyChanged(nameof(PlaybackModeTooltip));
        OnPropertyChanged(nameof(VolumeTooltip));
        OnPropertyChanged(nameof(SystemVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeText));
        OnPropertyChanged(nameof(RefreshTooltip));
        OnPropertyChanged(nameof(ThumbnailVisibility));
        OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        OnPropertyChanged(nameof(ArtworkBackdropVisibility));
        OnPropertyChanged(nameof(ArtworkBackdropCornerRadius));
        OnPropertyChanged(nameof(MusicAccentBrush));
        OnPropertyChanged(nameof(PlayPauseButtonBackgroundBrush));
        OnPropertyChanged(nameof(HasSeekableTimeline));
        OnPropertyChanged(nameof(CanInteractWithProgress));
    }

    private MusicPlaybackMode GetNextPlaybackMode()
    {
        return PlaybackMode switch
        {
            MusicPlaybackMode.Normal when CanChangeShuffle => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.Normal when CanChangeRepeat => MusicPlaybackMode.Repeat,
            MusicPlaybackMode.Shuffle when CanChangeRepeat => MusicPlaybackMode.Repeat,
            MusicPlaybackMode.Shuffle => MusicPlaybackMode.Normal,
            MusicPlaybackMode.Repeat => MusicPlaybackMode.Normal,
            _ => MusicPlaybackMode.Normal
        };
    }

    private static byte ClampByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static double NormalizeVolume(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, 3), 0.0, 1.0)
            : 0.0;
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Clamp((int)Math.Round(NormalizeVolume(value) * 100), 0, 100)}%";
    }
}
