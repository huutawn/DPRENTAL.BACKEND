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
