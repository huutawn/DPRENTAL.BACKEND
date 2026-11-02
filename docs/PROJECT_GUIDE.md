# DPRental — NotificationService Project Guide & MVP Domain Invariants

> **Mục đích của tài liệu này**
>
> Đây là tài liệu ngữ cảnh chung cho toàn bộ dự án NotificationService trong hệ sinh thái DPRental, đặc biệt dành cho developer/coding agent khi nhận một Jira Task/Subtask độc lập.
>
> Agent **không được chỉ đọc tên task rồi tự suy diễn domain**. Trước khi thực hiện task, phải đọc tài liệu này, kiểm tra source code hiện tại của service liên quan, sau đó đối chiếu acceptance criteria của task.
>
> Tài liệu này mô tả chi tiết:
> - Tổng quan hệ sinh thái microservices DPRental và vị trí của NotificationService;
> - Ranh giới trách nhiệm, sở hữu dữ liệu và quy tắc cấm truy cập DB chéo;
> - Thứ tự ưu tiên khi có mâu thuẫn giữa tài liệu và mã nguồn;
> - Phạm vi MVP Phase 1 và các tính năng ngoài phạm vi (Non-Goals);
> - Invariants và nguyên tắc thông báo cốt lõi (Recipient, Template, Financial Wording, Watermark, Delivery);
> - Bộ quy tắc kỹ thuật chung về phân tầng kiến trúc, SOLID, LINQ/EF Core, Native SQL được quy định tại **[AGENTS.md](../AGENTS.md)**.

---

## 1. Thứ Tự Ưu Tiên Khi Có Mâu Thuẫn

Khi requirement, source code và tài liệu khác nhau, áp dụng thứ tự ưu tiên:

1. **Jira Task/Subtask hiện tại và Acceptance Criteria đã được duyệt**
2. **Các quyết định kiến trúc/MVP trong tài liệu này và các ADR đã được phê duyệt (`docs/adr/`)**
3. **Contract đã được freeze giữa các service (`docs/contracts/`)**
4. **Source code hiện tại của service**
5. README / tài liệu cũ

Nếu phát hiện mâu thuẫn ảnh hưởng domain model hoặc contract giữa các service:
- Không tự ý chọn một phương án khác.
- Không âm thầm thay đổi contract của service khác.
- Ghi rõ conflict trong phần phân tích task.
- Chỉ triển khai theo phương án đã được task hoặc architecture guide xác nhận.

---

## 2. Tổng Quan Hệ Sinh Thái & Vị Trí Của NotificationService

DPRental là nền tảng kết nối Chủ thiết bị (Supplier) và Khách thuê (Renter) để thuê máy móc, thiết bị theo dạng định danh vật lý (serialized rental assets).

Hệ thống hoạt động theo kiến trúc Event-Driven Microservices:

```text
[Frontend / Client Web & App]
         │
         ▼
[API Gateway (Ocelot Reverse Proxy)]
   │           │           │             │
   ▼           ▼           ▼             ▼
[Auth]      [Rental]   [Payment]   [NotificationService]
(Identity)   (Order,    (Invoice,     (In-App Inbox,
             Handover,  Settlement,    Email Delivery,
             Return)    Refunds)       SignalR Invalidation)
   │           │           │             ▲
   │           ▼           ▼             │
   │      [Kafka: rental.events.v1] ─────┘
   │
   └─── Internal Batch Contact API ◄── (Email Worker)
```

### Ranh giới trách nhiệm:
- **NotificationService sở hữu**:
  - `notification.notifications`: Bản ghi thông báo in-app và trạng thái đọc (`ReadAt`).
  - `notification.inbox_receipts`: Dấu vết idempotency cho Kafka consumer.
  - `notification.delivery_jobs`: Quản lý trạng thái hàng đợi và lịch sử gửi email.
  - `notification.preferences`: Tùy chọn bật/tắt nhận email theo danh mục của từng người dùng.
  - `notification.aggregate_watermarks`: Quản lý phiên bản nghiệp vụ tối đa theo aggregate để ngăn chặn thông báo lỗi thời.
  - Bộ template nội dung thông báo đa ngôn ngữ (mặc định `vi-VN`) phiên bản hóa trong mã nguồn.
  - SignalR Hub cho realtime invalidation thông báo.
- **Các service khác tiếp tục sở hữu**:
  - `RentalService`: Đơn thuê, giữ máy, supplier decision, bàn giao (handover), trả máy (return), tranh chấp, quyết toán cọc.
  - `AuthService`: Định danh người dùng, credentials, trạng thái tài khoản (`IsActive`), email xác thực (`IsEmailVerified`), session và cấp phát JWT.
  - `PaymentService`: Hóa đơn điện tử, giao dịch cổng thanh toán, hoàn tiền và quyết toán tài chính.
  - `InventoryService`: Quản lý thiết bị vật lý, serial number, lịch trống và trạng thái máy.
  - `Gateway`: Định tuyến công khai, chặn endpoint nội bộ, xác thực danh tính ban đầu, hỗ trợ WebSocket upgrade.

**Quy tắc bất biến số 1**: NotificationService **TUYỆT ĐỐI KHÔNG TRUY CẬP TRỰC TIẾP** vào database hoặc schema của bất kỳ service nào khác (`rental.*`, `auth.*`, `payment.*`, `inventory.*`).

---

## 3. Phạm Vi MVP Phase 1 & Ngoài Phạm Vi (Non-Goals)

### 3.1. Phạm vi MVP Phase 1 (In-Scope)
1. **Tiếp nhận 10 sự kiện nghiệp vụ cốt lõi từ `RentalService`** qua Kafka topic `rental.events.v1`:
   - `RentalOrderCreated`
   - `RentalOrderPaid`
   - `RentalSupplierConfirmationRequested`
   - `RentalOrderExpired`
   - `RentalOrderCancelled`
   - `RentalItemHandoverCompleted`
   - `RentalHandoverOverdue`
   - `ReturnConfirmationOverdue`
   - `RentalOrderCompleted`
   - `LatePaymentRefundRequested`
2. **Hộp thư thông báo trong ứng dụng (In-app Notification Inbox)**:
   - Danh sách thông báo có phân trang ổn định (`page`, `pageSize`, `status=all|unread`, `category`).
   - Đếm số lượng thông báo chưa đọc (`unread-count`).
   - Đánh dấu đã đọc từng thông báo (`PATCH /api/v1/notifications/{id}/read`).
   - Đánh dấu đã đọc toàn bộ hộp thư (`PATCH /api/v1/notifications/read-all`) với server cutoff timestamp.
3. **Thông báo Realtime qua SignalR Hub**:
   - Endpoint `/api/v1/notifications/hubs/inbox`.
   - Bắn sự kiện `NotificationInboxChanged` để thông báo frontend refetch/cập nhật badge chuông.
4. **Gửi Transactional Email có khả năng chịu lỗi**:
   - Tích hợp nhà cung cấp SMTP cấu hình được.
   - Cơ chế claim job bằng lease (`FOR UPDATE SKIP LOCKED`).
   - Phân loại lỗi chính xác (`Pending`, `Processing`, `Succeeded`, `RetryScheduled`, `Uncertain`, `Failed`, `Suppressed`).
   - Phục hồi lease khi worker gặp sự cố (crash recovery).
5. **Quản lý Tùy chọn Người dùng (Preferences)**:
   - Tùy chọn nhận email theo 4 danh mục: `ORDER`, `PAYMENT`, `HANDOVER`, `RETURN`.
   - Áp dụng chính sách mặc định an toàn cho người dùng mới.

### 3.2. Ngoài phạm vi Phase 1 (Non-Goals — Tuyệt đối không tự ý phát triển)
- Không SMS (Twilio/Brandname).
- Không Mobile Push Notification (FCM / APNs).
- Không Marketing campaigns, bulk email hoặc gửi tin khuyến mãi.
- Không xây giao diện Admin biên tập template (Template Editor UI trong DB). Template được định nghĩa versioned trong mã nguồn.
- Không Generic workflow engine hoặc dynamic rule scripting engine phức tạp.
- Không AI-generated notification content.
- Không Multi-provider fallback phức tạp (Phase 1 sử dụng 1 provider SMTP chuẩn).
- Không thay thế hoặc can thiệp vào `OrderHub` của RentalService.

---

## 4. Invariants & Quy Tắc Nghiệp Vụ Cốt Lõi

### 4.1. Recipient Identity & Quyền Riêng Tư
1. **Nguồn định danh người nhận**:
   - Người nhận bắt buộc phải là một GUID hợp lệ lấy từ trường định danh đáng tin cậy trong Kafka payload: `RenterId` cho khách thuê, `SupplierId` cho nhà cung cấp.
   - Tuyệt đối không suy đoán người nhận từ các trường không liên quan hoặc thông tin client gửi lên.
   - Nếu event thiếu trường người nhận hoặc mang `Guid.Empty`, event phải được cách ly vào hàng đợi DLQ/Quarantine; tuyệt đối không gửi bừa cho admin hay người dùng khác.
2. **Không nhận Email trực tiếp từ Kafka**:
   - Email người nhận phải được tra cứu từ `AuthService` qua endpoint nội bộ `POST /api/v1/auth/internal/users/notification-contacts:batch`.
   - Không tin tưởng email nhúng trong payload sự kiện vì có thể đã cũ, chưa xác thực hoặc làm rò rỉ PII trên Kafka broker.

### 4.2. Ngôn Từ Tài Chính (Financial Wording Invariant)
1. **Ý nghĩa của `LatePaymentRefundRequested`**:
   - Sự kiện này phát sinh khi khách thanh toán trễ sau khi đơn hàng đã hết hạn hoặc bị hủy; RentalService gửi yêu cầu hoàn tiền sang PaymentService.
   - Nội dung thông báo **CHỈ ĐƯỢC PHÉP KHẲNG ĐỊNH**: *"Yêu cầu hoàn tiền đang được xử lý"* hoặc *"Khoản thanh toán đang chờ xử lý hoàn tiền"*.
   - **TUYỆT ĐỐI CẤM** khẳng định *"Đã hoàn tiền thành công"* hoặc *"Tiền đã về tài khoản"* khi chưa có sự kiện xác nhận giao dịch thành công chính thức từ PaymentService.

### 4.3. Ngăn Chặn Hành Động Lỗi Thời (Stale Action Suppression)
1. Khi các sự kiện đến muộn hoặc sai thứ tự:
   - Nếu `RentalOrderCreated` đến sau khi đơn hàng đã được ghi nhận `Paid`, `Expired` hoặc `Cancelled` trong `notification.aggregate_watermarks`:
     - Phải loại bỏ lời kêu gọi hành động thanh toán (Call-to-Action / nút thanh toán).
     - Chỉ hiển thị thông tin lưu trữ lịch sử hoặc chuyển thành thông báo informational.
   - Nếu `RentalSupplierConfirmationRequested` đến sau khi đơn hàng đã kết thúc (`Expired`, `Cancelled`):
     - Loại bỏ nút xác nhận đơn của supplier; ghi rõ đơn đã hết hiệu lực.

### 4.4. Bảo Mật Xác Thực & API Boundary
1. **Human JWT vs Service JWT**:
   - Human JWT (`sub` = User Guid): Được phép đọc/sửa hộp thư cá nhân và kết nối SignalR Hub.
   - Service JWT (`u: "svc"`): Chỉ dùng cho giao tiếp kỹ thuật giữa các service; bị cấm truy cập hộp thư cá nhân của người dùng.
2. **Không chấp nhận Access Token trên Query String**:
   - Kết nối SignalR Hub và các endpoint REST tuyệt đối không đọc token từ URL query parameter `?access_token=...` để tránh lộ token trong access logs, browser history và reverse proxy logs.
   - Token chỉ được truyền qua header `Authorization: Bearer <token>` hoặc HTTP-only cookie `accessToken` trong pha negotiate/upgrade.
3. **Gateway chặn Endpoint Nội Bộ**:
   - Mọi endpoint chứa tiền tố `/internal/` phải bị API Gateway từ chối bằng HTTP 403 Forbidden.

### 4.5. Độ Bền Vững Email & Kết Quả Không Xác Định (Uncertain Outcome)
1. Không mở database transaction trong lúc thực hiện I/O mạng gửi mail qua SMTP.
2. Khi gặp timeout hoặc mất kết nối mạng sau lệnh `DATA` của SMTP mà chưa nhận được mã `250 OK`:
   - Trạng thái delivery job phải chuyển sang `Uncertain`.
   - **CẤM RETRY TỰ ĐỘNG (BLIND RETRY)** khi trạng thái là `Uncertain` để tránh gửi trùng email tài chính cho khách hàng.
   - Ghi nhận correlation ID và cảnh báo để đối soát thủ công hoặc qua log provider.
