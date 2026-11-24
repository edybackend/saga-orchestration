using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TripBooking.Contracts;

namespace TripBooking.Participants;

public static class ParticipantEndpoints
{
    /// <summary>Maps <c>/api/{flights|hotels|cars}</c> for one participant.</summary>
    public static IEndpointRouteBuilder MapParticipant(this IEndpointRouteBuilder app, Participant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        var segment = ParticipantRoutes.Segment(participant.Kind);
        var group = app.MapGroup($"/api/{segment}").WithTags($"Participant: {segment}");

        var operations = group.MapGroup("/reservations").AddEndpointFilter(async (context, next) =>
        {
            // Failure injection: a 503 before the work (nothing happened) or after it (the work is done but the caller cannot know).
            var chaos = participant.Chaos.Current;
            if (chaos.DelayMs > 0)
            {
                await Task.Delay(chaos.DelayMs, context.HttpContext.RequestAborted);
            }

            if (!participant.Chaos.TryConsumeFailure(out var afterProcessing))
            {
                return await next(context);
            }

            if (afterProcessing)
            {
                await next(context);
            }

            return Results.Problem($"Injected failure ({(afterProcessing ? "after" : "before")} processing).", statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        operations.MapPut("/{reservationId}", async (string reservationId, ReserveRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ResourceKey) || reservationId.Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["resourceKey"] = ["A resource key is required."] });
            }

            var outcome = await participant.Store.ReserveAsync(reservationId, request, ct);
            return outcome.Result switch
            {
                ReserveResult.Reserved => Results.Ok(outcome.Reservation),
                ReserveResult.SoldOut => Results.Problem($"No {segment} left for {request.ResourceKey}.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem($"Reservation {reservationId} was cancelled before it was made.", statusCode: StatusCodes.Status409Conflict),
            };
        })
            .WithSummary("Reserve one unit (idempotent: the caller chooses the id)");

        operations.MapPost("/{reservationId}/cancel", async (string reservationId, CancellationToken ct) =>
            Results.Ok(await participant.Store.CancelAsync(reservationId, ct)))
            .WithSummary("Cancel (idempotent; an unknown id leaves a tombstone)");

        group.MapGet("/reservations/{reservationId}", async (string reservationId) =>
            await participant.Store.GetAsync(reservationId) is { } reservation
                ? Results.Ok(reservation)
                : Results.Problem($"Reservation {reservationId} was not found.", statusCode: StatusCodes.Status404NotFound));

        group.MapGet("/availability/{resourceKey}", async (string resourceKey) =>
            Results.Ok(await participant.Store.GetAvailabilityAsync(resourceKey)))
            .WithSummary("Capacity, reserved and remaining units for a resource key");

        group.MapPut("/chaos", (ChaosSettings settings) =>
        {
            participant.Chaos.Set(settings);
            return Results.Ok(participant.Chaos.Current);
        })
            .WithSummary("Inject failures: failNextCalls, failAfterProcessing, delayMs");

        return app;
    }
}
