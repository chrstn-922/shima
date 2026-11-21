using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shima.Core.Contracts;
using Windows.ApplicationModel;

namespace Shima.App;

/// <summary>
/// Manages "Start with Windows" using WinRT StartupTask when Sparse MSIX Package Identity is active,
/// falling back cleanly to per-user HKCU\Software\Microsoft\Windows\CurrentVersion\Run without UAC elevation.
/// </summary>
public sealed class StartupRegistrationAdapter : IStartupRegistrationAdapter
{
    public const string StartupTaskId = "ShimaStartupTask";
    public const string RegistryRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RegistryValueName = "Shima";

    private readonly ILogger? _logger;

    public StartupRegistrationAdapter(ILogger? logger = null)
    {
        _logger = logger;
    }

    public bool UsesPackagedStartupTask => HasPackageIdentity();

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (UsesPackagedStartupTask)
        {
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                _logger?.Warn("Packaged StartupTask query failed; falling back to HKCU Run.", ex);
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKeyPath, writable: false);
        var value = key?.GetValue(RegistryValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public async Task<bool> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (UsesPackagedStartupTask)
        {
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                if (enabled)
                {
                    var state = await task.RequestEnableAsync();
                    return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                }

                task.Disable();
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                _logger?.Warn("Packaged StartupTask update failed; falling back to HKCU Run.", ex);
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(RegistryRunKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        if (!enabled)
        {
            key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        key.SetValue(RegistryValueName, $"\"{processPath}\"", RegistryValueKind.String);
        return true;
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
