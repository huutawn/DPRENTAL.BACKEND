# Hướng dẫn tích hợp SignalR Realtime và Resync cho Frontend (Rental Service)

Tài liệu bàn giao hợp đồng giao tiếp thời gian thực giữa RentalService (qua API Gateway) và frontend client.

---

## 1. Kết nối và Authentication

### 1.1. Endpoint & Transport
- **Public URL**: `wss://<gateway-domain>/api/v1/rental/hubs/orders`
- **Negotiate URL**: `https://<gateway-domain>/api/v1/rental/hubs/orders/negotiate`
- **Giao thức mạng**: Chỉ sử dụng **WebSockets** (`HttpTransportType.WebSockets`).
- **Authentication**:
  - Browser gửi cookie HttpOnly `accessToken` (được Gateway tự động forward sang downstream).
  - Hoặc gửi qua header `Authorization: Bearer <token>`. Header có ưu tiên tuyệt đối; header không hợp lệ sẽ không fallback sang cookie.
  - **Không hỗ trợ query token**: tham số `?access_token=...` trên URL bị từ chối với HTTP 401 Unauthorized.
  - Token hết hạn: kết nối tự động đóng (`CloseOnAuthenticationExpiration = true`). Client refresh token qua AuthService và tự động kết nối lại.

### 1.2. Mẫu khởi tạo kết nối (TypeScript / SignalR Client)
```typescript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("/api/v1/rental/hubs/orders", {
    transport: signalR.HttpTransportType.WebSockets,
    // Browser tự động gửi cookie HttpOnly 'accessToken' cùng origin hoặc credentials
    withCredentials: true
  })
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
  .configureLogging(signalR.LogLevel.Information)
  .build();
```

---

## 2. Event Contracts & Payload

Hệ thống cung cấp **hai typed events** duy nhất. Hub chỉ hỗ trợ server push; client không gửi tin nhắn hay gọi các method join/subscribe. Server tự động gia nhập hai group danh tính: `renter:{UserId:D}` và `supplier:{UserId:D}`.

### 2.1. `RenterOrderChanged`
Phát khi đơn hàng của người thuê có thay đổi dữ liệu hiển thị (kể cả khi `Order.Status` không đổi).

```json
{
  "eventId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "orderId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "version": 18,
  "changeType": "SUPPLIER_CONFIRMED",
  "occurredAt": "2026-10-26T14:30:00Z"
}
```

*Lưu ý bảo mật*: Payload dành cho người thuê **tuyệt đối không chứa** thông tin vật lý (AssetId, Serial, bằng chứng kỹ thuật nội bộ).

### 2.2. `SupplierSubOrderChanged`
Phát khi đơn con của nhà cung cấp có thay đổi dữ liệu hiển thị hoặc khi đơn hàng vừa đủ điều kiện nhà cung cấp nhìn thấy.

```json
{
  "eventId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
  "orderId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
  "subOrderId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90",
  "orderVersion": 18,
  "changeType": "HANDOVER_CONFIRMED",
  "occurredAt": "2026-10-26T14:30:00Z"
}
```

### 2.3. Danh sách `changeType` chuẩn
- `ORDER_CREATED`: Đơn hàng mới được tạo sau checkout.
- `INVOICE_ATTACHED`: Hóa đơn thanh toán được khởi tạo và gắn vào đơn.
- `PAYMENT_CONFIRMED`: Thanh toán thành công (phát cho renter và từng suborder supplier).
- `PAYMENT_RECOVERY_PENDING`: Thanh toán trễ trong hạn đang chạy quy trình recovery giữ chỗ lại (chỉ renter nhận).
- `PAYMENT_REFUND_REQUIRED`: Thanh toán ngoài hạn hoặc recovery thất bại, chuyển hoàn tiền toàn phần (chỉ renter nhận).
- `DELIVERY_NOTE_UPDATED`: Người thuê cập nhật ghi chú giao nhận.
- `SUPPLIER_CONFIRMED`: Nhà cung cấp chấp nhận và Inventory xác nhận phân bổ thiết bị.
- `SUPPLIER_REJECTED`: Nhà cung cấp từ chối đơn con hoặc mục thuê.
- `HANDOVER_CONFIRMED`: Bàn giao thiết bị hoàn tất và bằng chứng được lưu trữ.
- `HANDOVER_ACKNOWLEDGED`: Người thuê xác nhận đã nhận thiết bị ngoài công trường.
- `ALLOCATION_CHANGED`: Phân bổ thiết bị thay đổi (giải phóng giữ chỗ, điều chỉnh allocation).
- `ORDER_CANCELLED`: Đơn hàng bị hủy (do người thuê hoặc hệ thống).
- `ITEM_CANCELLED`: Mục thuê chưa bàn giao bị hủy.
- `PAYMENT_EXPIRED`: Hết hạn thanh toán (chỉ báo renter, không báo supplier).
- `SUPPLIER_CONFIRMATION_EXPIRED`: Quá hạn 24h xác nhận của nhà cung cấp.
- `HANDOVER_OVERDUE`: Cảnh báo quá hạn ngày bàn giao.
- `HANDOVER_EXPIRED`: Hết hạn bàn giao thiết bị sau grace period.
- `RETURN_SUBMITTED`: Nhà cung cấp ghi nhận biên bản trả máy (phát ngay sau khi commit biên bản tại Rental).
- `RETURN_RECORDED`: Thiết bị hoàn trả được Inventory xác nhận đóng allocation.
- `SETTLEMENT_CHANGED`: Thay đổi đề xuất khấu trừ, khiếu nại hoặc tất toán hoàn tất.
- `REFUND_CHANGED`: Khoản hoàn tiền được PaymentService xác nhận.

### 2.4. Mutation Coverage Matrix
| Mutation / Nghiệp vụ | Completion Boundary | ChangeType | Người nhận Renter | Người nhận Supplier | Version tăng? |
| :--- | :--- | :--- | :---: | :---: | :---: |
| Checkout Submit | Commit RentalOrder | `ORDER_CREATED` | Có | Không | Có (1) |
| Gắn hóa đơn | Commit InvoiceRef | `INVOICE_ATTACHED` | Có | Không | Có (2) |
| Thanh toán 0 đồng | Commit MarkAsPaid | `PAYMENT_CONFIRMED` | Có | Đúng các SubOrder | Có (3) |
| Thanh toán đúng hạn | Commit MarkAsPaid | `PAYMENT_CONFIRMED` | Có | Đúng các SubOrder | Có |
| Thanh toán late/recovery | Commit RecordPayment | `PAYMENT_RECOVERY_PENDING` | Có | Không | Có |
| Recovery hoàn tất | Commit MarkAsPaid | `PAYMENT_CONFIRMED` | Có | Đúng các SubOrder | Có |
| Recovery thất bại/quá hạn | Commit LateRefund | `PAYMENT_REFUND_REQUIRED` | Có | Không | Có |
| Cập nhật DeliveryNote | Commit note | `DELIVERY_NOTE_UPDATED` | Có | Có (nếu đơn Paid) | Có |
| Xác nhận bàn giao ngoài công trường | Commit Acknowledge | `HANDOVER_ACKNOWLEDGED` | Có | Đúng SubOrder sở hữu | Có |
| Biên bản trả máy gửi | Commit ReturnRecord | `RETURN_SUBMITTED` | Có | Đúng SubOrder sở hữu | Có |
| Inventory đóng hoàn trả | Commit Return closure | `RETURN_RECORDED` | Có | Đúng SubOrder sở hữu | Có |
| Inventory giải phóng allocation | Commit Release closure | `ALLOCATION_CHANGED` | Có | Đúng SubOrder sở hữu | Có |
| Khấu trừ / Hoàn tiền | Commit settlement | `SETTLEMENT_CHANGED` / `REFUND_CHANGED` | Có | Đúng SubOrder liên quan | Có |

## 3. Chiến lược Resync & Cache Invalidation

Notification SignalR đóng vai trò **Invalidation Signal**; client nhận event để biết dữ liệu đã thay đổi và kích hoạt gọi lại 4 API REST hiện hữu (`/api/v1/rental/orders`, `/api/v1/rental/orders/{orderId}`, `/api/v1/rental/supplier/sub-orders`, `/api/v1/rental/supplier/sub-orders/{subOrderId}`).

### 3.1. Nguyên tắc so sánh Version
1. **Renter Detail**: So sánh theo `order.version` của đơn hàng tương ứng `orderId`. Nếu phiên bản nhận từ event lớn hơn phiên bản trong cache UI, kích hoạt fetch lại.
2. **Supplier SubOrder Detail**: So sánh theo `subOrder.orderVersion` tương ứng với đúng `subOrderId`. **Tuyệt đối không dùng một global version duy nhất** để lọc hoặc bỏ qua event của các SubOrder khác nhau.
3. **Danh sách (List Views)**: Luôn kích hoạt fetch lại trang hiện tại khi nhận event liên quan, kể cả khi `orderId` hoặc `subOrderId` chưa xuất hiện trong trang hiển thị, vì thay đổi trạng thái có thể làm entity lọt vào hoặc rời khỏi filter, dẫn đến thay đổi `totalCount` và phân trang.

### 3.2. Debounce Invalidation
Để tránh bão request khi có nhiều thay đổi diễn ra liên tiếp (burst of notifications):
- Áp dụng **Debounce 150 – 300 ms** trên từng view/resource.
- Đặt `maxWait` tối đa 1000 ms để bảo đảm dữ liệu không bị trì hoãn vô hạn.

### 3.3. Xử lý Race Condition giữa REST và WebSocket
Trường hợp GET đang chạy ngầm thì một event mới với version cao hơn ập tới:
1. Ghi nhận `highestRequiredVersion = Math.max(highestRequiredVersion, event.version)`.
2. Đánh dấu `isDirty = true`.
3. Khi GET response trả về:
   - Nếu `response.version < highestRequiredVersion`: dữ liệu trả về đã cũ (stale), không ghi đè vào cache mới; kích hoạt fetch thêm một lần nữa để lấy dữ liệu mới nhất.
   - Nếu `response.version >= highestRequiredVersion`: cập nhật cache và xóa cờ dirty.

### 3.4. Reconnection & Network Recovery
- **Initial Start**: Đăng ký các handler `connection.on(...)` trước khi gọi `connection.start()`. Ngay sau khi `start()` thành công, lập tức gọi GET REST để đóng khoảng trống dữ liệu trong lúc thiết lập kết nối.
- **Reconnecting / Reconnected**: Khi mạng phục hồi hoặc focus/resume tab trình duyệt, luôn kích hoạt fetch lại REST của màn hình hiện tại.
- **Không có SignalR replay/outbox**: Không giả định server sẽ gửi lại các notification bị mất trong lúc offline. Việc đồng bộ dữ liệu hoàn toàn dựa trên REST resync.

---

## 4. Giới hạn kiến trúc (Limitations)
1. **Best-Effort Delivery**: Notification SignalR được gửi sau khi transaction database commit thành công theo cơ chế best-effort. Lỗi mạng tức thời phía push socket không làm rollback transaction dữ liệu. Client phục hồi thông qua reconnect và REST resync.
2. **Single Instance**: Phiên bản hiện tại nghiệm thu trên mô hình 1 Rental instance (chưa cấu hình Redis/Valkey backplane). Khi triển khai nhiều replica, cần bổ sung backplane để fan-out notification giữa các instance.
