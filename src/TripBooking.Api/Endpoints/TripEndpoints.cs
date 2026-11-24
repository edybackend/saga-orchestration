using TripBooking.Saga;

namespace TripBooking.Api.Endpoints;

internal static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder app)
    {
        var trips = app.MapGroup("/api/trips").WithTags("Trips (orchestrator)");

        trips.MapPost("/", async (BookTrip request, TripService service, CancellationToken ct) =>
        {
            var result = await service.BookAsync(request, ct);
            return result.Errors is { } errors
                ? Results.ValidationProblem(errors)
                : Results.Accepted($"/api/trips/{result.Trip!.Id}", result.Trip);
        })
            .WithSummary("Book a trip: flight → hotel → car. Returns 202; the saga runs in the background");

        trips.MapGet("/{id:guid}", async (Guid id, TripService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } trip
                ? Results.Ok(trip)
                : Results.Problem($"Trip {id} was not found.", statusCode: StatusCodes.Status404NotFound))
            .WithSummary("Status, steps and the timeline of every call");

        trips.MapGet("/", async (TripService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .WithSummary("Latest trips");

        return app;
    }
}
