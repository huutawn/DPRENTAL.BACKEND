# DPR-244 — Plan triển khai chi tiết Dashboard Supplier & Admin (backend API)

> Đọc trước khi làm: [AGENTS.md](AGENTS.md) và [docs/PROJECT_GUIDE.md](docs/PROJECT_GUIDE.md).
> Plan này chỉ ra **từng bước, đúng file/class đang có** trên nhánh `origin/<svc>/develop` (khảo sát ngày 2026-10-19).
> `DPR-xxx` = số subtask Jira, thay khi đã tạo.

---

## 0. Quyết định chung (áp dụng mọi task)

| Chủ đề | Quyết định |
|---|---|
| Kiến trúc | Mỗi service tự trả số liệu của DB mình. Không query chéo DB, không BFF, không cache, không Reporting service. FE gọi song song rồi ghép. |
| Nhánh | Mỗi task một nhánh cắt từ `origin/<svc>/develop`: `<svc>/feat/DPR-xxx-<mô-tả>`. |
| Khoảng ngày | `from`/`to` dạng `yyyy-MM-dd` (ngày VN, bao gồm 2 đầu). Mặc định `to = hôm nay VN`, `from = to − 29`. Tối đa 366 ngày. Quy đổi UTC: `[StartOfDayUtc(from), StartOfDayUtc(to + 1))`. |
| Bucket | `granularity = day|week|month`. Tuần bắt đầu thứ Hai, tháng bắt đầu ngày 1. `bucketStart` là ngày VN đầu bucket; bucket đầu/cuối bị cắt theo `from`/`to`. Bucket rỗng vẫn trả `value = 0`. |
| "Bây giờ" | Luôn lấy từ `TimeProvider` (đã đăng ký DI ở cả service). Không dùng `DateTime.UtcNow`. |
| Snapshot vs Period | Snapshot = trạng thái lúc gọi, bỏ qua `from`/`to`. Period = sự kiện có mốc thời gian nằm trong khoảng. |
| Lỗi validate (mới, dùng chung) | `400 DASHBOARD_RANGE_INVALID` (sai định dạng, `from > to`), `400 DASHBOARD_RANGE_TOO_LARGE` (> 366 ngày), `400 DASHBOARD_PARAM_INVALID` (`granularity`/`metric`/`by`/`top` sai). |
| Lỗi phân quyền | **Giữ mã đang có của từng service**, không đặt mã mới (Single Source of Truth). Chi tiết ở từng task. |
| Phân tầng | Application quyết định mọi thứ (quyền, mốc giờ, tập trạng thái, công thức, lấp bucket). Repository chỉ lọc/đếm/cộng theo tiêu chí được truyền vào, trả record cụ thể, không trả `IQueryable`. Entity không thêm logic. Controller chỉ map request → service → `ProblemDetails`. |
| Truy vấn | LINQ server-side, `AsNoTracking()`, `.Select()` projection, `CountAsync/SumAsync/GroupBy` trên DB, truyền `CancellationToken`. Native SQL chỉ khi LINQ không làm được, có lý do trong PR, parameterize, integration test với Postgres thật, `EXPLAIN ANALYZE` nếu lý do là hiệu năng. |
| Bảo mật | API supplier lấy danh tính từ `JWT.sub`, không nhận `supplierId/ownerUserId`. Không trả `SerialNumber`, `AssetCode`, `SuspensionReason`, `RenterId`, email, SĐT, CitizenId, số tài khoản. |
| Kiểm tra | Task code: `dotnet build` + `dotnet test` của service pass 100%. Task tài liệu: review diff + `git diff --check`. |
| Commit | Dev tự commit. |

### Thứ tự làm & phụ thuộc

```
D0 (contract) ──┬── D1 Rental supplier ── D2 Rental admin
                ├── D3 Inventory supplier ── D4 Inventory admin
                ├── D5 Auth admin
                └── D6 Payment admin ── D7 Gateway + compose
```

D1, D3, D5, D6 chạy song song sau D0. D2 cần D1 (dùng lại repository method + validator). D4 cần D3. D7 cần D6.

---

## D0 — `[Docs] Freeze dashboard API contract v1`

**Nhánh:** nhánh docs hiện dùng cho `docs/contracts` (cùng chỗ với `docs/contracts/payment-invoice-kafka-v1.md`).
**Sản phẩm:** `docs/contracts/dashboard-v1.md`.

### Các bước
1. Tạo file theo khung giống `docs/contracts/payment-invoice-kafka-v1.md` (mục đích, phạm vi, version, changelog).
2. Viết **mục Quy ước chung**: copy bảng "Khoảng ngày / Bucket / Snapshot vs Period / Lỗi validate" ở mục 0.
3. Viết **bảng route** (route đầy đủ qua Gateway):

   | Service | Route | Ai gọi |
   |---|---|---|
   | Rental | `GET /api/v1/rental/supplier/dashboard/summary` | supplier |
   | Rental | `GET /api/v1/rental/supplier/dashboard/timeseries?metric=paidItems|gmv` | supplier |
   | Rental | `GET /api/v1/rental/admin/dashboard/summary` | admin |
   | Rental | `GET /api/v1/rental/admin/dashboard/timeseries?metric=orders|paidOrders|gmv` | admin |
   | Rental | `GET /api/v1/rental/admin/dashboard/top-suppliers?by=gmv|items&top=10` | admin |
   | Inventory | `GET /api/v1/inventory/supplier/dashboard/summary` | supplier |
   | Inventory | `GET /api/v1/inventory/supplier/dashboard/utilization` | supplier |
   | Inventory | `GET /api/v1/inventory/admin/dashboard/summary` | admin |
   | Inventory | `GET /api/v1/inventory/admin/dashboard/utilization?equipmentTypeId=` | admin |
   | Auth | `GET /api/v1/auth/admin/dashboard/summary` | admin |
   | Auth | `GET /api/v1/auth/admin/dashboard/timeseries?metric=newUsers|newOrganizations` | admin |
   | Payment | `GET /api/v1/payment/admin/dashboard/summary` | admin |
   | Payment | `GET /api/v1/payment/admin/dashboard/timeseries?metric=collected|invoicesPaid` | admin |

4. Viết **envelope chung** + JSON mẫu:
   ```json
   {
     "range": { "from": "2026-09-20", "to": "2026-10-19", "timezone": "Asia/Ho_Chi_Minh" },
     "generatedAt": "2026-10-19T03:15:00Z",
     "currency": "VND"
   }
   ```
   Timeseries: `{ range, granularity, metric, points: [{ "bucketStart": "2026-10-13", "value": 3 }] }`.
5. Viết **từ điển chỉ số**: mỗi chỉ số một dòng gồm *tên – loại (snapshot/period) – bảng/field nguồn – điều kiện lọc – mốc thời gian*. Lấy nguyên định nghĩa trong các task D1–D6 bên dưới. Ghi rõ:
   - `gmvRental` **không phải doanh thu**; tiền thực thu là của Payment (`collectedAmount`).
   - Utilization Inventory **không tính lịch sử tạm ngưng** (không có bảng lịch sử `AcceptingBookings`), chỉ trừ ngày bị `CalendarBlock`.
6. Viết **bảng lỗi phân quyền theo service** (giữ mã hiện có):

   | Service | Chưa đăng nhập | Service token | Không phải admin |
   |---|---|---|---|
   | Rental supplier | 401 `AUTH_REQUIRED` | 401 `AUTH_REQUIRED` | – |
   | Rental admin | 401 `Auth.Unauthorized` | 403 `Auth.ServiceTokenNotAllowed` | 403 `Auth.Forbidden` |
   | Inventory supplier | 401 `AUTH_REQUIRED` | 403 `SERVICE_TOKEN_REJECTED` | – |
   | Inventory admin | 401 `AUTH_REQUIRED` | 403 `SERVICE_TOKEN_REJECTED` | 403 (mã forbidden hiện có của Inventory) |
   | Auth admin | 401 | 403 (policy permission) | 403 |
   | Payment admin | 401 | 403 | 403 |

7. Viết **DTO mẫu** cho cả 13 endpoint (copy schema từ D1–D6).
8. Mời owner Rental, Inventory, Auth, Payment review. Sửa theo góp ý, ghi version `v1` + ngày chốt.
9. Kiểm tra: `git diff --check`.

**Done khi:** đủ 8 mục trên, 4 owner đã approve trên PR.

---

## D1 — `[Rental] Supplier dashboard API`

**Nhánh:** `rental/feat/DPR-xxx-supplier-dashboard` từ `origin/rental/develop`.
**Code tham chiếu:** feature `SupplierStatistics` (DTO → IService → Service → Repository → Controller) là mẫu cấu trúc.

### File sẽ tạo / sửa

| Loại | File |
|---|---|
| DTO | `RentalService/src/Application/DTOs/Dashboard/DashboardRangeRequest.cs`, `DashboardRange.cs`, `TimeseriesPoint.cs`, `SupplierDashboardSummaryResponse.cs`, `DashboardTimeseriesResponse.cs` |
| Validator | `RentalService/src/Application/Services/Dashboard/DashboardRangeValidator.cs` (static, dùng chung với D2) |
| Bucket | `RentalService/src/Application/Services/Dashboard/DashboardBucketFiller.cs` (static: sinh danh sách bucket + lấp 0) |
| Service | `RentalService/src/Application/IServices/Dashboard/ISupplierDashboardService.cs`, `RentalService/src/Application/Services/Dashboard/SupplierDashboardService.cs` |
| Repository record | `RentalService/src/Domain/IRepositories/DashboardRecords.cs` (các `record` kết quả + `RentalDashboardCriteria`) |
| Repository | thêm method vào `IRentalOrderRepository` + `RentalOrderRepository` |
| Controller | `RentalService/src/Api/Controllers/SupplierDashboardController.cs` — `[Route("supplier/dashboard")]` (prefix `api/v1/rental` do `ApiRoutePrefixConvention` tự thêm) |
| DI | `Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs` đăng ký `ISupplierDashboardService` |
| Test | `RentalService/tests/Api.Tests/Dashboard/` (thư mục mới) + cập nhật `Controllers/SwaggerDocumentationTests.cs` |

### Các bước

**Bước 1 — Validator khoảng ngày** (`DashboardRangeValidator`)
- Input: `string? from, string? to, string? granularity, DateTimeOffset utcNow`.
- Parse `yyyy-MM-dd` bằng `DateOnly.TryParseExact(..., CultureInfo.InvariantCulture)`. Sai → `DASHBOARD_RANGE_INVALID`.
- Mặc định: `today = RentalInventoryDateAdapter.ToBusinessDate(utcNow)`, `to = today`, `from = today.AddDays(-29)`.
- `from > to` → `DASHBOARD_RANGE_INVALID`. `to.DayNumber − from.DayNumber + 1 > 366` → `DASHBOARD_RANGE_TOO_LARGE`.
- `granularity` ∉ {day, week, month} → `DASHBOARD_PARAM_INVALID` (mặc định `day`).
- Output record `DashboardRange(DateOnly From, DateOnly To, DateTimeOffset FromUtc, DateTimeOffset ToUtcExclusive, Granularity)`; mốc UTC tính bằng `RentalInventoryDateAdapter.GetStartOfDayUtc` (đã có, không viết hàm mới).
- Unit test: biên 366/367 ngày, `from > to`, sai định dạng, default 30 ngày, granularity sai.

**Bước 2 — Bucket filler** (`DashboardBucketFiller`)
- `IReadOnlyList<DateOnly> BuildBuckets(DashboardRange)`: day → mỗi ngày; week → thứ Hai đầu tuần (bucket đầu = `from`); month → ngày 1 (bucket đầu = `from`).
- `BucketOf(DateOnly day, ...)` map 1 ngày VN về `bucketStart`.
- `Fill(buckets, IReadOnlyList<(DateOnly Day, decimal Value)>)` → cộng theo bucket, bucket thiếu = 0.
- Repository chỉ trả số theo **ngày VN**; Application gộp sang tuần/tháng. Như vậy repository chỉ có 1 kiểu group (theo ngày).
- Unit test: tuần cắt ngang tháng, `from` rơi vào thứ Tư, tháng 2.

**Bước 3 — Repository method** (thêm vào `IRentalOrderRepository`, tất cả nhận `Guid? supplierId`; `null` = toàn nền tảng, dành cho D2)

| Method | Truy vấn |
|---|---|
| `CountItemsByStatusAsync(Guid? supplierId, ct)` → `IReadOnlyList<StatusCountRecord>` | `RentalItems.AsNoTracking().Where(supplier).GroupBy(i => i.Status).Select(g => new(g.Key, g.Count()))` |
| `GetItemActionCountsAsync(RentalItemActionCriteria c, ct)` → `RentalItemActionCountsRecord` | Một query, nhiều `Count(predicate)` có điều kiện (xem bảng dưới). Criteria chứa `SupplierId?`, `TodayVn`, `ConfirmationDueBeforeUtc`. |
| `CountOpenDisputesAsync(Guid? supplierId, ct)` | `Disputes.Where(d => d.Status == Open)` join `DeductionProposals` qua `DeductionProposalId` → `RentalItems` qua `RentalItemId`, lọc supplier. |
| `SumDepositHeldAsync(Guid? supplierId, IReadOnlyCollection<RentalItemStatus> statuses, ct)` | `SumAsync(i => i.DepositAmount)` với `statuses` do Application truyền. |
| `GetPaidItemDailyTotalsAsync(PaidItemCriteria c, ct)` → `IReadOnlyList<DailyPaidItemsRecord(Date, ItemCount, RentalAmount)>` | Lọc `i.RentalOrder.PaymentConfirmedAt` ∈ `[FromUtc, ToUtcExclusive)` và `i.Status ∉ c.ExcludedStatuses`; group theo ngày VN. |
| `CountCompletedItemsAsync(Guid? supplierId, fromUtc, toUtc, ct)` | Đếm `RentalStateTransitions` có `RentalItemId != null`, `ToState == "Completed"`, `OccurredAt` trong khoảng, join item lọc supplier. Đếm **distinct item**. |

Điều kiện trong `GetItemActionCountsAsync` (Application truyền `TodayVn`, `ConfirmationDueBeforeUtc = utcNow + 6h`):
- `pendingConfirmation`: `Status == PendingSupplierConfirmation`.
- `confirmationDueSoon`: thêm `SupplierConfirmationDeadline <= ConfirmationDueBeforeUtc`.
- `awaitingHandoverToday`: `Status == AwaitingHandover && ExpectedStartDate == TodayVn`.
- `handoverOverdue`: `Status == AwaitingHandover && ExpectedStartDate < TodayVn` — **cùng điều kiện gốc** với `GetHandoverOverdueOrderIdsAsync` (dòng ~590 `RentalOrderRepository.cs`).
- `returnOverdue`: `Status == InRental && ExpectedEndDate < TodayVn` — cùng điều kiện gốc với `GetOverdueUnprotectedOrderIdsAsync`.
- `pendingSettlement`: `Status == PendingSettlement`.

> **Single Source of Truth:** tách 2 điều kiện overdue thành `private static Expression<Func<RentalItem, bool>>` trong `RentalOrderRepository` và cho **cả** worker query cũ lẫn query dashboard dùng chung. Chạy lại test của worker overdue sau khi tách.

> **Group theo ngày VN trong LINQ:** thử `GroupBy(i => i.RentalOrder.PaymentConfirmedAt!.Value.AddHours(7).Date)` (VN không có DST nên +7 cố định), xác nhận Npgsql dịch được bằng integration test. Nếu không dịch được → đây là trường hợp "hạn chế dịch LINQ đã kiểm chứng" → được dùng Native SQL `date_trunc('day', "PaymentConfirmedAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')`, ghi lý do trong PR.

**Bước 4 — Integration test repository** (Postgres thật, theo mẫu `Domain/RentalPostgreSqlRealWorkflowsTests.cs`)
- Seed: supplier A có máy lu Sakai `InRental` (kết thúc hôm qua), máy đầm cóc Mikasa `PendingSupplierConfirmation` (deadline +3h); supplier B có máy trộn bê tông `AwaitingHandover` bắt đầu hôm nay.
- Kiểm: đếm theo status, action counts cho A/B/null, dispute join đúng, daily totals đúng biên 23:30 / 00:30 VN, `Rejected/Cancelled/Expired` không cộng.

**Bước 5 — Application service** (`SupplierDashboardService`)
- `GetSummaryAsync(DashboardRangeRequest, IRentalCallerContext, ct)`:
  1. `RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller)` → lấy `supplierId` (401 `AUTH_REQUIRED` nếu là service token hoặc chưa đăng nhập).
  2. `utcNow = _timeProvider.GetUtcNow()`; validate range.
  3. Gọi tuần tự các repository method (cùng `DbContext` nên **không** `Task.WhenAll`).
  4. Tập trạng thái do service khai báo dạng `static readonly`: `GmvExcludedStatuses = {Rejected, Expired, Cancelled}`, `DepositHeldStatuses = {AwaitingHandover, InRental, PendingSettlement}`.
  5. `confirmation`: gọi lại `_rentalOrderRepository.GetSupplierConfirmationStatisticsAsync(supplierId, FromUtc, ToUtcExclusive)` (Native SQL sẵn có, không viết lại). `acceptRate = accepted / (accepted + rejected + timeout)`, mẫu 0 → `null`, làm tròn 4 chữ số.
  6. Ráp `SupplierDashboardSummaryResponse` gồm `range`, `generatedAt = utcNow`, `currency = "VND"`, `itemsByStatus` (đủ 9 status, thiếu = 0), `actionRequired`, `period { paidItems, gmvRental, completedItems }`, `confirmation`, `depositHeld`.
- `GetTimeseriesAsync(request, metric, caller, ct)`: `metric` ∈ {`paidItems`, `gmv`} (sai → `DASHBOARD_PARAM_INVALID`); gọi `GetPaidItemDailyTotalsAsync` rồi `DashboardBucketFiller.Fill`.
- Unit test (fake repository, `FakeTimeProvider`): service token → 401; user mới → toàn 0, `acceptRate = null`; set status truyền xuống đúng; mốc `ConfirmationDueBeforeUtc = now + 6h`.

**Bước 6 — Controller** (`SupplierDashboardController`)
- `[ApiController] [Authorize] [Route("supplier/dashboard")]`, inject `ISupplierDashboardService` + `IRentalCallerContext` (giống `AdminSupplierStatisticsController`).
- `[HttpGet("summary")]` nhận `[FromQuery] from, to`; `[HttpGet("timeseries")]` nhận thêm `granularity, metric`.
- Lỗi → `this.ToProblem(result.Error)`. Có `ProducesResponseType` 200/400/401 và XML doc tiếng Việt (Swagger).
- Quyết định với owner Rental: có để `[ApiExplorerSettings(IgnoreApi = true)]` như controller statistics không. Mặc định: **hiện** trên Swagger cho FE dùng.

**Bước 7 — API test** (theo mẫu `Controllers/RentalItemsControllerIntegrationTests.cs`)
- Supplier A gọi → chỉ thấy số của A. Service token → 401. Không token → 401.
- `from=2026-10-10&to=2026-10-01` → 400 `DASHBOARD_RANGE_INVALID`; khoảng 367 ngày → 400 `DASHBOARD_RANGE_TOO_LARGE`; `metric=abc` → 400.
- Response không chứa `renterId`.
- Cập nhật số operation trong `SwaggerDocumentationTests`.

**Bước 8 — Index (chỉ khi cần)**
- Chạy `EXPLAIN ANALYZE` cho query action counts và daily totals với seed vài nghìn dòng. Nếu Seq Scan tốn → thêm migration `AddRentalDashboardIndexes` (ví dụ `rental_items(SupplierId, Status)`). Không cần thì không thêm.

**Bước 9 — Kiểm tra**
```bash
dotnet build RentalService
```
```bash
dotnet test RentalService
```

---

## D2 — `[Rental] Admin dashboard API`

**Nhánh:** `rental/feat/DPR-xxx-admin-dashboard` từ `origin/rental/develop` **sau khi D1 merge** (hoặc rebase lên nhánh D1).

### File sẽ tạo / sửa
| Loại | File |
|---|---|
| Policy | `Application/Security/RentalAuthorizationPolicy.cs` — thêm `AuthorizeAdminDashboardRead` |
| DTO | `Application/DTOs/Dashboard/AdminDashboardSummaryResponse.cs`, `TopSupplierResponse.cs` |
| Service | `Application/IServices/Dashboard/IAdminDashboardService.cs`, `Application/Services/Dashboard/AdminDashboardService.cs` |
| Repository | thêm 4 method vào `IRentalOrderRepository`; **sửa** `GetSupplierConfirmationStatisticsAsync` nhận `Guid?` |
| Controller | `Api/Controllers/AdminDashboardController.cs` — `[Route("admin/dashboard")]` |
| Test | `tests/Api.Tests/Dashboard/AdminDashboard*Tests.cs` |

### Các bước

**Bước 1 — Policy**
```csharp
public static Result AuthorizeAdminDashboardRead(IRentalCallerContext caller) =>
    RequireAdmin(
        caller,
        "Service token không được truy cập dashboard quản trị.",
        Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được xem dashboard."));
```
Unit test trong `tests/Api.Tests/Policies/`: admin OK, user thường 403, service token 403, chưa đăng nhập 401.

**Bước 2 — Dùng lại repository của D1** với `supplierId = null`: `CountItemsByStatusAsync`, `GetItemActionCountsAsync`, `CountOpenDisputesAsync`, `GetPaidItemDailyTotalsAsync`, `CountCompletedItemsAsync`. **Không** viết bản admin riêng.

**Bước 3 — Repository method mới**
| Method | Truy vấn |
|---|---|
| `CountOrdersByStatusAsync(ct)` | `RentalOrders.GroupBy(o => o.Status)` |
| `GetOrderDailyCountsAsync(OrderDailyCriteria c, ct)` → `(Date, Created)` hoặc `(Date, Paid)` | `c.Basis = CreatedAt | PaymentConfirmedAt`; group theo ngày VN (cùng cách D1 bước 3). |
| `CountOrdersPaymentExpiredAsync(fromUtc, toUtc, ct)` | Đơn có `CreatedAt` trong khoảng **và** tồn tại `RentalStateTransition` với `RentalItemId == null`, `FromState == "PendingPayment"`, `ToState == "Expired"`, `Action == "ExpirePayment"` (đúng chuỗi do `RentalOrder.MarkPaymentExpired` ghi). |
| `CountDistinctPaidSuppliersAsync(fromUtc, toUtc, excludedStatuses, ct)` | `Select(i => i.SupplierId).Distinct().CountAsync()` trên các dòng đã thanh toán trong khoảng. |
| `GetTopSuppliersAsync(TopSupplierCriteria c, ct)` | Group theo `SupplierId` → `PaidItems`, `GmvRental`; `OrderByDescending(c.By)` rồi `ThenBy(SupplierId)`; `Take(c.Top)`. Tất cả trên DB. |

**Bước 4 — Mở rộng Native SQL sẵn có cho `supplierConfirmationTimeouts`**
- Đổi `GetSupplierConfirmationStatisticsAsync(Guid supplierId, …)` → `Guid? supplierId`; trong SQL thay `i."SupplierId" = {supplierId}` bằng `({supplierId}::uuid IS NULL OR i."SupplierId" = {supplierId})` ở cả 4 CTE.
- Lý do: tránh viết query thứ hai cho cùng định nghĩa timeout. Vẫn parameterize.
- Cập nhật caller cũ (`SupplierStatisticsService`, D1), **thêm integration test** cho nhánh `null` (2 supplier, mỗi bên 1 timeout → tổng 2).

**Bước 5 — Service** (`AdminDashboardService`)
- `GetSummaryAsync`: `AuthorizeAdminDashboardRead` → validate range (dùng `DashboardRangeValidator` của D1) → gọi repository → tính:
  - `paymentExpiredRate = paymentExpired / ordersCreated` (mẫu 0 → `null`, 4 chữ số).
  - `ordersByStatus` đủ 9 status, `itemsByStatus` đủ 9 status.
  - `attention { handoverOverdue, returnOverdue, pendingSettlement, openDisputes, supplierConfirmationTimeouts }`.
- `GetTimeseriesAsync`: `metric ∈ {orders, paidOrders, gmv}`.
- `GetTopSuppliersAsync(by, top)`: `by ∈ {gmv, items}` (mặc định `gmv`), `top` 1..50 (mặc định 10); sai → `DASHBOARD_PARAM_INVALID`.
- Unit test: rate tính đúng 1/3 = 0.3333; mẫu 0 → null; `top=0/51` → 400.

**Bước 6 — Controller** `AdminDashboardController` — 3 action `summary`, `timeseries`, `top-suppliers`.

**Bước 7 — API test**
- Non-admin 403 `Auth.Forbidden`; service token 403 `Auth.ServiceTokenNotAllowed`; admin 200.
- 3 đơn tạo trong khoảng, 1 đơn hết hạn thanh toán → `0.3333`. Đơn `Expired` do supplier timeout (không có transition `ExpirePayment`) **không** tính.
- Tổng `gmvRental` admin = tổng `gmvRental` của từng supplier.
- 2 supplier GMV bằng nhau → thứ tự theo `supplierId`.
- Swagger test cập nhật.

**Bước 8 — Kiểm tra:** `dotnet build` + `dotnet test` RentalService.

---

## D3 — `[Inventory] Supplier fleet dashboard API`

**Nhánh:** `inventory/feat/DPR-xxx-supplier-dashboard` từ `origin/inventory/develop`.
**Lưu ý cấu trúc Inventory:** `IServices/` và `Services/` là thư mục phẳng; validator nằm ở `Application/Validation/`; criteria/record của repository nằm ở `Domain/IRepositories/`.

### File sẽ tạo / sửa
| Loại | File |
|---|---|
| DTO | `InventoryService/src/Application/DTOs/Dashboard/FleetDashboardSummaryResponse.cs`, `FleetUtilizationResponse.cs`, `DashboardRange.cs` |
| Validator | `InventoryService/src/Application/Validation/DashboardRangeValidator.cs` (cạnh `AvailabilityDateValidator.cs`) |
| Bucket | `InventoryService/src/Application/Services/DashboardBucketFiller.cs` |
| Service | `InventoryService/src/Application/IServices/IFleetDashboardService.cs`, `InventoryService/src/Application/Services/FleetDashboardService.cs` |
| Repository | `Domain/IRepositories/FleetDashboardCriteria.cs` (criteria + record); thêm method vào `IInventoryAssetRepository` / `InventoryAssetRepository` (tách file `InventoryAssetRepository.Dashboard.cs` như các file `.Search.cs` hiện có) |
| Controller | `InventoryService/src/Api/Controllers/SupplierFleetDashboardController.cs` — `[Route("supplier/dashboard")]` |
| DI | `Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs` |
| Test | `InventoryService/tests/Api.Tests/Dashboard/` |

### Các bước

**Bước 1 — Validator + bucket**: giống D1 bước 1–2, dùng `BusinessDateTime.ToVietnamDate` và `BusinessDateTime.StartOfDayUtc` (đã có ở `Common/Utils/BusinessDateTime.cs`).

**Bước 2 — Repository: summary snapshot** (`FleetDashboardCriteria(Guid? OwnerUserId, Guid? EquipmentTypeId, DateOnly TodayVn, DateTimeOffset UtcNow)`)

| Method | Truy vấn |
|---|---|
| `GetListingStatusCountsAsync(criteria, ct)` | `EquipmentListings.Where(owner).GroupBy(l => l.Status)` (Draft/Active/Inactive) |
| `GetFleetTodayCountsAsync(criteria, ct)` → `FleetTodayCountsRecord(Total, AcceptingBookings, Suspended, Occupied, BlockedNotOccupied)` | Một query trên `InventoryAssets` join `Listing` lọc owner, mỗi asset tính 2 cờ `isOccupied`, `isBlocked` bằng `Any(...)`, rồi `GroupBy(_ => 1).Select(Count(...))`. |

Định nghĩa cờ (bám theo `AssetAvailabilityPolicy.GetOccupiedInterval` và predicate đã có trong `AssetAllocationRepository.cs:84`):
- `isOccupied = a.Allocations.Any(al =>
     (al.Status == Held && al.HoldExpiresAt > UtcNow && al.RentalPeriod.StartDate <= TodayVn && al.RentalPeriod.EndDate >= TodayVn)
  || (al.Status == Allocated && al.RentalPeriod.StartDate <= TodayVn
      && (al.RentalPeriod.EndDate >= TodayVn || (al.HandedOverAt != null && al.ReturnedAt == null))))`
- `isBlocked = !a.AcceptingBookings || a.CalendarBlocks.Any(b => b.StartDate <= TodayVn && b.EndDate >= TodayVn)`
- Repository trả `Occupied = Count(isOccupied)`, `BlockedNotOccupied = Count(!isOccupied && isBlocked)`.
- **Application** tính `available = total − occupied − blockedNotOccupied` (quyết định thứ tự ưu tiên nằm ở Application, repository chỉ đếm theo điều kiện được yêu cầu).

> Nếu predicate `isOccupied` trùng với predicate trong `AssetAllocationRepository`/`EquipmentListingRepository`/`InventoryAssetRepository`, tách thành `internal static Expression<...>` dùng chung ở `Domain/Repositories` thay vì copy lần thứ 4.

**Bước 3 — Repository: utilization (period)**
- Cách mặc định (không Native SQL): repository trả **khoảng** đã lọc trên DB, Application tính asset-ngày.
  - `GetAssetLifetimesAsync(criteria, ct)` → `(AssetId, CreatedDateVn)` cho asset tạo trước `ToUtcExclusive`.
  - `GetOccupiedIntervalsAsync(criteria, from, to, ct)` → `(AssetId, Start, End, HandedOverNotReturned)` của allocation `Allocated` (và `Held` còn hạn) **giao** với `[from, to]`.
  - `GetBlockIntervalsAsync(criteria, from, to, ct)` → `(AssetId, Start, End)` giao với `[from, to]`.
  - Chỉ projection cột cần, `AsNoTracking`, lọc khoảng trên DB → số dòng ≈ số allocation/block trong kỳ, không tải toàn bảng.
- Application (`FleetDashboardService`) với mỗi ngày `d` trong `[from, to]`:
  - `available(d)` = asset đã tồn tại (`CreatedDateVn <= d`) **và** không bị block ngày `d`.
  - `allocated(d)` = asset có khoảng chiếm phủ `d`; khoảng `HandedOverNotReturned` kéo dài tới `min(to, todayVn)`.
  - Gộp theo bucket → `assetDaysAvailable`, `assetDaysAllocated`, `utilizationRate = allocated / available` (mẫu 0 → `null`).
  - Allocation đè ngày block thì vẫn tính allocated (occupied > blocked), và ngày đó được tính là available để tỉ lệ không vượt 1.
- Nếu đo thấy chậm (EXPLAIN + thời gian với dữ liệu lớn) mới chuyển sang Native SQL `generate_series`, kèm đủ điều kiện mục 5 AGENTS.md.

**Bước 4 — Integration test repository** (theo mẫu `Integration/AssetHttpIntegrationTests.*.cs`)
- 3 máy trộn bê tông của owner A: #1 có allocation phủ hôm nay, #2 block `Maintenance` hôm nay, #3 rảnh → occupied 1 / blocked 1 / available 1.
- Asset vừa block vừa allocation → chỉ occupied.
- Máy lu đã `HandedOverAt`, `EndDate` = hôm qua, `ReturnedAt = null` → occupied.
- Asset `AcceptingBookings = false` → blocked.
- Held hết hạn (`HoldExpiresAt <= now`) → không occupied.
- Asset của owner B không lọt vào A.

**Bước 5 — Service** (`FleetDashboardService`)
- `GetSupplierSummaryAsync(caller, ct)`:
  1. Kiểm tra caller theo đúng cách `EquipmentListingService.GetOwnerStatisticsAsync` đang làm: service token → 403 `SERVICE_TOKEN_REJECTED`; chưa đăng nhập → 401 `AUTH_REQUIRED`. Đưa đoạn kiểm tra này thành 1 hàm trong `Application/Security/InventoryAccessEvaluator.cs` (ví dụ `AuthorizeHumanOwner`) và cho cả `GetOwnerStatisticsAsync` dùng lại, tránh 2 bản.
  2. `owner = caller.UserId`, `todayVn = BusinessDateTime.ToVietnamDate(_timeProvider.GetUtcNow())`.
  3. Gọi repository, tính `available`, assert `occupied + blocked + available == total`.
- `GetSupplierUtilizationAsync(rangeRequest, caller, ct)`.
- Unit test: invariant tổng; utilization 2 asset × 10 ngày, 1 asset thuê 5 ngày → `0.25`; asset tạo giữa kỳ chỉ tính từ ngày tạo.

**Bước 6 — Controller** `SupplierFleetDashboardController`: `[Authorize] [Route("supplier/dashboard")]`, `GET summary`, `GET utilization?from&to&granularity`. Response không có `SerialNumber`, `AssetCode`, `SuspensionReason`.

**Bước 7 — API test**: A không thấy B; service token 403; không token 401; range sai 400; kiểm Swagger nếu Inventory có test đếm operation.

**Bước 8 — Kiểm tra:** `dotnet build` + `dotnet test` InventoryService.

---

## D4 — `[Inventory] Admin fleet dashboard API`

**Nhánh:** `inventory/feat/DPR-xxx-admin-dashboard` sau khi D3 merge.

### File sẽ tạo / sửa
| Loại | File |
|---|---|
| DTO | `Application/DTOs/Dashboard/AdminFleetDashboardSummaryResponse.cs` (thêm `byEquipmentType`, `activeSuppliers`) |
| Service | thêm method admin vào `IFleetDashboardService` / `FleetDashboardService` |
| Repository | thêm `GetFleetCountsByEquipmentTypeAsync`, `CountActiveSuppliersAsync` |
| Controller | `Api/Controllers/AdminFleetDashboardController.cs` — `[Route("admin/dashboard")]` |

### Các bước
1. **Phân quyền**: `caller.IsHuman && caller.IsAdmin`; service token → 403 `SERVICE_TOKEN_REJECTED`; user thường → 403 (dùng đúng mã forbidden Inventory đang trả ở các API admin khác).
2. **Dùng lại** `GetListingStatusCountsAsync`, `GetFleetTodayCountsAsync`, các method utilization của D3 với `OwnerUserId = null`.
3. **Method mới**:
   - `GetFleetCountsByEquipmentTypeAsync(criteria, ct)` → `[{EquipmentTypeId, ListingsActive, AssetsTotal, OccupiedToday}]`: group theo `Listing.EquipmentTypeId`, dùng **cùng** predicate `isOccupied` của D3.
   - `CountActiveSuppliersAsync(ct)`: `EquipmentListings.Where(Status == Active).Select(OwnerUserId).Distinct().CountAsync()`.
4. **Utilization admin** nhận thêm `equipmentTypeId` (Guid hợp lệ hoặc rỗng; sai định dạng → `DASHBOARD_PARAM_INVALID`), truyền qua `FleetDashboardCriteria.EquipmentTypeId`.
5. **Test**:
   - Non-admin 403, service token 403, admin 200.
   - 2 supplier, 2 loại (máy đầm cóc, máy lu) → `byEquipmentType` tách đúng; tổng các dòng = khối tổng.
   - Lọc `equipmentTypeId` chỉ ra số của loại đó.
   - Cùng các invariant block/overdue như D3.
6. **Kiểm tra:** `dotnet build` + `dotnet test` InventoryService.

---

## D5 — `[Auth] Admin user & organization dashboard API`

**Nhánh:** `auth/feat/DPR-xxx-admin-dashboard` từ `origin/auth/develop`.
**Lưu ý cấu trúc Auth:** phân quyền admin dùng **policy theo permission** ở controller (`[Authorize(Policy = Permissions.Users.Read)]` như `AdminUsersController`, `Permissions.Organizations.Read` như `AdminOrganizationsController`). Giữ đúng cách này, không chế thêm kiểm tra role riêng.

### File sẽ tạo / sửa
| Loại | File |
|---|---|
| DTO | `AuthService/src/Application/DTOs/Dashboard/AdminIdentityDashboardResponse.cs`, `DashboardTimeseriesResponse.cs` |
| Validator + bucket | `AuthService/src/Application/Services/Dashboard/` (cùng spec D0) |
| Service | `AuthService/src/Application/IServices/IIdentityDashboardService.cs`, `AuthService/src/Application/Services/IdentityDashboardService.cs` |
| Repository | thêm method vào `IUserRepository`, `IOrganizationRepository` và implementation EF |
| Controller | `AuthService/src/Api/Controllers/AdminDashboardController.cs` — `[Route("admin/dashboard")]` |
| Test | `EfUserRepositoryTests.cs`, `EfOrganizationRepositoryTests.cs`, `AuthorizationMatrixTests.cs`, file mới `AdminDashboardEndpointsIntegrationTests.cs` |

### Các bước
1. **Controller**: 2 attribute `[Authorize(Policy = Permissions.Users.Read)]` + `[Authorize(Policy = Permissions.Organizations.Read)]` (ASP.NET AND cả hai). Thêm 2 route mới vào `AuthorizationMatrixTests` (admin 200, customer 403, service 403, anonymous 401).
2. **Repository `IUserRepository`**:
   - `GetUserStatusCountsAsync(DateTimeOffset utcNow, ct)` → `(Total, Active, Inactive, LockedOut, ProfileCompleted)`: một query `GroupBy(_ => 1)` với `Count(u => u.IsActive)`, `Count(u => u.LockoutEnd > utcNow)`, `Count(u => u.ProfileCompletedAtUtc != null)`.
   - `CountUsersByRoleAsync(ct)` → `[{RoleCode, Count}]`: từ `UserRoles` join `Role`, group theo `Role.Code`, đếm **distinct user**.
   - `GetUserDailyCreatedAsync(fromUtc, toUtcExclusive, ct)` → `[(DateVn, Count)]` theo `CreatedAt` (kiểu `DateTime` UTC — chuyển mốc sang `DateTime` UTC trước khi so sánh).
   - Projection **chỉ** cột đếm; không select Email/Phone/CitizenId.
3. **Repository `IOrganizationRepository`**:
   - `CountOrganizationsByStatusAndTypeAsync(ct)` → `[{Status, Type, Count}]`.
   - `GetOrganizationDailyCreatedAsync(fromUtc, toUtcExclusive, ct)` theo `CreatedAtUtc`.
4. **Service**:
   - `utcNow` từ `TimeProvider` (kiểm tra Auth đã đăng ký `TimeProvider` chưa; nếu chưa thì đăng ký `TimeProvider.System` ở DI).
   - `actionRequired.organizationsPendingApproval` = số org `Status == Pending` (Application chọn trạng thái, repository chỉ trả bảng đếm).
   - `period.newUsers`, `period.newOrganizations` = tổng daily trong khoảng.
   - `organizations` trả đủ 4 status × 3 type, thiếu = 0.
   - Timeseries `metric ∈ {newUsers, newOrganizations}`.
5. **Test**:
   - User có `LockoutEnd` tương lai → `lockedOut`; `LockoutEnd` quá khứ → không.
   - Org `Pending` → `organizationsPendingApproval = 1`.
   - User tạo 2026-10-10 23:30 VN (16:30 UTC) → bucket `2026-10-10`.
   - Response JSON không chứa chuỗi email seed.
6. **Kiểm tra:** `dotnet build` + `dotnet test` AuthService.

---

## D6 — `[Payment] Admin payment dashboard API`

**Nhánh:** `payment/feat/DPR-xxx-admin-dashboard` từ `origin/payment/develop`.
**Lưu ý cấu trúc Payment:** policy khai báo trong `Api/Program.cs` (`InvoiceServicePolicy`, `InvoiceRenterPolicy`); claim helper ở `Api/Security/PaymentClaimsPrincipalExtensions.cs`; controller dùng route đầy đủ (`api/v1/invoices`, `api/v1/payment/webhooks`), không có prefix convention. Repository nằm ở `Domain/IRepository/` + `Domain/Repository/`.

### File sẽ tạo / sửa
| Loại | File |
|---|---|
| Claim helper | `Api/Security/PaymentClaimsPrincipalExtensions.cs` — thêm `IsPlatformAdmin()` |
| Policy | `Api/Program.cs` — thêm `PaymentAdminPolicy` |
| DTO | `Application/DTOs/Dashboard/PaymentDashboardSummaryResponse.cs`, `DashboardTimeseriesResponse.cs` (theo thư mục DTO hiện có của Payment) |
| Validator + bucket | `Application/Services/Dashboard/` |
| Service | `Application/IServices/IPaymentDashboardService.cs`, `Application/Services/PaymentDashboardService.cs` |
| Repository | thêm method vào `IInvoiceRepository` + `EfInvoiceRepository` |
| Controller | `Api/Controllers/AdminPaymentDashboardController.cs` — `[Route("api/v1/payment/admin/dashboard")]` |
| Test | `tests/Api.Tests/Integration/AdminPaymentDashboardTests.cs`, `tests/Api.Tests/Unit/PaymentDashboardServiceTests.cs` |

### Các bước
1. **Claim helper `IsPlatformAdmin()`**: không phải service token (`u != "svc"`), có claim role là `admin` hoặc alias `adm` — dùng đúng tên claim role mà Rental đọc (`RentalAuthClaims`). Unit test với token admin / customer / service.
2. **Policy `PaymentAdminPolicy`** trong `Program.cs`: `RequireAuthenticatedUser()` + `HasClientAudience(...)` + `IsPlatformAdmin()`.
3. **Repository** (`IInvoiceRepository`):
   - `CountInvoicesByStatusAsync(ct)` → `[{Status, Count}]`.
   - `GetInvoiceDailyCreatedAsync(fromUtc, toUtcExclusive, ct)` theo `CreatedAt`.
   - `GetPaidInvoiceDailyTotalsAsync(fromUtc, toUtcExclusive, paidStatus, ct)` → `[(DateVn, Count, TotalAmount, TotalRentalAmount, TotalDepositAmount)]` theo `PaidAt`, `Status == paidStatus` (Application truyền `InvoiceStatus.Paid`).
   - `CountTransactionsByStatusAsync(IReadOnlyCollection<string> statuses, ct)` → `[{Status, Count}]`.
   - Không select `MerchantAccount`, `AccountNumber`, `Content`, `Description`, `RenterId`.
4. **Service**:
   - `invoicesByStatus` đủ PENDING/PAID/EXPIRED/CANCELLED.
   - `period { invoicesCreated, invoicesPaid, collectedAmount, collectedRental, collectedDeposit }`.
   - `actionRequired { reviewRequired, manualRefundRequired, unmatched }` với tập `{REVIEW_REQUIRED, MANUAL_REFUND_REQUIRED, UNMATCHED}` khai báo ở Application.
   - Timeseries `metric ∈ {collected, invoicesPaid}`.
5. **Controller** `[Authorize(Policy = "PaymentAdminPolicy")]`, 2 action `summary`, `timeseries`.
6. **Test** (Postgres thật, theo `TestHelpers/PostgresTestDatabaseHelper.cs`):
   - Non-admin 403, service token 403, không token 401, admin 200.
   - Invoice `EXPIRED` không cộng vào `collectedAmount`.
   - Invoice `PaidAt` 23:30 VN → đúng bucket ngày VN.
   - 2 giao dịch `REVIEW_REQUIRED`, 1 `UNMATCHED` → đếm đúng.
7. **Kiểm tra:** `dotnet build` + `dotnet test` PaymentService.

> Ngoài phạm vi: refund/payout (Payment chưa có bảng), route Gateway (D7).

---

## D7 — `[Gateway] Expose payment admin dashboard route`

**Nhánh:** `gateway/feat/DPR-xxx-payment-dashboard-route` từ `origin/gateway/develop`; phần compose: `platform/feat/DPR-xxx-payment-compose` từ `origin/platform/develop`.

### Hiện trạng
- `Gateway/ocelot/appsettings.json` có catch-all cho auth, media, inventory, catalog, rental; **chưa có route nào tới Payment**.
- `compose.yaml` của platform **chưa có** `payment-service` (chỉ có ở `PaymentService/docker-compose.yml`).

### Các bước
1. **Gateway route** — thêm vào `Routes` của `Gateway/ocelot/appsettings.json` (đặt cạnh route rental):
   ```json
   {
     "Key": "payment-admin-dashboard",
     "UpstreamPathTemplate": "/api/v1/payment/admin/dashboard/{everything}",
     "UpstreamHttpMethod": [ "GET" ],
     "DownstreamPathTemplate": "/api/v1/payment/admin/dashboard/{everything}",
     "DownstreamScheme": "http",
     "DownstreamHostAndPorts": [ { "Host": "payment-service", "Port": 8080 } ],
     "AuthenticationOptions": { "AuthenticationProviderKeys": [ "Bearer" ] }
   }
   ```
   - Port lấy theo `PaymentService/Dockerfile` (`EXPOSE`/`ASPNETCORE_HTTP_PORTS`), xác nhận trước khi merge.
   - Chỉ `GET`. **Không** mở `/api/v1/payment/{everything}`, không mở webhook, không mở `/api/v1/invoices`.
   - Nếu `GatewayConfigurationHealthCheck` hoặc `appsettings.Production.json` liệt kê host downstream thì thêm `payment-service` tương ứng.
2. **Gateway test** (`Gateway/tests/ApiGateway.Tests/GatewayIntegrationTests.cs`, dùng `DownstreamGatewayTestHost`):
   - Token admin → request tới downstream đúng path, trả 200.
   - Không token → 401 tại Gateway.
   - `GET /api/v1/payment/webhooks/...` và `GET /api/v1/invoices/...` → 404 (không bị lộ).
   - `POST` vào route dashboard → 404/405.
3. **Platform compose** — thêm `payment-postgres`, `payment-migrate`, `payment-service` vào `compose.yaml`, copy cấu hình từ `PaymentService/docker-compose.yml`, nối network `dprental`, `depends_on` jwt-keys/kafka như rental-service. Thêm biến vào `.env.example`.
4. **Kiểm tra**:
```bash
dotnet build Gateway
```
```bash
dotnet test Gateway
```
```bash
docker compose config
```
   Sau đó chạy compose local, gọi `GET /api/v1/payment/admin/dashboard/summary` qua Gateway bằng token admin.

---

## Checklist Done chung (dán vào mỗi subtask)

- [ ] Đúng route, DTO, mã lỗi trong `docs/contracts/dashboard-v1.md`.
- [ ] Application quyết định quyền, mốc giờ, tập trạng thái, công thức; repository chỉ lọc/đếm.
- [ ] Không có query song song trùng định nghĩa (supplier/admin dùng chung method).
- [ ] LINQ server-side, `AsNoTracking`, projection, `CancellationToken`; Native SQL (nếu có) đủ 4 điều kiện.
- [ ] Không lộ PII, Serial, AssetCode, RenterId.
- [ ] Test: phân quyền, IDOR, biên ngày VN 23:30/00:30, range 400, bucket rỗng = 0.
- [ ] `dotnet build` + `dotnet test` của service pass 100%; Swagger cập nhật.
- [ ] PR ghi rõ lệnh đã chạy và phần chưa kiểm chứng.
