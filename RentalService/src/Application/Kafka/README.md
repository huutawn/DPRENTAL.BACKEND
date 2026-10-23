# Kiến trúc Kafka trong RentalService (Application/Kafka)

Tài liệu này hướng dẫn chi tiết kiến trúc, cấu hình và cách vận hành hệ thống tin nhắn Kafka trong RentalService.

---

## 1. Cấu trúc thư mục

```text
RentalService/src/Application/Kafka/
├── Configuration/
│   ├── KafkaOptions.cs            # Typed configuration options
│   └── KafkaOptionsValidator.cs   # Startup validation (Options pattern)
├── Contracts/
│   ├── KafkaMessageEnvelope.cs    # Unified Envelope v1 chung cho toàn bộ events & commands
│   ├── KafkaContractSpec.cs       # Hằng số topics, event types, command types, producer IDs
│   └── AllocationCommandPayloads.cs # Inventory allocation command payloads
├── Events/
│   ├── RentalEvents.cs            # RentalOrderCreated, RentalItemConfirmed, DeadLetter, ...
│   └── PaymentEvents.cs           # InvoicePaid, RefundCompleted, RentalSettled
├── Producers/
│   ├── IKafkaProducerTransport.cs # Transport producer chung gửi bản tin tới Kafka broker
│   ├── KafkaProducerTransport.cs  # Implementation bọc Confluent.Kafka IProducer
│   ├── OutboxMessageIdGenerator.cs# Tạo MessageId tất định từ (producer, type, identity)
│   ├── ProducerEnqueueResult.cs   # Result DTO trả về MessageId và IsReplay
│   ├── IInventoryCommandProducer.cs
│   ├── InventoryCommandProducer.cs# Enqueue CreateHold, ReleaseAllocation vào Outbox
│   ├── IRentalEventProducer.cs
│   └── RentalEventProducer.cs     # Canonical typed and domain event enqueue path
├── Consumers/
│   ├── IPaymentEventsDispatcher.cs# Entrypoint xử lý Intake & Inbox Processing
│   └── PaymentEventsDispatcher.cs # Canonical JSON deduplication, non-swallowing DLQ
├── Workers/
│   ├── KafkaPaymentEventsConsumerWorker.cs # Consumer worker (EnableAutoCommit/OffsetStore=false)
│   ├── OutboxPublisherWorker.cs            # Claim 1 message, check lease, publish, ack
│   └── InboxProcessingWorker.cs            # Process pending retry inbox messages
├── DependencyInjection/
│   └── KafkaServiceCollectionExtensions.cs # AddKafka() extension method
├── appsettings.kafka.example.json          # File cấu hình mẫu
└── README.md                               # Tài liệu hướng dẫn (file này)
```

---

## 2. Kiến trúc Pipeline và Luồng Kafka

### 2.1. Checkout bất đồng bộ (202 Accepted + Polling)
- API `POST /api/v1/rental/checkout` trả về HTTP `202 Accepted` kèm `statusUrl` (`/api/v1/rental/checkout/operations/{operationId}`).
- Các lệnh `CreateHold` được enqueue bền vững vào Outbox (`rental_outbox_messages`) tới topic `inventory.allocation.commands.v1`.
- Worker xuất bản Outbox gửi lệnh tới broker; `InventoryAllocationResultHandler` xử lý kết quả:
  - **All-or-nothing**: Nếu tất cả các dòng giữ lịch thành công, hệ thống tạo đơn hàng nguyên tử, tạo hóa đơn qua HTTP với PaymentService, và đánh dấu thao tác thành công.
  - Nếu có bất kỳ dòng nào thất bại hoặc quá hạn 30 phút: thao tác thất bại và tự động enqueue lệnh bù trừ `ReleaseAllocation` qua Kafka cho toàn bộ các dòng đã giữ.

### 2.2. Payment Confirmation & Supplier Confirmation
- `PaymentService` phát sự kiện `InvoicePaid` tới topic `payment.events.v1` với partition key bằng `RentalOrderId`.
- Rental consumer ghi nhận Inbox, chuyển đơn hàng sang trạng thái `Paid`, và phát lệnh `ExtendHold` qua Kafka tới Inventory.
- Khi nhận được xác nhận `InventoryAllocationCommandSucceeded` cho `ExtendHold`, hệ thống hoàn tất receipt và phát sự kiện `RentalSupplierConfirmationRequested` để supplier tiến hành xác nhận đơn con.

### 2.3. Supplier Decision & Handover
- Supplier accept phát lệnh `ConfirmAllocation` qua Kafka. Khi Inventory xác nhận thành công, mục thuê chuyển sang `AwaitingHandover` và phát sự kiện `RentalItemConfirmed`.
- Bàn giao thiết bị (`HandoverItem`): Lưu evidence immutable của supplier, ghi nhận biên nhận tác vụ `MarkAssetsHandedOver` và enqueue lệnh qua Kafka.
- Trạng thái thiết bị chỉ chuyển sang `HandedOver` và mục thuê chuyển sang `InRental` khi nhận được sự kiện xác nhận thành công từ Inventory.

### 2.4. Return, Overdue & Runbook Đối soát
- Trả thiết bị: Phát lệnh `ApplyReturnedUnits` qua topic `inventory.allocation.commands.v1`; kết quả `ReturnedUnitsApplied` / `ReturnedUnitsRejected` được ghi Inbox và xử lý bởi cùng `PaymentEventsDispatcher` cùng consumer group với allocation result. State, Inbox, và `RentalItemReturned` outbox commit cùng transaction.
- Các receipt return cũ còn pending qua HTTP được chuyển sang trạng thái `MANUAL_RECONCILIATION_REQUIRED` bằng migration dữ liệu riêng.
- MarkAssetsOverdue dùng Kafka command `MarkAssetsOverdue`; Inventory giữ nguyên allocation occupancy để ngăn tái cấp phát trong khi thiết bị quá hạn. Kết quả `InventoryAllocationCommandSucceeded`/`Rejected` đi qua Inbox cùng dispatcher với allocation result.
- Mọi tác vụ bàn giao hoặc trả thiết bị quá 30 phút chưa nhận được kết quả acknowledgement sẽ được chuyển sang `MANUAL_RECONCILIATION_REQUIRED` để đối soát thủ công qua runbook của Rental, không tự suy diễn thành công.
---

## 3. Envelope dùng chung (Version 1)

Mọi message (cả Event và Command) đều được bao gói bằng một định dạng Envelope chuẩn duy nhất (`KafkaMessageEnvelope<TPayload>`):

```json
{
  "messageId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "messageType": "RentalOrderCreated",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "occurredAt": "2026-10-14T08:00:00+07:00",
  "correlationId": "corr-uuid-optional",
  "causationId": null,
  "payload": { ... }
}
```

- **Partition Key**: Luôn là `RentalOrderId` (`AggregateId`), đảm bảo trật tự bản tin của cùng một đơn hàng trên Kafka partition.
- **Data Privacy**: `RentalOrderCreated` chỉ chứa `RentalOrderId`, `OrderCode`, `RenterId`, danh sách items (Listing, Supplier, Quantity, Interval, Giá thuê, Cọc), hạn thanh toán. **Tuyệt đối không chứa physical identity (AssetId, SerialNumber, AssetCode) hoặc snapshot thông tin liên lạc cá nhân**.

---

## 4. Cơ chế Bền vững và Idempotency

### 4.1. Enqueue vào Outbox
- `MessageId` được sinh tất định từ: `ComputeDeterministicId(producer, messageType, logicalOperationIdentity)`.
- Enqueue nguyên tử qua `IOutboxRepository.EnqueueAtomicAsync`:
  - Nếu `MessageId` chưa tồn tại: lưu mới vào Outbox.
  - Nếu `MessageId` đã tồn tại và cùng nội dung (canonical JSON): trả về message cũ với cờ `IsReplay = true`.
  - Nếu `MessageId` đã tồn tại nhưng khác nội dung: trả về lỗi `Conflict` (`OUTBOX_MESSAGE_CONFLICT`).
- **Transaction**: Relational enqueue yêu cầu caller mở transaction; nếu chưa có thì trả `OUTBOX_TRANSACTION_REQUIRED`. Outbox không tự mở hoặc commit transaction riêng. Non-relational test providers chỉ hỗ trợ test hành vi producer, không chứng minh tính nguyên tử.

### 4.2. Service command ownership

| Direction | Read/query | Command/mutation | Canonical transport |
|---|---|---|---|
| Rental → Inventory | Listing snapshot, availability | CreateHold, ExtendHold, ConfirmAllocation, ReleaseAllocation, MarkAssetsHandedOver, ApplyReturnedUnits | HTTP read; Kafka command |
| Inventory → Rental | Allocation command result; returned units applied/rejected | Result events | Kafka |
| Rental → Payment | CreateInvoice, CancelInvoice, RequestRefund, SettleRental | HTTP command |
| Payment → Rental | InvoicePaid, RefundCompleted, RentalSettled | Kafka event |

`PaymentCommandProducer` and `payment.commands.v1` were removed. `RentalService` is the only Rental producer identity. Paid and completed recommendation signals use `renterId`, `quantity`, and payload `occurredAt`; schemas accept either the historical `userId` shape or current `renterId` shape, never both. Drain the old consumer group and audit pending messages before switching it; preserve current offsets, do not reset offsets, and do not replay historical events for Recommendation. These messages must not contain `SerialNumber`, `AssetCode`, `DeliveryAddress`, or `RefundAccount`.

### 4.3. Xuất bản tin (Outbox Publisher Worker)
- **Cấu hình Confluent**: `EnableIdempotence = true`, `Acks = Acks.All`, `MessageSendMaxRetries = 3`, `RetryBackoffMs = 100`.
- **Claim từng message**: Thay vì batch claim 50 message cùng lúc (dễ hết hạn lease), worker claim từng message ngay trước khi gửi (`ClaimNextPendingAsync` sử dụng `FOR UPDATE SKIP LOCKED` trên PostgreSQL), tối đa 50 message mỗi vòng.
- **Lease Duration**: 30 giây mặc định. Kiểm tra lại hạn lease ngay trước khi publish.
- **Delivery Timeout**: 10 giây mặc định qua `CancellationTokenSource`.
- **Acknowledgement**: Chỉ đánh dấu `Published` sau khi nhận được `PersistenceStatus.Persisted` từ broker.
- **Shutdown**: Flush có thời hạn (15s mặc định), không drop message đang gửi dở.

### 4.4. Tiêu thụ bản tin (Payment Events Consumer Worker)
- **Tắt auto commit**: `EnableAutoCommit = false`, `EnableAutoOffsetStore = false`.
- **Chỉ commit khi an toàn**: Offset chỉ được commit sau khi `RentalInboxMessage` hoặc DLQ đã được lưu bền vững vào PostgreSQL, hoặc khi xác nhận duplicate thành công.
- **Không skip lỗi**: Nếu xảy ra lỗi lưu trữ database (kết nối, deadlock), worker thử lại bản ghi đang giữ; tuyệt đối không đọc tiếp và không commit vượt qua bản ghi lỗi.
- **Deduplication Canonical JSON**: So sánh toàn bộ nội dung JSON sau khi canonical hóa (sắp xếp object properties theo bảng chữ cái Ordinal, giữ nguyên thứ tự phần tử array). Với Inbox cũ, deserialize và so sánh từ payload đã lưu.
- **DLQ an toàn**: Bản tin lỗi schema/conflict được đưa vào DLQ (`payment.events.v1.dlq`) với ID tất định từ `(topic, partition, offset)`. Lỗi ghi DLQ không bị swallow mà được trả về để retry.

---

## 5. Chuẩn hóa Rental Events phục vụ Analytics (`rental.events.v1`)

### 5.1. Semantics về Định danh, Phiên bản và Deduplication
- **Deduplication**: Consumer Analytics bắt buộc dedupe bằng `messageId` (UUID v5 tất định từ `(producer, messageType, logicalEntityId)`). Replay nghiệp vụ không tạo `messageId` mới; publisher retry gửi lại nguyên message đã lưu trong Outbox mà không dựng lại payload/version từ trạng thái mới.
- **Thứ tự thay đổi qua `orderVersion`**:
  - `orderVersion` là số nguyên dương bắt buộc phản ánh `RentalOrder.Version` sau khi thay đổi nghiệp vụ và rollup hoàn tất trong cùng transaction.
  - `orderVersion` dùng để nhận biết thứ tự các thay đổi theo từng đơn hàng (`RentalOrderId`), có thể có khoảng nhảy (do các thay đổi nội bộ hoặc thao tác không phát event ra ngoài).
  - **Nhiều event cùng transaction**: Các event phát sinh từ cùng một thay đổi đã commit (ví dụ: `RentalOrderPaid` và các `RentalItemPaid`, hoặc nhiều `RentalItemRejected` trong cùng thao tác từ chối suborder) sẽ mang cùng `orderVersion`. Consumer tuyệt đối **không được loại bỏ** các event có `orderVersion` bằng nhau nếu chúng có `messageId` hoặc `messageType` khác nhau.
  - **Event lịch sử**: Các event lịch sử phát trước khi chuẩn hóa thiếu trường `orderVersion` được coi là *chưa biết version* (`null`/`unknown`), không được mặc định thành `0`.
- **Envelope Version vs Business Version**: Envelope version giữ nguyên giá trị `1` (`KafkaContractSpec.CurrentEnvelopeVersion = 1`). Topic tiếp tục là `rental.events.v1`.

### 5.2. Mã Reason Chuẩn Hóa
Thay thế toàn bộ văn bản tự do trong các sự kiện huỷ/từ chối/hết hạn bằng 6 mã cố định:

| Mã Reason | Ý nghĩa nghiệp vụ | Sự kiện áp dụng |
|---|---|---|
| `SUPPLIER_REJECTED` | Nhà cung cấp từ chối dòng thuê hoặc đơn con | `RentalItemRejected` |
| `RENTER_CANCELLED` | Khách thuê chủ động huỷ đơn/dòng thuê trước hạn | `RentalOrderCancelled`, `RentalItemCancelled` |
| `HANDOVER_NOT_DELIVERED` | Khách huỷ do nhà cung cấp không giao thiết bị | `RentalItemCancelled`, `RentalOrderCancelled` |
| `SUPPLIER_CONFIRMATION_EXPIRED` | Quá hạn 24h hoặc deadline ngày thuê mà NCC không xác nhận | `RentalItemExpired` |
| `HANDOVER_EXPIRED` | Quá hạn giao thiết bị (D+1 + grace period) mà không bàn giao | `RentalItemExpired`, `RentalOrderExpired` |
| `PAYMENT_EXPIRED` | Quá hạn thanh toán 30 phút mà đơn hàng chưa được thanh toán | `RentalOrderExpired`, `RentalItemExpired` |

*Ghi chú*: Nội dung văn bản lý do gốc tiếp tục được lưu trữ đầy đủ trong entity và audit log của `RentalService` để phục vụ đối soát và hỗ trợ khách hàng. Trường `OptionalNote` trong contract `RentalItemRejectedPayload` được giữ lại để tương thích ngược nhưng luôn phát giá trị `null`.

### 5.3. Bảo Mật và Không Phát Dữ Liệu Nhạy Cảm
Payload được ánh xạ tường minh; tuyệt đối không serialize thô entity, snapshot giao hàng/hoàn tiền, ghi chú cá nhân hoặc response của dịch vụ bên ngoài:
- Không phát: số điện thoại, tài khoản ngân hàng, thông tin hoàn tiền cá nhân, địa chỉ đầy đủ (chỉ dùng ID), số serial (`SerialNumber`), mã tài sản nội bộ (`AssetCode`), hoặc token/secret thanh toán.
- Dòng thiết bị (`RentalOrderCreated.Items`) bổ sung snapshot thiết bị: `equipmentTypeId`, `equipmentTypeName`, `brandId`, `brandName`, `model`, ánh xạ trực tiếp từ snapshot của `RentalItem` (không gọi sang `InventoryService`, giữ nullable theo snapshot thực tế, không tạo giá trị giả).

### 5.4. Hướng Dẫn Consumer Trước Khi Rollout Producer
Trước khi producer mới được triển khai, các consumer (Recommendation, Analytics, Notification):
1. Cần cấu hình parser JSON chấp nhận trường bổ sung (`ignore unknown properties` / bỏ `additionalProperties: false` nghiêm ngặt) để không bị lỗi schema validation khi xuất hiện các trường mới như `orderVersion`, `equipmentTypeName`, `brandName`, v.v.
2. Áp dụng deduplication bằng `messageId` trên key-value cache hoặc state store với TTL phù hợp (tối thiểu 7 ngày).

### 5.5. Ví Dụ Payload Chuẩn Hóa

#### `RentalOrderCreated`
```json
{
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "messageType": "RentalOrderCreated",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-23T10:00:00Z",
  "correlationId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "payload": {
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "orderCode": "RNT-20261023-0001",
    "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "currency": "VND",
    "totalRentalAmount": 1500000.00,
    "totalDepositAmount": 5000000.00,
    "totalPayableAmount": 6500000.00,
    "paymentDeadline": "2026-10-23T10:30:00Z",
    "createdAt": "2026-10-23T10:00:00Z",
    "items": [
      {
        "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
        "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90",
        "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1",
        "quantity": 2,
        "expectedStartDate": "2026-10-25",
        "expectedEndDate": "2026-10-28",
        "rentalAmount": 1500000.00,
        "depositAmount": 5000000.00,
        "equipmentTypeId": "f6a7b8c9-d0e1-2f3a-4b5c-6d7e8f90a1b2",
        "equipmentTypeName": "Máy quay Sony FX3",
        "brandId": "a7b8c9d0-e1f2-3a4b-5c6d-7e8f90a1b2c3",
        "brandName": "Sony",
        "model": "ILME-FX3"
      }
    ],
    "orderVersion": 1
  }
}
```

#### `RentalOrderPaid` & `RentalItemPaid`
```json
{
  "messageId": "660e8400-e29b-41d4-a716-446655440001",
  "messageType": "RentalOrderPaid",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-23T10:15:00Z",
  "payload": {
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "invoiceRef": "INV-2026-001",
    "paymentRef": "PAY-2026-001",
    "paidAmount": 6500000.00,
    "paidAt": "2026-10-23T10:15:00Z",
    "orderVersion": 2
  }
}
```

```json
{
  "messageId": "770e8400-e29b-41d4-a716-446655440002",
  "messageType": "RentalItemPaid",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-23T10:15:00Z",
  "payload": {
    "userId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90",
    "equipmentTypeId": "f6a7b8c9-d0e1-2f3a-4b5c-6d7e8f90a1b2",
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
    "orderVersion": 2
  }
}
```

#### `RentalItemRejected`
```json
{
  "messageId": "880e8400-e29b-41d4-a716-446655440003",
  "messageType": "RentalItemRejected",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-23T11:00:00Z",
  "payload": {
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
    "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1",
    "decisionAt": "2026-10-23T11:00:00Z",
    "reason": "SUPPLIER_REJECTED",
    "optionalNote": null,
    "orderVersion": 3
  }
}
```

#### `RentalItemCompleted` & `RentalOrderCompleted`
```json
{
  "messageId": "990e8400-e29b-41d4-a716-446655440004",
  "messageType": "RentalItemCompleted",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-29T16:00:00Z",
  "payload": {
    "userId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90",
    "equipmentTypeId": "f6a7b8c9-d0e1-2f3a-4b5c-6d7e8f90a1b2",
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
    "orderVersion": 5
  }
}
```

```json
{
  "messageId": "aa0e8400-e29b-41d4-a716-446655440005",
  "messageType": "RentalOrderCompleted",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "occurredAt": "2026-10-29T16:00:00Z",
  "payload": {
    "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
    "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "completedAt": "2026-10-29T16:00:00Z",
    "orderVersion": 5
  }
}
```
- **Inbox Processing**: Re-read bản ghi Inbox sau khi lấy được khóa `OrderLock`. Cập nhật trạng thái nghiệp vụ, `Inbox.Processed` và các bản tin Outbox phát sinh trong cùng một database transaction.

---

## 5. Hướng dẫn Cấu hình

Kafka bị **tắt mặc định** khi chưa được cấu hình (`Enabled = false`).
Khi bật (`Enabled = true`), nếu thiếu `BootstrapServers` hoặc các thông số topics/timeouts bị sai, ứng dụng sẽ **báo lỗi ngay lúc startup (`OptionsValidationException`)**, không âm thầm vô hiệu hóa worker.

### Cấu hình mẫu trong `appsettings.json` hoặc Environment Variables:

```json
{
  "Kafka": {
    "Enabled": true,
    "BootstrapServers": "localhost:29092",
    "ClientId": "rental-service",
    "ConsumerGroupId": "rental-payment-events-consumer",
    "RentalEventsTopic": "rental.events.v1",
    "PaymentEventsTopic": "payment.events.v1",
    "PaymentDlqTopic": "payment.events.v1.dlq",
    "InventoryCommandsTopic": "inventory.allocation.commands.v1",
    "InventoryAllocationEventsTopic": "inventory.allocation.events.v1",
    "InventoryAllocationEventsDlqTopic": "inventory.allocation.events.v1.dlq",
    "DeliveryTimeoutSeconds": 10,
    "LeaseDurationSeconds": 30,
    "MaxBatchPerRound": 50,
    "OutboxPollingIntervalSeconds": 15,
    "InboxPollingIntervalSeconds": 15
  }
}
```

### Biến môi trường tương ứng:
- `Kafka__Enabled=true`
- `Kafka__BootstrapServers=kafka:9092`
- `Kafka__ConsumerGroupId=rental-payment-events-consumer`
- `Kafka__SecurityProtocol=Plaintext` (hoặc `SaslPlaintext`, `SaslSsl`)
- `Kafka__SaslMechanism=Plain` (hoặc `ScramSha256`, `ScramSha512`)
- `Kafka__SaslUsername=...`
- `Kafka__SaslPassword=...`
