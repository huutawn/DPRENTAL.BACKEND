# PaymentService

Dịch vụ PaymentService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc Clean Architecture.

## Cấu trúc

```text
PaymentService/
  src/
    Common/          # Kiểu dùng chung, Result/Error, Utils (VndAmountValidator, VietQrUrlGenerator, PaymentCodeGenerator, PayloadFingerprinter)
    Domain/          # Entities, IRepository, Repository (EfInvoiceRepository), Persistence (DbContext, Factory), Migrations
    Application/     # DTOs, IServices, Services (InvoiceService, Creation, Lifecycle, InvoicePaymentProcessor), Background (InvoiceExpiryWorker), Options
    Api/             # Controllers, Filters, JWT Auth, HTTP composition root
  tests/             # Kiểm thử đơn vị và tích hợp (PostgreSQL 17)
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ và Git hooks
```

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
# Đặt POSTGRES_PASSWORD riêng trong .env; đổi POSTGRES_PORT nếu cổng đã được dùng.
docker compose up -d
```

PostgreSQL lắng nghe ở cổng cấu hình trong `.env`. File `.env` không được commit.

## Chạy API cục bộ

Nạp thông tin PostgreSQL từ `.env` vào môi trường của tiến trình API:

```bash
set -a
. ./.env
set +a
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
dotnet run --project PaymentService/src/Api/PaymentService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build PaymentService.sln
dotnet test PaymentService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/payment/develop`, rồi build và test trước khi push.

## Common và xử lý lỗi

Template có `Result`/`Result<T>` và `Error`/`ErrorType`.
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang 400/401/403/404/409/410/422/500. Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, trả 500 ProblemDetails kèm `traceId`,
không trả exception message hoặc payload; request bị hủy không chuyển thành lỗi nghiệp vụ.

## API Hóa đơn, QR & Webhook SePay (Payment Contract v1)

- `POST /api/v1/invoices`: Tạo hóa đơn từ items `RentalOrder` (tính tiền độc lập, không truy cập Rental DB). Tự động replay an toàn bằng `OperationKey` và snapshot fingerprint. Hóa đơn tổng 0 khởi tạo `PAID` kèm outbox message `InvoicePaid`.
- `POST /api/v1/invoices/{invoiceRef}/cancel`: Hủy hóa đơn chưa thanh toán. Trả về 409 `INVOICE_ALREADY_PAID` nếu đã thanh toán. Hóa đơn `EXPIRED` được xác nhận đóng và giữ nguyên trạng thái.
- `GET /api/v1/invoices/{invoiceRef}`: Trả về thông tin hóa đơn và ảnh VietQR SePay (`https://vietqr.app/img?acc=...&bank=...&amount=...&des=...&template=compact`). Chỉ renter sở hữu mới được xem (404 nếu không khớp hoặc không tìm thấy). `qrUrl` là null khi không ở trạng thái PENDING hoặc tổng 0.
- `POST /api/v1/payment/webhooks`: Tiếp nhận webhook chuyển khoản từ SePay với xác thực `Authorization: Apikey <key>` (constant-time). Controller mỏng chỉ làm model binding DTO `SePayWebhookPayload` và map ProblemDetails qua `ToProblem()`; Application (`InvoicePaymentProcessor`, `SePayTransactionNormalizer`) sở hữu toàn bộ use case validation, chuẩn hóa, deduplicate và matching transaction.

### Cấu hình SePay Webhook (`Payment:SePay`)

```json
"Payment": {
  "Merchant": {
    "Bank": "MBBank",
    "Account": "0383799632"
  },
  "SePay": {
    "ApiKey": "YOUR_SEPAY_API_KEY",
    "Gateway": "MBBank",
    "AccountNumber": "0383799632",
    "GatewayMapping": {
      "MBBank": "MBBank",
      "MB": "MBBank"
    }
  }
}
```

- **ApiKey:** Cấu hình từ secret/biến môi trường (`Payment__SePay__ApiKey`), bắt buộc kiểm tra khi khởi động ứng dụng (`ValidateOnStart`). Tuyệt đối không ghi API key, header Authorization hoặc raw payload vào log.
- **Gateway & GatewayMapping:** Ánh xạ tường minh giữa gateway từ SePay và ngân hàng merchant đã đăng ký. Không suy đoán hoặc match chuỗi mờ từ tên ngân hàng.
- **AccountNumber:** Tài khoản nhận tiền được so khớp chính xác với snapshot hóa đơn và merchant cấu hình.

### Định dạng Webhook JSON và Xử lý Giao dịch

SePay gửi webhook JSON với các trường: `id`, `gateway`, `transactionDate`, `accountNumber`, `subAccount`, `code`, `content`, `transferType`, `description`, `transferAmount`, `accumulated`, `referenceCode`.

- **Chuẩn hóa thời gian:** `transactionDate` theo giờ Việt Nam (UTC+7) được chuẩn hóa sang UTC. Giữ riêng thời điểm giao dịch ngân hàng (`TransactionDate`) và thời điểm hệ thống xử lý sau khi lấy khóa (`ProcessedAt`).
- **Phân loại giao dịch:**
  - `MATCHED`: Tiền vào (`transferType = "in"`), mã thanh toán khớp duy nhất, số tiền khớp đúng `TotalAmount`, tài khoản khớp cấu hình/snapshot, còn hạn -> Hóa đơn chuyển `PAID` và phát 1 sự kiện Outbox `InvoicePaid`.
  - `UNMATCHED`: Thiếu mã thanh toán hoặc mã hợp lệ nhưng không tìm thấy hóa đơn -> Lưu transaction `UNMATCHED`, không xác nhận hóa đơn.
  - `REVIEW_REQUIRED`: Mã mơ hồ/mâu thuẫn, sai số tiền, sai tài khoản nhận, giao dịch tiền ra (`transferType != "in"`), hoặc hóa đơn đã ghi nhận thanh toán trước đó -> Lưu transaction `REVIEW_REQUIRED`, không xác nhận hóa đơn.
  - `MANUAL_REFUND_REQUIRED`: Khớp đầy đủ nhưng giao dịch trễ hạn thanh toán (thời điểm chuyển khoản hoặc thời điểm xử lý sau khi lấy khóa đạt/vượt `PaymentDeadline`, hoặc hóa đơn đã `EXPIRED`/`CANCELLED`) -> Hóa đơn `PENDING` chuyển `EXPIRED`, hóa đơn đã đóng giữ nguyên; lưu `PaymentRef`/`PaidAt` và phát 1 Outbox `InvoicePaid` (với `paidAt` là giờ ngân hàng, `occurredAt` là giờ xử lý) để Rental Service điều phối hoàn tiền thủ công.
- **Idempotency & Khóa hàng:** Claim ID nhà cung cấp (`Provider = "SEPAY"`, `ProviderTransactionId = id`) và khóa hàng hóa đơn `FOR UPDATE` trong cùng một database transaction. Giao dịch duplicate cùng ID trả 200 `{"success": true}` đã lưu; trùng ID nhưng dữ liệu nghiệp vụ khác trả 409 Conflict.
- **Phạm vi tích hợp:** Kết quả tích hợp dừng ở commit Outbox atomic trong database. Kafka publisher, đối soát admin UI và thực hiện disburse refund nằm ngoài phạm vi này.
