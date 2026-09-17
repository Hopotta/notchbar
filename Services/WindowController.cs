using System.Windows;
using System.Windows.Media.Animation;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class WindowController
{
    public const double WindowWidth = 400;
    public const double CompactHeight = 38;
    public const double ExpandedHeight = 224;
    public const double HiddenTriggerHeight = 2;

    private readonly Window _window;
    private bool _isSuppressed;

    public WindowController(Window window)
    {
        _window = window;
        _window.Width = WindowWidth;
        _window.Height = CompactHeight;
        _window.Left = GetCenteredLeft();
        _window.Top = GetHiddenTop();
    }

    public void SetSuppressed(bool suppressed)
    {
        _isSuppressed = suppressed;
    }

    public void Apply(NotchState state)
    {
        var targetHeight = state is NotchState.Expanded or NotchState.Pinned ? ExpandedHeight : CompactHeight;
        var targetTop = _isSuppressed
            ? -targetHeight
            : state == NotchState.Hidden
                ? -(targetHeight - HiddenTriggerHeight)
                : 0;
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        _window.Left = GetCenteredLeft();
        _window.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(targetHeight, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        _window.BeginAnimation(Window.TopProperty, new DoubleAnimation(targetTop, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static double GetCenteredLeft()
    {
        // WPF uses the primary display's logical coordinate space here. This keeps
        // the first version primary-monitor-only without hardcoded pixel values.
        return Math.Max(0, (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2);
    }

    private static double GetHiddenTop() => -(CompactHeight - HiddenTriggerHeight);
}
