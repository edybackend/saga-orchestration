using TripBooking.Contracts;

namespace TripBooking.Api;

/// <summary>
/// What this process runs. <c>All</c> (default) hosts the orchestrator and the three participants, for
/// <c>dotnet run</c> and the tests. In Docker Compose each container runs one role, so the calls cross the network.
/// </summary>
internal enum HostRole
{
    All,
    Orchestrator,
    Flights,
    Hotels,
    Cars,
}

internal static class HostRoleExtensions
{
    public static bool RunsOrchestrator(this HostRole role) => role is HostRole.All or HostRole.Orchestrator;

    public static IReadOnlyList<ReservationKind> Participants(this HostRole role) => role switch
    {
        HostRole.All => [ReservationKind.Flight, ReservationKind.Hotel, ReservationKind.Car],
        HostRole.Flights => [ReservationKind.Flight],
        HostRole.Hotels => [ReservationKind.Hotel],
        HostRole.Cars => [ReservationKind.Car],
        _ => [],
    };
}
