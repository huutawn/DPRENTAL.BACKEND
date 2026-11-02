# Kafka Event Contracts Specification — Version 1 (`rental.events.v1`)

## 1. Scope & Protocol Overview

This document specifies the Kafka event consumption contract for `NotificationService` (consumer group: `notification-service-v1`) on topic `rental.events.v1`.

### 1.1. Broker Trust & Transport Security
- **Broker Authentication**: NotificationService connects to the Kafka cluster using SASL/SCRAM or mTLS.
- **Trust Boundary**: The JSON envelope contains a `producer` metadata field. However, **NotificationService does not rely on the JSON `producer` field as independent authentication proof**. Security and tenant isolation are enforced at the Kafka Broker network/ACL layer and via transactional inbox deduplication.
- **Envelope Version**: Version `1` (`version = 1`).

---

## 2. Standard Message Envelope Specification

All messages published to `rental.events.v1` use the standard envelope structure:

```json
{
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "messageType": "RentalOrderPaid",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-11-02T03:00:00Z",
  "correlationId": "order:a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "causationId": null,
  "payload": {}
}
```

### Envelope Field Definitions:
| Field | Type | Required | Description |
|---|---|---|---|
| `messageId` | UUID string | Yes | Unique, deterministic message identifier (generated via SHA-256 by producer). Must not be empty GUID (`00000000-0000-0000-0000-000000000000`). |
| `messageType` | String | Yes | Name of the business event (e.g. `RentalOrderPaid`). |
| `version` | Integer | Yes | Envelope schema version. Must equal `1`. |
| `producer` | String | Yes | Service name emitting the event. Expected: `"RentalService"`. |
| `aggregateId` | UUID string | Yes | Aggregate identifier. For order events, matches `RentalOrderId`. |
| `occurredAt` | ISO-8601 UTC string | Yes | Timestamp when the domain event occurred. |
| `correlationId` | String | No | Trace or correlation identifier. |
| `causationId` | UUID string | No | Message ID of the event/command that caused this event. |
| `payload` | Object | Yes | Event-specific data payload matching the JSON Schema for `messageType`. |

---

## 3. Envelope Validation & Quarantine Policy

### 3.1. Validation Rules
A message is valid and eligible for domain processing only if:
1. JSON is syntactically valid.
2. `messageId` is a non-empty GUID.
3. `version` equals `1`.
4. `producer` equals `"RentalService"`.
5. `occurredAt` is a valid ISO-8601 UTC date-time string.
6. `messageType` is one of the 10 supported Phase 1 MVP types.
7. `payload` strictly conforms to the JSON Schema for that `messageType`.

### 3.2. Error Classification & Quarantine (DLQ)
- **Known Producer, Unknown Message Type (Ignored)**:
  If a message arrives from `RentalService` with an unknown or future `messageType` (e.g. `RentalItemReturned`), it is categorized as non-actionable. NotificationService logs an informational trace and commits the offset. It is NOT routed to DLQ and does NOT halt the consumer.
- **Contract Violation / Malformed Envelope (Quarantine)**:
  If the envelope contains unparseable JSON, an invalid `version` (`!= 1`), an empty `messageId`, an untrusted producer, or fails schema validation, it cannot be processed.
  - The message is written to the Dead Letter Queue: `rental.events.v1.notification.dlq`.
  - A quarantine record is written to `notification.inbox_receipts` with status `Quarantined`.
  - The consumer offset is committed to prevent consumer partition stalls (no infinite retries for poison pills).

---

## 4. Target Event Specifications (Phase 1 MVP)

### 4.1. `RentalOrderCreated`
- **MessageType**: `RentalOrderCreated`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `ORDER`
- **Description**: Emitted when a checkout order is successfully committed and awaiting payment.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "orderCode": "RNT-20261102-0001",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "depositAmount": 1000000.0,
  "rentalFeeAmount": 500000.0,
  "totalPayableAmount": 1500000.0,
  "paymentDeadline": "2026-11-02T03:30:00Z",
  "createdAt": "2026-11-02T03:00:00Z",
  "items": [
    {
      "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
      "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f9a",
      "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
      "quantity": 1,
      "rentalFee": 500000.0,
      "depositAmount": 1000000.0,
      "depositRefundPercentage": 100.0,
      "interval": {
        "startDate": "2026-11-05",
        "endDate": "2026-11-07"
      },
      "equipmentTypeId": "f6a7b8c9-d0e1-2f3a-4b5c-6d7e8f9a0b1c",
      "equipmentTypeName": "Camera",
      "brandId": "a7b8c9d0-e1f2-3a4b-5c6d-7e8f9a0b1c2d",
      "brandName": "Sony",
      "model": "A7 IV"
    }
  ],
  "orderVersion": 1
}
```

---

### 4.2. `RentalOrderPaid`
- **MessageType**: `RentalOrderPaid`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `PAYMENT`
- **Description**: Emitted when PaymentService confirms full payment for the order.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "invoiceRef": "INV-20261102-0001",
  "paymentRef": "PAY-20261102-0001",
  "paidAmount": 1500000.0,
  "paidAt": "2026-11-02T03:15:00Z",
  "orderVersion": 2
}
```

---

### 4.3. `RentalSupplierConfirmationRequested`
- **MessageType**: `RentalSupplierConfirmationRequested`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.supplierId`
- **Category**: `ORDER`
- **Description**: Emitted after payment when inventory hold is confirmed, prompting the supplier to review and confirm the suborder before the deadline.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
  "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
  "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f9a",
  "quantity": 1,
  "supplierConfirmationDeadline": "2026-11-03T03:15:00Z",
  "orderVersion": 2
}
```

---

### 4.4. `RentalOrderExpired`
- **MessageType**: `RentalOrderExpired`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `ORDER`
- **Description**: Emitted when an order expires due to payment timeout (30 mins) or supplier confirmation timeout (24 hours).

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "expiredAt": "2026-11-02T03:30:00Z",
  "reason": "PAYMENT_EXPIRED",
  "orderVersion": 2
}
```

---

### 4.5. `RentalOrderCancelled`
- **MessageType**: `RentalOrderCancelled`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `ORDER`
- **Description**: Emitted when an order is cancelled by the renter or cancelled due to supplier non-delivery.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "cancelledAt": "2026-11-02T04:00:00Z",
  "reason": "RENTER_CANCELLED",
  "orderVersion": 3
}
```

---

### 4.6. `RentalItemHandoverCompleted`
- **MessageType**: `RentalItemHandoverCompleted`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `HANDOVER`
- **Description**: Emitted after InventoryService verifies physical equipment handover and transitions the item to InRental.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
  "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f9a",
  "quantity": 1,
  "handoverRecordId": "f7a8b9c0-d1e2-3a4b-5c6d-7e8f9a0b1c2d",
  "actualHandoverAt": "2026-11-05T08:00:00Z",
  "completedAt": "2026-11-05T08:05:00Z",
  "orderVersion": 4
}
```

---

### 4.7. `RentalHandoverOverdue`
- **MessageType**: `RentalHandoverOverdue`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipients**: Both `payload.supplierId` (In-app + Email) and `payload.renterId` (In-app only)
- **Category**: `HANDOVER`
- **Description**: Emitted by HandoverOverdueWorker when scheduled handover time has elapsed without supplier completing handover.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
  "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "expectedStartDate": "2026-11-05",
  "overdueAt": "2026-11-05T12:00:00Z",
  "orderVersion": 3
}
```

---

### 4.8. `ReturnConfirmationOverdue`
- **MessageType**: `ReturnConfirmationOverdue`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipients**: Both `payload.supplierId` (In-app + Email) and `payload.renterId` (In-app only)
- **Category**: `RETURN`
- **Description**: Emitted by ReturnConfirmationOverdueWorker when equipment was returned but the supplier failed to complete inspection within the statutory deadline.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
  "returnRequestId": "a8b9c0d1-e2f3-4a5b-6c7d-8e9f0a1b2c3d",
  "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "confirmationDeadlineAt": "2026-11-08T18:00:00Z",
  "overdueAt": "2026-11-08T18:01:00Z",
  "overdueAssignmentIds": [
    "b9c0d1e2-f3a4-5b6c-7d8e-9f0a1b2c3d4e"
  ],
  "orderVersion": 5
}
```

---

### 4.9. `RentalOrderCompleted`
- **MessageType**: `RentalOrderCompleted`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `ORDER`
- **Description**: Emitted when all suborders and items have completed the rental lifecycle and deposit settlement is finished.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "completedAt": "2026-11-09T10:00:00Z",
  "orderVersion": 6
}
```

---

### 4.10. `LatePaymentRefundRequested`
- **MessageType**: `LatePaymentRefundRequested`
- **AggregateId**: `RentalOrderId`
- **PartitionKey**: `RentalOrderId`
- **Recipient**: `payload.renterId`
- **Category**: `PAYMENT`
- **Description**: Emitted when a late payment arrives for an order that was already expired or cancelled, initiating a refund request to PaymentService.
- **FINANCIAL SEMANTICS CONSTRAINT**: This event indicates that refund processing has been **REQUESTED / INITIATED**. It does **NOT** indicate that the refund was completed or that money has arrived in the customer bank account.

#### Payload Schema:
```json
{
  "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "invoiceRef": "INV-20261102-0001",
  "paymentRef": "PAY-20261102-0001",
  "totalRefundAmount": 1500000.0,
  "requestedAt": "2026-11-02T04:30:00Z",
  "orderVersion": 3
}
```

---

## 5. Same-Version Sibling Semantics & Ordering

In `RentalService`, multiple domain events can be generated and committed within a single database transaction (e.g. `RentalOrderPaid` alongside multiple `RentalItemPaid` events, or multiple suborder events).
1. **Shared Version**: Sibling events originating from the same atomic transaction share the exact same `orderVersion`.
2. **Sibling Deduplication Invariant**: NotificationService **MUST NOT discard** an incoming event merely because its `orderVersion` equals the currently stored `last_order_version` on the aggregate watermark. An event is only considered a duplicate if its `(consumer_name, message_id)` already exists in `notification.inbox_receipts`.
3. **Partition Order**: All events for the same `RentalOrderId` are partitioned by `RentalOrderId`, guaranteeing strictly sequential arrival at the Kafka consumer partition level.
