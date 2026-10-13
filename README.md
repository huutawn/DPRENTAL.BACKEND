# PaymentService

Dịch vụ PaymentService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc Clean Architecture.

## Cấu trúc

```text
PaymentService/
  src/
    Common/          # Kiểu dùng chung, Result/Error, Utils (VndAmountValidator, VietQrUrlGenerator, PaymentCodeGenerator, PayloadFingerprinter)
    Domain/          # Entities, IRepository, Repository (EF Core DbContext, EfInvoiceRepository), Migrations
    Application/     # DTOs, IServices, Services (InvoiceService, Creation, Lifecycle, Recording), Background (InvoiceExpiryWorker), Options
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

## API Hóa đơn & QR (Payment Contract v1)

- `POST /api/v1/invoices`: Tạo hóa đơn từ items `RentalOrder` (tính tiền độc lập, không truy cập Rental DB). Tự động replay an toàn bằng `OperationKey` và snapshot fingerprint. Hóa đơn tổng 0 khởi tạo `PAID` kèm outbox message `InvoicePaid`.
- `POST /api/v1/invoices/{invoiceRef}/cancel`: Hủy hóa đơn chưa thanh toán. Trả về 409 `INVOICE_ALREADY_PAID` nếu đã thanh toán. Hóa đơn `EXPIRED` được xác nhận đóng và giữ nguyên trạng thái.
- `GET /api/v1/invoices/{invoiceRef}`: Trả về thông tin hóa đơn và ảnh VietQR SePay (`https://vietqr.app/img?acc=...&bank=...&amount=...&des=...&template=compact`). Chỉ renter sở hữu mới được xem (404 nếu không khớp hoặc không tìm thấy). `qrUrl` là null khi không ở trạng thái PENDING hoặc tổng 0.
- **Thiết kế Entity & Invariants Runtime:**
  - Các entity trong `Domain` (`Invoice`, `InvoiceItem`, `OperationReceipt`, `OutboxMessage`) được thiết kế theo hướng `DataAnnotations` / `DataAnnotations.Schema` attributes (`[Table]`, `[Key]`, `[Required]`, `[MaxLength]`, `[Column(TypeName = "numeric(18,0)")]`, `[ForeignKey]`, `[InverseProperty]`) thay vì các lớp `IEntityTypeConfiguration`.
  - Mọi `DateTimeOffset` lưu trữ đều chuẩn hóa về UTC (`Offset = 0`).
  - `GET` kiểm tra quyền sở hữu bằng `AsNoTracking()`; mọi cập nhật trạng thái (`Cancel`, `RecordPayment`, `Expiry`) đều lấy khóa hàng PostgreSQL `FOR UPDATE` và `ReloadAsync()` từ database trước khi chuyển trạng thái.
  - `PaymentRef` có unique index nullable (`IX_invoices_PaymentRef`) chống thanh toán trùng giữa hai hóa đơn.
  - Sự kiện `InvoicePaid` trong `outbox_messages` luôn có `correlationId` kiểu chuỗi bắt buộc (mặc định bằng `MessageId`).

*Ghi chú:*
Outbox message được lưu bền vững vào bảng `outbox_messages` trong cùng database transaction, chưa được publish ra Kafka trong vertical slice này. Webhook và xử lý chuyển tiền đến thuộc phạm vi tích hợp tiếp theo. Không triển khai refund, card, wallet, recurring, installment hoặc multi-currency.
