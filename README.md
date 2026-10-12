# InventoryService

Dịch vụ InventoryService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
InventoryService/
  src/
    Common/          # Kiểu dùng chung, Result/Error, AllocationStatus và utility
    Domain/          # Entities, Policies, ValueObjects, IRepositories, Repositories, Migrations, Clients, Caching, Kafka
    Application/     # Services, IServices, API DTOs, Mapping, Validation
    Api/             # Controllers, Security, Filters, Routing, Program
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

- **Human thông thường**: Bắt buộc có permission `inventory.asset.manage` trong claim `p` và quyền sở hữu danh mục (`listing.OwnerUserId == JWT.sub`).
- **Human Admin (`r=adm`)**: Được phép đọc/sửa Listing xuyên người dùng trong quản lý kho.
- **Danh sách tài sản (`GET /api/v1/inventory/assets`)**: Luôn chỉ trả tài sản thuộc sở hữu của người dùng hiện tại (`JWT.sub`), kể cả khi caller là Admin.
- **Chi tiết lịch Physical Asset (`CalendarRead` / DPR-149)**: Giữ nghiêm ngặt quy ước **owner-only** (`asset.OwnerUserId == JWT.sub`), Admin không được bypass.
- **Lịch Listing (`GET /api/v1/inventory/equipment-listings/{id}/calendar`)**: Cho phép cả Owner và Admin truy cập để quản lý tình trạng dùng chung của danh mục.
- **Service Principal**: Bắt buộc `u=svc`, `aud=inventory-api` và scope tương ứng trong claim `s` (`inventory.allocation.write`, `inventory.availability.read`). Bị từ chối trên các API quản lý Listing của cá nhân.
- **Loại bỏ OrganizationId**: Tài sản không còn thuộc tính `organization_id`. Danh tính người sở hữu duy nhất được lấy từ JWT `sub`.
- **Ranh giới giá với RentalService**: Inventory sở hữu giá niêm yết hiện tại trên `EquipmentListing`; RentalService đọc/revalidate và lưu `PriceSnapshot` khi tạo đơn thuê (`RentalOrder`). Thay đổi giá trên Inventory không sửa đơn hàng cũ.

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

---

## Quản lý danh mục niêm yết (EquipmentListing) và Đơn vị vật lý (InventoryAsset)

### Mô hình nghiệp vụ cốt lõi

1. **EquipmentListing (Danh mục niêm yết)**: Đại diện cho tin đăng chào thuê trên thị trường. Sở hữu:
   - Tên hiển thị (`Name`): Bắt buộc, trim, tối đa 200 ký tự.
   - Mô tả (`Description`): Tùy chọn, tối đa 2.000 ký tự (chuỗi trắng chuẩn hóa thành null).
   - Tên model kỹ thuật (`Model`): Bắt buộc, tối đa 200 ký tự.
   - Metadata Catalog chuẩn: `EquipmentTypeId`, `BrandId`, `SpecValues` (JSONB), `ThumbnailMediaId`.
   - Bảng giá niêm yết: `DailyRate`, `WeeklyRate`, `MonthlyRate`, `Currency` (`"VND"`).
   - Vòng đời trạng thái: `Draft` (khởi tạo), `Active` (đang mở thuê), `Inactive` (tạm dừng). Chuyển sang `Active` bắt buộc phải có ít nhất 1 physical unit đang nhận lịch (`AcceptingBookings = true`).
   - Cung ứng phái sinh (`totalQuantity`): Số lượng physical units trực thuộc đang sẵn sàng nhận đặt lịch (`accepting_bookings = true`).
2. **InventoryAsset (Đơn vị vật lý)**: Đại diện cho từng thiết bị cụ thể trong kho:
   - Định danh duy nhất: `AssetCode` (`AST-{AssetId:N}`) sinh tự động bởi hệ thống.
   - Số serial (`SerialNumber`): Bắt buộc, không được để trống, duy nhất trong cùng Listing (không phân biệt hoa/thường và khoảng trắng đầu cuối).
   - Năm sản xuất (`ManufactureYear`): Thuộc tính riêng của từng đơn vị vật lý.
   - Cờ nhận lịch (`AcceptingBookings`): Khi tắt (`false`), giữ nguyên lịch sử allocation/calendar block nhưng không tính vào `totalQuantity` khả dụng của Listing.

---

### APIs Quản lý Listing & Physical Units

| Method | Endpoint | Yêu cầu quyền | Mô tả |
|---|---|---|---|
| `POST` | `/api/v1/inventory/equipment-listings` | `inventory.asset.manage` / Admin | Tạo mới Listing Draft kèm **đúng một unit vật lý ban đầu có serial**, bắt buộc `name` và `model`, tùy chọn `description`. Trả 201 Created + Location. |
| `GET` | `/api/v1/inventory/equipment-listings` | Owner / Admin | Danh sách Listing của current user phân trang (`page`, `pageSize`, `q` tìm kiếm chuỗi con trong `name` hoặc `model`, `equipmentTypeId`, `brandId`, `status`). Hai SELECTs trong RepeatableRead snapshot. |
| `GET` | `/api/v1/inventory/equipment-listings/{id}` | Owner / Admin | Chi tiết Listing và derived `totalQuantity`. |
| `PATCH` | `/api/v1/inventory/equipment-listings/{id}` | Owner / Admin | Cập nhật metadata/status (presence tracking cho `name`, `description`, `brandId`, `model`, `thumbnailMediaId`, `schemaVersion`, `specValues`, `status`). Từ chối `name: null` hoặc rỗng, `description: null` xóa mô tả. Từ chối chuỗi số `status` như `"999"`. Từ chối field `quantity`. Draft → Active yêu cầu >= 1 unit accepting bookings. |
| `PUT` | `/api/v1/inventory/equipment-listings/{id}/rental-rate` | Owner / Admin | Thay thế toàn bộ bảng giá thuê niêm yết của Listing (VND, số nguyên dương). |
| `GET` | `/api/v1/inventory/equipment-listings/{id}/assets` | Owner / Admin | Danh sách đơn vị vật lý thuộc Listing có phân trang. |
| `POST` | `/api/v1/inventory/equipment-listings/{id}/assets` | `inventory.asset.manage` / Admin | Thêm **từng đơn vị vật lý (add-one)** vào Listing bằng `serialNumber`. Trả 201 Created + Location trỏ tới Asset. |
| `GET` | `/api/v1/inventory/equipment-listings/{id}/calendar?from=&to=` | Owner / Admin | Lịch dùng chung tổng hợp cấp Listing (cửa sổ tối đa 6 tháng). |
| `POST` | `/api/v1/inventory/equipment-listings/{id}/availability/check` | `AvailabilityRead` | Kiểm tra tính khả dụng toàn khoảng thời gian cho số lượng yêu cầu. |
| `POST` | `/api/v1/inventory/equipment-listings/{id}/holds` | `inventory.allocation.write` | Tạo giữ chỗ tạm thời theo nhóm (Group Hold) trên Listing. |

---

### APIs Đơn vị vật lý (Physical Asset)

| Method | Endpoint | Yêu cầu quyền | Mô tả |
|---|---|---|---|
| `GET` | `/api/v1/inventory/assets` | Owner / Admin | Danh sách đơn vị vật lý của current user (hỗ trợ lọc theo `manufactureYearFrom/To`). |
| `GET` | `/api/v1/inventory/assets/{id}` | Owner / Admin | Chi tiết đơn vị vật lý (`assetCode`, `serialNumber`, `manufactureYear`, `acceptingBookings`). |
| `PATCH` | `/api/v1/inventory/assets/{id}` | Owner / Admin | Cập nhật thông tin vật lý (`serialNumber`, `manufactureYear`). Xử lý optimistic concurrency qua `xmin`. |
| `PATCH` | `/api/v1/inventory/assets/{id}/accepting-bookings` | Owner / Admin | Bật/tắt nhận lịch của đơn vị vật lý. Tắt yêu cầu cung cấp `reason`. |
| `GET` | `/api/v1/inventory/assets/{id}/calendar` | Owner Only | Lịch sử dụng chi tiết của đơn vị vật lý (Admin bị từ chối 403). |
| `POST` | `/api/v1/inventory/assets/{id}/calendar-blocks` | Owner Only | Tạo khoảng chặn lịch bảo trì/kiểm định cho đơn vị vật lý. |
| `DELETE` | `/api/v1/inventory/assets/{id}/calendar-blocks/{blockId}` | Owner Only | Xóa khoảng chặn lịch của đơn vị vật lý. |

---

### Ví dụ Payload Create Listing & Add Asset

#### 1. Tạo Listing Draft với Serial ban đầu (`POST /api/v1/inventory/equipment-listings`)
```json
{
  "equipmentTypeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "brandId": "7d9b23b4-18c2-48f1-8be5-612b489d89a1",
  "name": "Sony FX3 kèm bộ phụ kiện quay",
  "description": "Máy quay điện ảnh Full-frame chuyên nghiệp kèm cage và pin dự phòng",
  "model": "FX3",
  "serialNumber": "FX3-VN-2026-0001",
  "schemaVersion": 1,
  "specValues": {
    "sensor": "Full-Frame",
    "resolution": "4K"
  },
  "rentalRate": {
    "dailyRate": 500000,
    "weeklyRate": 3000000,
    "monthlyRate": 10000000,
    "currency": "VND"
  }
}
```
*Phản hồi*: `201 Created` kèm `Location: /api/v1/inventory/equipment-listings/{listingId}`.
*Quy tắc*: Gửi trường cũ `quantity` sẽ bị từ chối ngay lập tức với `400 Bad Request`.

#### 2. Thêm một đơn vị vật lý mới (`POST /api/v1/inventory/equipment-listings/{id}/assets`)
```json
{
  "serialNumber": "FX3-VN-2026-0002"
}
```
*Phản hồi*: `201 Created` kèm `Location: /api/v1/inventory/assets/{assetId}` và body là `InventoryAssetResponse`.
*Quy tắc*: Nếu trùng serial (kể cả unit đang bị disable), trả về `409 Conflict` với mã lỗi `SERIAL_ALREADY_EXISTS`.

---

## Khám phá và Đọc Listing công khai (Public Equipment Listing Discovery)

### APIs Công khai (AllowAnonymous)

| Method | Endpoint | Mô tả |
|---|---|---|
| `GET` | `/api/v1/inventory/public/equipment-listings` | Khám phá và tìm kiếm danh sách Listing Active phân trang. Hỗ trợ lọc theo `from`, `to`, `requestedQuantity`, `q` (tìm chuỗi con trong `name` hoặc `model`, không phân biệt hoa thường, giữ phân biệt dấu tiếng Việt), `equipmentTypeId`, `brandId`, dynamic filters. Lọc khả dụng thực hiện trước pagination. |
| `GET` | `/api/v1/inventory/public/equipment-listings/{id}` | Đọc chi tiết Listing Active công khai đã làm sạch dữ liệu nhạy cảm. Listing Draft/Inactive trả 404. |

### Bảo vệ dữ liệu nhạy cảm (Privacy & Security)
- Phản hồi công khai chỉ hiển thị cấp Listing: `id`, `equipmentTypeId`, `brandId`, `name`, `description`, `model`, `schemaVersion`, `specValues`, `rentalRate`, `thumbnailUrl`, `totalQuantity`, `availableQuantity`.
- Tuyệt đối không rò rỉ `serialNumber`, `assetCode`, hay các GUID định danh đơn vị vật lý (`assetId`).
- Từ chối tham số ngoài whitelist: Việc truyền `manufactureYearFrom` hay `manufactureYearTo` vào endpoint công khai sẽ bị từ chối `400 INVALID_QUERY_PARAMETER` (năm sản xuất thuộc từng unit vật lý, không thuộc Listing).

---

## Lịch tổng hợp (Shared Calendar) & Quản lý giữ chỗ nhóm (Group Hold)

### 1. Lịch tổng hợp cấp Listing (`GET /api/v1/inventory/equipment-listings/{id}/calendar`)
- Thống kê chi tiết theo từng ngày trong khoảng yêu cầu (`from` đến `to`, tối đa 6 tháng):
  - `occupiedQuantity`: Số unit đang có lượt giữ chỗ hợp lệ hoặc đơn thuê đã xác nhận.
  - `blockedQuantity`: Số unit đang có khoảng chặn lịch vận hành (`CalendarBlock`).
  - `availableQuantity`: Số unit hoạt động và hoàn toàn rảnh rỗi (`totalQuantity - distinct unavailable`).
  - `suspendedOccupiedQuantity` & `suspendedBlockedQuantity`: Thống kê riêng cho các unit tạm dừng nhận lịch (`accepting_bookings = false`).
- Các ngày không có lịch (ngày trống) tự động trả về `occupied = 0`, `blocked = 0`, `available = totalQuantity`.
- Lượt giữ chỗ đã hết hạn (`hold_expires_at <= now`) hoặc đã hủy/giải phóng (`Released`/`Cancelled`) không làm tăng số lượng occupied.
- Mỗi physical unit chỉ được tính tối đa một lần cho từng trạng thái trong ngày (`distinct-unit counting`).

### 2. Giữ chỗ tạm thời theo nhóm (Group Hold - DPR-151)
- Endpoint: `POST /api/v1/inventory/equipment-listings/{id}/holds`.
- Khóa phân cấp nghiêm ngặt: `Group Lock` → `Listing Lock` → `Asset Locks` (theo thứ tự ID tăng dần).
- **Tính nguyên tử tuyệt đối (Atomicity)**: Cấp phát N units cùng các transitions và Outbox messages trong duy nhất một PostgreSQL transaction. Nếu có bất kỳ lỗi nào xảy ra giữa chừng, toàn bộ nhóm được rollback; không bao giờ phát sinh tình trạng cấp phát dở dang (partial allocations).
- **Replay Idempotency**:
  - Gửi lại cùng `(sourceSystem, groupReference)` và cùng payload: Trả về 201 Created / 200 OK với kết quả nhóm hiện tại (`isReplay = true`), không tạo thêm allocation hay Outbox message.
  - Replay khi nhóm đã hết hạn hoặc đã giải phóng: Trả về `409 Conflict` (`HOLD_EXPIRED`).
  - Replay với payload khác biệt (sai số lượng, ngày, ghi chú): Trả về `409 Conflict` (`EXTERNAL_REFERENCE_REUSED`).
  - Dùng cùng key cho Listing khác: Trả về `409 Conflict` (`EXTERNAL_REFERENCE_REUSED`).
- **Yêu cầu trạng thái Listing Active**: Cả giữ chỗ đơn (`POST /api/v1/inventory/assets/{assetId}/holds`) và giữ chỗ nhóm (`POST /api/v1/inventory/equipment-listings/{id}/holds`) khi tạo mới đều bắt buộc Listing cha phải ở trạng thái `Active`. Nếu Listing ở trạng thái `Draft` hoặc `Inactive`, yêu cầu bị từ chối bằng `409 Conflict` (`LISTING_NOT_ACTIVE`). Replay hợp lệ và xác nhận phân bổ (`Allocate`) cho các lượt giữ chỗ đã tạo thành công trước đó vẫn tiếp tục hoạt động ngay cả khi Listing chuyển sang `Inactive`.
- **Đánh giá thời gian thực**: Lịch tổng hợp cấp Listing lấy clock đánh giá occupancy sau khi tải snapshot từ database, đảm bảo tính nhất quán tuyệt đối với availability queries nếu có hold hết hạn trong quá trình I/O.

---

## Breaking Changes so với phiên bản trước

1. **Bỏ các endpoints cũ**:
   - `POST /api/v1/inventory/assets` → Chuyển thành `POST /api/v1/inventory/equipment-listings`.
   - `PUT /api/v1/inventory/assets/{id}/rental-rate` → Chuyển thành `PUT /api/v1/inventory/equipment-listings/{id}/rental-rate`.
   - `GET /api/v1/inventory/public/assets` → Chuyển thành `GET /api/v1/inventory/public/equipment-listings`.
2. **Loại bỏ input quantity**: Payload tạo Listing không nhận trường `quantity`. Quantity là giá trị phái sinh (`totalQuantity`) từ số lượng physical units đang hoạt động.
3. **Thêm Asset dạng add-one**: Thêm đơn vị vật lý nhận từng serial và trả về `201 Created` kèm header `Location: /api/v1/inventory/assets/{assetId}`, thay vì nhận mảng quantity trả về 200.
4. **Loại bỏ lọc năm sản xuất công khai**: Public discovery không cho phép lọc theo năm sản xuất; tính năng lọc năm được giữ cho API quản lý của Owner.

---

## Runbook Migrations & Điều kiện Cutover

### Hai Forward Migrations bắt buộc
1. `20261007000000_RefactorEquipmentListingAndInventoryAsset`:
   - Phân tách bảng `inventory.equipment_listings` và tái cấu trúc `inventory.assets`.
2. `20261008000000_RequireAssetSerialNumberAndUniqueListingSerial`:
   - Bắt buộc `serial_number NOT NULL` với CHECK constraint `length(btrim(serial_number)) > 0 AND length(serial_number) <= 100`.
   - Tạo unique expression index `ix_assets_listing_serial_unique` trên `(equipment_listing_id, lower(btrim(serial_number)))`.

### Preflight Check & Quy tắc An toàn dữ liệu
- Migration chạy preflight script kiểm tra:
  - Tất cả assets hiện có phải có `serial_number` hợp lệ (không null, không rỗng sau khi trim).
  - Không có các serial trùng lặp sau khi lowercase và trim trong cùng một Listing.
- **Xử lý khi vi phạm**: Nếu phát hiện dữ liệu lỗi, migration lập tức ném exception và rollback toàn bộ transaction; không ghi nhận vào `__EFMigrationsHistory` và không thay đổi bất kỳ dữ liệu nào.
- **Chính sách Backfill**: Tuyệt đối **không tự động backfill serial giả** và **không tự động xóa dữ liệu**. Mọi dữ liệu thiếu hoặc trùng phải được rà soát và khắc phục thủ công trước khi chạy migration.

### Yêu cầu Gateway trước Cutover
- Gateway bắt buộc phải cấu hình và định tuyến các route mới (`/api/v1/inventory/equipment-listings`, `/api/v1/inventory/public/equipment-listings`) trước khi thực hiện cutover.
- Smoke test thành công qua Gateway là điều kiện tiên quyết ngoài repository Inventory trước khi mở traffic production.

---

## Phạm vi hoãn lại (Deferred Scope & Non-Goals)

Các tính năng sau tiếp tục được giữ ngoài phạm vi MVP hiện tại:
- **Day drill-down**: Xem chi tiết danh sách từng physical unit công khai theo ngày.
- **Bulk import serial**: Nhập hàng loạt số serial qua file CSV/Excel.
- **Group allocate / Group release nguyên tử**: Chuyển trạng thái phân bổ hoặc giải phóng đồng loạt cho cả nhóm giữ chỗ (hiện tại xử lý theo từng unit allocation).
- **Location-based discovery**: Lọc và tìm kiếm thiết bị theo khoảng cách địa lý / vị trí kho bãi.
