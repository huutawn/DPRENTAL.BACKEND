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
| `POST` | `/api/v1/inventory/assets` | Tạo mới tài sản (201 Created + Location header). Owner lấy từ JWT sub. |
| `GET` | `/api/v1/inventory/assets` | Danh sách tài sản phân trang của current user (`page` default 1, `pageSize` default 20, max 100). Chặn query khác. |
| `GET` | `/api/v1/inventory/assets/{id}` | Chi tiết tài sản (200 OK). 404 nếu không tìm thấy, 403 nếu cross-user (Admin bypass allowed). |
| `PATCH` | `/api/v1/inventory/assets/{id}` | Cập nhật thông tin chi tiết với presence tracking. Omitted giữ nguyên; explicit null clear serial/year/thumbnail. |
| `PATCH` | `/api/v1/inventory/assets/{id}/accepting-bookings` | Bật/tắt nhận lịch: `{ "acceptingBookings": bool, "reason": string? }`. Đổi cờ phát sự kiện availability qua Outbox. |

### Tích hợp Catalog & Media

- **CatalogService**: Tra cứu Published equipment type qua `GET /api/v1/catalog/equipment-types/{id}`. Canonical specification validation trả về rõ ràng `NotSupported` theo quyết định MVP và được chấp nhận sau khi kiểm tra cấu trúc JSON object tại Inventory.
- **MediaService**: Tái sử dụng `PreparePublicAsync` khi tạo hoặc cập nhật thumbnail thành GUID mới. Clear thumbnail (`null`) không gọi MediaService.

### Kafka Event Key & Cutover Rollout

- Sự kiện `InventoryAssetAvailabilityChanged` phát lên topic `inventory.asset.events.v1` với Kafka partition key là **Aggregate ID** (`assetId`), payload `{ "assetId": Guid, "changedAt": DateTimeOffset }`.
- **Quy trình Cutover**:
  1. Dừng traffic ghi và drain publisher Outbox hiện có.
  2. **Bắt buộc**: Consumer phải xử lý hết backlog sự kiện cũ trước khi hệ thống mới phát sự kiện với partition key mới (`assetId` thay vì `orgId:assetId`), vì Kafka không bảo đảm thứ tự giữa hai partition keys khác nhau.
  3. Chạy preflight script `scripts/complete-asset-management-preflight.sql` và nạp dữ liệu staging vào `inventory.asset_model_backfill`.
  4. Áp dụng migration `20261002030000_CompleteInventoryAssetManagement` (tự động rollback nếu backfill thiếu hoặc vi phạm year tương lai).
  5. Cấu hình `CatalogService__BaseUrl` và khởi động binary mới.


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
