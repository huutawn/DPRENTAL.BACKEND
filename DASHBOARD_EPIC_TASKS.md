# EPIC: Dashboard Supplier & Admin (Backend API)

> **Mục tiêu**: Cung cấp API số liệu để Frontend dựng hai dashboard:
> - **Supplier dashboard**: chủ thiết bị công trường (máy đầm cóc Mikasa, máy lu rung Sakai, máy trộn bê tông...) theo dõi đội máy, đơn thuê và việc cần xử lý.
> - **Admin dashboard**: quản trị viên theo dõi sức khỏe toàn nền tảng và các hàng đợi cần can thiệp.
>
> Epic chỉ làm **backend API**. Frontend, xuất báo cáo và dashboard theo Organization nằm ngoài phạm vi (xem mục 4).

---

## 1. Quyết định kiến trúc của Epic

| # | Quyết định | Lý do |
| :-- | :--- | :--- |
| 1 | **Mỗi service tự trả số liệu của dữ liệu mình sở hữu**: Rental, Inventory, Auth, Payment. Frontend gọi song song các endpoint rồi tự ghép màn hình. | AGENTS.md 18.2: không query DB của service khác, không join chéo DB. |
| 2 | **Không** dựng Reporting service, BFF, data warehouse, materialized view hay cache trong Epic này. | AGENTS.md 3.1: không over-engineer. Query aggregate trực tiếp trên DB của chính service, có index phù hợp. |
| 3 | "Supplier" = **user sở hữu thiết bị**: `EquipmentListing.OwnerUserId` = `RentalItem.SupplierId` = `JWT.sub`. | AGENTS.md mục 17. Auth đã có thực thể `Organization`, nhưng Inventory và Rental chưa gắn dữ liệu theo tổ chức. Dashboard theo tổ chức cần task kiến trúc riêng. |
| 4 | **Subtask D0 freeze contract trước**, sau đó D1–D6 làm song song, mỗi subtask trên nhánh của service tương ứng. | AGENTS.md 30.1 (contract-first). |
| 5 | Rental chỉ báo **GMV thuê** (giá trị thuê đã đặt). Tiền thực thu là số liệu của **Payment**. | Rental không chuyển tiền (AGENTS.md 12, 16). Không gọi số liệu của Rental là "doanh thu". |
| 6 | Không thêm permission mới. Admin dùng lại cơ chế hiện có của từng service. | Tránh đổi contract của AuthService. |

---

## 2. Bảng tổng quan Subtask

| # | Service / nhánh gợi ý | Nội dung | Phụ thuộc |
| :-- | :--- | :--- | :--- |
| **D0** | `docs/contracts/dashboard-v1.md` | Freeze contract chung: query param, envelope, định nghĩa chỉ số, mã lỗi, quy ước giờ VN | — |
| **D1** | Rental `rental/feat/DPR-xxx-Supplier-Dashboard` | Supplier: đơn theo trạng thái, việc cần xử lý, GMV, tỉ lệ xác nhận, timeseries | D0 |
| **D2** | Rental `rental/feat/DPR-xxx-Admin-Dashboard` | Admin: đơn/dòng theo trạng thái, GMV, quá hạn, quyết toán, khiếu nại, top supplier | D0 |
| **D3** | Inventory `inventory/feat/DPR-xxx-Supplier-Dashboard` | Supplier: listing, asset, ảnh chụp lịch hôm nay, tỉ lệ khai thác | D0 |
| **D4** | Inventory `inventory/feat/DPR-xxx-Admin-Dashboard` | Admin: như D3 nhưng toàn nền tảng, kèm phân theo loại thiết bị | D0 (nên làm sau D3 để dùng lại query) |
| **D5** | Auth `auth/feat/DPR-xxx-Admin-Dashboard` | Admin: user, organization chờ duyệt, đăng ký mới theo thời gian | D0 |
| **D6** | Payment `payment/feat/DPR-xxx-Admin-Dashboard` | Admin: invoice theo trạng thái, tiền đã thu, giao dịch cần review | D0 |
| **D7** | Gateway `gateway/feat/DPR-xxx-Payment-Dashboard-Route` | Route Gateway cho dashboard Payment | D6 |

> Số `DPR-xxx` sẽ gán khi tạo ticket trên Jira.

```text
D0 ──┬── D1 (Rental supplier)
     ├── D2 (Rental admin)
     ├── D3 (Inventory supplier) ── D4 (Inventory admin)
     ├── D5 (Auth admin)
     └── D6 (Payment admin) ── D7 (Gateway)
```

**Màn hình Frontend sẽ ghép từ:**

| Dashboard | Nguồn |
| :--- | :--- |
| Supplier | D1 (đơn thuê, GMV, việc cần làm) + D3 (đội máy, khai thác) |
| Admin | D2 (giao dịch thuê) + D4 (kho thiết bị) + D5 (người dùng, tổ chức) + D6 (thanh toán) |

---

## 3. Các Subtask chi tiết

### D0 — Freeze contract Dashboard v1

```text
TASK:
DPR-xxx [Docs] Freeze dashboard API contract v1

GOAL:
Các team làm D1–D6 song song mà không phải đoán tên route, tham số, định nghĩa chỉ số hay mã lỗi.

CONTEXT:
- Đọc AGENTS.md (mục 16, 18, 23, 24, 30).
- Tham khảo endpoint thống kê đã có:
  - Rental GET admin/suppliers/{supplierId}/confirmation-statistics (AdminSupplierStatisticsController, SupplierStatisticsService).
  - Inventory GET equipment-listings/statistics và GET public/equipment-listings/statistics.

IN SCOPE:
- File docs/contracts/dashboard-v1.md.
- Quy ước route, query param, envelope response, định nghĩa từng chỉ số, mã lỗi, quy tắc phân quyền chung.

OUT OF SCOPE:
- Code của bất kỳ service nào.
```

**Nội dung bắt buộc của contract:**

1. **Route**, theo prefix sẵn có của từng service:

   | Service | Supplier | Admin |
   | :--- | :--- | :--- |
   | Rental | `GET /api/v1/rental/supplier/dashboard/{summary\|timeseries}` | `GET /api/v1/rental/admin/dashboard/{summary\|timeseries\|top-suppliers}` |
   | Inventory | `GET /api/v1/inventory/supplier/dashboard/{summary\|utilization}` | `GET /api/v1/inventory/admin/dashboard/{summary\|utilization}` |
   | Auth | — | `GET /api/v1/auth/admin/dashboard/{summary\|timeseries}` |
   | Payment | — | `GET /api/v1/payment/admin/dashboard/{summary\|timeseries}` |

2. **Query param chung:**

   | Param | Kiểu | Mặc định | Quy tắc |
   | :--- | :--- | :--- | :--- |
   | `from` | `yyyy-MM-dd` (Business Date VN) | hôm nay − 29 ngày | Bao gồm ngày `from` |
   | `to` | `yyyy-MM-dd` (Business Date VN) | hôm nay | Bao gồm ngày `to`. `from > to` trả 400 |
   | `granularity` | `day\|week\|month` | `day` | Chỉ dùng cho timeseries/utilization. Tuần bắt đầu thứ Hai |
   | `metric` | tùy endpoint | bắt buộc với timeseries | Giá trị lạ trả 400 |

   - Khoảng thời gian quy đổi sang UTC là `[from 00:00 Asia/Ho_Chi_Minh, (to + 1) 00:00 Asia/Ho_Chi_Minh)`.
   - Khoảng tối đa là **366 ngày**.
   - "Hôm nay" lấy từ `TimeProvider` theo giờ VN, không dùng giờ máy.

3. **Hai loại chỉ số** (mỗi field trong DTO phải ghi rõ thuộc loại nào):
   - **Snapshot**: trạng thái *tại thời điểm gọi*, không phụ thuộc `from`/`to`. Ví dụ: số dòng đang `IN_RENTAL`, số việc cần xử lý.
   - **Period**: sự kiện *xảy ra trong khoảng* `from`–`to`. Ví dụ: số dòng được thanh toán, GMV.

4. **Envelope response:**

   ```json
   {
     "range": { "from": "2026-10-01", "to": "2026-10-19", "timezone": "Asia/Ho_Chi_Minh" },
     "generatedAt": "2026-10-19T03:15:00Z",
     "currency": "VND",
     "...": "các khối số liệu của endpoint"
   }
   ```

   Timeseries:

   ```json
   {
     "range": { "from": "2026-10-01", "to": "2026-10-07", "timezone": "Asia/Ho_Chi_Minh" },
     "granularity": "day",
     "metric": "gmv",
     "points": [
       { "bucketStart": "2026-10-01", "value": 12500000 },
       { "bucketStart": "2026-10-02", "value": 0 }
     ]
   }
   ```

   - Bucket không có dữ liệu vẫn có mặt với `value = 0`.
   - Tiền là `decimal`, đơn vị VND.
   - Bucket đầu và cuối có thể bị cắt theo `from`/`to`.

5. **Định nghĩa chỉ số tiền (Rental):**
   - `gmvRental` = Σ `RentalItem.RentalAmount` của các dòng **đã thanh toán**, tức dòng từng vào `PENDING_SUPPLIER_CONFIRMATION`, và hiện **không** ở `REJECTED`/`EXPIRED`/`CANCELLED`. Mốc thời gian là lúc đơn được thanh toán (`RentalOrder.PaymentConfirmedAt`).
   - `depositHeld` (snapshot) = Σ `DepositAmount` của các dòng `AWAITING_HANDOVER`/`IN_RENTAL`/`PENDING_SETTLEMENT`.
   - Không gọi các số này là "doanh thu", "tiền đã thu" hay "payout".

6. **Mã lỗi** (theo `Result`/`Error` → `ProblemDetails`):

   | HTTP | Code | Khi nào |
   | :--- | :--- | :--- |
   | 400 | `DASHBOARD_RANGE_INVALID` | `from`/`to` sai định dạng hoặc `from > to` |
   | 400 | `DASHBOARD_RANGE_TOO_LARGE` | Khoảng > 366 ngày |
   | 400 | `DASHBOARD_PARAM_INVALID` | `granularity`/`metric`/`top` không hợp lệ |
   | 401 | `AUTH_REQUIRED` | Chưa đăng nhập; service token gọi API supplier (giữ như các API supplier hiện có của Rental) |
   | 403 | `FORBIDDEN` | Gọi API admin mà không phải admin; service token gọi API admin |

7. **Quy tắc phân quyền chung:**
   - **API supplier**:
     - Luôn lọc theo `JWT.sub` và **không nhận** tham số `supplierId`/`ownerUserId` (chống IDOR, mass assignment).
     - User đã đăng nhập nhưng chưa có thiết bị nhận 200 với toàn số 0.
   - **API admin**: chỉ admin là người thật, theo cơ chế sẵn có (`RequireAdmin` ở Rental, `IsAdmin` ở Inventory, role `adm` ở Auth/Payment). Không nhận service token.
   - **Không trả** SerialNumber, AssetCode, RenterId, thông tin cá nhân của renter, hay ghi chú nội bộ trong dashboard supplier.

**ACCEPTANCE CRITERIA:**
1. File contract có đủ 7 phần trên và DTO mẫu cho từng endpoint của D1–D6.
2. Mỗi chỉ số có định nghĩa rõ: nguồn bảng/field, điều kiện lọc, loại snapshot/period và mốc thời gian.
3. Được review bởi owner của Rental, Inventory, Auth và Payment trước khi D1–D6 bắt đầu.

---

### D1 — Rental: Supplier dashboard

```text
TASK:
DPR-xxx [Rental] Supplier dashboard API

GOAL:
Supplier xem nhanh tình hình đơn thuê của mình và biết việc nào cần làm ngay
(xác nhận đơn, giao máy, quyết toán).

CONTEXT:
- Relevant service: RentalService
- Contract: docs/contracts/dashboard-v1.md (D0)
- Dùng lại:
  - RentalAuthorizationPolicy.AuthorizeSupplierCaller (Application/Security/RentalAuthorizationPolicy.cs)
  - Query transition của SupplierStatisticsService (Application/Services/SupplierStatistics/,
    SQL ở Domain/Repositories/RentalOrderRepository.cs ~249-300)

IN SCOPE:
- GET /api/v1/rental/supplier/dashboard/summary
- GET /api/v1/rental/supplier/dashboard/timeseries?metric=paidItems|gmv
- Index cần thiết (nếu query plan cần) + migration riêng.

OUT OF SCOPE:
- Số liệu kho/asset (D3).
- Tiền thực nhận/payout (thuộc Payment, chưa có).
- Danh sách chi tiết đơn (đã có supplier/sub-orders).
```

**REQUIRED BEHAVIOR — `summary`:**

| Khối | Field | Loại | Định nghĩa |
| :--- | :--- | :--- | :--- |
| `itemsByStatus` | đếm theo từng `RentalItemStatus` | snapshot | Các dòng có `SupplierId = sub` |
| `actionRequired` | `pendingConfirmation` | snapshot | Dòng `PENDING_SUPPLIER_CONFIRMATION` |
| | `confirmationDueSoon` | snapshot | Trong số trên, `SupplierConfirmationDeadline` ≤ now + 6h |
| | `awaitingHandoverToday` | snapshot | `AWAITING_HANDOVER` và `ExpectedStartDate` = hôm nay (VN) |
| | `handoverOverdue` | snapshot | `AWAITING_HANDOVER` và hôm nay (VN) ≥ `ExpectedStartDate + 1` |
| | `returnOverdue` | snapshot | `IN_RENTAL` và hôm nay (VN) > `ExpectedEndDate` |
| | `pendingSettlement` | snapshot | `PENDING_SETTLEMENT` |
| | `openDisputes` | snapshot | Dispute `Open` của các dòng thuộc supplier |
| `period` | `paidItems` | period | Số dòng của supplier có đơn được thanh toán trong khoảng |
| | `gmvRental` | period | Theo định nghĩa D0 |
| | `completedItems` | period | Dòng chuyển sang `COMPLETED` trong khoảng (từ `rental_state_transitions`) |
| `confirmation` | `accepted`, `rejected`, `timeout`, `cancelAfterAccept`, `acceptRate` | period | Cùng logic `SupplierStatisticsService`, với `supplierId = sub`. `acceptRate = accepted / (accepted + rejected + timeout)`, mẫu số 0 thì trả `null` |
| `depositHeld` | | snapshot | Theo định nghĩa D0 |

**`timeseries`:** `metric=paidItems|gmv`, bucket theo `PaymentConfirmedAt` quy đổi sang giờ VN.

**DOMAIN INVARIANTS:**
- Read-only, không đổi state, không phát event.
- Dòng `REJECTED`/`EXPIRED`/`CANCELLED` không cộng vào GMV.
- `returnOverdue` phải tính từ trạng thái Rental, không phụ thuộc Inventory.

**CONCURRENCY / IDEMPOTENCY:** GET read-only, không lock. Dùng `AsNoTracking`, aggregate trong SQL (`GROUP BY`), không load entity lên memory.

**SECURITY:**
- Supplier = `sub`. Không có tham số `supplierId`.
- Service token trả 401 `AUTH_REQUIRED`.
- Không trả RenterId hay thông tin renter.

**EXPECTED FILE AREAS:**
- `Application/Services/Dashboard/` (`ISupplierDashboardService`, `SupplierDashboardService`, DTO, validator khoảng ngày dùng chung với D2)
- Repository query mới, tách riêng, không nhồi vào service cũ.
- `Api/Controllers/SupplierDashboardController.cs` (controller mỏng)
- `tests/Api.Tests/.../SupplierDashboardTests.cs`

**ACCEPTANCE CRITERIA:**
1. Hai endpoint trả đúng schema D0. Bucket rỗng có `value = 0`.
2. Supplier A không bao giờ thấy số của supplier B.
3. Mọi mốc ngày tính theo Asia/Ho_Chi_Minh qua `TimeProvider`.
4. Swagger có mô tả; `SwaggerDocumentationTests` cập nhật số operation.

**TEST CASES:**
- Supplier A có 2 dòng (máy lu Sakai `IN_RENTAL`, máy đầm Mikasa `PENDING_SUPPLIER_CONFIRMATION`), supplier B có 1 dòng: A chỉ thấy 2 dòng của mình.
- Service token trả 401. User không có thiết bị nhận toàn số 0.
- Đơn thanh toán lúc 23:30 ngày 10/10 giờ VN (16:30 UTC) rơi vào bucket 10/10. Đơn lúc 00:30 ngày 11/10 giờ VN (17:30 UTC ngày 10/10) rơi vào bucket 11/10.
- Dòng `REJECTED` và `CANCELLED` không cộng vào `gmvRental`.
- `IN_RENTAL` với `ExpectedEndDate` = hôm qua được đếm vào `returnOverdue`.
- `from > to` và khoảng 367 ngày trả 400. `metric=abc` trả 400.
- `acceptRate = null` khi không có quyết định nào.

---

### D2 — Rental: Admin dashboard

```text
TASK:
DPR-xxx [Rental] Admin dashboard API

GOAL:
Admin theo dõi giao dịch thuê toàn nền tảng và các hàng đợi cần can thiệp
(quá hạn, quyết toán, khiếu nại).

CONTEXT:
- Relevant service: RentalService
- Contract: D0
- Dùng lại: RentalAuthorizationPolicy.RequireAdmin (thêm AuthorizeAdminDashboardRead cùng kiểu
  AuthorizeAdminStatisticsRead), validator khoảng ngày của D1.

IN SCOPE:
- GET /api/v1/rental/admin/dashboard/summary
- GET /api/v1/rental/admin/dashboard/timeseries?metric=orders|paidOrders|gmv
- GET /api/v1/rental/admin/dashboard/top-suppliers?by=gmv|items&top=10

OUT OF SCOPE:
- Tên/hồ sơ supplier (FE lấy từ Auth theo supplierId).
- Tiền thực thu/hoàn (D6).
```

**REQUIRED BEHAVIOR — `summary`:**

| Khối | Field | Loại | Định nghĩa |
| :--- | :--- | :--- | :--- |
| `ordersByStatus` | theo `RentalOrderStatus` | snapshot | Toàn nền tảng |
| `itemsByStatus` | theo `RentalItemStatus` | snapshot | Toàn nền tảng |
| `period` | `ordersCreated` | period | Đơn tạo trong khoảng (`CreatedAt`) |
| | `ordersPaid` | period | Đơn có `PaymentConfirmedAt` trong khoảng |
| | `paymentExpiredRate` | period | Đơn tạo trong khoảng có transition `PENDING_PAYMENT → EXPIRED` ÷ `ordersCreated`. Mẫu số 0 thì `null`. Chỉ xét transition, không xét `Status = EXPIRED`, vì đơn cũng có thể thành `EXPIRED` khi mọi dòng hết hạn chờ supplier |
| | `gmvRental` | period | Theo định nghĩa D0 |
| | `activeSuppliers` | period | Số `SupplierId` phân biệt có dòng được thanh toán trong khoảng |
| `attention` | `handoverOverdue`, `returnOverdue` | snapshot | Như D1, toàn nền tảng |
| | `pendingSettlement` | snapshot | Dòng `PENDING_SETTLEMENT` |
| | `openDisputes` | snapshot | Dispute `Open` |
| | `supplierConfirmationTimeouts` | period | Dòng hết hạn vì supplier không phản hồi trong khoảng (từ transition) |

**`top-suppliers`:**
- Response: `[{ supplierId, paidItems, gmvRental }]`, sắp xếp theo `by`.
- `top` từ 1 đến 50, mặc định 10.

**SECURITY:**
- Chỉ admin là người thật. Service token trả 403, user thường trả 403 `FORBIDDEN`.
- Không trả thông tin cá nhân renter.

**EXPECTED FILE AREAS:**
- `Application/Services/Dashboard/` (`IAdminDashboardService`, `AdminDashboardService`)
- `Api/Controllers/AdminDashboardController.cs`
- `tests/Api.Tests/.../AdminDashboardTests.cs`

**ACCEPTANCE CRITERIA:**
1. Ba endpoint đúng schema D0.
2. Số liệu admin bằng tổng số liệu D1 của mọi supplier, với các chỉ số cùng định nghĩa.
3. Query aggregate trong SQL, không load toàn bảng.

**TEST CASES:**
- Non-admin trả 403. Service token trả 403. Admin trả 200.
- 3 đơn tạo trong khoảng, 1 đơn hết hạn thanh toán: `paymentExpiredRate = 0.3333`.
- `top-suppliers?top=0` và `top=51` trả 400. Hai supplier có GMV bằng nhau được sắp xếp ổn định (phụ theo `supplierId`).
- `returnOverdue` và `handoverOverdue` đúng ở biên ngày VN.

---

### D3 — Inventory: Supplier dashboard

```text
TASK:
DPR-xxx [Inventory] Supplier fleet dashboard API

GOAL:
Supplier thấy đội máy của mình: bao nhiêu listing, bao nhiêu máy vật lý,
hôm nay bao nhiêu máy đang thuê / bị khóa / rảnh, và tỉ lệ khai thác theo thời gian.

CONTEXT:
- Relevant service: InventoryService
- Contract: D0
- Dùng lại:
  - InventoryAccessEvaluator, InventoryCallerContext (Application/Security, Api/Security)
  - Logic owner statistics đã có (EquipmentListingsController GET statistics, GetOwnerStatisticsAsync)

IN SCOPE:
- GET /api/v1/inventory/supplier/dashboard/summary
- GET /api/v1/inventory/supplier/dashboard/utilization?from&to&granularity

OUT OF SCOPE:
- Đổi logic availability/allocation.
- Drill-down từng asset (đã có calendar API).
```

**REQUIRED BEHAVIOR — `summary` (snapshot):**

| Khối | Field | Định nghĩa |
| :--- | :--- | :--- |
| `listings` | `draft`, `active`, `inactive` | Listing có `OwnerUserId = sub` |
| `assets` | `total` | Asset thuộc listing của supplier |
| | `acceptingBookings` | `AcceptingBookings = true` |
| | `suspended` | `AcceptingBookings = false` |
| `today` | `occupied` | Asset có allocation `Held`/`Allocated` phủ hôm nay, **hoặc** đã bàn giao mà chưa trả (`HandedOverAt` có, `ReturnedAt` null), kể cả khi đã quá `RentalPeriod` |
| | `blocked` | Asset không thuộc `occupied` và có `CalendarBlock` phủ hôm nay, hoặc đang bị tạm ngưng |
| | `available` | `total − occupied − blocked` |

- Mỗi asset chỉ thuộc **một** nhóm, ưu tiên `occupied` → `blocked` → `available`.
- Tổng ba nhóm bằng `assets.total`.

**`utilization` (period):**
- Theo từng bucket: `assetDaysAvailable`, `assetDaysAllocated` và `utilizationRate = allocated / available` (mẫu số 0 thì `null`).
- `assetDaysAvailable`: số asset-ngày asset đã tồn tại và không bị `CalendarBlock`.
- `assetDaysAllocated`: số asset-ngày có allocation `Allocated` phủ, cộng các ngày quá hạn chưa trả.
- Tính **theo từng asset, từng ngày**. Không suy từ count theo ngày của listing (AGENTS.md 3.3, 8).

**DOMAIN INVARIANTS:**
- Asset hư đang bị block **không** được đếm là `available`.
- Asset quá hạn trả vẫn là `occupied` cho đến khi có actual return (AGENTS.md 14.3).
- Allocation `Cancelled`/`Released` không tính.

**SECURITY:**
- Owner = `sub`, không có tham số owner.
- Không trả SerialNumber, AssetCode hay SuspensionReason.
- Service token bị từ chối theo cách Inventory đang dùng cho API owner.

**EXPECTED FILE AREAS:**
- `Application/Dashboard/` (service, DTO, query)
- `Api/Controllers/SupplierDashboardController.cs`
- tests

**ACCEPTANCE CRITERIA:**
1. `occupied + blocked + available = total` trong mọi trường hợp.
2. Utilization tính trong SQL (ví dụ `generate_series` theo ngày), không loop từng asset trong C#.
3. Supplier A không thấy asset của B.

**TEST CASES:**
- 3 máy trộn bê tông: 1 đang thuê, 1 có `CalendarBlock` Maintenance hôm nay, 1 rảnh → `1/1/1`.
- Asset vừa có block vừa có allocation hôm nay chỉ được đếm vào `occupied`.
- Máy lu đã bàn giao, `RentalPeriod` kết thúc hôm qua, chưa trả → vẫn `occupied`.
- Asset tạm ngưng (`AcceptingBookings = false`) → `blocked`, không phải `available`.
- Utilization: 2 asset trong 10 ngày, 1 asset thuê 5 ngày → `5 / 20 = 0.25`.
- Biên ngày VN, khoảng > 366 ngày trả 400, owner khác không thấy số.

---

### D4 — Inventory: Admin dashboard

```text
TASK:
DPR-xxx [Inventory] Admin fleet dashboard API

GOAL:
Admin theo dõi quy mô và mức khai thác kho thiết bị toàn nền tảng, theo loại thiết bị.

CONTEXT:
- Relevant service: InventoryService
- Contract: D0. Dùng lại query của D3, bỏ filter owner.

IN SCOPE:
- GET /api/v1/inventory/admin/dashboard/summary
- GET /api/v1/inventory/admin/dashboard/utilization?from&to&granularity&equipmentTypeId?

OUT OF SCOPE:
- Tên loại thiết bị (FE lấy từ Catalog theo equipmentTypeId).
```

**REQUIRED BEHAVIOR:**
- `summary` gồm các khối như D3 cho toàn nền tảng, cộng thêm:
  - `byEquipmentType: [{ equipmentTypeId, listingsActive, assetsTotal, occupiedToday }]`
  - `activeSuppliers`: số `OwnerUserId` phân biệt có listing `Active`.
- `utilization` như D3, có thể lọc theo `equipmentTypeId`.

**SECURITY:**
- Chỉ admin (`caller.IsAdmin`, role `adm`). Service token bị từ chối, user thường trả 403.
- Admin được thấy số tổng hợp, **không** trả danh sách serial/asset code.

**ACCEPTANCE CRITERIA:**
1. Tổng `byEquipmentType` khớp khối tổng.
2. Cùng invariant với D3 (block, overdue).
3. Non-admin trả 403.

**TEST CASES:** non-admin 403; 2 supplier, 2 loại thiết bị (máy đầm cóc, máy lu) phân tách đúng; lọc `equipmentTypeId` đúng; biên ngày VN.

---

### D5 — Auth: Admin dashboard

```text
TASK:
DPR-xxx [Auth] Admin user & organization dashboard API

GOAL:
Admin biết số người dùng, tăng trưởng đăng ký và số tổ chức đang chờ duyệt.

CONTEXT:
- Relevant service: AuthService
- Contract: D0
- Dùng lại: phân quyền role adm như AdminUserController / AdminOrganizationsController.

IN SCOPE:
- GET /api/v1/auth/admin/dashboard/summary
- GET /api/v1/auth/admin/dashboard/timeseries?metric=newUsers|newOrganizations

OUT OF SCOPE:
- Thay đổi model Organization, gắn tổ chức vào listing/order.
```

**REQUIRED BEHAVIOR — `summary`:**

| Khối | Field | Loại | Định nghĩa |
| :--- | :--- | :--- | :--- |
| `users` | `total`, `active` (`IsActive`), `inactive`, `lockedOut` (`LockoutEnd > now`), `profileCompleted` | snapshot | |
| | `byRole` | snapshot | Đếm theo role hệ thống (`customer`, `Owner`, `Member`, `admin`...) |
| `organizations` | theo `OrganizationStatus` × `OrganizationType` | snapshot | |
| `actionRequired` | `organizationsPendingApproval` | snapshot | Organization `Pending` |
| `period` | `newUsers`, `newOrganizations` | period | Theo `CreatedAt`, quy đổi giờ VN |

**SECURITY:**
- Chỉ admin. Không trả email, số điện thoại, CitizenId hay bất kỳ PII nào.
- Chỉ trả số đếm.

**ACCEPTANCE CRITERIA:**
1. Đúng schema D0.
2. Không trả PII.
3. Non-admin trả 403.

**TEST CASES:** non-admin 403; user bị khóa đếm đúng `lockedOut`; organization Pending đếm vào `actionRequired`; user tạo 23:30 giờ VN rơi đúng bucket.

---

### D6 — Payment: Admin dashboard

```text
TASK:
DPR-xxx [Payment] Admin payment dashboard API

GOAL:
Admin theo dõi dòng tiền thực thu và các giao dịch ngân hàng cần xử lý tay.

CONTEXT:
- Relevant service: PaymentService
- Contract: D0
- Hiện Payment chỉ có policy InvoiceServicePolicy và InvoiceRenterPolicy,
  nên cần thêm policy admin (role adm), giống cách các service khác kiểm tra admin.

IN SCOPE:
- GET /api/v1/payment/admin/dashboard/summary
- GET /api/v1/payment/admin/dashboard/timeseries?metric=collected|invoicesPaid
- Policy admin cho Payment.

OUT OF SCOPE:
- Refund và payout supplier (Payment chưa có entity refund/payout). Thêm vào dashboard khi các tính năng đó tồn tại.
- Route Gateway (D7).
```

**REQUIRED BEHAVIOR — `summary`:**

| Khối | Field | Loại | Định nghĩa |
| :--- | :--- | :--- | :--- |
| `invoicesByStatus` | `PENDING`, `PAID`, `EXPIRED`, `CANCELLED` | snapshot | |
| `period` | `invoicesCreated` | period | Theo `Invoice.CreatedAt` |
| | `invoicesPaid` | period | Theo `Invoice.PaidAt` |
| | `collectedAmount`, `collectedRental`, `collectedDeposit` | period | Σ `TotalAmount` / `TotalRentalAmount` / `TotalDepositAmount` của invoice `PAID` theo `PaidAt` |
| `actionRequired` | `transactionsReviewRequired` | snapshot | `PaymentTransaction.Status = REVIEW_REQUIRED` |
| | `transactionsManualRefundRequired` | snapshot | `MANUAL_REFUND_REQUIRED` |
| | `transactionsUnmatched` | snapshot | `UNMATCHED` |

**SECURITY:**
- Chỉ admin. Không trả số tài khoản, nội dung chuyển khoản hay thông tin renter.

**ACCEPTANCE CRITERIA:**
1. Đúng schema D0.
2. Policy admin có test.
3. Số tiền chỉ tính invoice `PAID`.

**TEST CASES:** non-admin 403; service token 403; invoice `EXPIRED` không cộng vào `collectedAmount`; invoice trả lúc 23:30 giờ VN rơi đúng bucket; đếm đúng giao dịch `REVIEW_REQUIRED`.

---

### D7 — Gateway: Route dashboard Payment

```text
TASK:
DPR-xxx [Gateway] Expose payment admin dashboard route

GOAL:
Frontend gọi được dashboard Payment qua Gateway.

CONTEXT:
- Gateway/ocelot/appsettings.json hiện chưa có route nào tới payment-service.
- Rental, Inventory và Auth đã có catch-all /api/v1/<svc>/{everything}, nên D1–D5 không cần đổi Gateway.

IN SCOPE:
- Route hẹp: /api/v1/payment/admin/dashboard/{everything} → payment-service, có AuthenticationOptions.
- Kiểm tra payment-service có trong platform compose (thêm nếu thiếu, ở task/nhánh platform).

OUT OF SCOPE:
- Mở catch-all toàn bộ Payment hoặc route webhook ra Gateway (cần quyết định riêng).
```

**ACCEPTANCE CRITERIA:**
1. Gọi qua Gateway với token admin trả 200.
2. Không có token trả 401.
3. Các route Payment khác không bị lộ qua Gateway.

---

## 4. Ngoài phạm vi Epic

- Dashboard theo **Organization** thật. Cần gắn `OrganizationId` vào listing/order hoặc mapping org → user từ Auth, và phải có task kiến trúc riêng (AGENTS.md 17).
- Frontend / màn hình dashboard.
- BFF / API gộp, Reporting service, data warehouse, materialized view, cache.
- Xuất CSV/PDF, gửi báo cáo định kỳ qua email.
- Realtime (websocket/SSE).
- Refund, payout và phí nền tảng (Payment chưa có).
- Notification cho các việc cần xử lý.

---

## 5. Definition of Done chung cho D1–D6

- Đúng contract D0. Nếu cần đổi contract thì dừng lại và cập nhật D0 trước (AGENTS.md 30.3).
- Phân quyền có test: IDOR supplier, non-admin, service token.
- Mọi mốc ngày theo Asia/Ho_Chi_Minh qua `TimeProvider`, có test ở biên 23:30/00:30.
- Aggregate trong SQL. Không N+1, không load toàn bảng.
- Migration index (nếu có) đặt tên riêng, mô tả rollout/rollback.
- Swagger cập nhật.
- `dotnet build` và `dotnet test` của service pass.
