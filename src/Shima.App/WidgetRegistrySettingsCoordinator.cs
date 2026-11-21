using Shima.Core.Contracts;
using Shima.Core.Widgets;

namespace Shima.App;

/// <summary>
/// Bridges the Settings Widgets section to <see cref="WidgetRegistry"/> and <see cref="IWidgetConfigRepository"/>.
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

    public Task SetEnabledAsync(string widgetId, bool isEnabled, CancellationToken ct = default) =>
        _registry.SetEnabledAsync(widgetId, isEnabled, ct);

    public Task ResetSettingsAsync(string widgetId, CancellationToken ct = default) =>
        _registry.ResetSettingsAsync(widgetId, ct);

    public Task UpdateOrderAsync(IReadOnlyList<string> orderedWidgetIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWidgetIds);
        ct.ThrowIfCancellationRequested();
        return _repository.UpdateOrderAsync(orderedWidgetIds);
    }
}
