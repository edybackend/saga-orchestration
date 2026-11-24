# 003 · Failures, retries and recovery

## Context
Networks fail and processes crash. The saga state is persisted after every step, so a crash only pauses a trip.

## Behavior
- A call that fails technically (timeout, `5xx`, connection refused) is retried with exponential backoff using the same reservation id.
- After `Saga:MaxAttempts` technical failures on a reserve step, the outcome is unknown: the step is **included** in the compensation (cancel is idempotent and leaves a tombstone).
- Compensation is retried until it succeeds; after `Saga:MaxCompensationAttempts` the trip becomes `NeedsAttention` (an operator must look).
- A background worker picks up every trip that is `Running` or `Compensating` and due, also after a restart.
- `PUT /api/{kind}/chaos` with `failNextCalls`, `failAfterProcessing` and `delayMs` injects failures into one participant.

## Acceptance criteria
- Car fails twice with `503` → the trip still completes, with one car reservation.
- Car reserves but answers `503` (`failAfterProcessing`) → the retry returns the same reservation; the trip completes with one car reservation.
- Car down beyond the attempts → car, hotel and flight are cancelled; the trip is `Compensated`.
- The process stops in the middle of a trip; a new process on the same databases finishes it.

## Out of scope
Several orchestrator instances at once (one worker is assumed), human approval steps.
