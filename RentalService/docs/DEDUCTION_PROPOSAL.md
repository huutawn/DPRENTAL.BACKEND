# Supplier deposit deduction (subtask 5)

`POST /api/v1/rental/items/{itemId}/deductions` requires an authenticated supplier who owns the item and an `Idempotency-Key` header (1–64 characters).

```json
{
  "assessments": [
    { "assetId": "22222222-2222-2222-2222-222222222222", "level": 2 }
  ],
  "damageDescription": "Gãy răng gầu máy xúc",
  "evidenceMediaIds": ["11111111-1111-1111-1111-111111111111"]
}
```

The supplier picks a **damage level per asset**; the server computes the amount (DPR-276). Supplier không gửi số tiền.

| Level | Name | Percent |
|---|---|---|
| 0 | Không hư | 0% |
| 1 | Nhẹ | 10% |
| 2 | Vừa | 30% |
| 3 | Nặng | 50% |
| 4 | Hỏng hoàn toàn | 100% |

- Per asset: `round(Percent × DepositPerUnitSnapshot / 100, 2)`, capped at 100% of that asset's deposit. Loss beyond the deposit is settled between the parties outside the system.
- `TotalProposedAmount` = sum over assessed assets. Assets at a 0% level are not recorded as affected.
- The table is frozen into `RentalItem.DamageLevelsSnapshot` at checkout; later changes to `rental.damage_levels` never change an existing order. A level absent from the item's snapshot is rejected (`INVALID_DAMAGE_LEVEL`); an empty or duplicate asset list is rejected (`INVALID_DAMAGE_ASSESSMENTS`); every assessed asset, even at a 0% level, must be a unit of this item returned damaged (`ASSET_NOT_DAMAGED_IN_ITEM`); all assets at 0% is rejected (`NO_DAMAGE_SELECTED`).
- `GET /api/v1/rental/damage-levels` (authenticated) returns the current table for the UI.

The total is still stored as a **single amount on the proposal**; per-asset levels are kept in the idempotency receipt payload. Evidence must be private, ready, and uploaded by the supplier. Only assets returned as damaged in this item may be selected.

The supplier window starts when the server records the final unit return. A proposal is accepted at or before `DeductionDeadline`; the timeout worker runs strictly after it. A repeated idempotency key with the same request returns the original response. A changed request with the same key returns conflict.

After 48 hours without a proposal, the worker finalizes deduction at zero once. The item remains `PENDING_SETTLEMENT`. Payment settlement and `RentalSettled` confirmation are required before `COMPLETED`; this integration is not yet implemented. Renter response and admin dispute are subtask 6.

Migration `AddDamageLevels` creates and seeds `rental.damage_levels`, adds `rental_items.DamageLevelsSnapshot` (jsonb) and backfills it from the seed for items created before it.

Migration `AddDeductionProposalTotal` adds proposal-level amount, description, and evidence. It backfills existing proposal amounts and evidence from assignment rows. Apply the migration to each Rental database before starting this version of the API.
