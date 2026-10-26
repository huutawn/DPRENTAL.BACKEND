# Analytics Dashboard API

Các route `GET /api/v1/analytics/{overview,revenue,orders,equipment,suppliers}` yêu cầu JWT của AuthService có role `adm` hoặc `adp` (hoặc tên role đầy đủ). Service token (`u=svc`) bị từ chối. `from` và `to` là ngày lịch Việt Nam, tính cả hai đầu, tối đa 366 ngày. Mọi amount của MVP là số nguyên VND.

Nguồn dữ liệu: `rental.events.v1` cho vòng đời đơn, item, thiết bị và supplier; `payment.analytics.v1` cho settlement, refund và payout. Consumer ghi projection vào PostgreSQL rồi mới commit Kafka offset. Payment event bắt buộc có header `x-dprental-proof` với audience `analytics-service`; cấu hình public keys qua `Kafka:ProofPublicKeys:{keyId}`. Không lưu raw message, thông tin giao hàng hoặc tài khoản ngân hàng.

Cấu hình cần có khi chạy: `ConnectionStrings:Postgres`, `Authentication:Jwt:{Issuer,Audience,JwksUri}`, và `Kafka:{Enabled,BootstrapServers,GroupId,ProofIssuer,ProofEnvironment,ProofPublicKeys}`. `Kafka:Enabled` mặc định `false`; chỉ bật khi broker và proof keys đã sẵn sàng. Có thể cấu hình SASL/TLS qua `Kafka:SecurityProtocol`, `Kafka:SaslMechanism`, `Kafka:SaslUsername`, `Kafka:SaslPassword` và `Kafka:SslCaLocation`.

Khi chạy local, đặt `ConnectionStrings:Postgres` trong .NET User Secrets của `AnalyticsService/src/Api/AnalyticsService.Api.csproj`, giống các service khác. `.env` chỉ được Docker Compose dùng để tạo PostgreSQL; API không tự nạp `.env`. Nếu AuthService chạy local, đặt thêm `Authentication:Jwt:JwksUri` trong User Secrets trỏ đến JWKS local của AuthService.

Sau khi cấu hình PostgreSQL, áp dụng migration bằng `dotnet ef database update --project AnalyticsService/src/Domain/AnalyticsService.Domain.csproj --startup-project AnalyticsService/src/Api/AnalyticsService.Api.csproj`. Việc replay cùng event được chống trùng theo `messageId` (Rental) hoặc khóa nghiệp vụ (Payment).

## Xử lý message (DPR-258)

```text
AnalyticsKafkaConsumerWorker (Api/Background, chỉ chạy khi Kafka:Enabled = true)
  → KafkaRecordProcessor: mỗi lần thử một DI scope; lỗi tạm thời → chờ tăng dần, thử lại CHÍNH message đó
    → AnalyticsEventDispatcher
        rental.events.v1:     phong bì → inbox (analytics_processed_rental_events) → projector
        payment.analytics.v1: proof → phong bì → projector (chống trùng bằng messageId / LogicalEventId)
      → một lần SaveChanges = read model + inbox trong cùng transaction
  → (Processed) báo SignalR "số liệu đã đổi"
  → commit offset Kafka (chỉ sau khi DB đã lưu)
```

| Kết quả | Khi nào | Commit offset |
| --- | --- | --- |
| `Processed` | Đã lưu | Có |
| `Duplicate` | Đã xử lý trước đó (giao lại, hoặc Payment phát lại cùng nghiệp vụ) | Có |
| `Skipped` | Topic / loại event không dùng (không ghi DB) | Có |
| `DeadLettered` | Lỗi dữ liệu: JSON hỏng, thiếu field, tiền lẻ, không phải VND, proof sai, xung đột số liệu | Có |
| `RetryLater` | Lỗi tạm thời: DB, hoặc `keyId` proof chưa có trong `Kafka:ProofPublicKeys` | Không: thử lại |

`analytics_dead_letters` chỉ lưu vị trí (`Topic`, `Partition`, `Offset`), `MessageId`, `MessageType`, `ErrorCode`; không lưu nội dung message. Xử lý lại: sửa nguyên nhân phía producer rồi đọc lại message theo vị trí.

Trạng thái đơn và dòng thuê đi qua cổng version: event mới hơn thì đổi, event cũ đến muộn không kéo lùi, nhiều event cùng `orderVersion` thì trạng thái "xa hơn" thắng, event thiếu version chỉ được tiến lên. Cột thời gian chỉ điền một lần nên không phụ thuộc thứ tự đến.

Cấu hình thêm: `Kafka:{RetryInitialDelayMilliseconds,RetryMaxDelaySeconds}` (kiểm tra lúc khởi động cùng toàn bộ section `Kafka` khi bật), `Authentication:Jwt:{TimeoutSeconds,MaxStaleMinutes,ClockSkewSeconds}`, `AnalyticsHub:{HubPath,AllowedOrigins}`.

## SignalR

- Kết nối `/hubs/analytics` (chỉ WebSocket), token lấy từ cookie `accessToken` giống PaymentHub; token service, sai audience hoặc thiếu `sub` bị từ chối. Môi trường thật phải điền `AnalyticsHub:AllowedOrigins`.
- Event `AnalyticsDataChanged`, payload `{ scope: "Rental" | "Finance" }` trong envelope `{ eventId, version, occurredAt, payload }`. Không chứa số liệu: FE gọi lại API dashboard.
- Admin nhận mọi thay đổi; NCC nhận thay đổi của dòng thuê / quyết toán / payout có `SupplierId` trùng `sub` (ownership MVP). Chỉ gửi sau khi DB đã lưu; gửi lỗi chỉ ghi log.

## Test PostgreSQL

`tests/Api.Tests/Integration` chạy migration, transaction, kịch bản đơn X của contract (đo bằng dashboard API) và các câu native SQL trên PostgreSQL thật. Đặt biến môi trường (user cần quyền `CREATEDB`; mỗi test tạo rồi xóa database `analytics_tests_*`):

```bash
export ANALYTICS_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=...;Password=..."
dotnet test AnalyticsService.sln
```

Không đặt biến này thì các test đó tự bỏ qua; đặt mà không kết nối được thì test báo lỗi.

## KPI chờ producer

Một số KPI trong `docs/contracts/analytics-kpi-v1.md` vẫn chờ producer bổ sung: trạng thái order `Rejected` và một số đường `Expired` từ Rental; `PendingRefundAmount` và phần tiền thuê riêng trong `GrossRentalValueRefunded` từ Payment. Dashboard không suy diễn các KPI đó từ dữ liệu khác.
