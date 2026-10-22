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

## Tài liệu nghiệp vụ & Luồng xử lý

- [`docs/RecommendationService_Business_Workflow.md`](RecommendationService_Business_Workflow.md): Đặc tả chi tiết mục tiêu nghiệp vụ, luồng xử lý không dùng ML/DL, bài toán người dùng đã đăng nhập vs chưa đăng nhập, và phạm vi Phase 1.

## Hợp đồng tích hợp (Integration Contracts)

Phase 1 định nghĩa bộ contract chuẩn và schema để `InventoryService`, `RentalService` và `RecommendationService` tích hợp độc lập:

- **Tài liệu đặc tả hợp đồng**: [`docs/contracts/recommendation-v1.md`](contracts/recommendation-v1.md)
  - Ranh giới trách nhiệm và ownership của từng service
  - Envelope v1 và đặc tả ngữ nghĩa của 5 sự kiện hành vi
  - Kafka topology, partition keys, delivery guarantees và DLQ routing
  - Bảng trọng số tính điểm, quy tắc cộng dồn độc lập và cập nhật projection
  - Định dạng snapshot cache Valkey ZSET, tích hợp opt-in `sort=recommended` và cơ chế fallback 3 tầng của InventoryService
  - Checklist trách nhiệm riêng cho từng service và ma trận kịch bản kiểm thử
- **JSON Schemas Draft 2020-12 & Fixtures**: [`docs/contracts/schemas/`](contracts/schemas/)
  - Schemas cho 5 event: `ListingViewed`, `SearchPerformed`, `CartItemAdded`, `RentalItemPaid`, `RentalItemCompleted`
  - Schemas cho 2 cache snapshot: `user-equipment-types-cache`, `global-popular-listings-cache`
  - Fixtures hợp lệ (`valid/`) và không hợp lệ (`invalid/`)
- **DDL tham chiếu PostgreSQL**: [`docs/contracts/recommendation-v1.sql`](contracts/recommendation-v1.sql)
  - 3 bảng trong schema `recommendation`: `interaction_events`, `user_equipment_type_interest`, `listing_popularity`
  - Ràng buộc toàn vẹn dữ liệu, CHECK constraints theo từng event và trọng số điểm
  - Indexes cho lịch sử, ranking (score DESC, tie-break UUID ASC) và change tracking
  - Thủ tục mẫu xử lý atomic ingestion, deduplication và rebuild projections

### Kiểm chứng hợp đồng (Contract Validation)

```bash
# Kiểm tra toàn bộ JSON Schemas và valid/invalid fixtures bằng Draft 2020-12
python3 scripts/tests/validate_contracts.py

# Kiểm tra DDL, constraints, atomic upsert và rebuild trên PostgreSQL tạm (yêu cầu Docker)
python3 scripts/tests/verify_ddl_postgres.py
```
