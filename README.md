# PaymentService

Dịch vụ PaymentService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc Clean Architecture.

## Cấu trúc

```text
PaymentService/
  src/
    Common/          # Kiểu dùng chung, Result/Error, Utils, Cryptography (AesGcm, CanonicalJournalHasher, EcdsaAttestation, SensitiveFieldEncryptor)
    Domain/          # Pure Data Entities, IRepositories contracts, DbContexts (PaymentServiceDbContext, IntegrityDbContext), EF Configurations, Migrations (Payment, Integrity)
    Application/     # Use cases, DTOs, Services theo nghiệp vụ (Invoices, Payments, Publishing, Refunds, Settlement, Integrity), Background workers, Clients
    IntegrityHost/   # Tiến trình độc lập, composition root, endpoints nội bộ, adapter quản lý khóa ký (IIntegritySigningAdapter, IntegrityKeyManager)
    Api/             # Routing, Controllers, Filters, JWT Auth, Public HTTP APIs
  tests/             # Kiểm thử đơn vị, tích hợp (PostgreSQL 17), tamper detection, load & benchmark (1M bản ghi)
```
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ và Git hooks

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
export InternalAuthentication__RentalApiKey="$PAYMENT_RENTAL_API_KEY"
export IntegrityHost__BaseUrl="http://127.0.0.1:8081"
export IntegrityHost__InternalApiKey="$PAYMENT_INTEGRITY_API_KEY"
export PaymentEncryption__ActiveKeyId="$PAYMENT_ENCRYPTION_KEY_ID"
export PaymentEncryption__DataEncryptionKeyBase64="$PAYMENT_DATA_ENCRYPTION_KEY_BASE64"
export Payment__SePay__ApiKey="$PAYMENT_SEPAY_API_KEY"
export Payment__Merchant__Bank="$PAYMENT_MERCHANT_BANK"
export Payment__Merchant__Account="$PAYMENT_MERCHANT_ACCOUNT"
dotnet run --project PaymentService/src/Api/PaymentService.Api.csproj
```

IntegrityHost runs as a separate process and requires the same Payment database, the integrity database,
`IntegrityHost__InternalApiKey`, `IntegrityHost__SigningKeyId`, and
`IntegrityHost__PrivateKeyBase64`. Supply persistent development keys through the environment; both
hosts intentionally fail at startup when required credentials or key material are missing. Do not use
sample or shared credentials outside a local disposable environment.

Trên PowerShell, chạy `./scripts/run-payment-api.ps1` để nạp `.env` và khởi động API tại `http://127.0.0.1:46280`. Script từ chối cấu hình trỏ tới database `rentalservice`.

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build PaymentService.sln
dotnet test PaymentService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/payment/develop`, rồi build và test trước khi push.

## CI/CD PaymentService cho dev

Workflow [paymentservice-dev.yml](.github/workflows/paymentservice-dev.yml) chạy khi push vào `payment/develop-ci-cd`. Giữ luồng của RentalService: job `publish` đẩy image lên GHCR, sau đó `deploy-dev` triển khai cùng revision qua SSH. Workflow không chạy cho pull request và không chạy test .NET; việc kiểm tra build, test và migrations nằm ở pre-push hook.

Mỗi push publish bốn image với full Git SHA:

```text
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-payment:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-payment-migrator:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-payment-integrity:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-payment-integrity-migrator:<git-sha>
```

Dockerfile tạo bundle cho `PaymentServiceDbContext` và `IntegrityDbContext`; từng migrator image chứa bundle cùng appsettings, không chứa SDK hoặc source. Runtime image chạy non-root: Payment API trên cổng `8080` với `/health/ready`, IntegrityHost trên cổng `8081` với `/health`.

Tạo GitHub Environment `dev`, khai báo biến `GHCR_IMAGE_NAMESPACE` và các secrets `DEV_VPS_HOST`, `DEV_VPS_USER`, `DEV_VPS_SSH_PRIVATE_KEY`, `DEV_VPS_KNOWN_HOSTS`; `DEV_VPS_PORT` là tùy chọn, mặc định `22`. Trên VPS, thư mục `/opt/dprental/dev` phải có `.env`, `compose.yaml` của DPRENTAL và `release.env` gồm đủ bốn biến `PAYMENT_SERVICE_IMAGE`, `PAYMENT_MIGRATOR_IMAGE`, `PAYMENT_INTEGRITY_HOST_IMAGE`, `PAYMENT_INTEGRITY_MIGRATOR_IMAGE`. Payment API, Payment migrator và kết nối Payment của IntegrityHost dùng database chung trên `postgres` với `Search Path=payment`; chỉ database bằng chứng Integrity dùng PostgreSQL riêng. Schema `payment` cần được tạo khi chuẩn bị database chung, trước lần chạy Payment migration đầu tiên. Script deploy đăng nhập GHCR bằng Docker config tạm, cập nhật tag SHA, chạy hai migration theo thứ tự, recreate IntegrityHost rồi Payment API, chờ healthcheck và kiểm tra Gateway readiness cùng JWKS nội bộ AuthService. Script không tự rollback database hoặc image nếu bước migration hay healthcheck thất bại.

Regression test deploy chạy bằng `bash scripts/tests/deploy-paymentservice.test.sh`.

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
  - `MANUAL_REFUND_REQUIRED`: Khớp đầy đủ nhưng giao dịch ngân hàng đến hạn hoặc muộn, hoặc hóa đơn đã `EXPIRED`/`CANCELLED`. Nếu webhook chỉ được xử lý sau deadline thì vẫn là thanh toán trễ, kể cả `transactionDate` sớm hơn deadline. Hóa đơn đã đóng không được mở lại. Payment ghi transaction/journal/outbox trong cùng transaction; Rental đăng ký refund qua API Payment và admin chuyển khoản ngoài hệ thống rồi xác nhận.
- **Idempotency & Khóa hàng:** Claim ID nhà cung cấp (`Provider = "SEPAY"`, `ProviderTransactionId = id`) và khóa hàng hóa đơn `FOR UPDATE` trong cùng một database transaction. Giao dịch duplicate cùng ID trả 200 `{"success": true}` đã lưu; trùng ID nhưng dữ liệu nghiệp vụ khác trả 409 Conflict.
- **Tích hợp Kafka:** `InvoicePaidOutboxPublisherWorker` publish `InvoicePaid`, `RefundCompleted` và `RentalSettled` sau khi journal được xác nhận. Header `x-dprental-proof` mang bằng chứng ECDSA bền vững, ràng buộc payload, topic, key, message/event/operation IDs và không hết hạn theo thời gian Kafka backlog.

### Settlement và refund

- `POST /api/v1/settlements`: ghi đủ ba ledger entries và journal `RentalSettled`; nếu cần hoàn cọc thì chờ admin hoàn tiền và xác nhận trước khi phát sự kiện settlement.
- `POST /api/v1/refunds`: Rental đăng ký khoản refund cần chuyển khoản thủ công. Đây là thao tác ghi idempotent theo `OperationKey`; endpoint không thực hiện chuyển tiền.
- `GET /api/v1/manual-refunds` và `GET /api/v1/manual-refunds/{id}`: admin truy vấn các yêu cầu cần xử lý.
- `POST /api/v1/manual-refunds/{id}/complete`: admin xác nhận chuyển khoản ngoài hệ thống bằng `ExternalTransferReference` và `OperationKey`. Payment phát `RefundCompleted` hoặc `RentalSettled` sau khi journal được xác nhận.
- Service-to-service ghi hóa đơn, settlement và refund dùng `X-Api-Key` riêng cho Rental→Payment. JWT vẫn bảo vệ endpoint khách hàng và thao tác admin.

## Lịch sử và kiểm chứng giao dịch Payment theo SOLID

### 1. Kiến trúc phân tầng và ranh giới quyền sở hữu
- **Domain:** Thuần túy chứa quy tắc nghiệp vụ, thực thể, value objects và interface repository (`IRepository`). Không phụ thuộc EF Core hay PostgreSQL.
- **Infrastructure:** Sở hữu `PaymentServiceDbContext`, các migration EF, mapping PostgreSQL và triển khai các repository.
- **Application:** Sở hữu use case nghiệp vụ, bộ kiểm tra toàn vẹn dùng chung (`ITransactionIntegrityChecker`) và các service chuyên biệt (`InvoiceCreationService`, `InvoiceLifecycleService`, `InvoicePaymentProcessor`, `SettlementService`, `ManualRefundService`). Đã loại bỏ hoàn toàn `InvoiceService` chuyển tiếp thừa.
- **IntegrityHost:** Chạy như một host độc lập, sở hữu cơ sở dữ liệu bằng chứng riêng (`IntegrityDbContext`), credential riêng và nắm giữ khóa riêng ECDSA P-256 (`nistP256`). PaymentService chỉ sở hữu public key qua cấu hình (`IntegrityVerification:PublicKeys`).

### 2. Sổ nhật ký giao dịch bất biến (Transaction Journal)
- Ghi nhận append-only cho mọi thay đổi tài chính trong cùng transaction nghiệp vụ:
  - Hóa đơn: `INVOICE_CREATED`, `INVOICE_CANCELLED`, `INVOICE_EXPIRED`
  - Ngân hàng: `BANK_INCOMING`, `BANK_OUTGOING`
  - Matching: `PAYMENT_MATCHED`, `PAYMENT_UNMATCHED`
  - Settlement & Hoàn tiền: `RENTAL_SETTLED`, `REFUND_REQUIRED`, `REFUND_CONFIRMED`
- Mỗi luồng thực thể (`StreamId`) có phiên bản tuần tự đơn điệu tăng dần (`Version`), `EventId`, `PreviousHash` (bắt đầu bằng `GENESIS`) và `PayloadHash` chuẩn hóa SHA-256 từ canonical payload.
- Dữ liệu nhạy cảm được mã hóa AES-256-GCM với nonce 12 byte và tag 16 byte riêng biệt.

### 3. Quy trình xác nhận và chống sửa đổi (IntegrityHost Pipeline)
1. **Commit:** Payment ghi nhận nghiệp vụ và journal entry nguyên tử (`IsConfirmed = false`).
2. **Xác nhận (IntegrityConfirmationWorker):** Chạy định kỳ (mặc định 250 ms, batch 100), xác minh chuỗi phiên bản, hash trước và hash nội dung, ký bằng chứng ECDSA P-256 và lưu mốc xác thực vào kho riêng (`integrity_stream_manifests`, `integrity_attestations`), sau đó cập nhật `ProofSignature` vào Payment DB.
3. **Rà soát định kỳ (IntegrityPeriodicScannerWorker):** Quét toàn bộ manifest để phát hiện xóa bản ghi, sửa đổi trực tiếp trong DB hoặc quay lui DB nghiệp vụ (`ROLLBACK_DETECTED`, `UNAUTHORIZED_EDIT`). Khi phát hiện sai lệch, tiến trình chặn stream ngay lập tức và ghi nhận incident, không tự ý sửa đổi hay hợp thức hóa.
4. **Kafka Outbox:** Publisher chỉ phát outbox khi nội dung khớp bằng chứng đã được IntegrityHost xác nhận; bổ sung bằng chứng xác thực qua Kafka headers (`x-dprental-signature`, `x-dprental-key-id`, `x-dprental-proof`, `x-dprental-stream-id`, `x-dprental-stream-version`, `x-dprental-event-id`).

### 4. API Kiểm chứng & Lịch sử
- `POST /api/v1/payment/verifications`: Yêu cầu Service JWT có scope `payment.transaction.verify`.
  - Quy ước status code:
    - `200 OK`: Kiểm tra tính toàn vẹn thành công (kể cả khi không đủ điều kiện nghiệp vụ như lệch số tiền).
    - `202 Accepted`: Giao dịch đang chờ IntegrityHost xác nhận (`PENDING_CONFIRMATION`).
    - `409 Conflict`: Phát hiện dữ liệu bị chỉnh sửa trái phép, hash không khớp hoặc chữ ký sai (`TAMPERED`).
    - `503 Service Unavailable`: Vượt quá ngân sách thời gian xử lý server (mặc định 900 ms).
- `GET /api/v1/payment/history` (hoặc `/journals`): Yêu cầu Service JWT có scope `payment.transaction.history` hoặc Admin token. Phân trang cursor an toàn, không lộ dữ liệu ngân hàng nhạy cảm mặc định.

### 5. Hiệu năng & Kiểm thử
- Đạt p99 < 1 giây tại tải 100 check/giây trên kho 1.000.000 bản ghi có đồng thời ghi và worker chạy song song (được kiểm chứng qua `TransactionIntegrityBenchmarkTests`).
- 100% test suite đạt chuẩn (232 tests bao phủ unit, integration PostgreSQL thật, tamper detection, và pre-push hook).
