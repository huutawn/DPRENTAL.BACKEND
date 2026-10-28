# Direct Handover Lifecycle (Vòng Đời Bàn Giao Trực Tiếp A → B)

Tài liệu mô tả kiến trúc, vòng đời xử lý, chính sách thời gian và cơ chế an toàn cho luồng bàn giao trực tiếp giữa Nhà cung cấp (Supplier) và Người thuê (Renter) trong `RentalService`.

---

## 1. Tổng quan luồng bàn giao

```
Supplier chấp nhận Đơn con
        │
        ▼
Mục thuê chuyển AwaitingHandover
        │
        ▼
Supplier gửi bằng chứng bàn giao (POST /items/{itemId}/handover)
        │ ── Lưu immutable snapshot + receipt + outbox lệnh MarkAssetsHandedOver (Transaction atomic)
        ▼
Trả về HTTP 202 Accepted {"status": "Processing"}
        │
        ▼
InventoryService xử lý & gửi Kafka event xác nhận
        │
        ├── Thành công ──► Tạo HandoverRecord + gắn evidence từng máy + Item chuyển InRental + phát event RentalItemHandoverCompleted
        │
        └── Từ chối   ──► Receipt đánh dấu Failed, Item giữ nguyên AwaitingHandover (cho phép Supplier nộp lại bằng chứng)
        │
        ▼
Renter xác nhận nhận thiết bị (POST /items/{itemId}/acknowledge-receipt)
        │ ── Ghi nhận người nhận, thời điểm và note vào HandoverRecord
        ▼
Trả về HTTP 200 OK (Idempotent: replay giữ nguyên note và thời điểm ban đầu, không đổi state/version/audit)
```

---

## 2. Chi tiết các Endpoint & Hợp đồng API

### 2.1. POST `/items/{itemId}/handover`

- **Người gọi hợp lệ**: Supplier sở hữu mục thuê (`RentalItem`).
- **Header bắt buộc**: `Idempotency-Key` (chuỗi không rỗng, tối đa 64 ký tự).
- **Quy tắc tiền điều kiện (Preconditions)**:
  1. Mục thuê phải đang ở trạng thái `AwaitingHandover`.
  2. Chưa vượt quá hạn chót hủy tự động (`HandoverDeadlinePolicy.IsAutoExpireDue`).
  3. Thời điểm bàn giao thực tế (`ActualHandoverAt`): bắt buộc có, **không được ở tương lai** so với thời điểm máy chủ UTC (không có dung sai future tolerance).
  4. Cờ `Confirmed = true`.
  5. Số lượng thiết bị bàn giao trong request phải bằng `Quantity` của mục thuê; các `AssignmentId` phải tương ứng duy nhất và đầy đủ với các máy thuộc mục thuê (`UnitAssignments`).
  6. Toàn bộ máy được bàn giao phải kèm bằng chứng hình ảnh (`MediaIds` không rỗng); các ảnh phải hợp lệ và thuộc quyền sở hữu của Supplier (`RentalEvidenceValidator`). Quá trình xác minh media diễn ra trước khi mở database transaction.
- **Ý nghĩa trạng thái HTTP 202 Accepted**:
  - Yêu cầu được chấp nhận xử lý. Snapshot bằng chứng được ghi nhận bất biến trong `RentalOperationReceipt`.
  - Chưa tạo `HandoverRecord` và chưa chuyển trạng thái mục thuê sang `InRental` tại thời điểm này.
  - Phản hồi trả về: `{"handoverRecordId": null, "actualHandoverAt": "...", "status": "Processing"}`.
- **Xử lý Replay & Trùng lặp**:
  - Cùng key, cùng payload, đang chờ Inventory: trả về HTTP 202 Accepted với trạng thái operation hiện có (`Processing` hoặc `Pending`).
  - Cùng key, cùng payload, đã hoàn tất thành công: trả về HTTP 202 Accepted kèm `handoverRecordId` đã tạo và status `Succeeded`.
  - Cùng key, khác payload / action / item: trả về HTTP 409 Conflict (`IDEMPOTENCY_CONFLICT`).
  - Key mới khi lệnh trước đang `Pending`, `Processing` hoặc `MANUAL_RECONCILIATION_REQUIRED`: trả về HTTP 409 Conflict (`HANDOVER_IN_PROGRESS`).
  - Key mới sau khi bàn giao đã hoàn tất thành công: trả về HTTP 409 Conflict (`INVALID_STATUS`).

### 2.2. POST `/items/{itemId}/acknowledge-receipt`

- **Người gọi hợp lệ**: Renter sở hữu đơn hàng (`RentalOrder`).
- **Tiền điều kiện**: Mục thuê đã có `HandoverRecord` (tức InventoryService đã xác nhận bàn giao). Nếu chưa có, trả về HTTP 409 Conflict (`HANDOVER_NOT_RECORDED`).
- **Hành vi**:
  - Lần gọi đầu tiên: Lưu `RenterAcknowledgedById`, `RenterAcknowledgedAt` (thời điểm server) và `RenterAcknowledgementNote` vào `HandoverRecord`. Ghi nhận 1 state transition audit `RenterAcknowledgeReceipt` (`FromState = ToState`), tăng `Version` của order, commit atomically và phát thông báo qua SignalR Hub.
  - Không thay đổi trạng thái của mục thuê (`RentalItemStatus`) hay đơn hàng (`RentalOrderStatus`).
  - Replay (cùng Renter gọi lại, kể cả khi gửi note khác): Trả về HTTP 200 OK với thông tin xác nhận ban đầu; không ghi đè dữ liệu, không tăng version, không ghi thêm audit và không phát thông báo lặp lại.
  - Hỗ trợ xác nhận muộn: Renter vẫn có thể xác nhận sau khi mục thuê đã chuyển sang trạng thái tiếp theo hoặc hoàn tất (`Completed`), miễn là `HandoverRecord` tồn tại.

### 2.3. POST `/items/{itemId}/cancel-not-delivered`

- **Người gọi hợp lệ**: Renter sở hữu đơn hàng.
- **Header bắt buộc**: `Idempotency-Key` (chuỗi không rỗng, tối đa 64 ký tự).
- **Tiền điều kiện**:
  1. Mục thuê đang ở trạng thái `AwaitingHandover`.
  2. Đã tới mốc mở quyền hủy: 00:00 ngày D+1 theo giờ Việt Nam (`HandoverDeadlinePolicy.IsNoticeDue`), trong đó D là ngày bắt đầu thuê dự kiến.
  3. Không có lệnh bàn giao nào đang `Pending`, `Processing` hoặc cần đối soát (`MANUAL_RECONCILIATION_REQUIRED`). Nếu có, trả về HTTP 409 Conflict (`HANDOVER_IN_PROGRESS`).
- **Hành vi**:
  - Trả về HTTP 202 Accepted.
  - Chuyển riêng mục thuê sang trạng thái `Cancelled` (các mục thuê khác giữ nguyên).
  - Lên lịch giải phóng vị trí thiết bị (`ReleaseAllocation`) và hoàn tiền 100% tiền thuê + cọc của mục thuê (`RequestRefund`).
  - Việc chuyển trạng thái, ghi nhận receipt, lên lịch release/refund và enqueue sự kiện `RentalItemCancelled` được thực thi **nguyên tử trong một database transaction duy nhất**.
  - Replay: Trả về kết quả hủy ban đầu, không tạo thêm lệnh release hay refund.

---

## 3. Xác nhận bất đồng bộ từ InventoryService

Khi nhận được Kafka event `InventoryAllocationCommandSucceeded` đối với lệnh `MarkAssetsHandedOver`:

1. **Đối soát khóa và danh tính**: Kiểm tra `rentalOrderId`, `rentalItemId`, `commandType` và `operationKey` khớp với `RentalOperationReceipt` đã lưu.
2. **Khử trùng lặp terminal**:
   - Nếu receipt đã ở trạng thái `Succeeded` với payload tương đương: bỏ qua an toàn (`NoChange`).
   - Nếu kết quả đến có trạng thái đối nghịch với trạng thái terminal đã chốt: báo lỗi xung đột (`INVENTORY_RESULT_CONFLICT`) và chuyển thông điệp vào Dead-Letter Queue (DLQ).
3. **Áp dụng bàn giao**:
   - Tải `HandoverEvidenceSnapshot` từ `receipt.PayloadJson`.
   - Nếu bằng chứng bị thiếu hoặc không hợp lệ: chuyển receipt sang `MANUAL_RECONCILIATION_REQUIRED` để đối soát thủ công; không tự động chuyển trạng thái item.
   - Nếu mục thuê không còn ở trạng thái `AwaitingHandover` (ví dụ do sự cố đã bị hủy/hết hạn trước đó): chuyển receipt sang `MANUAL_RECONCILIATION_REQUIRED`; không ghi đè trạng thái.
   - Khi hợp lệ: Tạo duy nhất một bản ghi `HandoverRecord`, cập nhật tình trạng và gắn ảnh bàn giao cho từng máy (`RentalUnitAssignment`), chuyển trạng thái mục thuê sang `InRental`.
   - Cập nhật trạng thái tổng thể của đơn hàng (`RentalOrderStatusPolicy.Apply`).
   - Đánh dấu receipt `Succeeded`.
4. **Commit nguyên tử (Atomic Commit)**:
   - Enqueue sự kiện Kafka `RentalItemHandoverCompleted` vào bảng outbox.
   - Lưu toàn bộ thay đổi dữ liệu aggregate và outbox trong một transaction database duy nhất.
   - Khi có bất kỳ lỗi lưu trữ nào, rollback toàn bộ và discard tracking để đảm bảo tính nhất quán.

Khi nhận được `InventoryAllocationCommandFailed`:
- Đánh dấu receipt là `Failed` kèm mã lỗi và thông điệp từ InventoryService.
- Mục thuê tiếp tục giữ trạng thái `AwaitingHandover`.
- Supplier được phép nộp lại bằng chứng bàn giao mới với `Idempotency-Key` mới nếu vẫn chưa qua hạn chót hủy tự động.

---

## 4. Chính sách Deadline & Hết hạn tự động (Auto-Expiry)

- **Mốc D (Start Date)**: Ngày bắt đầu thuê dự kiến theo giờ Việt Nam.
- **Mốc D+1 (Notice Due)**: 00:00 ngày kế tiếp ngày D theo giờ Việt Nam (`Asia/Ho_Chi_Minh`, tức 17:00 UTC ngày D). Từ thời điểm này, Renter có quyền hủy đơn nếu chưa nhận được thiết bị.
- **Mốc Auto-Expire**: Mặc định 24 giờ sau mốc D+1 (tương ứng `AutoExpireGraceHours = 24`). Khi đến mốc này:
  - Hệ thống (`HandoverOverdueWorker`) tự động kết thúc mục thuê chưa bàn giao sang trạng thái `Expired`.
  - Mọi yêu cầu bàn giao mới từ Supplier gửi sau mốc này đều bị từ chối với mã lỗi `HANDOVER_DEADLINE_PASSED`.
  - Nếu mục thuê đang có bàn giao dở dang hoặc cần đối soát (`MANUAL_RECONCILIATION_REQUIRED`), worker bỏ qua mục thuê đó để tránh hủy nhầm thiết bị đã giao thực tế.

---

## 5. Giới hạn tương thích dữ liệu

- **Receipt persisted cũ (Legacy)**: Đối với các mục thuê đã tạo trước khi chuyển đổi luồng trì hoãn bàn giao, nếu `item.HandoverRecord` đã tồn tại sẵn trong cơ sở dữ liệu, handler vẫn hỗ trợ nhánh hoàn tất cũ bằng cách xác nhận các unit assignment và kích hoạt `ApplyHandover` mà không gây lỗi phân tích snapshot.
