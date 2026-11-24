namespace TripBooking.IntegrationTests;

public sealed class RecoveryTests : IDisposable
{
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task A_trip_interrupted_by_a_restart_is_finished_by_the_next_process()
    {
        Guid tripId;
        // Plenty of attempts, so the first process is still retrying (not compensating) when it stops.
        using (var first = new SagaApiFactory(_databases, maxAttempts: 10_000))
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

        // A new process on the same databases, car service healthy. Its first calls can come before its own test
        // server is up (they fail as transient), so it also gets room to retry instead of compensating.
        using var second = new SagaApiFactory(_databases, maxAttempts: 10_000);
        using var restarted = second.CreateClient();

        var trip = await restarted.FinishedTripAsync(tripId);

        Assert.Equal("Completed", trip.Status);
        Assert.Equal(1, trip.Calls().Count(c => c == "Reserve Flight Succeeded"));
        Assert.Equal(1, trip.Calls().Count(c => c == "Reserve Car Succeeded"));
    }

    public void Dispose() => _databases.Dispose();
}
