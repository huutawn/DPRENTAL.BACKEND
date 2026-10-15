# Tài liệu PaymentService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility như `VndAmountValidator`, `VietQrUrlGenerator`, `PaymentCodeGenerator`, `PayloadFingerprinter`) | Application, Domain, HTTP pipeline |
| `Domain` | Entities (`Invoice`, `InvoiceItem`, `OperationReceipt`, `OutboxMessage`, `PaymentTransaction`), `IRepository`, `Repository` (`EfInvoiceRepository`), `Persistence` (`PaymentServiceDbContext`, Factory), `Migrations` | Application DTOs, HTTP pipeline, controller |
| `Application` | DTOs, IServices (`IInvoiceService`, `IInvoiceCreationService`, `IInvoiceLifecycleService`, `IInvoicePaymentProcessor`), Services (`InvoicePaymentProcessor`, `PaymentCodeExtractor`), Background workers (`InvoiceExpiryWorker`), Options | Controller, HTTP pipeline, direct DbContext/EF/Npgsql access |
| `Api` | Controllers (`InvoicesController`, `PaymentWebhooksController`), Security (JWT/JWKS, SePayApiKey), Exception handlers, DI/composition root (`AddPaymentDomainRepositories`, `AddPaymentApplication`) | Business logic trực tiếp |

Hạ tầng persistence và unique indexes (`InvoiceRef`, `RentalOrderId`, `PaymentCode`, `PaymentRef`, `OperationKey`) được quản lý bởi `PaymentServiceDbContext` trong `Domain/Persistence` và `EfInvoiceRepository` trong `Domain/Repository`, đăng ký qua `AddPaymentDomainRepositories`. Tầng `Application` và background worker đăng ký qua `AddPaymentApplication`.
## Cấu hình môi trường

`Api/appsettings.Development.json` chỉ chứa giá trị không nhạy cảm. Docker Compose lấy credential local từ `.env`: sao chép `.env.example` thành `.env` và đặt `POSTGRES_PASSWORD` riêng. `.env` đã bị Git bỏ qua. API lấy connection string từ biến môi trường `ConnectionStrings__Postgres`.

Cấu hình merchant tài khoản nhận tiền đặt tại `Payment:Merchant` (`Bank`, `Account`). Cấu hình SePay webhook đặt tại `Payment:SePay` (`ApiKey`, `Gateway`, `AccountNumber`, `GatewayMapping`). Cấu hình JWT đặt tại `Authentication:Jwt` (`Issuer`, `ServiceAudience`, `ClientAudience`, `JwksUri`).

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

## API Hóa đơn, QR & Webhook SePay (Payment Contract v1)

- `POST /api/v1/invoices`: Tạo hóa đơn từ danh sách items của `RentalOrder`, hỗ trợ replay an toàn qua `OperationKey`. Yêu cầu service token (`u=svc`, `aud=payment-api`, `s=payment.invoice.write`). Trả về 201 Created khi tạo mới, 200 OK khi replay.
- `POST /api/v1/invoices/{invoiceRef}/cancel`: Hủy hóa đơn chưa thanh toán. Yêu cầu service token (`u=svc`, `aud=payment-api`, `s=payment.invoice.write`).
- `GET /api/v1/invoices/{invoiceRef}`: Lấy thông tin thanh toán và URL ảnh VietQR SePay. Yêu cầu renter token (`aud=dprental-clients`, `sub == RenterId`). Trả về 404 nếu không tìm thấy hoặc caller không sở hữu.
- `POST /api/v1/payment/webhooks`: Tiếp nhận webhook chuyển khoản từ cổng thanh toán SePay. Yêu cầu xác thực `Authorization: Apikey <key>` với so sánh thời gian hằng số (constant-time). Xác thực, chuẩn hóa dữ liệu ngân hàng và điều phối qua `IInvoicePaymentProcessor`.

- **Invariants về thời gian, khóa hàng và sự kiện:**
  - Mọi `DateTimeOffset` lưu trữ trong domain (`PaymentDeadline`, `CreatedAt`, `PaidAt`, `CancelledAt`, `ExpiredAt`, `TransactionDate`) được chuẩn hóa về UTC (`Offset = 0`).
  - Bước kiểm tra ownership trong `GET` dùng `AsNoTracking()`; mọi cập nhật trạng thái (`Cancel`, `Expiry`, xác nhận webhook) đều lấy khóa hàng PostgreSQL `SELECT ... FOR UPDATE` kèm `ReloadAsync()` từ database trước khi quyết định chuyển trạng thái.
  - **Phân tách trách nhiệm sau refactor:**
    - *API Controller (`PaymentWebhooksController`):* Controller mỏng, chỉ đảm nhận model binding DTO `SePayWebhookPayload`, gọi `IInvoicePaymentProcessor.ProcessAsync`, và chuyển `Result` sang HTTP ProblemDetails (`this.ToProblem(result.Error)`). Không chứa validation, parse ngày, normalization hay matching.
    - *Application (`InvoicePaymentProcessor`, `SePayTransactionNormalizer`):* Sở hữu toàn bộ use case webhook bao gồm validate input (fail-fast trước DB tx), chuẩn hóa thời gian/chuỗi (`SePayDateTimeParser`), claim provider ID, trích xuất mã thanh toán (`PaymentCodeExtractor`), khóa hàng và reload Invoice, kiểm tra replay/duplicate, matching và điều phối transaction.
    - *Domain (`Invoice`):* Bảo vệ invariants và thực hiện chuyển trạng thái (`MarkPaid`, `RecordLatePayment`).
    - *Repository (`EfInvoiceRepository`):* Quản lý câu truy vấn, khóa hàng PostgreSQL (`FOR UPDATE`), và lưu trữ nguyên tử PaymentTransaction, Invoice, Outbox.
  - Các giao dịch late payment (thời điểm chuyển khoản hoặc thời điểm xử lý sau khi lấy khóa đạt/vượt `PaymentDeadline`, hoặc hóa đơn đã đóng `EXPIRED`/`CANCELLED`) chuyển hóa đơn PENDING sang `EXPIRED`, giữ nguyên trạng thái hóa đơn đã đóng, lưu `PaymentRef` định dạng ổn định `SEPAY:<id>`, lưu transaction trạng thái `MANUAL_REFUND_REQUIRED` và phát 1 sự kiện Outbox `InvoicePaid` (với `paidAt` là giờ ngân hàng, `occurredAt` là giờ xử lý) để Rental Service điều phối hoàn tiền thủ công.
  - `PaymentRef` có unique index để chặn hai hóa đơn dùng chung một mã giao dịch thanh toán.
  - Sự kiện `InvoicePaid` trong `outbox_messages` luôn có `correlationId` kiểu chuỗi bắt buộc (mặc định bằng `MessageId` dạng UUID).

*Lưu ý:* Tin nhắn Outbox (`InvoicePaid`) cho cả thanh toán đúng hạn và trễ hạn đã được lưu bền vững vào bảng `outbox_messages` trong cùng transaction, chưa được publish ra Kafka/broker trong vertical slice này. Webhook, publisher và thực hiện disburse refund nằm ngoài phạm vi này. Không hỗ trợ thẻ tín dụng, ví điện tử, thanh toán định kỳ, trả góp hoặc ngoại tệ.
