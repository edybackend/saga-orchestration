using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TripBooking.IntegrationTests;

/// <summary>The four SQLite files of one "deployment". Two factories can share them to simulate a restart.</summary>
public sealed class TestDatabases : IDisposable
{
    private readonly string _prefix = Path.Combine(Path.GetTempPath(), $"saga-tests-{Guid.NewGuid():N}");

    public string For(string name) => $"Data Source={_prefix}-{name}.db;Pooling=False";

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_prefix) + "-*"))
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// The whole system in one test server (role All). The orchestrator's HTTP calls to the participants go through
/// the test server's handler, so they are real HTTP requests through the real endpoints — just without a socket.
/// </summary>
public sealed class SagaApiFactory(TestDatabases databases) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var name in new[] { "Trips", "Flights", "Hotels", "Cars" })
        {
            builder.UseSetting($"ConnectionStrings:{name}", databases.For(name));
            builder.UseSetting($"Saga:Participants:{name}", "http://localhost");
        }

        builder.UseSetting("Samples:Seed", "false");
        builder.UseSetting("Saga:MaxAttempts", "4");
        builder.UseSetting("Saga:MaxCompensationAttempts", "50");
        builder.UseSetting("Saga:BaseBackoff", "00:00:00.020");
        builder.UseSetting("Saga:MaxBackoff", "00:00:00.100");
        builder.UseSetting("Saga:PollingInterval", "00:00:00.020");
        builder.ConfigureTestServices(services => services.ConfigureHttpClientDefaults(client =>
            client.ConfigurePrimaryHttpMessageHandler(sp => new TestServerHandler(sp))));
    }

    /// <summary>
    /// Resolves the test server on each request instead of when the handler is built: after a restart the worker
    /// calls a participant before the server has started, and IHttpClientFactory would cache that failure forever.
    /// Until the server is up, requests fail like a refused connection, which the saga retries.
    /// </summary>
    private sealed class TestServerHandler(IServiceProvider services) : HttpMessageHandler
    {
        private HttpMessageInvoker? _invoker;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                _invoker ??= new HttpMessageInvoker(((TestServer)services.GetRequiredService<IServer>()).CreateHandler());
            }
            catch (InvalidOperationException ex)
            {
                throw new HttpRequestException("Test server not started yet.", ex);
            }

            return _invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public sealed record TripStepDto(string Kind, string ReservationId, string ResourceKey, string Status);

public sealed record TimelineDto(string Step, string Action, string Result, string Detail);

public sealed record TripDto(Guid Id, string Status, IReadOnlyList<TripStepDto> Steps, string? FailureReason, IReadOnlyList<TimelineDto>? Timeline);

public sealed record AvailabilityDto(int Capacity, int Reserved, int Remaining);

public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days);

    public static async Task<Guid> BookAsync(this HttpClient client, string destination = "REC", int inDays = 30, string email = "ana@example.com")
    {
        var response = await client.PostAsJsonAsync(
            "/api/trips",
            new { customerEmail = email, origin = "GRU", destination, departureDate = InDays(inDays), nights = 3 },
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TripDto>(Json, TestContext.Current.CancellationToken))!.Id;
    }

    public static async Task<TripDto> TripAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TripDto>($"/api/trips/{id}", Json, TestContext.Current.CancellationToken))!;

    /// <summary>Waits until the trip reaches a final status (the saga runs in the background).</summary>
    public static async Task<TripDto> FinishedTripAsync(this HttpClient client, Guid id, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var trip = await client.TripAsync(id);
            if (trip.Status is "Completed" or "Compensated" or "NeedsAttention" || DateTime.UtcNow > deadline)
            {
                return trip;
            }

            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
    }

    public static async Task<AvailabilityDto> AvailabilityAsync(this HttpClient client, string participant, string resourceKey) =>
        (await client.GetFromJsonAsync<AvailabilityDto>($"/api/{participant}/availability/{resourceKey}", Json, TestContext.Current.CancellationToken))!;

    public static async Task ChaosAsync(this HttpClient client, string participant, int failNextCalls, bool afterProcessing = false)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/{participant}/chaos", new { failNextCalls, failAfterProcessing = afterProcessing, delayMs = 0 }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static IEnumerable<string> Calls(this TripDto trip) => trip.Timeline!.Select(e => $"{e.Action} {e.Step} {e.Result}");
}
