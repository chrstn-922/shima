using Shima.Core.Contracts;
using Shima.Core.Widgets;

namespace Shima.App;

/// <summary>
/// Bridges the Settings Widgets and Widget-Settings tabs to <see cref="WidgetRegistry"/> and <see cref="IWidgetConfigRepository"/>.
/// </summary>
public sealed class WidgetRegistrySettingsCoordinator : IWidgetSettingsCoordinator
{
    private readonly WidgetRegistry _registry;
    private readonly IWidgetConfigRepository _repository;

    public WidgetRegistrySettingsCoordinator(WidgetRegistry registry, IWidgetConfigRepository repository)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public event EventHandler? WidgetsChanged;

    public Task<IReadOnlyList<WidgetManagementItem>> GetWidgetsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var items = _registry.Widgets
            .Select(w => new WidgetManagementItem(
                w.Metadata.WidgetId,
                w.Metadata.DisplayName,
                w.IsEnabled,
                w.SortOrder,
                w.IsFaulted))
            .OrderBy(w => w.SortOrder)
            .ToArray();

        return Task.FromResult<IReadOnlyList<WidgetManagementItem>>(items);
    }

    public async Task SetEnabledAsync(string widgetId, bool isEnabled, CancellationToken ct = default)
    {
        await _registry.SetEnabledAsync(widgetId, isEnabled, ct).ConfigureAwait(false);
        WidgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetSettingsAsync(string widgetId, CancellationToken ct = default)
    {
        await _registry.ResetSettingsAsync(widgetId, ct).ConfigureAwait(false);
        WidgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateOrderAsync(IReadOnlyList<string> orderedWidgetIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWidgetIds);
        ct.ThrowIfCancellationRequested();
        await _repository.UpdateOrderAsync(orderedWidgetIds).ConfigureAwait(false);
        WidgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public object? CreateSettingsView(string widgetId)
    {
        var state = _registry.Widgets.FirstOrDefault(w => string.Equals(w.Metadata.WidgetId, widgetId, StringComparison.Ordinal));
        if (state is null || !state.IsEnabled || state.IsFaulted)
        {
            return null;
        }

        return _registry.CreateSettingsView(widgetId);
    }
}
