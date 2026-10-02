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

- **Human thông thường**: Bắt buộc có permission tương ứng trong `p` (`inventory.asset.manage`, `inventory.calendar.read`) và `asset.OwnerUserId == JWT.sub`.
- **Human Admin (`r=adm`)**: Được phép đọc/sửa tài sản xuyên người dùng mà không cần permission `*.all`.
- **Service Principal**: Bắt buộc `u=svc`, `aud=inventory-api` và scope tương ứng trong claim `s` (`inventory.allocation.write`, `inventory.calendar.read`).
- **OrganizationId**: Là metadata liên kết của tài sản, không phải authorization boundary. Thành viên chung tổ chức không được tự động truy cập tài sản của Owner khác.

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

## Hợp đồng dùng chung và Concurrency Protocol

- `Common/Result.cs`: Result và Result<T> cho lỗi dự kiến.
- `Common/Error.cs`: Error/ErrorType với mã chuẩn.
- `Domain/IRepositories/IRepo.cs`: Hợp đồng đọc theo ID. Không cung cấp CRUD tự lưu: Inventory ghi qua `IInventoryWriteTransaction` để giữ PostgreSQL advisory lock (`AssetLockKey.FromAssetId`), idempotency và atomic commit.
- `Application/Security/InventoryAccessEvaluator.cs`: Thực thi ma trận phân quyền DPR-147.
- `Application/Kafka/Contracts/KafkaTopics.cs`: Các topics target: `inventory.allocation.commands.v1`, `inventory.allocation.events.v1`, `inventory.asset.events.v1` cùng DLQ tương ứng.
