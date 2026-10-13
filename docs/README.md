# Tài liệu PaymentService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility như `VndAmountValidator`, `VietQrUrlGenerator`, `PaymentCodeGenerator`, `PayloadFingerprinter`) | Application, Domain, HTTP pipeline |
| `Domain` | Entities (`Invoice`, `InvoiceItem`, `OperationReceipt`, `OutboxMessage`), `IRepository`, `Repository` (`EfInvoiceRepository`), `Persistence` (`PaymentServiceDbContext`, Factory), `Migrations` | Application DTOs, HTTP pipeline, controller |
| `Application` | DTOs, IServices (`IInvoiceService`, `IInvoiceCreationService`, `IInvoiceLifecycleService`, `IPaymentRecordingService`), Services, Background workers (`InvoiceExpiryWorker`), Options | Controller, HTTP pipeline, direct DbContext/EF/Npgsql access |
| `Api` | Controllers (`InvoicesController`), Security (JWT/JWKS), Exception handlers, DI/composition root (`AddPaymentDomainRepositories`, `AddPaymentApplication`) | Business logic trực tiếp |

Hạ tầng persistence và unique indexes (`InvoiceRef`, `RentalOrderId`, `PaymentCode`, `PaymentRef`, `OperationKey`) được quản lý bởi `PaymentServiceDbContext` trong `Domain/Persistence` và `EfInvoiceRepository` trong `Domain/Repository`, đăng ký qua `AddPaymentDomainRepositories`. Tầng `Application` và background worker đăng ký qua `AddPaymentApplication`.
## Cấu hình môi trường

`Api/appsettings.Development.json` chỉ chứa giá trị không nhạy cảm. Docker Compose lấy credential local từ `.env`: sao chép `.env.example` thành `.env` và đặt `POSTGRES_PASSWORD` riêng. `.env` đã bị Git bỏ qua. API lấy connection string từ biến môi trường `ConnectionStrings__Postgres`.

Cấu hình merchant tài khoản nhận tiền đặt tại `Payment:Merchant` (`Bank`, `Account`). Cấu hình JWT đặt tại `Authentication:Jwt` (`Issuer`, `ServiceAudience`, `ClientAudience`, `JwksUri`).

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
   dotnet run --project PaymentService/src/Api/PaymentService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:56280` và `http://localhost:46280`.

## API Hóa đơn & QR (Payment Contract v1)

- `POST /api/v1/invoices`: Tạo hóa đơn từ danh sách items của `RentalOrder`, hỗ trợ replay an toàn qua `OperationKey`. Yêu cầu service token (`u=svc`, `aud=payment-api`, `s=payment.invoice.write`). Trả về 201 Created khi tạo mới, 200 OK khi replay.
- `POST /api/v1/invoices/{invoiceRef}/cancel`: Hủy hóa đơn chưa thanh toán. Yêu cầu service token (`u=svc`, `aud=payment-api`, `s=payment.invoice.write`).
- `GET /api/v1/invoices/{invoiceRef}`: Lấy thông tin thanh toán và URL ảnh VietQR SePay. Yêu cầu renter token (`aud=dprental-clients`, `sub == RenterId`). Trả về 404 nếu không tìm thấy hoặc caller không sở hữu.
- **Invariants về thời gian, khóa hàng và sự kiện:**
  - Mọi `DateTimeOffset` lưu trữ trong domain (`PaymentDeadline`, `CreatedAt`, `PaidAt`, `CancelledAt`, `ExpiredAt`) được chuẩn hóa về UTC (`Offset = 0`).
  - Bước kiểm tra ownership trong `GET` dùng `AsNoTracking()`; mọi cập nhật trạng thái (`Cancel`, `RecordPayment`, `Expiry`) đều lấy khóa hàng PostgreSQL `SELECT ... FOR UPDATE` kèm `ReloadAsync()` từ database trước khi quyết định chuyển trạng thái.
  - `Create` replay kích hoạt kiểm tra hết hạn dưới khóa hàng trước khi trả response cũ.
  - `RecordPaymentReceivedAsync` kiểm tra replay `PAID` trước kiểm tra thời gian; với hóa đơn `PENDING`, từ chối `paidAt >= PaymentDeadline` và tự động persist `EXPIRED` nếu thời điểm hiện tại đã qua hạn. `PaymentRef` có unique index để chặn hai hóa đơn dùng chung một mã giao dịch thanh toán.
  - Sự kiện `InvoicePaid` trong `outbox_messages` luôn có `correlationId` kiểu chuỗi bắt buộc (mặc định bằng `MessageId` dạng UUID).

*Lưu ý:* Tin nhắn Outbox (`InvoicePaid`) cho hóa đơn tổng 0 đã được lưu bền vững vào bảng `outbox_messages` trong cùng transaction, chưa được publish ra Kafka/broker trong vertical slice này. Webhook, publisher và xử lý thanh toán thực tế sẽ thuộc task tích hợp tiếp theo. Không hỗ trợ refund, thẻ tín dụng, ví điện tử, thanh toán định kỳ, trả góp hoặc ngoại tệ.
