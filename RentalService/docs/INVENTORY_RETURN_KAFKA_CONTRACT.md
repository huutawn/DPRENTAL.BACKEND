# Rental -> Inventory return command (proposed v1)

This is a **proposal for Inventory review**, not a frozen provider contract. Do not set
`Rental:InventoryReturn:Transport=Kafka` in a shared environment until Inventory confirms
the schema and implements the consumer and result event.

## Topics and envelope

- Rental publishes `ApplyReturnedUnits` to `inventory.allocation.commands.v1`.
- Inventory publishes `ReturnedUnitsApplied` to `inventory.allocation.events.v1` only
  after the allocation and damage protection changes commit.
- Both use envelope v1: `messageId` (GUID), `messageType`, `version=1`, `producer`,
  `aggregateId` (RentalOrderId as GUID string), `occurredAt` (UTC), `correlationId`,
  `causationId`, `payload`. Kafka partition key is RentalOrderId.
- `producer` is `rental-service` for the command and `inventory-service` for the result.

## Command payload

```json
{
  "rentalOrderId": "11111111-1111-1111-1111-111111111111",
  "rentalItemId": "22222222-2222-2222-2222-222222222222",
  "allocationGroupId": "group-reference",
  "returnRecordId": "33333333-3333-3333-3333-333333333333",
  "actualReturnAt": "2026-10-15T08:00:00Z",
  "operationKey": "stable-key-for-this-return",
  "units": [
    { "allocationId": "44444444-4444-4444-4444-444444444444", "condition": "NORMAL" },
    { "allocationId": "55555555-5555-5555-5555-555555555555", "condition": "DAMAGED" }
  ]
}
```

`units` contains **only the physical allocations in this return record**. Inventory
derives AssetId from each allocation and checks that every allocation belongs to the
group. Unreturned allocations must remain active. Rental never sends a serial number.
The operation key and message ID are stable on retry; Inbox deduplication and a
business operation-key constraint are both required on Inventory.

Inventory must release a normal returned allocation. For a damaged unit it must
protect the physical asset (`MAINTENANCE`, `OUT_OF_SERVICE`, or equivalent block)
**before making the allocation inactive**, atomically in Inventory's database. A
partial failure must not emit the success event. If the whole command is rejected,
the services still need to agree on a failure event or DLQ/reconciliation policy.

## Success result payload

```json
{
  "rentalOrderId": "11111111-1111-1111-1111-111111111111",
  "rentalItemId": "22222222-2222-2222-2222-222222222222",
  "allocationGroupId": "group-reference",
  "returnRecordId": "33333333-3333-3333-3333-333333333333",
  "operationKey": "stable-key-for-this-return",
  "receiptRef": "inventory-return-receipt",
  "appliedAt": "2026-10-15T08:00:01Z",
  "units": [
    {
      "allocationId": "44444444-4444-4444-4444-444444444444",
      "assetId": "66666666-6666-6666-6666-666666666666",
      "status": "RELEASED",
      "allocationClosedAt": "2026-10-15T08:00:01Z",
      "damageDispositionRef": null
    },
    {
      "allocationId": "55555555-5555-5555-5555-555555555555",
      "assetId": "77777777-7777-7777-7777-777777777777",
      "status": "MAINTENANCE",
      "allocationClosedAt": "2026-10-15T08:00:01Z",
      "damageDispositionRef": "block-reference"
    }
  ]
}
```

Rental checks every ID against its saved assignments and pending command. A damaged
unit must have a protected status and disposition reference. Duplicate equivalent
results are accepted; conflicting results are rejected. Invalid results are stored
in `inventory.allocation.events.v1.rental.dlq` before committing the consumed offset.
Temporary database failures leave the offset uncommitted for retry.

## Rental enablement

Set `Kafka:Enabled=true` with broker configuration,
`Rental:InventoryReturn:Enabled=true`, and
`Rental:InventoryReturn:Transport=Kafka`. This routes **new** returns to Kafka;
the existing HTTP receipts remain for historical returns. The Kafka return result
consumer uses its own group, `rental-inventory-return-consumer`. Without the switch,
the existing HTTP workflow remains selected. Inventory result confirms calendar
closure, not Payment settlement; normal final returns remain `PENDING_SETTLEMENT`
until the Payment workflow is implemented and confirmed.
