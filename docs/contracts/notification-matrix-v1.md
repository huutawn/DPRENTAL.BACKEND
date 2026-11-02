# Notification Matrix & Template Specification (v1)

## 1. Source Evidence & Event Traceability Table

The table below documents the end-to-end trace for all 10 Phase 1 MVP target events from domain use case to Kafka emission, verified via source code inspection.

### 1.1. Inspected Git Revisions
- **DPRENTAL Root**: `109de834ca3532e542af7d5eb30d9f81a7ec68eb`
- **RentalService**: `159702d3a94e824e4da6da8af2bfc9867006119e`
- **AuthService**: `4ef9bb466a2e031dcd49e0ea69e741319591478a`
- **PaymentService**: `7b3aa68430dfd7a056274f6d6111f62f048a5690`
- **Gateway**: `d23cd533ede2d4b4142ff07bf3f7c34356be40a0`

### 1.2. Source Evidence Matrix

| # | Event Type | Producer & Source Location | Real Use Case & Trigger | Outbox Enqueue Method | Topic & Partition Key | Authoritative Recipient |
|---|---|---|---|---|---|---|
| 1 | `RentalOrderCreated` | `RentalService`<br>`CheckoutService.cs:592` | Customer completes checkout, order created in `PENDING_PAYMENT` state. | `EnqueueRentalOrderCreatedAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 2 | `RentalOrderPaid` | `RentalService`<br>`PaymentWorkflowService.cs:187`<br>`InventoryAllocationResultHandler.cs:567` | Payment webhook confirmed, invoice paid, order transitioned to `PAID`. | `EnqueueRentalOrderPaidAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 3 | `RentalSupplierConfirmationRequested` | `RentalService`<br>`InventoryAllocationResultHandler.cs:587` | Inventory allocation confirmed, suborder requires supplier approval before deadline. | `EnqueueRentalSupplierConfirmationRequestedAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.supplierId` |
| 4 | `RentalOrderExpired` | `RentalService`<br>`ExpiryWorkflowService.cs:303`<br>`RentalOperationExecutor.Payment.cs:435`<br>`RentalOperationExecutor.Supplier.cs:377` | Payment deadline (30m) or supplier confirmation deadline (24h) elapsed. | `EnqueueRentalOrderExpiredAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 5 | `RentalOrderCancelled` | `RentalService`<br>`ItemCancellationService.cs:283`<br>`RentalOperationExecutor.Cancellation.cs:284, 413` | Customer cancels order, or supplier rejection forces order cancellation. | `EnqueueRentalOrderCancelledAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 6 | `RentalItemHandoverCompleted` | `RentalService`<br>`InventoryAllocationResultHandler.cs:859` | Inventory confirms equipment handover inspection; item transitions to `IN_RENTAL`. | `EnqueueRentalItemHandoverCompletedAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 7 | `RentalHandoverOverdue` | `RentalService`<br>`HandoverOverdueService.cs:172` via `HandoverOverdueWorker` | Background scan detects handover start date passed without supplier handover. | `EnqueueRentalHandoverOverdueAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | Recipient 1: `payload.supplierId`<br>Recipient 2: `payload.renterId` |
| 8 | `ReturnConfirmationOverdue` | `RentalService`<br>`ReturnConfirmationOverdueService.cs:103` via `ReturnConfirmationOverdueWorker` | Background scan detects return inspection deadline elapsed without supplier decision. | `EnqueueReturnConfirmationOverdueAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | Recipient 1: `payload.supplierId`<br>Recipient 2: `payload.renterId` |
| 9 | `RentalOrderCompleted` | `RentalService`<br>`RentalSettledConsumer.cs:128`<br>`ItemCancellationService.cs:301, 528`<br>`ExpiryWorkflowService.cs:285` | All suborders finished rental lifecycle, deposit refunded, final settlement complete. | `EnqueueRentalOrderCompletedAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |
| 10 | `LatePaymentRefundRequested` | `RentalService`<br>`PaymentWorkflowService.cs:483` | Payment received for order already expired/cancelled; refund staged to PaymentService. | `EnqueueLatePaymentRefundRequestedAsync` | Topic: `rental.events.v1`<br>Key: `RentalOrderId` | `payload.renterId` |

---

## 2. Notification & Channel Matrix

| Event Type | Notification Type | Category | Severity | Recipient Role | In-App Inbox | Email Channel | Realtime SignalR | Default Email Preference |
|---|---|---|---|---|---|---|---|---|
| `RentalOrderCreated` | `ORDER_CREATED` | `ORDER` | `ACTION_REQUIRED` | Renter | Yes | No | Yes | Enabled |
| `RentalOrderPaid` | `ORDER_PAID` | `PAYMENT` | `SUCCESS` | Renter | Yes | Yes | Yes | Enabled |
| `RentalSupplierConfirmationRequested` | `SUPPLIER_CONFIRMATION_REQUIRED` | `ORDER` | `ACTION_REQUIRED` | Supplier | Yes | Yes | Yes | Enabled |
| `RentalOrderExpired` | `ORDER_EXPIRED` | `ORDER` | `WARNING` | Renter | Yes | No | Yes | Enabled |
| `RentalOrderCancelled` | `ORDER_CANCELLED` | `ORDER` | `WARNING` | Renter | Yes | No | Yes | Enabled |
| `RentalItemHandoverCompleted` | `HANDOVER_COMPLETED` | `HANDOVER` | `SUCCESS` | Renter | Yes | No | Yes | Enabled |
| `RentalHandoverOverdue` (Supplier) | `HANDOVER_OVERDUE_SUPPLIER` | `HANDOVER` | `ACTION_REQUIRED` | Supplier | Yes | Yes | Yes | Enabled |
| `RentalHandoverOverdue` (Renter) | `HANDOVER_OVERDUE_RENTER` | `HANDOVER` | `INFO` | Renter | Yes | No | Yes | Enabled |
| `ReturnConfirmationOverdue` (Supplier) | `RETURN_CONFIRMATION_OVERDUE_SUPPLIER` | `RETURN` | `ACTION_REQUIRED` | Supplier | Yes | Yes | Yes | Enabled |
| `ReturnConfirmationOverdue` (Renter) | `RETURN_CONFIRMATION_OVERDUE_RENTER` | `RETURN` | `INFO` | Renter | Yes | No | Yes | Enabled |
| `RentalOrderCompleted` | `ORDER_COMPLETED` | `ORDER` | `SUCCESS` | Renter | Yes | Yes | Yes | Enabled |
| `LatePaymentRefundRequested` | `LATE_PAYMENT_REFUND_REQUESTED` | `PAYMENT` | `INFO` | Renter | Yes | No | Yes | Enabled |

---

## 3. vi-VN Template Definitions & Deep Links

All user-facing templates are standardized in Vietnamese (`vi-VN`) and versioned in application source code.

### 3.1. `ORDER_CREATED` (Renter)
- **Title**: `Đơn thuê #{orderCode} đã được tạo`
- **Body**: `Đơn thuê {orderCode} đang chờ thanh toán {totalPayableAmount:N0} VND trước {paymentDeadline:HH:mm dd/MM/yyyy}.`
- **IsActionable**: `true`
- **CallToAction**: `Thanh toán ngay`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`
- **ActionUrl**: `/checkout/payment?orderId={rentalOrderId}`

### 3.2. `ORDER_PAID` (Renter)
- **Title**: `Thanh toán thành công đơn thuê #{orderCode}`
- **Body**: `Đơn thuê {orderCode} đã thanh toán thành công {paidAmount:N0} VND. Nhà cung cấp sẽ chuẩn bị thiết bị để bàn giao.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`
- **Email Subject**: `[DPRental] Xác nhận thanh toán thành công đơn thuê #{orderCode}`
- **Email Body**: `Kính gửi Quý khách, DPRental xác nhận đã nhận thanh toán số tiền {paidAmount:N0} VND cho đơn thuê {orderCode}. Mã hóa đơn: {invoiceRef}.`

### 3.3. `SUPPLIER_CONFIRMATION_REQUIRED` (Supplier)
- **Title**: `Yêu cầu xác nhận đơn thuê mới #{orderCode}`
- **Body**: `Bạn có yêu cầu thuê thiết bị mới cho đơn hàng {orderCode}. Vui lòng xác nhận trước {supplierConfirmationDeadline:HH:mm dd/MM/yyyy}.`
- **IsActionable**: `true`
- **CallToAction**: `Xem và xác nhận`
- **DeepLinkUrl**: `/supplier/sub-orders/{rentalOrderId}`
- **ActionUrl**: `/supplier/sub-orders/{rentalOrderId}/confirm`
- **Email Subject**: `[DPRental] Yêu cầu xác nhận đơn thuê #{orderCode}`

### 3.4. `ORDER_EXPIRED` (Renter)
- **Title**: `Đơn thuê #{orderCode} đã hết hạn`
- **Body**: `Đơn thuê {orderCode} đã hết hạn do quá thời gian quy định ({reason}). Thiết bị đã được giải phóng.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`

### 3.5. `ORDER_CANCELLED` (Renter)
- **Title**: `Đơn thuê #{orderCode} đã bị hủy`
- **Body**: `Đơn thuê {orderCode} đã bị hủy vì lý do: {reason}. Các khoản tiền liên quan sẽ được xử lý theo chính sách hoàn tiền.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`

### 3.6. `HANDOVER_COMPLETED` (Renter)
- **Title**: `Bàn giao thiết bị thành công cho đơn thuê #{orderCode}`
- **Body**: `Thiết bị trong đơn thuê {orderCode} đã được bàn giao thành công lúc {actualHandoverAt:HH:mm dd/MM/yyyy}. Chúc Quý khách có trải nghiệm thuê tốt đẹp.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`

### 3.7. `HANDOVER_OVERDUE_SUPPLIER` (Supplier)
- **Title**: `CẢNH BÁO: Quá hạn bàn giao thiết bị đơn thuê #{orderCode}`
- **Body**: `Đơn thuê {orderCode} đã quá hạn bàn giao dự kiến ({expectedStartDate:dd/MM/yyyy}). Vui lòng tiến hành bàn giao ngay hoặc liên hệ hỗ trợ.`
- **IsActionable**: `true`
- **CallToAction**: `Thực hiện bàn giao`
- **DeepLinkUrl**: `/supplier/sub-orders/{rentalOrderId}/handover`
- **Email Subject**: `[Khẩn cấp] Đơn thuê #{orderCode} đã quá hạn bàn giao thiết bị`

### 3.8. `HANDOVER_OVERDUE_RENTER` (Renter)
- **Title**: `Thông báo chậm trễ bàn giao đơn thuê #{orderCode}`
- **Body**: `Đơn thuê {orderCode} hiện chưa được bàn giao theo lịch hẹn ({expectedStartDate:dd/MM/yyyy}). DPRental đang đôn đốc nhà cung cấp xử lý.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`

### 3.9. `RETURN_CONFIRMATION_OVERDUE_SUPPLIER` (Supplier)
- **Title**: `CẢNH BÁO: Quá hạn kiểm tra thiết bị hoàn trả đơn thuê #{orderCode}`
- **Body**: `Đã hết hạn xác nhận hoàn trả ({confirmationDeadlineAt:HH:mm dd/MM/yyyy}) cho đơn thuê {orderCode}. Vui lòng hoàn tất kiểm tra tình trạng máy.`
- **IsActionable**: `true`
- **CallToAction**: `Kiểm tra hoàn trả`
- **DeepLinkUrl**: `/supplier/returns/{returnRequestId}`
- **Email Subject**: `[Khẩn cấp] Quá hạn xác nhận hoàn trả thiết bị đơn thuê #{orderCode}`

### 3.10. `RETURN_CONFIRMATION_OVERDUE_RENTER` (Renter)
- **Title**: `Đang chờ nhà cung cấp xác nhận hoàn trả đơn thuê #{orderCode}`
- **Body**: `Nhà cung cấp đang chậm trễ kiểm tra thiết bị hoàn trả cho đơn thuê {orderCode}. Hệ thống sẽ tự động hoàn tất nếu nhà cung cấp không phản hồi.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`

### 3.11. `ORDER_COMPLETED` (Renter)
- **Title**: `Đơn thuê #{orderCode} đã hoàn tất`
- **Body**: `Đơn thuê {orderCode} đã hoàn tất toàn bộ chu kỳ thuê và quyết toán cọc vào lúc {completedAt:HH:mm dd/MM/yyyy}. Cảm ơn Quý khách!`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`
- **Email Subject**: `[DPRental] Đơn thuê #{orderCode} đã hoàn tất chu kỳ thuê`

### 3.12. `LATE_PAYMENT_REFUND_REQUESTED` (Renter) — CRITICAL FINANCIAL WORDING
- **Title**: `Yêu cầu hoàn tiền đơn thuê #{orderCode}`
- **Body**: `Khoản thanh toán {totalRefundAmount:N0} VND cho đơn thuê {orderCode} (do thanh toán sau khi đơn đã kết thúc) đang được xử lý hoàn tiền. Vui lòng theo dõi tiến độ xử lý tại chi tiết đơn.`
- **IsActionable**: `false`
- **DeepLinkUrl**: `/renter/orders/{rentalOrderId}`
- **FINANCIAL WORDING INVARIANT**:
  - The notification body **MUST ONLY ASSERT**: *"Yêu cầu hoàn tiền đang được xử lý"* or *"Khoản thanh toán đang chờ xử lý hoàn tiền"*.
  - It is **STRICTLY FORBIDDEN** to assert *"Đã hoàn tiền thành công"* or *"Tiền đã chuyển về tài khoản của bạn"* because PaymentService has only received the request and has not settled the refund.

---

## 4. Stale Action Suppression Matrix

When Kafka events arrive out of order, or after replay, NotificationService checks the aggregate watermark (`notification.aggregate_watermarks`).

| Incoming Event Type | Aggregate Watermark State | Stale Suppression Action |
|---|---|---|
| `RentalOrderCreated` (`orderVersion = 1`) | Watermark terminal state is `PAID` (`orderVersion >= 2`) | **Suppress Action**: Remove CTA (`isActionable = false`, `actionUrl = null`). Change body to informational copy: *"Đơn thuê đã được tạo và thanh toán hoàn tất (thông tin lưu trữ)."* Deep link points to order details. |
| `RentalOrderCreated` (`orderVersion = 1`) | Watermark terminal state is `EXPIRED` or `CANCELLED` | **Suppress Action**: Remove CTA. Change body to: *"Đơn thuê đã được tạo nhưng đã hết hạn/hủy (thông tin lưu trữ)."* |
| `RentalSupplierConfirmationRequested` | Watermark terminal state is `EXPIRED` or `CANCELLED` | **Suppress Action**: Remove CTA button. Disable approval deep link. Mark notification as informational audit record. |
| `RentalHandoverOverdue` | Watermark terminal state is `COMPLETED` or `CANCELLED` | **Suppress Action & Email**: Suppress email dispatch. Mark notification as resolved. |
| `ReturnConfirmationOverdue` | Watermark terminal state is `COMPLETED` | **Suppress Action & Email**: Suppress email dispatch. Mark notification as resolved. |
| Any Event | `incoming.orderVersion < watermark.last_order_version` | Mark as stale; render informational variant without urgent action triggers. |
| Same-Version Sibling (`incoming.orderVersion == watermark.last_order_version`) | Any | **Process Normally**: Check `inbox_receipts` deduplication by `messageId`. If `messageId` is distinct, process and insert notification. |
