using TripBooking.Contracts;

namespace TripBooking.Participants;

/// <summary>One reservation service (flights, hotels or cars): its store and its failure injection.</summary>
public sealed class Participant(ReservationStore store, ChaosState chaos)
{
    public ReservationKind Kind => store.Kind;

    public ReservationStore Store => store;

    public ChaosState Chaos => chaos;
}

public sealed class ParticipantOptions
{
    public int Capacity { get; set; } = 2;
}
