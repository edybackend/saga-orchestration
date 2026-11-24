using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripBooking.Saga.Domain;
using TripBooking.Saga.Participants;
using TripBooking.Saga.Persistence;

namespace TripBooking.Saga;

/// <summary>
/// Moves every due trip forward, one call at a time, saving the state after each call. Because the state lives in
/// the database, a restart simply continues where the previous process stopped. One worker is assumed (see README).
/// </summary>
public sealed partial class SagaWorker(
    SagaStore store,
    IParticipantClient participants,
    TimeProvider clock,
    IOptions<SagaOptions> options,
    ILogger<SagaWorker> logger) : BackgroundService
{
    private readonly SagaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // e.g. the database is busy for a moment: log and try again on the next tick
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPassFailed(ex);
            }

            await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>One pass over the due trips. Public so tests can drive the saga without waiting for the timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var saga in await store.GetDueAsync(clock.GetUtcNow(), 50, cancellationToken))
        {
            await AdvanceAsync(saga, cancellationToken);
        }
    }

    private async Task AdvanceAsync(TripSaga saga, CancellationToken cancellationToken)
    {
        // Keep going while the saga has an action that is due now; stop at a backoff or a final status.
        while (saga.NextAction() is { } action && saga.NextAttemptAt <= clock.GetUtcNow())
        {
            var outcome = action.Type == SagaActionType.Reserve
                ? await participants.ReserveAsync(action.Step, saga.Request.CustomerEmail, cancellationToken)
                : await participants.CancelAsync(action.Step, cancellationToken);
            var before = saga.Status;
            saga.Record(action, outcome, clock.GetUtcNow(), _options.Policy);
            await store.SaveAsync(saga, CancellationToken.None); // never lose an outcome that already happened

            LogStep(saga.Id, action.Type, action.Step.Kind, outcome.Result, outcome.Detail);
            if (saga.Status != before)
            {
                LogStatus(saga.Id, before, saga.Status, saga.FailureReason);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trip {TripId}: {Action} {Step} → {Result} ({Detail})")]
    private partial void LogStep(Guid tripId, SagaActionType action, TripBooking.Contracts.ReservationKind step, CallResult result, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trip {TripId}: {From} → {To}. {Reason}")]
    private partial void LogStatus(Guid tripId, SagaStatus from, SagaStatus to, string? reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saga worker pass failed")]
    private partial void LogPassFailed(Exception ex);
}
