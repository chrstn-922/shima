using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shima.Core.Contracts;
using Shima.WindowsInterop.System;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.Win32;

namespace Shima.WindowsInterop.Notifications;

internal interface INotificationNativeBackend : IDisposable
{
    bool IsSupportedOsVersion();

    bool HasPackageIdentity();

    Task<NotificationAccessState> GetAccessStateAsync(CancellationToken ct = default);

    Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync(CancellationToken ct = default);

    Task<bool> TryRemoveFromActionCenterAsync(uint osToastId, CancellationToken ct = default);

    bool IsDoNotDisturbActive();

    Task StartListeningAsync(CancellationToken ct = default);

    void StopListening();

    event EventHandler<OsNotificationEventArgs>? NotificationArrived;

    event EventHandler<uint>? NotificationRemovedExternally;
}

internal sealed class WinRtNotificationNativeBackend : INotificationNativeBackend
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int MaxIconBytes = 512 * 1024;

    private readonly object _sync = new();
    private UserNotificationListener? _listener;
    private bool _isListening;
    private bool _disposed;

    public event EventHandler<OsNotificationEventArgs>? NotificationArrived;

    public event EventHandler<uint>? NotificationRemovedExternally;

    public bool IsSupportedOsVersion() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public bool HasPackageIdentity()
    {
        if (!IsSupportedOsVersion())
        {
            return false;
        }

        uint length = 0;
        var rc = GetCurrentPackageFullName(ref length, null);
        return rc != AppModelErrorNoPackage && (rc == ErrorInsufficientBuffer || rc == ErrorSuccess);
    }

    public Task<NotificationAccessState> GetAccessStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSupportedOsVersion())
        {
            return Task.FromResult(NotificationAccessState.UnsupportedOs);
        }

        if (!HasPackageIdentity())
        {
            return Task.FromResult(NotificationAccessState.IdentityUnavailable);
        }

        try
        {
            var listener = UserNotificationListener.Current;
            return Task.FromResult(MapStatus(listener.GetAccessStatus()));
        }
        catch
        {
            return Task.FromResult(NotificationAccessState.IdentityUnavailable);
        }
    }

    public async Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSupportedOsVersion())
        {
            return NotificationAccessState.UnsupportedOs;
        }

        if (!HasPackageIdentity())
        {
            return NotificationAccessState.IdentityUnavailable;
        }

        try
        {
            var listener = UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
            return MapStatus(status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotificationAccessState.DeniedByUserOrPolicy;
        }
    }

    public async Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (await GetAccessStateAsync(ct).ConfigureAwait(false) != NotificationAccessState.Allowed)
        {
            return Array.Empty<OsNotificationDto>();
        }

        try
        {
            var listener = UserNotificationListener.Current;
            var toasts = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
            var list = new List<OsNotificationDto>(toasts.Count);
            foreach (var notification in toasts)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(await ConvertToastAsync(notification, ct).ConfigureAwait(false));
            }

            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<OsNotificationDto>();
        }
    }

    public async Task<bool> TryRemoveFromActionCenterAsync(uint osToastId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (await GetAccessStateAsync(ct).ConfigureAwait(false) != NotificationAccessState.Allowed)
        {
            return false;
        }

        try
        {
            UserNotificationListener.Current.RemoveNotification(osToastId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsDoNotDisturbActive()
    {
        try
        {
            var hr = PInvoke.SHQueryUserNotificationState(out var state);
            if (hr.Succeeded)
            {
                var mapped = (UserNotificationQueryState)(int)state;
                if (mapped is UserNotificationQueryState.Busy
                    or UserNotificationQueryState.RunningD3DFullscreen
                    or UserNotificationQueryState.PresentationMode
                    or UserNotificationQueryState.QuietTime)
                {
                    return true;
                }
            }

            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            if (key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int enabled && enabled == 0)
            {
                return true;
            }
        }
        catch
        {
            // Treat unreadable Focus Assist flags as non-suppressed so notifications aren't lost silently.
        }

        return false;
    }

    public async Task StartListeningAsync(CancellationToken ct = default)
    {
        if (await GetAccessStateAsync(ct).ConfigureAwait(false) != NotificationAccessState.Allowed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed || _isListening)
            {
                return;
            }

            _listener = UserNotificationListener.Current;
            _listener.NotificationChanged += OnListenerNotificationChanged;
            _isListening = true;
        }
    }

    public void StopListening()
    {
        lock (_sync)
        {
            if (!_isListening || _listener is null)
            {
                _isListening = false;
                return;
            }

            try
            {
                _listener.NotificationChanged -= OnListenerNotificationChanged;
            }
            catch
            {
                // Ignore WinRT teardown failures.
            }
            finally
            {
                _listener = null;
                _isListening = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
    }

    private void OnListenerNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (args.ChangeKind == UserNotificationChangedKind.Removed)
        {
            NotificationRemovedExternally?.Invoke(this, args.UserNotificationId);
            return;
        }

        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            _ = HandleAddedToastSafeAsync(sender, args.UserNotificationId);
        }
    }

    private async Task HandleAddedToastSafeAsync(UserNotificationListener listener, uint notificationId)
    {
        try
        {
            var toast = listener.GetNotification(notificationId);
            if (toast is null)
            {
                return;
            }

            var dto = await ConvertToastAsync(toast, CancellationToken.None).ConfigureAwait(false);
            if (!_disposed)
            {
                NotificationArrived?.Invoke(this, new OsNotificationEventArgs(dto));
            }
        }
        catch
        {
            // Ignore transient race if notification is dismissed before GetNotification completes.
        }
    }

    private static async Task<OsNotificationDto> ConvertToastAsync(UserNotification notification, CancellationToken ct)
    {
        var appId = string.IsNullOrWhiteSpace(notification.AppInfo?.AppUserModelId)
            ? $"toast:{notification.Id}"
            : notification.AppInfo.AppUserModelId;
        var appName = string.IsNullOrWhiteSpace(notification.AppInfo?.DisplayInfo?.DisplayName)
            ? appId
            : notification.AppInfo.DisplayInfo.DisplayName;

        string? title = null;
        string? body = null;

        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is not null)
        {
            var lines = new List<string>();
            foreach (var element in binding.GetTextElements())
            {
                if (!string.IsNullOrWhiteSpace(element.Text))
                {
                    lines.Add(element.Text.Trim());
                }
            }

            if (lines.Count > 0)
            {
                title = lines[0];
                if (lines.Count > 1)
                {
                    body = string.Join(Environment.NewLine, lines.Skip(1));
                }
            }
        }

        byte[]? iconBytes = null;
        var logoRef = notification.AppInfo?.DisplayInfo?.GetLogo(new Size(48, 48));
        if (logoRef is not null)
        {
            try
            {
                using var ras = await logoRef.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
                using var stream = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while (ms.Length < MaxIconBytes
                       && (read = await stream.ReadAsync(
                           buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxIconBytes - ms.Length)),
                           ct).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }

                if (ms.Length > 0 && ms.Length <= MaxIconBytes)
                {
                    iconBytes = ms.ToArray();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                iconBytes = null;
            }
        }

        return new OsNotificationDto(
            notification.Id,
            appId,
            appName,
            title,
            body,
            iconBytes,
            notification.CreationTime.ToUniversalTime());
    }

    private static NotificationAccessState MapStatus(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => NotificationAccessState.Allowed,
        _ => NotificationAccessState.DeniedByUserOrPolicy
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}

/// <summary>
/// Native Windows toast adapter backed strictly by WinRT UserNotificationListener
/// (zero UI Automation window scraping) with graceful Sparse Package IdentityUnavailable detection.
/// Never logs notification titles or bodies.
/// </summary>
public sealed class NotificationListenerAdapter : INotificationListenerAdapter
{
    private readonly INotificationNativeBackend _backend;
    private readonly ILogger? _logger;
    private bool _disposed;

    public NotificationListenerAdapter(ILogger? logger = null)
        : this(new WinRtNotificationNativeBackend(), logger)
    {
    }

    internal NotificationListenerAdapter(INotificationNativeBackend backend, ILogger? logger = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger;
        _backend.NotificationArrived += OnBackendNotificationArrived;
        _backend.NotificationRemovedExternally += OnBackendNotificationRemovedExternally;
    }

    public event EventHandler<OsNotificationEventArgs>? NotificationArrived;

    public event EventHandler<uint>? NotificationRemovedExternally;

    public async Task<NotificationAccessState> GetAccessStateAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = await _backend.GetAccessStateAsync(ct).ConfigureAwait(false);
        if (state != NotificationAccessState.Allowed)
        {
            _logger?.Info($"Windows notification listener state: {state}.");
        }

        return state;
    }

    public Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.RequestAccessAsync(ct);
    }

    public Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetCurrentToastSnapshotAsync(ct);
    }

    public Task<bool> TryRemoveFromActionCenterAsync(uint osToastId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.TryRemoveFromActionCenterAsync(osToastId, ct);
    }

    public bool IsDoNotDisturbActive() => !_disposed && _backend.IsDoNotDisturbActive();

    public Task StartListeningAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.StartListeningAsync(ct);
    }

    public void StopListening()
    {
        if (!_disposed)
        {
            _backend.StopListening();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.NotificationArrived -= OnBackendNotificationArrived;
        _backend.NotificationRemovedExternally -= OnBackendNotificationRemovedExternally;
        _backend.Dispose();
    }

    private void OnBackendNotificationArrived(object? sender, OsNotificationEventArgs e)
    {
        if (!_disposed)
        {
            NotificationArrived?.Invoke(this, e);
        }
    }

    private void OnBackendNotificationRemovedExternally(object? sender, uint osToastId)
    {
        if (!_disposed)
        {
            NotificationRemovedExternally?.Invoke(this, osToastId);
        }
    }
}
