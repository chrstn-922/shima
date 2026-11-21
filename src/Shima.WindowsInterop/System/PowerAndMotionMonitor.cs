using System.Runtime.InteropServices;
using Shima.Core.Contracts;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.System;

public sealed unsafe class PowerAndMotionMonitor : IPowerAndMotionMonitor
{
    private const int WmPowerBroadcast = (int)PInvoke.WM_POWERBROADCAST;
    private const int WmWtsSessionChange = (int)PInvoke.WM_WTSSESSION_CHANGE;

    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;

    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const uint NotifyForThisSession = 0;

    private readonly object _sync = new();
    private readonly List<HPOWERNOTIFY> _powerHandles = [];
    private nint _attachedHwnd;
    private bool _wtsRegistered;
    private UISettings? _uiSettings;

    private bool _isEnergySaver;
    private bool _isOnBattery;
    private bool _isDisplayOff;
    private PowerProfile _currentPowerProfile = PowerProfile.AC;
    private bool _isReducedMotionEnabled;
    private bool _isSessionLocked;
    private bool _disposed;

    public PowerProfile CurrentPowerProfile
    {
        get
        {
            lock (_sync)
            {
                return _currentPowerProfile;
            }
        }
    }

    public bool IsReducedMotionEnabled
    {
        get
        {
            lock (_sync)
            {
                return _isReducedMotionEnabled;
            }
        }
    }

    public bool IsSessionLocked
    {
        get
        {
            lock (_sync)
            {
                return _isSessionLocked;
            }
        }
    }

    public event EventHandler<PowerProfile>? PowerProfileChanged;

    public event EventHandler<bool>? ReducedMotionChanged;

    public event EventHandler<bool>? SessionLockChanged;

    public void Attach(nint hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _attachedHwnd = hwnd;

        if (hwnd != 0 && _powerHandles.Count == 0)
        {
            RegisterPowerGuid(hwnd, PInvoke.GUID_POWER_SAVING_STATUS);
            RegisterPowerGuid(hwnd, PInvoke.GUID_ACDC_POWER_SOURCE);
            RegisterPowerGuid(hwnd, PInvoke.GUID_CONSOLE_DISPLAY_STATE);
        }

        if (hwnd != 0 && !_wtsRegistered)
        {
            try
            {
                _wtsRegistered = PInvoke.WTSRegisterSessionNotification(new HWND(hwnd), NotifyForThisSession);
            }
            catch
            {
                _wtsRegistered = false;
            }
        }

        if (_uiSettings is null)
        {
            try
            {
                _uiSettings = new UISettings();
                _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
                UpdateReducedMotion(!_uiSettings.AnimationsEnabled);
            }
            catch
            {
                UpdateReducedMotion(false);
            }
        }
    }

    public void HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (_disposed)
        {
            return;
        }

        if (msg == WmWtsSessionChange)
        {
            var code = (int)wParam;
            if (code == WtsSessionLock)
            {
                SetSessionLocked(true);
            }
            else if (code == WtsSessionUnlock)
            {
                SetSessionLocked(false);
            }

            return;
        }

        if (msg != WmPowerBroadcast)
        {
            return;
        }

        var powerEvent = (int)wParam;
        if (powerEvent == PbtApmSuspend)
        {
            lock (_sync)
            {
                _isDisplayOff = true;
            }

            RecomputePowerProfile();
        }
        else if (powerEvent == PbtApmResumeAutomatic)
        {
            lock (_sync)
            {
                _isDisplayOff = false;
            }

            RecomputePowerProfile();
        }
        else if (powerEvent == PbtPowerSettingChange && lParam != 0)
        {
            var setting = (POWERBROADCAST_SETTING*)lParam;
            if (setting->DataLength >= 1)
            {
                var value = setting->Data[0];
                lock (_sync)
                {
                    if (setting->PowerSetting == PInvoke.GUID_POWER_SAVING_STATUS)
                    {
                        _isEnergySaver = value != 0;
                    }
                    else if (setting->PowerSetting == PInvoke.GUID_ACDC_POWER_SOURCE)
                    {
                        // PoAc = 0, PoDc = 1, PoHot = 2
                        _isOnBattery = value == 1;
                    }
                    else if (setting->PowerSetting == PInvoke.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        // PowerMonitorOff = 0, PowerMonitorOn = 1, PowerMonitorDim = 2
                        _isDisplayOff = value == 0;
                    }
                }

                RecomputePowerProfile();
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

        foreach (var handle in _powerHandles)
        {
            if (handle.Value != 0)
            {
                PInvoke.UnregisterPowerSettingNotification(handle);
            }
        }

        _powerHandles.Clear();

        if (_wtsRegistered && _attachedHwnd != 0)
        {
            PInvoke.WTSUnRegisterSessionNotification(new HWND(_attachedHwnd));
            _wtsRegistered = false;
        }

        if (_uiSettings is not null)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            _uiSettings = null;
        }
    }

    private void RegisterPowerGuid(nint hwnd, Guid powerGuid)
    {
        var notify = PInvoke.RegisterPowerSettingNotification(
            new HANDLE(hwnd),
            &powerGuid,
            REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (notify.Value != 0)
        {
            _powerHandles.Add(notify);
        }
    }

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        try
        {
            UpdateReducedMotion(!sender.AnimationsEnabled);
        }
        catch
        {
            UpdateReducedMotion(false);
        }
    }

    private void UpdateReducedMotion(bool reducedMotion)
    {
        bool changed;
        lock (_sync)
        {
            changed = _isReducedMotionEnabled != reducedMotion;
            _isReducedMotionEnabled = reducedMotion;
        }

        if (changed)
        {
            ReducedMotionChanged?.Invoke(this, reducedMotion);
        }
    }

    private void SetSessionLocked(bool locked)
    {
        bool changed;
        lock (_sync)
        {
            changed = _isSessionLocked != locked;
            _isSessionLocked = locked;
        }

        if (changed)
        {
            SessionLockChanged?.Invoke(this, locked);
        }
    }

    private void RecomputePowerProfile()
    {
        PowerProfile next;
        bool changed;

        lock (_sync)
        {
            next = _isDisplayOff
                ? PowerProfile.DisplayOff
                : _isEnergySaver
                    ? PowerProfile.EnergySaver
                    : _isOnBattery
                        ? PowerProfile.Battery
                        : PowerProfile.AC;

            changed = _currentPowerProfile != next;
            _currentPowerProfile = next;
        }

        if (changed)
        {
            PowerProfileChanged?.Invoke(this, next);
        }
    }
}
