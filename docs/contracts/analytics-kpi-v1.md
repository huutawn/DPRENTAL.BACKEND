# Contract: KPI & hợp đồng dữ liệu báo cáo (Analytics) — v1

| Mục | Giá trị |
| --- | --- |
| Trạng thái | **PROPOSED**. Chuyển sang **FROZEN** khi Rental và Payment xác nhận mục 9 |
| Jira | DPR-253 (contract) → DPR-258 (read model), thuộc epic DPR-254 |
| Bên sở hữu | AnalyticsService |
| Nguồn dữ liệu | RentalService (`rental.events.v1`), PaymentService (`payment.events.v1`) |
| Bên dùng | Dashboard Admin / Supplier (FE), QA, PO |

---

## 1. Mục tiêu và phạm vi

Khóa **một ý nghĩa duy nhất** cho từng con số báo cáo, để Rental, Payment, Analytics và Dashboard không hiểu khác nhau.

**Trong phạm vi:**
- Định nghĩa, công thức, nguồn chuẩn và mốc thời gian của từng KPI (mục 5).
- Biểu đồ nào dùng ngày nào (mục 7).
- Các field producer cần bổ sung (mục 9).

**Ngoài phạm vi:**
- Route và response của API dashboard (task riêng).
- Schema bảng read model (DPR-258).
- KPI tồn kho và tỉ lệ sử dụng thiết bị (thuộc InventoryService).

---

## 2. Thuật ngữ

| Thuật ngữ | Nghĩa trong tài liệu này |
| --- | --- |
| Đơn (order) | Một `RentalOrder` |
| Dòng thuê / item | Một `RentalItem`: một thiết bị (listing) của một NCC trong đơn, có `Quantity` ≥ 1 |
| Lượt thuê | 1 item = 1 lượt, **không** nhân với `Quantity` |
| Item đã trả tiền | Item thuộc đơn đã nhận `RentalOrderPaid` |
| GMV | Gross Merchandise Value: tổng **tiền thuê** khách đã trả, **không gồm cọc**, chưa trừ hoàn tiền |
| Period | KPI đếm hoặc cộng các sự kiện có mốc thời gian nằm trong khoảng `from`–`to` |
| Snapshot | KPI phản ánh trạng thái tại thời điểm gọi, bỏ qua `from`–`to` |
| Ngày VN | Ngày lịch theo múi giờ `Asia/Ho_Chi_Minh` |

---

## 3. Quy ước chung

### 3.1. Thời gian

- Mọi timestamp lưu ở dạng **UTC**. Envelope có thể mang offset (ví dụ `+07:00`), Analytics chuẩn hóa về UTC khi nhận.
- Mốc thời gian của KPI lấy từ **field nghiệp vụ trong payload** (`paidAt`, `completedAt`...). **Không** dùng thời điểm Analytics nhận message.
- Chỉ dùng `occurredAt` của envelope khi payload không có mốc nghiệp vụ, và chỉ khi được ghi rõ trong mục 4 (hiện chỉ có `RentalItemCompleted`, xem yêu cầu R2).
- Dashboard nhóm ngày theo **`Asia/Ho_Chi_Minh`**. Ví dụ: `2026-11-10T17:30:00Z` thuộc ngày VN **2026-11-11**.
- `from`/`to` là ngày VN, tính cả hai đầu. Khoảng UTC tương ứng là `[đầu ngày from (VN), đầu ngày to + 1 (VN))`.
- Bucket: `day` | `week` | `month`. Tuần bắt đầu thứ Hai, tháng bắt đầu ngày 1. Bucket không có dữ liệu vẫn trả giá trị `0`.

### 3.2. Tiền

- Đơn vị **VND, số nguyên**. Payload `decimal` có phần lẻ bị coi là lỗi dữ liệu: không làm tròn, không đưa vào KPI.
- **Tiền cọc không bao giờ là doanh thu, cũng không phải GMV.** Cọc chỉ xuất hiện ở `DepositCollected` và trong phần hoàn tiền.

### 3.3. Nguồn chuẩn

- Mỗi KPI có **đúng một** nguồn chuẩn (cột "Nguồn chuẩn" ở mục 5).
- Analytics **chỉ ghi nhận** số liệu do producer đã chốt. Analytics không tự tính phí nền tảng, tiền phải trả NCC, khấu trừ, hay trạng thái đơn.
- Khi producer chưa cung cấp field cần thiết, KPI được đánh dấu **"Chưa có nguồn"**. Analytics không suy diễn hay thay bằng công thức tự đặt.

### 3.4. Trùng lặp và thứ tự event (tóm tắt, chi tiết ở DPR-258)

- Rental: dedupe theo `messageId` (tất định). Thứ tự thay đổi của một đơn theo `orderVersion`. Event cũ đến sau **không** làm lùi trạng thái.
- Payment: dedupe theo **khóa tự nhiên** (`settlementRef`, `refundRef` + `rentalItemId`, `payoutRef`), vì `messageId` hiện được sinh ngẫu nhiên.
- Replay cùng một message không làm thay đổi bất kỳ tổng nào.

---

## 4. Event nguồn

| Event | Topic | Producer | Field dùng cho KPI | Mốc thời gian | Khóa dedupe | Trạng thái |
| --- | --- | --- | --- | --- | --- | --- |
| `RentalOrderCreated` | `rental.events.v1` | Rental | `rentalOrderId`, `orderCode`, `renterId`, `createdAt`, `items[]`: `rentalItemId`, `supplierId`, `equipmentListingId`, `equipmentTypeId/Name`, `brandId/Name`, `model`, `quantity`, `rentalAmount`, `depositAmount` | `createdAt` | `messageId` | Có |
| `RentalOrderPaid` | `rental.events.v1` | Rental | `rentalOrderId`, `paidAt` | `paidAt`, áp dụng cho **mọi item** của đơn | `messageId` | Có |
| `RentalItemPaid` | `rental.events.v1` | Rental | `rentalItemId` | Không có. Dùng `RentalOrderPaid.paidAt` | `messageId` | Có |
| `RentalItemConfirmed` | `rental.events.v1` | Rental | `rentalItemId`, `supplierId` | `decisionAt` | `messageId` | Có |
| `RentalItemRejected` | `rental.events.v1` | Rental | `rentalItemId`, `supplierId`, `reason` | `decisionAt` | `messageId` | Có |
| `RentalItemExpired` | `rental.events.v1` | Rental | `rentalItemId`, `supplierId`, `reason` | `expiredAt` | `messageId` | Có |
| `RentalItemCancelled` | `rental.events.v1` | Rental | `rentalItemId`, `reason` | `cancelledAt` | `messageId` | Có |
| `RentalItemHandoverCompleted` | `rental.events.v1` | Rental | `rentalItemId`, `supplierId`, `quantity` | `actualHandoverAt` | `messageId` | Có |
| `RentalItemReturned` | `rental.events.v1` | Rental | `rentalItemId` | `returnsCompletedAt` | `messageId` | Có |
| `RentalItemCompleted` | `rental.events.v1` | Rental | `rentalItemId` | Tạm dùng `occurredAt` của envelope (Rental ghi cùng transaction hoàn thành), chờ R2 | `messageId` | Có, thiếu mốc (R2) |
| `RentalOrderCompleted` | `rental.events.v1` | Rental | `rentalOrderId` | `completedAt` | `messageId` | Có |
| `RentalOrderCancelled` | `rental.events.v1` | Rental | `rentalOrderId`, `reason` | `cancelledAt` | `messageId` | Có |
| `RentalOrderExpired` | `rental.events.v1` | Rental | `rentalOrderId`, `reason` | `expiredAt` | `messageId` | Có, chưa phát khi rollup do NCC không xác nhận (R1) |
| `RentalOrderRejected` | `rental.events.v1` | Rental | `rentalOrderId` | `rejectedAt` | `messageId` | **Chưa có (R1)** |
| `RentalSettled` | `payment.events.v1` | Payment | `settlementRef`, `rentalItemId`, `finalDeductionAmount`, `refundedDepositAmount` (= DepositRefundDue); **cần thêm** `platformFeeAmount`, `supplierPayable`, `supplierId`, `rentalAmount`, `depositAmount` | `completedAt` | `settlementRef` | **Thiếu field (P1)** |
| `RefundCompleted` | `payment.events.v1` | Payment | `refundRef`, `rentalOrderId`, `items[]`: `rentalItemId`, `refundedRentalAmount`, `refundedDepositAmount`; **cần thêm** `kind` | `completedAt` | `refundRef` + `rentalItemId` | Có, thiếu `kind` (P4) |
| `SupplierPayoutCompleted` | `payment.events.v1` | Payment | `payoutRef`, `supplierId`, `rentalItemId`, `amount` | `completedAt` | `payoutRef` | **Chưa có (P3)** |
| `RefundRequestedFinancial` | `payment.analytics.v1` | Payment | `refundRef`, `rentalOrderId`, `rentalItemId?`, `refundKind`, `amount`, `currency` | `requestedAt` | `refundRef` | Analytics consumer hỗ trợ; Payment producer cần phát event |

**Không được lưu** từ bất kỳ event nào: số tài khoản ngân hàng, số điện thoại người nhận, địa chỉ giao hàng, nội dung chuyển khoản, raw payload SePay, `SerialNumber`, `AssetCode`, và raw JSON của message.

---

## 5. Định nghĩa KPI

Ký hiệu "Chưa có nguồn (Px/Rx)": KPI đã được định nghĩa nhưng chưa tính được cho đến khi producer làm yêu cầu tương ứng ở mục 9.

### 5.1. Đơn thuê

| KPI | Định nghĩa | Công thức | Nguồn chuẩn | Mốc thời gian | Loại |
| --- | --- | --- | --- | --- | --- |
| `TotalOrders` | Số đơn được tạo | `COUNT(order)` | `RentalOrderCreated` | `CreatedAt` | Period |
| `PaidOrders` | Số đơn đã thanh toán | `COUNT(order) WHERE PaidAt IS NOT NULL` | `RentalOrderPaid` | `PaidAt` | Period |
| `ActiveOrders` | Số đơn đã thanh toán và chưa kết thúc tại cuối ngày `to` theo giờ Việt Nam | `COUNT(order) WHERE PaidAt < endUtc AND các mốc CompletedAt/CancelledAt/ExpiredAt/RejectedAt đều null hoặc >= endUtc` | Tổng hợp từ các event đơn; khi chưa có `RejectedAt`, loại status `REJECTED` | Cuối ngày `to` | As-of snapshot |
| `CompletedOrders` | Số đơn hoàn thành | `COUNT(order)` | `RentalOrderCompleted` | `CompletedAt` | Period |
| `CancelledOrders` | Số đơn bị hủy | `COUNT(order)` | `RentalOrderCancelled` | `CancelledAt` | Period |
| `RejectedOrders` | Số đơn mà **mọi** item đều bị NCC từ chối, theo đúng luật rollup của Rental (`RentalOrderStatusPolicy`). Đơn chỉ có một phần item bị từ chối **không** thuộc KPI này. Đơn có item bị từ chối và item hết hạn được Rental tính là `Expired` | `COUNT(order)` | `RentalOrderRejected` — Chưa có nguồn (R1) | `RejectedAt` | Period |
| `ExpiredOrders` | Số đơn có trạng thái `Expired` theo Rental: quá hạn thanh toán (`PAYMENT_EXPIRED`), quá hạn giao (`HANDOVER_EXPIRED`), hoặc không còn item nào hoạt động và có item hết hạn xác nhận (`SUPPLIER_CONFIRMATION_EXPIRED`) | `COUNT(order)` | `RentalOrderExpired`. Hiện **chỉ đủ** cho `PAYMENT_EXPIRED` và `HANDOVER_EXPIRED`; trường hợp rollup do NCC không xác nhận cần R1 | `ExpiredAt` | Period |

### 5.2. Tài chính

| KPI | Định nghĩa | Công thức | Nguồn chuẩn | Mốc thời gian | Loại |
| --- | --- | --- | --- | --- | --- |
| `GrossRentalValue` (GMV) | Tổng tiền thuê khách đã trả. **Không gồm cọc.** **Có chứa** item bị hủy, từ chối hoặc hoàn tiền sau đó, nên dùng để đo **nhu cầu**, không phải doanh số thực | `Σ item.RentalAmount` của item đã trả tiền | Rental: `RentalOrderCreated.items[].rentalAmount` + `RentalOrderPaid` | `PaidAt` | Period |
| `GrossRentalValueRefunded` | Phần tiền thuê trong GMV đã được hoàn cho khách. Chỉ gồm **tiền thuê**, không gồm hoàn cọc. Gán vào **ngày thanh toán của item**, để nằm cùng cột với GMV | `Σ RefundCompleted.items[].refundedRentalAmount` | Payment `RefundCompleted`, nối với ngày thanh toán của item | `PaidAt` của item | Period |
| `CompletedRentalValue` | Tiền thuê của các item đã hoàn thành. Đây là **doanh số thuê chính**: không bao giờ chứa item bị hủy, và số của ngày cũ không thay đổi về sau | `Σ item.RentalAmount` của item đã hoàn thành | Rental `RentalItemCompleted` | `CompletedAt` của item | Period |
| `DepositCollected` | Tổng tiền cọc khách đã nộp. Là tiền giữ hộ, **không phải doanh thu** | `Σ item.DepositAmount` của item đã trả tiền | Rental: `RentalOrderCreated.items[].depositAmount` + `RentalOrderPaid` | `PaidAt` | Period |
| `PlatformRevenue` | Phí nền tảng thực thu. **Chỉ lấy từ Payment**, Analytics không tự nhân tỉ lệ phí | `Σ PlatformFeeAmount` | Payment `RentalSettled` — Chưa có nguồn (P1) | `Settlement.CompletedAt` | Period |
| `DeductionAmount` | Tổng khấu trừ cọc sau quyết toán (bồi thường hư hỏng) | `Σ FinalDeductionAmount` | Payment `RentalSettled` | `Settlement.CompletedAt` | Period |
| `RefundAmount` | Tổng tiền đã hoàn cho khách (tiền thuê + tiền cọc), tính theo **ngày hoàn tiền** | `Σ (refundedRentalAmount + refundedDepositAmount)` | Payment `RefundCompleted` | `Refund.CompletedAt` | Period |
| `PendingRefundAmount` | Tiền đã phát sinh nghĩa vụ hoàn nhưng chưa hoàn xong | `Σ max(requested.amount − completed.amount, 0)` theo `refundRef` | Payment `RefundRequestedFinancial` và `RefundCompletedFinancial`; cần Payment producer phát yêu cầu hoàn | Lúc gọi | Snapshot |
| `SupplierPayable` | Tiền nền tảng phải trả NCC sau quyết toán | `Σ SupplierPayable` | Payment `RentalSettled` — Chưa có nguồn (P1) | `Settlement.CompletedAt` | Period |
| `SupplierPayoutCompleted` | Tiền đã thực chuyển cho NCC | `Σ Amount WHERE Status = COMPLETED` | Payment `SupplierPayoutCompleted` — Chưa có nguồn (P3) | `Payout.CompletedAt` | Period |

Ghi chú:
- `RefundAmount` **chỉ** lấy từ `RefundCompleted`. `RentalSettled.refundedDepositAmount` là số cọc **phải hoàn** (DepositRefundDue), chưa phải tiền đã hoàn, nên **không** cộng vào `RefundAmount`.
- `GrossRentalValueRefunded` và `RefundAmount` dùng cùng dữ liệu nhưng khác mốc ngày và khác phạm vi (chỉ tiền thuê, hay tiền thuê cộng cọc). Hai KPI không thay thế nhau.
- "Phần còn lại" của GMV = `GrossRentalValue − GrossRentalValueRefunded`, do dashboard tự trừ, không phải KPI riêng.

### 5.3. Thiết bị

| KPI | Định nghĩa | Công thức | Nguồn chuẩn | Mốc thời gian | Loại |
| --- | --- | --- | --- | --- | --- |
| `RentalItemCount` | Số lượt thuê | `COUNT(item)` đã trả tiền | Rental `RentalOrderCreated` + `RentalOrderPaid` | `PaidAt` | Period |
| `RentedQuantity` | Số máy **đã thật sự bàn giao** cho khách. Item bị từ chối hoặc hủy trước khi giao không được tính | `Σ Quantity` | Rental `RentalItemHandoverCompleted` | `HandoverAt` (= `actualHandoverAt`) | Period |
| `TopEquipmentTypes` | Bảng **"Loại thiết bị thuê nhiều nhất"** | Xem quy tắc xếp hạng bên dưới, nhóm theo `EquipmentTypeId` | Rental | `PaidAt` | Period |
| `TopListings` | Bảng **"Listing thuê nhiều nhất"** | Nhóm theo `EquipmentListingId` | Rental | `PaidAt` | Period |
| `TopBrands` | Bảng **"Hãng thuê nhiều nhất"** | Nhóm theo `BrandId` | Rental | `PaidAt` | Period |

**Quy tắc xếp hạng chung cho các bảng Top:**
1. Tập dữ liệu: các item đã trả tiền, có `PaidAt` trong khoảng `from`–`to`.
2. Xếp theo `RentalItemCount` (số lượt thuê) giảm dần. Hòa thì xếp theo `RentedQuantity` (đếm các item của nhóm đã bàn giao trước cuối ngày `to`), rồi theo Id tăng dần để kết quả không thay đổi do vòng đời item sau khoảng đã chọn.
3. Mặc định lấy top 10.
4. Mỗi dòng trả kèm `GrossRentalValue` để tham khảo. Giá trị này **không** dùng để xếp hạng.
5. Item thiếu snapshot (`EquipmentTypeId` / `BrandId` null) được gom vào nhóm **"Không xác định"**. Không tạo giá trị giả.
6. Dashboard **không** được gọi chung là "Top thiết bị". Luôn ghi rõ là "thuê nhiều nhất".

### 5.4. Nhà cung cấp

Mọi KPI trong nhóm này lọc theo `SupplierId`. Khi payload không có `supplierId`, Analytics lấy nó từ item tương ứng (do `RentalOrderCreated` cung cấp).

| KPI | Định nghĩa | Công thức | Nguồn chuẩn | Mốc thời gian | Loại |
| --- | --- | --- | --- | --- | --- |
| `AcceptedCount` | Số item NCC chấp nhận | `COUNT(item)` | `RentalItemConfirmed` | `DecisionAt` | Period |
| `RejectedCount` | Số item NCC từ chối | `COUNT(item) WHERE reason = SUPPLIER_REJECTED` | `RentalItemRejected` | `DecisionAt` | Period |
| `ConfirmationTimeoutCount` | Số item NCC không xác nhận kịp hạn | `COUNT(item) WHERE reason = SUPPLIER_CONFIRMATION_EXPIRED` | `RentalItemExpired` | `ExpiredAt` | Period |
| `CompletedRentalCount` | Số item NCC đã hoàn thành cho thuê | `COUNT(item)` | `RentalItemCompleted` | `CompletedAt` của item | Period |
| `RentalValue` | Tiền thuê của các item đã trả tiền thuộc NCC (GMV của NCC) | `Σ item.RentalAmount` | Rental `RentalOrderCreated` + `RentalOrderPaid` | `PaidAt` | Period |
| `SupplierPayable` | Như mục 5.2, lọc theo NCC | `Σ SupplierPayable` | Payment `RentalSettled` — Chưa có nguồn (P1) | `Settlement.CompletedAt` | Period |

---

## 6. Quy tắc bất biến

1. Tiền cọc **không bao giờ** được tính vào GMV, doanh số hay doanh thu.
2. `PlatformRevenue` và `SupplierPayable` **chỉ** lấy từ Payment. Analytics không nhân tỉ lệ phí, không lấy tiền thuê trừ phí.
3. Analytics **không tự tính lại settlement**: khấu trừ, cọc phải hoàn, phí, tiền phải trả NCC đều lấy nguyên từ `RentalSettled`.
4. Analytics **không tự suy ra trạng thái đơn** từ trạng thái item. Trạng thái đơn chỉ đến từ event cấp đơn của Rental.
5. Mỗi KPI có đúng một nguồn chuẩn. Số liệu từ nguồn khác chỉ dùng để đối soát (mục 8).
6. Mốc thời gian lấy từ payload. Ngoại lệ được liệt kê tường minh ở mục 4.
7. Số đã ghi nhận cho một ngày **không được giảm vì event đến muộn**. Ngoại lệ duy nhất là phần "đã hoàn" trong cột GMV (`GrossRentalValueRefunded`): phần này có thể tăng khi có hoàn tiền muộn, nhưng tổng chiều cao cột GMV không đổi.

---

## 7. Biểu đồ → ngày

| Biểu đồ | KPI | Cột ngày |
| --- | --- | --- |
| Đơn tạo theo ngày | `TotalOrders` | `RentalOrder.CreatedAt` |
| Đơn thanh toán theo ngày | `PaidOrders` | `RentalOrder.PaidAt` |
| Đơn hoàn thành / hủy / hết hạn / bị từ chối | `CompletedOrders` / `CancelledOrders` / `ExpiredOrders` / `RejectedOrders` | `CompletedAt` / `CancelledAt` / `ExpiredAt` / `RejectedAt` của đơn |
| GMV theo ngày: cột chồng 2 phần "còn lại" và "đã hoàn" | `GrossRentalValue`, `GrossRentalValueRefunded` | `PaidAt` của item |
| **Doanh số thuê** (biểu đồ doanh số chính) | `CompletedRentalValue` | `CompletedAt` của item |
| Cọc đã thu | `DepositCollected` | `PaidAt` của item |
| Doanh thu nền tảng | `PlatformRevenue` | `Settlement.CompletedAt` |
| Khấu trừ cọc | `DeductionAmount` | `Settlement.CompletedAt` |
| Phải trả NCC | `SupplierPayable` | `Settlement.CompletedAt` |
| Hoàn tiền | `RefundAmount` | `Refund.CompletedAt` |
| Đã chi trả NCC | `SupplierPayoutCompleted` | `SupplierPayout.CompletedAt` |
| Số máy đã cho thuê | `RentedQuantity` | `HandoverAt` của item |
| Bảng Top "thuê nhiều nhất" | `Top*` | `PaidAt` của item |
| Hiệu suất NCC | `AcceptedCount`, `RejectedCount`, `ConfirmationTimeoutCount` | `DecisionAt` / `ExpiredAt` của item |

---

## 8. Ví dụ đối chiếu (dùng cho QA)

Giả định phí nền tảng 10%. Tỉ lệ thật do Payment quyết định, Analytics chỉ ghi nhận số Payment gửi.

Đơn **X** thanh toán lúc `2026-11-10T03:00:00Z` (10:00 ngày 10/11 giờ VN), gồm 2 item:

| Item | Thiết bị | NCC | SL | Tiền thuê | Cọc | Diễn biến |
| --- | --- | --- | --- | --- | --- | --- |
| I1 | Máy đầm cóc | A | 3 | 900.000 | 3.000.000 | A chấp nhận 11/11. Giao 12/11. Hoàn thành 15/11. Settlement 16/11: khấu trừ 0, phí 90.000, phải trả NCC 810.000 |
| I2 | Máy xúc mini | B | 1 | 6.000.000 | 20.000.000 | B từ chối 11/11. Hoàn tiền xong 11/11: 6.000.000 tiền thuê + 20.000.000 cọc |

Kết quả mong đợi (theo ngày VN):

| Ngày | KPI |
| --- | --- |
| 10/11 | `TotalOrders` = 1, `PaidOrders` = 1, `RentalItemCount` = 2, `GrossRentalValue` = 6.900.000, `DepositCollected` = 23.000.000. Sau khi hoàn tiền ngày 11/11, `GrossRentalValueRefunded` của ngày 10/11 = 6.000.000 |
| 11/11 | `AcceptedCount`(A) = 1, `RejectedCount`(B) = 1, `RefundAmount` = 26.000.000. `RejectedOrders` = 0, vì đơn vẫn còn I1 |
| 12/11 | `RentedQuantity` = 3 |
| 15/11 | `CompletedRentalValue` = 900.000, `CompletedRentalCount`(A) = 1. `CompletedOrders` = 1 (khi Rental phát `RentalOrderCompleted`) |
| 16/11 | `PlatformRevenue` = 90.000, `SupplierPayable` = 810.000, `DeductionAmount` = 0 |

Bảng `TopEquipmentTypes` cho khoảng 10/11–16/11: máy đầm cóc và máy xúc mini cùng có 1 lượt. Hòa thì xét `RentedQuantity` (3 so với 0), nên **máy đầm cóc xếp trên**.

Ví dụ múi giờ: một đơn thanh toán lúc `2026-11-10T17:30:00Z` (00:30 ngày 11/11 giờ VN) được tính vào ngày **11/11**.

---

## 9. Yêu cầu bổ sung cho producer

KPI phụ thuộc vào yêu cầu nào thì hiển thị "Chưa có nguồn" cho đến khi yêu cầu đó được triển khai. Mọi thay đổi chỉ **thêm field hoặc thêm event**, không đổi hay xóa field cũ, nên consumer hiện tại không bị ảnh hưởng.

### 9.1. PaymentService

| # | Yêu cầu | KPI phụ thuộc | Ghi chú |
| --- | --- | --- | --- |
| P1 | `RentalSettled` thêm `supplierId`, `rentalAmount`, `depositAmount`, `platformFeeAmount`, `supplierPayable` | `PlatformRevenue`, `SupplierPayable` | Các giá trị đã có trong `RentalItemSettledDomainEvent` nội bộ của Payment |
| P2 | `messageId` tất định theo `settlementRef` / `refundRef` / `payoutRef` | — | Khuyến nghị. Analytics vẫn dedupe theo khóa tự nhiên |
| P3 | Event mới `SupplierPayoutCompleted { payoutRef, supplierId, rentalItemId, amount, currency, createdAt, completedAt }` | `SupplierPayoutCompleted` | **Không** mang thông tin tài khoản nhận tiền |
| P4 | `RefundCompleted` thêm `kind` (ví dụ `CANCELLATION`, `SUPPLIER_REJECTION`, `LATE_PAYMENT`, `DEPOSIT_SETTLEMENT`) | Phân loại `RefundAmount` | `RefundAmount` tổng vẫn tính được khi chưa có `kind` |
| P5 | Event mới `RefundRequestedFinancial { refundRef, rentalOrderId, rentalItemId?, refundKind, amount, currency, requestedAt }` trên `payment.analytics.v1` | `PendingRefundAmount` | Analytics đã ingest và khử trùng theo `refundRef`; Payment producer cần phát event, dùng cùng `refundRef` với `RefundCompletedFinancial` |

### 9.2. RentalService

| # | Yêu cầu | KPI phụ thuộc | Ghi chú |
| --- | --- | --- | --- |
| R1 | Mỗi khi `RentalOrderStatusPolicy` chuyển đơn sang trạng thái kết thúc, phát **đúng một** event cấp đơn tương ứng, trong cùng transaction: (a) event mới `RentalOrderRejected { rentalOrderId, renterId, rejectedAt, reason = SUPPLIER_REJECTED, orderVersion }` khi sang `Rejected`; (b) `RentalOrderExpired` (reason `SUPPLIER_CONFIRMATION_EXPIRED`) khi sang `Expired` do NCC không xác nhận hoặc do kết hợp từ chối + hết hạn. Hiện các luồng `ExpiryWorkflowService` (hết hạn xác nhận) và `RentalOperationExecutor.Supplier` (từ chối) chỉ phát event cấp item | `RejectedOrders`, `ExpiredOrders` | Tương tự `RentalOrderCancelled` / `RentalOrderExpired` đã có. Không phát trùng khi đơn đã ở trạng thái kết thúc |
| R2 | `RentalItemCompleted` thêm `completedAt` | `CompletedRentalValue`, `CompletedRentalCount` | Trước khi có R2, Analytics dùng `occurredAt` của envelope (mục 4) |

---

## 10. Đối soát (không dùng làm KPI)

| Kiểm tra | Kỳ vọng |
| --- | --- |
| `Σ item.RentalAmount + Σ item.DepositAmount` của đơn so với `InvoicePaid.paidAmount` | Bằng nhau. Lệch thì cảnh báo, không sửa KPI |
| `Σ SupplierPayoutCompleted` so với `Σ SupplierPayable` lũy kế theo NCC | Payout ≤ Payable |
| `RefundAmount` theo `refundRef` so với `RefundRequested.amount` | Hoàn ≤ yêu cầu |

---

## 11. Changelog

| Version | Ngày | Thay đổi |
| --- | --- | --- |
| v1 (PROPOSED) | 2026-10-26 | Bản đầu: định nghĩa KPI 4 nhóm, quy tắc thời gian, bổ sung `GrossRentalValueRefunded`, `CompletedRentalValue`, yêu cầu P1–P5, R1–R2 |
