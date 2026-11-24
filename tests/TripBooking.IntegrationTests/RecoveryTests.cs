namespace TripBooking.IntegrationTests;

public sealed class RecoveryTests : IDisposable
{
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task A_trip_interrupted_by_a_restart_is_finished_by_the_next_process()
    {
        Guid tripId;
        using (var first = new SagaApiFactory(_databases))
        using (var client = first.CreateClient())
        {
            await client.ChaosAsync("cars", failNextCalls: 1_000); // the car service is down…
            tripId = await client.BookAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!(await client.TripAsync(tripId)).Timeline!.Any(e => e is { Step: "Car", Result: "TransientFailure" }) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal("Running", (await client.TripAsync(tripId)).Status);
        } // …and the process stops in the middle of the trip.

        using var second = new SagaApiFactory(_databases); // a new process on the same databases, car service healthy
        using var restarted = second.CreateClient();

        var trip = await restarted.FinishedTripAsync(tripId);

        Assert.Equal("Completed", trip.Status);
        Assert.Equal(1, trip.Calls().Count(c => c == "Reserve Flight Succeeded"));
        Assert.Equal(1, trip.Calls().Count(c => c == "Reserve Car Succeeded"));
    }

    public void Dispose() => _databases.Dispose();
}
