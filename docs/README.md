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

1. `InventoryAsset` (`inventory.assets`): Tài sản trong kho, thuộc sở hữu cá nhân (`OwnerUserId`), liên kết với loại thiết bị Catalog (`EquipmentTypeId`), tên model (`Model`), số serial (`SerialNumber`), năm sản xuất (`ManufactureYear`), ảnh đại diện (`ThumbnailMediaId`), phiên bản dynamic schema (`SchemaVersion`), thông số kỹ thuật (`SpecValues` lưu JSONB), và cờ nhận lịch (`AcceptingBookings`). Thuộc tính `OrganizationId` đã được loại bỏ hoàn toàn khỏi entity và database schema.
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
   - Bật công tắc `AcceptingBookings`: bắt buộc để nhận phân bổ/hold mới (kiểm tra trong `CheckAvailability`).
   - Khóa vận hành `CalendarBlock`: owner được tạo và xóa ngay cả khi `AcceptingBookings = false` (kiểm tra trùng lặp độc lập qua `CheckCalendarOverlap`).
   - Các khoảng chặn lịch `CalendarBlock`: chiếm lịch trong khoảng `[StartDate, EndDate]`.
   - `Held` (còn hạn: `HoldExpiresAt > now`) và `Allocated`: chiếm lịch trong khoảng `[StartDate, EndDate]`.
   - `Held` đã hết hạn (`Now >= HoldExpiresAt`), `Released`, `Cancelled`: **không chiếm lịch**.
---

## Concurrency Protocol

Hệ thống áp dụng cơ chế: **PostgreSQL `READ COMMITTED` transaction + asset-level `pg_advisory_xact_lock`**.

### Các bước thực thi chuẩn:
1. Mở transaction độc lập trên context riêng của operation ở mức cô lập `READ COMMITTED`.
2. Khóa độc quyền tài sản bằng số nguyên 64-bit bigint suy ra ổn định từ Asset ID (`AssetLockKey.FromAssetId`) thông qua `SELECT pg_advisory_xact_lock(@key)`. Không sử dụng `.NET GetHashCode()`.
3. Sau khi có khóa, đọc lại toàn bộ dữ liệu tài sản, allocations, blocks và clock; không sử dụng aggregate đã load trước khi có khóa.
4. Với Booking (Hold): kiểm tra `AcceptingBookings`, kiểm tra idempotency qua `ValidateExternalRetry` và kiểm tra trùng lặp lịch qua `AssetAvailabilityPolicy.CheckAvailability` (các hold đã hết hạn thụ động không chiếm lịch theo `OccupiesCalendar`). Với Allocate: kiểm tra replay operation key, không kiểm tra `AcceptingBookings` hay overlap.
5. Với Khóa vận hành (`CalendarBlock`): kiểm tra ownership của caller sau khóa, kiểm tra overlap qua `AssetAvailabilityPolicy.CheckCalendarOverlap` (không kiểm tra `AcceptingBookings`).
6. Stage các thay đổi cho entity và outbox message (`InventoryAssetAvailabilityChanged` cho CalendarBlock) trong cùng một transaction.
7. Gọi `SaveChangesAsync` đúng một lần và commit transaction; mọi lỗi hoặc cancellation sẽ tự động rollback toàn bộ và giải phóng khóa advisory lock.
---

## Ma trận Phân quyền DPR-147

| Khả năng (Capability) | Human thường | Human Admin (`r=adm`) | Service Principal |
|---|---|---|---|
| Quản lý tài sản (`AssetManage`) | `p=inventory.asset.manage` + own asset (`OwnerUserId == sub`) | Cho phép xuyên user | Từ chối |
| Chi tiết lịch (`CalendarRead` / DPR-149) | Human own asset (`OwnerUserId == sub`, không cần `p`) | Được phép khi là owner (`OwnerUserId == sub`), không bypass | Từ chối |
| Lịch khả dụng làm sạch (`SanitizedAvailability`) | Bất kỳ Human đã xác thực | Cho phép | `s=inventory.calendar.read` |
| Lệnh phân bổ/hold (`AllocationWrite`) | Từ chối | Từ chối | `s=inventory.allocation.write` |
| Phân bổ thủ công (`ManualAllocation`) | `p=inventory.asset.manage` + own asset (`OwnerUserId == sub`) | Cho phép xuyên user | Từ chối |

---

## Kafka Topics

- Commands: `inventory.allocation.commands.v1`
- Events: `inventory.allocation.events.v1`, `inventory.asset.events.v1`
- Mỗi topic đều có DLQ tương ứng `${topic}.dlq`.

---

## Quản lý tài sản (DPR-160: Inventory Asset Management MVP)

### Hợp đồng API

1. `POST /api/v1/inventory/assets`: Tạo mới tài sản (201 Created + Location header). Caller context bắt buộc authenticated human có permission `inventory.asset.manage` hoặc admin. Owner luôn lấy từ JWT `sub`. Chặn spoofed/unknown body fields (`ownerUserId`, `organizationId`).
2. `GET /api/v1/inventory/assets`: Danh sách phân trang (`page`, `pageSize`). Chỉ chấp nhận hai query parameters này; query khác trả 400. Luôn lọc theo JWT `sub` của caller (kể cả admin).
3. `GET /api/v1/inventory/assets/{id}`: Chi tiết tài sản. 404 nếu không tìm thấy, 403 nếu cross-user (Admin được bypass xem tài sản).
4. `PATCH /api/v1/inventory/assets/{id}`: Cập nhật thông tin với presence tracking. Omitted giữ nguyên; explicit null cho phép clear `serialNumber`, `manufactureYear`, `thumbnailMediaId`. Null model/schema/spec hoặc empty patch trả 400. Chặn sửa identity, owner, hoặc booking flag.
5. `PATCH /api/v1/inventory/assets/{id}/accepting-bookings`: Bật/tắt nhận lịch: `{ "acceptingBookings": bool, "reason": string? }`. Thuộc tính `acceptingBookings` bắt buộc ở JSON boundary. Bật lại tự động xóa reason. Chỉ khi đổi cờ mới phát sự kiện `InventoryAssetAvailabilityChanged` atomically qua Outbox.

### Tích hợp Catalog & Media

- **CatalogService**: Tra cứu Published equipment type qua typed client gọi `GET /api/v1/catalog/equipment-types/{id}`. Trạng thái Draft/Archived trả về 400; không tìm thấy trả về 400; lỗi mạng/timeout/ID mismatch/sai định dạng trả về 503. Canonical spec validation trả về rõ ràng `CatalogSpecValidationStatus.NotSupported` theo quyết định MVP và được chấp nhận ở application boundary sau khi đã kiểm tra cấu trúc JSON object hợp lệ.
- **MediaService**: Tái sử dụng `PreparePublicAsync` kiểm tra trạng thái Ready và uploader khớp JWT sub trước khi chuyển Public. Không gọi Media khi clear thumbnail (`thumbnailMediaId: null`).

### Kafka Event Key và Quy trình Cutover Rollout

- **Kafka Key**: Sự kiện `InventoryAssetAvailabilityChanged` phát lên `inventory.asset.events.v1` sử dụng **Aggregate ID** (`assetId`) làm partition key thay vì `{orgId}:{assetId}`.
- **Yêu cầu Cutover**:
  1. Dừng writers gửi request ghi tới InventoryService.
  2. Drain toàn bộ Outbox messages còn tồn đọng trong database.
  3. **Quan trọng**: Consumer phía downstream phải xử lý hết toàn bộ backlog sự kiện cũ trước khi hệ thống mới bắt đầu phát sự kiện với partition key mới (`assetId`), do Kafka không bảo đảm thứ tự giữa hai partition keys khác nhau.
  4. Chạy preflight script `scripts/complete-asset-management-preflight.sql` để tạo bảng staging `inventory.asset_model_backfill` và đối soát dữ liệu model (phải có model, năm sản xuất không vượt quá năm hiện tại tại Việt Nam).
  5. Thực thi migration `20261002030000_CompleteInventoryAssetManagement` (tự động rollback toàn bộ nếu backfill thiếu hoặc không hợp lệ).
  6. Thiết lập environment variable `CatalogService__BaseUrl` và khởi động binary mới. Rollback khẩn cấp thực hiện bằng cách restore database backup và binary cũ.

## Quản lý giữ chỗ và phân bổ tài sản (DPR-151: Asset Hold & Allocation Core)

### Hợp đồng API

1. `POST /api/v1/inventory/assets/{assetId}/holds`:
   - **Quyền**: Service Principal với scope `inventory.allocation.write`.
   - **Request**: `sourceSystem`, `externalReference`, `startDate`, `endDate`, `holdExpiresAt`, optional `notes`.
   - **Thành công**: `201 Created` khi tạo mới; `200 OK` khi idempotent replay đúng payload.
   - **Response**: `AllocationResponse` (chứa các trường tối thiểu: `id`, `assetId`, `sourceSystem`, `externalReference`, `startDate`, `endDate`, `status` là chuỗi `"Held"` hoặc `"Allocated"`, `holdExpiresAt`, `notes`, `createdAt`, `updatedAt`; không để lộ navigation, owner, `xmin` hoặc transitions).
2. `POST /api/v1/inventory/allocations/{allocationId}/allocate`:
   - **Quyền**: Service Principal với scope `inventory.allocation.write`.
   - **Request**: `operationKey` (bắt buộc).
   - **Thành công**: `200 OK`, trả về `AllocationResponse` hiện tại (`status: "Allocated"`).

### Quy trình giao dịch, đồng thời và tính lũy kế

- **Create Hold**:
  1. Validate cấu trúc request DTO (từ chối unknown properties qua `JsonUnmappedMemberHandling.Disallow`, kiểm tra trường bắt buộc, ngày mặc định, `startDate <= endDate`, chuẩn hóa `holdExpiresAt` về microsecond UTC bằng `BusinessDateTime.NormalizeUtc`).
  2. Mở transaction độc lập dưới PostgreSQL `pg_advisory_xact_lock` trên `assetId`.
  3. Đọc lại asset và lấy clock từ `TimeProvider` sau khi giành advisory lock (404 nếu asset không tồn tại).
  4. Tra cứu business key `(sourceSystem, externalReference)`. Nếu đã tồn tại: gọi `ValidateExternalRetry` đối chiếu asset, period, canonical expiry và notes. Replay đúng payload trả allocation hiện tại trước mọi điều kiện tạo mới (kể cả hold đã hết hạn, đã Allocated hoặc bookings đã tắt; status là `Held` hoặc `Allocated`), không phát thêm Outbox; khác payload trả 409 Conflict (`EXTERNAL_REFERENCE_REUSED`).
  5. Với key mới: validate bằng `CreateHold` với canonical expiry, tải candidate allocations và calendar blocks, gọi `CheckAvailability`. Nếu tài sản tắt bookings trả 400 (`ASSET_NOT_ACCEPTING_BOOKINGS`); nếu overlap trả 409 Conflict (`ASSET_BLOCKED` hoặc `ASSET_UNAVAILABLE`) với thông báo đã làm sạch tại application boundary (không tiết lộ chi tiết lịch hoặc metadata xung đột).
  6. Stage thực thể `AssetAllocation` cùng transition `CreateHold`, stage sự kiện `InventoryAssetAvailabilityChanged` vào Outbox (Kafka key `assetId`), commit atomically.
  7. Xử lý race condition: nếu vi phạm unique constraint do hai asset khác nhau dùng cùng business key, sau rollback thực hiện đúng một transaction đối chiếu lại dưới cùng asset lock. Row tìm được qua `ValidateExternalRetry`, khác payload trả 409; không tìm được giữ lỗi ban đầu, không retry insert.
- **Held → Allocated**:
  1. Sau authorization, đọc allocation để lấy riêng `AssetId` (404 nếu thiếu).
  2. Acquire advisory lock trên `AssetId` qua `ExecuteAsync`, đọc lại allocation cùng transitions trong transaction, lấy clock mới.
  3. Domain kiểm tra replay trước state/expiry: nếu `operationKey` đã được dùng thành công cho Allocate trước đó, trả về thành công mà không thêm transition.
  4. Lưu thay đổi bằng aggregate đang được EF tracking; không gọi `Repository.Update` (tránh đánh dấu modified cho transitions cũ).
  5. Không query overlap lại (Held còn hạn và Allocated chiếm cùng period).
  6. Không kiểm tra `AcceptingBookings`. Key mới trên expired/Allocated/Released/Cancelled trả 409 Conflict (Application service trực tiếp chuyển lỗi `HOLD_EXPIRED` và `INVALID_STATUS_TRANSITION` thành `Error.Conflict`, giữ nguyên mã lỗi; controller chuyển trực tiếp qua `ToProblem(result.Error)`). Không phát sự kiện Outbox.


## Forward migration và maintenance cutover

`20261002024109_HardenInventoryBoundary` áp dụng sau migration boundary hiện có; không chỉnh sửa migration đã merge. DB được giữ nguyên: assets/allocations, ngày, trạng thái, external references, operation keys và Outbox/Inbox vẫn tồn tại. Migration thêm CHECK cho GUID khác rỗng, SchemaVersion >= 1 và SpecValues phải là JSON object. Dữ liệu không hợp lệ làm toàn bộ migration rollback, không được tự suy đoán mapping hoặc reset database.

**Data safety:** dừng Inventory writers/consumers, backup DB trước cutover. Dữ liệu domain dev cũ được phép bỏ: migration xóa toàn bộ Notes của allocations và Reason của transitions có trước cutover vì free text có thể chứa renter PII. Notes/reasons operational mới sau cutover vẫn được lưu. Fingerprint của Return đã được migration trước chuyển thành Release (reason `Hoàn tất kỳ phân bổ`) được xóa; operation key vẫn được giữ để reject reuse. Không bảo đảm replay request thương mại cũ; drain/quarantine topic backlog trước khi dùng command namespace allocation mới. Không rewrite Outbox/Inbox payload hoặc tự xóa broker topics; đối chiếu payload legacy trong backup và quyết định drain/quarantine trước khi bật publisher.

DB ở trạng thái inventory/develop: chạy `scripts/inventory-boundary-preflight.sql`, điền `asset_backfill` bằng owner đã đối chiếu Auth và EquipmentTypeId/SchemaVersion/SpecValues từ Catalog contract. SpecValues phải là JSON object; không chạy migration khi missing_or_invalid_count > 0. DB đã refactor: kiểm tra assets với GUID rỗng, schema_version < 1 hoặc jsonb_typeof(spec_values) <> 'object' và sửa bằng dữ liệu đã xác minh trước migration. Chạy `dotnet ef database update --project InventoryService/src/Domain/InventoryService.Domain.csproj` với ConnectionStrings__Postgres qua environment. Kiểm tra health ready, smoke security và chỉ khởi động consumer/publisher sau khi migration thành công. Down không phục hồi free text; rollback bằng backup và binary phù hợp.

Root Compose dùng `inventory-migrate` chạy một lần trước Inventory runtime; legacy populated DB vẫn cần preflight/backfill và maintenance procedure trên. Không chạy stack upgrade với writer phiên bản cũ đang hoạt động.

## JWT/JWKS behavior

JWT RS256 lấy keys trực tiếp từ Auth; timeout/cache/maximum usable age configurable. Cache age được tính từ lần fetch thành công, không được kéo dài bởi outage. Unknown kid được refresh bất đồng bộ trước validation của request hiện tại; forced refresh và outage retry được giới hạn 10 giây để coalesce concurrent requests. Token có kid vẫn không tồn tại bị 401; không thử ký bằng tất cả keys. Request cancellation được propagate. Health endpoints anonymous; endpoint không khai báo policy vẫn yêu cầu authentication theo fallback policy. Resource policies chỉ được gọi với resource đã load phía server; không lấy OwnerUserId từ request làm authorization resource. Tạo asset và validate organization metadata thuộc DPR-148.
