using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace TripBooking.IntegrationTests;

/// <summary>The saga end to end: orchestrator → HTTP → participants, with injected failures.</summary>
public sealed class TripSagaTests : IDisposable
{
    private readonly TestDatabases _databases = new();
    private readonly SagaApiFactory _factory;
    private readonly HttpClient _client;

    public TripSagaTests()
    {
        _factory = new SagaApiFactory(_databases);
        _client = _factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Key(string destination, int inDays = 30) => $"{destination}@{Api.InDays(inDays):yyyy-MM-dd}";

    [Fact]
    public async Task A_trip_with_room_everywhere_completes()
    {
        var trip = await _client.FinishedTripAsync(await _client.BookAsync());

        Assert.Equal("Completed", trip.Status);
        Assert.Equal(["Reserve Flight Succeeded", "Reserve Hotel Succeeded", "Reserve Car Succeeded"], trip.Calls());
        Assert.Equal(1, (await _client.AvailabilityAsync("cars", Key("REC"))).Reserved);
    }

    [Fact]
    public async Task When_the_hotel_is_full_the_flight_is_cancelled_and_the_seat_released()
    {
        await _client.FinishedTripAsync(await _client.BookAsync(email: "a@example.com"));
        await _client.FinishedTripAsync(await _client.BookAsync(email: "b@example.com"));

        var third = await _client.FinishedTripAsync(await _client.BookAsync(email: "c@example.com"));

        Assert.Equal("Compensated", third.Status);
        Assert.Equal(["Reserve Flight Succeeded", "Reserve Hotel Rejected", "Cancel Flight Succeeded"], third.Calls());
        Assert.Contains("No hotels left", third.FailureReason, StringComparison.Ordinal);
        Assert.Equal(2, (await _client.AvailabilityAsync("flights", $"GRU-{Key("REC")}")).Reserved);
    }

    [Fact]
    public async Task Two_failures_of_the_car_service_are_retried_and_the_trip_completes()
    {
        await _client.ChaosAsync("cars", failNextCalls: 2);

        var trip = await _client.FinishedTripAsync(await _client.BookAsync());

        Assert.Equal("Completed", trip.Status);
        Assert.Equal(2, trip.Calls().Count(c => c == "Reserve Car TransientFailure"));
        Assert.Equal(1, (await _client.AvailabilityAsync("cars", Key("REC"))).Reserved);
    }

    [Fact]
    public async Task A_car_reserved_but_unanswered_is_not_reserved_twice()
    {
        await _client.ChaosAsync("cars", failNextCalls: 1, afterProcessing: true);

        var trip = await _client.FinishedTripAsync(await _client.BookAsync());

        Assert.Equal("Completed", trip.Status);
        Assert.Contains("Reserve Car TransientFailure", trip.Calls());
        Assert.Equal(1, (await _client.AvailabilityAsync("cars", Key("REC"))).Reserved); // the retry found the same reservation
    }

    [Fact]
    public async Task When_the_car_service_stays_down_everything_is_cancelled_in_reverse_order()
    {
        await _client.ChaosAsync("cars", failNextCalls: 4);

        var trip = await _client.FinishedTripAsync(await _client.BookAsync());

        Assert.Equal("Compensated", trip.Status);
        Assert.Equal(
            ["Cancel Car Succeeded", "Cancel Hotel Succeeded", "Cancel Flight Succeeded"],
            trip.Calls().Where(c => c.StartsWith("Cancel", StringComparison.Ordinal)));
        Assert.Contains("Car unavailable after 4 attempts", trip.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, (await _client.AvailabilityAsync("flights", $"GRU-{Key("REC")}")).Reserved);
        Assert.Equal(0, (await _client.AvailabilityAsync("hotels", Key("REC"))).Reserved);
        Assert.Equal(0, (await _client.AvailabilityAsync("cars", Key("REC"))).Reserved);
    }

    [Fact]
    public async Task A_failing_compensation_is_retried_until_it_succeeds()
    {
        await _client.ChaosAsync("cars", failNextCalls: 4 + 3); // 4 reserves, then 3 cancels fail

        var trip = await _client.FinishedTripAsync(await _client.BookAsync());

        Assert.Equal("Compensated", trip.Status);
        Assert.Equal(3, trip.Calls().Count(c => c == "Cancel Car TransientFailure"));
        Assert.All(trip.Steps, s => Assert.Equal("Cancelled", s.Status));
    }

    [Theory]
    [InlineData("""{"customerEmail":"x","origin":"GRU","destination":"REC","departureDate":"2099-01-01","nights":3}""", "customerEmail")]
    [InlineData("""{"customerEmail":"a@b.com","origin":"GR","destination":"REC","departureDate":"2099-01-01","nights":3}""", "origin")]
    [InlineData("""{"customerEmail":"a@b.com","origin":"GRU","destination":"GRU","departureDate":"2099-01-01","nights":3}""", "destination")]
    [InlineData("""{"customerEmail":"a@b.com","origin":"GRU","destination":"REC","departureDate":"2020-01-01","nights":3}""", "departureDate")]
    [InlineData("""{"customerEmail":"a@b.com","origin":"GRU","destination":"REC","departureDate":"2099-01-01","nights":0}""", "nights")]
    public async Task Invalid_input_is_a_400_with_the_field(string body, string field)
    {
        var response = await _client.PostAsync("/api/trips", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_is_400_and_an_unknown_trip_is_404()
    {
        var malformed = await _client.PostAsync("/api/trips", new StringContent("{bad", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/trips/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _databases.Dispose();
    }
}
