# Tích hợp ReviewStatus vào API Đơn hàng (RentalService)

## 1. Tổng quan và Mục tiêu

RentalService tích hợp trực tiếp `reviewStatus` vào từng dòng thiết bị (rental item) của các API đọc đơn hàng. Frontend không cần gọi thêm API batch của ReviewService, dữ liệu được làm giàu (enrich) tự động tại tầng Application của RentalService.

### Phạm vi áp dụng (3 endpoints)
1. **Danh sách đơn người thuê**: `GET /api/v1/orders/my`
2. **Chi tiết đơn người thuê**: `GET /api/v1/orders/my/{orderId}`
3. **Chi tiết đơn con nhà cung cấp**: `GET /api/v1/orders/supplier/sub-orders/{subOrderId}`

*(Danh sách đơn nhà cung cấp `GET /api/v1/orders/supplier/sub-orders` giữ nguyên cấu trúc vì response không chứa danh sách items; nhà cung cấp xem trạng thái review tại trang chi tiết đơn con).*

---

## 2. Bảng Trạng thái Review (`reviewStatus`) cho Frontend

Mỗi item trong 3 response trên luôn chứa trường `reviewStatus` với cấu trúc:

```json
"reviewStatus": {
  "state": "AVAILABLE",
  "reviewId": null,
  "deadlineAt": "2026-12-08T10:00:00Z"
}
```

| `state` | Ý nghĩa nghiệp vụ | Hành vi hiển thị đề xuất cho FE |
|---|---|---|
| **`AVAILABLE`** | Đủ điều kiện và trong thời hạn đánh giá | Hiển thị nút **"Đánh giá"** để mở form viết review |
| **`REVIEWED`** | Đã đánh giá (do chính user viết) | Hiển thị nút/nhãn **"Đã đánh giá"**; mở chi tiết review qua `reviewId` |
| **`EXPIRED`** | Đã hết thời hạn 30 ngày đánh giá | Hiển thị trạng thái **"Hết hạn đánh giá"**; không cho mở form viết mới |
| **`NOT_ELIGIBLE`** | Chưa hoặc không đủ điều kiện đánh giá (chưa hoàn tất, hoặc trùng người thuê & nhà cung cấp) | Không hiển thị nút/hành động đánh giá |
| **`UNKNOWN`** | Chưa tải được trạng thái từ ReviewService (lỗi downstream, timeout 2s, chưa có projection) | Hiển thị trạng thái đang cập nhật / cho phép bấm tải lại đơn hàng |

### Lưu ý cho Frontend:
- `reviewStatus` luôn tồn tại (không null). Hai trường `reviewId` và `deadlineAt` là nullable.
- Khi user vừa tạo review thành công qua ReviewService API (`POST /api/v1/reviews`), FE chỉ cần gọi lại API chi tiết/danh sách đơn hàng hiện tại để nhận trạng thái `REVIEWED` mới nhất.
- Trong giai đoạn rollout lệch phiên bản giữa các service, nếu trường bị thiếu hoặc state chưa nhận diện được, FE fallback xử lý như `UNKNOWN`.

---

## 3. Cấu trúc JSON minh họa cho 3 Response

### 3.1 Danh sách đơn người thuê (`GET /api/v1/orders/my`)

```json
{
  "items": [
    {
      "orderId": "6a2f9cc2-1367-44bd-b639-948e0ae13112",
      "orderCode": "DH-2026-000001",
      "status": "COMPLETED",
      "items": [
        {
          "rentalItemId": "f3f67188-5462-4072-be73-af5d16351df3",
          "equipmentListingId": "33333333-3333-3333-3333-333333333333",
          "name": "Máy quay Sony FX3",
          "status": "COMPLETED",
          "reviewStatus": {
            "state": "AVAILABLE",
            "reviewId": null,
            "deadlineAt": "2026-12-08T10:00:00.000Z"
          }
        }
      ]
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
```

### 3.2 Chi tiết đơn người thuê (`GET /api/v1/orders/my/{orderId}`)

```json
{
  "orderId": "6a2f9cc2-1367-44bd-b639-948e0ae13112",
  "orderCode": "DH-2026-000001",
  "status": "COMPLETED",
  "subOrders": [
    {
      "subOrderId": "77777777-7777-7777-7777-777777777777",
      "subOrderCode": "DH-2026-000001-01",
      "supplierId": "22222222-2222-2222-2222-222222222222",
      "items": [
        {
          "rentalItemId": "f3f67188-5462-4072-be73-af5d16351df3",
          "equipmentListingId": "33333333-3333-3333-3333-333333333333",
          "status": "COMPLETED",
          "reviewStatus": {
            "state": "REVIEWED",
            "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
            "deadlineAt": "2026-12-08T10:00:00.000Z"
          }
        }
      ]
    }
  ]
}
```

### 3.3 Chi tiết đơn con nhà cung cấp (`GET /api/v1/orders/supplier/sub-orders/{subOrderId}`)

```json
{
  "subOrderId": "77777777-7777-7777-7777-777777777777",
  "subOrderCode": "DH-2026-000001-01",
  "orderCode": "DH-2026-000001",
  "status": "COMPLETED",
  "items": [
    {
      "rentalItemId": "f3f67188-5462-4072-be73-af5d16351df3",
      "equipmentListingId": "33333333-3333-3333-3333-333333333333",
      "status": "COMPLETED",
      "reviewStatus": {
        "state": "AVAILABLE",
        "reviewId": null,
        "deadlineAt": "2026-12-08T10:00:00.000Z"
      }
    }
  ]
}
```

---

## 4. Kiến trúc và Khả năng chịu lỗi (Resilience) Backend

- **Không mở DB Transaction**: Quá trình gọi HTTP downstream sang ReviewService diễn ra sau khi hoàn tất đọc dữ liệu từ DB, không giữ kết nối DB mở.
- **Enrichment Budget 2 Giây**: Tổng thời gian gọi ReviewService tối đa 2 giây. Nếu downstream chậm hoặc lỗi mạng, RentalService fallback trả `UNKNOWN` cho các item bị ảnh hưởng, giữ nguyên dữ liệu đơn hàng và mã HTTP 200 OK cho client.
- **Bảo mật Token**: Bearer JWT được chuyển tiếp nguyên vẹn qua `ReviewTokenPropagationHandler` gắn riêng cho ReviewClient. Không lưu token trong bộ nhớ dùng chung hay HttpContext rò rỉ vào query service.
- **Batching & Concurrency**: Các mục thuê được gom trên toàn trang hoặc toàn đơn, chia batch tối đa 100 mục/request, tối đa 2 request đồng thời.
