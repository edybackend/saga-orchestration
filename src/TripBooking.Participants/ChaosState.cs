using TripBooking.Contracts;

namespace TripBooking.Participants;

/// <summary>Failure injection for one participant, to watch the saga retry and compensate.</summary>
public sealed class ChaosState
{
    private readonly Lock _lock = new();
    private ChaosSettings _settings = new(0, false, 0);

    public ChaosSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    public void Set(ChaosSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            _settings = settings with
            {
                FailNextCalls = Math.Max(0, settings.FailNextCalls),
                DelayMs = Math.Clamp(settings.DelayMs, 0, 30_000),
            };
        }
    }

    /// <summary>Returns true if this call must fail, and consumes one of the planned failures.</summary>
    public bool TryConsumeFailure(out bool afterProcessing)
    {
        lock (_lock)
        {
            afterProcessing = _settings.FailAfterProcessing;
            if (_settings.FailNextCalls == 0)
            {
                return false;
            }

            _settings = _settings with { FailNextCalls = _settings.FailNextCalls - 1 };
            return true;
        }
    }
}
