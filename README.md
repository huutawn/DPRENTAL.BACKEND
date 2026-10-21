# RecommendationService

Dịch vụ RecommendationService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) chuẩn hóa theo RentalService.

## Cấu trúc

```text
RecommendationService/
  src/
    Common/          # Kiểu dùng chung, Result/Error và assembly marker
    Domain/          # Entities, IRepositories, Repositories, Migrations, DependencyInjection
    Application/     # Background, DTOs, IServices, Kafka, Services, Validation
    Api/             # Controllers, Exceptions, Filters, Properties, Program.cs
  tests/             # Kiểm thử
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật & contracts
scripts/             # Scripts hỗ trợ và Git hooks
```

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
# Đặt POSTGRES_PASSWORD riêng trong .env; đổi cổng nếu cần.
docker compose up -d
```

Để kích hoạt thêm Valkey cục bộ (thay vì kết nối shared Valkey):

```bash
docker compose --profile local up -d
```

Compose bao gồm:
- PostgreSQL 17 (`recommendation-pg`)
- Migration job (chạy `--migrate` và thoát sau khi PostgreSQL sẵn sàng)
- API RecommendationService (chờ migration hoàn tất)
- Kafka KRaft broker & service tự tạo topic (`kafka-init`)
- Valkey local (profile `local`)

## Chạy Database Migration

API không tự động migration khi khởi động bình thường. Migration được thực hiện qua lệnh riêng:

```bash
dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj -- --migrate
```

Hoặc qua Docker container:
```bash
docker compose run --rm migration
```

## Lệnh Rebuild (Projections & Cache)

### 1. Rebuild Projections (PostgreSQL)
Để tính toán và tái tạo lại toàn bộ điểm từ raw events trong PostgreSQL transaction với SHARE lock:

```bash
dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj -- --rebuild-projections
```

### 2. Rebuild Serving Cache (Valkey ZSET)
Để tái tạo serving cache ZSET trên Valkey từ dữ liệu projections hiện có trong PostgreSQL:

```bash
dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj -- --rebuild-cache
```

*(Ghi chú: Khi cần tái tạo toàn diện cả hai tầng, có thể chạy tuần tự `--rebuild-projections` rồi đến `--rebuild-cache`)*
## Chạy API cục bộ

Nạp biến môi trường từ `.env`:

```bash
set -a
. ./.env
set +a
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
export ConnectionStrings__Redis="127.0.0.1:${VALKEY_PORT}"
export Kafka__BootstrapServers="127.0.0.1:${KAFKA_PORT}"
dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj
```

## Health Checks

- `/health/live`: Trả về `HTTP 200` khi tiến trình hoạt động.
- `/health/ready`: Trả về `HTTP 200` khi PostgreSQL kết nối thành công và toàn bộ EF Core migrations đã được áp dụng. Trả về `HTTP 503` nếu PostgreSQL mất kết nối hoặc còn migration chưa chạy. Kafka hoặc Valkey outage không làm `/health/ready` thất bại.

## Kafka Ingestion, Retry và DLQ

Service lắng nghe trên 2 topic và chuyển DLQ khi có lỗi không thể phục hồi:
- Topic `inventory.behavior.events.v1` (Group `recommendation-inventory-behavior-v1`)
  - DLQ: `inventory.behavior.events.v1.recommendation.dlq`
- Topic `rental.events.v1` (Group `recommendation-rental-behavior-v1`)
  - DLQ: `rental.events.v1.recommendation.dlq`

Quy tắc:
- Tắt auto-commit và auto-offset-store. Chỉ commit offset sau khi DB commit thành công, duplicate hợp lệ, event rental ngoài phạm vi bỏ qua, hoặc DLQ delivery acknowledgment.
- Lỗi cú pháp JSON, sai version, sai producer, vi phạm schema hoặc xung đột payload được chuyển sang topic DLQ tương ứng với header `x-dlq-error-reason` cùng metadata nguồn.
- Lỗi tạm thời (DB/Network) được retry bằng exponential backoff có jitter (1s đến 30s, tối đa 5 lần mỗi round, tiếp tục các round tiếp theo khi outage kéo dài; không chuyển DLQ do mất kết nối).
- Trong thời gian retry wait, partition được pause và tiếp tục poll để duy trì heartbeat consumer group.

## Valkey Snapshot Cache Worker

- Chạy ngay khi khởi động rồi định kỳ mỗi 60 giây.
- Đọc top 20 equipment types của user (theo trang) và top 100 listings toàn cầu (score DESC, UUID ASC).
- Ghi snapshot JSON nguyên tử với TTL 300 giây. Lua script bảo đảm chỉ snapshot có `generatedAt >= existing generatedAt` mới được ghi đè.
- Valkey outage không làm gián đoạn API readiness hay luồng ingest Kafka.

## Build và Test

```bash
dotnet build RecommendationService.sln -c Release
dotnet test RecommendationService.sln -c Release
```
