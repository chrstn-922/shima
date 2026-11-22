using System.IO;
using IoPath = System.IO.Path;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Services;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Settings;
using Shima.Presentation.Theming;
using Shima.WindowsInterop.Overlay;
using Xunit;

namespace Shima.UI.Tests;

[Collection("IslandShellUi")]
public sealed class ReleaseVerificationUiTests
{
    [Fact]
    public async Task SettingsWindow_WiresUpdateOfflineCheck_AndOfflineLicenseActivationFlow()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();

        var rootDir = IoPath.Combine(IoPath.GetTempPath(), "shima-release-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);

        try
        {
            var prefsStore = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(rootDir));
            await prefsStore.SaveAsync(new AppPreferences { UpdatesOfflineMode = true });
            var prefsService = new AppPreferencesService(prefsStore, new DpapiSecretStore());
            await prefsService.InitializeAsync();

            var colorService = new ColorProcessingService();
            var pinController = new OverlayPinController();
            var presetManager = new PresetManager(
                new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(rootDir)),
                colorService);
            var appearanceManager = new AppearanceManager(
                new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(rootDir)),
                presetManager,
                colorService,
                pinController);
            await appearanceManager.InitializeAsync();
            var diagService = new DiagnosticLogService(IoPath.Combine(rootDir, "logs"));

            using var updateService = new UpdateServiceAdapter(
                prefsService,
                rootDir,
                manifestEndpointUrl: "https://updates.shima.test/manifest.json",
                currentVersion: "1.0.0",
                currentArchitecture: "win-x64");

            using var licenseService = new LicenseServiceAdapter(
                prefsService,
                verificationPublicKeyPem: publicPem,
                machineIdentityProvider: () => "release-ui-test-machine");

            using var host = new StaWpfTestHost();
            var window = await host.RunAsync(() =>
            {
                var themeRenderer = new ThemeRenderer(appearanceManager, colorService, Dispatcher.CurrentDispatcher);
                var settings = new SettingsWindow(
                    appearanceManager,
                    presetManager,
                    colorService,
                    prefsService,
                    diagService,
                    themeRenderer: themeRenderer,
                    updateService: updateService,
                    licenseService: licenseService);
                settings.Show();
                return settings;
            });

            await host.PumpAsync();

            // 1. Verify Updates panel offline mode check
            var (installedText, updateStatusText, checkUpdatesButton) = await host.RunAsync(() =>
            {
                window.SelectSection("Updates");
                return (
                    Find<TextBlock>(window, "InstalledVersionText"),
                    Find<TextBlock>(window, "UpdateStatusText"),
                    Find<Button>(window, "CheckForUpdatesButton"));
            });
            await host.PumpAsync();

            (await host.RunAsync(() => installedText.Text)).Should().Contain("1.0.0").And.Contain("win-x64");
            await host.RunAsync(() => InvokeButton(checkUpdatesButton));
            await WaitUntilAsync(
                () => host.RunAsync(() => updateStatusText.Text.Contains("Offline mode is enabled", StringComparison.Ordinal)).GetAwaiter().GetResult(),
                () => host.PumpAsync());

            // 2. Verify License panel offline signed token activation + clear
            var signedToken = LicenseServiceAdapter.CreateSignedToken(
                new SignedLicensePayload(
                    "LIC-UI-01",
                    "Pro",
                    "UI Verifier",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddDays(30),
                    licenseService.ComputeHardwareFingerprintHash()),
                signingKey);

            var (licenseBox, licenseStateText, clearButton, saveButton) = await host.RunAsync(() =>
            {
                window.SelectSection("License");
                return (
                    Find<PasswordBox>(window, "LicenseKeyBox"),
                    Find<TextBlock>(window, "LicenseStateText"),
                    Find<Button>(window, "ClearLicenseKeyButton"),
                    FindByAutomationId<Button>(window, "SaveLicenseKeyButton"));
            });

            await host.RunAsync(() =>
            {
                licenseBox.Password = signedToken;
                InvokeButton(saveButton);
            });

            await WaitUntilAsync(
                () => prefsService.Current.HasLicenseKey,
                () => host.PumpAsync());

            (await host.RunAsync(() => licenseStateText.Text)).Should().Contain("Pro").And.Contain("UI Verifier");
            (await host.RunAsync(() => clearButton.IsEnabled)).Should().BeTrue();

            await host.RunAsync(() => InvokeButton(clearButton));
            await WaitUntilAsync(
                () => !prefsService.Current.HasLicenseKey,
                () => host.PumpAsync());

            (await host.RunAsync(() => licenseStateText.Text)).Should().Contain("Community offline edition");
            await host.RunAsync(() => window.Close());
        }
        finally
        {
            try
            {
                Directory.Delete(rootDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void OverlayGeometry_MultiDpiTopCenterAnchor_StaysCenteredAndWithinMonitorBounds(double dpiScale)
    {
        var primaryBounds = new MonitorBounds(0, 0, 1920, 1080);
        var secondaryBounds = new MonitorBounds(1920, 0, 2560, 1440);

        foreach (var bounds in new[] { primaryBounds, secondaryBounds })
        {
            var collapsed = OverlayWindowAdapter.CalculateAnchoredPixelRect(bounds, dpiScale, new DipSize(136, 28));
            var expanded = OverlayWindowAdapter.CalculateAnchoredPixelRect(bounds, dpiScale, new DipSize(412, 168));

            collapsed.Top.Should().Be(bounds.Top);
            expanded.Top.Should().Be(bounds.Top);

            collapsed.Width.Should().Be((int)Math.Round(136 * dpiScale, MidpointRounding.AwayFromZero));
            expanded.Width.Should().Be((int)Math.Round(412 * dpiScale, MidpointRounding.AwayFromZero));

            collapsed.Left.Should().BeGreaterOrEqualTo(bounds.Left);
            expanded.Left.Should().BeGreaterOrEqualTo(bounds.Left);
            (expanded.Left + expanded.Width).Should().BeLessOrEqualTo(bounds.Left + bounds.Width);

            var monitorCenter = bounds.Left + bounds.Width / 2.0;
            var expandedCenter = expanded.Left + expanded.Width / 2.0;
            Math.Abs(expandedCenter - monitorCenter).Should().BeLessOrEqualTo(1.0);
        }
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T
            ?? throw new InvalidOperationException($"Named element '{name}' of type {typeof(T).Name} was not found.");

    private static T FindByAutomationId<T>(DependencyObject root, string automationId) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T typed
                && string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(current), automationId, StringComparison.Ordinal))
            {
                return typed;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(current, i));
            }
        }

        throw new InvalidOperationException($"Element with AutomationId '{automationId}' was not found.");
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
        {
            invoker.Invoke();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<Task> pump)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await pump();
            await Task.Delay(15);
        }

        await pump();
        condition().Should().BeTrue();
    }

    private sealed class StaWpfTestHost : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public StaWpfTestHost()
        {
            _thread = new Thread(() =>
            {
                if (Application.Current is null)
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcherReady.SetResult(dispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public async Task<T> RunAsync<T>(Func<T> func)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(func).Task.ConfigureAwait(false);
        }

        public async Task RunAsync(Action action)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }

        public async Task PumpAsync()
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_dispatcherReady.Task.IsCompletedSuccessfully)
            {
                var dispatcher = _dispatcherReady.Task.Result;
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }

            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
