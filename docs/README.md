# Tài liệu AnalyticsService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, repository contracts, EF Core DbContext và repositories như AuthService | HTTP pipeline, controller |
| `Application` | DTOs, Services, Mapping và điều phối domain | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, middleware và DI/composition root | Business logic trực tiếp |

Domain sở hữu repository contracts, EF Core DbContext và repository implementations theo cấu trúc AuthService. API đăng ký persistence qua `AddAnalyticsServiceDomainRepositories`.

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
   dotnet run --project AnalyticsService/src/Api/AnalyticsService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:51309` và `http://localhost:41309`.

## Read model (DPR-258)

Định nghĩa KPI: [`contracts/analytics-kpi-v1.md`](contracts/analytics-kpi-v1.md). Analytics chỉ đọc event Kafka và ghi vào các bảng `*_facts`; không gọi HTTP hay đọc DB service khác.

Luồng xử lý một message:

```text
AnalyticsKafkaConsumerWorker (Api/Background)
  → KafkaRecordProcessor: mỗi lần thử một DI scope; lỗi tạm thời → chờ tăng dần, thử lại CHÍNH message đó
    → AnalyticsEventDispatcher: phong bì → inbox (chống trùng) → định tuyến → projector
      → một lần SaveChanges = facts + inbox trong cùng transaction
  → (Processed) báo SignalR "số liệu đã đổi"
  → commit offset Kafka (chỉ sau khi DB đã lưu)
```

| Kết quả | Ý nghĩa | Commit offset |
| --- | --- | --- |
| `Processed` | Đã lưu facts + inbox | Có |
| `Duplicate` | `messageId` đã có trong inbox | Có |
| `Skipped` | Loại event Analytics không dùng (không ghi DB) | Có |
| `DeadLettered` | Lỗi dữ liệu (JSON hỏng, thiếu field, tiền lẻ, không phải VND, xung đột số liệu) | Có |
| `RetryLater` | Lỗi tạm thời (DB) | Không: thử lại |

### Dead letter

Bảng `analytics_dead_letters` chỉ lưu vị trí (`Topic`, `Partition`, `Offset`), `MessageId`, `MessageType`, `ErrorCode`; **không lưu nội dung message**. Muốn xử lý lại: đọc lại message từ Kafka theo vị trí, sửa nguyên nhân phía producer, rồi phát lại.

### Cấu hình

| Section | Khóa chính | Ghi chú |
| --- | --- | --- |
| `Kafka` | `Enabled`, `BootstrapServers`, `ConsumerGroupId`, `RentalEventsTopic`, `PaymentEventsTopic`, `Retry*` | Tắt mặc định. Bật mà thiếu cấu hình thì app không khởi động. Group mới đọc từ đầu topic (`Earliest`) |
| `Authentication:Jwt` | `Issuer`, `ClientAudience`, `JwksUri` | Xác thực token AuthService (RS256 qua JWKS), giống PaymentService |
| `AnalyticsHub` | `HubPath` (`/hubs/analytics`), `AllowedOrigins`, timeout | SignalR theo mẫu PaymentHub |

### SignalR

- Kết nối `/hubs/analytics` (chỉ WebSocket), token lấy từ cookie `accessToken` giống PaymentHub. Token service (`u = svc`), sai audience hoặc thiếu `sub` bị từ chối.
- Event `AnalyticsDataChanged`, payload `{ scope: "Rental" | "Finance" }` bọc trong envelope `{ eventId, version, occurredAt, payload }`. **Không chứa số liệu**: FE gọi lại API dashboard để lấy số.
- Admin (claim `r`/`role` là `adm`, `adp`, `admin`, `admin_platform`) nhận mọi thay đổi. NCC nhận thay đổi của dòng thuê có `SupplierId` trùng `sub` của mình (ownership MVP).
- Chỉ gửi sau khi DB đã lưu; gửi lỗi chỉ ghi log, không ảnh hưởng việc đọc Kafka.

### Test PostgreSQL

Test trong `tests/Api.Tests/Integration` chạy migration và transaction trên PostgreSQL thật. Đặt biến môi trường (user cần quyền `CREATEDB`; mỗi test tạo rồi xóa database `analytics_tests_*`):

```bash
export ANALYTICS_TEST_POSTGRES="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
dotnet test AnalyticsService.sln
```

Không đặt biến này thì các test đó tự bỏ qua; đặt mà không kết nối được thì test báo lỗi.
