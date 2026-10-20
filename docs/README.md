# Tài liệu Kỹ thuật InventoryService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, Value Objects, policies, repository contracts, EF Core persistence, Clients, Caching, Kafka | HTTP pipeline, controller, use cases |
| `Application` | Use case services, IServices, API DTOs, Mapping, Security Access Evaluator | EF Core, Npgsql, Confluent.Kafka, StackExchange.Redis SDK |
| `Api` | Controllers, JWT Bearer Authentication, JWKS manager, Authorization handlers, Routing và DI composition | Business logic trực tiếp |

Domain sở hữu entities, value objects, domain policies, repository contracts, EF Core DbContext/repositories, HTTP clients (Catalog/Media), Valkey cache adapter và Kafka infrastructure. API tham chiếu Domain để đăng ký DI composition root.

---

## Domain Model và Thực thể (Target Domain)

Hệ thống quản lý 7 bảng thực thể trong schema `inventory`:

1. `EquipmentListing` (`inventory.equipment_listings`): Danh mục niêm yết thiết bị trong kho, thuộc sở hữu cá nhân (`OwnerUserId`), liên kết với loại thiết bị Catalog (`EquipmentTypeId`) cùng thương hiệu chuẩn (`BrandId`), tên hiển thị (`Name`), mô tả (`Description`), tên model kỹ thuật (`Model`), năm sản xuất (`ManufactureYear`), danh sách media có thứ tự (`MediaIds` lưu `uuid[]`, tối đa 20 mục ảnh/video), ảnh đại diện (`ThumbnailMediaId` thuộc `MediaIds`), phiên bản dynamic schema (`SchemaVersion`), thông số kỹ thuật (`SpecValues` lưu JSONB), giá thuê niêm yết (`DailyRate`, `WeeklyRate`, `MonthlyRate`, `Currency = "VND"`), trạng thái (`Status`: Draft=1, Active=2, Inactive=3), `CreatedAt`, `UpdatedAt`, và concurrency token `xmin`.
2. `InventoryAsset` (`inventory.assets`): Đơn vị thiết bị vật lý thuộc một `EquipmentListing` (`EquipmentListingId`), định danh mã tài sản duy nhất (`AssetCode` = `AST-{AssetId:N}`), số serial (`SerialNumber`), năm sản xuất (`ManufactureYear` kế thừa từ Listing lúc tạo và có thể PATCH riêng), cờ nhận lịch (`AcceptingBookings`), lý do tạm dừng (`SuspensionReason`), `CreatedAt`, `UpdatedAt`, và `xmin`.
3. `AssetAllocation` (`inventory.asset_allocations`): Lượt phân bổ hoặc giữ chỗ lịch của đơn vị tài sản, gồm nguồn (`SourceSystem`), mã tham chiếu (`ExternalReference`), mã nhóm giữ chỗ (`GroupReference`), khoảng thời gian (`RentalPeriod`), trạng thái (`AllocationStatus`: Held=1, Allocated=2, Cancelled=5, Released=6) và hạn giữ (`HoldExpiresAt`).
4. `AllocationTransition` (`inventory.allocation_transitions`): Lịch sử chuyển đổi trạng thái của lượt phân bổ (append-only audit log), có thứ tự `Sequence` và `OperationKey` chống trùng lặp.
5. `CalendarBlock` (`inventory.calendar_blocks`): Khoảng thời gian chặn lịch đơn vị tài sản phục vụ bảo trì, kiểm định hoặc sử dụng nội bộ (tối đa chân trời 6 tháng).
6. `OutboxMessage` (`inventory.outbox_messages`): Lưu trữ thông điệp sự kiện theo mẫu Transactional Outbox Pattern với payload `jsonb` phục vụ xuất bản ra Kafka.
7. `InboxMessage` (`inventory.inbox_messages`): Lưu trữ thông điệp theo mẫu Transactional Inbox Pattern với khóa duy nhất `(handler, message_id)`.
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
| Lịch khả dụng làm sạch (`AvailabilityRead` / DPR-150) | Bất kỳ Human đã xác thực | Cho phép | `s=inventory.availability.read` (`u=svc`) |
| Lệnh phân bổ/hold (`AllocationWrite`) | Từ chối | Từ chối | `s=inventory.allocation.write` |

---

## Kafka Topics

- Commands: `inventory.allocation.commands.v1`
- Events: `inventory.allocation.events.v1`, `inventory.asset.events.v1`
- Mỗi topic đều có DLQ tương ứng `${topic}.dlq`.

---

## Quản lý danh mục và tài sản (EquipmentListing & InventoryAsset)

### Hợp đồng API Quản lý

1. `POST /api/v1/inventory/equipment-listings`: Tạo mới Listing ở trạng thái Draft kèm đúng 1 đơn vị vật lý ban đầu qua số serial (201 Created + Location header). Bắt buộc `serialNumber` (không rỗng, trim, max 100), từ chối trường `quantity`. Bắt buộc `name`, `brandId`, `equipmentTypeId`, `model`, `manufactureYear` (1900 đến năm hiện tại theo múi giờ Việt Nam), `schemaVersion`, `specValues`, `rentalRate`; tùy chọn `description`, `mediaIds` (danh sách GUID ảnh/video không rỗng, không trùng, tối đa 20 mục, giữ thứ tự) và `thumbnailMediaId` (phải thuộc `mediaIds` và là ảnh). Asset đầu tiên kế thừa `manufactureYear` của Listing. Chặn spoofed/unknown body fields (`ownerUserId`, `organizationId`).
2. `POST /api/v1/inventory/equipment-listings/{id}/assets`: Thêm một đơn vị vật lý mới bằng số serial (`serialNumber`). Đọc `manufactureYear` của Listing bên trong transaction có khóa Listing để gán cho asset mới; từ chối nếu Listing chưa có năm hợp lệ. Trả 201 Created + Location trỏ tới Asset (`/api/v1/inventory/assets/{assetId}`). Từ chối số serial trùng lặp trong cùng Listing với 409 Conflict (`SERIAL_ALREADY_EXISTS`).
3. `GET /api/v1/inventory/equipment-listings`: Danh sách Listing thuộc sở hữu người dùng phân trang (`page`, `pageSize`, max 100, `q` tìm chuỗi con trong `name` hoặc `model`, `equipmentTypeId`, `brandId`, `status`). Trả derived `totalQuantity` (thực hiện qua 2 SELECTs trong RepeatableRead snapshot: count và page projection quantity, không N+1).
4. `GET /api/v1/inventory/equipment-listings/{id}`: Chi tiết Listing (bao gồm `manufactureYear`, `media: [{id, url}]`, `thumbnailMediaId`, `thumbnailUrl`) và derived `totalQuantity` (số unit `AcceptingBookings=true`).
5. `PATCH /api/v1/inventory/equipment-listings/{id}`: Cập nhật thông tin chung và trạng thái với presence tracking cho `name`, `description`, `brandId`, `model`, `manufactureYear`, `mediaIds`, `thumbnailMediaId`, `schemaVersion`, `specValues`, `status`. Từ chối `name: null` hoặc rỗng, `manufactureYear: null`, `mediaIds: null`; `description: null` xóa mô tả; `mediaIds: []` xóa danh sách media (nếu xóa media đang làm thumbnail phải đồng thời xóa hoặc chọn lại thumbnail). Sửa `manufactureYear` của Listing chỉ ảnh hưởng các asset tạo tiếp theo. Chuyển sang `Active` bắt buộc phải có ít nhất 1 physical unit đang nhận lịch (`AcceptingBookings=true`). Từ chối `quantity`.
6. `PUT /api/v1/inventory/equipment-listings/{id}/rental-rate`: Cập nhật bảng giá niêm yết của Listing (thay thế toàn bộ). Yêu cầu Human Owner hoặc Admin.
7. `GET /api/v1/inventory/equipment-listings/{id}/assets`: Danh sách đơn vị vật lý thuộc Listing có phân trang.
8. `GET /api/v1/inventory/equipment-listings/{id}/calendar?from=&to=`: Lịch dùng chung tổng hợp cấp Listing (cửa sổ tối đa 6 tháng). Cho phép Owner và Admin.
9. `POST /api/v1/inventory/equipment-listings/{id}/holds`: Tạo giữ chỗ tạm thời theo nhóm (Group Hold) cấp phát N units nguyên tử.
10. `GET /api/v1/inventory/assets`: Danh sách đơn vị vật lý của current user (`OwnerUserId = JWT sub`). Hỗ trợ lọc theo `manufactureYearFrom/To`.
11. `GET /api/v1/inventory/assets/{id}`: Chi tiết đơn vị vật lý (`assetCode`, `serialNumber`, `manufactureYear`, `acceptingBookings`).
12. `PATCH /api/v1/inventory/assets/{id}`: Cập nhật thông tin vật lý (`serialNumber`, `manufactureYear`). Xử lý optimistic concurrency qua `xmin`/`Version`.
13. `PATCH /api/v1/inventory/assets/{id}/accepting-bookings`: Bật/tắt nhận lịch của đơn vị vật lý: `{ "acceptingBookings": bool, "reason": string? }`.
14. `GET /api/v1/inventory/assets/{id}/calendar`: Lịch sử dụng chi tiết của đơn vị vật lý. Nghiêm ngặt Owner-only (Admin bị từ chối 403).
15. `POST /api/v1/inventory/assets/{id}/calendar-blocks`: Tạo khoảng chặn lịch của đơn vị vật lý.
### Tích hợp Catalog & Media

- **CatalogService**:
  - **Tra cứu Equipment Type**: `GET /api/v1/catalog/equipment-types/{id}` kiểm tra trạng thái `Published`.
  - **Tra cứu Brand chuẩn**: `GET /api/v1/catalog/brands/{id}` trả `200` với body JSON chứa `id`, `code`, `name`, `slug`, `logoMediaId`, `logoUrl`, `isActive` (`bool`). Brand không tồn tại hoặc inactive trên public Catalog trả `404` với mã `Brand.NotFound`. Response thiếu/null/sai kiểu `isActive` trả `503 Service Unavailable`; `isActive: false` được Inventory từ chối bằng `400 Brand.Inactive`. Brand chỉ cần Active trên toàn Catalog; chưa thêm quan hệ Brand–EquipmentType.
  - **Đồng bộ contract Brand**: Implementation Brand hiện có trong Catalog local (chưa commit) sử dụng `bool isActive`. Trước cutover, phải xác nhận deployment Catalog được release có cùng contract và kiểm thử HTTP thực tế; mock/test server không thay thế bước này.
  - **Thẩm tra Specification**: `POST /api/v1/catalog/equipment-types/{id}/specifications/validate` với body `{ schemaVersion, specValues }` (`specValues` là JSON object).
    - **Xác thực & JWT Forwarding**: Chuyển tiếp JWT đã được Inventory xác thực (lấy từ authentication ticket qua `SaveToken = true` trong cấu hình JWT). Gắn header `Authorization: Bearer <token>` trên từng request validation. Không lưu token trong shared headers, không chuyển sang MediaService và giữ tắt auto-redirect. Thiếu token tại bước gọi validation trả lỗi dependency `503 Service Unavailable`; cancellation của caller tiếp tục được propagate; giữ timeout 10s, không thêm retry.
    - **Contract Response**:
      - Thành công chỉ khi Catalog trả HTTP `200`, `valid: true` và `errors: []`.
      - Validation thất bại: Catalog trả HTTP `200`, `valid: false` và `errors: [{ field, code, message }]`. Inventory chuyển thành HTTP `400` ProblemDetails với `title = SPEC_VALIDATION_FAILED`, detail mô tả validation thất bại, extension `errors` giữ nguyên danh sách `{ field, code, message }` và thứ tự Catalog trả.
      - Chấp nhận đủ 5 mã field chuẩn của Catalog: `REQUIRED`, `TYPE_MISMATCH`, `OUT_OF_RANGE`, `INVALID_ENUM_OPTION`, `UNKNOWN_FIELD`.
      - Catalog trả HTTP `404` với `EquipmentType.SchemaNotFound` hoặc `EquipmentType.NotFound`, và HTTP `400` với `Catalog.InvalidRequest` được chuyển thành Inventory HTTP `400 BadRequest`, giữ nguyên `title` và `detail`.
  - **Chính sách ánh xạ lỗi**:
    - Lỗi nghiệp vụ thuộc contract (`Brand.NotFound`, `Brand.Inactive`, `EquipmentType.SchemaNotFound`, `EquipmentType.NotFound`, `Catalog.InvalidRequest`, và `SPEC_VALIDATION_FAILED` kèm `errors`) được Inventory chuyển thành HTTP `400 BadRequest`.
    - Response thiếu trường, sai kiểu, mâu thuẫn (`valid: true` kèm `errors` không rỗng hoặc `valid: false` kèm `errors` rỗng), mã field lạ, lỗi mạng, timeout, response hỏng (malformed JSON/empty), upstream `401/403/5xx`, endpoint missing (404 ngoài contract) hoặc `204`/`501` đều được ánh xạ thành HTTP `503 Service Unavailable`.
    - Không giữ endpoint fallback; `204` hoặc `501` tuyệt đối không được coi là valid.
- **MediaService**: `MediaServiceClient.VerifyAsync` đọc thêm `ContentType` từ `GET /api/v1/media/{mediaId}/verify`. `InventoryMediaService.PreparePublicAsync` chuẩn bị danh sách `mediaIds` và `thumbnailMediaId` ngoài transaction: mọi media mới phải thuộc người upload (`caller.UserId`), ở trạng thái `Ready`, có `ContentType` là ảnh (`image/*`) hoặc video (`video/*`); `thumbnailMediaId` phải nằm trong `mediaIds` và có `ContentType` là ảnh (`image/*`). Khi chọn lại thumbnail từ media đã có trong listing, xác minh media đó ở trạng thái `Ready` và là ảnh. Lỗi media không ghi thay đổi vào database.

### Write Flows & Invariants

- **Create Asset**:
  `Auth/Permission` → `Local validation` (từ chối missing/null/empty GUID `brandId`, kiểm tra format spec) → `Published EquipmentType` → `Active Brand` → `Catalog spec validation` → `Media preparation` → `Write transaction` (persist DB).
  Validation thất bại dừng trước Media và ghi DB.
- **PATCH Asset**:
  `Auth/Ownership` → `Merge snapshot` → `Local domain validation` (từ chối null/empty `brandId`, ngăn xóa Brand đã có) → `Validate Brand nếu gửi` → `Validate schema/spec nếu gửi một trong hai` → `Media preparation nếu thumbnail đổi` → `Write transaction` (advisory lock, ownership recheck, xmin check).
  - Schema-only PATCH: validate với spec hiện tại của snapshot.
  - Spec-only PATCH: validate với version hiện tại của snapshot.
  - Model-only PATCH: không gọi downstream Catalog.
  - Asset cũ có `brand_id = null` giữ nguyên null khi PATCH không gửi `brandId`.
  - EquipmentTypeId bất biến.

### Giai đoạn Nullable, Migration & Rollout Runbook

- **Forward Migration (`20261005050000_AddAssetBrandId`)**:
  - Thêm cột `brand_id uuid NULL`, không default.
  - Thêm constraint `ck_assets_brand_id CHECK (brand_id IS NULL OR brand_id <> '00000000-0000-0000-0000-000000000000'::uuid)`.
  - `Down()` bị chặn bằng `NotSupportedException`; cấm rollback phá hủy schema Brand đã ghi.
  - Không tạo foreign key sang cơ sở dữ liệu của CatalogService.
  - Không thay đổi schema/spec, Calendar, Hold, Allocation hoặc Availability của Asset cũ.
- **Audit Query (Kiểm tra số Asset thiếu Brand)**:
  ```sql
  SELECT
      COUNT(*) AS total_assets,
      COUNT(brand_id) AS assets_with_brand,
      COUNT(CASE WHEN brand_id IS NULL THEN 1 END) AS missing_brand_count,
      COUNT(CASE WHEN brand_id = '00000000-0000-0000-0000-000000000000'::uuid THEN 1 END) AS invalid_empty_brand_count
  FROM inventory.assets;
  ```
- **Quy trình Cutover & Rollback**:
  1. Triển khai và kiểm thử Catalog contract trước (Brand GET với `bool isActive` và EquipmentType specification validation endpoint).
  2. Áp dụng migration `20261005050000_AddAssetBrandId` lên PostgreSQL.
  3. Drain toàn bộ Inventory writer cũ trước khi mở ghi bằng binary mới.
  4. **Quy tắc Rollback**: Tuyệt đối không phục hồi binary còn bypass `NotSupported`. Nếu chưa có binary tương thích để rollback, dừng ghi Asset trong lúc khắc phục sự cố; giữ nguyên cột `brand_id` và dữ liệu đã ghi.
  5. **Backfill**: Thực hiện qua API PATCH với đầy đủ authorization và Catalog validation. Inventory team sở hữu giai đoạn nullable.
  6. **Điều kiện kết thúc nullable**: `missing_brand_count = 0` (không còn Asset thiếu Brand) và không còn writer cũ. Migration `NOT NULL` thuộc đợt tiếp theo, phải kiểm thử từ chối dữ liệu chưa backfill và thành công sau backfill.
  7. Kiểm thử với Catalog thật đã release là điều kiện cutover tiên quyết; implementation local chưa commit và test server không được coi là bằng chứng Catalog production đã hỗ trợ contract.

- **Kafka Key**: Sự kiện `InventoryAssetAvailabilityChanged` phát lên `inventory.asset.events.v1` sử dụng **Aggregate ID** (`assetId`) làm partition key thay vì `{orgId}:{assetId}`.
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


## Truy vấn tính khả dụng và Valkey Read Cache cho toàn InventoryService

### APIs & Chính sách Cache

| Nhóm chức năng | Thiết kế & TTL | Mô tả |
|---|---|---|
| Public discovery và public detail | Cache response đã làm sạch, Dynamic TTL 15s | Cache kết quả tìm kiếm công khai và chi tiết listing công khai. Không chứa dữ liệu nhạy cảm (serial, asset ID, địa chỉ chi tiết, tọa độ kho). |
| Busy-ranges, calendar asset/listing | Cache kết quả đọc, TTL tối đa 15s | Giới hạn bởi min(15s, nearest active hold expiry, chuyển ngày VN hoặc chuyển sang overdue). |
| Availability/check asset batch và listing | Cache kết quả đọc, TTL tối đa 15s | Batch check cache theo từng asset và date window; chỉ query PostgreSQL cho các asset miss. |
| Owner listing/asset detail và danh sách units | Cache DTO đọc, Metadata TTL 120s | Resource authorization evaluator chạy trên mọi request; không cache quyết định cấp quyền. |
| Owner listing/asset search | Cache cả page, count và backend info, TTL 120s | Giữ nguyên header X-Search-Backend khi hit cache. |
| Public/owner statistics | Cache theo phạm vi và bộ lọc, TTL 120s | Thống kê số lượng listing theo loại và thương hiệu. |
| Warehouse của owner | Cache DTO đọc, TTL 120s | Thông tin kho hàng của chủ sở hữu. |
| Catalog phục vụ discovery | Cache category → type IDs và batch schemas, TTL 120s | Chỉ cache category khi đã tải và kiểm tra đủ mọi trang; batch schemas cache theo tập version chuẩn hóa. |
| Listing batch phục vụ Rental | **Không cache (Đọc PostgreSQL trực tiếp)** | Bắt buộc đọc PostgreSQL để revalidate giá, trạng thái và warehouse cho RentalOrder. |
| Hold, allocate, extend, release, handover, return | **Không cache (PostgreSQL transaction)** | Đọc và ghi trong transaction PostgreSQL có khóa advisory lock; không dùng cache để quyết định. |
| Catalog validation và Media khi ghi | **Không cache (Nguồn thật)** | Luôn gọi trực tiếp service nguồn; không cache quyền sở hữu, trạng thái Ready hay kết quả publish. |

### Kiến trúc Valkey Read Cache & Invalidation độc lập Kafka

1. **Cấu hình & Adapter**:
   - Pin `StackExchange.Redis 3.3.1` trong `Directory.Packages.props`. Adapter `ValkeyReadCache` đặt tại `Api/Caching`, thực thi interface `IInventoryReadCache` do Application sở hữu. Application và Domain không phụ thuộc Redis.
   - Tái sử dụng `ConnectionStrings:Redis`, cấu hình cache tắt mặc định (`InventoryCache:Enabled = false`), `DynamicTtlSeconds = 15`, `MetadataTtlSeconds = 120`, `CatalogTtlSeconds = 120`, `TimeoutMs = 100`.
   - `AbortOnConnectFail = false`: Mất kết nối Redis không chặn khởi động hay readiness của ứng dụng; tự động fallback đọc PostgreSQL.
   - Giới hạn payload 1 MiB (`MaxPayloadBytes = 1048576`). Payload vượt ngưỡng bỏ ghi cache và ghi log cảnh báo.
   - Chống thundering herd: Giới hạn tối đa 32 source fills đồng thời trong mỗi process qua SemaphoreSlim và in-flight task coalescing.
2. **Key Schema & Generation Tokens (read:v2)**:
   - Namespace: `inventory:<environment>:read:v2`.
   - Hash tag theo slot: Mọi key trong cùng scope dùng chung hash tag `{inventory:<env>:read:v2:scope:<scope>}`.
   - Generation key: `{inventory:<env>:read:v2:scope:<scope>}:gen`.
   - Data key: `{inventory:<env>:read:v2:scope:<scope>}:d:{queryType}:{inputHash}`.
   - Đổi generation token ngẫu nhiên khi invalidation ($O(1)$) với TTL dẫn xuất = `max(TTLs) + 60s` (mặc định 180s); không dùng KEYS, SCAN, FLUSHDB hay Sorted Sets.
   - Ghi có điều kiện qua Lua: Dùng `SET ... PXAT <unix-ms>` đối chiếu server time của Valkey, chỉ ghi nếu generation token hiện tại của scope vẫn khớp với token lúc đọc nguồn.
3. **Hàng đợi Invalidation bền vững (`inventory.cache_invalidation_jobs`)**:
   - Tách biệt hoàn toàn khỏi Kafka publishing.
   - Mutation nghiệp vụ stage invalidation job vào bảng `inventory.cache_invalidation_jobs` trong cùng transaction PostgreSQL.
   - Warehouse mutation stage fanout descriptor (`fanout_owner_user_id`), không đọc vòng lặp listings trong database transaction.
   - `CacheInvalidationWorker` claim tối đa 100 job bằng `FOR UPDATE SKIP LOCKED`, xử lý tối đa 4 job đồng thời với DI scope riêng và tối đa 32 Valkey invalidations đồng thời.
   - Fanout listings được keyset theo trang (100 IDs/trang), checkpoint cursor `fanout_after_listing_id` bằng lease token; chỉ Completed khi hoàn tất trang cuối.
   - Dọn dẹp định kỳ các job Completed quá 7 ngày theo lô 500 records.
   - Lỗi kết nối Valkey: Retry với exponential backoff 1s–60s, tối đa 10 lần. Sau 10 lần, job chuyển sang trạng thái `Failed` để vận hành kiểm tra và requeue.

### Runbook Vận hành & Phục hồi Invalidation Queue

```sql
-- 1. Xem danh sách các invalidation jobs bị Failed:
SELECT id, scopes, fanout_owner_user_id, fanout_after_listing_id, retry_count, last_error, created_at
FROM inventory.cache_invalidation_jobs
WHERE status = 'Failed'
ORDER BY created_at ASC;

-- 2. Requeue các job Failed về Pending để worker tiếp tục xử lý (giữ nguyên checkpoint fanout đã hoàn tất):
UPDATE inventory.cache_invalidation_jobs
SET status = 'Pending',
    retry_count = 0,
    next_attempt_at = NOW(),
    last_error = NULL
WHERE status = 'Failed';
```
---

## Ranh giới sở hữu giá thuê niêm yết (EquipmentListing Rental Rates)

1. **Ranh giới sở hữu giữa các dịch vụ**:
   - **Inventory Service sở hữu giá niêm yết hiện tại trên EquipmentListing**: Inventory quản lý và lưu trữ mức giá cho thuê niêm yết hiện hành (`DailyRate`, `WeeklyRate`, `MonthlyRate`, `Currency = "VND"`) trên từng `EquipmentListing`.
   - **Rental Service chịu trách nhiệm revalidate và lưu PriceSnapshot**: Khi khách hàng hoặc đối tác tạo đơn thuê (`RentalOrder`), Rental Service bắt buộc phải đọc/revalidate giá niêm yết từ Inventory Service và lưu bản chụp giá (`PriceSnapshot`) cố định gắn liền với vòng đời của đơn hàng.
   - **Tính bất biến của đơn hàng**: Việc thay đổi giá niêm yết trên Inventory Service tuyệt đối không làm thay đổi giá của các đơn hàng cũ đã tạo.

2. **Quy tắc phân quyền cập nhật giá**:
   - API `PUT /api/v1/inventory/equipment-listings/{id}/rental-rate` yêu cầu Human Owner (JWT sub khớp `listing.OwnerUserId`) hoặc Human Admin (`r=adm`), không cần permission `inventory.asset.manage`.
   - Service principal và người dùng khác bị từ chối với HTTP 403 Forbidden.
   - Anonymous bị từ chối với HTTP 401 Unauthorized.

3. **Ngoài phạm vi của Inventory Service**:
   - `RentalOrder`, `PriceSnapshot` implementation, payment/refund/deposit, fee, voucher, tax, delivery fee, dynamic pricing và price history.
4. **Runbook bảo trì và cutover migration AddAssetRentalRate**:
   - **Preflight script**: `scripts/add-asset-rental-rate-preflight.sql` tạo staging table `inventory.asset_rental_rate_backfill` sử dụng kiểu `numeric` không giới hạn precision để kiểm tra phần lẻ, range `[1, 999999999999999999]` và currency = `'VND'`.
   - **Yêu cầu đối với DB có asset**: Từng AssetId phải có mapping giá hợp lệ trước khi chạy migration; thiếu giá, sai giá hoặc AssetId không tồn tại làm migration thất bại atomically. DB rỗng không yêu cầu backfill.
   - **Quy trình cutover**:
     1. Dừng toàn bộ writer cũ của Inventory Service.
     2. Sao lưu (backup) database PostgreSQL.
     3. Chạy script tạo staging table `inventory.asset_rental_rate_backfill` (`scripts/add-asset-rental-rate-preflight.sql`), sau đó Inventory team nạp dữ liệu giá đã xác nhận vào staging.
     4. Chạy truy vấn kiểm tra mức độ sẵn sàng trong preflight script. Xác nhận `missing_count = 0`, `invalid_rate_count = 0` và `unmatched_asset_count = 0`. Thiếu giá xác nhận hoặc sai sót dữ liệu sẽ chặn cutover.
     5. Thực thi migration `AddAssetRentalRate` (tạo bảng, constraints, index, insert giá đã xác nhận, xóa staging table).
     6. Migration chặn `Down()` để bảo vệ dữ liệu giá. Khởi động binary Inventory Service mới.
## Forward migration và maintenance cutover

`20261002024109_HardenInventoryBoundary` áp dụng sau migration boundary hiện có; không chỉnh sửa migration đã merge. DB được giữ nguyên: assets/allocations, ngày, trạng thái, external references, operation keys và Outbox/Inbox vẫn tồn tại. Migration thêm CHECK cho GUID khác rỗng, SchemaVersion >= 1 và SpecValues phải là JSON object. Dữ liệu không hợp lệ làm toàn bộ migration rollback, không được tự suy đoán mapping hoặc reset database.

**Data safety:** dừng Inventory writers/consumers, backup DB trước cutover. Dữ liệu domain dev cũ được phép bỏ: migration xóa toàn bộ Notes của allocations và Reason của transitions có trước cutover vì free text có thể chứa renter PII. Notes/reasons operational mới sau cutover vẫn được lưu. Fingerprint của Return đã được migration trước chuyển thành Release (reason `Hoàn tất kỳ phân bổ`) được xóa; operation key vẫn được giữ để reject reuse. Không bảo đảm replay request thương mại cũ; drain/quarantine topic backlog trước khi dùng command namespace allocation mới. Không rewrite Outbox/Inbox payload hoặc tự xóa broker topics; đối chiếu payload legacy trong backup và quyết định drain/quarantine trước khi bật publisher.

DB ở trạng thái inventory/develop: chạy `scripts/inventory-boundary-preflight.sql`, điền `asset_backfill` bằng owner đã đối chiếu Auth và EquipmentTypeId/SchemaVersion/SpecValues từ Catalog contract. SpecValues phải là JSON object; không chạy migration khi missing_or_invalid_count > 0. DB đã refactor: kiểm tra assets với GUID rỗng, schema_version < 1 hoặc jsonb_typeof(spec_values) <> 'object' và sửa bằng dữ liệu đã xác minh trước migration. Chạy `dotnet ef database update --project InventoryService/src/Domain/InventoryService.Domain.csproj` với ConnectionStrings__Postgres qua environment. Kiểm tra health ready, smoke security và chỉ khởi động consumer/publisher sau khi migration thành công. Down không phục hồi free text; rollback bằng backup và binary phù hợp.

Root Compose dùng `inventory-migrate` chạy một lần trước Inventory runtime; legacy populated DB vẫn cần preflight/backfill và maintenance procedure trên. Không chạy stack upgrade với writer phiên bản cũ đang hoạt động.

## JWT/JWKS behavior

JWT RS256 lấy keys trực tiếp từ Auth; timeout/cache/maximum usable age configurable. Cache age được tính từ lần fetch thành công, không được kéo dài bởi outage. Unknown kid được refresh bất đồng bộ trước validation của request hiện tại; forced refresh và outage retry được giới hạn 10 giây để coalesce concurrent requests. Token có kid vẫn không tồn tại bị 401; không thử ký bằng tất cả keys. Request cancellation được propagate. Health endpoints anonymous; endpoint không khai báo policy vẫn yêu cầu authentication theo fallback policy. Resource policies chỉ được gọi với resource đã load phía server; không lấy OwnerUserId từ request làm authorization resource. Tạo asset và validate organization metadata thuộc DPR-148.

## Đọc và Khám phá danh mục công khai (DPR-166: Public Equipment Listing Discovery)

1. **Phân tách phạm vi (Scope Separation)**:
   - Tách biệt rõ ràng giữa luồng đọc Owner (`IEquipmentListingService`, `IInventoryAssetService`) và luồng công khai (`IPublicEquipmentListingService`, `EquipmentListingSearchCriteria`, `SearchPublicListingsAsync`).
   - Owner search: Bắt buộc `OwnerUserId`, tìm kiếm chuỗi con trong `Name` OR `Model` (không phân biệt hoa/thường, giữ phân biệt dấu tiếng Việt), hỗ trợ lọc `equipmentTypeId`, `brandId`, `status`, không có date availability filter.
   - Public search: Không có caller context hay owner predicate; bắt buộc Listing `status = Active` và `totalQuantity > 0`; tìm kiếm chuỗi con trong `Name` OR `Model` (không phân biệt hoa/thường, giữ phân biệt dấu tiếng Việt); hỗ trợ availability filter toàn khoảng thời gian (`from`, `to`, `requestedQuantity`) trước phân trang.
   - Bỏ lọc theo năm sản xuất công khai (`manufactureYear` thuộc từng unit vật lý, không thuộc Listing).

2. **Tái sử dụng bộ lọc dùng chung (Shared Filter Architecture)**:
   - `AssetSearchQueryValidator`: Chuẩn hóa và xác thực keyword, metadata (`equipmentTypeId`, `brandId`), `sort` và pagination bounds cho luồng Listing công khai.
   - `AssetSearchFilterResolver`: Xử lý phân giải dynamic filters JSONB, tra cứu batch schemas từ CatalogService một lần duy nhất, kiểm tra tính tương thích lịch sử.
   - `PublicAssetDataPolicy`: Quản lý danh sách các thuộc tính nội bộ bảo lưu (owner identity, serial number, suspension reason, scheduling/allocation details, concurrency, audit). Tự động loại bỏ khỏi `specValues` công khai và từ chối các dynamic filter nhắm vào thuộc tính bảo lưu trước khi gọi Catalog hay DB.

3. **Tính toán khả dụng tương đương Domain Policy (Availability SQL Parity)**:
   - Triển khai availability predicates trực tiếp dưới PostgreSQL bằng anti-joins:
     - Anti-join bảng `calendar_blocks` với điều kiện overlap: `start_date <= to AND end_date >= from`.
     - Anti-join bảng `asset_allocations` với điều kiện overlap và trạng thái: `status = Allocated` HOẶC `(status = Held AND hold_expires_at > evaluationNowUtc)`.
   - Tính toán số lượng physical units sẵn sàng nhận lịch (`accepting_bookings = true`) rảnh rỗi trong toàn bộ khoảng thời gian `[from, to]` và so sánh với `requestedQuantity`.
   - Áp dụng trước `ORDER BY`, `LIMIT` và `OFFSET` đảm bảo trang kết quả luôn đầy đủ và `totalCount` phản ánh chính xác số lượng Listing thực tế có thể đặt.

4. **Hiệu năng & Ngăn chặn N+1 (Performance & No-N+1 Invariants)**:
   - Public Detail: 4 truy vấn PostgreSQL (Listing + Units + Rates), 0 Catalog HTTP, 0 Media HTTP.
   - Public List không dynamic filter: 3 truy vấn PostgreSQL (Count + Page), 0 Catalog HTTP, 0 Media HTTP.
   - Public List có dynamic filter: 4 truy vấn PostgreSQL (Distinct schema versions + Count + Page), đúng 1 Catalog batch HTTP request, 0 Media HTTP.
   - Số lượng truy vấn hoàn toàn độc lập với kích thước trang (`pageSize` 1, 20 hay 100).
   - Thumbnail URL được format cục bộ thông qua `MediaContentUrlBuilder` và `PublicGatewayUrl`.

## Inventory search runtime and recovery

Search adapters, provisioning, backfill, bootstrap and the projection worker live in
`Application/Search`. Api only registers Application dependencies and maps the existing
HTTP responses. Routes, query parameters and JSON payloads are unchanged. PostgreSQL
owns authorization, filters, availability, exact counts and pagination. Keyword queries
use ordered candidate IDs only when the generation is ready; no keyword, unavailable
indexes, transient failures and truncation use the existing SQL query. Invalid credentials
or index settings return the existing unavailable error format with HTTP 503.

`ISearchProjectionStore` owns PostgreSQL access and short atomic state updates. Application
has no EF/Npgsql reference in its compiled assembly. The worker acquires the shared index
writer lock before claiming one job at a time, renews its lease and binds asynchronous
task IDs to the submitted revision. New desired revisions cannot acknowledge older task
results. Fan-out cursors belong to a revision and restart for later metadata updates.
Retries are bounded at ten attempts. A request whose acceptance cannot be determined
sets `SEARCH_SUBMISSION_UNKNOWN`; further index writes stop and search uses SQL.

Online backfill uses uniquely named staging indexes. Metadata mutations acquire a shared
transaction advisory barrier and attach the current run ID to their transactional Outbox
event. Replay reads all committed events, including unpublished events, and records
per-event receipts after successful index tasks. It does not use a timestamp or publication
cursor. The final mutation barrier has a five-second deadline; it retries catch-up before
cutover if that deadline expires. The writer lock remains held until the persisted swap
task reaches a terminal state. A restart recovers its UID from task history using the
unique staging names and never blindly submits a second swap. Interrupted builds before
swap are abandoned and rebuilt into fresh staging indexes.

Upgrade handling: drain/stop old Inventory replicas before applying
`20261014011834_HardenSearchProjectionRecovery`; old projectors do not participate in the
new mutation barrier. The forward migration preserves pending task IDs, invalidates the
old readiness flag, abandons pre-swap legacy runs and requires a fresh backfill. Bootstrap
is owned and cancelled by the host, retries transient failures and periodically detects
missing indexes. Search stays on PostgreSQL until the fresh swap succeeds.

For `SEARCH_SUBMISSION_UNKNOWN`, stop Inventory index writers, inspect Meilisearch task
history and wait for every possibly accepted live-index request to become terminal.
Do not clear the marker or retry the write until request acceptance has been reconciled.
An operator may then attach the confirmed task IDs to the correct submission revision;
if correspondence cannot be established, discard that job's submission only after proving
there are no outstanding live writes, invalidate readiness and rebuild from PostgreSQL.
Failed jobs and unresolved swap runs are retained for diagnostics. Old staging indexes
from abandoned builds require operator cleanup once their tasks are terminal; automatic
cleanup only removes the previous live indexes after a confirmed successful swap.

Tests require real PostgreSQL (default port 5433), Kafka (29092) and Meilisearch v1.55.0
(7700). The Meilisearch suite fails when its dependency is absent. HTTP repository fixtures
exclude indexing workers so seed data cannot race background snapshots; dedicated search
tests cover the real worker, backfill and Kafka pipeline.

The scale test defaults to 1,000 listings / 10,000 assets. To measure the larger dataset:

```bash
INVENTORY_SEARCH_SCALE_LISTINGS=100000 \
INVENTORY_SEARCH_BENCHMARK_REPORT=/tmp/inventory-search-benchmark.json \
dotnet test InventoryService.sln --filter FullyQualifiedName~Scale_Backfill
```

The report contains backfill elapsed time and measured p50/p95 of candidate search plus
PostgreSQL availability/count/pagination. These are local measurements, not a production
latency guarantee.

[Measured large-dataset report](inventory-search-benchmark.json): 100,000 listings,
1,000,000 assets; backfill 363 seconds; 30 selective candidate + SQL samples yielded
p50 7.95 ms and p95 9.89 ms on the local Docker test environment. Broad queries still
truncate at 5,000 candidates and require PostgreSQL fallback; this measurement does not
represent their latency or concurrent production traffic.
