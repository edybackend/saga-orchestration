using TripBooking.Saga.Domain;

namespace TripBooking.Saga;

public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    /// <summary>Technical failures tolerated on a reserve step before giving up and compensating.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Technical failures tolerated on a cancel before the trip needs an operator.</summary>
    public int MaxCompensationAttempts { get; set; } = 20;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Base URL of each participant, e.g. <c>http://flights:8080</c> in Docker Compose.</summary>
    public Dictionary<string, Uri> Participants { get; set; } = [];

    public SagaPolicy Policy => new(MaxAttempts, MaxCompensationAttempts, BaseBackoff, MaxBackoff);
}
