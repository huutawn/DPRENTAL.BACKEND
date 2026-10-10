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

#### Đối soát Journal (`rental_operation_receipts`)
- **Truy vấn tác vụ cần đối soát**:
  ```sql
  SELECT "Id", "RentalOrderId", "OperationId", "Action", "Status", "ErrorClassification", "RetryCount", "ErrorMessage", "CreatedAt"
  FROM rental.rental_operation_receipts
  WHERE "Status" = 'Processing' OR "ErrorClassification" = 'UncertainExhausted'
  ORDER BY "CreatedAt" ASC;
  ```
- **Ý nghĩa trạng thái**:
  - `Uncertain`: Gặp sự cố mạng/timeout sau khi gửi request; hệ thống đang khóa hành động đối nghịch để ngăn ngừa mâu thuẫn dữ liệu.
  - `UncertainExhausted`: Đã hết 10 lần retry tự động nhưng chưa xác nhận được kết quả upstream. Vận hành kiểm tra đối soát với Provider (Payment/Inventory).
- **Kích hoạt Replay có Audit**:
  Sử dụng lệnh nội bộ `ReplayStoredOperationAsync(orderId, operationId)` của `RentalOperationExecutor`. Lệnh này tái mở budget retry, đọc chính xác `PayloadJson` đã lưu ban đầu (không sửa đổi payload) và ghi vết audit `ReplayOperation` (Actor: Admin).

#### Đối soát Dead Letter Queue (`payment.events.v1.dlq`)
- **Truy vấn tin nhắn DeadLetter trong Inbox**:
  ```sql
  SELECT "Id", "MessageId", "MessageType", "AggregateId", "ErrorMessage", "ReceivedAt"
  FROM rental.rental_inbox_messages
  WHERE "Status" = 'DeadLettered'
  ORDER BY "ReceivedAt" DESC;
  ```
- **Truy vấn Outbox DLQ tương ứng**:
  ```sql
  SELECT "Id", "Topic", "PartitionKey", "MessageType", "Status", "CreatedAt"
  FROM rental.rental_outbox_messages
  WHERE "Topic" = 'payment.events.v1.dlq'
  ORDER BY "CreatedAt" DESC;
  ```

#### Metrics Observability (`Dprental.RentalService`)
- Meter Name: `Dprental.RentalService`
- Danh mục Metrics:
  - `rental.operations.retries`: Bộ đếm số lần retry của các tác vụ nghiệp vụ (`action`).
  - `rental.operations.reconciled`: Bộ đếm số tác vụ được phục hồi/reconcile thành công (`action`).
  - `rental.dlq.messages`: Bộ đếm số tin nhắn được chuyển vào Dead Letter Queue (`topic`).
  - `rental.release.failures`: Bộ đếm số lần giải phóng allocation thất bại.
  - `rental.refund.failures`: Bộ đếm số lần yêu cầu hoàn tiền thất bại.
