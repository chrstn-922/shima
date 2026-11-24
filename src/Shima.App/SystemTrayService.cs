using Shima.Core.Contracts;
using Shima.Presentation.Shell;
using Shima.WindowsInterop.System;

namespace Shima.App;

public sealed class SystemTrayService : IDisposable
{
    private const uint CmdExpandCollapse = 1001;
    private const uint CmdOpenSettings = 1002;
    private const uint CmdToggleZenNotifications = 1003;
    private const uint CmdResetPosition = 1004;
    private const uint CmdQuit = 1005;

    private readonly IIslandStateManager _stateManager;
    private readonly ILogger _logger;
    private readonly NativeTrayIconHost _trayHost;
    private IslandWindow? _islandWindow;
    private Action? _shutdownCallback;
    private Action? _openSettingsCallback;
    private bool _disposed;

    public SystemTrayService(IIslandStateManager stateManager, ILogger logger)
    {
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _trayHost = new NativeTrayIconHost(BuildMenuItems);
        _trayHost.PrimaryActionInvoked += OnPrimaryActionInvoked;
        _trayHost.MenuItemClicked += OnMenuItemClicked;
    }

    public void Attach(
        IslandWindow islandWindow,
        Action shutdownCallback,
        Action? openSettingsCallback = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _islandWindow = islandWindow ?? throw new ArgumentNullException(nameof(islandWindow));
        _shutdownCallback = shutdownCallback ?? throw new ArgumentNullException(nameof(shutdownCallback));
        _openSettingsCallback = openSettingsCallback;

        if (islandWindow.WindowHandle == 0)
        {
            throw new InvalidOperationException("IslandWindow handle must be initialized before attaching tray icon.");
        }

        islandWindow.AddWindowMessageHook(_trayHost.HandleWindowMessage);
        _trayHost.Attach(islandWindow.WindowHandle, "Shima Dynamic Island");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _islandWindow?.RemoveWindowMessageHook(_trayHost.HandleWindowMessage);
        _trayHost.PrimaryActionInvoked -= OnPrimaryActionInvoked;
        _trayHost.MenuItemClicked -= OnMenuItemClicked;
        _trayHost.Dispose();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenuItems()
    {
        var expandLabel = _stateManager.CurrentState == IslandShellState.Expanded
            ? "Collapse Island"
            : "Expand Island";

        return
        [
            new TrayMenuItem(CmdExpandCollapse, expandLabel),
            new TrayMenuItem(CmdOpenSettings, "Open Settings"),
            new TrayMenuItem(CmdToggleZenNotifications, "Pause Notifications / Zen Mode", IsChecked: _stateManager.IsNotificationsPaused),
            new TrayMenuItem(CmdResetPosition, "Reset Island Position"),
            new TrayMenuItem(0, string.Empty, IsSeparator: true),
            new TrayMenuItem(CmdQuit, "Quit Shima")
        ];
    }

    private void OnPrimaryActionInvoked(object? sender, EventArgs e) => ToggleIslandExpansion();

    private void OnMenuItemClicked(object? sender, uint commandId)
    {
        switch (commandId)
        {
            case CmdExpandCollapse:
                ToggleIslandExpansion();
                break;

            case CmdOpenSettings:
                if (_openSettingsCallback is not null)
                {
                    _openSettingsCallback();
                }
                else
                {
                    _logger.Info("Settings window trigger invoked from system tray, but no settings handler is attached.");
                }

                break;

            case CmdToggleZenNotifications:
                _stateManager.IsNotificationsPaused = !_stateManager.IsNotificationsPaused;
                break;

            case CmdResetPosition:
                _islandWindow?.ResetPosition();
                break;

            case CmdQuit:
                _shutdownCallback?.Invoke();
                break;
        }
    }

    private void ToggleIslandExpansion()
    {
        if (_stateManager.CurrentState == IslandShellState.Expanded)
        {
            _stateManager.RequestUserCollapse(force: true);
        }
        else
        {
            _stateManager.RequestUserExpand();
        }
    }
}
