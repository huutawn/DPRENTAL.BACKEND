# Rental refund synchronization

Public DTOs and existing admin routes remain unchanged. Rental now has a service-only adapter at `POST /internal/rental-refunds/synchronize` protected by the Rental service API-key identity plus `payment.refund.write`; user JWTs cannot call this endpoint. The gateway does not expose it.

The adapter validates the original operation key, order, item and exact amount. Multi-item requirements are rejected for manual reconciliation. An absent transfer reference is a read-only probe. A supplied reference requires original admin ID/time and delegates to the existing `ManualRefundService.CompleteAsync`: no second financial workflow, no direct ledger edits, no bypass of Integrity, locking or outbox.

A completed requirement returns canonical evidence. Conflicting transfer evidence is rejected. Idempotency is derived from requirement ID; historical confirmations retain their original Rental timestamp while Payment records its processing time. Existing direct Payment admin confirmations can be mirrored by Rental's reconciler without changing event DTOs.

Deploy this adapter before Rental. It does not initiate bank transfers. Details and backfill procedures are in RentalService `docs/REFUND_SETTLEMENT_RECONCILIATION.md`.
