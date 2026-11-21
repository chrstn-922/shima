using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Audio;

internal interface IAudioDebounceTimer : IDisposable
{
    bool IsRunning { get; }

    TimeSpan? LastScheduledDelay { get; }

    void Schedule(TimeSpan delay, Action onElapsed);

    void Stop();
}

internal sealed class SystemAudioDebounceTimer : IAudioDebounceTimer
{
    private readonly object _sync = new();
    private Timer? _timer;
    private Action? _callback;
    private bool _running;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running && !_disposed;
            }
        }
    }

    public TimeSpan? LastScheduledDelay { get; private set; }

    public void Schedule(TimeSpan delay, Action onElapsed)
    {
        ArgumentNullException.ThrowIfNull(onElapsed);
        var safeDelay = delay < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : delay;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _callback = onElapsed;
            LastScheduledDelay = safeDelay;
            _running = true;

            if (_timer is null)
            {
                _timer = new Timer(OnTick, null, safeDelay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(safeDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _running = false;
            _callback = null;
            if (!_disposed && _timer is not null)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _callback = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTick(object? state)
    {
        Action? callback;
        lock (_sync)
        {
            if (_disposed || !_running)
            {
                return;
            }

            _running = false;
            callback = _callback;
        }

        callback?.Invoke();
    }
}

internal sealed record MasterVolumeChangedArgs(float Volume, bool IsMuted);

internal sealed record SessionVolumeChangedArgs(string SessionKey, float Volume, bool IsMuted);

internal sealed record AudioDeviceSnapshot(
    bool IsAvailable,
    string DeviceId,
    string DeviceName,
    float MasterVolume,
    bool IsMasterMuted,
    IReadOnlyList<AudioSessionSnapshot> Sessions);

internal interface IAudioMixerBackend : IDisposable
{
    event EventHandler<MasterVolumeChangedArgs>? MasterVolumeChanged;

    event EventHandler<SessionVolumeChangedArgs>? SessionVolumeChanged;

    event EventHandler<AudioSessionSnapshot>? SessionUpserted;

    event EventHandler<string>? SessionRemovedOrExpired;

    event EventHandler? DefaultDeviceChanged;

    Task<AudioDeviceSnapshot> StartAsync(CancellationToken ct = default);

    void Stop();

    Task<AudioDeviceSnapshot> RebindDefaultDeviceAsync(CancellationToken ct = default);

    Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default);

    Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default);

    Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default);

    Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default);
}

internal sealed class MtaComWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly int _threadId;
    private volatile bool _disposed;

    public MtaComWorker(string name)
    {
        var ready = new ManualResetEventSlim(false);
        var id = 0;

        _thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            ready.Set();
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                    }
                    catch
                    {
                        // Individual work items capture and handle their own errors.
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutdown.
            }
        })
        {
            IsBackground = true,
            Name = name
        };

        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        ready.Wait();
        ready.Dispose();
        _threadId = id;
    }

    public void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Worker already shutting down or disposed.
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        }

        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctr = ct.CanBeCanceled
            ? ct.Register(() => tcs.TrySetCanceled(ct))
            : default;

        try
        {
            _queue.Add(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        if (Environment.CurrentManagedThreadId != _threadId && _thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromMilliseconds(500));
        }

        _queue.Dispose();
    }
}

internal sealed class WasapiAudioMixerBackend : IAudioMixerBackend, IMMNotificationClient
{
    private readonly IAppIconResolver _iconResolver;
    private readonly ILogger? _logger;
    private readonly MtaComWorker _mtaWorker = new("Shima-CoreAudio-MTA");
    private readonly Dictionary<string, SessionBinding> _activeBindings = new(StringComparer.Ordinal);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpointVolume;
    private AudioSessionManager? _sessionManager;
    private bool _notificationRegistered;
    private string _currentDeviceId = string.Empty;
    private bool _started;
    private bool _disposed;

    public WasapiAudioMixerBackend(IAppIconResolver iconResolver, ILogger? logger = null)
    {
        _iconResolver = iconResolver ?? throw new ArgumentNullException(nameof(iconResolver));
        _logger = logger;
    }

    public event EventHandler<MasterVolumeChangedArgs>? MasterVolumeChanged;

    public event EventHandler<SessionVolumeChangedArgs>? SessionVolumeChanged;

    public event EventHandler<AudioSessionSnapshot>? SessionUpserted;

    public event EventHandler<string>? SessionRemovedOrExpired;

    public event EventHandler? DefaultDeviceChanged;

    public Task<AudioDeviceSnapshot> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mtaWorker.InvokeAsync(() =>
        {
            _started = true;
            if (_enumerator is null)
            {
                _enumerator = new MMDeviceEnumerator();
            }

            if (!_notificationRegistered)
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
                _notificationRegistered = true;
            }

            return BindDefaultDeviceOnMta();
        }, ct);
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _mtaWorker.InvokeAsync(() =>
            {
                _started = false;
                TeardownCurrentDeviceOnMta();
                if (_enumerator is not null && _notificationRegistered)
                {
                    try
                    {
                        _enumerator.UnregisterEndpointNotificationCallback(this);
                    }
                    catch
                    {
                        // Ignore COM teardown failures.
                    }

                    _notificationRegistered = false;
                }

                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown errors.
        }
    }

    public Task<AudioDeviceSnapshot> RebindDefaultDeviceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mtaWorker.InvokeAsync(() => _started ? BindDefaultDeviceOnMta() : UnavailableDeviceSnapshot(), ct);
    }

    public Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clamped = Math.Clamp(volume, 0f, 1f);
        return _mtaWorker.InvokeAsync(() =>
        {
            if (_endpointVolume is null)
            {
                return false;
            }

            try
            {
                _endpointVolume.MasterVolumeLevelScalar = clamped;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn("Failed setting CoreAudio master volume.", ex);
                return false;
            }
        }, ct);
    }

    public Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mtaWorker.InvokeAsync(() =>
        {
            if (_endpointVolume is null)
            {
                return false;
            }

            try
            {
                _endpointVolume.Mute = isMuted;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn("Failed setting CoreAudio master mute.", ex);
                return false;
            }
        }, ct);
    }

    public Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clamped = Math.Clamp(volume, 0f, 1f);
        return _mtaWorker.InvokeAsync(() =>
        {
            if (!_activeBindings.TryGetValue(sessionKey, out var binding))
            {
                return false;
            }

            try
            {
                binding.Control.SimpleAudioVolume.Volume = clamped;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Failed setting volume for audio session '{sessionKey}'.", ex);
                return false;
            }
        }, ct);
    }

    public Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mtaWorker.InvokeAsync(() =>
        {
            if (!_activeBindings.TryGetValue(sessionKey, out var binding))
            {
                return false;
            }

            try
            {
                binding.Control.SimpleAudioVolume.Mute = isMuted;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Failed setting mute for audio session '{sessionKey}'.", ex);
                return false;
            }
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _mtaWorker.InvokeAsync(() =>
        {
            _enumerator?.Dispose();
            _enumerator = null;
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        _mtaWorker.Dispose();
    }

    private AudioDeviceSnapshot BindDefaultDeviceOnMta()
    {
        TeardownCurrentDeviceOnMta();

        if (_enumerator is null)
        {
            return UnavailableDeviceSnapshot();
        }

        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                return UnavailableDeviceSnapshot();
            }

            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _currentDeviceId = _device.ID ?? string.Empty;

            _endpointVolume = _device.AudioEndpointVolume;
            _endpointVolume.OnVolumeNotification += OnEndpointVolumeNotification;

            _sessionManager = _device.AudioSessionManager;
            _sessionManager.OnSessionCreated += OnSessionCreatedCallback;
            _sessionManager.RefreshSessions();

            var snapshots = new List<AudioSessionSnapshot>();
            var sessions = _sessionManager.Sessions;
            if (sessions is not null)
            {
                var count = sessions.Count;
                for (var i = 0; i < count; i++)
                {
                    var control = sessions[i];
                    var snapshot = AttachSessionOnMta(control);
                    if (snapshot is not null && snapshot.State != AudioSessionActivityState.Expired)
                    {
                        snapshots.Add(snapshot);
                    }
                }
            }

            return new AudioDeviceSnapshot(
                IsAvailable: true,
                DeviceId: _currentDeviceId,
                DeviceName: string.IsNullOrWhiteSpace(_device.FriendlyName) ? "Default Output" : _device.FriendlyName,
                MasterVolume: Math.Clamp(_endpointVolume.MasterVolumeLevelScalar, 0f, 1f),
                IsMasterMuted: _endpointVolume.Mute,
                Sessions: snapshots);
        }
        catch (COMException ex)
        {
            _logger?.Warn("WASAPI default audio device binding failed.", ex);
            TeardownCurrentDeviceOnMta();
            return UnavailableDeviceSnapshot();
        }
        catch (Exception ex)
        {
            _logger?.Warn("Unexpected error binding default WASAPI audio device.", ex);
            TeardownCurrentDeviceOnMta();
            return UnavailableDeviceSnapshot();
        }
    }

    private void TeardownCurrentDeviceOnMta()
    {
        foreach (var binding in _activeBindings.Values.ToArray())
        {
            binding.Dispose();
        }

        _activeBindings.Clear();

        if (_sessionManager is not null)
        {
            try
            {
                _sessionManager.OnSessionCreated -= OnSessionCreatedCallback;
            }
            catch
            {
                // Ignore COM unsubscribe errors on removed devices.
            }

            try
            {
                _sessionManager.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors.
            }

            _sessionManager = null;
        }

        if (_endpointVolume is not null)
        {
            try
            {
                _endpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification;
            }
            catch
            {
                // Ignore COM unsubscribe errors on removed devices.
            }

            try
            {
                _endpointVolume.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors.
            }

            _endpointVolume = null;
        }

        if (_device is not null)
        {
            try
            {
                _device.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors.
            }

            _device = null;
        }

        _currentDeviceId = string.Empty;
    }

    private AudioSessionSnapshot? AttachSessionOnMta(AudioSessionControl control)
    {
        try
        {
            var sessionKey = ResolveSessionKey(control);
            if (_activeBindings.TryGetValue(sessionKey, out var existing))
            {
                existing.Dispose();
                _activeBindings.Remove(sessionKey);
            }

            var binding = new SessionBinding(this, sessionKey, control);
            _activeBindings[sessionKey] = binding;
            control.RegisterEventClient(binding);

            return BuildSnapshotOnMta(sessionKey, control);
        }
        catch (COMException)
        {
            try
            {
                control.Dispose();
            }
            catch
            {
                // Ignore COM errors.
            }

            return null;
        }
    }

    private AudioSessionSnapshot? BuildSnapshotOnMta(string sessionKey, AudioSessionControl control)
    {
        try
        {
            var state = MapState(control.State);
            var pid = control.GetProcessID;
            var isSystemSounds = control.IsSystemSoundsSession;
            var rawDisplay = SafeRead(() => control.DisplayName);
            var rawIconPath = SafeRead(() => control.IconPath);
            var exePath = _iconResolver.TryGetExecutablePath(pid);
            var displayName = _iconResolver.ResolveDisplayName(pid, rawDisplay, exePath, isSystemSounds);
            var icon = _iconResolver.TryResolveIcon(exePath, rawIconPath);
            var simpleVol = control.SimpleAudioVolume;
            var volume = Math.Clamp(simpleVol.Volume, 0f, 1f);
            var isMuted = simpleVol.Mute;
            var processName = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFileNameWithoutExtension(exePath);

            return new AudioSessionSnapshot
            {
                SessionKey = sessionKey,
                ProcessId = pid,
                DisplayName = displayName,
                ProcessName = processName,
                ExecutablePath = exePath,
                Volume = volume,
                IsMuted = isMuted,
                IsSystemSounds = isSystemSounds,
                State = state,
                IconImage = icon
            };
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data)
    {
        if (!_started || _disposed || data is null)
        {
            return;
        }

        var vol = Math.Clamp(data.MasterVolume, 0f, 1f);
        var muted = data.Muted;
        _mtaWorker.Post(() =>
        {
            if (_started && !_disposed)
            {
                MasterVolumeChanged?.Invoke(this, new MasterVolumeChangedArgs(vol, muted));
            }
        });
    }

    private void OnSessionCreatedCallback(object? sender, IAudioSessionControl newSession)
    {
        if (!_started || _disposed || newSession is null)
        {
            return;
        }

        _mtaWorker.Post(() =>
        {
            if (!_started || _disposed)
            {
                return;
            }

            var control = new AudioSessionControl(newSession);
            var snapshot = AttachSessionOnMta(control);
            if (snapshot is not null && snapshot.State != AudioSessionActivityState.Expired)
            {
                SessionUpserted?.Invoke(this, snapshot);
            }
        });
    }

    private void PostSessionVolumeChanged(string sessionKey, float volume, bool isMuted) =>
        _mtaWorker.Post(() =>
        {
            if (_started && !_disposed)
            {
                SessionVolumeChanged?.Invoke(
                    this,
                    new SessionVolumeChangedArgs(sessionKey, Math.Clamp(volume, 0f, 1f), isMuted));
            }
        });

    private void PostSessionMetadataChanged(string sessionKey) =>
        _mtaWorker.Post(() =>
        {
            if (!_started || !_activeBindings.TryGetValue(sessionKey, out var binding))
            {
                return;
            }

            var snapshot = BuildSnapshotOnMta(sessionKey, binding.Control);
            if (snapshot is not null && snapshot.State != AudioSessionActivityState.Expired)
            {
                SessionUpserted?.Invoke(this, snapshot);
            }
        });

    private void PostSessionStateChanged(string sessionKey, AudioSessionState state) =>
        _mtaWorker.Post(() =>
        {
            if (!_started)
            {
                return;
            }

            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                RemoveBindingOnMta(sessionKey);
                SessionRemovedOrExpired?.Invoke(this, sessionKey);
                return;
            }

            if (_activeBindings.TryGetValue(sessionKey, out var binding))
            {
                var snapshot = BuildSnapshotOnMta(sessionKey, binding.Control);
                if (snapshot is not null)
                {
                    SessionUpserted?.Invoke(this, snapshot);
                }
            }
        });

    private void PostSessionDisconnected(string sessionKey) =>
        _mtaWorker.Post(() =>
        {
            if (!_started)
            {
                return;
            }

            RemoveBindingOnMta(sessionKey);
            SessionRemovedOrExpired?.Invoke(this, sessionKey);
        });

    private void RemoveBindingOnMta(string sessionKey)
    {
        if (_activeBindings.TryGetValue(sessionKey, out var existing))
        {
            _activeBindings.Remove(sessionKey);
            existing.Dispose();
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (!_started || _disposed)
        {
            return;
        }

        _mtaWorker.Post(() =>
        {
            if (_started && string.Equals(deviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (!_started || _disposed)
        {
            return;
        }

        _mtaWorker.Post(() =>
        {
            if (_started && string.Equals(deviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (!_started || _disposed || flow != DataFlow.Render || role != Role.Multimedia)
        {
            return;
        }

        _mtaWorker.Post(() =>
        {
            if (_started)
            {
                DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    private static string ResolveSessionKey(AudioSessionControl control)
    {
        var instanceId = SafeRead(() => control.GetSessionInstanceIdentifier);
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            return instanceId;
        }

        var sessionId = SafeRead(() => control.GetSessionIdentifier);
        return $"{control.GetProcessID}:{sessionId}";
    }

    private static T? SafeRead<T>(Func<T> reader) where T : class
    {
        try
        {
            return reader();
        }
        catch
        {
            return null;
        }
    }

    private static AudioSessionActivityState MapState(AudioSessionState state) => state switch
    {
        AudioSessionState.AudioSessionStateActive => AudioSessionActivityState.Active,
        AudioSessionState.AudioSessionStateExpired => AudioSessionActivityState.Expired,
        _ => AudioSessionActivityState.Inactive
    };

    private static AudioDeviceSnapshot UnavailableDeviceSnapshot() =>
        new(false, string.Empty, "No audio output device", 0f, false, Array.Empty<AudioSessionSnapshot>());

    private sealed class SessionBinding : IAudioSessionEventsHandler, IDisposable
    {
        private readonly WasapiAudioMixerBackend _owner;
        private readonly string _sessionKey;
        private bool _disposed;

        public SessionBinding(WasapiAudioMixerBackend owner, string sessionKey, AudioSessionControl control)
        {
            _owner = owner;
            _sessionKey = sessionKey;
            Control = control;
        }

        public AudioSessionControl Control { get; }

        public void OnVolumeChanged(float volume, bool isMuted) =>
            _owner.PostSessionVolumeChanged(_sessionKey, volume, isMuted);

        public void OnDisplayNameChanged(string displayName) =>
            _owner.PostSessionMetadataChanged(_sessionKey);

        public void OnIconPathChanged(string iconPath) =>
            _owner.PostSessionMetadataChanged(_sessionKey);

        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }

        public void OnStateChanged(AudioSessionState state) =>
            _owner.PostSessionStateChanged(_sessionKey, state);

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) =>
            _owner.PostSessionDisconnected(_sessionKey);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Control.UnRegisterEventClient(this);
            }
            catch
            {
                // Ignore COM unregister failures on expired sessions.
            }

            try
            {
                Control.Dispose();
            }
            catch
            {
                // Ignore COM teardown failures.
            }
        }
    }
}

/// <summary>
/// Bidirectional CoreAudio WASAPI Volume Mixer adapter with 150ms transient-session termination debounce,
/// automatic default-device rebinding, and zero polling loops.
/// </summary>
public sealed class AudioMixerAdapter : IAudioMixerAdapter
{
    public static readonly TimeSpan SessionRemovalDebounceDelay = TimeSpan.FromMilliseconds(150);

    private readonly IAudioMixerBackend _backend;
    private readonly IAudioDebounceTimer _debounceTimer;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, AudioSessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pendingRemovals = new(StringComparer.Ordinal);

    private bool _isAvailable;
    private string _deviceId = string.Empty;
    private string _deviceName = "No audio output device";
    private float _masterVolume;
    private bool _isMasterMuted;
    private AudioMixerSnapshot _currentSnapshot = AudioMixerSnapshot.Unavailable;
    private bool _started;
    private bool _disposed;

    public AudioMixerAdapter(ILogger? logger = null)
        : this(
            new WasapiAudioMixerBackend(new Win32AppIconResolver(), logger),
            new SystemAudioDebounceTimer(),
            null,
            logger)
    {
    }

    internal AudioMixerAdapter(
        IAudioMixerBackend backend,
        IAudioDebounceTimer? debounceTimer = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        ILogger? logger = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _debounceTimer = debounceTimer ?? new SystemAudioDebounceTimer();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    public AudioMixerSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public event EventHandler<AudioMixerSnapshot>? MixerChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _backend.MasterVolumeChanged += OnBackendMasterVolumeChanged;
            _backend.SessionVolumeChanged += OnBackendSessionVolumeChanged;
            _backend.SessionUpserted += OnBackendSessionUpserted;
            _backend.SessionRemovedOrExpired += OnBackendSessionRemovedOrExpired;
            _backend.DefaultDeviceChanged += OnBackendDefaultDeviceChanged;
        }

        var deviceSnapshot = await _backend.StartAsync(ct).ConfigureAwait(false);
        AudioMixerSnapshot snapshot;
        lock (_sync)
        {
            ApplyDeviceSnapshotLocked(deviceSnapshot);
            snapshot = _currentSnapshot;
        }

        MixerChanged?.Invoke(this, snapshot);
    }

    public void Stop()
    {
        bool wasStarted;
        lock (_sync)
        {
            wasStarted = _started;
            if (!wasStarted)
            {
                return;
            }

            _started = false;
            _backend.MasterVolumeChanged -= OnBackendMasterVolumeChanged;
            _backend.SessionVolumeChanged -= OnBackendSessionVolumeChanged;
            _backend.SessionUpserted -= OnBackendSessionUpserted;
            _backend.SessionRemovedOrExpired -= OnBackendSessionRemovedOrExpired;
            _backend.DefaultDeviceChanged -= OnBackendDefaultDeviceChanged;
            _pendingRemovals.Clear();
            _sessions.Clear();
            _debounceTimer.Stop();
            _isAvailable = false;
            _deviceId = string.Empty;
            _deviceName = "No audio output device";
            _masterVolume = 0f;
            _isMasterMuted = false;
            _currentSnapshot = AudioMixerSnapshot.Unavailable;
        }

        _backend.Stop();
    }

    public async Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        var clamped = Math.Clamp(volume, 0f, 1f);
        var ok = await _backend.SetMasterVolumeAsync(clamped, ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        AudioMixerSnapshot snapshot;
        lock (_sync)
        {
            _masterVolume = clamped;
            snapshot = RebuildSnapshotLocked();
        }

        MixerChanged?.Invoke(this, snapshot);
        return true;
    }

    public async Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        var ok = await _backend.SetMasterMuteAsync(isMuted, ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        AudioMixerSnapshot snapshot;
        lock (_sync)
        {
            _isMasterMuted = isMuted;
            snapshot = RebuildSnapshotLocked();
        }

        MixerChanged?.Invoke(this, snapshot);
        return true;
    }

    public async Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (_disposed)
        {
            return false;
        }

        var clamped = Math.Clamp(volume, 0f, 1f);
        var ok = await _backend.SetSessionVolumeAsync(sessionKey, clamped, ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        AudioMixerSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionKey, out var existing))
            {
                _sessions[sessionKey] = existing with { Volume = clamped };
                snapshot = RebuildSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            MixerChanged?.Invoke(this, snapshot);
        }

        return true;
    }

    public async Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (_disposed)
        {
            return false;
        }

        var ok = await _backend.SetSessionMuteAsync(sessionKey, isMuted, ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        AudioMixerSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionKey, out var existing))
            {
                _sessions[sessionKey] = existing with { IsMuted = isMuted };
                snapshot = RebuildSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            MixerChanged?.Invoke(this, snapshot);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _debounceTimer.Dispose();
        _backend.Dispose();
    }

    private void OnBackendMasterVolumeChanged(object? sender, MasterVolumeChangedArgs args)
    {
        AudioMixerSnapshot snapshot;
        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            _masterVolume = Math.Clamp(args.Volume, 0f, 1f);
            _isMasterMuted = args.IsMuted;
            snapshot = RebuildSnapshotLocked();
        }

        MixerChanged?.Invoke(this, snapshot);
    }

    private void OnBackendSessionVolumeChanged(object? sender, SessionVolumeChangedArgs args)
    {
        AudioMixerSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            if (_sessions.TryGetValue(args.SessionKey, out var existing))
            {
                _sessions[args.SessionKey] = existing with
                {
                    Volume = Math.Clamp(args.Volume, 0f, 1f),
                    IsMuted = args.IsMuted
                };
                snapshot = RebuildSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            MixerChanged?.Invoke(this, snapshot);
        }
    }

    private void OnBackendSessionUpserted(object? sender, AudioSessionSnapshot session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.SessionKey))
        {
            return;
        }

        if (session.State == AudioSessionActivityState.Expired)
        {
            ScheduleSessionRemoval(session.SessionKey);
            return;
        }

        AudioMixerSnapshot snapshot;
        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            _pendingRemovals.Remove(session.SessionKey);
            _sessions[session.SessionKey] = session;

            if (_pendingRemovals.Count == 0)
            {
                _debounceTimer.Stop();
            }
            else
            {
                RescheduleDebounceLocked();
            }

            snapshot = RebuildSnapshotLocked();
        }

        MixerChanged?.Invoke(this, snapshot);
    }

    private void OnBackendSessionRemovedOrExpired(object? sender, string sessionKey)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            ScheduleSessionRemoval(sessionKey);
        }
    }

    private void ScheduleSessionRemoval(string sessionKey)
    {
        lock (_sync)
        {
            if (!_started || _disposed || !_sessions.ContainsKey(sessionKey))
            {
                return;
            }

            _pendingRemovals[sessionKey] = _utcNowProvider() + SessionRemovalDebounceDelay;
            RescheduleDebounceLocked();
        }
    }

    private void RescheduleDebounceLocked()
    {
        if (_pendingRemovals.Count == 0)
        {
            _debounceTimer.Stop();
            return;
        }

        var earliest = _pendingRemovals.Values.Min();
        var delay = earliest - _utcNowProvider();
        if (delay < TimeSpan.FromMilliseconds(10))
        {
            delay = TimeSpan.FromMilliseconds(10);
        }

        _debounceTimer.Schedule(delay, OnRemovalDebounceElapsed);
    }

    private void OnRemovalDebounceElapsed()
    {
        AudioMixerSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            var now = _utcNowProvider();
            var expiredKeys = _pendingRemovals
                .Where(kvp => kvp.Value <= now)
                .Select(kvp => kvp.Key)
                .ToArray();

            var removedAny = false;
            foreach (var key in expiredKeys)
            {
                _pendingRemovals.Remove(key);
                if (_sessions.Remove(key))
                {
                    removedAny = true;
                }
            }

            if (_pendingRemovals.Count > 0)
            {
                RescheduleDebounceLocked();
            }
            else
            {
                _debounceTimer.Stop();
            }

            if (removedAny)
            {
                snapshot = RebuildSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            MixerChanged?.Invoke(this, snapshot);
        }
    }

    private void OnBackendDefaultDeviceChanged(object? sender, EventArgs e) =>
        _ = RebindDefaultDeviceSafeAsync();

    private async Task RebindDefaultDeviceSafeAsync()
    {
        try
        {
            var deviceSnapshot = await _backend.RebindDefaultDeviceAsync(CancellationToken.None).ConfigureAwait(false);
            AudioMixerSnapshot? emitted = null;
            lock (_sync)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                ApplyDeviceSnapshotLocked(deviceSnapshot);
                emitted = _currentSnapshot;
            }

            MixerChanged?.Invoke(this, emitted);
        }
        catch (Exception ex)
        {
            _logger?.Warn("Error handling default audio device change.", ex);
        }
    }

    private void ApplyDeviceSnapshotLocked(AudioDeviceSnapshot deviceSnapshot)
    {
        _pendingRemovals.Clear();
        _debounceTimer.Stop();
        _sessions.Clear();

        _isAvailable = deviceSnapshot.IsAvailable;
        _deviceId = deviceSnapshot.DeviceId ?? string.Empty;
        _deviceName = string.IsNullOrWhiteSpace(deviceSnapshot.DeviceName)
            ? "Default Output"
            : deviceSnapshot.DeviceName;
        _masterVolume = Math.Clamp(deviceSnapshot.MasterVolume, 0f, 1f);
        _isMasterMuted = deviceSnapshot.IsMasterMuted;

        foreach (var session in deviceSnapshot.Sessions)
        {
            if (session.State != AudioSessionActivityState.Expired)
            {
                _sessions[session.SessionKey] = session;
            }
        }

        RebuildSnapshotLocked();
    }

    private AudioMixerSnapshot RebuildSnapshotLocked()
    {
        var orderedSessions = _sessions.Values
            .OrderBy(s => s.IsSystemSounds ? 1 : 0)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ProcessId)
            .ThenBy(s => s.SessionKey, StringComparer.Ordinal)
            .ToArray();

        _currentSnapshot = new AudioMixerSnapshot
        {
            DeviceId = _deviceId,
            DeviceName = _deviceName,
            MasterVolume = _masterVolume,
            IsMasterMuted = _isMasterMuted,
            IsAvailable = _isAvailable,
            Sessions = orderedSessions
        };

        return _currentSnapshot;
    }
}
