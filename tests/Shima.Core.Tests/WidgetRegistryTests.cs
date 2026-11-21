using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Widgets;
using Xunit;

namespace Shima.Core.Tests;

public sealed class WidgetRegistryTests
{
    [Fact]
    public async Task InitializeAsync_InitializesOnlyEnabledWidgets_AndCreatesMissingConfigs()
    {
        var repository = new InMemoryWidgetConfigRepository(
        [
            new WidgetConfig { WidgetId = "shima.test.disabled", IsEnabled = false, SortOrder = 2 }
        ]);
        var enabled = new FakeWidget("shima.test.enabled", 0);
        var missing = new FakeWidget("shima.test.missing", 1);
        var disabled = new FakeWidget("shima.test.disabled", 2);

        await using var registry = CreateRegistry(repository, enabled, missing, disabled);
        await registry.InitializeAsync();

        enabled.InitializeCount.Should().Be(1);
        missing.InitializeCount.Should().Be(1);
        disabled.InitializeCount.Should().Be(0);
        (await repository.GetAllAsync()).Should().Contain(
            config => config.WidgetId == "shima.test.missing" && config.IsEnabled);
    }

    [Fact]
    public async Task InitializeAsync_WhenWidgetThrows_MarksItFaultedAndContinues()
    {
        var repository = new InMemoryWidgetConfigRepository();
        var faulty = new FakeWidget("shima.test.faulty", 0) { FailInitialize = true };
        var healthy = new FakeWidget("shima.test.healthy", 1);

        await using var registry = CreateRegistry(repository, faulty, healthy);
        await registry.InitializeAsync();

        healthy.InitializeCount.Should().Be(1);
        registry.Widgets.Single(widget => widget.Metadata.WidgetId == "shima.test.faulty")
            .IsFaulted.Should().BeTrue();
        registry.Widgets.Single(widget => widget.Metadata.WidgetId == "shima.test.healthy")
            .IsFaulted.Should().BeFalse();
        registry.CreateExpandedView("shima.test.faulty").Should().BeOfType<FaultedWidgetFallbackView>();
        registry.CreateExpandedView("shima.test.healthy").Should().Be("shima.test.healthy-view");
    }

    [Fact]
    public async Task ReloadAsync_ClearsFaultAndReinitializesWidget()
    {
        var repository = new InMemoryWidgetConfigRepository();
        var widget = new FakeWidget("shima.test.reload", 0) { FailInitialize = true };

        await using var registry = CreateRegistry(repository, widget);
        await registry.InitializeAsync();
        registry.Widgets.Single().IsFaulted.Should().BeTrue();

        widget.FailInitialize = false;
        await registry.ReloadAsync("shima.test.reload");

        registry.Widgets.Single().IsFaulted.Should().BeFalse();
        widget.InitializeCount.Should().Be(2);
    }

    [Fact]
    public async Task SetEnabledAsync_PersistsState_AndDisposesWidget()
    {
        var repository = new InMemoryWidgetConfigRepository();
        var widget = new FakeWidget("shima.test.toggle", 0);

        await using var registry = CreateRegistry(repository, widget);
        await registry.InitializeAsync();

        await registry.SetEnabledAsync("shima.test.toggle", false);

        widget.DisposeCount.Should().Be(1);
        (await repository.GetAllAsync()).Single().IsEnabled.Should().BeFalse();

        await registry.SetEnabledAsync("shima.test.toggle", true);

        widget.InitializeCount.Should().Be(2);
        (await repository.GetAllAsync()).Single().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_WhenWidgetThrows_IsolatesFaultAndKeepsOtherWidgetsUsable()
    {
        var repository = new InMemoryWidgetConfigRepository();
        var faulty = new FakeWidget("shima.test.activate-fault", 0) { FailActivate = true };
        var healthy = new FakeWidget("shima.test.activate-ok", 1);

        await using var registry = CreateRegistry(repository, faulty, healthy);
        await registry.InitializeAsync();

        await registry.ActivateAsync("shima.test.activate-fault", WidgetActivationReason.UserSelection);
        registry.Widgets.Single(widget => widget.Metadata.WidgetId == "shima.test.activate-fault")
            .IsFaulted.Should().BeTrue();

        await registry.ActivateAsync("shima.test.activate-ok", WidgetActivationReason.UserSelection);
        registry.ActiveWidgetId.Should().Be("shima.test.activate-ok");
    }

    [Fact]
    public async Task OnShellStateChanged_PropagatesStateToInitializedWidgets()
    {
        var repository = new InMemoryWidgetConfigRepository();
        var widget = new FakeWidget("shima.test.state", 0);

        await using var registry = CreateRegistry(repository, widget);
        await registry.InitializeAsync();

        registry.OnShellStateChanged(IslandShellState.Expanded);

        widget.LastShellState.Should().Be(IslandShellState.Expanded);
    }

    private static WidgetRegistry CreateRegistry(
        InMemoryWidgetConfigRepository repository,
        params FakeWidget[] widgets)
    {
        var registrations = widgets.Select(
            widget => new WidgetModuleRegistration(widget.Metadata, () => widget));
        return new WidgetRegistry(
            registrations,
            repository,
            new NoOpLogger(),
            Substitute.For<IEventDispatcher>(),
            Substitute.For<IThemeTokenProvider>(),
            Substitute.For<IIslandAttentionRequester>());
    }

    private sealed class NoOpLogger : ILogger
    {
        public void Log(LogLevel level, string message, Exception? exception = null)
        {
        }
    }

    private sealed class FakeWidget : IWidgetModule
    {
        public FakeWidget(string widgetId, int defaultOrder)
        {
            Metadata = new WidgetDescriptor
            {
                WidgetId = widgetId,
                DisplayName = widgetId,
                DefaultOrder = defaultOrder
            };
        }

        public WidgetDescriptor Metadata { get; }

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool FailInitialize { get; set; }

        public bool FailActivate { get; set; }

        public IslandShellState? LastShellState { get; private set; }

        public Task InitializeAsync(IWidgetContext context, CancellationToken ct)
        {
            InitializeCount++;
            if (FailInitialize)
            {
                throw new InvalidOperationException("Widget initialization failed.");
            }

            return Task.CompletedTask;
        }

        public Task ActivateAsync(WidgetActivationReason reason)
        {
            if (FailActivate)
            {
                throw new InvalidOperationException("Widget activation failed.");
            }

            return Task.CompletedTask;
        }

        public Task DeactivateAsync() => Task.CompletedTask;

        public void OnShellStateChanged(IslandShellState newState) => LastShellState = newState;

        public void OnPowerProfileChanged(PowerProfile profile)
        {
        }

        public object CreateExpandedView() => $"{Metadata.WidgetId}-view";

        public object? CreateCompactActiveView() => null;

        public object CreateSettingsView() => $"{Metadata.WidgetId}-settings";

        public Task ResetSettingsAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryWidgetConfigRepository : IWidgetConfigRepository
    {
        private readonly Dictionary<string, WidgetConfig> _configs;

        public InMemoryWidgetConfigRepository(IEnumerable<WidgetConfig>? seed = null)
        {
            _configs = (seed ?? []).ToDictionary(config => config.WidgetId, StringComparer.Ordinal);
        }

        public Task<IReadOnlyList<WidgetConfig>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<WidgetConfig>>(_configs.Values.ToArray());

        public Task SaveAsync(WidgetConfig config)
        {
            _configs[config.WidgetId] = config;
            return Task.CompletedTask;
        }

        public Task UpdateOrderAsync(IReadOnlyList<string> orderedIds)
        {
            for (var index = 0; index < orderedIds.Count; index++)
            {
                if (_configs.TryGetValue(orderedIds[index], out var config))
                {
                    _configs[orderedIds[index]] = config with { SortOrder = index };
                }
            }

            return Task.CompletedTask;
        }
    }
}
