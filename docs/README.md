# Tài liệu RecommendationService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, assembly marker) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, repository contracts, EF Core DbContext, repository implementations và DI theo RentalService | HTTP pipeline, controller, Application logic |
| `Application` | DTOs, IServices, Services, Validation, Background, Kafka và điều phối domain | Controller, HTTP pipeline, DbContext trực tiếp |
| `Api` | Controllers, Exceptions, Filters, Properties và DI/composition root | Business logic trực tiếp, DbContext trực tiếp |

Domain sở hữu repository contracts, EF Core DbContext, repository implementations và DI theo cấu trúc tham chiếu RentalService. Cấu trúc persistence này là **ngoại lệ kiến trúc đã chọn** (không tách project Infrastructure riêng). Application và API handlers tuyệt đối không được thao tác persistence trực tiếp hay inject DbContext. API đăng ký persistence qua `AddRecommendationServiceDomainRepositories` tại namespace `Dprental.RecommendationService.Domain.DependencyInjection`.
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
   dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:54336` và `http://localhost:44336`.
