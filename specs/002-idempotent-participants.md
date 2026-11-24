# 002 · Idempotent participants

## Context
The orchestrator retries calls whose outcome it does not know (timeout, connection reset, `5xx`). A participant may have reserved and then failed to answer, so every operation must be safe to repeat.

## Behavior
- `PUT /api/{flights|hotels|cars}/reservations/{reservationId}` with `resourceKey` reserves one unit. The id is chosen by the caller (`{tripId}-{step}`), so a retry carries the same id.
- `POST /api/{kind}/reservations/{reservationId}/cancel` cancels and releases the unit.
- `GET /api/{kind}/availability/{resourceKey}` shows capacity, reserved units and remaining units.

## Acceptance criteria
- Reserving the same id twice returns the same reservation (`200`) and uses one unit.
- Sold out → `409` with a reason; the orchestrator treats it as a definitive rejection.
- Cancelling an unknown id succeeds and leaves a **tombstone**: if the original reserve arrives later (it was still in flight), it is refused (`409`), so a compensated trip can never keep a late reservation.
- Cancelling twice is a no-op.
- Concurrent reserves never exceed capacity.

## Out of scope
Holds that expire on their own, partial quantities.
