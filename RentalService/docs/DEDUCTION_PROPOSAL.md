# Supplier deposit deduction (subtask 5)

`POST /api/v1/rental/items/{itemId}/deductions` requires an authenticated supplier who owns the item and an `Idempotency-Key` header (1–64 characters).

```json
{
  "deductionAmount": 100000,
  "damageDescription": "Lens repair",
  "evidenceMediaIds": ["11111111-1111-1111-1111-111111111111"],
  "affectedAssetIds": ["22222222-2222-2222-2222-222222222222"]
}
```

The amount is a **single total for the RentalItem**. Affected assets are linked to the proposal for audit; no per-asset amount is inferred. Evidence must be private, ready, and uploaded by the supplier. Only assets returned as damaged in this item may be selected.

The supplier window starts when the server records the final unit return. A proposal is accepted at or before `DeductionDeadline`; the timeout worker runs strictly after it. A repeated idempotency key with the same request returns the original response. A changed request with the same key returns conflict.

After 48 hours without a proposal, the worker finalizes deduction at zero once. The item remains `PENDING_SETTLEMENT`. Payment settlement and `RentalSettled` confirmation are required before `COMPLETED`; this integration is not yet implemented. Renter response and admin dispute are subtask 6.

Migration `AddDeductionProposalTotal` adds proposal-level amount, description, and evidence. It backfills existing proposal amounts and evidence from assignment rows. Apply the migration to each Rental database before starting this version of the API.
