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
- When the final unit is returned normally, the item moves to `PENDING_SETTLEMENT`;
  financial completion still requires Payment confirmation.
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

Each return record now schedules durable Inventory operations. Normal units are closed
separately. Damaged units must receive a successful `BlockDamagedAssets` receipt
before their `MarkAssetsReturned` receipt can execute. A temporary Inventory failure
keeps the operation pending for recovery; it does not undo the recorded return.
The response is checked against every assigned allocation and asset before closure
fields are saved. Partial returns do not close other assignments.

Inventory's provider endpoints remain unimplemented/unconfirmed on the current
Inventory branch. Rental-only tests use a fake client. Payment settlement and
`RentalSettled` event handling belong to the settlement workflow and are still
required before normal returns can become `COMPLETED` end-to-end.
The proposed Kafka alternative for new returns is documented in
`INVENTORY_RETURN_KAFKA_CONTRACT.md`. It is opt-in through
`Rental:InventoryReturn:Transport=Kafka` after Inventory signs off the wire contract.
The Development configuration sets `Rental:InventoryReturn:Enabled=false`, so the
recovery worker leaves return and damage receipts pending while Inventory is absent.
Set `Rental__InventoryReturn__Enabled=true` when a compatible Inventory provider
is available; pending receipts are then eligible for replay. Production defaults
to enabled.
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

## Partial return requests lifecycle

### 1. Overview and Endpoints

The partial return flow allows renters to declare their intention to return specific equipment units before supplier pickup/handover, and enables suppliers to view those requests and atomically close them upon recording asset returns.

- **Renter Order Detail**: `GET /api/v1/rental/orders/{orderId}`
  - Item response includes `units`: array of units with `assignmentId`, `status`, `hasOpenReturnRequest`, and `canRequestReturn`. Internal IDs (`assetId`, `allocationId`, serials) are never exposed to renters.
  - Item response includes `returnRequests`: array of return requests submitted on this item.
- **Renter Return Request**: `POST /api/v1/rental/items/{itemId}/return-requests`
  - Header `Idempotency-Key` (1–64 characters).
  - Body contains `actualReturnAt`, optional `note`, and `units`: list of `rentalUnitAssignmentId` and `mediaIds`.
  - Response: `ReturnRequestResponse` containing `returnRequestId`, `itemId`, `actualReturnAt`, `note`, `submittedAt`, and `units` (`rentalUnitAssignmentId`, `mediaIds`, `closedAt`).
  - At creation time, `closedAt` is always `null`.
- **Supplier SubOrder Detail**: `GET /api/v1/rental/supplier/sub-orders/{subOrderId}`
  - Item response includes `returnRequests`: array of return requests submitted for the supplier's items.
  - Isolated per supplier: suppliers only see return requests and items belonging to their suborder.
- **Supplier Record Return**: `POST /api/v1/rental/items/{itemId}/returns`
  - Unchanged endpoint contract. Atomically closes matching open request units upon persisting the `ReturnRecord`.

### 2. Return Eligibility & Single Source of Truth

Assignment return eligibility is governed by `ReturnEligibilityPolicy` in the Application layer, shared by both query mapping (`canRequestReturn`) and command validation (`CreateReturnRequestAsync`).

An assignment is eligible (`canRequestReturn == true`) if and only if:
1. The item status is `InRental`.
2. The assignment status is `HandedOver` and associated with the item's `HandoverRecord`.
3. The assignment has not yet been returned (`ReturnRecordId == null`).
4. The assignment has no currently actionable return request unit (`ClosedAt == null && RejectedAt == null`).
### 3. Atomic Closing of Request Units on Supplier Return

When a supplier successfully calls `POST /api/v1/rental/items/{itemId}/returns`:
1. The `ReturnRecord` is created for the validated assignments.
2. Open request units (`ClosedAt == null`) for the matching item and returned assignments are identified.
3. `ClosedAt` is set to the current server timestamp (`timeProvider.GetUtcNow()`), **not** the self-reported `ActualReturnAt`.
4. All updates (`ReturnRecord`, assignment status, `ClosedAt`, state transition, operation receipt, outbox) are saved and committed atomically within the same database transaction.
5. If inventory command enqueue, event enqueue, or database `SaveChangesAsync` fails, the transaction is rolled back completely; neither `ReturnRecord` nor `ClosedAt` is persisted.

**Constraints:**
- Partial returns: Returning a subset of machines in a multi-unit request closes only the returned units; remaining units stay open.
- Multiple requests: A single return call closes matching units across multiple requests.
- Direct returns: Returning units without prior return requests is valid and succeeds normally.
- Isolation: Never closes units of other items or assignments.

### 4. ClosedAt Timing and Replay Snapshot Semantics

- **Initial POST**: Returns `closedAt: null` for all units.
- **GET Order / SubOrder Details**: Reflects the current database state (live `ClosedAt` timestamp once closed by supplier).
- **Idempotent Replay**: Replaying `POST /return-requests` or `POST /returns` with the same key and payload returns the saved snapshot from `RentalOperationReceipt.ApiResponseJson`. It does not mutate data, does not change `ClosedAt`, and does not re-emit SignalR notifications or outbox events.
- **Legacy Receipt Compatibility**: Older receipts in the database stored without `ClosedAt` deserialize with `ClosedAt = null`.

### 5. Realtime Notifications

- When a return request is successfully created and committed:
  - Change type: `RETURN_REQUESTED` (`OrderChangeTypes.ReturnRequested`).
  - Sent via `IOrderHubNotifier` to the renter (`NotifyRenterOrderChangedAsync`) and the suborder's supplier (`NotifySupplierSubOrderChangedAsync`).
  - Sent strictly after transaction commit. Replay does not trigger notifications or increment order version.
  - Notification failures are caught and logged; they do not cause a committed request to fail.
- When a supplier records return:
  - Existing `RETURN_SUBMITTED` notification is dispatched. Subsequent GET calls immediately reflect updated `ClosedAt` values.

### 6. Maintenance & Legacy Data Reconciliation

For databases where return requests were created before automatic closing was introduced, the maintenance tool reconciles unclosed request units idempotently:

```powershell
# Dry-run mode (default without --apply): counts unclosed units whose assignment already has a ReturnRecord
dotnet run --project RentalService/src/Maintenance -- reconcile-return-requests

# Apply mode: reconciles units in finite batches (100 per batch) with transaction per batch
dotnet run --project RentalService/src/Maintenance -- reconcile-return-requests --apply
```

- **Logic**: Selects `ReturnRequestUnit` where `ClosedAt == null && RejectedAt == null` and the assignment has a completed `ReturnRecord` on the same item. Sets `ClosedAt = ReturnRecord.SubmittedAt`.
- **Safety**: Does not alter evidence, state transitions, or operation receipts. Re-running returns 0 records to change.

## Return disputes and manual resolution (Task 4)

### 7. Reporting Return Issues

- **Endpoint**: `POST /api/v1/rental/items/{itemId}/return-requests/{returnRequestId}/report-issue`
- **Headers**: `Idempotency-Key` (required, 1–64 characters).
- **Authorization**: Must be an authenticated human user who is either the order's renter or the item's supplier. Third parties receive 404 (IDOR isolation); service tokens receive 403 Forbidden.
- **Payload**:
  - `reason`: required, trimmed, max 500 characters.
  - `note`: optional, max 2000 characters.
  - `mediaIds`: optional array of up to 10 verified private media UUIDs uploaded by the caller.
  - `assignmentIds`: optional array of assignment UUIDs belonging to the request. If omitted, applies to all currently actionable units in the request.
- **Business Rules**:
  - Any invalid unit or media in a batch fails the entire request (all-or-nothing).
  - Does not create `ReturnRecord` or invoke Inventory commands.
  - Transitions matching unit statuses to `Disputed` and the overall request status to `Disputed`.
  - Reports are immutable audit records (`ReturnRequestIssue`); renter and supplier may each file distinct reports without overwriting each other's evidence.
  - Replaying with the same key returns the stored snapshot from the receipt.

### 8. Confirmation Overdue Tracking & Background Worker

- **Confirmation Deadline**: `ConfirmationDeadlineAt = SubmittedAt + 24 hours`, persisted at request creation time.
- **Overdue Condition**: When `now >= ConfirmationDeadlineAt`, any remaining actionable units (`ClosedAt == null && RejectedAt == null`) are considered overdue.
- **Independence from Dispute**: `OverdueConfirmation` is an independent condition; disputed units still become overdue once the 24-hour deadline elapses.
- **Background Worker**: `ReturnConfirmationOverdueWorker` runs every 60 seconds (batch size 100).
  - Executes server-side query identifying orders with overdue, unflagged actionable units (`OverdueNotifiedAt == null`).
  - Acquires order advisory lock, reloads aggregate, rechecks conditions.
  - Sets `OverdueNotifiedAt = now`, increments order version once, and enqueues `ReturnConfirmationOverdue` event into the outbox within the same database transaction.
  - If event enqueue fails, the transaction is rolled back and the flag is not persisted, allowing clean retry.
  - Dispatches `RETURN_CONFIRMATION_OVERDUE` SignalR notifications to renter and supplier after commit.
  - Invariant: Never auto-confirms returns, never alters `RentalItemStatus`, and never releases or blocks inventory.

### 9. Audited Administrative Resolution & Integration with Task 3

Administrative APIs require an Admin JWT (`admin`, `admin_platform`). Service tokens are refused.

- **List Return Requests**: `GET /api/v1/rental/admin/return-requests`
  - Supports pagination (`page`, `pageSize`) and filters: `disputed` (bool), `overdue` (bool), `itemId`, `orderId`.
- **Detail Return Request**: `GET /api/v1/rental/admin/return-requests/{returnRequestId}`
  - Includes request metadata, units with live statuses, issue reports, administrative decision history, and associated inventory tracking status.
- **Resolve Return Request**: `POST /api/v1/rental/items/{itemId}/return-requests/{returnRequestId}/resolve`
  - Header `Idempotency-Key` (required).
  - Requires `decision` (`ConfirmReceived` or `RejectRequest`), `decisionNote` (required, max 2000 chars), `assignmentIds` (non-empty, actionable), and optional `resolutionMediaIds`.
  - One decision per operation; mixed decisions are submitted via separate operations.
  - **ConfirmReceived**:
    - Executes the shared Task 3 application workflow, preserving the Admin actor identity (`submittedById = adminId`).
    - Requires `actualReturnAt` and per-unit inspection (`ReturnUnitRequest`).
    - Invariant: Units inspected as `DAMAGED` strictly require both a description and evidence photos.
    - Atomically creates `ReturnRecord`, sets `ClosedAt = now`, closes allocations via `ApplyReturnedUnits` Kafka command, records `RentalUnitsReturned` event, updates item settlement state, and records resolution audit.
  - **RejectRequest**:
    - Sets `RejectedAt = now` on targeted units and persists resolution audit.
    - Has zero Inventory or financial side effects (no `ReturnRecord`, no allocation command, no deposit deduction).
    - Immediately frees the assignment in `ReturnEligibilityPolicy`, allowing the renter to submit a new return request if the assignment is still eligible.
  - **Concurrency & Races**:
    - All concurrent operations (issue reporting, supplier confirmation, overdue worker, admin resolution, and renter re-requests) serialize deterministically under PostgreSQL order advisory locks.
    - Idempotent replays return saved receipt snapshots without duplicating records, decisions, or outbox messages.
