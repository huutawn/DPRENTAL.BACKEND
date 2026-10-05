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

## Triển khai và Rollback (AddEquipmentTypeSpecSchemaAndDropOrganizationId)

### 1. Khoảng dừng ghi và Sao lưu
- Tạm dừng ghi đối với CatalogService trước khi tiến hành migration. Dừng phiên bản API cũ trong cửa sổ triển khai do việc drop column `OrganizationId`.
- Tạo bản sao lưu đầy đủ:
  ```bash
  pg_dump -h <host> -p <port> -U <user> -Fc -d catalogservice > catalogservice_pre_specschema.dump
  ```

### 2. Migration và Backfill
- Migration thực hiện trong một transaction duy nhất.
- Tạo bảng `EquipmentTypeSpecSchemas`, unique index draft `UX_EquipmentTypeSpecSchemas_EquipmentTypeId_Draft` và thêm cột `CurrentSchemaVersion`.
- Tự động backfill mỗi `EquipmentType` hiện có một bản ghi published v1 với `{"fields":[]}` và `CurrentSchemaVersion=1`.
- Drop index `IX_EquipmentTypes_OrganizationId` và drop column `OrganizationId`.
- Thiết lập composite foreign key `FK_EquipmentTypes_EquipmentTypeSpecSchemas_CurrentSchema` trên `(Id, CurrentSchemaVersion)`.
- Thực hiện migration:
  ```bash
  dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
  ```

### 3. Deploy và Mở ghi
- Khởi động phiên bản API mới.
- Tiến hành smoke test các endpoint discovery, admin EquipmentType CRUD và spec-schema endpoints.
- Khôi phục luồng ghi dữ liệu.

### 4. Rollback
- Phương thức `Down` ném `NotSupportedException` do việc drop `OrganizationId` và quản lý schema không thể rollback tự động.
- Rollback bằng cách khôi phục cơ sở dữ liệu từ bản sao lưu:
  ```bash
  pg_restore -h <host> -p <port> -U <user> -d catalogservice --clean catalogservice_pre_specschema.dump
  ```
- Khởi động lại phiên bản API cũ.

## Triển khai và Rollback (AddBrands)

Migration `AddBrands` bổ sung bảng `catalog.Brands` và các unique index đi kèm mà không ảnh hưởng tới dữ liệu của các bảng hiện có.

### 1. Migration
Chạy migration trước khi deploy:
```bash
dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
```

### 2. Rollback
Phương thức `Down` của migration `AddBrands` ném `NotSupportedException` để từ chối rollback phá hủy dữ liệu. Khi rollback ứng dụng, bảng `catalog.Brands` được giữ nguyên trong cơ sở dữ liệu.
