# Tài liệu InventoryService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, Value Objects, repository contracts, EF Core DbContext và repositories | HTTP pipeline, controller |
| `Application` | DTOs, Services, Mapping và điều phối domain | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, middleware và DI/composition root | Business logic trực tiếp |

Theo quyết định của dự án, project Domain vẫn tham chiếu EF; đây là ngoại lệ đối với domain thuần framework. State machine và interfaces không dùng DbContext/DbSet/IQueryable.

Domain sở hữu entities, value objects, domain availability policy, repository contracts, EF Core DbContext và repository implementations. API đăng ký persistence qua `AddInventoryServiceDomainRepositories`.

---

## Domain Model và Thực thể (Phase 1 Baseline)

Hệ thống quản lý 9 bảng thực thể trong schema `inventory`:

1. `InventoryEquipment` (`inventory.equipments`): Thiết bị trong kho, liên kết với Catalog Equipment ID, thuộc một tổ chức, quản lý cờ `AcceptingBookings` và trạng thái `CatalogLifecycleStatus`.
2. `Reservation` (`inventory.reservations`): Lượt đặt lịch thuê thiết bị, gồm mã `ReservationNumber` (do sequence `ReservationNumberSequence` cấp, mã hiển thị `LD-NNNNNN`), khoảng thuê `RentalPeriod`, trạng thái theo State Machine, thông tin giao/thu hồi và liên kết khách thuê.
3. `ReservationTransition` (`inventory.reservation_transitions`): Lịch sử chuyển đổi trạng thái và dòng thời gian (append-only), có thứ tự `Sequence` và `OperationKey` chống trùng lặp.
4. `CalendarBlock` (`inventory.calendar_blocks`): Khoảng thời gian chặn lịch thiết bị phục vụ bảo trì, kiểm định hoặc sử dụng nội bộ (tối đa chân trời 6 tháng, không giới hạn 30 ngày).
5. `DamageReport` (`inventory.damage_reports`): Báo cáo sự cố hư hỏng từ khách thuê trong thời gian `InUse` (tối đa 5 ảnh, tối đa 5 báo cáo trên mỗi lượt đặt, bất biến sau khi gửi).
6. `Notification` (`inventory.notifications`): Thông báo vận hành gửi đến tổ chức, dùng chung trạng thái đã xem cho toàn tổ chức, tự hết hạn sau 90 ngày.
7. `OutboxMessage` (`inventory.outbox_messages`): Lưu trữ sự kiện theo mẫu Transactional Outbox Pattern với payload `jsonb` phục vụ xuất bản ra Message Broker.
8. `InboxMessage` (`inventory.inbox_messages`): Lưu trữ thông điệp theo mẫu Transactional Inbox Pattern với khóa duy nhất `(Consumer, MessageId)`.
9. `SagaInstance` (`inventory.saga_instances`): Quản lý trạng thái và bối cảnh JSONB của các phiên phối hợp saga phân tán.

---

## Date Semantics và Availability Policy

1. **Khoảng thuê (`RentalPeriod`)**:
   - Hai đầu inclusive: số ngày thuê = `EndDate.DayNumber - StartDate.DayNumber + 1`.
   - Giới hạn từ 1 đến 30 ngày (cho phép thuê 1 ngày khi `StartDate == EndDate`).
   - Trùng lặp (Overlap): `A.StartDate <= B.EndDate && A.EndDate >= B.StartDate`.
   - Chân trời đặt lịch: `StartDate >= Today` và `EndDate <= Today.AddMonths(6)`.
2. **Múi giờ nghiệp vụ**:
   - Múi giờ chuẩn: `Asia/Ho_Chi_Minh` (UTC+7, không có Daylight Saving Time).
   - Timestamp của Reservation được chuẩn hóa UTC với độ chính xác microsecond để retry nhất quán sau roundtrip PostgreSQL. Audit, hold expiry, bàn giao và thu hồi lưu `DateTimeOffset` UTC (PostgreSQL `timestamptz`). Ngày thuê lưu PostgreSQL `date`.
   - Hold expiry: `Now < ExpiresAt < StartDate 00:00:00 ICT` (chuyển đổi sang UTC).
3. **Quy tắc chiếm lịch và Availability**:
   - `Held` (còn hạn), `Confirmed`, `InUse` chiếm lịch trong khoảng `[StartDate, EndDate]`.
   - `Held` đã hết hạn (`Now >= HoldExpiresAt`): **không chiếm lịch**.
   - `Completed`: **Thu hồi ngày D cho đặt lại từ D+1**. D tính từ `ReturnedAt` theo UTC+7, không dùng ngày trả dự kiến. Ngày D vẫn giữ là ngày bận dù lượt đặt đã `Completed`. Khách mới chỉ được đặt từ ngày D+1 trở đi.
   - `Overdue`: Thiết bị có lượt thuê `InUse` mà `EndDate < Today` (quá hạn chưa trả) sẽ **từ chối** các lượt đặt mới bắt đầu vào hôm nay hoặc ngày mai (`StartDate <= Today + 1 ngày`).

---

## Concurrency Protocol

Hệ thống áp dụng cơ chế: **PostgreSQL `READ COMMITTED` transaction + equipment-level `pg_advisory_xact_lock`**.

### Các bước thực thi chuẩn:
1. Mở transaction độc lập trên context riêng của operation ở mức cô lập `READ COMMITTED`.
2. Khóa độc quyền thiết bị bằng số nguyên 64-bit bigint suy ra ổn định từ Equipment ID (`EquipmentLockKey.FromEquipmentId`) thông qua `SELECT pg_advisory_xact_lock(@key)`. Không sử dụng `.NET GetHashCode()`.
3. Sau khi có khóa, đọc lại toàn bộ dữ liệu thiết bị, reservations, blocks và clock; không sử dụng aggregate đã load trước khi có khóa.
4. Xử lý các hold đã quá hạn, kiểm tra idempotency, điều kiện nhận đặt lịch của thiết bị, tình trạng quá hạn (overdue) và kiểm tra trùng lặp lịch qua `EquipmentAvailabilityPolicy`.
5. Stage các thay đổi cho reservation, transition và outbox message trong cùng một transaction.
6. Gọi `SaveChangesAsync` đúng một lần và commit transaction; mọi lỗi hoặc cancellation sẽ tự động rollback toàn bộ và giải phóng khóa advisory lock.

### Giới hạn kỹ thuật quan trọng của Concurrency Protocol:
> **Lưu ý:** PostgreSQL advisory locks là khóa mức logic ở tầng ứng dụng và **không bắt buộc các truy vấn SQL trực tiếp từ bên ngoài tuân thủ**. Mọi tiến trình ghi dữ liệu lịch của InventoryService **bắt buộc phải đi qua giao thức `IInventoryWriteTransaction`**. Baseline Phase 1 không sử dụng exclusion constraint (bTree GiST range constraint) theo lựa chọn baseline; bảo vệ này yêu cầu mọi writer cùng tuân thủ protocol.

### Contract ghi và retry
- `IInventoryWriteContext.EquipmentId` xác định thiết bị đã khóa. Boundary từ chối stage changes thuộc thiết bị khác và từ chối lồng executor trong cùng operation.
- `CreateHold` chỉ tạo nguồn External, bắt buộc source system, external reference và renter account; các chuỗi nguồn/reference được trim, phân biệt hoa/thường.
- Lookup cùng nguồn/reference phải gọi `ValidateExternalRetry` trước khi trả lượt cũ, kể cả terminal; payload khác trả conflict. So sánh với hạn giữ ban đầu trong transition, không với hạn đã gia hạn.
- Operation key chỉ được replay với cùng loại thao tác, actor và payload đã chuẩn hóa; thời điểm thử lại không nằm trong fingerprint. Đổi ngày giao/thu hồi, hạn giữ, lý do hoặc ảnh trả conflict, không phát thêm transition/outbox.
- Transition cũ chưa có fingerprint không được tự suy đoán thành retry thành công. Writer dùng key mới hoặc kiểm tra trạng thái qua query.
- Lookup aggregate để mutation và query expired holds load transitions; các query candidates/list phục vụ đọc lịch.
- Ảnh và transitions là read-only collections; EF lưu backing fields. Transaction boundary từ chối sửa/xóa transitions và sửa nội dung/xóa báo hỏng.
- `Return` yêu cầu `EquipmentReturnCondition` (`Intact=1`, `Damaged=2`); Damaged cần mô tả và ít nhất một ảnh. Thời điểm thu hồi phải sau giao và không sau now. Giao không sớm hơn 00:00 ICT một ngày trước StartDate, không sau now.
- Công tắc của nhà cung cấp độc lập Catalog lifecycle. Bật công tắc luôn được phép, nhưng chỉ thiết bị Catalog Active (projection của Published) mới nhận lượt mới. Workflow tắt công tắc/chặn lịch phải kiểm tra lượt hiệu lực sau khi có khóa.
- SQL/exception details không đi vào Result công khai; unique, constraint và stale-write có error codes riêng. Không tự retry transaction.

### Phát hiện Stale Updates:
- Sử dụng cột hệ thống `xmin` của PostgreSQL qua thuộc tính `[Timestamp] uint Version` trên các thực thể mutable (`InventoryEquipment`, `Reservation`, `CalendarBlock`, `Notification`, `SagaInstance`) để phát hiện cập nhật đồng thời trái phép (`DbUpdateConcurrencyException`).

---

## Cấu hình môi trường và Chạy API cục bộ

1. Khởi động PostgreSQL qua Docker Compose:

   ```bash
   cp .env.example .env
   # Đặt POSTGRES_PASSWORD riêng trong .env
   docker compose up -d
   ```

2. Chạy API cục bộ:

   ```bash
   set -a
   . ./.env
   set +a
   export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
   dotnet run --project InventoryService/src/Api/InventoryService.Api.csproj
   ```

## Build và Test

```bash
dotnet build InventoryService.sln
dotnet test InventoryService.sln
```

## Migration sửa contract và kiểm chứng

Giữ nguyên `InitialInventoryBaseline`; `HardenInventoryContracts` sửa schema đã có. Migration đổi tên ghi chú thu hồi thành `return_condition_notes`, thêm tình trạng typed, fingerprint và constraints. Không cast ghi chú tự do thành tình trạng và không cắt ngắn dữ liệu quá giới hạn SRS.

Nếu baseline cũ có Completed reservations, owner module Inventory phải xác nhận tình trạng từ bằng chứng giao nhận và cung cấp bảng staging trong cùng database trước khi migrate:

```sql
CREATE TABLE inventory.return_condition_backfill (
  reservation_id uuid PRIMARY KEY,
  condition integer NOT NULL CHECK (condition IN (1, 2))
);
-- INSERT từng reservation với condition đã được xác nhận: 1=Intact, 2=Damaged.
```

Bảng này chỉ phục vụ migration: chủ sở hữu là Inventory migration, lý do là schema cũ không lưu tình trạng typed, điều kiện xóa là tất cả constraints đã áp dụng thành công. Migration tự xóa bảng sau khi dùng. Thiếu backfill/bằng chứng hoặc dữ liệu không hợp lệ làm migration rollback; staging và dữ liệu gốc được giữ để kiểm tra. Các ghi chú vượt giới hạn phải được xử lý có xác nhận trước khi migration chạy. Không downgrade production tùy tiện: schema cũ không biểu đạt tình trạng typed/fingerprint, downgrade có thể mất thông tin mới; ưu tiên migration sửa tiếp.

EF design-time factory đọc `ConnectionStrings__Postgres`; khi chỉ sinh metadata, fallback dùng host `.invalid` không thể nhầm sang database thật. Muốn apply migration phải cấu hình connection string. Không tự migrate khi API khởi động.

Integration tests dùng PostgreSQL 17 và tạo database `inventory_tests_<uuid>` riêng, migrate và drop database đó sau test. Connection string cấu hình chỉ dùng để xác định server/credentials; database ứng dụng không bị migrate/clear. Test account cần quyền CREATE DATABASE. Tests kiểm tra schema 9 bảng, timestamp roundtrip, expiry history, early/late return, giới hạn báo hỏng theo reservation, payload retry, khóa sai equipment, nested transaction, migration backfill/removal và cạnh tranh thực tế quan sát qua `pg_locks`.

---

## Kafka Foundation, Outbox/Inbox và Saga Runtime

### 1. Kiến trúc Kafka Transport và Contracts

- **Thư viện**: Sử dụng `Confluent.Kafka` 2.15.1, pin dependency tập trung trong `Directory.Packages.props`. Chỉ các transport worker tham chiếu thư viện Confluent; public contracts hoàn toàn độc lập với Kafka client.
- **MessageEnvelope<T>**:
  - Chuẩn hóa: `MessageId` (Guid), `MessageType` (string), `Version` (positive int), `CorrelationId` (string), `CausationId` (Guid nullable), `AggregateId` (string), `OrganizationId` (Guid), `OccurredAt` (DateTimeOffset UTC), `Producer` (string), `Payload` (T).
  - Serialization: UTF-8 camelCase, format timestamp ISO 8601 UTC.
  - Factory: `CreateRoot` tạo message gốc (sinh MessageId mới, CausationId = null); `CreateContinuation` tạo message tiếp nối (sinh MessageId mới, giữ nguyên CorrelationId, CausationId trỏ tới MessageId nguồn); Retry giữ nguyên toàn bộ envelope và ID.
  - Kafka key kết hợp: `{OrganizationId:D}:{AggregateId}` đảm bảo phân vùng ổn định theo tenant và aggregate.
- **Baseline Topics**:
  - `catalog.equipment.events.v1`
  - `inventory.reservation.commands.v1`
  - `inventory.reservation.events.v1`
  - `inventory.equipment.events.v1`
  - `inventory.damage.events.v1`
  - Mỗi topic đều có Dead Letter Queue (DLQ) dạng `${topic}.dlq`.
- **Topic Provisioning & Outage Resilience**:
  - Development: Tự động provision các topic baseline và DLQ qua `KafkaTopicProvisioner` (mặc định 3 partitions, replication factor 1) bằng cơ chế idempotent.
  - Production: Chỉ kiểm tra danh sách topics được provision bởi deployment script, không tự ý tạo topic.
  - Broker outage: Khi Kafka broker không khả dụng lúc khởi động, `KafkaTopicStartupTask` ghi log cảnh báo và không ném lỗi làm sập API. API vẫn tiếp tục nhận và lưu các transaction PostgreSQL bình thường.

### 2. Transactional Outbox và Thao tác Phục hồi Thủ công (Requeue)

- **Lưu trữ**: Bảng `inventory.outbox_messages` lưu trực tiếp wire JSON, topic, key và headers. Không tái tạo envelope khi xuất bản.
- **Publisher Worker**: `OutboxPublisherWorker` claim thông điệp bằng transaction ngắn với `FOR UPDATE SKIP LOCKED`, xuất bản ra Kafka ngoài database transaction, và cập nhật `published_at` sau khi nhận được Broker ACK cùng `lock_lease_token` để ngăn worker mất lease ghi đè.
- **Strict Key Ordering**: Cơ chế claim sử dụng điều kiện `NOT EXISTS` kiểm tra không có bản ghi unpublished nào trước đó có cùng `kafka_key`, đảm bảo thứ tự phát hành tuyệt đối cho từng partition key.
- **Retry Budget & Backoff**: Tối đa 10 lần retry với exponential backoff từ 1 giây, cap 60 giây, jitter 20%.
- **Manual Requeue (Phục hồi thông điệp cạn budget)**:
  Khi thông điệp Outbox đã thử đủ 10 lần mà vẫn thất bại, bản ghi được giữ lại trong bảng `inventory.outbox_messages` (với `retry_count >= 10`, `last_error` được lưu lại để chẩn đoán) và không bị xóa hoặc đánh dấu published.
  Để phục hồi và đưa thông điệp trở lại hàng đợi xuất bản, kỹ sư vận hành thực thi câu lệnh SQL sau trên cơ sở dữ liệu `inventoryservice`:

  ```sql
  -- Requeue một Outbox message cụ thể theo ID:
  UPDATE inventory.outbox_messages
  SET retry_count = 0,
      next_attempt_at = NOW(),
      last_error = NULL,
      lock_lease_token = NULL,
      lock_lease_expires_at = NULL
  WHERE id = '<outbox_message_uuid>';

  -- Hoặc requeue toàn bộ các outbox messages bị lỗi thuộc một topic cụ thể:
  UPDATE inventory.outbox_messages
  SET retry_count = 0,
      next_attempt_at = NOW(),
      last_error = NULL,
      lock_lease_token = NULL,
      lock_lease_expires_at = NULL
  WHERE published_at IS NULL AND retry_count >= 10 AND topic = 'inventory.reservation.events.v1';
  ```

### 3. Transactional Inbox và Execution Boundary

- **Lưu trữ**: Bảng `inventory.inbox_messages` đổi tên cột `consumer` thành `handler`, duy trì unique constraint `(handler, message_id)`.
- **Execution Boundary**: `IInboxStore.ExecuteInTransactionAsync` quản lý vòng đời xử lý thông điệp:
  1. Khóa receipt bản ghi Inbox bằng `FOR UPDATE`.
  2. Bỏ qua an toàn nếu thông điệp đã `Processed` trước đó (Idempotent replay).
  3. Khởi tạo transaction trên scoped DbContext.
  4. Thực thi logic nghiệp vụ của handler. `IInventoryWriteTransaction` tham gia transaction này trên cùng scoped DbContext qua Ambient Context, giữ nguyên cơ chế equipment advisory lock và write scope validation; chỉ Inbox execution boundary mới commit/save.
  5. Nếu thành công: Ghi `processed_at`, commit atomically toàn bộ thay đổi nghiệp vụ, Outbox messages và Saga transitions.
  6. Nếu thất bại: Rollback transaction, xóa ChangeTracker; ghi nhận failure và lịch retry theo exponential backoff (tối đa 5 lần). Khi cạn budget retry (5 lần), tự động chuyển sang `DeadLettered` và stage bản ghi DLQ Outbox atomically trước khi commit offset Kafka.

### 4. Saga Runtime và Quản lý Timeout

- **Thực thể**: Bảng `inventory.saga_instances` có identity duy nhất là `(saga_type, organization_id, correlation_id)`. Quản lý `current_step`, `state` (enum `SagaState`), metadata `timeout_id`, `timeout_deadline`, `timeout_retry_count` và token `xmin` phát hiện cập nhật đồng thời trái phép.
- **Saga Orchestrator**: `ISagaOrchestrator` khóa saga bằng `FOR UPDATE`, thực thi transition, stage outgoing Outbox messages và commit cùng transaction với Inbox.
- **Timeout Worker**: `SagaTimeoutWorker` định kỳ quét các timeout quá hạn bằng `FOR UPDATE SKIP LOCKED`. Thực thi callback timeout, cập nhật saga và Outbox atomically. Hai worker cạnh tranh không bao giờ chạy trùng một timeout; restart hệ thống bảo toàn lịch timeout đã lưu bền vững trong PostgreSQL. Saga ở trạng thái terminal (`Completed`, `Failed`) tự động hủy timeout metadata.
