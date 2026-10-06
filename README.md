# InventoryService

Dịch vụ InventoryService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
InventoryService/
  src/
    Common/          # Kiểu dùng chung, Result/Error, AllocationStatus và utility
    Domain/          # Entities (Asset, Allocation, Block), repository contracts và EF Core persistence
    Application/     # DTOs, Kafka transport, Security evaluators & policies
    Api/             # Controllers, Filters, JWT Authentication, Authorization handlers, HTTP composition root
  tests/             # Unit, Integration, Migration & Security Tests
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ, Git hooks và migration preflight
```

## Ranh giới dữ liệu và Bảo mật (DPR-147)

**Inventory bảo vệ dữ liệu theo `OwnerUserId`, không kiểm tra permission theo organization.**

- **Human thông thường**: Bắt buộc có permission `inventory.asset.manage` trong claim `p` và quyền sở hữu tài sản (`asset.OwnerUserId == JWT.sub`).
- **Human Admin (`r=adm`)**: Được phép đọc/sửa tài sản xuyên người dùng trong quản lý tài sản (`AssetManage`).
- **Danh sách tài sản (`GET /api/v1/inventory/assets`)**: Luôn chỉ trả tài sản thuộc sở hữu của người dùng hiện tại (`JWT.sub`), kể cả khi caller là Admin.
- **Chi tiết lịch (`CalendarRead` / DPR-149)**: Giữ nghiêm ngặt owner-only (`asset.OwnerUserId == JWT.sub`), Admin không được bypass.
- **Service Principal**: Bắt buộc `u=svc`, `aud=inventory-api` và scope tương ứng trong claim `s` (`inventory.allocation.write`, `inventory.availability.read`). Bị từ chối trên các API quản lý tài sản của cá nhân.
- **Loại bỏ OrganizationId**: Tài sản không còn thuộc tính `organization_id`. Danh tính người sở hữu duy nhất được lấy từ JWT `sub`.

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
# Đặt POSTGRES_PASSWORD riêng trong .env; đổi POSTGRES_PORT nếu cổng đã được dùng.
docker compose up -d
```

PostgreSQL lắng nghe ở cổng cấu hình trong `.env` (mặc định 5433). File `.env` không được commit.

## Chạy API cục bộ

Nạp thông tin PostgreSQL từ `.env` vào môi trường của tiến trình API:

```bash
set -a
. ./.env
set +a
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
dotnet run --project InventoryService/src/Api/InventoryService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build InventoryService.sln
dotnet test InventoryService.sln
```

## Quản lý tài sản (Inventory Asset Management - DPR-160)

### APIs

| Method | Endpoint | Mô tả |
|---|---|---|
| `POST` | `/api/v1/inventory/assets` | Tạo mới tài sản (201 Created + Location header). Bắt buộc `brandId`, `equipmentTypeId`, `model`, `schemaVersion`, `specValues`. Owner lấy từ JWT sub. |
| `GET` | `/api/v1/inventory/assets` | Danh sách tài sản phân trang của current user (`page` default 1, `pageSize` default 20, max 100). Hỗ trợ tìm kiếm `q`, metadata filters (`equipmentTypeId`, `brandId`, `acceptingBookings`, `manufactureYearFrom`, `manufactureYearTo`), `sort` whitelist, và dynamic filters qua `filters`. Owner luôn cố định `JWT.sub`. |
| `GET` | `/api/v1/inventory/assets/{id}` | Chi tiết tài sản (200 OK). 404 nếu không tìm thấy, 403 nếu cross-user (Admin bypass allowed). Trả `brandId` (nullable). |
| `PATCH` | `/api/v1/inventory/assets/{id}` | Cập nhật thông tin chi tiết với presence tracking cho `brandId`, `model`, `serialNumber`, `manufactureYear`, `thumbnailMediaId`, `schemaVersion`, `specValues`. Omitted giữ nguyên; explicit null cho serial/year/thumbnail; `brandId` từ chối null; domain ngăn xóa Brand đã có. |
| `PATCH` | `/api/v1/inventory/assets/{id}/accepting-bookings` | Bật/tắt nhận lịch: `{ "acceptingBookings": bool, "reason": string? }`. Đổi cờ phát sự kiện availability qua Outbox. |

### Tìm kiếm và lọc nâng cao My Assets (`GET /api/v1/inventory/assets`)

- **Phạm vi bảo vệ và Ownership**:
  - Luôn luôn giới hạn `OwnerUserId = JWT sub`, kể cả caller là Admin.
  - Chặn triệt để query parameter ngoài whitelist, bao gồm `ownerUserId` (trả `400 INVALID_QUERY_PARAMETER`).
  - Từ chối tham số lặp lại (duplicate query keys) để tránh binding mơ hồ.
  - Whitelist query parameters: `q`, `equipmentTypeId`, `brandId`, `acceptingBookings`, `manufactureYearFrom`, `manufactureYearTo`, `sort`, `filters`, `page`, `pageSize`.
- **Tìm kiếm từ khóa (`q`)**:
  - Tự động `trim()`, chuỗi trắng tương đương không search, tối đa 200 ký tự.
  - Substring matching không phân biệt hoa/thường trên `Model OR SerialNumber`.
  - Ký tự đặc biệt `%`, `_`, `\` được escape và xử lý như ký tự literal.
- **Bộ lọc Metadata**:
  - Nối điều kiện dạng `AND`.
  - `equipmentTypeId` và `brandId`: UUID hợp lệ, không được rỗng (`Guid.Empty`).
  - `manufactureYearFrom` và `manufactureYearTo`: Trong khoảng từ 1900 đến năm hiện tại theo múi giờ Việt Nam (`Asia/Ho_Chi_Minh`), `From <= To`. Phạm vi inclusive; tài sản có `manufacture_year` null sẽ không match khi có year filter.
- **Sắp xếp (`sort`)**:
  - Whitelist: `created_desc` (mặc định), `created_asc`, `model_desc`, `model_asc`, `manufacture_year_desc`, `manufacture_year_asc`.
  - Mọi sort khác thêm `Id` cùng chiều làm tie-breaker.
  - Sắp xếp theo năm sản xuất (`manufacture_year`) luôn đưa các bản ghi null về cuối cùng (`NULLS LAST`).
- **Bộ lọc thuộc tính động (`filters`)**:
  - Truyền qua query parameter `filters` dưới dạng URL-encoded JSON array (tối đa 20 điều kiện, nối `AND`):
    ```json
    [
      {"field":"capacityKg","operator":"gte","value":2500},
      {"field":"fuelType","operator":"eq","value":"diesel"}
    ]
    ```
  - `filters=[]` tương đương không áp dụng dynamic filter.
  - Bắt buộc phải có `equipmentTypeId` khi `filters` có điều kiện.
  - Whitelist operators:
    - `number`: `eq`, `gt`, `gte`, `lt`, `lte`. Value phải là JSON number.
    - `text`: `eq`. Value phải là JSON string.
    - `enum`: `eq`. Value phải là JSON string và là option hợp lệ trong schema.
    - `boolean`: `eq`. Value phải là JSON boolean (`true`/`false`).
  - Field key và giá trị text/enum so sánh chính xác, phân biệt hoa/thường.
  - JSON không hợp lệ, null, sai cấu trúc hoặc có thuộc tính ngoài `field/operator/value` trả về `400`.
- **Tương thích Schema và Tích hợp Catalog**:
  - Yêu cầu không có dynamic filter sẽ **không gọi CatalogService**.
  - Khi có dynamic filter: PostgreSQL truy vấn distinct `schema_version` của các tài sản thỏa mãn metadata filter trước pagination (không giữ transaction DB khi gọi HTTP).
  - Inventory gọi Catalog batch schemas: `POST /api/v1/catalog/equipment-types/{id}/schemas/batch` với `{ "versions": [...] }`.
  - Current schema quyết định field được phép filter; field thiếu hoặc `filterable=false` ở current trả Inventory `400`.
  - Historical version chỉ match khi mọi field có `filterable=true`, cùng data type và unit với current; enum phải chứa option đang filter. Version không tương thích bị loại.
  - Lỗi nghiệp vụ từ Catalog (404/400) được map sang `400`; timeout, lỗi mạng hoặc response hỏng/thiếu schema map sang `503 Service Unavailable`.
  - Toàn bộ metadata và JSONB predicates thực hiện dưới PostgreSQL trước `ORDER BY` và `LIMIT/OFFSET`.
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
- **MediaService**: Tái sử dụng `PreparePublicAsync` khi tạo hoặc cập nhật thumbnail thành GUID mới. Clear thumbnail (`null`) không gọi MediaService.

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

- Sự kiện `InventoryAssetAvailabilityChanged` phát lên topic `inventory.asset.events.v1` với Kafka partition key là **Aggregate ID** (`assetId`), payload `{ "assetId": Guid, "changedAt": DateTimeOffset }`.
## Quản lý giữ chỗ và phân bổ tài sản (Hold & Allocation Core - DPR-151)

### APIs

| Method | Endpoint | Yêu cầu quyền | Mô tả |
|---|---|---|---|
| `POST` | `/api/v1/inventory/assets/{assetId}/holds` | Service (`s=inventory.allocation.write`) | Tạo giữ chỗ tạm thời (201 Created khi mới; 200 OK khi replay đúng payload). |
| `POST` | `/api/v1/inventory/allocations/{allocationId}/allocate` | Service (`s=inventory.allocation.write`) | Chuyển đổi trạng thái Held sang Allocated (200 OK, trả allocation hiện tại). |

### Transaction Flow và Idempotency

- **Create Hold**:
  1. Bắt buộc Service Principal với scope `inventory.allocation.write` (Human/Admin bị từ chối 403).
  2. Validate cấu trúc request DTO (từ chối unknown properties, kiểm tra ngày bắt buộc, `startDate <= endDate`, chuẩn hóa `holdExpiresAt` về microsecond UTC bằng `BusinessDateTime.NormalizeUtc`).
  3. Mở transaction độc lập dưới PostgreSQL `pg_advisory_xact_lock` trên mức tài sản (`assetId`).
  4. Đọc lại asset và lấy clock từ `TimeProvider` sau khi có advisory lock (404 nếu asset không tồn tại).
  5. Tra cứu business key `(sourceSystem, externalReference)`. Nếu đã tồn tại: gọi `ValidateExternalRetry` đối chiếu asset, period, canonical expiry và notes. Replay đúng payload trả allocation hiện tại (200 OK, status có thể là `Held` hoặc `Allocated`) mà không thêm Outbox; khác payload trả 409 Conflict (`EXTERNAL_REFERENCE_REUSED`).
  6. Với key mới: validate nghiệp vụ qua `CreateHold`, tải candidate allocations và calendar blocks, kiểm tra availability. Nếu tài sản tắt bookings trả 400 (`ASSET_NOT_ACCEPTING_BOOKINGS`); nếu trùng lặp trả 409 Conflict (`ASSET_BLOCKED` hoặc `ASSET_UNAVAILABLE`) với thông báo đã làm sạch, không để lộ thông tin lịch của đối tượng khác.
  7. Stage thực thể Hold và phát đúng một sự kiện `InventoryAssetAvailabilityChanged` vào Outbox cùng transaction atomically.
  8. Nếu vi phạm unique constraint do hai asset dùng cùng business key: sau rollback, thực hiện đối chiếu lại dưới cùng asset lock. Row tìm được qua `ValidateExternalRetry`, khác payload trả 409; không tìm được giữ lỗi ban đầu.
- **Held → Allocated**:
  1. Bắt buộc Service Principal với scope `inventory.allocation.write` và validate `operationKey` không rỗng.
  2. Tra cứu allocation để lấy `AssetId` (404 nếu không tìm thấy).
  3. Acquire advisory lock trên `AssetId` qua `ExecuteAsync`, đọc lại allocation cùng transitions, lấy clock mới.
  4. Domain kiểm tra replay trước state/expiry: nếu `operationKey` đã được sử dụng thành công cho Allocate trước đó, trả về thành công (200 OK) mà không ghi thêm transition.
  5. Nếu key mới trên hold đã hết hạn hoặc trạng thái không phải Held (`HOLD_EXPIRED`, `INVALID_STATUS_TRANSITION`, `ALLOCATION_IN_TERMINAL_STATE`): Application service trực tiếp chuyển lỗi thành `Error.Conflict` (HTTP 409).
  6. Lưu thay đổi qua EF tracking, không query overlap lại, không kiểm tra `AcceptingBookings`, không phát sự kiện Outbox (khoảng thời gian chiếm lịch không đổi).

## Truy vấn tính khả dụng và Valkey Read Cache (Availability Queries - DPR-150)

### APIs

| Method | Endpoint | Yêu cầu quyền | Mô tả |
|---|---|---|---|
| `POST` | `/api/v1/inventory/availability/check` | `AvailabilityRead` (Human authenticated hoặc Service `s=inventory.availability.read`) | Kiểm tra tính khả dụng hàng loạt tài sản. Luôn đọc trực tiếp từ PostgreSQL trong transaction `RepeatableRead` (tối đa 3 SELECTs). Giới hạn tối đa 500 ID trước khi gộp trùng. |
| `GET` | `/api/v1/inventory/assets/{assetId}/busy-ranges?from=&to=` | `AvailabilityRead` (Human authenticated hoặc Service `s=inventory.availability.read`) | Đọc danh sách các khoảng bận đã làm sạch của tài sản. Luôn kiểm tra existence và `AcceptingBookings` từ PostgreSQL. Dùng Valkey month cache với TTL động. |

### Đặc tả Valkey Read Cache & Invalidation

- **Pin Dependency**: `StackExchange.Redis 3.3.1` trong `Directory.Packages.props`. Adapter đặt tại `Api/Caching`, thực thi interface `IInventoryAvailabilityCache`. Domain không phụ thuộc Redis.
- **Cấu hình**: `ConnectionStrings:Redis`, cấu hình cache tắt mặc định (`InventoryCache:Enabled = false`), TTL 60s, timeout 500ms. Mất kết nối không ngăn cản startup hay readiness.
- **Key Schema**: `inventory:asset:{assetId}:busy:{yyyyMM}`, index `inventory:asset:{assetId}:months` (ZSET). Payload chỉ chứa `ranges` và `expiresAtUtc`.
- **TTL động**: `min(normal TTL deadline, nearest active Hold expiry) - current time`.
- **Outbox Invalidation**: `OutboxPublisherWorker` invalidate cache cho asset khi claim sự kiện `InventoryAssetAvailabilityChanged` trước khi phát Kafka, ngoài DB transaction. Lỗi cache không chặn Kafka publish.
## Hợp đồng dùng chung và Concurrency Protocol

- `Common/Result.cs`: Result và Result<T> cho lỗi dự kiến.
- `Common/Error.cs`: Error/ErrorType với mã chuẩn.
- `Domain/IRepositories/IRepo.cs`: Hợp đồng đọc theo ID. Inventory ghi qua `IInventoryWriteTransaction` để giữ PostgreSQL advisory lock (`AssetLockKey.FromAssetId`), optimistic concurrency qua `xmin` và atomic commit.
- `Application/Security/InventoryAccessEvaluator.cs`: Thực thi ma trận phân quyền DPR-147.
- `Application/Kafka/Contracts/KafkaTopics.cs`: Các topics target: `inventory.allocation.commands.v1`, `inventory.allocation.events.v1`, `inventory.asset.events.v1` cùng DLQ tương ứng.
