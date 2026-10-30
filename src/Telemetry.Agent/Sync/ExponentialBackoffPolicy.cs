using Telemetry.Agent.Configuration;

namespace Telemetry.Agent.Sync;

public sealed class ExponentialBackoffPolicy
{
    private readonly int _baseDelayMs;
    private readonly int _maxDelayMs;
    private readonly bool _disableJitter;

    public ExponentialBackoffPolicy(AgentOptions options, bool disableJitter = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _baseDelayMs = Math.Max(0, options.RetryBaseDelayMs);
        _maxDelayMs = Math.Max(0, options.RetryMaxDelayMs);
        _baseDelayMs = Math.Min(Math.Max(0, options.RetryBaseDelayMs), _maxDelayMs);
        _disableJitter = disableJitter;
    }

    public TimeSpan GetDelay(int attempt)
    {
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be greater than zero.");
        }

        var exponent = Math.Min(attempt - 1, 30);
        var rawDelay = Math.Min(_maxDelayMs, _baseDelayMs * Math.Pow(2, exponent));
        var jitteredDelay = _disableJitter ? rawDelay : rawDelay * (0.85 + (Random.Shared.NextDouble() * 0.3));
        return TimeSpan.FromMilliseconds(Math.Clamp(jitteredDelay, 0, _maxDelayMs));
    }
}
