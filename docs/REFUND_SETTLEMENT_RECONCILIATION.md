# Refund confirmation and settlement reconciliation

## Ownership and public compatibility

Payment owns refund completion, financial journals and settlement events. Rental's refund confirmation is a projection of verified Payment evidence. Existing FE routes, request/response DTOs and enum serialization are unchanged:

- `POST /api/v1/rental/admin/refunds/{rentalItemId}/complete` with `{ bankTransactionRef, note }`.
- The existing direct Payment admin endpoint remains admin-only and uses the same financial workflow.

Rental delegates authenticated admin evidence through the service-only `POST /internal/rental-refunds/synchronize`. This route is not exposed through the gateway. The Rental API-key identity and refund scope are required. Public admin endpoint policies have not been relaxed. No browser receives the service credential.

The internal request identifies the original SettleRental/RequestRefund operation, order, item and exact amount. Payment rejects mismatches and multi-item requirements rather than confirming a larger transfer implicitly. When confirmation evidence is supplied, it reuses `ManualRefundService.CompleteAsync`, including Integrity verification, transaction locks, transfer uniqueness, journal and outbox. Its deterministic confirmation operation key is derived from the Payment requirement ID.

Rental holds its order session lock but no database transaction during HTTP. A Payment error or timeout creates no local success. If Payment committed but Rental failed to save, retry or reconciliation reads Payment's result and repairs the projection. A conflicting transfer is rejected for manual review. Item COMPLETED still requires the existing validated RentalSettled event and Inventory closure; HTTP acknowledgement does not force state.

## Backfill

Deploy Payment first. Run a dry-run before deploying Rental's automatic reconciler, then apply. Use the same environment configuration as Rental (`ConnectionStrings__Postgres`, `Services__Payment__BaseUrl`, `Services__Payment__ApiKey`); do not put credentials in command arguments.

```sh
dotnet RentalService.Maintenance.dll reconcile-refund-confirmations
dotnet RentalService.Maintenance.dll reconcile-refund-confirmations --apply
```

Both modes use keyset pagination (100 items/page) and a fresh scope per item. Dry-run only probes Payment and does not confirm or save anything. Apply:

1. Rental-only historical confirmation: validate amount and transfer, submit original admin/transfer evidence to Payment; preserve Rental confirmation ID, timestamp, actor, note and bank snapshot.
2. Payment-only confirmation: mirror original Payment actor, timestamp and transfer into Rental. Do not fabricate bank evidence or force financial state.
3. Both pending with no confirmation evidence: leave pending; never auto-confirm an unrecorded transfer.
4. Conflicting amount/transfer, missing mapping or multi-item requirement: report REVIEW_REQUIRED and continue; return exit code 1 if any item needs review.

Payment records when it processed the historical confirmation; Rental retains the original admin timestamp. No ledger history is rewritten. No bank transfer is initiated. Re-running is safe and must not create another refund, journal, completion event or Rental confirmation.

The bounded automatic reconciler checks up to 50 candidates per minute with a cursor that advances past conflicts. It uses exactly the same Application workflow as the CLI; a failed item cannot starve later pages. Pending failures remain discoverable on the next scan. Kafka/outbox delivery remains responsible for completing Rental state; dead-lettered completion events require existing operational recovery, not a forced database state change.

## Verification and rollout checks

- Build and test both services, including real PostgreSQL integration tests.
- Snapshot existing dev data before applying the backfill.
- Check refunds 60/62/64: Payment requirement/settlement completed, Rental financial closure received, item and order rollup completed when all items qualify.
- Check refund 52: existing Payment completion reflected in the Rental admin queue.
- Compare original Rental confirmation IDs, actors, timestamps and transfer references before/after.
- Run apply again; verify no extra confirmation receipts or completion events.
- API-only rollback: restore previous images if necessary. Do not restore financial database snapshots after events have been published; repair through the authoritative workflow.

There is no schema migration or public DTO change. Multi-item Payment requirements are intentionally held for explicit manual reconciliation because the current Rental UI confirms one item at a time.
