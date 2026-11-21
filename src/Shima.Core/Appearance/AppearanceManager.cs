using Shima.Core.Color;
using Shima.Core.Contracts;

namespace Shima.Core.Appearance;

/// <summary>
/// Central appearance state coordinator implementing live preview sessions, Collapsed/Expanded linking,
/// slot-scoped reset, non-blocking WCAG contrast audits, and recent-color updates in PresetCatalog.
/// </summary>
public sealed class AppearanceManager : IAppearanceManager
{
    private readonly IAppearanceConfigRepository _configRepository;
    private readonly IPresetManager _presetManager;
    private readonly IColorProcessingService _colorService;
    private readonly IOverlayPinController? _pinController;
    private readonly object _sync = new();

    private AppearanceConfiguration _committed = AppearanceConfiguration.Default;
    private AppearanceConfiguration _rendered = AppearanceConfiguration.Default;
    private EditingSession? _activeSession;

    public AppearanceManager(
        IAppearanceConfigRepository configRepository,
        IPresetManager presetManager,
        IColorProcessingService colorService,
        IOverlayPinController? pinController = null)
    {
        _configRepository = configRepository ?? throw new ArgumentNullException(nameof(configRepository));
        _presetManager = presetManager ?? throw new ArgumentNullException(nameof(presetManager));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _pinController = pinController;
    }

    public AppearanceConfiguration CurrentCommitted
    {
        get
        {
            lock (_sync)
            {
                return _committed;
            }
        }
    }

    public AppearanceConfiguration ActiveRendered
    {
        get
        {
            lock (_sync)
            {
                return _rendered;
            }
        }
    }

    public ContrastAuditReport CurrentContrastReport
    {
        get
        {
            lock (_sync)
            {
                return _colorService.ValidateConfigurationContrast(_rendered);
            }
        }
    }

    public IAppearanceEditingSession? ActiveSession
    {
        get
        {
            lock (_sync)
            {
                return _activeSession;
            }
        }
    }

    public event EventHandler? ThemeChanged;

    public event EventHandler<AppearanceConfiguration>? AppearanceChanged;

    public event EventHandler<AppearanceConfiguration>? RenderedAppearanceChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _presetManager.InitializeAsync(ct).ConfigureAwait(false);
        var loaded = await _configRepository.LoadAsync(ct).ConfigureAwait(false);
        var normalized = SanitizeConfiguration(loaded);

        lock (_sync)
        {
            _committed = normalized;
            _rendered = normalized;
        }

        NotifySubscribers(normalized, emitCommittedChanged: true);
    }

    public IAppearanceEditingSession BeginEditingSession(
        bool acquireLivePreviewPin = true,
        PreviewTargetState initialPreviewState = PreviewTargetState.Expanded)
    {
        EditingSession? previous;
        EditingSession next;
        PinToken? pinToken = null;

        if (acquireLivePreviewPin && _pinController is not null)
        {
            pinToken = _pinController.AcquirePin(PinReason.ColorPickerPreview, initialPreviewState);
        }

        lock (_sync)
        {
            previous = _activeSession;
            next = new EditingSession(this, _committed, pinToken, initialPreviewState);
            _activeSession = next;
        }

        previous?.Cancel();
        return next;
    }

    public async Task ApplyPresetAsync(string presetId, CancellationToken ct = default)
    {
        var preset = _presetManager.FindById(presetId)
            ?? throw new KeyNotFoundException($"Preset '{presetId}' was not found.");

        AppearanceConfiguration updated;
        lock (_sync)
        {
            updated = ApplyPresetToConfiguration(_committed, preset);
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetLinkCollapsedAndExpandedAsync(bool linked, CancellationToken ct = default)
    {
        AppearanceConfiguration updated;
        lock (_sync)
        {
            updated = _committed with
            {
                LinkCollapsedAndExpanded = linked,
                ExpandedTokens = linked ? _committed.CollapsedTokens : _committed.ExpandedTokens
            };
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetUsePerWidgetAccentsAsync(bool enabled, CancellationToken ct = default)
    {
        AppearanceConfiguration updated;
        lock (_sync)
        {
            updated = _committed with { UsePerWidgetAccents = enabled };
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetGlowOpacityAsync(double glowOpacity, CancellationToken ct = default)
    {
        AppearanceConfiguration updated;
        lock (_sync)
        {
            updated = _committed with { GlowOpacity = Math.Clamp(glowOpacity, 0.0, 1.0) };
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetBorderThicknessAsync(double borderThicknessDip, CancellationToken ct = default)
    {
        AppearanceConfiguration updated;
        lock (_sync)
        {
            updated = _committed with { BorderThicknessDip = Math.Clamp(borderThicknessDip, 0.0, 4.0) };
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetWidgetAccentAsync(string widgetId, string? hex, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);

        string? normalizedHex = null;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            normalizedHex = _colorService.FromHex(hex).Hex;
        }

        AppearanceConfiguration updated;
        lock (_sync)
        {
            var dict = new Dictionary<string, string>(_committed.PerWidgetAccents, StringComparer.Ordinal);
            if (normalizedHex is null)
            {
                dict.Remove(widgetId);
            }
            else
            {
                dict[widgetId] = normalizedHex;
            }

            updated = _committed with { PerWidgetAccents = dict };
        }

        if (normalizedHex is not null)
        {
            await _presetManager.RecordRecentColorAsync(normalizedHex, ct).ConfigureAwait(false);
        }

        await PersistCommittedAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task<AppearanceConfiguration> ResetAllToDefaultsAsync(CancellationToken ct = default)
    {
        var sessionToCancel = ActiveSession;
        sessionToCancel?.Cancel();

        var reset = AppearanceConfiguration.Default;
        await PersistCommittedAsync(reset, ct).ConfigureAwait(false);
        return reset;
    }

    public string GetToken(string tokenName)
    {
        var rendered = ActiveRendered;
        var previewCollapsed = !rendered.LinkCollapsedAndExpanded
            && ActiveSession?.CurrentPreviewState == PreviewTargetState.Collapsed;

        var surface = previewCollapsed ? rendered.CollapsedTokens : rendered.ExpandedTokens;

        return tokenName switch
        {
            "SurfaceCollapsed" or "SurfaceCollapsedColor" => rendered.CollapsedTokens.BackgroundHex,
            "SurfaceExpanded" or "SurfaceExpandedColor" => rendered.ExpandedTokens.BackgroundHex,
            "BackgroundHex" => surface.BackgroundHex,
            "AccentBrush" or "AccentColor" or "AccentHex" => surface.AccentHex,
            "BorderHairline" or "BorderHairlineColor" or "BorderHex" => surface.BorderHex,
            "GlowBrush" or "GlowColor" or "GlowHex" => surface.GlowHex,
            "TextPrimary" or "TextPrimaryColor" or "PrimaryTextHex" => surface.PrimaryTextHex,
            "TextMuted" or "TextMutedColor" or "SecondaryIconHex" => surface.SecondaryIconHex,
            "SurfaceElevated" => "#141414",
            "FocusRingBrush" => "#FFFFFF",
            _ => surface.AccentHex
        };
    }

    public string GetWidgetAccent(string widgetId)
    {
        var rendered = ActiveRendered;
        if (rendered.UsePerWidgetAccents
            && !string.IsNullOrWhiteSpace(widgetId)
            && rendered.PerWidgetAccents.TryGetValue(widgetId, out var overrideHex))
        {
            return overrideHex;
        }

        return rendered.ExpandedTokens.AccentHex;
    }

    private static AppearanceConfiguration ApplyPresetToConfiguration(AppearanceConfiguration baseline, ColorPreset preset) =>
        baseline with
        {
            ActivePresetId = preset.PresetId,
            CollapsedTokens = preset.CollapsedTokens,
            ExpandedTokens = baseline.LinkCollapsedAndExpanded ? preset.CollapsedTokens : preset.ExpandedTokens
        };

    private async Task PersistCommittedAsync(AppearanceConfiguration updated, CancellationToken ct)
    {
        var sanitized = SanitizeConfiguration(updated);
        await _configRepository.SaveAsync(sanitized, ct).ConfigureAwait(false);

        lock (_sync)
        {
            _committed = sanitized;
            if (_activeSession is null)
            {
                _rendered = sanitized;
            }
        }

        NotifySubscribers(ActiveRendered, emitCommittedChanged: true);
    }

    private void UpdateLivePreview(EditingSession session, AppearanceConfiguration draft)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeSession, session))
            {
                return;
            }

            _rendered = draft;
        }

        NotifySubscribers(draft, emitCommittedChanged: false);
    }

    private async Task<AppearanceConfiguration> CommitSessionAsync(
        EditingSession session,
        AppearanceConfiguration draft,
        IReadOnlyList<string> pendingRecentHexes,
        PinToken? pinToken,
        CancellationToken ct)
    {
        var finalConfig = SanitizeConfiguration(draft);
        await _configRepository.SaveAsync(finalConfig, ct).ConfigureAwait(false);

        foreach (var hex in pendingRecentHexes)
        {
            await _presetManager.RecordRecentColorAsync(hex, ct).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _committed = finalConfig;
            _rendered = finalConfig;
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
        }

        ReleasePinSafe(pinToken);
        NotifySubscribers(finalConfig, emitCommittedChanged: true);
        return finalConfig;
    }

    private void CancelSession(EditingSession session, AppearanceConfiguration snapshot, PinToken? pinToken)
    {
        var changed = false;
        lock (_sync)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
                _rendered = snapshot;
                changed = true;
            }
        }

        ReleasePinSafe(pinToken);
        if (changed)
        {
            NotifySubscribers(snapshot, emitCommittedChanged: false);
        }
    }

    private void ReleasePinSafe(PinToken? pinToken)
    {
        if (pinToken is not null)
        {
            _pinController?.ReleasePin(pinToken);
        }
    }

    private void NotifySubscribers(AppearanceConfiguration rendered, bool emitCommittedChanged)
    {
        RenderedAppearanceChanged?.Invoke(this, rendered);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        if (emitCommittedChanged)
        {
            AppearanceChanged?.Invoke(this, rendered);
        }
    }

    private AppearanceConfiguration SanitizeConfiguration(AppearanceConfiguration input)
    {
        var collapsed = NormalizePalette(input.CollapsedTokens);
        var expanded = input.LinkCollapsedAndExpanded
            ? collapsed
            : NormalizePalette(input.ExpandedTokens);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input.PerWidgetAccents is not null)
        {
            foreach (var (widgetId, hex) in input.PerWidgetAccents)
            {
                if (!string.IsNullOrWhiteSpace(widgetId) && _colorService.TryParseHex(hex, out var parsed) && parsed is not null)
                {
                    overrides[widgetId] = parsed.Hex;
                }
            }
        }

        var presetId = _presetManager.FindById(input.ActivePresetId)?.PresetId ?? AppearanceConfiguration.DefaultPresetId;

        return input with
        {
            SchemaVersion = 1,
            ActivePresetId = presetId,
            CollapsedTokens = collapsed,
            ExpandedTokens = expanded,
            GlowOpacity = double.IsFinite(input.GlowOpacity)
                ? Math.Clamp(input.GlowOpacity, 0.0, 1.0)
                : AppearanceConfiguration.DefaultGlowOpacity,
            BorderThicknessDip = double.IsFinite(input.BorderThicknessDip)
                ? Math.Clamp(input.BorderThicknessDip, 0.0, 4.0)
                : AppearanceConfiguration.DefaultBorderThicknessDip,
            PerWidgetAccents = overrides
        };
    }

    private IslandColorPalette NormalizePalette(IslandColorPalette? input)
    {
        var palette = input ?? IslandColorPalette.Midnight;
        return new IslandColorPalette
        {
            BackgroundHex = NormalizeHexOrFallback(palette.BackgroundHex, IslandColorPalette.MidnightBackground),
            AccentHex = NormalizeHexOrFallback(palette.AccentHex, IslandColorPalette.MidnightAccent),
            BorderHex = NormalizeHexOrFallback(palette.BorderHex, IslandColorPalette.MidnightBorder),
            GlowHex = NormalizeHexOrFallback(palette.GlowHex, IslandColorPalette.MidnightGlow),
            PrimaryTextHex = NormalizeHexOrFallback(palette.PrimaryTextHex, IslandColorPalette.MidnightPrimaryText),
            SecondaryIconHex = NormalizeHexOrFallback(palette.SecondaryIconHex, IslandColorPalette.MidnightSecondaryIcon)
        };
    }

    private string NormalizeHexOrFallback(string? input, string fallback) =>
        _colorService.TryParseHex(input, out var parsed) && parsed is not null ? parsed.Hex : fallback;

    private sealed class EditingSession : IAppearanceEditingSession
    {
        private readonly AppearanceManager _owner;
        private readonly PinToken? _pinToken;
        private readonly List<string> _modifiedRecentHexes = [];
        private readonly object _sessionSync = new();

        private AppearanceConfiguration _draft;
        private PreviewTargetState _previewState;
        private bool _completed;

        public EditingSession(
            AppearanceManager owner,
            AppearanceConfiguration snapshot,
            PinToken? pinToken,
            PreviewTargetState initialPreviewState)
        {
            _owner = owner;
            CommittedSnapshot = snapshot;
            _draft = snapshot;
            _pinToken = pinToken;
            _previewState = initialPreviewState;
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public AppearanceConfiguration CommittedSnapshot { get; }

        public AppearanceConfiguration DraftConfiguration
        {
            get
            {
                lock (_sessionSync)
                {
                    return _draft;
                }
            }
        }

        public bool IsLivePreviewPinned => _pinToken is not null && !_completed;

        public PreviewTargetState CurrentPreviewState
        {
            get
            {
                lock (_sessionSync)
                {
                    return _previewState;
                }
            }
        }

        public ContrastAuditReport CurrentContrastReport =>
            _owner._colorService.ValidateConfigurationContrast(DraftConfiguration);

        public bool IsCompleted
        {
            get
            {
                lock (_sessionSync)
                {
                    return _completed;
                }
            }
        }

        public void SetPreviewState(PreviewTargetState state)
        {
            EnsureMutable();
            AppearanceConfiguration snapshotForNotify;
            lock (_sessionSync)
            {
                _previewState = state;
                snapshotForNotify = _draft;
            }

            if (_pinToken is not null)
            {
                _owner._pinController?.UpdatePinTargetState(_pinToken, state);
            }

            _owner.UpdateLivePreview(this, snapshotForNotify);
        }

        public void UpdateToken(AppearanceTokenSlot slot, IslandStateTarget target, string hex)
        {
            EnsureMutable();
            var normalizedHex = _owner._colorService.FromHex(hex).Hex;

            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                var applyCollapsed = _draft.LinkCollapsedAndExpanded || target is IslandStateTarget.Collapsed or IslandStateTarget.Both;
                var applyExpanded = _draft.LinkCollapsedAndExpanded || target is IslandStateTarget.Expanded or IslandStateTarget.Both;

                var nextCollapsed = applyCollapsed
                    ? _draft.CollapsedTokens.WithSlotHex(slot, normalizedHex)
                    : _draft.CollapsedTokens;

                var nextExpanded = applyExpanded
                    ? _draft.ExpandedTokens.WithSlotHex(slot, normalizedHex)
                    : _draft.ExpandedTokens;

                _draft = _draft with
                {
                    CollapsedTokens = nextCollapsed,
                    ExpandedTokens = nextExpanded
                };
                _modifiedRecentHexes.Add(normalizedHex);
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewWidgetAccent(string widgetId, string? hex)
        {
            EnsureMutable();
            ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);

            string? normalizedHex = null;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                normalizedHex = _owner._colorService.FromHex(hex).Hex;
            }

            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                var dict = new Dictionary<string, string>(_draft.PerWidgetAccents, StringComparer.Ordinal);
                if (normalizedHex is null)
                {
                    dict.Remove(widgetId);
                }
                else
                {
                    dict[widgetId] = normalizedHex;
                    _modifiedRecentHexes.Add(normalizedHex);
                }

                _draft = _draft with { PerWidgetAccents = dict };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewPreset(string presetId)
        {
            EnsureMutable();
            var preset = _owner._presetManager.FindById(presetId)
                ?? throw new KeyNotFoundException($"Preset '{presetId}' was not found.");

            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                _draft = ApplyPresetToConfiguration(_draft, preset);
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewLinkCollapsedAndExpanded(bool linked)
        {
            EnsureMutable();
            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                _draft = _draft with
                {
                    LinkCollapsedAndExpanded = linked,
                    ExpandedTokens = linked ? _draft.CollapsedTokens : _draft.ExpandedTokens
                };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewUsePerWidgetAccents(bool enabled)
        {
            EnsureMutable();
            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                _draft = _draft with { UsePerWidgetAccents = enabled };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewGlowOpacity(double glowOpacity)
        {
            EnsureMutable();
            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                _draft = _draft with { GlowOpacity = Math.Clamp(glowOpacity, 0.0, 1.0) };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void PreviewBorderThickness(double borderThicknessDip)
        {
            EnsureMutable();
            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                _draft = _draft with { BorderThicknessDip = Math.Clamp(borderThicknessDip, 0.0, 4.0) };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public void ApplyRecommendedContrastFix()
        {
            EnsureMutable();
            AppearanceConfiguration next;
            lock (_sessionSync)
            {
                var fixedCollapsed = FixPaletteContrast(_draft.CollapsedTokens);
                var fixedExpanded = _draft.LinkCollapsedAndExpanded
                    ? fixedCollapsed
                    : FixPaletteContrast(_draft.ExpandedTokens);

                _draft = _draft with
                {
                    CollapsedTokens = fixedCollapsed,
                    ExpandedTokens = fixedExpanded
                };
                next = _draft;
            }

            _owner.UpdateLivePreview(this, next);
        }

        public string ResetSlotToDefault(AppearanceTokenSlot slot, IslandStateTarget target = IslandStateTarget.Both)
        {
            EnsureMutable();
            var defaultHex = IslandColorPalette.GetMidnightDefault(slot);
            UpdateToken(slot, target, defaultHex);
            return defaultHex;
        }

        public async Task<AppearanceConfiguration> CommitAsync(CancellationToken ct = default)
        {
            AppearanceConfiguration draftSnapshot;
            IReadOnlyList<string> recentsSnapshot;

            lock (_sessionSync)
            {
                if (_completed)
                {
                    throw new InvalidOperationException("This appearance editing session has already completed.");
                }

                _completed = true;
                draftSnapshot = _draft;
                recentsSnapshot = _modifiedRecentHexes.ToArray();
            }

            try
            {
                return await _owner.CommitSessionAsync(this, draftSnapshot, recentsSnapshot, _pinToken, ct).ConfigureAwait(false);
            }
            catch
            {
                _owner.CancelSession(this, CommittedSnapshot, _pinToken);
                throw;
            }
        }

        public void Cancel()
        {
            lock (_sessionSync)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            _owner.CancelSession(this, CommittedSnapshot, _pinToken);
        }

        public void Dispose() => Cancel();

        private IslandColorPalette FixPaletteContrast(IslandColorPalette palette)
        {
            var bg = palette.BackgroundHex;
            var fixedText = _owner._colorService.SolveRecommendedForeground(
                palette.PrimaryTextHex,
                bg,
                CoreColorProcessingEngine.PrimaryTextMinimumContrast);
            var fixedIcon = _owner._colorService.SolveRecommendedForeground(
                palette.SecondaryIconHex,
                bg,
                CoreColorProcessingEngine.SecondaryIconMinimumContrast);
            var fixedAccent = _owner._colorService.SolveRecommendedForeground(
                palette.AccentHex,
                bg,
                CoreColorProcessingEngine.AccentMinimumContrast);

            _modifiedRecentHexes.Add(fixedText);
            _modifiedRecentHexes.Add(fixedIcon);
            _modifiedRecentHexes.Add(fixedAccent);

            return palette with
            {
                PrimaryTextHex = fixedText,
                SecondaryIconHex = fixedIcon,
                AccentHex = fixedAccent
            };
        }

        private void EnsureMutable()
        {
            lock (_sessionSync)
            {
                if (_completed)
                {
                    throw new InvalidOperationException("Cannot mutate an appearance editing session after CommitAsync or Cancel.");
                }
            }
        }
    }
}
