# Tài liệu Kỹ thuật InventoryService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, Value Objects, policies, repository contracts, EF Core DbContext và repositories | HTTP pipeline, controller |
| `Application` | DTOs, Kafka transport, Security Access Evaluator và policies | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, JWT Bearer Authentication, JWKS manager, Authorization handlers và DI | Business logic trực tiếp |

Domain sở hữu entities, value objects, domain availability policy, repository contracts, EF Core DbContext và repository implementations. API đăng ký persistence qua `AddInventoryServiceDomainRepositories`.

---

## Domain Model và Thực thể (Target Domain)

Hệ thống quản lý 6 bảng thực thể trong schema `inventory`:

1. `InventoryAsset` (`inventory.assets`): Tài sản trong kho, thuộc sở hữu cá nhân (`OwnerUserId`), liên kết với loại thiết bị Catalog (`EquipmentTypeId`), quản lý phiên bản dynamic schema (`SchemaVersion`), thông số kỹ thuật (`SpecValues` lưu JSONB), và cờ nhận lịch (`AcceptingBookings`).
2. `AssetAllocation` (`inventory.asset_allocations`): Lượt phân bổ hoặc giữ chỗ lịch của tài sản, gồm nguồn (`SourceSystem`), mã tham chiếu (`ExternalReference`), khoảng thời gian (`RentalPeriod`), trạng thái (`AllocationStatus`: Held=1, Allocated=2, Cancelled=5, Released=6) và hạn giữ (`HoldExpiresAt`).
3. `AllocationTransition` (`inventory.allocation_transitions`): Lịch sử chuyển đổi trạng thái của lượt phân bổ (append-only audit log), có thứ tự `Sequence` và `OperationKey` chống trùng lặp.
4. `CalendarBlock` (`inventory.calendar_blocks`): Khoảng thời gian chặn lịch tài sản phục vụ bảo trì, kiểm định hoặc sử dụng nội bộ (tối đa chân trời 6 tháng).
5. `OutboxMessage` (`inventory.outbox_messages`): Lưu trữ thông điệp sự kiện theo mẫu Transactional Outbox Pattern với payload `jsonb` phục vụ xuất bản ra Kafka.
6. `InboxMessage` (`inventory.inbox_messages`): Lưu trữ thông điệp theo mẫu Transactional Inbox Pattern với khóa duy nhất `(handler, message_id)`.

---

## Date Semantics và Availability Policy

1. **Khoảng thời gian (`RentalPeriod`)**:
   - Hai đầu inclusive: số ngày = `EndDate.DayNumber - StartDate.DayNumber + 1`.
   - Bất biến: `StartDate <= EndDate`. Giới hạn 30 ngày và chân trời đặt lịch được Rental Service quản lý.
   - Trùng lặp (Overlap): `A.StartDate <= B.EndDate && A.EndDate >= B.StartDate`.
2. **Múi giờ nghiệp vụ**:
   - Múi giờ chuẩn: `Asia/Ho_Chi_Minh` (UTC+7, không có Daylight Saving Time).
   - Hold expiry: `Now < ExpiresAt < StartDate 00:00:00 ICT` (chuyển đổi sang UTC).
3. **Quy tắc chiếm lịch (`AssetAvailabilityPolicy`)**:
   - Bật công tắc `AcceptingBookings`: bắt buộc để nhận phân bổ mới.
   - Các khoảng chặn lịch `CalendarBlock`: chiếm lịch trong khoảng `[StartDate, EndDate]`.
   - `Held` (còn hạn) và `Allocated`: chiếm lịch trong khoảng `[StartDate, EndDate]`.
   - `Held` đã hết hạn (`Now >= HoldExpiresAt`), `Released`, `Cancelled`: **không chiếm lịch**.

---

## Concurrency Protocol

Hệ thống áp dụng cơ chế: **PostgreSQL `READ COMMITTED` transaction + asset-level `pg_advisory_xact_lock`**.

### Các bước thực thi chuẩn:
1. Mở transaction độc lập trên context riêng của operation ở mức cô lập `READ COMMITTED`.
2. Khóa độc quyền tài sản bằng số nguyên 64-bit bigint suy ra ổn định từ Asset ID (`AssetLockKey.FromAssetId`) thông qua `SELECT pg_advisory_xact_lock(@key)`. Không sử dụng `.NET GetHashCode()`.
3. Sau khi có khóa, đọc lại toàn bộ dữ liệu tài sản, allocations, blocks và clock; không sử dụng aggregate đã load trước khi có khóa.
4. Xử lý các hold đã quá hạn, kiểm tra idempotency, điều kiện nhận đặt lịch của tài sản và kiểm tra trùng lặp lịch qua `AssetAvailabilityPolicy`.
5. Stage các thay đổi cho allocation, transition và outbox message trong cùng một transaction.
6. Gọi `SaveChangesAsync` đúng một lần và commit transaction; mọi lỗi hoặc cancellation sẽ tự động rollback toàn bộ và giải phóng khóa advisory lock.

---

## Ma trận Phân quyền DPR-147

| Khả năng (Capability) | Human thường | Human Admin (`r=adm`) | Service Principal |
|---|---|---|---|
| Quản lý tài sản (`AssetManage`) | `p=inventory.asset.manage` + own asset (`OwnerUserId == sub`) | Cho phép xuyên user | Từ chối |
| Chi tiết lịch (`CalendarRead`) | `p=inventory.calendar.read` + own asset (`OwnerUserId == sub`) | Cho phép xuyên user | Từ chối |
| Lịch khả dụng làm sạch (`SanitizedAvailability`) | Bất kỳ Human đã xác thực | Cho phép | `s=inventory.calendar.read` |
| Lệnh phân bổ/hold (`AllocationWrite`) | Từ chối | Từ chối | `s=inventory.allocation.write` |
| Phân bổ thủ công (`ManualAllocation`) | `p=inventory.asset.manage` + own asset (`OwnerUserId == sub`) | Cho phép xuyên user | Từ chối |

---

## Kafka Topics

- Commands: `inventory.allocation.commands.v1`
- Events: `inventory.allocation.events.v1`, `inventory.asset.events.v1`
- Mỗi topic đều có DLQ tương ứng `${topic}.dlq`.

## Forward migration và maintenance cutover

`20261002024109_HardenInventoryBoundary` áp dụng sau migration boundary hiện có; không chỉnh sửa migration đã merge. DB được giữ nguyên: assets/allocations, ngày, trạng thái, external references, operation keys và Outbox/Inbox vẫn tồn tại. Migration thêm CHECK cho GUID khác rỗng, SchemaVersion >= 1 và SpecValues phải là JSON object. Dữ liệu không hợp lệ làm toàn bộ migration rollback, không được tự suy đoán mapping hoặc reset database.

**Data safety:** dừng Inventory writers/consumers, backup DB trước cutover. Dữ liệu domain dev cũ được phép bỏ: migration xóa toàn bộ Notes của allocations và Reason của transitions có trước cutover vì free text có thể chứa renter PII. Notes/reasons operational mới sau cutover vẫn được lưu. Fingerprint của Return đã được migration trước chuyển thành Release (reason `Hoàn tất kỳ phân bổ`) được xóa; operation key vẫn được giữ để reject reuse. Không bảo đảm replay request thương mại cũ; drain/quarantine topic backlog trước khi dùng command namespace allocation mới. Không rewrite Outbox/Inbox payload hoặc tự xóa broker topics; đối chiếu payload legacy trong backup và quyết định drain/quarantine trước khi bật publisher.

DB ở trạng thái inventory/develop: chạy `scripts/inventory-boundary-preflight.sql`, điền `asset_backfill` bằng owner đã đối chiếu Auth và EquipmentTypeId/SchemaVersion/SpecValues từ Catalog contract. SpecValues phải là JSON object; không chạy migration khi missing_or_invalid_count > 0. DB đã refactor: kiểm tra assets với GUID rỗng, schema_version < 1 hoặc jsonb_typeof(spec_values) <> 'object' và sửa bằng dữ liệu đã xác minh trước migration. Chạy `dotnet ef database update --project InventoryService/src/Domain/InventoryService.Domain.csproj` với ConnectionStrings__Postgres qua environment. Kiểm tra health ready, smoke security và chỉ khởi động consumer/publisher sau khi migration thành công. Down không phục hồi free text; rollback bằng backup và binary phù hợp.

Root Compose dùng `inventory-migrate` chạy một lần trước Inventory runtime; legacy populated DB vẫn cần preflight/backfill và maintenance procedure trên. Không chạy stack upgrade với writer phiên bản cũ đang hoạt động.

## JWT/JWKS behavior

JWT RS256 lấy keys trực tiếp từ Auth; timeout/cache/maximum usable age configurable. Cache age được tính từ lần fetch thành công, không được kéo dài bởi outage. Unknown kid được refresh bất đồng bộ trước validation của request hiện tại; forced refresh và outage retry được giới hạn 10 giây để coalesce concurrent requests. Token có kid vẫn không tồn tại bị 401; không thử ký bằng tất cả keys. Request cancellation được propagate. Health endpoints anonymous; endpoint không khai báo policy vẫn yêu cầu authentication theo fallback policy. Resource policies chỉ được gọi với resource đã load phía server; không lấy OwnerUserId từ request làm authorization resource. Tạo asset và validate organization metadata thuộc DPR-148.
