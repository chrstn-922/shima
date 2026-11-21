using Shima.Core.Contracts;

namespace Shima.Widgets.NowPlaying;

/// <summary>
/// Now Playing widget module (shima.music) driven by WinRT GSMTC events.
/// Compact Active pill reflects playback automatically; progress timer runs ONLY while Expanded,
/// active, playing, and display is powered on.
/// </summary>
public sealed class NowPlayingWidgetModule : IWidgetModule
{
    public const string Id = "shima.music";

    private readonly IMediaSessionAdapter _mediaAdapter;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly INowPlayingTimer _progressTimer;
    private readonly object _stateSync = new();

    private IWidgetContext? _context;
    private bool _isActive;
    private IslandShellState _shellState = IslandShellState.Collapsed;
    private PowerProfile _powerProfile = PowerProfile.AC;
    private bool _disposed;

    public NowPlayingWidgetModule(
        IMediaSessionAdapter mediaAdapter,
        Func<DateTimeOffset>? utcNowProvider = null,
        INowPlayingTimer? progressTimer = null)
    {
        _mediaAdapter = mediaAdapter ?? throw new ArgumentNullException(nameof(mediaAdapter));
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _progressTimer = progressTimer ?? new SystemNowPlayingTimer();
        ViewModel = new NowPlayingWidgetViewModel(_mediaAdapter, _utcNowProvider);
        ViewModel.SettingsChanged += OnViewModelSettingsChanged;
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Now Playing",
        DefaultOrder = 1,
        RequiresBackgroundSubscription = true,
        SupportsCompactActiveState = true,
        SupportsNotificationPeek = false,
        DefaultAccentHex = "#34D399"
    };

    public NowPlayingWidgetViewModel ViewModel { get; }

    public bool IsProgressTimerRunning => _progressTimer.IsRunning;

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _context = context ?? throw new ArgumentNullException(nameof(context));

        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);
        _mediaAdapter.SessionChanged += OnMediaSessionChanged;
        await _mediaAdapter.StartAsync(ct).ConfigureAwait(false);

        SyncSessionAndCompactPill(_mediaAdapter.CurrentSession);
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateSync)
        {
            _isActive = true;
            _shellState = IslandShellState.Expanded;
        }

        ViewModel.ApplySession(_mediaAdapter.CurrentSession, _utcNowProvider());
        EvaluateProgressTimerLifecycle();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        lock (_stateSync)
        {
            _isActive = false;
        }

        _progressTimer.Stop();
        return Task.CompletedTask;
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        lock (_stateSync)
        {
            _shellState = newState;
        }

        if (newState is IslandShellState.Collapsed or IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            _progressTimer.Stop();
            return;
        }

        EvaluateProgressTimerLifecycle();
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        lock (_stateSync)
        {
            _powerProfile = profile;
        }

        if (profile == PowerProfile.DisplayOff)
        {
            _progressTimer.Stop();
            return;
        }

        EvaluateProgressTimerLifecycle();
    }

    public object CreateExpandedView() => new Views.NowPlayingView(ViewModel);

    public object? CreateCompactActiveView() => new Views.NowPlayingCompactView(ViewModel);

    public object CreateSettingsView() => new Views.NowPlayingSettingsView(ViewModel);

    public async Task ResetSettingsAsync()
    {
        await ViewModel.ResetToDefaultsAsync().ConfigureAwait(false);
        SyncCompactPill(_mediaAdapter.CurrentSession);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_stateSync)
        {
            _disposed = true;
            _isActive = false;
        }

        ViewModel.SettingsChanged -= OnViewModelSettingsChanged;
        _mediaAdapter.SessionChanged -= OnMediaSessionChanged;
        _progressTimer.Dispose();
        _context?.Island.UpdateCompactPill(Id, null);
        _mediaAdapter.Stop();

        return ValueTask.CompletedTask;
    }

    private void OnMediaSessionChanged(object? sender, MediaSessionSnapshot? snapshot)
    {
        if (_disposed)
        {
            return;
        }

        SyncSessionAndCompactPill(snapshot);
        EvaluateProgressTimerLifecycle();
    }

    private void OnViewModelSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        SyncCompactPill(_mediaAdapter.CurrentSession);
    }

    private void SyncSessionAndCompactPill(MediaSessionSnapshot? snapshot)
    {
        ViewModel.ApplySession(snapshot, _utcNowProvider());
        SyncCompactPill(snapshot);
    }

    private void SyncCompactPill(MediaSessionSnapshot? snapshot)
    {
        var settings = new NowPlayingSettings
        {
            ShowCompactActivePill = ViewModel.ShowCompactActivePill,
            ShowAlbumArtInCompactPill = ViewModel.ShowAlbumArtInCompactPill
        };
        var badge = NowPlayingWidgetViewModel.BuildCompactBadgeText(snapshot, settings);
        _context?.Island.UpdateCompactPill(Id, badge);
    }

    private void EvaluateProgressTimerLifecycle()
    {
        bool shouldRun;
        PowerProfile power;
        lock (_stateSync)
        {
            power = _powerProfile;
            var session = _mediaAdapter.CurrentSession;
            shouldRun = !_disposed
                && _isActive
                && _shellState == IslandShellState.Expanded
                && power != PowerProfile.DisplayOff
                && session is { IsPlaying: true }
                && session.Duration > TimeSpan.Zero;
        }

        if (!shouldRun)
        {
            _progressTimer.Stop();
            return;
        }

        var interval = power is PowerProfile.Battery or PowerProfile.EnergySaver
            ? TimeSpan.FromMilliseconds(1000)
            : TimeSpan.FromMilliseconds(500);
        _progressTimer.Start(interval, OnProgressTick);
    }

    private void OnProgressTick()
    {
        if (_disposed)
        {
            return;
        }

        ViewModel.TickInterpolatedPosition(_utcNowProvider());
    }
}
