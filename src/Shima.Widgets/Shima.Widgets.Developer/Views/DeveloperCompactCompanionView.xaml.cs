using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shima.Core.Contracts;

namespace Shima.Widgets.Developer.Views;

public partial class DeveloperCompactCompanionView : UserControl, IReducedMotionAware
{
    private const double SweepHighlightWidth = 44.0;

    private readonly DeveloperWidgetViewModel _viewModel;
    private bool _isReducedMotion;
    private bool _isWorkingAnimationActive;
    private double _lastSweepSpanWidth;

    public DeveloperCompactCompanionView(DeveloperWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.AttachUiDispatcher(DispatchToUi);
        DataContext = _viewModel;

        Loaded += (_, _) => UpdateSweepAnimation();
        Unloaded += (_, _) => StopSweepAnimation(immediate: true);
        IsVisibleChanged += (_, _) => UpdateSweepAnimation();
        CompanionTrackRoot.SizeChanged += (_, _) =>
        {
            if (_isWorkingAnimationActive && Math.Abs(CompanionTrackRoot.ActualWidth - _lastSweepSpanWidth) > 2.0)
            {
                RestartSweepAnimation();
            }
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public bool IsWorkingSweepActive => _isWorkingAnimationActive;

    public void SetReducedMotion(bool isReducedMotion)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetReducedMotion(isReducedMotion));
            return;
        }

        if (_isReducedMotion == isReducedMotion)
        {
            return;
        }

        _isReducedMotion = isReducedMotion;
        UpdateSweepAnimation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DeveloperWidgetViewModel.BarVisualState))
        {
            UpdateSweepAnimation();
        }
    }

    private void UpdateSweepAnimation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateSweepAnimation);
            return;
        }

        var shouldAnimateWorking = IsVisible
            && !_isReducedMotion
            && _viewModel.BarVisualState == DeveloperBarVisualState.Working;

        if (shouldAnimateWorking)
        {
            StartSweepAnimation();
        }
        else
        {
            StopSweepAnimation(immediate: _isReducedMotion || !IsVisible);
        }
    }

    private void RestartSweepAnimation()
    {
        StopSweepAnimation(immediate: true);
        UpdateSweepAnimation();
    }

    private void StartSweepAnimation()
    {
        if (_isWorkingAnimationActive)
        {
            return;
        }

        _isWorkingAnimationActive = true;
        var trackWidth = CompanionTrackRoot.ActualWidth > 16.0 ? CompanionTrackRoot.ActualWidth : 208.0;
        _lastSweepSpanWidth = trackWidth;

        var sweepAnim = new DoubleAnimation(-SweepHighlightWidth, trackWidth, TimeSpan.FromMilliseconds(1250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            RepeatBehavior = RepeatBehavior.Forever
        };
        CompanionSweepTransform.BeginAnimation(TranslateTransform.XProperty, sweepAnim);

        CompanionSweepHighlight.BeginAnimation(OpacityProperty, null);
        CompanionSweepHighlight.Opacity = 0.50;

        var pulseAnim = new DoubleAnimation(1.0, 0.72, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        CompanionProgressBar.BeginAnimation(OpacityProperty, pulseAnim);
    }

    private void StopSweepAnimation(bool immediate)
    {
        _isWorkingAnimationActive = false;

        CompanionSweepTransform.BeginAnimation(TranslateTransform.XProperty, null);
        CompanionSweepTransform.X = -SweepHighlightWidth;

        if (immediate)
        {
            CompanionSweepHighlight.BeginAnimation(OpacityProperty, null);
            CompanionSweepHighlight.Opacity = 0.0;

            CompanionProgressBar.BeginAnimation(OpacityProperty, null);
            CompanionProgressBar.Opacity = 1.0;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var settleFade = new DoubleAnimation(CompanionSweepHighlight.Opacity, 0.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = easing
        };
        settleFade.Completed += (_, _) =>
        {
            if (!_isWorkingAnimationActive)
            {
                CompanionSweepHighlight.BeginAnimation(OpacityProperty, null);
                CompanionSweepHighlight.Opacity = 0.0;
            }
        };
        CompanionSweepHighlight.BeginAnimation(OpacityProperty, settleFade);

        var settleBar = new DoubleAnimation(CompanionProgressBar.Opacity, 1.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = easing
        };
        settleBar.Completed += (_, _) =>
        {
            if (!_isWorkingAnimationActive)
            {
                CompanionProgressBar.BeginAnimation(OpacityProperty, null);
                CompanionProgressBar.Opacity = 1.0;
            }
        };
        CompanionProgressBar.BeginAnimation(OpacityProperty, settleBar);
    }

    private void DispatchToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }
}
