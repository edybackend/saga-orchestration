using System.Net;
using System.Net.Http.Json;

namespace TripBooking.IntegrationTests;

/// <summary>A participant on its own: every operation is safe to repeat, and capacity is never exceeded.</summary>
public sealed class ParticipantTests : IDisposable
{
    private readonly TestDatabases _databases = new();
    private readonly SagaApiFactory _factory;
    private readonly HttpClient _client;

    public ParticipantTests()
    {
        _factory = new SagaApiFactory(_databases);
        _client = _factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> ReserveAsync(string id, string key = "REC@2026-12-20") =>
        _client.PutAsJsonAsync($"/api/hotels/reservations/{id}", new { resourceKey = key, customerEmail = "ana@example.com" }, Ct);

    [Fact]
    public async Task Reserving_the_same_id_twice_uses_one_unit()
    {
        Assert.Equal(HttpStatusCode.OK, (await ReserveAsync("trip-1-hotel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReserveAsync("trip-1-hotel")).StatusCode);

        Assert.Equal(1, (await _client.AvailabilityAsync("hotels", "REC@2026-12-20")).Reserved);
    }

    [Fact]
    public async Task When_sold_out_the_answer_is_409()
    {
        await ReserveAsync("a");
        await ReserveAsync("b");

        var third = await ReserveAsync("c");

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Contains("No hotels left", await third.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_releases_the_unit_and_cancelling_again_is_a_no_op()
    {
        await ReserveAsync("a");

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/hotels/reservations/a/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/hotels/reservations/a/cancel", null, Ct)).StatusCode);

        Assert.Equal(0, (await _client.AvailabilityAsync("hotels", "REC@2026-12-20")).Reserved);
    }

    [Fact]
    public async Task A_reserve_that_arrives_after_its_cancel_is_refused()
    {
        // The saga gave up and cancelled while the reserve was still in flight.
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/hotels/reservations/late/cancel", null, Ct)).StatusCode);

        var late = await ReserveAsync("late");

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(0, (await _client.AvailabilityAsync("hotels", "REC@2026-12-20")).Reserved);
    }

    [Fact]
    public async Task Concurrent_reserves_never_exceed_capacity()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => ReserveAsync($"concurrent-{i}")));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await _client.AvailabilityAsync("hotels", "REC@2026-12-20")).Reserved);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _databases.Dispose();
    }
}
