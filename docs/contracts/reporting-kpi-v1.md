# Contract: Chuẩn hóa KPI Báo cáo và Hợp đồng Dữ liệu Analytics — v1

| Thuộc tính | Giá trị |
|---|---|
| **Mã tài liệu** | `CONTRACT-ANALYTICS-REPORTING-KPI-V1` |
| **Trạng thái** | **OFFICIAL / STANDARDIZED** — Hợp đồng chính thức thống nhất giữa AnalyticsService, RentalService và PaymentService. |
| **Chủ quản tài liệu** | AnalyticsService Team |
| **Dịch vụ cung cấp nguồn** | RentalService (`rental.events.v1`), PaymentService (`payment.analytics.v1`) |
| **Phạm vi** | Định nghĩa 26 KPI chuẩn, nguyên tắc kế toán/phân tích, ranh giới dữ liệu và hợp đồng tích hợp bất đồng bộ |
| **Phiên bản commit tham chiếu** | • PaymentService: `271e70a5798b43f5536812663ec4d404b5f8b700` (`271e70a`)<br>• RentalService: `3c2d9eca69939c811614326decba4d398ef72b81` (`3c2d9ec`)<br>• AnalyticsService: `8221ba630f38843d24243cb35e124de355a23fa4` (`8221ba6`) |

---

## 1. Mục tiêu và Phạm vi

### 1.1. Mục tiêu
Tài liệu này xác lập tiêu chuẩn duy nhất (Single Source of Truth) về:
1. **Định nghĩa 26 KPI báo cáo** cốt lõi của nền tảng DPRENTAL cho cả cấp độ toàn sàn (Admin) và cấp độ từng nhà cung cấp (Supplier).
2. **Ranh giới trách nhiệm tài chính**: Phân định rạch ròi 5 dòng tiền cơ bản:
   - **Giá trị thuê gộp (Gross Rental Value)**: Giá trị dịch vụ cho thuê phát sinh tại thời điểm thanh toán.
   - **Tiền cọc đã thu (Deposit Collected)**: Tiền thế chân thực thu từ khách hàng, là nghĩa vụ nợ tạm giữ, tuyệt đối không phải doanh thu sàn.
   - **Doanh thu nền tảng (Platform Revenue)**: Khoản phí dịch vụ sàn thực tế được ghi nhận khi quyết toán hoàn tất.
   - **Nghĩa vụ trả nhà cung cấp (Supplier Payable)**: Khoản nợ sàn phải trả cho đối tác sau khi trừ phí và cộng khấu trừ hư hại đã chốt.
   - **Tiền payout đã chuyển (Supplier Payout Completed)**: Dòng tiền thực tế đã chi trả qua tài khoản ngân hàng của supplier.
3. **Quy tắc tính toán và tổng hợp**: AnalyticsService chỉ đóng vai trò thu thập, chuẩn hóa, phân loại và tổng hợp số liệu do các domain service sở hữu cung cấp; **AnalyticsService tuyệt đối không tự ý tính lại settlement, không tự suy diễn tỉ lệ phí sàn hay tự bù trừ công nợ**.

### 1.2. Phạm vi áp dụng & Ngoài phạm vi
- **Trong phạm vi**:
  - Đặc tả chi tiết 26 KPI (ngữ nghĩa, grain, nguồn chuẩn, điều kiện, công thức, khóa chống trùng, mốc thời gian, ví dụ).
  - Quy chuẩn thời gian (UTC, múi giờ Việt Nam Asia/Ho_Chi_Minh, quy tắc windowing ngày và snapshot tại `asOf`).
  - Hợp đồng dữ liệu tối thiểu cần tiếp nhận từ RentalService và PaymentService.
  - Phân tích chênh lệch với kế hoạch dashboard hiện có của RentalService (`DASHBOARD_IMPLEMENTATION_PLAN.md`).
  - Bộ kịch bản nghiệm thu kiểm chứng logic và các trường hợp biên.
- **Ngoài phạm vi**:
  - Không triển khai mã nguồn API, controller, background worker hay event consumer trong task này.
  - Không sửa đổi schema database hay logic nghiệp vụ nội bộ của RentalService hoặc PaymentService.
  - Không tuyên bố hợp đồng tích hợp Kafka/Event đã được freeze hoàn toàn khi các bên chưa thống nhất schema chuyển giao.

---

## 2. Ranh giới Kiến trúc và Nguyên tắc Kế toán / Phân tích

### 2.1. Phân định rõ 5 dòng tiền
Hệ thống kế toán và phân tích phân biệt tuyệt đối 5 khái niệm tài chính sau:

```text
Khách thanh toán Invoice
  ├── Tiền thuê (GrossRentalValue) ────────► Không trừ khi hủy/hoàn về sau (Gross metric)
  └── Tiền cọc (DepositCollected)  ────────► Nghĩa vụ nợ tạm giữ (Holding Liability), KHÔNG phải doanh thu

Quyết toán hoàn tất (Settlement Completed)
  ├── Phí sàn (PlatformRevenue)   ────────► Doanh thu thực nhận của nền tảng (Platform Fee)
  ├── Khấu trừ (DeductionAmount)   ────────► Bồi thường thiệt hại chốt từ cọc sang supplier
  ├── Nghĩa vụ supplier (SupplierPayable) ─► Tổng tiền phải trả supplier = (Thuê - Phí) + Khấu trừ
  └── Hoàn cọc thừa (RefundAmount) ────────► Trả lại khách hàng = Cọc - Khấu trừ

Giải ngân (Payout Completed)
  └── Đã chi trả (SupplierPayoutCompleted) ─► Dòng tiền thực tế đã chuyển khoản thành công cho Supplier
```

1. **Gross Rental Value (`GrossRentalValue`)**: Ghi nhận ngay khi Payment xác nhận hóa đơn `PAID`. Là thước đo quy mô dịch vụ (GMV cho thuê). Nếu đơn hàng bị hủy hoặc hoàn tiền sau đó, giá trị này **không bị trừ ngược**.
2. **Tiền cọc đã thu (`DepositCollected`)**: Toàn bộ số tiền cọc thực thu tại thời điểm thanh toán. Đây là khoản nợ phải trả/hoàn lại, **không bao giờ được tính vào doanh thu nền tảng**.
3. **Doanh thu nền tảng (`PlatformRevenue`)**: Chỉ ghi nhận khi bản ghi quyết toán `RentalItemSettlementRecord` đạt trạng thái `Completed`. Lấy nguyên giá trị `PlatformFeeAmount` do PaymentService tính toán và lưu trong snapshot.
4. **Nghĩa vụ trả supplier (`SupplierPayable`)**: Phát sinh khi PaymentService tạo bản ghi quyết toán. Đây là công nợ phải trả, độc lập với việc lệnh payout đã được chuyển tiền hay chưa.
5. **Tiền payout đã chuyển (`SupplierPayoutCompleted`)**: Chỉ ghi nhận khi lệnh chuyển tiền `SupplierPayout` đạt trạng thái `COMPLETED`.

> **Cảnh báo bất biến**: Tuyệt đối **không** dùng tổng tiền hóa đơn (`TotalAmount`), tiền cọc (`DepositAmount`), hay tiền khấu trừ bồi thường (`FinalDeductionAmount`) làm doanh thu nền tảng (`PlatformRevenue`).

### 2.2. Nguyên tắc độc lập & Single Source of Truth
- **Dữ liệu tài chính thuộc sở hữu duy nhất của PaymentService**: Toàn bộ số tiền, trạng thái thanh toán, quyết toán, hoàn tiền và chi trả phải lấy từ PaymentService. Analytics không tự nhân chia phần trăm chiết khấu.
- **Trạng thái đơn hàng và vòng đời thiết bị thuộc sở hữu duy nhất của RentalService**: RentalService quản lý diễn biến trạng thái của đơn hàng và từng thiết bị.
- **Không tự suy diễn trạng thái đơn hàng từ item (Mixed status rule)**: Một đơn hàng có thể chứa nhiều item với các trạng thái khác nhau (ví dụ: 1 item đã hoàn tất `Completed`, 1 item bị hủy `Cancelled`). AnalyticsService **không được tự tổng hợp trạng thái của Order** từ trạng thái các Item. Chỉ sử dụng trạng thái và transition chính thức do RentalService phát hành tại cấp độ Order (`RentalOrderId`, với `RentalItemId IS NULL`).
- **Item có số lượng lớn hơn 1 (Quantity > 1)**:
  - Đóng góp vào KPI đếm item (`RentalItemCount`): đúng **1** đơn vị (distinct `RentalItem.Id`).
  - Đóng góp vào KPI đếm số lượng máy (`RentedQuantity`): bằng đúng giá trị `RentalItem.Quantity`.
- **Đơn vị tiền tệ và làm tròn**:
  - Toàn bộ giá trị tài chính sử dụng kiểu số thực cố định `decimal(18, 0)`, đơn vị VND nguyên (đồng).
  - Tuyệt đối không làm tròn số lẻ thập phân hoặc điều chỉnh sai khác số học so với dữ liệu nguồn.

---

## 3. Quy chuẩn Thời gian và Cửa sổ Báo cáo (Time & Windowing Rules)

### 3.1. Lưu trữ và biểu diễn
- Toàn bộ mốc thời gian trong cơ sở dữ liệu và message hợp đồng bắt buộc lưu trữ dưới định dạng **UTC**, biểu diễn chuẩn **ISO 8601** kèm hậu tố `Z` (ví dụ: `2026-10-23T14:30:00Z`).

### 3.2. Nhóm ngày nghiệp vụ theo múi giờ Việt Nam (`Asia/Ho_Chi_Minh` — UTC+7)
- Báo cáo dashboard phân nhóm theo ngày làm việc tại Việt Nam (UTC+7, không có Daylight Saving Time).
- Mọi khoảng ngày do người dùng chọn `[FromDate, ToDate]` (định dạng `yyyy-MM-dd`) là khoảng đóng bao gồm cả 2 ngày đầu và cuối.
- Khi chuyển đổi sang khoảng truy vấn UTC, công thức bắt buộc là nửa đóng - nửa mở:
  $$\text{FromUtc} = \text{StartOfDayUtc}(\text{FromDate})$$
  $$\text{ToUtcExclusive} = \text{StartOfDayUtc}(\text{ToDate} + 1\text{ day})$$
  Trong đó $\text{StartOfDayUtc}(D) = D\text{ 00:00:00+07} = (D - 7\text{ hours UTC})$.

### 3.3. Thời điểm phát sinh vs. Snapshot tại thời điểm (`asOf`)
1. **Chỉ số phát sinh (Event-based / Accrual Metrics)**:
   - Sử dụng đúng mốc thời gian nghiệp vụ được định nghĩa cho từng KPI (ví dụ: `Invoice.PaidAt`, `Settlement.CompletedAt`, `RentalStateTransition.OccurredAt`).
   - Tuyệt đối **không** thay thế bằng `UpdatedAt`, thời điểm ghi nhận event vào database Analytics (`IngestedAt`), hay thời điểm Rental nhận được webhook (`PaymentConfirmedAt`).
2. **Chỉ số trạng thái tức thời (Snapshot Metrics)**:
   - `ActiveOrders` và `PendingRefundAmount` là snapshot tại mốc thời gian `asOf`.
   - **Biểu đồ lịch sử theo ngày**: Điểm dữ liệu của ngày quá khứ là snapshot được chốt tại thời điểm cuối ngày đó (`23:59:59.999999+07` $\Leftrightarrow$ `16:59:59.999999Z`).
   - **Ngày hiện tại**: Dùng snapshot tại thời điểm truy vấn dữ liệu (`asOf = UtcNow`).
   - **Quy tắc bất biến**: **Tuyệt đối không cộng dồn các snapshot qua các ngày** (ví dụ: không cộng snapshot của ngày 1, ngày 2, ngày 3 để ra "tổng snapshot của tháng").
3. **Quy tắc không phân rã `TotalOrders`**:
   - Diễn biến trạng thái (`CompletedOrders`, `CancelledOrders`, `RejectedOrders`, `ExpiredOrders`) được ghi nhận theo thời điểm xảy ra sự kiện chuyển trạng thái (`RentalStateTransition.OccurredAt`).
   - Một đơn hàng được tạo ở tháng trước (`TotalOrders` tháng trước) có thể chuyển trạng thái hoàn tất hoặc hủy trong tháng này.
   - Do đó, **không coi tổng các KPI trạng thái phát sinh theo thời gian là phép phân rã (decomposition) của `TotalOrders` trong cùng một kỳ báo cáo**.

---

## 4. Bảng Khóa 26 KPI Analytics v1

Mỗi KPI dưới đây được khóa chặt chẽ theo 8 chiều thông tin:

| STT | Tên KPI | Ý nghĩa nghiệp vụ | Đơn vị / Grain | Nguồn chuẩn | Điều kiện ghi nhận | Phép tổng hợp | Khóa chống trùng | Mốc báo cáo (Timestamp) | Ví dụ minh họa |
|:---:|---|---|---|---|---|---|---|---|---|
| **1** | **TotalOrders** | Tổng số đơn thuê được người dùng tạo thành công trên sàn | Đơn hàng (Order), số nguyên `int` | RentalService: `RentalOrder` | Bản ghi `RentalOrder` được tạo mới thành công | `COUNT(DISTINCT RentalOrder.Id)` | `RentalOrder.Id` | `RentalOrder.CreatedAt` | Ngày 2026-10-20 có 5 đơn hàng được khởi tạo thành công $\rightarrow$ `TotalOrders = 5`. |
| **2** | **PaidOrders** | Tổng số đơn hàng đã được Payment xác nhận thanh toán thành công qua hóa đơn; không mất lượt đếm khi đơn bị hủy hoặc hoàn tiền sau đó | Đơn hàng (Order), số nguyên `int` | PaymentService: `Invoice` | `Invoice.Status == "PAID"` | `COUNT(DISTINCT Invoice.RentalOrderId)` | `Invoice.RentalOrderId` | `Invoice.PaidAt` | Đơn ORD-1 có hóa đơn thanh toán lúc 2026-10-20 10:00Z $\rightarrow$ `PaidOrders = 1`. Ngày 2026-10-21 đơn bị hủy $\rightarrow$ `PaidOrders` ngày 20-10 vẫn là 1. |
| **3** | **ActiveOrders** | Snapshot số lượng đơn hàng đang trong quá trình thực hiện tại thời điểm chốt (`asOf`), chỉ gồm các đơn đang xử lý sau thanh toán | Đơn hàng (Order) tại mốc `asOf`, số nguyên `int` | RentalService: `RentalOrder` & `RentalStateTransition` | Trạng thái đơn tại `asOf` thuộc tập `{Paid, AwaitingHandover, InRental, PendingSettlement}` | `COUNT(DISTINCT RentalOrder.Id)` tại mốc `asOf` | `(RentalOrder.Id, asOf)` | `asOf` | Tại 23:59:59+07 ngày 2026-10-20 có 2 đơn `InRental`, 1 đơn `AwaitingHandover` $\rightarrow$ `ActiveOrders = 3`. Không cộng dồn với ngày hôm sau. |
| **4** | **CompletedOrders** | Đếm số lượng đơn hàng lần đầu tiên chuyển sang trạng thái hoàn tất (`Completed`), chỉ dùng transition cấp order | Đơn hàng (Order), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Order) | `RentalItemId IS NULL AND ToState == "Completed"` (lấy transition đầu tiên) | `COUNT(DISTINCT RentalOrderId)` | `RentalOrderId` | `RentalStateTransition.OccurredAt` | Đơn ORD-1 có transition cấp order sang `Completed` lúc 2026-10-22 15:00Z $\rightarrow$ ghi nhận `CompletedOrders = 1` ngày 22-10. |
| **5** | **CancelledOrders** | Đếm số lượng đơn hàng lần đầu tiên chuyển sang trạng thái đã hủy (`Cancelled`), chỉ dùng transition cấp order | Đơn hàng (Order), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Order) | `RentalItemId IS NULL AND ToState == "Cancelled"` (lấy transition đầu tiên) | `COUNT(DISTINCT RentalOrderId)` | `RentalOrderId` | `RentalStateTransition.OccurredAt` | Khách hủy đơn sau khi thanh toán lúc 2026-10-21 08:00Z $\rightarrow$ ghi nhận `CancelledOrders = 1` ngày 21-10. |
| **6** | **RejectedOrders** | Đếm số lượng đơn hàng lần đầu tiên chuyển sang trạng thái bị từ chối (`Rejected`), chỉ dùng transition cấp order | Đơn hàng (Order), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Order) | `RentalItemId IS NULL AND ToState == "Rejected"` (lấy transition đầu tiên) | `COUNT(DISTINCT RentalOrderId)` | `RentalOrderId` | `RentalStateTransition.OccurredAt` | Đơn hàng có toàn bộ item bị từ chối bởi supplier, chuyển trạng thái order lúc 2026-10-20 14:00Z $\rightarrow$ `RejectedOrders = 1`. |
| **7** | **ExpiredOrders** | Đếm số lượng đơn hàng lần đầu tiên chuyển sang trạng thái hết hạn (`Expired`), phân biệt rõ hết hạn thanh toán và hết hạn do supplier xác nhận | Đơn hàng (Order), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Order) | `RentalItemId IS NULL AND ToState == "Expired"`. Phân rã theo lý do: Payment Timeout (`Action == "ExpirePayment"`) vs Supplier Timeout | `COUNT(DISTINCT RentalOrderId)` | `RentalOrderId` | `RentalStateTransition.OccurredAt` | Đơn ORD-2 hết hạn thanh toán 30 phút, hệ thống expire lúc 2026-10-20 11:30Z $\rightarrow$ `ExpiredOrders (PaymentTimeout) = 1`. |
| **8** | **GrossRentalValue** | Tổng giá trị tiền thuê của các hóa đơn đã được xác nhận PAID trong kỳ; không gồm cọc, không trừ hoàn tiền về sau | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `InvoiceItem` | `Invoice.Status == "PAID"`. Lấy `InvoiceItem.RentalAmount` | `SUM(InvoiceItem.RentalAmount)` | `InvoiceItem.Id` | `Invoice.PaidAt` | Hóa đơn thanh toán gồm Item 1 thuê 3.000.000 VND và cọc 5.000.000 VND $\rightarrow$ `GrossRentalValue = 3.000.000 VND`. |
| **9** | **DepositCollected** | Tổng số tiền cọc thực tế đã thu từ các hóa đơn PAID trong kỳ; là tiền cọc thu hộ, không phải doanh thu sàn | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `InvoiceItem` | `Invoice.Status == "PAID"`. Lấy `InvoiceItem.DepositAmount` | `SUM(InvoiceItem.DepositAmount)` | `InvoiceItem.Id` | `Invoice.PaidAt` | Cùng hóa đơn trên $\rightarrow$ `DepositCollected = 5.000.000 VND`. Không cộng vào doanh thu sàn. |
| **10** | **PlatformRevenue** | Tổng doanh thu sàn thực tế từ các bản ghi quyết toán đã hoàn tất (`Completed`); lấy nguyên snapshot của Payment | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `RentalItemSettlementRecord` | `RentalItemSettlementRecord.Status == SettlementState.Completed` | `SUM(RentalItemSettlementRecord.PlatformFeeAmount)` | `RentalItemSettlementRecord.Id` | `RentalItemSettlementRecord.CompletedAt` | Quyết toán hoàn tất STL-101 có phí sàn snapshot 300.000 VND lúc 2026-10-25 09:00Z $\rightarrow$ `PlatformRevenue = 300.000 VND`. |
| **11** | **DeductionAmount** | Tổng số tiền khấu trừ vi phạm/hư hại đã được Payment ghi nhận chính thức vào quyết toán; không lấy số đề xuất chưa chốt | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `RentalItemSettlementRecord` | Bản ghi quyết toán được Payment tạo lập thành công | `SUM(RentalItemSettlementRecord.FinalDeductionAmount)` | `RentalItemSettlementRecord.Id` | `RentalItemSettlementRecord.CreatedAt` | Quyết toán ghi nhận bồi thường hư hại máy 500.000 VND lúc 2026-10-25 08:30Z $\rightarrow$ `DeductionAmount = 500.000 VND`. |
| **12** | **RefundAmount** | Tổng số tiền hoàn trả cho khách hàng đã hoàn tất (`Completed`), bao gồm mọi loại hoàn tiền mà Payment quản lý | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `ManualRefundRequirement` | `ManualRefundRequirement.Status == ManualRefundStatus.Completed` | `SUM(ManualRefundRequirement.Amount)` trên requirement | `ManualRefundRequirement.Id` | `ManualRefundRequirement.CompletedAt` | Yêu cầu hoàn cọc 4.500.000 VND được admin xác nhận chuyển tiền xong lúc 2026-10-26 14:00Z $\rightarrow$ `RefundAmount = 4.500.000 VND`. |
| **13** | **PendingRefundAmount** | Snapshot tổng nghĩa vụ hoàn tiền Payment đã ghi nhận nhưng chưa chuyển khoản hoàn tất tại thời điểm chốt (`asOf`) | Tiền tệ VND tại mốc `asOf`, `decimal(18, 0)` nguyên | PaymentService: `ManualRefundRequirement` | Tại mốc `asOf`: `CreatedAt <= asOf` VÀ (`Status == PendingManualRefund` HOẶC `CompletedAt > asOf`) | `SUM(ManualRefundRequirement.Amount)` tại `asOf` | `(ManualRefundRequirement.Id, asOf)` | `asOf` | Cuối ngày 2026-10-25 có 2 lệnh hoàn cọc đang chờ admin xử lý tổng cộng 7.000.000 VND $\rightarrow$ `PendingRefundAmount = 7.000.000 VND`. |
| **14** | **SupplierPayable** | Tổng nghĩa vụ phải trả cho supplier phát sinh từ quyết toán của Payment, kể cả nghĩa vụ đã payout hay chưa | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `RentalItemSettlementRecord` | Bản ghi quyết toán được Payment tạo lập thành công | `SUM(RentalItemSettlementRecord.SupplierPayable)` | `RentalItemSettlementRecord.Id` | `RentalItemSettlementRecord.CreatedAt` | Quyết toán STL-101 ghi nhận nghĩa vụ trả supplier 3.200.000 VND lúc 2026-10-25 08:30Z $\rightarrow$ `SupplierPayable = 3.200.000 VND`. |
| **15** | **SupplierPayoutCompleted** | Tổng số tiền thực tế đã giải ngân chuyển khoản thành công cho supplier qua tài khoản ngân hàng đã xác minh | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `SupplierPayout` | `SupplierPayout.Status == "COMPLETED"` | `SUM(SupplierPayout.Amount)` | `SupplierPayout.Id` | `SupplierPayout.CompletedAt` | Admin bấm xác nhận chuyển khoản ngân hàng thành công PO-101 lúc 2026-10-27 10:00Z $\rightarrow$ `SupplierPayoutCompleted = 3.200.000 VND`. |
| **16** | **RentalItemCount** | Đếm số lượng distinct thiết bị trong các đơn hàng đã được Payment xác nhận thanh toán; quantity > 1 vẫn đếm là 1 | Thiết bị (Item), số nguyên `int` | RentalService: `RentalItem` liên kết với Payment `Invoice` | `RentalItem` thuộc đơn có `Invoice.Status == "PAID"` | `COUNT(DISTINCT RentalItem.Id)` | `RentalItem.Id` | `Invoice.PaidAt` | Đơn có 1 dòng "Bộ giáo tiệp (Quantity = 10)" và 1 dòng "Máy đầm (Quantity = 1)" $\rightarrow$ `RentalItemCount = 2`. |
| **17** | **RentedQuantity** | Tổng số lượng thiết bị vật lý được thuê trên cùng tập item đã thanh toán | Số lượng hiện vật (Unit), số nguyên `int` | RentalService: `RentalItem` liên kết với Payment `Invoice` | `RentalItem` thuộc đơn có `Invoice.Status == "PAID"` | `SUM(RentalItem.Quantity)` | `RentalItem.Id` | `Invoice.PaidAt` | Cùng đơn trên (10 bộ giáo tiệp + 1 máy đầm) $\rightarrow$ `RentedQuantity = 10 + 1 = 11`. |
| **18** | **TopEquipmentTypes** | Xếp hạng loại thiết bị theo số lượng thuê thực tế trên tập item đã thanh toán; mặc định top 10 | Danh sách nhóm `(EquipmentTypeId, Name, Quantity, RentalValue)` | RentalService: `RentalItem` snapshot | Item thuộc đơn có `Invoice.Status == "PAID"`. Gom nhóm theo `EquipmentTypeId` | `SUM(Quantity) DESC`, `EquipmentTypeId ASC` | `(EquipmentTypeId, Kỳ báo cáo)` | `Invoice.PaidAt` | Loại "Máy đầm cóc" thuê 25 cái, "Máy lu mini" thuê 15 cái $\rightarrow$ Xếp thứ 1 và 2. ID rỗng gom vào "Không xác định". |
| **19** | **TopListings** | Xếp hạng bài đăng cho thuê theo số lượng thiết bị được thuê trên tập item đã thanh toán; mặc định top 10 | Danh sách nhóm `(EquipmentListingId, Title, Quantity, RentalValue)` | RentalService: `RentalItem` snapshot | Item thuộc đơn có `Invoice.Status == "PAID"`. Gom nhóm theo `EquipmentListingId` | `SUM(Quantity) DESC`, `EquipmentListingId ASC` | `(EquipmentListingId, Kỳ báo cáo)` | `Invoice.PaidAt` | Bài đăng "Máy lu Sakai 25 tấn - Kho Q9" có 8 lượt thuê tổng 12 máy $\rightarrow$ Top listing. |
| **20** | **TopBrands** | Xếp hạng thương hiệu thiết bị theo số lượng thiết bị được thuê trên tập item đã thanh toán; mặc định top 10 | Danh sách nhóm `(BrandId, BrandName, Quantity, RentalValue)` | RentalService: `RentalItem` snapshot | Item thuộc đơn có `Invoice.Status == "PAID"`. Gom nhóm theo `BrandId` | `SUM(Quantity) DESC`, `BrandId ASC` | `(BrandId, Kỳ báo cáo)` | `Invoice.PaidAt` | Thương hiệu "Mikasa" đạt 30 máy, "Sakai" đạt 20 máy $\rightarrow$ Xếp theo thứ tự số lượng. Thiếu ID vào "Không xác định". |
| **21** | **Supplier AcceptedCount** | Đếm số lượng distinct item mà supplier bấm chấp nhận hợp lệ từ trạng thái chờ xác nhận sang chờ bàn giao | Thiết bị (Item), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Item) | `RentalItemId IS NOT NULL AND Action == "SupplierAccept" AND FromState == "PendingSupplierConfirmation" AND ToState == "AwaitingHandover" AND ActorRole == Supplier` | `COUNT(DISTINCT RentalItemId)` (transition đầu tiên) | `RentalItemId` | `RentalStateTransition.OccurredAt` | Supplier A bấm chấp nhận cho thuê máy lu ITM-01 lúc 2026-10-20 09:30Z $\rightarrow$ `AcceptedCount` của Supplier A tăng 1. |
| **22** | **Supplier RejectedCount** | Đếm số lượng distinct item mà supplier bấm từ chối cho thuê hợp lệ từ trạng thái chờ xác nhận | Thiết bị (Item), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Item) | `RentalItemId IS NOT NULL AND Action == "SupplierReject" AND ToState == "Rejected" AND ActorRole == Supplier` | `COUNT(DISTINCT RentalItemId)` (transition đầu tiên) | `RentalItemId` | `RentalStateTransition.OccurredAt` | Supplier A hết máy nên từ chối ITM-02 lúc 2026-10-20 10:00Z $\rightarrow$ `RejectedCount` của Supplier A tăng 1. |
| **23** | **Supplier ConfirmationTimeoutCount** | Đếm số lượng distinct item bị hệ thống tự động hủy do supplier quá hạn thời gian xác nhận đơn | Thiết bị (Item), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Item) | `RentalItemId IS NOT NULL AND Action == "SupplierTimeout" AND FromState == "PendingSupplierConfirmation" AND ToState == "Expired"` | `COUNT(DISTINCT RentalItemId)` (transition đầu tiên) | `RentalItemId` | `RentalStateTransition.OccurredAt` | Quá hạn 6 giờ không phản hồi, background worker expire ITM-03 lúc 2026-10-20 15:30Z $\rightarrow$ `ConfirmationTimeoutCount` tăng 1. |
| **24** | **Supplier CompletedRentalCount** | Đếm số lượng distinct item của supplier lần đầu tiên hoàn tất chu trình thuê và quyết toán (`Completed`) | Thiết bị (Item), số nguyên `int` | RentalService: `RentalStateTransition` (cấp Item) | `RentalItemId IS NOT NULL AND ToState == "Completed"` | `COUNT(DISTINCT RentalItemId)` (transition đầu tiên) | `RentalItemId` | `RentalStateTransition.OccurredAt` | Thiết bị ITM-01 hoàn tất quyết toán, chuyển `Completed` lúc 2026-10-25 10:00Z $\rightarrow$ `CompletedRentalCount` tăng 1. |
| **25** | **Supplier RentalValue** | Doanh thu tiền thuê gộp của riêng supplier từ các hóa đơn PAID trong kỳ (cùng định nghĩa GrossRentalValue) | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `InvoiceItem` | `Invoice.Status == "PAID" AND InvoiceItem.SupplierId == TargetSupplierId` | `SUM(InvoiceItem.RentalAmount)` | `InvoiceItem.Id` | `Invoice.PaidAt` | Hóa đơn có Item của Supplier A thuê 2.000.000 VND và Item của Supplier B thuê 1.000.000 VND $\rightarrow$ Supplier A nhận 2.000.000 VND. |
| **26** | **Supplier SupplierPayable** | Nghĩa vụ tài chính sàn phải trả riêng cho supplier phát sinh từ quyết toán Payment (cùng định nghĩa SupplierPayable) | Tiền tệ VND, `decimal(18, 0)` nguyên | PaymentService: `RentalItemSettlementRecord` | Bản ghi quyết toán tạo thành công bởi Payment có `SupplierId == TargetSupplierId` | `SUM(RentalItemSettlementRecord.SupplierPayable)` | `RentalItemSettlementRecord.Id` | `RentalItemSettlementRecord.CreatedAt` | Quyết toán ghi nhận nghĩa vụ cho Supplier A là 1.800.000 VND $\rightarrow$ `SupplierPayable` của Supplier A ghi nhận 1.800.000 VND. |

---

## 5. Quy tắc Nghiệp vụ Đi kèm (Business Rules & Guidelines)

### 5.1. Quy tắc đếm Item vs. Số lượng thực thuê
- Mỗi dòng `RentalItem` trong đơn hàng có một mã định danh duy nhất `RentalItem.Id` và trường số lượng `RentalItem.Quantity`.
- Trong mọi thống kê đếm item (`RentalItemCount`, `AcceptedCount`, `RejectedCount`, `ConfirmationTimeoutCount`, `CompletedRentalCount`): một dòng item có `Quantity = 10` vẫn chỉ tính là **1 item**.
- Chỉ số `RentedQuantity` và các bảng xếp hạng Top mới thực hiện cộng dồn theo trường `Quantity`.

### 5.2. Quy tắc bảng xếp hạng Top (Top EquipmentTypes, Listings, Brands)
- Mặc định giới hạn: **Top 10** nhóm có số lượng thuê lớn nhất.
- Thứ tự sắp xếp: `RentedQuantity` giảm dần (`DESC`). Nếu bằng nhau, sắp xếp theo ID thực thể tăng dần (`ASC`) để đảm bảo kết quả tất định.
- Trường hợp dữ liệu snapshot thiếu hoặc null (ví dụ: `BrandId = null` hoặc rỗng): Toàn bộ số lượng được gom chung vào một nhóm duy nhất có tên hiển thị là `"Không xác định"`.
- Bắt buộc dùng đúng tên snapshot tại thời điểm đặt hàng do RentalService lưu giữ (`EquipmentTypeNameSnapshot`, `ListingTitleSnapshot`, `BrandNameSnapshot`) để bảo toàn tính lịch sử khi catalog bị sửa đổi.

### 5.3. Xử lý Trạng thái Hoàn tiền (Refund Breakdown)
- Bảng thực thể PaymentService:
  - `ManualRefundRequirement`: Thực thể đại diện cho yêu cầu hoàn tiền tổng thể (chứa `Amount`, `Kind`, `Status`, `CompletedAt`).
  - `ManualRefundItem`: Thực thể chi tiết cấu thành khoản hoàn tiền (chứa `RentalItemId`, `RentalAmount`, `DepositAmount`).
- **Quy tắc chống trùng hai tầng**:
  - Khi tính `RefundAmount` hoặc `PendingRefundAmount`, **chỉ cộng trên trường `Amount` của `ManualRefundRequirement`**.
  - Tuyệt đối **không** cộng đồng thời `ManualRefundRequirement.Amount` và các dòng con `ManualRefundItem`, tránh nhân đôi số tiền hoàn.
- Phân loại hoàn tiền theo `Kind`:
  - `ManualRefundKind.SettlementDeposit (1)`: Hoàn phần cọc thừa sau khi quyết toán xong hợp đồng thuê.
  - `ManualRefundKind.RentalRefund (2)`: Hoàn tiền thuê/cọc do hủy đơn, supplier từ chối hoặc bồi hoàn sự cố.

---

## 6. Hợp đồng Dữ liệu Yêu cầu từ các Owner Service

AnalyticsService thu thập dữ liệu thông qua cơ chế bất đồng bộ (Kafka Events hoặc Read Projection API do các service cung cấp). **AnalyticsService tuyệt đối không truy cập trực tiếp vào database của RentalService hay PaymentService**.

### 6.1. Dữ liệu tối thiểu yêu cầu từ PaymentService

#### A. Hóa đơn thanh toán (`Invoice` & `InvoiceItem`)
- **Dữ liệu cần nhận**:
  - `InvoiceId` (GUID), `InvoiceRef` (string), `RentalOrderId` (GUID).
  - `Status`: Bắt buộc phân biệt rõ `PENDING`, `PAID`, `EXPIRED`, `CANCELLED`.
  - `PaidAt`: Timestamp UTC chính xác khi giao dịch thanh toán thành công (không dùng `CreatedAt` hay thời điểm nhận webhook).
  - `Currency`: Bắt buộc `"VND"`.
  - Danh sách `Items`: Mảng các đối tượng chứa:
    - `InvoiceItemId` (GUID), `RentalItemId` (GUID), `SupplierId` (GUID).
    - `RentalAmount` (decimal nguyên VND).
    - `DepositAmount` (decimal nguyên VND).
    - `TotalAmount` (decimal nguyên VND).
- **Khóa chống trùng**: `InvoiceId` cho hóa đơn, `InvoiceItemId` cho từng item.

#### B. Bản ghi quyết toán (`RentalItemSettlementRecord`)
- **Dữ liệu cần nhận**:
  - `SettlementId` (GUID), `SettlementRef` (string), `OperationKey` (string).
  - `InvoiceId` (GUID), `RentalItemId` (GUID), `SupplierId` (GUID).
  - `RentalAmount`, `DepositAmount`, `FinalDeductionAmount`, `DepositRefundDue`, `PlatformFeeAmount`, `SupplierPayable` (decimal nguyên).
  - `Status`: `PendingManualRefund (1)` hoặc `Completed (2)`.
  - `CreatedAt` (UTC): Thời điểm phát sinh quyết toán (dùng cho `DeductionAmount` và `SupplierPayable`).
  - `CompletedAt` (UTC): Thời điểm hoàn tất quyết toán (dùng cho `PlatformRevenue`).
- **Khóa chống trùng**: `SettlementId` hoặc `OperationKey`.

#### C. Yêu cầu hoàn tiền (`ManualRefundRequirement`)
- **Dữ liệu cần nhận**:
  - `RequirementId` (GUID), `RequirementRef` (string), `OperationKey` (string).
  - `SettlementId` (GUID, nullable), `InvoiceId` (GUID), `RentalItemId` (GUID, nullable), `RenterId` (GUID).
  - `Kind`: `SettlementDeposit (1)` hoặc `RentalRefund (2)`.
  - `Amount` (decimal nguyên VND).
  - `Status`: `PendingManualRefund (1)` hoặc `Completed (2)`.
  - `CreatedAt` (UTC), `CompletedAt` (UTC, nullable).
- **Khóa chống trùng**: `RequirementId` hoặc `OperationKey`.

#### D. Lệnh chi trả cho Supplier (`SupplierPayout`)
- **Dữ liệu cần nhận**:
  - `PayoutId` (GUID), `PayoutRef` (string), `SettlementId` (GUID), `RentalItemId` (GUID), `SupplierId` (GUID).
  - `Amount` (decimal nguyên VND), `Currency` (`"VND"`).
  - `Status`: `"PENDING_PAYOUT"` hoặc `"COMPLETED"`.
  - `CreatedAt` (UTC), `CompletedAt` (UTC, nullable).
- **Khóa chống trùng**: `PayoutId`.

---

### 6.2. Dữ liệu tối thiểu yêu cầu từ RentalService

#### A. Đơn thuê và Chi tiết Thiết bị (`RentalOrder` & `RentalItem`)
- **Dữ liệu cần nhận**:
  - `RentalOrderId` (GUID), `OrderCode` (string), `RenterId` (GUID), `Status` (enum), `CreatedAt` (UTC).
  - Danh sách `RentalItems`:
    - `RentalItemId` (GUID), `RentalOrderId` (GUID), `RentalSubOrderId` (GUID), `SupplierId` (GUID).
    - `EquipmentListingId` (GUID), `ListingTitleSnapshot` (string).
    - `EquipmentTypeId` (GUID, nullable), `EquipmentTypeNameSnapshot` (string, nullable).
    - `BrandId` (GUID, nullable), `BrandNameSnapshot` (string, nullable).
    - `Quantity` (int $\ge 1$), `RentalAmount` (decimal), `DepositAmount` (decimal).
    - `Status` (enum RentalItemStatus).
- **Khóa chống trùng**: `RentalOrderId`, `RentalItemId`.

#### B. Diễn biến chuyển trạng thái (`RentalStateTransition`)
- **Dữ liệu cần nhận**:
  - `TransitionId` (GUID), `RentalOrderId` (GUID), `RentalItemId` (GUID, nullable).
  - `FromState` (string), `ToState` (string), `Action` (string).
  - `ActorId` (GUID, nullable), `ActorRole` (string/enum: Customer, Supplier, Admin, System).
  - `OccurredAt` (UTC): Thời điểm thực tế xảy ra chuyển trạng thái.
  - `OperationId` (string).
- **Khóa chống trùng**: `TransitionId` hoặc `(RentalOrderId, RentalItemId, ToState, Action, OccurredAt)`.

---

### 6.3. Nguyên tắc Idempotency và Dữ liệu đến Muộn (Late-arriving data)
1. **Event Replay / Thao tác lặp lại**:
   - Khi một message event được gửi lại (replay) từ Kafka hoặc outbox publisher, hệ thống đối chiếu khóa chống trùng (Idempotency Key). Nếu bản ghi đã tồn tại, **tuyệt đối không cộng thêm số lượng hay giá trị tiền**.
2. **Dữ liệu đến muộn (Late-arriving data)**:
   - Dữ liệu đến sau (ví dụ: mất kết nối mạng khiến event thanh toán của ngày hôm trước chỉ nhận được vào hôm nay) phải được **ghi nhận và phân nhóm theo đúng mốc thời gian nghiệp vụ gốc** (`Invoice.PaidAt`), cập nhật lại dữ liệu lịch sử của ngày gốc đó; tuyệt đối không cộng vào ngày hiện tại nhận được tin.
3. **Thiếu nguồn dữ liệu**:
   - Khi một domain service chưa phát sinh dữ liệu hoặc trường thông tin bị thiếu, Analytics biểu diễn trạng thái "Chưa có dữ liệu" (`null` hoặc thiếu bucket), tuyệt đối **không được giả lập số 0 hoặc tự động suy đoán từ bảng khác**.

---

## 7. Bảng Đối chiếu Chênh lệch với Kế hoạch Dashboard Rental Hiện có

Bảng phân tích sự khác biệt cốt lõi giữa **Contract KPI Analytics v1** và kế hoạch triển khai dashboard nội bộ của RentalService (`RentalService/DASHBOARD_IMPLEMENTATION_PLAN.md`):

| Tiêu chí | Kế hoạch Dashboard Rental (`DASHBOARD_IMPLEMENTATION_PLAN.md`) | Chuẩn hóa Contract KPI Analytics v1 | Lý do điều chỉnh & Rủi ro loại trừ |
|---|---|---|---|
| **Mốc ghi nhận doanh thu / đơn thanh toán** | Sử dụng cột nội bộ `RentalOrder.PaymentConfirmedAt`. | Sử dụng `Invoice.PaidAt` do PaymentService chính thức cung cấp và đóng dấu. | `PaymentConfirmedAt` chỉ là thời điểm Rental nhận webhook (có thể trễ hàng giờ do nghẽn mạng/retry). Payment mới là chủ sở hữu quyền lực duy nhất xác nhận thời điểm tiền vào tài khoản. |
| **Xử lý đơn sau khi hủy / hoàn tiền** | Loại trừ item bằng `ExcludedStatuses = {Rejected, Expired, Cancelled}`. Nếu đơn bị hủy sau khi thanh toán, doanh thu tiền thuê trong quá khứ bị **trừ ngược** khỏi báo cáo. | **Không loại ngược giá trị Gross**. `GrossRentalValue` và `PaidOrders` ghi nhận vĩnh viễn tại ngày thanh toán. Khoản tiền trả lại được ghi nhận độc lập ở `RefundAmount`. | Kế toán và phân tích thương mại điện tử chuẩn quốc tế: Gross Merchandise Value (GMV) không thay đổi theo thời gian; hoàn tiền là dòng tiền hoàn trả độc lập. Việc trừ ngược làm sai lệch báo cáo tài chính đã chốt kỳ của kế toán. |
| **Doanh thu nền tảng (Platform Fee)** | Không định nghĩa hoặc tính nhẩm dựa trên giá trị đơn hàng. | Lấy nguyên snapshot `RentalItemSettlementRecord.PlatformFeeAmount` khi settlement `Completed`. | Phí sàn có thể thay đổi chính sách theo từng thời kỳ, khuyến mãi hoặc phân hạng đối tác. Analytics không được tự nhân chia mà phải lấy số Payment đã quyết toán. |
| **Trạng thái Active Orders** | Đếm snapshot dựa trên trạng thái hiện tại của toàn bộ bảng mà không phân biệt rõ trước hay sau thanh toán. | Snapshot `ActiveOrders` tại `asOf` **chỉ gồm các đơn đang thực hiện sau thanh toán**: `{Paid, AwaitingHandover, InRental, PendingSettlement}`. | Đơn `PendingPayment` chưa phát sinh nghĩa vụ cung cấp dịch vụ nên không được coi là Active Order đang vận hành. |
| **Đơn vị đếm Supplier Statistics** | Trong một số query đếm order, dễ nhầm lẫn giữa order và item. | Khóa toàn bộ thống kê supplier theo **distinct item** (`RentalItemId`) và số lượng thiết bị (`RentedQuantity`). | Đơn hàng trên DPRENTAL là đa đối tác (Multi-supplier). Một order có thể chứa thiết bị của 3 supplier khác nhau; đếm theo order cho supplier sẽ gây trùng lặp và sai số. |
| **Xử lý Timeout xác nhận** | Viết query Native SQL 4 CTE trong Rental (`GetSupplierConfirmationStatisticsAsync`). | Chuẩn hóa event/transition `SupplierTimeout` từ `PendingSupplierConfirmation` sang `Expired` do hệ thống kích hoạt. | Thống nhất dữ liệu qua diễn biến chuyển trạng thái (`RentalStateTransition`), đảm bảo mọi báo cáo kiểm toán đều truy xuất được nguồn gốc sự kiện. |

---

## 8. Hiện trạng Triển khai và Event Thực tế Giữa Các Service

Hợp đồng dữ liệu báo cáo giữa RentalService, PaymentService và AnalyticsService đã được thống nhất và chuẩn hóa theo các topic và event thực tế:

### 8.1. Dữ liệu và Event nguồn đã triển khai
- **PaymentService (`payment.analytics.v1`)**:
  - Event tài chính ký số điện tử (digital proof signature), gửi trực tiếp vào `payment.analytics.v1` để Analytics ghi nhận:
    - `InvoicePaidFinancial`: Đơn hàng được thanh toán, gồm `RentalAmount` (GMV), `DepositAmount` (tiền cọc), `TotalAmount`, `PaidAt`.
    - `RentalItemFinancialSettled`: Quyết toán từng item hoàn tất, gồm `PlatformFeeAmount`, `SupplierPayable`, `FinalDeductionAmount`, `DepositRefundDue`, `CompletedAt`.
    - `RefundRequestedFinancial`: Yêu cầu hoàn tiền phát sinh nghĩa vụ nợ, gồm `RefundRef`, `RentalOrderId`, `Amount`, `Currency`, `RequestedAt`.
    - `RefundCompletedFinancial`: Hoàn tiền hoàn tất chuyển khoản, gồm `RefundRef`, `RentalOrderId`, `RentalItemId?`, `Amount`, `Currency`, `CompletedAt`.
    - `SupplierPayoutCompleted`: Lệnh chi tiền cho NCC hoàn tất chuyển khoản, gồm `PayoutRef`, `SettlementId`, `RentalItemId`, `SupplierId`, `Amount`, `CompletedAt`.
- **RentalService (`rental.events.v1`)**:
  - Bảng `rental_orders` và `rental_items` lưu trữ đầy đủ `Quantity`, snapshot `EquipmentTypeId`, `BrandId`, `ListingTitle`.
  - Vòng đời đơn và thiết bị được phát hành qua `rental.events.v1`:
    - Cấp đơn: `RentalOrderCreated`, `RentalOrderPaid`, `RentalOrderCompleted`, `RentalOrderCancelled`, `RentalOrderExpired`, `RentalOrderRejected`.
    - Cấp item: `RentalItemConfirmed`, `RentalItemRejected`, `RentalItemExpired`, `RentalItemCancelled`, `RentalItemHandoverCompleted`, `RentalItemReturned`, `RentalItemCompleted`.

### 8.2. Ranh giới vận hành và Snapshot Analytics
1. **GMV và Dòng tiền**:
   - Toàn bộ GMV toàn sàn, tiền thanh toán, quyết toán, hoàn tiền và payout lấy trực tiếp từ `PaymentFinancialEvent` (`payment.analytics.v1`).
   - Đối với nhà cung cấp (Supplier), tiền thuê được phân rã bằng các item thuộc sở hữu của nhà cung cấp trong snapshot Rental, gắn với mốc thanh toán `PaidAt` của Invoice; tuyệt đối không gán toàn bộ tiền của đơn đa NCC cho từng NCC.
2. **PendingRefund**:
   - Là snapshot nghĩa vụ hoàn tiền tại thời điểm gọi. Khi chưa có event yêu cầu hoàn tiền (`RefundRequestedFinancial`) trong hệ thống, chỉ số trả về `null` thay vì 0 để tránh hiển thị sai lệch khi producer chưa kích hoạt nguồn.

## 9. Bộ Kịch bản Nghiệm thu & Trường hợp Biên (Acceptance Scenarios & Edge Cases)

### Kịch bản 1: Tiền thuê và Tiền cọc tách biệt
- **Dữ liệu**: Khách thuê 1 máy hàn trong 3 ngày: Tiền thuê 600.000 VND, Tiền cọc 2.000.000 VND. Invoice INV-01 có `TotalAmount = 2.600.000 VND`, thanh toán thành công lúc `2026-10-20T10:00:00Z`.
- **Kết quả nghiệm thu**:
  - `GrossRentalValue` ngày 20-10 = `600.000 VND`.
  - `DepositCollected` ngày 20-10 = `2.000.000 VND`.
  - `PlatformRevenue` ngày 20-10 = `0 VND` (chưa quyết toán).
  - Không có bất kỳ khoản tiền 2.600.000 VND nào được ghi nhận vào doanh thu.

### Kịch bản 2: Thanh toán thành công sau đó bị Hủy / Hoàn tiền toàn phần
- **Dữ liệu**: Đơn ORD-100 thanh toán lúc `2026-10-20T08:00:00Z` với tiền thuê 1.500.000 VND, tiền cọc 3.000.000 VND. Đến ngày `2026-10-21T09:00:00Z`, do khách bận việc đột xuất nên bấm hủy đơn. Admin duyệt hoàn tiền toàn bộ 4.500.000 VND lúc `2026-10-22T14:00:00Z`.
- **Kết quả nghiệm thu**:
  - **Ngày 2026-10-20**: `PaidOrders` = 1, `GrossRentalValue` = 1.500.000 VND, `DepositCollected` = 3.000.000 VND.
  - **Ngày 2026-10-21**: `CancelledOrders` = 1 (transition cấp order lúc 09:00Z). `GrossRentalValue` của ngày 20-10 **vẫn giữ nguyên 1.500.000 VND**, tuyệt đối không bị trừ về 0.
  - **Ngày 2026-10-22**: `RefundAmount` ghi nhận 4.500.000 VND (loại `ManualRefundKind.RentalRefund`).

### Kịch bản 3: Settlement khác ngày Refund và khác ngày Payout
- **Dữ liệu**: Đơn ORD-200 trả máy ngày 2026-10-24.
  - Ngày `2026-10-25T08:00:00Z`: Payment tạo bản ghi quyết toán STL-200: Tiền thuê 2.000.000 VND, Phí sàn 200.000 VND, Hư hỏng nhẹ khấu trừ 300.000 VND, SupplierPayable = 2.100.000 VND, Hoàn cọc còn thừa = 1.700.000 VND. Trạng thái settlement đạt `Completed` lúc `2026-10-25T09:00:00Z`.
  - Ngày `2026-10-26T10:00:00Z`: Admin chuyển tiền hoàn cọc 1.700.000 VND cho khách (`ManualRefundRequirement` completed).
  - Ngày `2026-10-27T15:00:00Z`: Admin chuyển tiền payout 2.100.000 VND cho Supplier (`SupplierPayout` completed).
- **Kết quả nghiệm thu**:
  - **Ngày 2026-10-25**: `PlatformRevenue` = 200.000 VND, `DeductionAmount` = 300.000 VND, `SupplierPayable` = 2.100.000 VND, `PendingRefundAmount` snapshot cuối ngày = 1.700.000 VND.
  - **Ngày 2026-10-26**: `RefundAmount` = 1.700.000 VND, `PendingRefundAmount` snapshot giảm về 0.
  - **Ngày 2026-10-27**: `SupplierPayoutCompleted` = 2.100.000 VND.
  - Dòng tiền phản ánh chính xác từng ngày nghiệp vụ phát sinh.

### Kịch bản 4: Đơn hàng Đa đối tác (Multi-supplier Order)
- **Dữ liệu**: Đơn hàng ORD-300 gồm 2 item:
  - Item 1 của Supplier X: Tiền thuê 1.000.000 VND.
  - Item 2 của Supplier Y: Tiền thuê 1.500.000 VND.
  - Invoice thanh toán lúc `2026-10-21T07:00:00Z`.
- **Kết quả nghiệm thu**:
  - Cấp toàn sàn: `TotalOrders` = 1, `PaidOrders` = 1, `GrossRentalValue` = 2.500.000 VND, `RentalItemCount` = 2.
  - Dashboard Supplier X: `Supplier RentalValue` = 1.000.000 VND.
  - Dashboard Supplier Y: `Supplier RentalValue` = 1.500.000 VND.
  - Tổng số tiền phân rã từng supplier bằng đúng tổng toàn sàn.

### Kịch bản 5: Item có số lượng lớn hơn 1 (`Quantity > 1`)
- **Dữ liệu**: Khách thuê 1 dòng sản phẩm "Giàn giáo xây dựng": `RentalItem.Id = ITM-555`, `Quantity = 10`, tiền thuê 1.000.000 VND. Thanh toán lúc `2026-10-21T11:00:00Z`.
- **Kết quả nghiệm thu**:
  - `RentalItemCount` = 1 (chỉ đếm distinct `RentalItemId`).
  - `RentedQuantity` = 10.
  - Bảng xếp hạng Top: Danh mục giàn giáo được cộng 10 đơn vị số lượng thuê.

### Kịch bản 6: Duplicate Event và Late-arriving Data
- **Dữ liệu**:
  - Event `InvoicePaid` của Invoice INV-888 xảy ra lúc `2026-10-18T10:00:00Z`, nhưng do nghẽn mạng đến ngày `2026-10-20T04:00:00Z` mới được Analytics nhận lần đầu.
  - Đến `2026-10-20T04:05:00Z`, Kafka rebalance khiến event đó được gửi lại lần 2.
- **Kết quả nghiệm thu**:
  - Lần nhận đầu tiên: Số liệu tiền thuê được ghi nhận vào báo cáo của ngày **2026-10-18** (ngày nghiệp vụ gốc theo `PaidAt`), không ghi nhận vào ngày 2026-10-20.
  - Lần nhận thứ hai: Hệ thống kiểm tra khóa chống trùng `InvoiceId = INV-888`, xác định đã tồn tại và bỏ qua; không có số liệu nào bị cộng lặp.

### Kịch bản 7: Điểm biên thời gian 16:59:59Z vs 17:00:00Z qua 2 ngày Việt Nam
- **Quy tắc**: Múi giờ Việt Nam là UTC+7.
  - `16:59:59Z` $+ 7\text{ giờ} = \text{23:59:59+07}$ (Vẫn thuộc ngày cũ).
  - `17:00:00Z` $+ 7\text{ giờ} = \text{00:00:00+07}$ (Bắt đầu ngày mới tiếp theo).
- **Dữ liệu**:
  - Event A: `Invoice.PaidAt = 2026-10-20T16:59:59Z`.
  - Event B: `Invoice.PaidAt = 2026-10-20T17:00:00Z`.
- **Kết quả nghiệm thu**:
  - Khi xem dashboard ngày **2026-10-20 (Asia/Ho_Chi_Minh)**: Cửa sổ truy vấn UTC là `[2026-10-19T17:00:00Z, 2026-10-20T17:00:00Z)`. Event A nằm trong khoảng này và được tính vào ngày 20-10.
  - Event B rơi vào đúng mốc mở của ngày tiếp theo nên được tính vào ngày **2026-10-21 (Asia/Ho_Chi_Minh)**.
  - Không có event nào bị rơi vào khoảng trống hoặc bị đếm trùng lặp giữa hai ngày.

### Kịch bản 8: Kiểm tra Độc lập Snapshot và Không cộng dồn
- **Dữ liệu**:
  - Cuối ngày 2026-10-20: Có 3 đơn `ActiveOrders`.
  - Cuối ngày 2026-10-21: Cả 3 đơn trên vẫn đang chạy, có thêm 1 đơn mới thanh toán $\rightarrow$ Có 4 đơn `ActiveOrders`.
- **Kết quả nghiệm thu**:
  - Khi biểu diễn biểu đồ đường (Timeseries): Ngày 20-10 hiển thị 3; Ngày 21-10 hiển thị 4.
  - Khi hiển thị tổng quan kỳ từ ngày 20-10 đến 21-10: Tuyệt đối **không hiển thị tổng = 7**. Giá trị tổng hợp kỳ của snapshot là giá trị tại thời điểm kết thúc kỳ báo cáo (tức 4 đơn).
