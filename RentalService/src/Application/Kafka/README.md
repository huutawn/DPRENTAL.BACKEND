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
│   └── ProposedCommands.cs        # PROPOSED: CreateHold, ReleaseAllocation, CreateInvoice
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
│   ├── IPaymentCommandProducer.cs
│   ├── PaymentCommandProducer.cs  # Enqueue CreateInvoice vào Outbox
│   ├── IRentalEventProducer.cs
│   └── RentalEventProducer.cs     # Enqueue RentalOrderCreated vào Outbox
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
- Trả thiết bị: Phát lệnh `ApplyReturnedUnits` qua Kafka, xử lý kết quả qua consumer chuyên trách `InventoryReturnResultHandler` (`ReturnedUnitsApplied` / `ReturnedUnitsRejected`), đảm bảo thiết bị hỏng phải được bảo vệ/khóa trước khi đóng allocation.
- Các receipt return cũ còn pending qua HTTP được chuyển sang trạng thái `MANUAL_RECONCILIATION_REQUIRED` bằng migration dữ liệu riêng.
- Lệnh bảo vệ quá hạn (Overdue) duy trì qua HTTP với InventoryService do Inventory chưa hỗ trợ command Kafka tương ứng.
- Mọi tác vụ bàn giao hoặc trả thiết bị quá 30 phút chưa nhận được kết quả acknowledgement sẽ được chuyển sang `MANUAL_RECONCILIATION_REQUIRED` để đối soát thủ công qua runbook của Rental, không tự suy diễn thành công.
---

## 3. Envelope dùng chung (Version 1)

Mọi message (cả Event và Command) đều được bao gói bằng một định dạng Envelope chuẩn duy nhất (`KafkaMessageEnvelope<TPayload>`):

```json
{
  "messageId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "messageType": "RentalOrderCreated",
  "version": 1,
  "producer": "rental-service",
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
- **Transaction**: Nếu caller đã có transaction đang mở (`Database.CurrentTransaction != null`), Outbox lưu trong transaction đó và **không tự commit** transaction của caller. Nếu caller không có transaction, Outbox tự mở và commit transaction riêng.

### 4.2. Xuất bản tin (Outbox Publisher Worker)
- **Cấu hình Confluent**: `EnableIdempotence = true`, `Acks = Acks.All`, `MessageSendMaxRetries = 3`, `RetryBackoffMs = 100`.
- **Claim từng message**: Thay vì batch claim 50 message cùng lúc (dễ hết hạn lease), worker claim từng message ngay trước khi gửi (`ClaimNextPendingAsync` sử dụng `FOR UPDATE SKIP LOCKED` trên PostgreSQL), tối đa 50 message mỗi vòng.
- **Lease Duration**: 30 giây mặc định. Kiểm tra lại hạn lease ngay trước khi publish.
- **Delivery Timeout**: 10 giây mặc định qua `CancellationTokenSource`.
- **Acknowledgement**: Chỉ đánh dấu `Published` sau khi nhận được `PersistenceStatus.Persisted` từ broker.
- **Shutdown**: Flush có thời hạn (15s mặc định), không drop message đang gửi dở.

### 4.3. Tiêu thụ bản tin (Payment Events Consumer Worker)
- **Tắt auto commit**: `EnableAutoCommit = false`, `EnableAutoOffsetStore = false`.
- **Chỉ commit khi an toàn**: Offset chỉ được commit sau khi `RentalInboxMessage` hoặc DLQ đã được lưu bền vững vào PostgreSQL, hoặc khi xác nhận duplicate thành công.
- **Không skip lỗi**: Nếu xảy ra lỗi lưu trữ database (kết nối, deadlock), worker thử lại bản ghi đang giữ; tuyệt đối không đọc tiếp và không commit vượt qua bản ghi lỗi.
- **Deduplication Canonical JSON**: So sánh toàn bộ nội dung JSON sau khi canonical hóa (sắp xếp object properties theo bảng chữ cái Ordinal, giữ nguyên thứ tự phần tử array). Với Inbox cũ, deserialize và so sánh từ payload đã lưu.
- **DLQ an toàn**: Bản tin lỗi schema/conflict được đưa vào DLQ (`payment.events.v1.dlq`) với ID tất định từ `(topic, partition, offset)`. Lỗi ghi DLQ không bị swallow mà được trả về để retry.
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
    "PaymentCommandsTopic": "payment.commands.v1",
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
