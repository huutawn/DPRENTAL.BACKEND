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
- **Service Principal**: Bắt buộc `u=svc`, `aud=inventory-api` và scope tương ứng trong claim `s` (`inventory.allocation.write`, `inventory.calendar.read`). Bị từ chối trên các API quản lý tài sản của cá nhân.
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

## Hợp đồng dùng chung và Concurrency Protocol

- `Common/Result.cs`: Result và Result<T> cho lỗi dự kiến.
- `Common/Error.cs`: Error/ErrorType với mã chuẩn.
- `Domain/IRepositories/IRepo.cs`: Hợp đồng đọc theo ID. Inventory ghi qua `IInventoryWriteTransaction` để giữ PostgreSQL advisory lock (`AssetLockKey.FromAssetId`), optimistic concurrency qua `xmin` và atomic commit.
- `Application/Security/InventoryAccessEvaluator.cs`: Thực thi ma trận phân quyền DPR-147.
- `Application/Kafka/Contracts/KafkaTopics.cs`: Các topics target: `inventory.allocation.commands.v1`, `inventory.allocation.events.v1`, `inventory.asset.events.v1` cùng DLQ tương ứng.
