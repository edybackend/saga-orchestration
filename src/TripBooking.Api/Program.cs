using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TripBooking.Api;
using TripBooking.Api.Endpoints;
using TripBooking.Participants;
using TripBooking.Saga;

var builder = WebApplication.CreateBuilder(args);
var role = builder.Configuration.GetValue("Role", HostRole.All);
var participants = role.Participants();

builder.Services.AddSingleton(TimeProvider.System);
if (role.RunsOrchestrator())
{
    builder.Services.AddTripSaga(builder.Configuration);
}

builder.Services.AddParticipants(builder.Configuration, participants);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

await app.Services.InitializeParticipantsAsync(participants);
if (role.RunsOrchestrator())
{
    await app.Services.GetRequiredService<TripBooking.Saga.Persistence.SagaStore>().InitializeAsync();
    if (app.Configuration.GetValue("Samples:Seed", false))
    {
        await SampleData.SeedAsync(app.Services, TimeProvider.System, CancellationToken.None);
    }
}

// Malformed JSON raises BadHttpRequestException, which carries its own 400; without the selector it becomes a 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle($"Trip booking saga — {role}"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
app.MapHealthChecks("/health");
if (role.RunsOrchestrator())
{
    app.MapTripEndpoints();
}

foreach (var kind in participants)
{
    app.MapParticipant(app.Services.GetRequiredKeyedService<Participant>(kind));
}

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
