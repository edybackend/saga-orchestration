using TripBooking.Contracts;

namespace TripBooking.Saga.Domain;

public enum SagaStatus
{
    /// <summary>Reserving the steps in order.</summary>
    Running,

    /// <summary>A step failed; cancelling what was reserved, in reverse order.</summary>
    Compensating,

    Completed,

    /// <summary>Every reserved step was cancelled. The trip did not happen and nothing is left booked.</summary>
    Compensated,

    /// <summary>A cancellation kept failing. An operator has to look; nothing is retried any more.</summary>
    NeedsAttention,
}

public enum StepStatus
{
    NotStarted,
    Reserved,

    /// <summary>The participant said no (sold out). Nothing was reserved, so nothing to cancel.</summary>
    Rejected,

    /// <summary>The calls kept failing: the participant may or may not have reserved. It is cancelled to be sure.</summary>
    Unknown,

    Cancelled,
}

public sealed class SagaStep
{
    public required ReservationKind Kind { get; init; }

    public required string ReservationId { get; init; }

    public required string ResourceKey { get; init; }

    public StepStatus Status { get; set; }

    /// <summary>Whether this step has to be undone during compensation.</summary>
    public bool NeedsCancel => Status is StepStatus.Reserved or StepStatus.Unknown;
}

public enum SagaActionType
{
    Reserve,
    Cancel,
}

/// <summary>The next call the orchestrator must make.</summary>
public sealed record SagaAction(SagaActionType Type, SagaStep Step);

public enum CallResult
{
    /// <summary>The participant did it (reserved or cancelled).</summary>
    Succeeded,

    /// <summary>The participant refused for a business reason. Definitive: retrying will not help.</summary>
    Rejected,

    /// <summary>Timeout, connection refused or 5xx. The outcome is unknown; retrying with the same id is safe.</summary>
    TransientFailure,
}

public sealed record CallOutcome(CallResult Result, string Detail);

public sealed record TimelineEntry(DateTimeOffset At, ReservationKind Step, SagaActionType Action, CallResult Result, string Detail);

public sealed record SagaPolicy(int MaxAttempts, int MaxCompensationAttempts, TimeSpan BaseBackoff, TimeSpan MaxBackoff)
{
    public TimeSpan BackoffAfter(int failedAttempts)
    {
        var delay = BaseBackoff * Math.Pow(2, Math.Min(failedAttempts - 1, 20));
        return delay < MaxBackoff ? delay : MaxBackoff;
    }
}

/// <summary>
/// The trip saga as a state machine. It does no I/O: <see cref="NextAction"/> says which call to make and
/// <see cref="Record"/> applies its outcome. The orchestrator persists the state after every call, so a
/// crash never loses more than the call in flight, and that call is safe to repeat.
/// </summary>
public sealed class TripSaga
{
    public static readonly IReadOnlyList<ReservationKind> StepOrder = [ReservationKind.Flight, ReservationKind.Hotel, ReservationKind.Car];

    private readonly List<TimelineEntry> _newEntries = [];

    public required Guid Id { get; init; }

    public required TripRequest Request { get; init; }

    public required IReadOnlyList<SagaStep> Steps { get; init; }

    public SagaStatus Status { get; private set; }

    /// <summary>Consecutive technical failures of the current call.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Timeline entries not saved yet; the store appends them and then calls <see cref="MarkEntriesSaved"/>.</summary>
    public IReadOnlyList<TimelineEntry> NewEntries => _newEntries;

    public bool IsFinished => Status is SagaStatus.Completed or SagaStatus.Compensated or SagaStatus.NeedsAttention;

    public static TripSaga Start(TripRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.CreateVersion7(now);
        var date = request.DepartureDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new TripSaga
        {
            Id = id,
            Request = request,
            Steps =
            [
                NewStep(id, ReservationKind.Flight, $"{request.Origin}-{request.Destination}@{date}"),
                NewStep(id, ReservationKind.Hotel, $"{request.Destination}@{date}"),
                NewStep(id, ReservationKind.Car, $"{request.Destination}@{date}"),
            ],
            Status = SagaStatus.Running,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Rebuilds a saga loaded from the store.</summary>
    public static TripSaga Restore(
        Guid id, TripRequest request, IReadOnlyList<SagaStep> steps, SagaStatus status, int attempts,
        DateTimeOffset nextAttemptAt, string? failureReason, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new()
        {
            Id = id,
            Request = request,
            Steps = steps,
            Status = status,
            Attempts = attempts,
            NextAttemptAt = nextAttemptAt,
            FailureReason = failureReason,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };

    /// <summary>The call to make now, or null if the saga is finished.</summary>
    public SagaAction? NextAction() => Status switch
    {
        SagaStatus.Running => Steps.FirstOrDefault(s => s.Status == StepStatus.NotStarted) is { } next
            ? new SagaAction(SagaActionType.Reserve, next)
            : null,
        SagaStatus.Compensating => Steps.LastOrDefault(s => s.NeedsCancel) is { } toUndo
            ? new SagaAction(SagaActionType.Cancel, toUndo)
            : null,
        _ => null,
    };

    public void Record(SagaAction action, CallOutcome outcome, DateTimeOffset now, SagaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(policy);
        if (action != NextAction())
        {
            throw new InvalidOperationException($"{action.Type} {action.Step.Kind} is not the saga's next action.");
        }

        _newEntries.Add(new TimelineEntry(now, action.Step.Kind, action.Type, outcome.Result, outcome.Detail));
        UpdatedAt = now;

        if (outcome.Result == CallResult.TransientFailure)
        {
            RecordTransientFailure(action, outcome, now, policy);
            return;
        }

        Attempts = 0;
        NextAttemptAt = now;
        if (action.Type == SagaActionType.Reserve)
        {
            if (outcome.Result == CallResult.Succeeded)
            {
                action.Step.Status = StepStatus.Reserved;
            }
            else
            {
                action.Step.Status = StepStatus.Rejected;
                BeginCompensation($"{action.Step.Kind} rejected: {outcome.Detail}");
            }
        }
        else
        {
            action.Step.Status = StepStatus.Cancelled; // cancel is idempotent: a rejection can only mean "nothing to cancel"
        }

        Settle();
    }

    /// <summary>Called by the store after a commit, so the next save does not append the same entries again.</summary>
    internal void MarkEntriesSaved() => _newEntries.Clear();

    private void RecordTransientFailure(SagaAction action, CallOutcome outcome, DateTimeOffset now, SagaPolicy policy)
    {
        Attempts++;
        var limit = Status == SagaStatus.Running ? policy.MaxAttempts : policy.MaxCompensationAttempts;
        if (Attempts < limit)
        {
            NextAttemptAt = now + policy.BackoffAfter(Attempts);
            return;
        }

        Attempts = 0;
        NextAttemptAt = now;
        if (Status == SagaStatus.Running)
        {
            action.Step.Status = StepStatus.Unknown; // it may have been reserved: cancel it too
            BeginCompensation($"{action.Step.Kind} unavailable after {limit} attempts: {outcome.Detail}");
            Settle();
        }
        else
        {
            Status = SagaStatus.NeedsAttention;
            FailureReason = $"{FailureReason} Cancelling {action.Step.Kind} failed {limit} times: {outcome.Detail}";
        }
    }

    private void BeginCompensation(string reason)
    {
        Status = SagaStatus.Compensating;
        FailureReason = reason;
    }

    /// <summary>Moves to a final status once there is nothing left to do.</summary>
    private void Settle()
    {
        if (NextAction() is not null)
        {
            return;
        }

        Status = Status == SagaStatus.Running ? SagaStatus.Completed : SagaStatus.Compensated;
    }

    private static SagaStep NewStep(Guid sagaId, ReservationKind kind, string resourceKey) => new()
    {
        Kind = kind,
        ReservationId = $"{sagaId:N}-{kind.ToString().ToLowerInvariant()}",
        ResourceKey = resourceKey,
        Status = StepStatus.NotStarted,
    };
}

public sealed record TripRequest(string CustomerEmail, string Origin, string Destination, DateOnly DepartureDate, int Nights);
