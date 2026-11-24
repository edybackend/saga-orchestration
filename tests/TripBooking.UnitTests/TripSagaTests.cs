using TripBooking.Contracts;
using TripBooking.Saga.Domain;

namespace TripBooking.UnitTests;

/// <summary>The saga's decisions, with no I/O: given the outcome of each call, what happens next.</summary>
public class TripSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly SagaPolicy Policy = new(MaxAttempts: 3, MaxCompensationAttempts: 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    private static readonly CallOutcome Ok = new(CallResult.Succeeded, "200 OK");
    private static readonly CallOutcome SoldOut = new(CallResult.Rejected, "sold out");
    private static readonly CallOutcome Timeout = new(CallResult.TransientFailure, "timeout");

    private static TripSaga NewSaga() =>
        TripSaga.Start(new TripRequest("ana@example.com", "GRU", "REC", new DateOnly(2026, 12, 20), 5), Now);

    /// <summary>Applies outcomes in order and returns the calls the saga asked for, as "Reserve Flight", "Cancel Hotel"…</summary>
    private static List<string> Run(TripSaga saga, params CallOutcome[] outcomes)
    {
        var calls = new List<string>();
        foreach (var outcome in outcomes)
        {
            var action = saga.NextAction() ?? throw new InvalidOperationException("The saga has no next action.");
            calls.Add($"{action.Type} {action.Step.Kind}");
            saga.Record(action, outcome, Now, Policy);
        }

        return calls;
    }

    [Fact]
    public void A_new_saga_reserves_flight_hotel_and_car_with_stable_ids()
    {
        var saga = NewSaga();

        Assert.Equal(SagaStatus.Running, saga.Status);
        Assert.Equal(["GRU-REC@2026-12-20", "REC@2026-12-20", "REC@2026-12-20"], saga.Steps.Select(s => s.ResourceKey));
        Assert.Equal($"{saga.Id:N}-flight", saga.Steps[0].ReservationId);
    }

    [Fact]
    public void When_every_step_succeeds_the_trip_completes()
    {
        var saga = NewSaga();

        var calls = Run(saga, Ok, Ok, Ok);

        Assert.Equal(["Reserve Flight", "Reserve Hotel", "Reserve Car"], calls);
        Assert.Equal(SagaStatus.Completed, saga.Status);
        Assert.Null(saga.NextAction());
    }

    [Fact]
    public void A_rejected_hotel_cancels_only_the_flight()
    {
        var saga = NewSaga();

        var calls = Run(saga, Ok, SoldOut, Ok);

        Assert.Equal(["Reserve Flight", "Reserve Hotel", "Cancel Flight"], calls);
        Assert.Equal(SagaStatus.Compensated, saga.Status);
        Assert.Equal([StepStatus.Cancelled, StepStatus.Rejected, StepStatus.NotStarted], saga.Steps.Select(s => s.Status));
        Assert.Contains("Hotel rejected: sold out", saga.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_flight_leaves_nothing_to_compensate()
    {
        var saga = NewSaga();

        Run(saga, SoldOut);

        Assert.Equal(SagaStatus.Compensated, saga.Status);
        Assert.Null(saga.NextAction());
    }

    [Fact]
    public void A_technical_failure_is_retried_with_exponential_backoff()
    {
        var saga = NewSaga();

        Run(saga, Ok, Ok, Timeout);
        Assert.Equal(Now.AddSeconds(1), saga.NextAttemptAt);
        Run(saga, Timeout);
        Assert.Equal(Now.AddSeconds(2), saga.NextAttemptAt);

        Assert.Equal(SagaStatus.Running, saga.Status);
        Assert.Equal(2, saga.Attempts);
        Run(saga, Ok);
        Assert.Equal(SagaStatus.Completed, saga.Status);
    }

    [Fact]
    public void A_step_whose_outcome_stays_unknown_is_compensated_too_in_reverse_order()
    {
        var saga = NewSaga();

        var calls = Run(saga, Ok, Ok, Timeout, Timeout, Timeout, Ok, Ok, Ok);

        Assert.Equal(
            ["Reserve Flight", "Reserve Hotel", "Reserve Car", "Reserve Car", "Reserve Car", "Cancel Car", "Cancel Hotel", "Cancel Flight"],
            calls);
        Assert.Equal(SagaStatus.Compensated, saga.Status);
        Assert.All(saga.Steps, s => Assert.Equal(StepStatus.Cancelled, s.Status));
    }

    [Fact]
    public void A_cancellation_that_keeps_failing_needs_an_operator()
    {
        var saga = NewSaga();

        Run(saga, Ok, SoldOut, Timeout, Timeout);

        Assert.Equal(SagaStatus.NeedsAttention, saga.Status);
        Assert.Null(saga.NextAction());
        Assert.Equal(StepStatus.Reserved, saga.Steps[0].Status); // still reserved: that is why a human must look
    }

    [Fact]
    public void Recording_an_outcome_for_the_wrong_call_is_refused()
    {
        var saga = NewSaga();
        var wrong = new SagaAction(SagaActionType.Reserve, saga.Steps[2]);

        Assert.Throws<InvalidOperationException>(() => saga.Record(wrong, Ok, Now, Policy));
    }

    [Fact]
    public void Every_call_lands_in_the_timeline()
    {
        var saga = NewSaga();

        Run(saga, Ok, SoldOut, Ok);

        Assert.Equal(
            [(ReservationKind.Flight, CallResult.Succeeded), (ReservationKind.Hotel, CallResult.Rejected), (ReservationKind.Flight, CallResult.Succeeded)],
            saga.NewEntries.Select(e => (e.Step, e.Result)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(10, 30)]
    public void Backoff_doubles_up_to_the_cap(int failedAttempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), Policy.BackoffAfter(failedAttempts));
}
