namespace NotchBar.Services;

/// <summary>
/// A small critically-damped spring that keeps its current velocity when the
/// target changes. This makes reversals continuous instead of restarting an
/// easing curve from the logical state.
/// </summary>
public sealed class SpringMotion
{
    private const double DefaultSettledDistance = 0.001;
    private const double DefaultSettledVelocity = 0.001;

    private readonly double _settledDistance;
    private readonly double _settledVelocity;

    public SpringMotion(
        double initialValue = 0,
        double response = 0.34,
        double settledDistance = DefaultSettledDistance,
        double settledVelocity = DefaultSettledVelocity)
    {
        Value = initialValue;
        Target = initialValue;
        Response = response;
        _settledDistance = Math.Max(0, settledDistance);
        _settledVelocity = Math.Max(0, settledVelocity);
    }

    public double Value { get; private set; }

    public double Target { get; private set; }

    public double Velocity { get; private set; }

    /// <summary>
    /// Approximate time in seconds for the spring to settle.
    /// </summary>
    public double Response { get; set; }

    public bool IsSettled => Math.Abs(Target - Value) <= _settledDistance
        && Math.Abs(Velocity) <= _settledVelocity;

    public void SetImmediate(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    public void SetTarget(double target) => Target = target;

    /// <summary>
    /// Advances the exact critically-damped solution for the elapsed frame.
    /// The closed form avoids frame-rate-dependent overshoot and preserves the
    /// current velocity when the target is retargeted mid-flight.
    /// </summary>
    public bool Step(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }

        var seconds = Math.Min(elapsed.TotalSeconds, 0.05);
        var response = Math.Clamp(Response, 0.18, 0.8);
        var angularFrequency = 4.6 / response;
        var error = Value - Target;
        var combined = Velocity + (angularFrequency * error);
        var decay = Math.Exp(-angularFrequency * seconds);

        Value = Target + ((error + (combined * seconds)) * decay);
        Velocity = (Velocity - (angularFrequency * combined * seconds)) * decay;

        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
        }

        return true;
    }
}
