# 001 · Book a trip (orchestrated saga)

## Context
A trip needs a flight seat, a hotel room and a rental car from three independent services. Each owns its database, so there is no transaction across them. The saga books them one by one and, if a later step cannot be booked, undoes the earlier ones with compensating actions.

## Behavior
- `POST /api/trips` with `customerEmail`, `origin`, `destination`, `departureDate` and `nights` returns `202 Accepted` and the trip id. The saga runs in the background.
- Steps, in order: reserve flight (`origin-destination@date`), reserve hotel (`destination@date`), reserve car (`destination@date`).
- If every step succeeds the trip is `Completed`.
- If a step is **rejected** (sold out), the trip becomes `Compensating`: the steps already reserved are cancelled in reverse order, then the trip is `Compensated` with the reason.
- `GET /api/trips/{id}` returns the status, the reservation of each step and a timeline of every call and its outcome.

## Acceptance criteria
- Capacity: 5 seats per route and date, 2 hotel rooms and 2 cars per city and date. A third trip to the same city and date ends `Compensated`, and its flight seat is released.
- Compensation runs in reverse order and never cancels a step that was never attempted.
- Invalid input (`400`): email, 3-letter city codes, origin = destination, departure in the past, nights 1–30.
- Unknown trip → `404`.

## Out of scope
Payment, choosing specific flights or hotels, authentication.
