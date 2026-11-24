using TripBooking.Saga;

namespace TripBooking.Api;

/// <summary>
/// Three trips to the same city and date, so the first run already shows both endings: hotels have 2 rooms per
/// city and date, so the third trip is compensated and its flight seat is released.
/// </summary>
internal static class SampleData
{
    public static async Task SeedAsync(IServiceProvider services, TimeProvider clock, CancellationToken cancellationToken)
    {
        var trips = services.GetRequiredService<TripService>();
        if ((await trips.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        var date = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(30);
        foreach (var email in new[] { "ana@example.com", "bruno@example.com", "carla@example.com" })
        {
            await trips.BookAsync(new BookTrip(email, "GRU", "REC", date, 5), cancellationToken);
        }
    }
}
