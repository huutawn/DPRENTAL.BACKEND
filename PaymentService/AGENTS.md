# Quy Tắc Kỹ Thuật Chung Cho Developer & Coding Agent — PaymentService

> **Ngữ cảnh nghiệp vụ & An toàn tài chính**:
> PaymentService là dịch vụ trung tâm xử lý thanh toán, hóa đơn, sổ cái tài chính (financial ledger), kiểm chứng tính toàn vẹn (cryptographic audit integrity) và hoàn cọc cho hệ sinh thái DPRENTAL.
> Trước khi thực hiện thay đổi liên quan đến domain, invariant, tính toán số tiền, trạng thái giao dịch hoặc chữ ký toàn vẹn, bắt buộc phải đọc kỹ tài liệu này và requirements/acceptance criteria của task được giao.

---

## 1. Bản Đồ Codebase & Ranh Giới Phân Tầng

Codebase tuân thủ cấu trúc phân tầng sạch (Clean Architecture) tương đương RentalService, phản ánh đúng 5 thành phần cốt lõi:

| Thành phần | Đường dẫn | Trách nhiệm chính | Điều cấm kỵ |
|---|---|---|---|
| **Common** | `src/Common/` | `Result`/`Error`, tiện ích ổn định (`VndAmountValidator`, `VietQrUrlGenerator`, `PaymentCodeGenerator`, `PayloadFingerprinter`), primitives mã hóa & chữ ký (`AesGcm`, `CanonicalJournalHasher`, `EcdsaAttestation`, `SensitiveFieldEncryptor`). | Không phụ thuộc vào bất kỳ tầng nào khác (Domain, Application, Api). |
| **Domain** | `src/Domain/` | Dữ liệu thuần túy (Pure Data Entities), Repository Contracts (`IRepositories`), Triển khai Repository (`Repositories`), DbContexts (`PaymentServiceDbContext`, `IntegrityDbContext`, Factories, Interceptors), EF Core Configurations, và Migrations (`Migrations/Payment`, `Migrations/Integrity`). | Không chứa logic nghiệp vụ, tính toán tiền, workflow, quyền hạn hoặc quyết định chuyển trạng thái. Không phụ thuộc Application hay Api. |
| **Application** | `src/Application/` | Trung tâm điều phối nghiệp vụ: Use Cases, DTOs, Services nhóm theo nghiệp vụ miền (`Invoices`, `Payments`, `Publishing`, `Refunds`, `Settlement`, `Integrity`), Background Workers (`InvoiceExpiryWorker`, `InvoicePaidOutboxPublisherWorker`), Clients tích hợp (`IntegrityHostClient`, `IInvoicePaidKafkaProducer`), Options. | Không chứa transport logic (HTTP controllers, headers), không truy cập trực tiếp `DbContext` hay native driver, không sở hữu adapter khóa riêng của Integrity. |
| **IntegrityHost** | `src/IntegrityHost/` | Tiến trình host độc lập, composition root cho kiểm chứng toàn vẹn, internal endpoints (`/internal/v1/integrity/...`), adapter quản lý khóa ký riêng (`IIntegritySigningAdapter`, `IntegrityKeyManager`). | Không chứa public Payment API endpoints; không chia sẻ bộ nhớ mutable hoặc khóa riêng với Payment API. |
| **Api** | `src/Api/` | Routing, Controllers (`InvoicesController`, `SettlementsController`, `ManualRefundsController`, `PaymentWebhooksController`, `PaymentVerificationsController`), Middleware, JWT/JWKS Authentication, ánh xạ lỗi ProblemDetails (`ToProblem()`). | Không chứa quyết định nghiệp vụ hoặc câu lệnh truy vấn database trực tiếp. Không đăng ký adapter khóa riêng. |

---

## 2. Nguyên Tắc Thay Đổi Mã Nguồn

- **Đọc và hiểu trước khi sửa**: Luôn đọc kỹ implementation hiện tại, luồng gọi (callers), ràng buộc dữ liệu và các integration tests liên quan trước khi chỉnh sửa.
- **Tái sử dụng và đơn giản hóa**: Ưu tiên tận dụng các abstraction, helper, mật mã primitives và repository contracts sẵn có; giữ thiết kế thực dụng, không tạo abstraction phỏng đoán tương lai.
- **Một nguồn chân lý duy nhất (Single Source of Truth)**: Mỗi hành vi nghiệp vụ chỉ có duy nhất một implementation chính thức tại tầng Application; không tạo giải pháp song song gây phân mảnh logic.
- **Dọn dẹp triệt để khi thay thế**: Khi chuyển đổi hoặc nâng cấp logic, phải cập nhật toàn bộ callers, tests, migrations và xóa bỏ hoàn toàn đường code cũ/obsolete trong cùng đợt; không để lại shims, aliases tạm thời hay mã thừa.
- **Đúng phạm vi task (Scoped Implementation)**: Tập trung giải quyết trọn vẹn yêu cầu và acceptance criteria của task; tuyệt đối không tự ý mở rộng phạm vi ngoài yêu cầu.

---

## 3. Quyền Sở Hữu Nghiệp Vụ & Thiết Kế SOLID

- **SRP (Single Responsibility)**:
  - **Application Service**: Là nơi duy nhất sở hữu và điều phối toàn bộ quyết định nghiệp vụ: xác thực quyền và ownership, kiểm tra invariants, tính toán số tiền/phí/hoàn cọc, quyết định ledger entries, điều phối state transitions, xử lý idempotency/replay, điều phối transaction qua `IUnitOfWork` và phát outbox messages.
  - **Domain Entity**: Chỉ lưu giữ trạng thái dữ liệu, cấu trúc bảng, quan hệ (relationships, foreign keys), mapping ORM, audit revisions và các thao tác gán trạng thái (`Assign...`) đã được Application Service quyết định. Tuyệt đối không giữ bản tính toán, công thức tài chính hoặc validation thứ hai trong Entity.
  - **Repository**: Chỉ thực hiện truy vấn, khóa dữ liệu (`FOR UPDATE`), lưu trữ persistence và báo lỗi persistence (`DbUpdateException`). Không tự ý quyết định replay, không tự động sinh ledger entries hay outbox messages.
  - **API Controllers & Background Workers**: Chỉ xử lý giao tiếp (transport): routing, model binding, xác thực danh tính, bắt lỗi và map kết quả trả về (`ToProblem`); chuyển tiếp hoàn toàn xử lý sang Application Services tương ứng.
- **OCP & LSP**: Các implementation phải tuân thủ nghiêm ngặt contracts của abstraction (kiểu trả về, mã lỗi chuẩn, hành vi biên); không giả vờ thành công hoặc âm thầm nuốt lỗi persistence.
- **ISP**: Interface nhỏ gọn, thiết kế theo nhu cầu thực tế của use cases; phân tách rõ ràng contracts repository (`IInvoiceRepository`, `ISettlementLedger`, `IManualRefundRepository`, `ITransactionJournalRepository`, `IIntegrityEvidenceRepository`).
- **DIP**: Các tầng cấp cao phụ thuộc vào abstraction qua Dependency Injection. Domain hoàn toàn độc lập, không tham chiếu ngược lại Application hay API.

---

## 4. Truy Cập Dữ Liệu & ORM (LINQ / EF Core)

- **Server-side Evaluation**: Mọi điều kiện lọc (`Where`), sắp xếp (`OrderBy`), gom nhóm (`GroupBy`), đếm (`CountAsync`) và tổng hợp dữ liệu phải được biên dịch và thực thi trực tiếp tại database engine PostgreSQL.
- **Chiếu dữ liệu (Projection)**: Luôn dùng `.Select()` để chỉ tải về các trường dữ liệu cần thiết cho DTO/Record, giảm tải bộ nhớ và băng thông.
- **Truy vấn chỉ đọc**: Bắt buộc sử dụng `.AsNoTracking()` cho toàn bộ các truy vấn đọc hoặc kiểm tra điều kiện không sửa đổi trạng thái.
- **Phân trang & Bất đồng bộ**: Mọi truy vấn tập dữ liệu lớn (như outbox messages, journal pending confirmation, scanner manifests) phải có phân trang hoặc giới hạn kích thước lô (`batchSize`); luôn thực thi async (`ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`) và truyền `CancellationToken`.
- **Triệt tiêu N+1**: Sử dụng eager loading vừa đủ (`Include`/`ThenInclude`) hoặc projection phẳng; tuyệt đối không tải dữ liệu vào bộ nhớ (`ToList()`, `AsEnumerable()`) rồi mới lọc hay đếm.
- **Không làm rò rỉ IQueryable**: Repository contract tuyệt đối không trả về `IQueryable<T>`; bắt buộc trả về kiểu dữ liệu cụ thể (`Task<T?>`, `Task<IReadOnlyList<T>>`, `Task<Result<T>>`).
- **Tuân thủ ranh giới Persistence**: Service, Controller, Worker và Consumer tuyệt đối không truy cập `DbContext` trực tiếp, không ép kiểu abstraction thành `DbContext` và không tự khởi tạo concrete repository bằng `new`. Mọi tương tác database phải thông qua abstraction persistence được inject qua DI container.

---

## 5. Quy Chuẩn Sử Dụng Native SQL

- **Ngoại lệ hẹp có căn cứ**: Chỉ sử dụng Native SQL khi LINQ/EF Core không có API tương đương cho các tính năng đặc thù PostgreSQL đã được kiểm chứng:
  - Khóa hàng cạnh tranh: `SELECT ... FOR UPDATE` (thông qua `FromSqlInterpolated`).
  - Khóa phân tán cấp giao dịch: Advisory xact lock `SELECT pg_advisory_xact_lock(hashtext({streamId}))`.
- **An toàn SQL Injection tuyệt đối**: 100% tham số đầu vào phải được parameterize thông qua `FromSqlInterpolated` hoặc `ExecuteSqlInterpolatedAsync` của EF Core; tuyệt đối không cộng chuỗi SQL thô (`FromSqlRaw` với chuỗi ghép).
- **Không chứa quyết định nghiệp vụ trong SQL**: SQL chỉ thực hiện khóa kỹ thuật hoặc truy vấn dữ liệu; toàn bộ validation, kiểm tra quyền hạn, deadline và công thức tính toán tài chính phải nằm tại Application Service.
- **Bắt buộc có Integration Test**: Mọi câu lệnh Native SQL phải có integration test chạy thực tế với PostgreSQL để kiểm chứng cú pháp, hành vi lock và khả năng rollback.

---

## 6. Quy Tắc Riêng Biệt Cho PaymentService (Domain Invariants & Financial Safety)

### 6.1. Tiền Tệ & Định Dạng VND
- Hệ thống thanh toán chỉ hỗ trợ tiền tệ Việt Nam Đồng (`VND`).
- Giá trị số tiền là **số nguyên không âm** (không có phần thập phân/hào xu). Kiểm tra bắt buộc qua `VndAmountValidator.ValidateVndAmount`.
- Tỷ lệ phí nền tảng mặc định là `10%` (`PlatformFeeRate = 0.10m`).
- Tính toán phí làm tròn tới VND gần nhất theo quy tắc làm tròn nửa lên: `Math.Round(amount, 0, MidpointRounding.AwayFromZero)`.
- Phí nền tảng chỉ tính trên tiền thuê (`RentalAmount`), tuyệt đối không thu phí trên tiền cọc (`DepositAmount`).

### 6.2. Webhook SePay & Chuẩn Hóa Giao Dịch
- Xác thực Webhook bằng `Authorization: Apikey <key>` với cơ chế so sánh thời gian hằng số (`CryptographicOperations.FixedTimeEquals`) để chống tấn công timing.
- Chuẩn hóa dữ liệu đầu vào qua `SePayTransactionNormalizer` và `SePayDateTimeParser`.
- Mã thanh toán (`PaymentCode`) được trích xuất từ nội dung chuyển khoản theo tiền tố cấu hình (`DPR...`).
- Xử lý hai nhánh giao dịch:
  - **Thanh toán đúng hạn** (thời điểm giao dịch ngân hàng < `PaymentDeadline`): Hóa đơn chuyển trạng thái `PAID`, gán `PaymentRef`, ghi nhận 1 journal `PaymentMatched`, phát 1 sự kiện Outbox `InvoicePaid`.
  - **Thanh toán trễ**: Giao dịch được phân loại trễ nếu webhook được xử lý từ deadline trở đi (sau khi lấy khóa hàng), giao dịch ngân hàng từ deadline trở đi, hoặc hóa đơn đã `EXPIRED`/`CANCELLED`. Webhook không tự mở lại invoice; chỉ command recovery hợp lệ từ Rental mới được chấp nhận trong 15 phút (giữ điều kiện không có refund reservation và kiểm tra Integrity). Ghi transaction `MANUAL_REFUND_REQUIRED`, gán `PaymentRef`, journal và Outbox `InvoicePaid` cùng transaction để Rental đăng ký refund.
- Deduplication: Kiểm tra trùng lặp trên cặp `(Provider, ProviderTransactionId)` và unique index trên `PaymentRef`.

### 6.3. Snapshot Hóa Đơn & Quyết Định Settlement
- PaymentService hoàn toàn độc lập với Rental database; không truy vấn trực tiếp bảng của Rental.
- Mọi thông tin đơn hàng được snapshot cố định tại thời điểm tạo hóa đơn (`InvoiceItem`: `RentalItemId`, `SupplierId`, `RentalAmount`, `DepositAmount`).
- Khi tiếp nhận yêu cầu settlement (`POST /api/v1/settlements`), Application Service đối chiếu nghiêm ngặt dữ liệu request với snapshot `InvoiceItem` của hóa đơn đã thanh toán.
- Công thức settlement:
  - `DepositRefundDue = DepositAmount - FinalDeductionAmount` (với `0 <= FinalDeductionAmount <= DepositAmount`).
  - `PlatformFeeAmount = Round(RentalAmount * 0.10, 0, AwayFromZero)`.
  - `SupplierPayable = RentalAmount - PlatformFeeAmount + FinalDeductionAmount`.
- Ghi nhận đầy đủ 3 bút toán sổ cái (`SettlementLedgerEntry`): `SupplierPayable`, `PlatformFee`, `DepositRefundDue`.

### 6.4. Hoàn Tiền Thủ Công (Manual Refund)
- Nếu `DepositRefundDue > 0`: Trạng thái settlement là `PENDING_MANUAL_REFUND`. Hệ thống tạo một bản ghi `ManualRefundRequirement` với mã `MRF-{id:N}`, gắn `RentalItemId`, `RenterId`, `PaymentRef`, `Amount`.
- Quá trình chuyển khoản hoàn tiền được thực hiện thủ công ngoài hệ thống bởi nhân viên quản trị (Admin).
- Sau khi chuyển khoản, Admin xác nhận qua `POST /api/v1/refunds/{id}/confirm` kèm `ExternalTransferReference` và danh tính Admin (`CompletedBy`).
- Sự kiện Kafka `RentalSettled` **chỉ được phép phát** khi:
  - Settlement không có tiền hoàn cọc (`DepositRefundDue == 0`), HOẶC
  - Yêu cầu hoàn cọc thủ công đã được xác nhận hoàn tất thành công (`ManualRefundStatus.Completed`).

### 6.5. Sổ Cái Giao Dịch Bất Biến (Immutable Transaction Journal)
- Mọi biến động tài chính cốt lõi (`PaymentMatched`, `RentalSettled`, `RefundRequired`, `RefundConfirmed`) bắt buộc ghi nhận vào `transaction_journal`.
- Payload tài chính được chuẩn hóa theo chuẩn JSON kinh điển (Canonical JSON) và mã hóa AES-GCM-256 với AAD ràng buộc chặt chẽ theo `(schemaVersion, environment, streamId, version, eventId)`.
- Ràng buộc băm chuỗi Merkle: Mỗi bản ghi chứa `PreviousHash` trỏ tới `EntryHash` của bản ghi liền trước (bản ghi đầu tiên trỏ tới `GenesisHash`).
- Bản ghi sổ cái sau khi ghi nhận là **bất biến (append-only)**: Không bao giờ sửa đổi hoặc xóa; mọi cập nhật chỉ thông qua chữ ký chứng thực (`ProofSignature`, `ConfirmedAt`).

### 6.6. Sự Kiện Outbox Mang Bằng Chứng (Proof-Carrying Outbox)
- Bản ghi `outbox_messages` được lưu trữ trong cùng transaction cơ sở dữ liệu với nghiệp vụ tài chính.
- Outbox message mang các metadata liên kết bằng chứng: `attestation_event_id`, `attestation_operation_id`, `correlation_id`, `causation_id`.
- Tiến trình `InvoicePaidOutboxPublisherWorker` chỉ thực hiện đọc, claim lease an toàn cạnh tranh, publish tới Kafka broker và xóa/đánh dấu đã phát. Không thực hiện tính toán nghiệp vụ trong outbox worker.

### 6.7. Ranh Giới Khóa Ký & Phân Tách Tiến Trình IntegrityHost
- `IntegrityHost` là tiến trình riêng biệt, lắng nghe trên cổng nội bộ riêng (mặc định `8081`), kết nối database bằng chứng riêng (`IntegrityDbContext` trên database `integrity_evidence`).
- Khóa riêng ECDSA P-256 được nạp và quản lý độc quyền tại `IntegrityHost` thông qua `IIntegritySigningAdapter` / `IntegrityKeyManager`.
- **Ranh giới an toàn**: Payment API tuyệt đối **không được nạp private key**, không đăng ký `IIntegritySigningAdapter` và không sở hữu năng lực tạo chữ ký chứng thực. Payment API chỉ sử dụng public key để xác minh tính hợp lệ của receipt trả về từ `IntegrityHostClient`.
- Mọi tương tác kiểm chứng giữa API và IntegrityHost diễn ra qua hợp đồng HTTP nội bộ (`/internal/v1/integrity/verify`).

---

## 7. Giao Dịch, Cạnh Tranh & Tính Bất Khả Biến (Idempotency)

- **Đơn vị công việc (Unit of Work)**:
  - Quản lý transaction thông qua abstraction `IUnitOfWork` tại tầng Domain.
  - Hàm `BeginTransactionAsync` trả về abstraction `IExecutionTransaction`, tuyệt đối không để lộ `IDbContextTransaction` ra tầng Application.
  - Transaction Payment và Transaction evidence store hoàn toàn độc lập, đảm bảo khả năng retry và thứ tự xác nhận bất đồng bộ.
- **Biên nhận thao tác (OperationReceipt) & Idempotent Replay**:
  - Mọi thao tác ghi nhạy cảm (`CreateInvoice`, `CancelInvoice`, `SettleRentalItem`, `ConfirmManualRefund`) bắt buộc yêu cầu `OperationKey` từ client.
  - Khi phát hiện trùng `OperationKey`:
    - Nếu payload fingerprint khớp: Trả về kết quả trước đó với cờ `IsReplay = true` (HTTP 200 OK).
    - Nếu payload fingerprint khác: Báo lỗi xung đột `Error.Conflict("DUPLICATE_OPERATION_KEY", ...)`.
- **Khóa hàng (Row-level Locks)**:
  - Khi cập nhật hóa đơn hoặc xác nhận hoàn cọc, bắt buộc thực thi khóa hàng `FOR UPDATE` trong giao dịch để bảo vệ chống race conditions và double-spending.

---

## 8. Bảo Mật & Bảo Vệ Dữ Liệu Nhạy Cảm

- **Mã Hóa Cột Nhạy Cảm**: Số tài khoản ngân hàng (`AccountNumber`), tiểu tài khoản (`SubAccount`) và nội dung chuyển khoản (`Content`) của `PaymentTransaction` bắt buộc phải được mã hóa tại tầng lưu trữ thông qua `SensitiveFieldEncryptor` và `IPaymentDataEncryptor`.
- **Bảo Vệ Log**: Tuyệt đối không ghi log số tài khoản ngân hàng đầy đủ, khóa bí mật, private key base64, API keys hoặc webhook raw payloads chứa thông tin tài chính nhạy cảm.
- **Phân Quyền API**:
  - Service-to-service endpoints (`CreateInvoice`, `CancelInvoice`, `Settlements`): Yêu cầu token định danh service (`u=svc`) kèm scopes tương ứng (`payment.invoice.write`, `payment.settlement.write`).
  - Customer endpoints (`GetInvoice`): Kiểm tra quyền sở hữu IDOR (`sub == RenterId`).
  - Admin endpoints (`GET /api/v1/manual-refunds`, `POST .../{id}/complete`, `POST /api/v1/admin/payments/unmatched/{id}/resolve`): Yêu cầu JWT client quản trị viên (role admin) và danh tính từ `sub` (không dùng service token, renter hoặc supplier).

---

## 9. Kiểm Chứng & Báo Cáo

- **Kiểm Thử Theo Hành Vi**:
  - Unit test: Toàn bộ công thức tài chính, làm tròn VND, cryptographic hash chain, và validators tại tầng Application/Common.
  - Integration test: Kiểm thử với cơ sở dữ liệu PostgreSQL 17 thật cho các luồng khóa hàng, concurrency, transaction rollback, unique constraint violation, EF migrations và chữ ký attestation.
- **Quy Tắc Thực Thi Trước Khi Nghiệm Thu**:
  - Bắt buộc thực hiện `dotnet build` đạt 0 Error, 0 Warning.
  - Bắt buộc chạy toàn bộ test suite (`dotnet test`) đạt 100% Pass.
  - Bắt buộc kiểm tra pending migrations cho cả hai context:
    - `dotnet ef migrations has-pending-model-changes --context PaymentServiceDbContext`
    - `dotnet ef migrations has-pending-model-changes --context IntegrityDbContext`
  - Bắt buộc chạy script kiểm tra định dạng và pre-push: `./scripts/tests/pre-push-check.test.sh` và `git diff --check`.
- **Báo Cáo Minh Bạch**: Nêu rõ các kịch bản kiểm tra đã chạy, bằng chứng kết quả thực tế, và các điều kiện môi trường liên quan.
