# Overdue asset protection (subtask 1)

When an item is `IN_RENTAL` and the expected return date has ended in Vietnam time,
Rental schedules one `MarkAssetsOverdue` operation per handed-over assignment without a return.
For an expected end date of October 15, the first overdue instant is October 16 at
00:00 Asia/Ho_Chi_Minh (October 15 at 17:00 UTC). Partial returns protect only units
that remain with the renter. No late fee is calculated.

`OverdueProtectionWorker` scans due orders using `Rental:OverdueProtection`:

```json
{ "Enabled": true, "ScanIntervalSeconds": 60, "BatchSize": 50 }
```

The Development configuration disables this worker while Inventory does not yet
expose the agreed endpoint. To exercise it locally with a compatible Inventory
service, set `Rental__OverdueProtection__Enabled=true` for the Rental process.
Do not treat a successful Rental-only test with a fake client as proof that an
asset is unavailable in Inventory.

It stores a pending operation receipt before calling Inventory. The executor takes
the existing order lock and rechecks the assignment; a return recorded after
scheduling causes the receipt to be skipped without calling Inventory. Inventory
receives the allocation group, one allocation ID, and an operation key derived from
the assignment ID. The key remains the same across retries and receipt generations.
The executor validates Inventory's allocation, asset, group, key and protection
reference before marking the receipt successful. Transient and uncertain failures
are retried by `OperationRecoveryWorker` using the existing receipt policy.

No new database table or migration is needed; this uses the existing operation
receipt journal. This subtask does not release allocations or calculate settlement.

Inventory must keep an unreturned allocated asset unavailable while the overdue
command is in flight. Rental's periodic scan alone cannot prevent a brief gap at
the expiry boundary if Inventory independently frees allocations at midnight.
The Inventory `MarkAssetsOverdue` contract must also be idempotent for the supplied
operation key. Both properties need end-to-end verification with Inventory.
