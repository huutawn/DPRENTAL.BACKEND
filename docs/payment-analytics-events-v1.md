# Payment financial events for Analytics (v1)

Payment keeps `payment.events.v1` for Rental and publishes the four financial event types below to `payment.analytics.v1`. Each message uses the existing `PaymentEventEnvelope` and carries only Payment snapshots. Analytics deduplicates by `messageId` and never recalculates the platform fee.

| Message type | Emission point | Stable identity |
| --- | --- | --- |
| `InvoicePaidFinancial` | Invoice becomes `PAID`. A late bank transaction emits this only when Rental's acceptance command commits. `paidAt` is bank time; envelope `occurredAt` is acceptance time. | Invoice ID + message type |
| `RentalItemFinancialSettled` | Settlement reaches `COMPLETED` after all required deposit refunds are confirmed. | Settlement ID + message type |
| `RefundCompletedFinancial` | Admin confirms the actual manual bank refund. | Manual refund requirement ID + message type |
| `SupplierPayoutCompleted` | Admin confirms the actual supplier transfer. | Supplier payout ID + message type |

All four are written to Payment outbox in the same transaction as their financial state and linked to a transaction journal entry. IntegrityHost confirms the journal and signs a publication proof with audience `analytics-service` before the publisher sends to Kafka. The existing `payment.events.v1` route retains audience `rental-service`. Retrying a command with the same operation key or republishing an outbox row keeps the same logical message ID.

## Rental refund request contract

`POST /api/v1/refunds` now requires `refundKind`, exactly `CancellationRefund` or `LatePaymentRefund`. Rental chooses this from its business decision, not from the free-text `reason`. `LatePaymentRefund` requires a bank payment at or after the invoice deadline. Settlement deposit refunds are classified internally as `SettlementDepositRefund`.

Rental must include `refundKind` on every new refund request when its caller is updated. Historical `RentalRefund` records without a category stay unclassified and do not produce a guessed Analytics refund event; existing Rental workflow events remain unchanged.

The new database columns are added by `AddAnalyticsOutboxTopic` and `AddFinancialRefundKind`. Existing outbox rows default to `payment.events.v1`; historical refund rows keep a null category. No bank account, transfer reference, address, or credentials appear in the Analytics payloads.
