namespace TripBooking.Contracts;

/// <summary>The three services a trip needs. Each one owns its database and is reached only over HTTP.</summary>
public enum ReservationKind
{
    Flight,
    Hotel,
    Car,
}

public static class ParticipantRoutes
{
    /// <summary>URL segment of a participant: <c>/api/flights</c>, <c>/api/hotels</c>, <c>/api/cars</c>.</summary>
    public static string Segment(ReservationKind kind) => kind switch
    {
        ReservationKind.Flight => "flights",
        ReservationKind.Hotel => "hotels",
        ReservationKind.Car => "cars",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown participant."),
    };
}

/// <summary>Body of <c>PUT /api/{kind}/reservations/{reservationId}</c>. The id is chosen by the caller, which makes retries safe.</summary>
public sealed record ReserveRequest(string ResourceKey, string CustomerEmail);

public sealed record ReservationResponse(
    string ReservationId,
    ReservationKind Kind,
    string? ResourceKey,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record AvailabilityResponse(ReservationKind Kind, string ResourceKey, int Capacity, int Reserved, int Remaining);

/// <summary>Body of <c>PUT /api/{kind}/chaos</c>: makes the next calls fail, before or after doing the work.</summary>
public sealed record ChaosSettings(int FailNextCalls, bool FailAfterProcessing, int DelayMs);
