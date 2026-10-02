# Tài liệu CatalogService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, repository contracts, EF Core DbContext và repositories như AuthService | HTTP pipeline, controller |
| `Application` | DTOs, Services, Mapping và điều phối domain | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, middleware và DI/composition root | Business logic trực tiếp |

Domain sở hữu repository contracts, EF Core DbContext và repository implementations theo cấu trúc AuthService. API đăng ký persistence qua `AddCatalogServiceDomainRepositories`.

## Cấu hình môi trường

`Api/appsettings.Development.json` chỉ chứa giá trị không nhạy cảm. Docker Compose lấy credential local từ `.env`: sao chép `.env.example` thành `.env` và đặt `POSTGRES_PASSWORD` riêng. `.env` đã bị Git bỏ qua. API lấy connection string từ biến môi trường `ConnectionStrings__Postgres`.

## Chạy API cục bộ

1. Khởi động PostgreSQL:

   ```bash
   cp .env.example .env
   # Đặt POSTGRES_PASSWORD riêng trong .env.
   docker compose up -d
   ```

2. Chạy API:

   ```bash
   set -a
   . ./.env
   set +a
   export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
   dotnet run --project CatalogService/src/Api/CatalogService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:57297` và `http://localhost:47297`.

## Triển khai và Rollback (RefactorEquipmentToEquipmentType)

### 1. Khoảng dừng ghi và Sao lưu
- Tạm dừng ghi đối với CatalogService trước khi tiến hành migration.
- Tạo bản sao lưu đầy đủ:
  ```bash
  pg_dump -h <host> -p <port> -U <user> -Fc -d catalogservice > catalogservice_pre_refactor.dump
  ```

### 2. Preflight và Migration
- Migration thực hiện trong transaction độc quyền với preflight kiểm tra trùng `Code`.
- Nếu có duplicate `Code`, transaction abort ngay lập tức và giữ nguyên dữ liệu/schema cũ.
- Thực hiện migration:
  ```bash
  dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
  ```

### 3. Deploy và Mở ghi
- Khởi động phiên bản API mới với base route `/api/v1/catalog/equipment-types`.
- Tiến hành smoke test các endpoint GET và POST/PATCH.
- Khôi phục luồng ghi dữ liệu.

### 4. Rollback
- Phương thức `Down` ném `NotSupportedException` do các cột supplier bị drop vĩnh viễn.
- Rollback bằng cách khôi phục cơ sở dữ liệu từ bản sao lưu:
  ```bash
  pg_restore -h <host> -p <port> -U <user> -d catalogservice --clean catalogservice_pre_refactor.dump
  ```
- Khởi động lại phiên bản API cũ.
