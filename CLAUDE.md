# Engineering rules for AI agents

This file is loaded by Claude Code in every session. It defines how changes are made in this repository.
Hooks in `.claude/settings.json` enforce the parts that can be automated.

## Workflow
1. **Spec first.** A new behavior starts as a spec in `specs/NNN-name.md` (context, behavior, acceptance criteria, out of scope). Code follows the spec.
2. **Small steps.** One concern per commit, [Conventional Commits](https://www.conventionalcommits.org/) (`feat(saga): ...`, `fix(participants): ...`).
3. **Definition of done** = the CI gates pass locally: `dotnet build` (warnings are errors), `dotnet test`, `dotnet format --verify-no-changes`. The Stop hook blocks finishing while they are red.

## Boundaries (enforced by `tests/TripBooking.ArchitectureTests`)
- `TripBooking.Saga` (orchestrator) and `TripBooking.Participants` never reference each other. They share only `TripBooking.Contracts` and talk only over HTTP.
- `TripBooking.Saga/Domain` is a pure state machine: no I/O, no clock, no `async`. `NextAction()` decides, `Record()` applies an outcome. A hook blocks I/O there.
- `TripBooking.Api` picks a role (`All`, `Orchestrator`, `Flights`, `Hotels`, `Cars`) and maps the endpoints of that role.

## Saga rules
- **Persist after every call.** The worker saves the state and the timeline entry before making the next call. Never keep saga progress only in memory.
- **Every participant operation is idempotent.** The reservation id is chosen by the orchestrator (`{tripId}-{step}`), so a retry carries the same id. Cancel of an unknown id writes a tombstone.
- **Three outcomes only:** succeeded, rejected (business, definitive: compensate) and transient (unknown: retry with backoff). After the attempts run out, a reserve step counts as *unknown* and is compensated too.
- **Compensate in reverse order**, and only steps that may have been reserved. Compensation is retried; when it keeps failing the trip is `NeedsAttention`, never silently dropped.
- **The participant contract is shared by separately deployed services.** Never rename or remove a field or enum member in `ParticipantContracts.cs`; add an optional field. A hook blocks removals.

## C# conventions
- Classes `sealed` by default; file-scoped namespaces; `TimeProvider` instead of `DateTime.Now` (outside Domain, which receives `now`); `Guid.CreateVersion7()` for ids.
- SQL with Dapper; every write that checks and changes state runs in a `BEGIN IMMEDIATE` transaction.
- No MediatR/MassTransit/Polly: the saga's retry policy is part of the state machine and is tested there. Logging with `[LoggerMessage]`.

## Never
- Edit the SQLite files by hand; change data through the API. Blocked by hook.
- Read or write secrets (`.env`, keys). Blocked by permissions and hook.
- Disable an analyzer or a test to make the build pass.
- Add a dependency without a reason stated in the commit message.
