# Hợp đồng API & Đặc tả Mock Dữ liệu Dashboard v1 (Admin, Nhà cung cấp & Realtime SignalR)

| Thuộc tính | Giá trị |
|---|---|
| **Mã tài liệu** | `SPEC-DASHBOARD-V1-MOCK` |
| **Mục đích** | Đặc tả chi tiết URL, Header, Query Parameters, Schema JSON, kết nối Realtime SignalR Hub và quy tắc hiển thị cho Frontend mock và tích hợp |
| **Dịch vụ cung cấp** | `AnalyticsService` (Dashboard Analytics REST & SignalR Hub) & `InventoryService` (Thống kê Listing NCC) |
| **Đối tượng sử dụng** | Frontend Team (Web Admin / Supplier Portal), QA |

---

## 1. Danh mục Endpoint REST & SignalR Hub

| STT | Endpoint | Method / Protocol | Service | Quyền (JWT) | Chức năng trên UI |
|:---:|---|:---:|---|---|---|
| **1** | `/api/v1/analytics/overview` | `GET` (REST) | AnalyticsService | Admin (`adm`, `adp`, `admin`, `admin_platform`) | 4 thẻ KPI Admin tổng quan toàn sàn |
| **2** | `/api/v1/analytics/revenue` | `GET` (REST) | AnalyticsService | Admin (`adm`, `adp`, `admin`, `admin_platform`) | Biểu đồ GMV và Phí sàn Admin |
| **3** | `/api/v1/analytics/me/overview` | `GET` (REST) | AnalyticsService | NCC (User token, trích xuất `sub`) | 4 thẻ KPI của Nhà cung cấp đăng nhập |
| **4** | `/api/v1/analytics/me/revenue` | `GET` (REST) | AnalyticsService | NCC (User token, trích xuất `sub`) | Biểu đồ tiền thuê & tiền đã nhận của NCC |
| **5** | `/api/v1/inventory/equipment-listings/statistics` | `GET` (REST) | InventoryService | NCC (User token) | Thẻ tổng số listing hiện có của NCC |
| **6** | `/hubs/analytics/negotiate` | `POST` (HTTP) | AnalyticsService | Admin / NCC qua Cookie `accessToken` hoặc Bearer | Bắt tay negotiate SignalR qua Gateway |
| **7** | `/hubs/analytics` | `GET` (WebSocket) | AnalyticsService | Admin / NCC qua Cookie `accessToken` hoặc Bearer | Kênh WebSocket nhận event `AnalyticsDataChanged` |

---

## 2. Quy chuẩn Tham số Truy vấn & Hiển thị

1. **Bộ lọc thời gian**:
   - `from`: Ngày bắt đầu theo định dạng `yyyy-MM-dd` (ví dụ `2026-10-01`).
   - `to`: Ngày kết thúc theo định dạng `yyyy-MM-dd` (ví dụ `2026-10-05`), bao gồm cả hai đầu ngày. Khoảng cách giữa `from` và `to` tối đa 366 ngày.
   - **Quy tắc mặc định 30 ngày**: Chỉ áp dụng khi người dùng **thiếu cả `from` và `to`**; Backend tự động lấy khoảng đóng [Hôm nay - 29 ngày, Hôm nay] theo múi giờ Việt Nam (`Asia/Ho_Chi_Minh` — UTC+7). Nếu chỉ truyền một trong hai, API sẽ trả về lỗi `400 INVALID_DATE_RANGE`.
2. **Đơn vị nhóm biểu đồ (`granularity`)**:
   - Giá trị: `day` (mặc định), `week` (bắt đầu từ thứ Hai), `month` (ngày 1 đầu tháng).
3. **Quy tắc tiền tệ & hiển thị**:
   - Tiền tệ: Đơn vị cố định là `VND`, số nguyên (ví dụ: `43500000.0` là 43.500.000 VND).
   - **GMV và Tiền thuê không bao gồm tiền cọc**, không được gọi gộp là doanh thu nền tảng.
   - **Lượt thuê**: Tính theo dòng thiết bị (`RentalItem`), một dòng thuê dù có `Quantity = 10` vẫn tính là **1 lượt thuê**.
   - **Tần suất làm mới dữ liệu**:
     - Phản ứng thời gian thực qua SignalR Hub `AnalyticsDataChanged`.
     - Polling dự phòng mỗi **60 giây** khi tab dashboard đang mở (do SignalR không có bộ đệm lưu lại event khi mất kết nối).

---

## 3. Đặc tả Chi tiết Các Endpoint REST

### 3.1. Admin — 4 Thẻ Tổng quan Toàn sàn
- **Route**: `GET /api/v1/analytics/overview?from=2026-10-01&to=2026-10-05`
- **Header**: `Authorization: Bearer <ADMIN_JWT_TOKEN>`

#### Bảng ánh xạ 4 thẻ giao diện Admin:
| Thẻ trên giao diện | JSON Path trong Response | Ghi chú nghiệp vụ |
|---|---|---|
| **Tổng đơn tạo trong kỳ** | `orders.totalOrders` | Số lượng đơn hàng được tạo mới trong khoảng thời gian chọn |
| **Đơn đã thanh toán** | `orders.paidOrders` | Số đơn có hóa đơn thanh toán thành công (`InvoicePaidFinancial`) |
| **Tổng tiền thuê đã thanh toán (GMV)** | `financial.grossRentalValue` | Tổng tiền thuê thực khách trả (không bao gồm tiền cọc) |
| **Phí nền tảng đã ghi nhận** | `financial.platformRevenue` | Doanh thu thực của sàn từ các đơn đã quyết toán (`RentalItemFinancialSettled`) |

#### Sample Response (`200 OK`):
```json
{
  "from": "2026-10-01",
  "to": "2026-10-05",
  "currency": "VND",
  "orders": {
    "totalOrders": 25,
    "paidOrders": 20,
    "activeOrders": 6,
    "completedOrders": 15,
    "cancelledOrders": 2,
    "expiredOrders": 1,
    "rejectedItems": 2
  },
  "financial": {
    "grossRentalValue": 43500000.0,
    "completedRentalValue": 32000000.0,
    "depositCollected": 95000000.0,
    "platformRevenue": 4350000.0,
    "deductionAmount": 500000.0,
    "refundAmount": 1500000.0,
    "supplierPayable": 39650000.0,
    "payoutCompleted": 36000000.0,
    "pendingRefund": null
  },
  "rental": {
    "paidItems": 28,
    "rentedQuantity": 42,
    "completedRentals": 20
  }
}
```

---

### 3.2. Admin — Biểu đồ GMV và Phí Sàn
- **Route**: `GET /api/v1/analytics/revenue?from=2026-10-01&to=2026-10-05&granularity=day`
- **Header**: `Authorization: Bearer <ADMIN_JWT_TOKEN>`

#### Bảng ánh xạ dữ liệu biểu đồ Admin:
| Trục / Đường biểu đồ | JSON Path | Ý nghĩa |
|---|---|---|
| Trục hoành (Thời gian) | `buckets[].periodStart` | Mốc ngày bắt đầu của bucket (`yyyy-MM-dd`) |
| Đường 1: GMV | `buckets[].grossRentalValue` | Tiền thuê khách thanh toán phát sinh trong ngày |
| Đường 2: Phí nền tảng | `buckets[].platformRevenue` | Phí dịch vụ sàn ghi nhận từ quyết toán trong ngày |

> **Khớp số liệu tổng**: Tổng `buckets[].platformRevenue` = `totalPlatformRevenue` = `4350000.0`. Tổng `buckets[].grossRentalValue` = `totalGrossRentalValue` = `43500000.0`.

#### Sample Response (`200 OK`):
```json
{
  "from": "2026-10-01",
  "to": "2026-10-05",
  "granularity": "day",
  "currency": "VND",
  "totalPlatformRevenue": 4350000.0,
  "totalGrossRentalValue": 43500000.0,
  "totalRefundAmount": 1500000.0,
  "totalSupplierPayout": 36000000.0,
  "buckets": [
    {
      "periodStart": "2026-10-01",
      "platformRevenue": 1200000.0,
      "grossRentalValue": 12000000.0,
      "refundAmount": 0.0,
      "supplierPayout": 8500000.0
    },
    {
      "periodStart": "2026-10-02",
      "platformRevenue": 950000.0,
      "grossRentalValue": 9500000.0,
      "refundAmount": 500000.0,
      "supplierPayout": 11000000.0
    },
    {
      "periodStart": "2026-10-03",
      "platformRevenue": 0.0,
      "grossRentalValue": 0.0,
      "refundAmount": 0.0,
      "supplierPayout": 0.0
    },
    {
      "periodStart": "2026-10-04",
      "platformRevenue": 1400000.0,
      "grossRentalValue": 14000000.0,
      "refundAmount": 1000000.0,
      "supplierPayout": 9500000.0
    },
    {
      "periodStart": "2026-10-05",
      "platformRevenue": 800000.0,
      "grossRentalValue": 8000000.0,
      "refundAmount": 0.0,
      "supplierPayout": 7000000.0
    }
  ]
}
```

---

### 3.3. Nhà cung cấp — 4 Thẻ Tổng quan NCC
- **Route**: `GET /api/v1/analytics/me/overview?from=2026-10-01&to=2026-10-05`
- **Header**: `Authorization: Bearer <SUPPLIER_JWT_TOKEN>`

#### Bảng ánh xạ 4 thẻ giao diện Nhà cung cấp:
| Thẻ trên giao diện | Nguồn dữ liệu & JSON Path | Ghi chú nghiệp vụ |
|---|---|---|
| **Lượt thuê đã thanh toán** | `rental.paidItems` | Tổng số dòng thuê của NCC trong các đơn đã trả tiền |
| **Tổng tiền thuê đã thanh toán** | `financial.grossRentalValue` | Phân rã từ các item thuộc NCC (không lấy trọn đơn nếu là đơn đa NCC) |
| **Tiền đã nhận** | `financial.payoutCompleted` | Tiền thực tế đã chuyển khoản thành công vào tài khoản NCC (`SupplierPayoutCompleted`) |
| **Tổng listing hiện có** | *Gọi riêng API Inventory (mục 3.5)* | Số lượng listing hiện hành của NCC, độc lập với bộ lọc ngày |

#### Sample Response (`200 OK`):
```json
{
  "from": "2026-10-01",
  "to": "2026-10-05",
  "currency": "VND",
  "orders": {
    "totalOrders": 8,
    "paidOrders": 6,
    "activeOrders": 2,
    "completedOrders": 5,
    "cancelledOrders": 0,
    "expiredOrders": 0,
    "rejectedItems": 1
  },
  "financial": {
    "grossRentalValue": 14500000.0,
    "completedRentalValue": 11000000.0,
    "depositCollected": 35000000.0,
    "platformRevenue": 1450000.0,
    "deductionAmount": 200000.0,
    "refundAmount": 0.0,
    "supplierPayable": 13250000.0,
    "payoutCompleted": 12000000.0,
    "pendingRefund": null
  },
  "rental": {
    "paidItems": 8,
    "rentedQuantity": 14,
    "completedRentals": 6
  }
}
```

---

### 3.4. Nhà cung cấp — Biểu đồ Tiền thuê & Tiền đã nhận
- **Route**: `GET /api/v1/analytics/me/revenue?from=2026-10-01&to=2026-10-05&granularity=day`
- **Header**: `Authorization: Bearer <SUPPLIER_JWT_TOKEN>`

#### Bảng ánh xạ dữ liệu biểu đồ NCC:
| Trục / Đường biểu đồ | JSON Path | Ý nghĩa |
|---|---|---|
| Trục hoành (Thời gian) | `buckets[].periodStart` | Mốc ngày bắt đầu của bucket (`yyyy-MM-dd`) |
| Đường 1: Tiền thuê | `buckets[].grossRentalValue` | Tiền thuê khách thanh toán cho các item của NCC phát sinh trong ngày |
| Đường 2: Tiền đã nhận | `buckets[].supplierPayout` | Tiền thực tế đã giải ngân payout về tài khoản NCC trong ngày |

> **Khớp số liệu tổng**: Tổng `buckets[].grossRentalValue` = `totalGrossRentalValue` = `14500000.0`. Tổng `buckets[].supplierPayout` = `totalSupplierPayout` = `12000000.0`.

#### Sample Response (`200 OK`):
```json
{
  "from": "2026-10-01",
  "to": "2026-10-05",
  "granularity": "day",
  "currency": "VND",
  "totalPlatformRevenue": 1450000.0,
  "totalGrossRentalValue": 14500000.0,
  "totalRefundAmount": 0.0,
  "totalSupplierPayout": 12000000.0,
  "buckets": [
    {
      "periodStart": "2026-10-01",
      "platformRevenue": 300000.0,
      "grossRentalValue": 3000000.0,
      "refundAmount": 0.0,
      "supplierPayout": 0.0
    },
    {
      "periodStart": "2026-10-02",
      "platformRevenue": 450000.0,
      "grossRentalValue": 4500000.0,
      "refundAmount": 0.0,
      "supplierPayout": 5000000.0
    },
    {
      "periodStart": "2026-10-03",
      "platformRevenue": 0.0,
      "grossRentalValue": 0.0,
      "refundAmount": 0.0,
      "supplierPayout": 0.0
    },
    {
      "periodStart": "2026-10-04",
      "platformRevenue": 500000.0,
      "grossRentalValue": 5000000.0,
      "refundAmount": 0.0,
      "supplierPayout": 4000000.0
    },
    {
      "periodStart": "2026-10-05",
      "platformRevenue": 200000.0,
      "grossRentalValue": 2000000.0,
      "refundAmount": 0.0,
      "supplierPayout": 3000000.0
    }
  ]
}
```

---

### 3.5. Nhà cung cấp — Thẻ Tổng Số Listing Hiện Có (InventoryService)
- **Route**: `GET /api/v1/inventory/equipment-listings/statistics`
- **Header**: `Authorization: Bearer <SUPPLIER_JWT_TOKEN>`

#### Cấu trúc Dictionary thực tế & Công thức tính:
API Inventory trả về định dạng Dictionary `{ byEquipmentType: { [typeId]: count }, byBrand: { [brandId]: count } }`.

$$ \text{TotalListings} = \sum \text{Object.values(response.byEquipmentType)} $$

```javascript
// Mã JavaScript tính tổng listing phía Frontend
const totalListings = Object.values(response.byEquipmentType || {})
  .reduce((sum, count) => sum + count, 0);
```

> **Cảnh báo chống đếm đôi**:
> - Chỉ tính tổng giá trị các key trong dictionary `byEquipmentType`.
> - **Tuyệt đối không cộng thêm `byBrand`**, vì các listing được nhóm song song theo cả loại và hãng; cộng cả hai sẽ bị đếm lặp (x2).
> - API không trả về tên thiết bị hay trạng thái tồn kho chi tiết tại endpoint này.

#### Sample Response (`200 OK`):
```json
{
  "byEquipmentType": {
    "b1000000-0000-0000-0000-000000000001": 8,
    "b1000000-0000-0000-0000-000000000002": 4
  },
  "byBrand": {
    "c1000000-0000-0000-0000-000000000001": 8,
    "c1000000-0000-0000-0000-000000000002": 4
  }
}
```
*(Trong ví dụ này: Tổng listing = 8 + 4 = **12**).*

---

## 4. Đặc tả SignalR Hub & Realtime Streaming

### 4.1. Thông số & Địa chỉ Kết nối (Dev vs Local)

- **Môi trường Dev (có Caddy proxy cùng origin)**:
  - Frontend domain: `https://dev-app.lazyops.cloud`
  - URL Hub cùng origin: `/hubs/analytics` (tự động chuyển qua Caddy $\rightarrow$ Gateway)
  - URL Negotiate: `/hubs/analytics/negotiate`
- **Môi trường Local (chưa có Caddy)**:
  - Frontend domain: `http://localhost:3000`
  - Gateway URL: `http://localhost:5050`
  - URL Hub trực tiếp qua Gateway: `http://localhost:5050/hubs/analytics`
  - `withCredentials: true` gửi kèm cookie `accessToken` qua CORS tới Gateway (Gateway đã cấu hình CORS cho `http://localhost:3000`).
  - **Lưu ý**: Trên local không dùng đường dẫn tương đối `/hubs/analytics` trên Next.js trừ khi có cấu hình rewrite trong `next.config.js`.

### 4.2. Khởi tạo SignalR Client (Mẫu chuẩn theo PaymentService)
```typescript
import * as signalR from '@microsoft/signalr';

// Chọn hubUrl tùy theo môi trường
const hubUrl = process.env.NEXT_PUBLIC_SIGNALR_URL || '/hubs/analytics';

const connection = new signalR.HubConnectionBuilder()
  .withUrl(hubUrl, {
    skipNegotiation: false,
    transport: signalR.HttpTransportType.WebSockets,
    withCredentials: true // Tự động gửi cookie HttpOnly accessToken
  })
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]) // Reconnect tự động
  .configureLogging(signalR.LogLevel.Information)
  .build();
```

> **Nguyên tắc Auth của Hub**:
> - Ưu tiên Authorization Bearer Header nếu client gửi header trực tiếp.
> - Nếu không có header, Hub đọc token từ cookie `accessToken`.
> - Không đọc token từ query string `?access_token=...`.
> - Không yêu cầu JavaScript đọc token nhạy cảm (`accessTokenFactory`).
> - Kết nối tự động đóng khi JWT token hết hạn (`CloseOnAuthenticationExpiration = true`).

### 4.3. Envelope Thông báo & Event Name
- **Event Name**: `AnalyticsDataChanged`
- **Contract Envelope**: Sử dụng trường `data` (không dùng `payload`).

```json
{
  "eventId": "c96e288b-cab8-4d89-b225-31ef14c65873",
  "version": 1,
  "occurredAt": "2026-10-28T03:00:00Z",
  "data": {
    "scope": "Finance"
  }
}
```

### 4.4. Quy tắc Tải Lại Dữ liệu (Reload-All Policy)

> **QUY TẮC BẮT BUỘC CHO FRONTEND**:
> - Khi nhận bất kỳ event `AnalyticsDataChanged` (dù `scope` là `Rental` hay `Finance`), Frontend **tải lại MỌI endpoint Analytics đang hiển thị trên màn hình hiện tại** và người dùng được phép gọi.
> - **Lý do**: Snapshot Rental đến muộn (ví dụ `RentalOrderCreated` đến sau `InvoicePaidFinancial`) sẽ làm thay đổi số liệu tiền thuê của `/api/v1/analytics/me/revenue`, dù event phát ra mang scope `Rental`. Không cố gắng tối ưu theo bảng dependencies bộ phận.
> - **Inventory độc lập**: Endpoint `/api/v1/inventory/equipment-listings/statistics` **KHÔNG** tải lại theo event SignalR của Analytics, mà chỉ tải khi mở trang và làm mới theo chu kỳ polling 60 giây.
> - **Không lọc theo occurredAt**: `occurredAt` trong envelope chỉ là thời điểm gửi thông báo realtime, **không phải ngày nghiệp vụ của dữ liệu**. Không được so sánh `occurredAt` với khoảng ngày `[from, to]` đang xem để bỏ qua event.

---

## 5. Trình tự Vận hành (Flow Lifecycle) Phía Frontend

```text
[Khởi động Dashboard]
        │
        ├───────────────────────────────────────────────────────┐
        ▼                                                       ▼
1. Đăng ký Handler `connection.on("AnalyticsDataChanged")`   3. Tải ngay các REST API ban đầu
        │                                                       (KHÔNG chờ SignalR kết nối)
        ▼                                                       │
2. Bắt đầu kết nối `connection.start().catch(...)`              ▼
        │                                                    [Hiển thị UI ban đầu]
        ▼
[Trạng thái hoạt động bình thường]
        │
        ├───────────────────────────────────────────────────────┐
        ▼                                                       ▼
[Nhận event SignalR]                                   [Mất kết nối / Reconnect]
        │                                                       │
4. Debounce 300 ms (gộp các event dồn dập)              Tự động reconnect theo backoff
        │                                                       │
5. Hủy request REST đang in-flight (AbortController)    Reconnect thành công:
        │                                               Tải lại toàn bộ REST API hiện hành
        ▼                                               (giữ nguyên bộ lọc thời gian)
6. Tải lại toàn bộ các REST API đang mở
```

### Xử lý Ngoại lệ & Giới hạn Mạng:
1. **Khởi tạo độc lập**:
   - `connection.start()` phải chạy song song, **không `await` chặn việc tải REST ban đầu**. Nếu SignalR lỗi (mất mạng, server Hub restart), dashboard vẫn phải hiển thị dữ liệu REST bình thường.
2. **Debounce 300ms**:
   - Khi nhận nhiều event dồn dập (ví dụ đơn nhiều dòng thiết bị), gộp lại và chỉ kích hoạt một đợt gọi lại REST duy nhất sau 300ms.
3. **Chống response cũ ghi đè (In-flight cancellation)**:
   - Dùng `AbortController` hủy bỏ request REST trước đó khi có đợt tải lại mới.
4. **Xử lý lỗi Auth (401 vs 403)**:
   - **401 Unauthorized**: Token hết hạn $\rightarrow$ gọi luồng refresh session hiện có của AuthService, lấy token mới rồi kết nối lại Hub.
   - **403 Forbidden**: Người dùng không có quyền truy cập $\rightarrow$ dừng kết nối, **tuyệt đối không retry / reconnect vô hạn**.
5. **Polling dự phòng 60 giây**:
   - Chạy timer polling mỗi 60 giây khi trang đang mở để bù đắp các event SignalR có thể bị lỡ trong lúc mạng chập chờn.

---

## 6. Bộ Kịch bản Mock Kiểm thử (Mock Testing Scenarios)

### Kịch bản 1: Nạp Khởi Tạo và SignalR Khởi Động Chậm / Thất Bại
- Mở dashboard NCC, SignalR `connection.start()` bị timeout hoặc lỗi mạng.
- **Kỳ vọng UI**:
  - Giao diện vẫn hiển thị dữ liệu từ các REST API gọi ban đầu (`/me/overview`, `/me/revenue`, listing).
  - Dashboard không bị treo ở trạng thái loading vô hạn.

### Kịch bản 2: Payment Đến Trước Snapshot Rental
- Khách thanh toán trước (`InvoicePaidFinancial`), NCC mở dashboard.
- Ban đầu `/me/revenue` hiển thị 0 đ (vì snapshot Rental chưa tới, chưa map được NCC).
- Sau đó nhận event SignalR `AnalyticsDataChanged` scope `Rental` (do `RentalOrderCreated` đến sau).
- **Kỳ vọng UI**:
  - Frontend reload cả `/me/overview` và `/me/revenue` (áp dụng quy tắc reload-all).
  - Số tiền thuê nhảy tăng lên đúng giá trị thiết bị của NCC.

### Kịch bản 3: Token Hết Hạn & Refresh Thành Công
- Tab dashboard mở qua đêm, access token hết hạn.
- Hub SignalR tự động ngắt kết nối (`CloseOnAuthenticationExpiration = true`).
- Lần gọi REST tiếp theo nhận mã `401`.
- **Kỳ vọng UI**:
  - Frontend tự động gọi endpoint refresh token của AuthService.
  - Sau khi có cookie mới, SignalR tự động kết nối lại thành công và tải lại số liệu.

### Kịch bản 4: Admin Timeout Nhưng NCC Vẫn Nhận Tin
- Backend gặp sự cố gửi nhóm Admin bị timeout (> 2s), nhưng gửi NCC vẫn hoàn tất độc lập.
- **Kỳ vọng UI**:
  - Dashboard của NCC vẫn nhận được event `AnalyticsDataChanged` và cập nhật dữ liệu bình thường, không bị ảnh hưởng bởi lỗi của nhóm Admin.
