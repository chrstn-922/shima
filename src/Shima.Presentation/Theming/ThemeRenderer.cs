using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;

namespace Shima.Presentation.Theming;

/// <summary>
/// Propagates active <see cref="AppearanceConfiguration"/> tokens from <see cref="IAppearanceManager"/>
/// into attached WPF <see cref="ResourceDictionary"/> instances using VSync-coalesced CompositionTarget.Rendering.
/// Detaches the rendering hook immediately after each flush to preserve 0.0% steady-state idle CPU.
/// </summary>
public sealed class ThemeRenderer : IDisposable
{
    private readonly IAppearanceManager _appearanceManager;
    private readonly IColorProcessingService _colorService;
    private readonly Dispatcher _dispatcher;
    private readonly List<WeakReference<ResourceDictionary>> _targets = [];
    private readonly object _sync = new();

    private bool _pendingFlush;
    private bool _renderHookAttached;
    private bool _disposed;

    public ThemeRenderer(
        IAppearanceManager appearanceManager,
        IColorProcessingService colorService,
        Dispatcher? dispatcher = null)
    {
        _appearanceManager = appearanceManager ?? throw new ArgumentNullException(nameof(appearanceManager));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _dispatcher = dispatcher ?? ResolveLiveDispatcher();

        _appearanceManager.ThemeChanged += OnThemeChanged;
    }

    private static Dispatcher ResolveLiveDispatcher()
    {
        var app = Application.Current;
        if (app is { Dispatcher: { HasShutdownStarted: false, HasShutdownFinished: false } })
        {
            return app.Dispatcher;
        }

        return Dispatcher.CurrentDispatcher;
    }

    public bool IsRenderingHookAttached => _renderHookAttached;

    public event EventHandler? TokensRendered;

    public void AttachResourceDictionary(ResourceDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        lock (_sync)
        {
            CleanupDeadTargetsLocked();
            foreach (var weak in _targets)
            {
                if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, dictionary))
                {
                    return;
                }
            }

            _targets.Add(new WeakReference<ResourceDictionary>(dictionary));
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyDictionaryTokens(dictionary, _appearanceManager.ActiveRendered);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ApplyDictionaryTokens(dictionary, _appearanceManager.ActiveRendered));
        }
    }

    public void AttachFrameworkElement(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        AttachResourceDictionary(element.Resources);
    }

    public void ApplyNow()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            FlushCore();
        }
        else
        {
            _dispatcher.Invoke(FlushCore);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ScheduleCoalescedFlush();
        }
        else
        {
            _dispatcher.BeginInvoke(ScheduleCoalescedFlush, DispatcherPriority.Render);
        }
    }

    private void ScheduleCoalescedFlush()
    {
        if (_disposed)
        {
            return;
        }

        _pendingFlush = true;
        if (!_renderHookAttached)
        {
            _renderHookAttached = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        DetachRenderHook();

        if (_disposed || !_pendingFlush)
        {
            return;
        }

        FlushCore();
    }

    private void DetachRenderHook()
    {
        if (_renderHookAttached)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _renderHookAttached = false;
        }
    }

    private void FlushCore()
    {
        _pendingFlush = false;
        DetachRenderHook();

        var config = _appearanceManager.ActiveRendered;
        List<ResourceDictionary> activeTargets;

        lock (_sync)
        {
            CleanupDeadTargetsLocked();
            activeTargets = new List<ResourceDictionary>(_targets.Count);
            foreach (var weak in _targets)
            {
                if (weak.TryGetTarget(out var dict))
                {
                    activeTargets.Add(dict);
                }
            }
        }

        foreach (var dict in activeTargets)
        {
            ApplyDictionaryTokens(dict, config);
        }

        TokensRendered?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDictionaryTokens(ResourceDictionary root, AppearanceConfiguration config)
    {
        var previewCollapsed = !config.LinkCollapsedAndExpanded
            && _appearanceManager.ActiveSession?.CurrentPreviewState == PreviewTargetState.Collapsed;

        var activeChrome = previewCollapsed ? config.CollapsedTokens : config.ExpandedTokens;

        var collapsedColor = ParseMediaColor(config.CollapsedTokens.BackgroundHex, Colors.Black);
        var expandedColor = ParseMediaColor(config.ExpandedTokens.BackgroundHex, Colors.Black);
        var accentColor = ParseMediaColor(activeChrome.AccentHex, Colors.White);
        var borderColor = ParseMediaColor(activeChrome.BorderHex, Color.FromRgb(0x1F, 0x1F, 0x24));
        var glowBase = ParseMediaColor(activeChrome.GlowHex, Colors.Black);
        var glowAlpha = (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(config.GlowOpacity, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero),
            0,
            255);
        var glowColor = Color.FromArgb(glowAlpha, glowBase.R, glowBase.G, glowBase.B);
        var textPrimaryColor = ParseMediaColor(activeChrome.PrimaryTextHex, Color.FromRgb(0xF8, 0xFA, 0xFC));
        var textMutedColor = ParseMediaColor(activeChrome.SecondaryIconHex, Color.FromRgb(0x94, 0xA3, 0xB8));

        var expandedLuminance = _colorService.CalculateRelativeLuminance(expandedColor.R, expandedColor.G, expandedColor.B);
        var isLightExpanded = expandedLuminance > 0.45;
        var focusRingColor = isLightExpanded
            ? Color.FromRgb(0x0F, 0x17, 0x2A)
            : Colors.White;

        var scrollTrackColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        var scrollThumbColor = isLightExpanded
            ? Color.FromArgb(0x47, 0x47, 0x55, 0x69)
            : Color.FromArgb(0x5C, 0x64, 0x74, 0x8B);
        var scrollThumbHoverColor = isLightExpanded
            ? Color.FromArgb(0x7A, 0x33, 0x41, 0x55)
            : Color.FromArgb(0x99, 0x94, 0xA3, 0xB8);
        var scrollThumbActiveColor = isLightExpanded
            ? Color.FromArgb(0xB3, 0x1E, 0x29, 0x3B)
            : Color.FromArgb(0xD9, 0xCB, 0xD5, 0xE1);

        var updates = new (string ColorKey, string BrushKey, Color Value)[]
        {
            ("SurfaceCollapsedColor", "SurfaceCollapsed", collapsedColor),
            ("SurfaceExpandedColor", "SurfaceExpanded", expandedColor),
            ("AccentColor", "AccentBrush", accentColor),
            ("BorderHairlineColor", "BorderHairline", borderColor),
            ("GlowColor", "GlowBrush", glowColor),
            ("TextPrimaryColor", "TextPrimary", textPrimaryColor),
            ("TextMutedColor", "TextMuted", textMutedColor),
            ("FocusRingColor", "FocusRingBrush", focusRingColor),
            ("ScrollBarTrackColor", "ScrollBarTrackBrush", scrollTrackColor),
            ("ScrollBarThumbColor", "ScrollBarThumbBrush", scrollThumbColor),
            ("ScrollBarThumbHoverColor", "ScrollBarThumbHoverBrush", scrollThumbHoverColor),
            ("ScrollBarThumbActiveColor", "ScrollBarThumbActiveBrush", scrollThumbActiveColor)
        };

        var borderThickness = new Thickness(Math.Clamp(config.BorderThicknessDip, 0.0, 4.0));
        ApplyUpdatesRecursive(root, updates, borderThickness);
    }

    private static void ApplyUpdatesRecursive(
        ResourceDictionary dict,
        (string ColorKey, string BrushKey, Color Value)[] updates,
        Thickness borderThickness)
    {
        foreach (var merged in dict.MergedDictionaries)
        {
            ApplyUpdatesRecursive(merged, updates, borderThickness);
        }

        foreach (var (colorKey, brushKey, value) in updates)
        {
            if (dict.Contains(colorKey) || dict.MergedDictionaries.Count == 0)
            {
                dict[colorKey] = value;
            }

            if (dict.Contains(brushKey) || dict.MergedDictionaries.Count == 0)
            {
                var brush = new SolidColorBrush(value);
                brush.Freeze();
                dict[brushKey] = brush;
            }
        }

        if (dict.Contains("IslandBorderThickness") || dict.MergedDictionaries.Count == 0)
        {
            dict["IslandBorderThickness"] = borderThickness;
        }
    }

    private Color ParseMediaColor(string hex, Color fallback)
    {
        if (_colorService.TryParseHex(hex, out var parsed) && parsed is not null)
        {
            return Color.FromRgb(parsed.R, parsed.G, parsed.B);
        }

        return fallback;
    }

    private void CleanupDeadTargetsLocked()
    {
        for (var i = _targets.Count - 1; i >= 0; i--)
        {
            if (!_targets[i].TryGetTarget(out _))
            {
                _targets.RemoveAt(i);
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
        _appearanceManager.ThemeChanged -= OnThemeChanged;

        if (_dispatcher.CheckAccess())
        {
            DetachRenderHook();
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(DetachRenderHook);
        }
    }
}
