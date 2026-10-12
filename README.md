# RentalService

Dịch vụ RentalService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
RentalService/
  src/
    Common/          # Kiểu dùng chung, Result/Error và utility
    Domain/          # Entities, repository contracts và EF Core persistence
    Application/     # DTOs, services, mapping
    Api/             # Controllers, Filters, HTTP composition root
  tests/             # Kiểm thử
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
dotnet run --project RentalService/src/Api/RentalService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build RentalService.sln
dotnet test RentalService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/rental/develop`, rồi build và test trước khi push.

## Docker artifact

Dockerfile ở repository root có hai target cho deploy, đều dùng cùng source revision:

```bash
# Image runtime của RentalService
docker build --target runtime -t ghcr.io/<org>/dprental-rental:<git-sha> .

# Image chạy EF Core migration một lần trước khi khởi động API
docker build --target migrator -t ghcr.io/<org>/dprental-rental-migrator:<git-sha> .
```

Runtime lắng nghe cổng `8080`, chạy non-root UID `1654` và cung cấp `/health/live` cùng `/health/ready`. Readiness kiểm tra kết nối PostgreSQL. Image migrator chứa EF Core migration bundle và các file `appsettings*.json`, không chứa SDK, source code hay `dotnet-ef`. Bundle nhận cấu hình môi trường và connection string từ Compose như migrator trước đó; không đưa secret vào image.

## CI/CD RentalService cho dev

Workflow [rentalservice-dev.yml](.github/workflows/rentalservice-dev.yml) chỉ chạy khi có push vào `rental/develop-ci-cd`; nó không chạy cho pull request và không kiểm tra branch nền hoặc chạy test. Job publish build và đẩy hai Docker image lên GHCR một lần, sau đó job deploy chạy trên VPS. Kiểm tra build và test .NET vẫn có trong [pre-push hook](scripts/pre-push-check.sh) ở máy lập trình viên, nhưng hook này có thể bị bỏ qua và không thay thế CI.

Mỗi push sẽ publish hai image GHCR bất biến bằng full Git SHA:

```text
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-rental:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-rental-migrator:<git-sha>
```

Thiết lập GitHub Environment variable `GHCR_IMAGE_NAMESPACE` bằng namespace GHCR viết thường (ví dụ `huutawn`). Workflow gắn OCI source metadata để package mới tự liên kết repository. Với package đã từng được push thủ công, vào từng package GHCR và cấp repository workflow quyền **Write** trong phần Actions access trước lần CI đầu tiên. Tạo GitHub Environment tên `dev` với các secrets sau; không đặt PostgreSQL password hoặc nội dung `.env` vào GitHub:

```text
DEV_VPS_HOST              # IP hoặc hostname VPS
DEV_VPS_USER              # user deploy thuộc group docker
DEV_VPS_SSH_PRIVATE_KEY   # private key riêng của user deploy
DEV_VPS_KNOWN_HOSTS       # dòng host key theo định dạng known_hosts
DEV_VPS_PORT              # tùy chọn, mặc định 22
```

Workflow nạp deploy key vào `ssh-agent` qua stdin và kiểm tra host key đã lưu trong `DEV_VPS_KNOWN_HOSTS`. Nó truyền `GITHUB_TOKEN` cùng deploy script qua stdin của kết nối SSH, không đặt token trong câu lệnh SSH. Trên VPS, script đăng nhập GHCR bằng Docker config tạm rồi thực thi [deploy-rentalservice.sh](scripts/deploy-rentalservice.sh) từ commit đang phát hành. Docker credential tạm bị xóa ngay khi script kết thúc; user SSH của workflow cần quyền đọc `.env` và ghi `release.env`. `.env` vẫn chỉ tồn tại trên VPS. `release.env` phải đã có hai image RentalService với tag để script thay bằng SHA mới. Script pull hai image bằng một lệnh Compose, chạy migration một lần, chỉ recreate `rental-service`, chờ healthcheck, rồi gọi `/health/ready` qua `DEV_DOMAIN` và kiểm tra JWKS trực tiếp trong `auth-service` container. Gateway không public JWKS; Gateway tự tải JWKS qua Docker network nội bộ. Script không tự động rollback database hoặc image nếu migration hay healthcheck thất bại.

Ví dụ cấp quyền ghi `release.env` cho user GitHub Actions trên VPS (thay `deploy` bằng user thực tế):

```bash
sudo chown deploy:deploy /opt/dprental/dev/release.env
sudo chmod 600 /opt/dprental/dev/release.env
```

User `deploy` phải thuộc group `docker`; quyền Docker tương đương quyền quản trị VPS nên deploy key chỉ dùng cho automation này. Khóa host SSH có thể lấy từ máy tin cậy bằng `ssh-keyscan -H <vps-host>` rồi lưu nguyên kết quả vào `DEV_VPS_KNOWN_HOSTS`. Nếu chỉ muốn triển khai sau khi merge PR, cấu hình branch protection/ruleset cho `rental/develop-ci-cd` để yêu cầu PR và chặn push trực tiếp; event `push` tự nó không bảo đảm commit đến từ PR.

### Điều kiện triển khai VPS (Prerequisites)

Đường dẫn triển khai trên VPS giữ nguyên `/opt/dprental/dev` và toàn bộ danh sách secrets môi trường như AuthService. Trước khi kích hoạt CI/CD RentalService trên VPS, môi trường dev cần hoàn tất các điều kiện sau:

1. **Cấu hình Compose (`compose.yaml`)**: Hiện tại Compose dev chung tại VPS chưa định nghĩa hai service `rental-service` và `rental-migrate`. Cần bổ sung định nghĩa cho `rental-service` (cùng network services, phụ thuộc postgres và valkey/kafka, trỏ image `${RENTAL_SERVICE_IMAGE}`) và `rental-migrate` (profile `migration`, trỏ image `${RENTAL_MIGRATOR_IMAGE}`).
2. **Cấu hình môi trường (`.env`)**: Bổ sung đầy đủ các biến môi trường cho RentalService (ConnectionStrings__Postgres, Kafka, JWT validation options, v.v.).
3. **Khai báo image trong `release.env`**: File `/opt/dprental/dev/release.env` phải có sẵn hai dòng cấu hình image ban đầu kèm tag hợp lệ (để script cập nhật SHA mới):
   ```text
   RENTAL_SERVICE_IMAGE=ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-rental:initial
   RENTAL_MIGRATOR_IMAGE=ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-rental-migrator:initial
   ```

## Common và xử lý lỗi

Template có `Result`/`Result<T>` và `Error`/`ErrorType` (tham khảo AuthService).
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang 400/401/403/404/409/410/422/500. Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, trả 500 ProblemDetails kèm `traceId`,
không trả exception message hoặc payload; request bị hủy không chuyển thành lỗi nghiệp vụ.

`Domain/IRepositories/IRepo<TEntity, TKey>` là contract CRUD có CancellationToken,
không phụ thuộc EF/HTTP. Repository theo aggregate chỉ kế thừa khi cần đủ các thao tác;
nếu không, định nghĩa contract nhỏ theo use case. Implementation persistence ở
`Domain/Repositories` theo bố cục hiện tại của AuthService; không sinh generic EF
repository hoặc entity nghiệp vụ giả. `GetAllAsync` chỉ dùng cho tập dữ liệu nhỏ;
với dữ liệu lớn, contract chuyên biệt phải hỗ trợ phân trang.

Template test kiểm tra Result invariant, ánh xạ error và exception qua HTTP ở
Production, gồm fallback khi client không nhận JSON. Không sao chép mã lỗi,
constraint database hay dependency đặc thù nghiệp vụ AuthService.

## Mô hình Rental Domain MVP

Dịch vụ triển khai mô hình nghiệp vụ canonical theo AGENTS.md và thiết kế mới:
- Toàn bộ ValueObjects đã được inline vào các entities sở hữu kèm DataAnnotations và Fluent API trong `RentalServiceDbContext`.
- Các enums nghiệp vụ (`RentalCartStatus`, `RentalOrderStatus`, `RentalItemStatus`, `RentalUnitAssignmentStatus`, `UnitCondition`, `DeductionProposalStatus`, `DisputeStatus`, `ActorRole`) và `ErrorType` được đặt tập trung tại `Common/Enums`.
- Ngoại lệ kiến trúc: `RentalServiceDbContext`, design-time factory và DI registration đặt tại `Domain/DependencyInjection`, repository contracts tại `Domain/IRepositories`, repository implementations tại `Domain/Repositories`, migrations tại `Domain/Migrations`. Không tạo folder `Persistence` hoặc project `Infrastructure`.
- Hai Aggregate Roots:
  1. `RentalCart`: Quản lý các dòng `RentalCartItem`, checkout all-or-nothing, abandon và optimistic concurrency qua `Version`.
  2. `RentalOrder`: Quản lý `RentalItem`, `RentalUnitAssignment`, `RentalStateTransition`, `RentalTermsAcceptance`, `HandoverRecord`, `ReturnRecord`, `DeductionProposal`, `Dispute`, `RentalOperationReceipt`. Trạng thái `RentalOrderStatus.Paid = 9` (loại bỏ `PendingSupplierConfirmation` khỏi Order enum; Item vẫn giữ trạng thái này).
## API Lịch thuê cá nhân (Renter Calendar)

- Endpoint: `GET /api/v1/rental/me/calendar?month=YYYY-MM&page=1&pageSize=50`
- Yêu cầu xác thực: JWT Bearer Token (Human authenticated). Danh tính người thuê lấy authoritative từ `sub`. Admin cũng chỉ truy cập lịch của chính mình trên `/me`.
- Tham số truy vấn:
  - `month`: Định dạng `YYYY-MM` (mặc định tháng hiện tại theo giờ Việt Nam).
  - `page`: Trang hiện tại (mặc định 1).
  - `pageSize`: Kích thước trang (mặc định 50, tối đa 100).
- Dữ liệu trả về:
  - `days`: Đầy đủ các ngày trong tháng kèm `rentedQuantity` (tổng quantity của các RentalItem khớp ngày, tính trước phân trang).
  - `entries`: Danh sách các mục thuê (`orderId`, `orderCode`, `rentalItemId`, `equipmentListingId`, tên snapshot `listingTitle`/`listingTitleSnapshot`, `quantity`, `startDate`, `endDate`, `status`) kèm metadata phân trang.
  - Các trạng thái RentalItem được hiển thị: `AwaitingHandover`, `InRental`, `PendingSettlement`, `Completed`.
  - Ngày thuê được lưu end-exclusive trong Rental và tự động chuyển đổi sang end-inclusive cho calendar projection.
  - Tuyệt đối không để lộ thông tin physical asset, serial number, allocation reference hoặc lịch bảo trì của Supplier.

## Payment và Supplier Confirmation Workflow

### 1. InvoicePaid Consumer
- Consumer `InvoicePaid` validate envelope v1, producer (`payment-service`), aggregate/order, renter, invoice, payment reference, currency và số tiền theo snapshot.
- Deduplicate theo message ID và payment reference trong Inbox table (`rental_inbox_messages`); replay idempotent không cập nhật lại deadline hoặc tái phát notification.
- Giao dịch local: lưu payment reference, Order chuyển `PAID` (giá trị 9), Items chuyển `PENDING_SUPPLIER_CONFIRMATION` và tạo operations `ExtendHold`.
- Deadline xác nhận từng item: `min(PaidAt + 24h, 00:00 ngày thuê tại Asia/Ho_Chi_Minh − 1 giây)`, lưu UTC và cố định khi retry.
- Sau commit local: gọi Inventory `ExtendHold`. Thành công phát Outbox event `RentalSupplierConfirmationRequested` trên topic `rental.events.v1`.
- Hold hết hạn hoặc deadline đã qua: chuyển item sang `Expired`, lên lịch giải phóng hold và hoàn tiền 100% cho riêng item mà không ảnh hưởng item khác.
- Late payment: thanh toán đến sau hạn hoặc trên Order `Expired/Cancelled` giữ trạng thái kết thúc, ghi nhận payment reference, cờ `LatePaymentRefundRequired = true` và yêu cầu hoàn tiền toàn bộ.

### 2. Supplier Decision API
- `POST /api/v1/rental/items/{itemId}/accept`
- `POST /api/v1/rental/items/{itemId}/reject`
- Yêu cầu JWT human (`JWT.sub == item.SupplierId`) và header `Idempotency-Key` (tối đa 64 ký tự). Admin hoặc service token bị từ chối (403 Forbidden).
- **Accept:** ghi nhận intent trong journal `RentalOperationReceipt` (`Status = Processing`), commit local trước khi gọi `ConfirmAllocation` (timeout 10s). Xác thực receipt reference, group ID và toàn bộ assigned units từ Inventory; hợp lệ chuyển item `AwaitingHandover`, cập nhật rollup order status và phát Outbox event `RentalItemConfirmed`. Phản hồi không để lộ thông tin physical asset/serial number/allocation ID. Lỗi dependency tạm thời trả 503 để worker phục hồi.
- **Reject:** body gồm `rejectReason` (bắt buộc, tối đa 500 ký tự) và `optionalNote` (tùy chọn, tối đa 2.000 ký tự). Chuyển item `Rejected`, giải phóng unit assignments, lên lịch release allocation và refund (`RentalAmount + DepositAmount` của riêng item), phát Outbox event `RentalItemRejected`.
- Replay cùng key và payload trả lại kết quả cũ (200 OK); key khác sau khi đã quyết định hoặc payload khác trả 409 Conflict.

### 3. Expiry và Refund
- **Payment timeout:** gọi `CancelInvoice`. `INVOICE_ALREADY_PAID` dừng expiry và chờ payment event đã validate. Hủy thành công thì giải phóng toàn bộ holds và chuyển Order/Items sang `Expired`.
- **Supplier timeout:** chỉ expire item chưa quyết định, giải phóng hold và yêu cầu hoàn tiền toàn bộ item. Tự động reconcile intent `Accept` đã được ghi nhận trước hạn.
- **RefundCompleted Consumer:** đối chiếu operation key, invoice/payment reference và breakdown, lưu audit `RefundCompleted`, không chuyển trạng thái item bị rejected/expired thành `Completed`.
- Rollup trạng thái Order:
  - Còn item chờ supplier $\rightarrow$ `PAID`
  - Hết chờ và có accepted item $\rightarrow$ `AWAITING_HANDOVER`
  - Tất cả items rejected $\rightarrow$ `REJECTED`
  - Tất cả items kết thúc trong nhánh reject/expiry và có item expired $\rightarrow$ `EXPIRED`

### 4. Concurrency và Background Workers
- Serialization per-order qua PostgreSQL session advisory lock (`PostgresOrderLockManager`) kết hợp optimistic concurrency qua `Version`. Không giữ DB transaction mở trong lúc gọi HTTP upstream.
- `OutboxPublisherWorker`: quét batch 50, polling 15 giây, claim tin nhắn nguyên tử qua `FOR UPDATE SKIP LOCKED` với lease 30 giây, gửi `rental.events.v1` với partition key là Order ID, chỉ `MarkPublished` sau broker acknowledgment (`PersistenceStatus.Persisted`). Exponential backoff có jitter tối đa 60 giây, chuyển DLQ sau 10 lần thử.
- `OperationRecoveryWorker`: quét batch 50, polling 15 giây, phục hồi các tác vụ `ExtendHold`, `ConfirmAllocation`, `ReleaseAllocation`, `RequestRefund`, `CancelInvoice` qua `RentalOperationExecutor`.
- `ExpiryScannerWorker`: quét batch 50, polling 15 giây, quét và xử lý timeout thanh toán và timeout supplier.
- `InboxProcessingWorker`: xử lý bền vững các tin nhắn `InvoicePaid` và `RefundCompleted` từ bảng `rental_inbox_messages`, thử lại các lỗi tạm thời (`INVOICE_NOT_ATTACHED`), chuyển `DeadLettered` và đẩy DLQ Outbox sau 10 lần thử.
- `KafkaPaymentEventsConsumerWorker`: consumer đăng ký topic `payment.events.v1` với `EnableAutoCommit = false`, chỉ commit offset sau khi tin nhắn đã được lưu bền vững vào Inbox.

### 5. Hướng dẫn đối soát Journal, DLQ và Vận hành (Runbook)

#### Luồng Intake → Inbox → Journal
1. **Kafka Intake (`KafkaPaymentEventsConsumerWorker`):**
   - Chỉ gọi entrypoint `ReceiveMessageAsync`.
   - Xác thực schema Envelope v1, tính toán fingerprint canonical từ các trường hợp đồng (`messageId`, `messageType`, `version`, `producer`, `aggregateId`, financial fields).
   - Lưu bền vững vào bảng `rental.rental_inbox_messages` với trạng thái `Received`, kèm metadata Kafka (`KafkaTopic`, `KafkaPartition`, `KafkaOffset`), rồi commit local DB.
   - Commit offset Kafka ngay sau khi commit DB thành công. Tuyệt đối không lookup Order hoặc thực thi use case nghiệp vụ trong luồng consumer này.
   - Bản tin lỗi định dạng/schema được định tuyến ngay vào topic DLQ `payment.events.v1.dlq` với ID tất định từ topic/partition/offset để chống trùng lặp DLQ khi Kafka gửi lại record.

2. **Inbox Processing (`InboxProcessingWorker`):**
   - Quét descriptors (`GetPendingRetryDescriptorsAsync`) bằng `AsNoTracking` để tránh tracking entity rải rác.
   - Khởi tạo `IServiceScope` độc lập cho từng message.
   - Thu thập PostgreSQL advisory lock theo `orderId` (tổng timeout kết nối và chờ lock là 5 giây).
   - Tải aggregate `RentalOrder` sau khi đã giữ lock.
   - Thực thi use case (`PaymentWorkflowService` hoặc `RefundCompletedConsumer`) theo dạng staging (không tự gọi `SaveChangesAsync`).
   - Thực hiện **một local commit nguyên tử** đồng thời: Order/Items state, audit transitions, operation receipts (journal), Outbox notifications và Inbox chuyển `Processed`.
   - Lỗi tạm thời (`INVOICE_NOT_ATTACHED`, `ORDER_NOT_FOUND`) được đánh dấu `Failed` và lên lịch retry bằng exponential backoff có jitter (tối đa 60s). Hết 10 lần retry sẽ chuyển `DeadLettered` và phát Outbox DLQ.

#### Trạng thái và Retry Semantics trong Journal (`rental_operation_receipts`)
- **Ngân sách retry:** Chốt tối đa 10 attempts cho một budget, bao gồm cả lần đầu tiên.
- **Bộ đếm Retry:** `RetryCount` tăng đúng một lần trước khi bắt đầu HTTP attempt qua `receipt.BeginProviderAttempt(utcNow)`. Chờ điều kiện tiên quyết (`PrerequisiteOperationId`), chưa đến `NextRetryAt`, scan định kỳ hoặc replay response không tính là attempt.
- **Phân loại kết quả:**
  - **Transient Failure:** Lỗi mạng hoặc 5xx tạm thời $\rightarrow$ `Status = Pending`, lên lịch retry (`NextRetryAt`).
  - **Business Failure:** Lỗi từ chối nghiệp vụ xác định từ provider (4xx không liên quan timeout) $\rightarrow$ `Status = Failed`, `ErrorClassification = Business`, không retry.
  - **Uncertain:** Timeout mạng, lỗi 5xx sau khi đã gửi request, hoặc nhận mã 2xx nhưng response thiếu/sai receipt hoặc identity $\rightarrow$ `Status = Processing`, `IsOutcomeUncertain = true`, `ErrorClassification = Uncertain`. Uncertainty là trạng thái **sticky**: lỗi 4xx ở lần retry sau không tự chứng minh request trước chưa được provider xử lý.
  - **Success:** Provider xác nhận hợp lệ $\rightarrow$ `Status = Succeeded`, xóa uncertainty (`IsOutcomeUncertain = false`), commit side effects.
  - **UncertainExhausted:** Hết 10 attempts nhưng vẫn còn uncertainty $\rightarrow$ `Status = Failed`, `ErrorClassification = UncertainExhausted`, tiếp tục bảo vệ item khỏi thao tác đối nghịch và yêu cầu can thiệp Admin.
  - **TransientExhausted:** Hết 10 attempts do lỗi tạm thời chắc chắn chưa có side effect $\rightarrow$ `Status = Failed`, `ErrorClassification = TransientExhausted`.

#### Endpoint Admin Replay (`POST /api/v1/rental/internal/orders/{orderId}/operations/{operationId}/replay`)
- **Quyền truy cập:** Yêu cầu JWT của human Admin (`ActorRole.Admin`). Service token hoặc customer token bị từ chối (403 Forbidden). Không xác thực trả 401 Unauthorized.
- **Mạng nội bộ:** Endpoint thuộc prefix `internal`, chỉ publish qua mạng nội bộ hoặc API Gateway private.
- **Headers:**
  - `Authorization: Bearer <ADMIN_JWT>`
  - `Idempotency-Key: <IDEMP_KEY>` (bắt buộc, tối đa 64 ký tự).
- **Body:**
  ```json
  {
    "reason": "Xác nhận mạng Inventory đã ổn định; tái thực thi confirm allocation"
  }
  ```
- **Quy tắc xử lý:**
  - Thu thập order advisory lock, kiểm tra sự tồn tại và tính hợp lệ của receipt.
  - Nếu operation đã `Succeeded`: trả 409 Conflict (`OPERATION_ALREADY_SUCCEEDED`).
  - Nếu journal payload bị hỏng hoặc thiếu: trả 422 Unprocessable Entity (`CORRUPTED_JOURNAL`).
  - Kiểm tra idempotency: Replay với cùng key và reason trả 202 đã lưu (không reset lặp lại); cùng key nhưng khác reason trả 409 Conflict (`IDEMPOTENCY_CONFLICT`).
  - Ghi vết audit transition `AdminReplayRequested` kèm thông tin Admin actor, lý do, trạng thái cũ và số lần retry trước đó.
  - Đặt lại `RetryCount = 0`, `Status = Pending`, `NextRetryAt = NOW()`, giữ nguyên `IsOutcomeUncertain` và payload bất biến ban đầu.
  - Commit thay đổi và trả về `202 Accepted` kèm body `AdminReplayResponse`:
    ```json
    {
      "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "operationId": "op-confirm-001",
      "action": "SupplierAccept",
      "status": "Pending",
      "retryCount": 0,
      "replayedAt": "2026-10-11T14:30:00Z"
    }
    ```
  - Worker `OperationRecoveryWorker` sẽ tự động đón và thực thi tác vụ này trong chu kỳ chạy kế tiếp.

#### Cách tìm Uncertainty, Exhausted, Prerequisite bị kẹt và Lease hết hạn

1. **Tìm các tác vụ có kết quả chưa rõ hoặc đã hết lượt thử lại:**
   ```sql
   SELECT "Id", "RentalOrderId", "OperationId", "Action", "Status", "ErrorClassification", "IsOutcomeUncertain", "RetryCount", "LastAttemptedAt", "NextRetryAt"
   FROM rental.rental_operation_receipts
   WHERE "IsOutcomeUncertain" = TRUE
      OR "ErrorClassification" IN ('UncertainExhausted', 'TransientExhausted')
   ORDER BY "CreatedAt" ASC;
   ```

2. **Tìm các tác vụ bị kẹt do điều kiện tiên quyết (Prerequisite) chưa hoàn tất:**
   ```sql
   SELECT r."OperationId" AS "BlockedOperation", r."Action" AS "BlockedAction",
          r."PrerequisiteOperationId",
          p."Status" AS "PrereqStatus", p."ErrorClassification" AS "PrereqError", p."RetryCount" AS "PrereqRetries"
   FROM rental.rental_operation_receipts r
   LEFT JOIN rental.rental_operation_receipts p
          ON r."RentalOrderId" = p."RentalOrderId" AND r."PrerequisiteOperationId" = p."OperationId"
   WHERE r."PrerequisiteOperationId" IS NOT NULL
     AND (p."Status" IS NULL OR p."Status" != 'Succeeded');
   ```

3. **Tìm Outbox messages có lease bị hết hạn hoặc worker crash:**
   ```sql
   SELECT "Id", "Topic", "PartitionKey", "MessageType", "Status", "RetryCount", "LockedBy", "LockedUntilUtc", NOW() AS "DatabaseNow"
   FROM rental.rental_outbox_messages
   WHERE "Status" = 'Pending'
     AND "LockedUntilUtc" IS NOT NULL
     AND "LockedUntilUtc" <= NOW();
   ```

#### Kiểm tra Dead Letter Queue mà không ghi log payload nhạy cảm
- **Nguyên tắc bảo mật:** Không in nội dung thô `rawMessage` ra log console. Log ứng dụng chỉ ghi `MessageId`, `Topic`, `PartitionKey`, mã lỗi và thông báo vắn tắt.
- **Truy vấn an toàn các tin nhắn trong DLQ:**
  ```sql
  SELECT "Id", "Topic", "PartitionKey", "MessageType", "Status", "RetryCount", "CreatedAt"
  FROM rental.rental_outbox_messages
  WHERE "Topic" = 'payment.events.v1.dlq'
  ORDER BY "CreatedAt" DESC
  LIMIT 50;
  ```

#### Ghi chú về Dependency Conformance
- Toàn bộ logic nội bộ, ranh giới dịch vụ, cơ chế khóa session advisory lock PostgreSQL và giao thức streaming Kafka đã được kiểm chứng bằng kiểm thử tích hợp tự động.
- Khả năng tương thích end-to-end với các dịch vụ phụ thuộc (`AuthService`, `PaymentService`, `InventoryService`) cần được kiểm chứng trên môi trường staging tích hợp thông qua live provider khi các dịch vụ này triển khai đúng contract đã freeze (v1).

#### Metrics Observability (`Dprental.RentalService`)
- **Meter Name:** `Dprental.RentalService` (đặt tại tầng `Application/Observability`)
- **Danh mục Metrics:**
  - `rental.operations.retries`: Số lần thử lại tác vụ nghiệp vụ (`action`).
  - `rental.operations.reconciled`: Số tác vụ được phục hồi/reconcile thành công (`action`).
  - `rental.dlq.messages`: Số tin nhắn chuyển vào Dead Letter Queue (`topic`).
  - `rental.release.failures`: Số lần giải phóng allocation thất bại.
  - `rental.refund.failures`: Số lần yêu cầu hoàn tiền thất bại.
  - `rental.operations.uncertain_exhausted`: Số tác vụ hết lượt thử lại nhưng trạng thái chưa rõ (`action`).

## Thống kê xác nhận của Supplier cho Admin (Admin Supplier Confirmation Statistics)

### 1. Endpoint

`GET /api/v1/rental/admin/suppliers/{supplierId}/confirmation-statistics?fromUtc=YYYY-MM-DDTHH:mm:ssZ&toUtc=YYYY-MM-DDTHH:mm:ssZ`

### 2. Định nghĩa bốn chỉ số

| Chỉ số | Điều kiện qualifying transition trong `RentalStateTransition` |
|---|---|
| `acceptedCount` | Action `SupplierAccept`, `PendingSupplierConfirmation` → `AwaitingHandover`, actor là supplier sở hữu item (`ActorRole = Supplier`, `ActorId = item.SupplierId`). |
| `rejectedCount` | Action `SupplierReject`, `PendingSupplierConfirmation` → `Rejected`, actor là supplier sở hữu item (`ActorRole = Supplier`, `ActorId = item.SupplierId`). |
| `confirmationTimeoutCount` | Action `SupplierTimeout`, `PendingSupplierConfirmation` → `Expired`, actor `System` (`ActorRole = System`). |
| `cancelAfterAcceptCount` | Transition thực sự chuyển sang `Cancelled` (`ToState = Cancelled`, `FromState <> Cancelled`), actor `Supplier` (`ActorRole = Supplier`, `ActorId = item.SupplierId`), và tồn tại qualifying `SupplierAccept` thành công của cùng item tại thời điểm trước hoặc bằng thời điểm hủy. |

### 3. Quy tắc đếm và Deduplication

- **Đơn vị đếm:** Đếm số lượng `RentalItem` duy nhất (không đếm `RentalOrder`, không nhân `Quantity`, không đếm số lần retry/thao tác).
- **Deduplication:** Lấy thời điểm qualifying transition đầu tiên (`MIN(OccurredAt)`) của từng item cho chỉ số đó trước khi so khớp khoảng thời gian báo cáo.
- **Khoảng thời gian:** Nửa mở `[fromUtc, toUtc)`: gồm thời điểm bắt đầu (`>= fromUtc`), không gồm thời điểm kết thúc (`< toUtc`).
- **Thời điểm tính:** Dùng thời điểm diễn ra sự kiện (`OccurredAt` của transition), không dùng `CreatedAt` của Order.
- **Bằng chứng độc lập thời gian:** Sự kiện accept diễn ra trước `fromUtc` vẫn là bằng chứng hợp lệ để ghi nhận supplier cancellation diễn ra trong khoảng `[fromUtc, toUtc)`.
- **Loại trừ sự kiện khác:** Không tính payment timeout, `HoldExpired`, `RefundCompleted`, `OrderRollup` hay `ReplayOperation` vào `confirmationTimeoutCount`. Renter/Admin/System hủy không làm tăng `cancelAfterAcceptCount`. Không tính audit không đổi trạng thái (như `Cancelled` → `Cancelled`).

### 4. Quy tắc thời gian (Timezone & Date)

- `fromUtc` và `toUtc` là tham số bắt buộc theo chuẩn ISO 8601 kèm timezone tường minh (`Z` hoặc offset `±HH:mm`), hỗ trợ phần thập phân giây từ 1 đến 7 chữ số:
  - `yyyy-MM-ddTHH:mm:ssZ`
  - `yyyy-MM-ddTHH:mm:ss±HH:mm`
  - `yyyy-MM-ddTHH:mm:ss.<1–7 chữ số>Z`
  - `yyyy-MM-ddTHH:mm:ss.<1–7 chữ số>±HH:mm`
- Cho phép ký tự `t`/`z` viết thường (được chuẩn hóa trước khi parse).
- Dấu `+` trong query string HTTP cần được URL-encode thành `%2B` (ví dụ: `%2B07:00`) để tránh bị chuyển thành dấu cách.
- Mọi mốc thời gian được chuẩn hóa về UTC trước khi thực hiện truy vấn cơ sở dữ liệu.
- Không sử dụng machine local timezone và không tự gán khoảng thời gian mặc định.
- Error codes trả về:
  - `MISSING_DATE_RANGE`: Thiếu `fromUtc` hoặc `toUtc`.
  - `INVALID_DATETIME_FORMAT`: Định dạng không hợp lệ, thiếu timezone, date/time/offset sai hoặc có ký tự thừa.
  - `INVALID_TIME_RANGE`: Khoảng rỗng hoặc đảo ngược sau khi chuẩn hóa (`fromUtc >= toUtc`).
  - `INVALID_SUPPLIER_ID`: `supplierId` rỗng (`Guid.Empty`) hoặc không hợp lệ.
### 5. Phân quyền (Authorization)

- **Chưa đăng nhập:** HTTP 401 (`Auth.Unauthorized`).
- **Người dùng quản trị (Human Admin):** HTTP 200 (`Result.Success`).
- **Người dùng thông thường (Renter / Supplier không phải Admin):** HTTP 403 (`Auth.Forbidden`).
- **Service token:** HTTP 403 (`Auth.ServiceTokenNotAllowed`).

### 6. Giới hạn dữ liệu và History Coverage

- Nguồn dữ liệu hoàn toàn xuất phát từ audit log `RentalStateTransition`. Không sử dụng trạng thái hiện tại (`RentalItem.Status`) làm căn cứ thống kê nhằm tránh sai lệch khi item chuyển trạng thái tiếp theo (`InRental`, `Completed`, `Cancelled`).
- Hệ thống không tự ý backfill dữ liệu lịch sử hoặc suy đoán actor từ trạng thái hiện tại.
- **Phạm vi cancellation hiện tại:** Workflow hủy đơn từ phía supplier sau accept chưa được triển khai trong phiên bản này. Chỉ số `cancelAfterAcceptCount` phản ánh số sự kiện hủy hợp lệ được ghi nhận trong audit log và mặc định trả về `0` cho đến khi workflow này được hiện thực hóa ở task riêng. Truy vấn đã được thiết kế sẵn sàng để tự động nhận diện các bản ghi audit khi workflow đó đi vào hoạt động mà không cần thêm pipeline phân tích hay thay đổi database schema.
