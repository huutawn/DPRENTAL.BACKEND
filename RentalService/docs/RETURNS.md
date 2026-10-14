# Record return per assigned asset (subtask 2)

## Endpoint

`POST /api/v1/rental/items/{itemId}/returns`

Requires a user JWT for the item's supplier and an `Idempotency-Key` header (1–64 characters).
The identity comes from JWT, never from the request body. Service tokens cannot record returns.

```json
{
  "actualReturnAt": "2026-10-14T14:00:00+07:00",
  "note": "Received one unit",
  "units": [
    {
      "rentalUnitAssignmentId": "11111111-1111-1111-1111-111111111111",
      "condition": "DAMAGED",
      "description": "Lens scratched",
      "mediaIds": ["22222222-2222-2222-2222-222222222222"]
    }
  ]
}
```

Replace example IDs with existing assignments and private, ready media uploaded by the supplier.
Conditions are `NORMAL` and `DAMAGED`. At most 10 distinct media IDs per unit.
One request is one actual return time; use separate requests for units returned at different times.
All units in a request are validated before any change is persisted.

The response contains `returnRecordId`, `submittedAt`, `remainingUnits`, `status`, and `deductionDeadline`.
Same key + same content replays the stored response (including after later state changes).
Same key + different content returns 409. A previously returned unit cannot be overwritten with a new key.

## Agreed lifecycle

- Partial returns keep the item `IN_RENTAL`.
- The final return sets `ReturnsCompletedAt` to server time, not supplier-entered `ActualReturnAt`.
- If any unit is damaged, the final return changes the item to `PENDING_SETTLEMENT` and starts 48 hours for the supplier proposal.
- All-normal returns set final deduction to zero, but do not mark the item completed.
- Rental fees are unchanged; early return does not refund unused days.
- Return record, assignment evidence, state history, operation receipt and outbox are committed atomically.
- Requests serialize using the existing PostgreSQL order advisory lock.

## Media configuration

Set `Services:Media:BaseUrl` to the Media service origin and store `MediaService:ApiKey` in user secrets/environment.
Environment equivalents: `Services__Media__BaseUrl`, `MediaService__ApiKey`.
Do not commit the API key. Verification uses the frozen `X-Api-Key` contract and the verify/metadata routes.
The HTTP client uses a 10-second timeout, disables redirects through DI, forwards cancellation and never exposes raw upstream errors.
No missing/unavailable Media configuration is treated as successful verification.

## Durable event and remaining workflows

`RentalUnitsReturned` v1 uses the existing `RentalEventEnvelope` on `Kafka:RentalEventsTopic` (default `rental.events.v1`).
Payload: `orderId`, `rentalItemId`, `returnRecordId`, `actualReturnAt`, `submittedAt`, `assignmentIds`.
The payload carries no serial numbers or internal asset codes. Consumers must deduplicate by `messageId`.
Outbox delivery confirms a recorded return, **not** successful Inventory release or Payment settlement.

Subtasks 1/3/4 add overdue protection and Inventory return processing; subtasks 5/6 add proposal/dispute/settlement.
The record is the durable input for those workflows. Inventory closure fields stay unset until Inventory confirms.
No migration is needed: existing return/evidence, receipt and outbox mappings are reused.

## Verification

Start the local PostgreSQL service (`docker compose up -d postgres`) and run:

```powershell
dotnet build RentalService.sln
dotnet test RentalService.sln
```

Return tests create isolated PostgreSQL databases and apply the actual migration chain.
Coverage includes partial/final return, immutable evidence, authorization, idempotency, concurrent requests,
Media failure/retry, transaction rollback, API routing and Swagger documentation.
