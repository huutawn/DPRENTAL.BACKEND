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

## Supplier Return Inspection & Confirmation

### 1. `actualReturnAt` Semantics

- **Meaning**: The exact timestamp when the supplier physically received and inspected the units (`ActualReceivedAt`).
- **Validation**:
  - Cannot be `default`.
  - Cannot be in the future relative to server time (`actualReturnAt <= now`).
  - Cannot precede the handover timestamp of the item (`actualReturnAt >= HandoverRecord.ActualHandoverAt`).
- **Distinct from `ReturnsCompletedAt`**:
  - `actualReturnAt` is the physical return timestamp declared by the supplier.
  - `ReturnsCompletedAt` is the authoritative server timestamp recorded when all units of an item have been received (`timeProvider.GetUtcNow()`). It anchors the 48-hour deduction window.

### 2. Supplier Inspection & Evidence Validation (Application Layer)

- Handled exclusively in `ReturnService.RecordReturnAsync`:
  - Caller must be an authenticated User with Supplier role who owns the item (`RentalAuthorizationPolicy.AuthorizeSupplierCaller` and `AuthorizeSupplierItem`). Service tokens and Renters are forbidden.
  - New commands are only accepted when the item is in `IN_RENTAL`.
  - `Idempotency-Key` is required (1–64 characters).
  - Each unit in the request must belong to the item, must be currently in `HandedOver` status, and must not have an existing `ReturnRecordId`.
  - `Condition` must be either `NORMAL` or `DAMAGED`.
  - **DAMAGED requirements**:
    - `Description`: mandatory, cannot be empty or only whitespace (maximum 2,000 characters).
    - `MediaIds`: mandatory, at least 1 and at most 10 valid media GUIDs.
    - Verified via `MediaService` before opening the database transaction: must exist, be in `Ready` status, `Private` visibility, and uploaded by the calling supplier (`expectedUploaderId == supplierId`). Renter media cannot be substituted for supplier evidence.
  - **NORMAL requirements**:
    - Description and MediaIds are optional.
    - If media IDs are provided, they are verified with the same uploader/ready/private rules.
  - Entire batch is verified atomically before database changes. A single invalid unit fails the entire request.

### 3. State & Financial Settlement Matrix

| Return Scenario | Item Status | DeductionDeadline | FinalDeductionAmount |
|---|---|---|---|
| Remaining units unreturned | `IN_RENTAL` | Not opened (`null`) | Not decided (`null`) |
| All units returned, ANY damaged across all batches, `DepositAmount > 0` | `PENDING_SETTLEMENT` | Server `ReturnsCompletedAt` + 48 hours | Not decided (`null`) |
| All units returned, no damaged units | `PENDING_SETTLEMENT` | Not opened (`null`) | `0.00` |
| All units returned, damaged units exist but `DepositAmount == 0` | `PENDING_SETTLEMENT` | Not opened (`null`) | `0.00` |

- **Cross-batch Damage Evaluation**: Damage is evaluated across all units ever returned for the item (`HasDamagedUnits`), not only the final return batch.
- **No Premature Completion**: Task 3 never transitions an item or order to `COMPLETED`. Items remain in `PENDING_SETTLEMENT` until downstream Inventory allocation closure and Payment settlement confirm.
- **Replay Invariance**: Replaying a previously recorded return returns the stored response snapshot; it never extends deadlines or alters financial figures.

### 4. Inventory Return Outcomes & Damage Protection

Rental enqueues `ApplyReturnedUnits` via Kafka topic `inventory.allocation.commands.v1`:
- **NORMAL units**: confirmed only when Inventory returns valid allocation, asset ID, and terminal closure status (`RELEASED` or `CLOSED`).
- **DAMAGED units**: confirmed only when Inventory returns valid allocation, asset ID, `ReturnRecordId`, valid `AllocationClosedAt`, supported protection status (`MAINTENANCE`, `OUT_OF_SERVICE`, `BLOCKED`, `CALENDAR_BLOCKED`), and non-empty `DamageDispositionRef`. Merely closing the allocation is strictly insufficient to confirm damage protection.
- **Batch Integrity**: All units in the result must match the command's units and identity (`orderId`, `itemId`, `allocationGroupId`, `returnRecordId`, `operationKey`). An invalid unit fails the entire incoming result batch atomically.
- **Rejections (`ReturnedUnitsRejected`)**: Keep the recorded `ReturnRecord` and closed request units (as supplier physically received them); mark the operation receipt `Failed`. Allocations remain unclosed.
- **Idempotency**: Duplicate valid results are no-ops; they do not increment version, emit duplicate events, or trigger redundant notifications.

### 5. Verification Limits

- In environments without a live `InventoryService` or `RecommendationService`, integration tests use mock replies and isolated PostgreSQL test databases (`PostgresTestDatabaseHelper`).
- Mock replies prove RentalService state transitions, advisory locking, idempotency, and transactional outbox behavior. They do not prove that physical assets are blocked inside the external Inventory database engine.
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
4. The assignment has no currently open return request unit (`ClosedAt == null`).

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

- **Logic**: Selects `ReturnRequestUnit` where `ClosedAt == null` and the assignment has a completed `ReturnRecord` on the same item. Sets `ClosedAt = ReturnRecord.SubmittedAt`.
- **Safety**: Does not alter evidence, state transitions, or operation receipts. Re-running returns 0 records to change.
