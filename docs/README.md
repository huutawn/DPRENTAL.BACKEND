# Tài liệu PaymentService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility như `VndAmountValidator`, `VietQrUrlGenerator`, `PaymentCodeGenerator`, `PayloadFingerprinter`, crypto/signature primitives) | Application, Domain, HTTP pipeline |
| `Domain` | Data Entities (`Invoice`, `InvoiceItem`, `OperationReceipt`, `OutboxMessage`, `PaymentTransaction`, `RentalItemSettlementRecord`, `ManualRefundRequirement`, `TransactionJournalEntry`), `IRepositories`, `Repositories` (`EfInvoiceRepository`, `EfSettlementLedger`, `ManualRefundRepository`, `EfOutboxRepository`, `EfTransactionJournalRepository`, `UnitOfWork`), `DbContexts` (`PaymentServiceDbContext`, `IntegrityDbContext`, Factories), EF Configurations, Migrations (`Payment`, `Integrity`) | Application DTOs, HTTP pipeline, controller, business logic calculations |
| `Application` | DTOs, IServices, Use case services nhóm theo nghiệp vụ (`Invoices`, `Payments`, `Publishing`, `Refunds`, `Settlement`, `Integrity`), Background workers, Clients | Controller, HTTP pipeline, direct DbContext/EF/Npgsql access, private signing key |
| `IntegrityHost` | Composition root cho tiến trình Integrity, internal endpoints, adapter quản lý khóa ký (`IIntegritySigningAdapter`, `IntegrityKeyManager`) | Public Payment API endpoints, shared mutable state |
| `Api` | Routing, Controllers (`InvoicesController`, `SettlementsController`, `ManualRefundsController`, `PaymentWebhooksController`), Security (JWT/JWKS, SePayApiKey), Exception handlers, DI/composition root | Business logic trực tiếp, private signing key |

Hạ tầng persistence và unique indexes (`InvoiceRef`, `RentalOrderId`, `PaymentCode`, `PaymentRef`, `OperationKey`, `SettlementRef`) được quản lý bởi `PaymentServiceDbContext` trong `Domain/Repositories` và repositories trong `Domain/Repositories`, đăng ký qua `AddPaymentDomainPersistence`. Kho bằng chứng độc lập được quản lý bởi `IntegrityDbContext` và `EfIntegrityEvidenceRepository`, đăng ký qua `AddIntegrityEvidencePersistence`. Tầng `Application` và background workers đăng ký qua `AddPaymentApplication`.
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

## Hướng dẫn Nghiệp vụ Chi tiết & Project Guide

Xem tài liệu chính thức: **[Project Guide & Domain Invariants](PROJECT_GUIDE.md)** để biết chi tiết về:
- Ranh giới nghiệp vụ Rental–Payment, hợp đồng settlement v1 và hoàn cọc thủ công.
- Các trạng thái hóa đơn, số tiền VND và làm tròn phí sàn.
- Sổ cái giao dịch Merkle bất biến (Schema v4) và kiểm chứng tính toàn vẹn (Integrity Receipt v2).
- Kafka outbox publisher (`InvoicePaidOutboxPublisherWorker`) với proof v3 và tiến trình kiểm toán định kỳ (`IntegrityPeriodicScannerWorker`).
- Ranh giới an toàn cryptographic signing key và mô hình phân tách VPS hai database PostgreSQL.
