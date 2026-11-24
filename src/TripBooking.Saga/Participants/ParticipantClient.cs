using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using TripBooking.Contracts;
using TripBooking.Saga.Domain;

namespace TripBooking.Saga.Participants;

/// <summary>How the orchestrator calls a participant. The only coupling between them is this HTTP contract.</summary>
public interface IParticipantClient
{
    Task<CallOutcome> ReserveAsync(SagaStep sagaStep, string customerEmail, CancellationToken cancellationToken);

    Task<CallOutcome> CancelAsync(SagaStep sagaStep, CancellationToken cancellationToken);
}

/// <summary>
/// Turns HTTP into the three outcomes the saga understands: 2xx = succeeded, 409 = rejected (definitive),
/// anything else — 5xx, timeout, connection refused — = transient (unknown outcome, retry with the same id).
/// </summary>
public sealed class HttpParticipantClient(IHttpClientFactory httpClientFactory) : IParticipantClient
{
    public static string ClientName(ReservationKind kind) => $"participant-{ParticipantRoutes.Segment(kind)}";

    public Task<CallOutcome> ReserveAsync(SagaStep sagaStep, string customerEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sagaStep);
        return CallAsync(sagaStep.Kind, client => client.PutAsJsonAsync(
            $"api/{ParticipantRoutes.Segment(sagaStep.Kind)}/reservations/{sagaStep.ReservationId}",
            new ReserveRequest(sagaStep.ResourceKey, customerEmail),
            cancellationToken), cancellationToken);
    }

    public Task<CallOutcome> CancelAsync(SagaStep sagaStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sagaStep);
        return CallAsync(sagaStep.Kind, client => client.PostAsync(
            $"api/{ParticipantRoutes.Segment(sagaStep.Kind)}/reservations/{sagaStep.ReservationId}/cancel",
            content: null,
            cancellationToken), cancellationToken);
    }

    private async Task<CallOutcome> CallAsync(ReservationKind kind, Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName(kind));
        try
        {
            using var response = await send(client);
            if (response.IsSuccessStatusCode)
            {
                return new CallOutcome(CallResult.Succeeded, $"{(int)response.StatusCode} {response.StatusCode}");
            }

            var problem = await ReadProblemAsync(response);
            return response.StatusCode == HttpStatusCode.Conflict
                ? new CallOutcome(CallResult.Rejected, problem)
                : new CallOutcome(CallResult.TransientFailure, $"{(int)response.StatusCode}: {problem}");
        }
        catch (HttpRequestException ex)
        {
            return new CallOutcome(CallResult.TransientFailure, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (not a shutdown): the participant may still have done the work.
            return new CallOutcome(CallResult.TransientFailure, $"No answer within {client.Timeout.TotalSeconds:0.#} s.");
        }
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            return problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? "no details";
        }
        catch (System.Text.Json.JsonException)
        {
            return response.ReasonPhrase ?? "no details";
        }
    }
}
