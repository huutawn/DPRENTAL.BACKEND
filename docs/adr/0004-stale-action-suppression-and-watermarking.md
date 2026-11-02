# ADR-0004: In-App Stale Action Suppression and Aggregate Watermarking

## Status
Accepted

## Context
In distributed event-driven systems, message delivery order cannot be guaranteed across distinct aggregates or delayed consumer processing paths. Furthermore, Kafka partitions guarantee ordering only per partition key (which is `RentalOrderId`).
However:
1. An event such as `RentalOrderCreated` might arrive delayed after `RentalOrderPaid` or `RentalOrderExpired` has already been processed (e.g. during replay or consumer lag across topic redeployment).
2. If `RentalOrderCreated` is rendered without context, it contains action prompts like "Vui lòng hoàn tất thanh toán trước hạn" (Please complete payment before deadline) and deep links to checkout payment. Displaying this prompt when the order is already `Paid` or `Expired` creates severe user confusion and support burden.
3. Similarly, `RentalSupplierConfirmationRequested` prompting the supplier to accept an order should not display an urgent action prompt if the order was already `Cancelled` or `Expired`.

## Decision
1. **Per-Aggregate, Per-Recipient Watermarking**:
   - NotificationService maintains a watermark table `notification.aggregate_watermarks`:
     - `aggregate_id` (Guid - e.g. `RentalOrderId`)
     - `recipient_user_id` (Guid)
     - `last_order_version` (long)
     - `terminal_state` (string nullable - e.g. `PAID`, `EXPIRED`, `CANCELLED`, `COMPLETED`)
     - `updated_at` (DateTimeOffset)
     - Primary key: `(aggregate_id, recipient_user_id)`
   - Global watermarks across all users are strictly prohibited. Watermarks must be scoped to `(aggregate_id, recipient_user_id)`.

2. **Order Version Semantics**:
   - `orderVersion` is an integer monotonically incremented by `RentalService` for state transitions on `RentalOrder`.
   - **Same-Version Siblings**: Multiple events emitted in the same database transaction in `RentalService` (for example, `RentalOrderPaid` and individual item events, or suborder decisions) share the same `orderVersion`.
   - Consumer rule: NotificationService DOES NOT discard events having `orderVersion` equal to `last_order_version` if their `messageId` or `messageType` differs.
   - Incoming events with `orderVersion < last_order_version`: Considered an out-of-order stale event.

3. **Stale Action Suppression Rules**:
   - When an event arrives with `orderVersion < last_order_version`, or when the aggregate watermark indicates a superseding terminal state:
     - **Actionable Notification Types** (`ORDER_CREATED`, `SUPPLIER_CONFIRMATION_REQUIRED`):
       The notification's call-to-action is suppressed. The notification is either:
       a) Rendered with informational/stale template variant (e.g. "Đơn thuê đã được tạo (thông tin lưu trữ)"), removing active payment/action buttons and deep links to active flows; OR
       b) Marked as `Suppressed` if the event is strictly superseded by a completed terminal state.
     - **Non-Actionable Informational / Audit Events** (`ORDER_PAID`, `ORDER_COMPLETED`, `ORDER_CANCELLED`):
       Retain their audit message, but update watermark to the latest version.

4. **Specific Scenario Protections**:
   - `RentalOrderCreated` after `RentalOrderPaid`: Payment call-to-action removed; deep link points to order history rather than checkout payment invoice.
   - `RentalOrderCreated` after `RentalOrderExpired` or `RentalOrderCancelled`: Payment call-to-action removed; marked informational.
   - `RentalSupplierConfirmationRequested` after `RentalOrderExpired` or `RentalOrderCancelled`: Confirmation button suppressed; marked expired.

## Consequences
### Positive
- Prevents misleading calls-to-action on user screens.
- Deterministic behavior regardless of Kafka arrival delays or out-of-order partition delivery.
- Safe handling of sibling events sharing the same order version.

### Trade-offs / Risks
- Requires maintaining watermark state in PostgreSQL.
