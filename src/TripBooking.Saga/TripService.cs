using System.Net.Mail;
using TripBooking.Contracts;
using TripBooking.Saga.Domain;
using TripBooking.Saga.Persistence;

namespace TripBooking.Saga;

public sealed record BookTrip(string? CustomerEmail, string? Origin, string? Destination, DateOnly? DepartureDate, int? Nights);

public sealed record TripStepView(ReservationKind Kind, string ReservationId, string ResourceKey, StepStatus Status);

public sealed record TripView(
    Guid Id,
    SagaStatus Status,
    TripRequest Request,
    IReadOnlyList<TripStepView> Steps,
    string? FailureReason,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TimelineEntry>? Timeline);

public sealed record BookTripResult(TripView? Trip, IReadOnlyDictionary<string, string[]>? Errors);

/// <summary>Starts trips and reads them. Starting only persists the saga; the worker does the calls.</summary>
public sealed class TripService(SagaStore store, TimeProvider clock)
{
    public async Task<BookTripResult> BookAsync(BookTrip command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = Validate(command, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        if (errors.Count > 0)
        {
            return new BookTripResult(null, errors);
        }

        var request = new TripRequest(
            command.CustomerEmail!.Trim(),
            command.Origin!.ToUpperInvariant(),
            command.Destination!.ToUpperInvariant(),
            command.DepartureDate!.Value,
            command.Nights!.Value);
        var saga = TripSaga.Start(request, clock.GetUtcNow());
        await store.SaveAsync(saga, cancellationToken);
        return new BookTripResult(ToView(saga, timeline: null), null);
    }

    public async Task<TripView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var saga = await store.GetAsync(id, cancellationToken);
        return saga is null ? null : ToView(saga, await store.GetTimelineAsync(id, cancellationToken));
    }

    public async Task<IReadOnlyList<TripView>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await store.ListAsync(100, cancellationToken)).Select(s => ToView(s, timeline: null))];

    private static Dictionary<string, string[]> Validate(BookTrip command, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        var email = command.CustomerEmail?.Trim() ?? string.Empty;
        if (email.Length is 0 or > 254 || !MailAddress.TryCreate(email, out _))
        {
            errors["customerEmail"] = ["A valid email address is required."];
        }

        if (!IsCityCode(command.Origin))
        {
            errors["origin"] = ["Use a 3-letter city code, such as GRU."];
        }

        if (!IsCityCode(command.Destination))
        {
            errors["destination"] = ["Use a 3-letter city code, such as REC."];
        }
        else if (string.Equals(command.Origin, command.Destination, StringComparison.OrdinalIgnoreCase))
        {
            errors["destination"] = ["Destination must differ from origin."];
        }

        if (command.DepartureDate is not { } date || date < today)
        {
            errors["departureDate"] = ["Departure must be today or later."];
        }

        if (command.Nights is not (>= 1 and <= 30))
        {
            errors["nights"] = ["Nights must be between 1 and 30."];
        }

        return errors;
    }

    private static bool IsCityCode(string? code) => code is { Length: 3 } && code.All(char.IsAsciiLetter);

    private static TripView ToView(TripSaga saga, IReadOnlyList<TimelineEntry>? timeline) => new(
        saga.Id,
        saga.Status,
        saga.Request,
        [.. saga.Steps.Select(s => new TripStepView(s.Kind, s.ReservationId, s.ResourceKey, s.Status))],
        saga.FailureReason,
        saga.Attempts,
        saga.NextAttemptAt,
        saga.CreatedAt,
        saga.UpdatedAt,
        timeline);
}
