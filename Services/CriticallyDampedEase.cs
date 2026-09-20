using System.Windows;
using System.Windows.Media.Animation;

namespace NotchBar.Services;

/// <summary>
/// A monotonic, spring-like easing curve that settles without overshoot.
/// </summary>
internal sealed class CriticallyDampedEase : EasingFunctionBase
{
    public double Response { get; set; } = 0.34;

    protected override double EaseInCore(double normalizedTime)
    {
        var time = Math.Clamp(normalizedTime, 0d, 1d);
        var decay = 1.8d / Math.Clamp(Response, 0.2d, 0.6d);
        var terminal = 1d - ((1d + decay) * Math.Exp(-decay));
        var value = 1d - ((1d + (decay * time)) * Math.Exp(-decay * time));

        return terminal <= double.Epsilon
            ? time
            : Math.Clamp(value / terminal, 0d, 1d);
    }

    protected override Freezable CreateInstanceCore() => new CriticallyDampedEase
    {
        Response = Response
    };
}