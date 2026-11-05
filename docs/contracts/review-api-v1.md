# Hợp đồng API ReviewService v1 (Public & Admin API Specification)

Tài liệu này đặc tả toàn bộ các endpoints, cấu trúc DTO theo cơ chế Allowlist, vòng đời hai trục (`authorState` $\times$ `moderationState`), cơ chế chống trùng lặp (Idempotency), khóa lạc quan (Optimistic Concurrency với Strong ETag), xác thực/phân quyền và ma trận lỗi của `ReviewService`.

---

## 1. Chuẩn giao thức và Quy ước chung

1. **Base URL:** `/api/v1/reviews` và `/api/v1/reviews/admin`.
2. **Định dạng dữ liệu:** `application/json; charset=utf-8`.
3. **Chuẩn lỗi:** Tuân thủ RFC 9110 và RFC 7807 (`application/problem+json`).
4. **Chuỗi thời gian:** ISO 8601 UTC mở rộng (`YYYY-MM-DDTHH:mm:ss.fffZ`).
5. **Quy tắc đếm ký tự bình luận (Unicode Scalar Values):**
   - Giới hạn: tối đa 1.000 ký tự.
   - Cơ chế: Đếm theo **Unicode Scalar Values / Runes (`System.Text.Rune`)**, không đếm theo số lượng byte UTF-8 hay UTF-16 code units (`char`).
   - Ký tự có dấu tiếng Việt (NFC/NFD), emoji đơn hoặc emoji ghép (surrogate pairs) đều được xử lý chuẩn xác; một Rune tương ứng với một đơn vị đếm.
6. **Xác định danh tính (Zero Trust Client Input):**
   - Người gọi **không truyền** `authorId`, `subjectId`, `targetId`, `listingId`, `supplierId` khi tạo review.
   - Hệ thống tự động xác định `actorId` từ claim `sub` trong JWT của người gọi.
   - `subjectId`, `targetId`, `listingId`, `supplierId` và chiều đánh giá (`direction`) được ánh xạ từ `RentalItemReviewSnapshot` của RentalService.
   - **Phân biệt Human Token và Service Token:** Token hợp lệ phải mang UserId UUID trong claim `sub` và không có `u = "svc"`. Service token (`u = "svc"`) **tuyệt đối không được cấp quyền** viết/sửa/rút review; nếu gọi sẽ nhận `403 Forbidden` (`FORBIDDEN_SERVICE_TOKEN`).
7. **Định dạng Concurrency Token (Strong ETag):**
   - Sử dụng **Strong ETag** biểu diễn số nguyên phiên bản, đặt trong dấu ngoặc kép: `"1"`, `"2"`.
   - Tuyệt đối không dùng Weak ETag (`W/"1"`).
   - Mọi thao tác đột biến trên review đã tồn tại (`PUT /api/v1/reviews/{reviewId}`, `POST .../withdraw`, `POST .../hide`, `POST .../restore`) **bắt buộc phải có header `If-Match: "<version>"`**.
   - Thiếu `If-Match`: Trả về `428 Precondition Required`.
   - `If-Match` sai phiên bản: Trả về `412 Precondition Failed`.
   - Wildcard (`*`) **bị từ chối** đối với mutation (`400 Bad Request` hoặc `412 Precondition Failed`).

---

## 2. Ma trận Route, Phân quyền và Mã Lỗi (Route & Error Matrix)

| # | Thao tác | Route | Method | Yêu cầu Xác thực & Headers | Mã HTTP thành công | Mã HTTP lỗi có thể xảy ra |
| :- | :--- | :--- | :--- | :--- | :--- | :--- |
| **1** | **Tạo đánh giá** | `/api/v1/reviews` | `POST` | Bearer JWT + `Idempotency-Key` | `201 Created` | `400`, `401`, `403`, `404`, `409`, `422`, `502`, `504` |
| **2** | **Chỉnh sửa đánh giá** | `/api/v1/reviews/{reviewId}` | `PUT` | Bearer JWT (Chính tác giả) + `Idempotency-Key` + `If-Match` | `200 OK` | `400`, `401`, `403`, `404`, `409`, `412`, `422`, `428` |
| **3** | **Rút đánh giá (Withdraw)** | `/api/v1/reviews/{reviewId}/withdraw` | `POST` | Bearer JWT (Chính tác giả) + `Idempotency-Key` + `If-Match` | `200 OK` | `400`, `401`, `403`, `404`, `409`, `412`, `428` |
| **4** | **Chi tiết đánh giá (Private)** | `/api/v1/reviews/{reviewId}` | `GET` | Bearer JWT (Tác giả, Đối tượng nhận, hoặc Admin) | `200 OK` | `401`, `404` |
| **5** | **Đánh giá của tôi** | `/api/v1/reviews/my` | `GET` | Bearer JWT (Người dùng bất kỳ) | `200 OK` | `401`, `400` |
| **6** | **Mục chờ đánh giá** | `/api/v1/reviews/pending` | `GET` | Bearer JWT (Người dùng bất kỳ) | `200 OK` | `401`, `400` |
| **7** | **Xem review thiết bị** | `/api/v1/reviews/listings/{listingId}` | `GET` | Public (Không yêu cầu đăng nhập) | `200 OK` | `400`, `404` |
| **8** | **Xem review nhà cung cấp** | `/api/v1/reviews/suppliers/{supplierId}` | `GET` | Public (Không yêu cầu đăng nhập) | `200 OK` | `400`, `404` |
| **9** | **Xem tổng hợp điểm (Summary)** | `/api/v1/reviews/summary` | `GET` | Public (Không yêu cầu đăng nhập) | `200 OK` | `400` |
| **10**| **Báo cáo vi phạm** | `/api/v1/reviews/{reviewId}/reports` | `POST` | Bearer JWT (Người dùng bất kỳ) + `Idempotency-Key` | `201 Created` | `400`, `401`, `404`, `409` |
| **11**| **Admin ẩn review** | `/api/v1/reviews/admin/{reviewId}/hide` | `POST` | Bearer JWT (`Admin` role) + `Idempotency-Key` + `If-Match` | `200 OK` | `400`, `401`, `403`, `404`, `409`, `412`, `428` |
| **12**| **Admin khôi phục review** | `/api/v1/reviews/admin/{reviewId}/restore` | `POST` | Bearer JWT (`Admin` role) + `Idempotency-Key` + `If-Match` | `200 OK` | `400`, `401`, `403`, `404`, `409`, `412`, `428` |

---

## 3. Chi tiết Hợp đồng từng Endpoint

### 3.1 `POST /api/v1/reviews` (Tạo đánh giá mới)

- **Headers:**
  - `Authorization: Bearer <JWT>` (Bắt buộc).
  - `Idempotency-Key: <UUID>` (Bắt buộc, chuỗi $\le 64$ ký tự).
- **Request Body (`CreateReviewRequest`):**
  ```json
  {
    "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
    "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
    "rating": 5,
    "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!"
  }
  ```
- **Response Headers:**
  - `ETag: "1"`
  - `Location: /api/v1/reviews/8f731888-eb87-43cf-8a21-72946c87e6aa`
- **Response Body (`201 Created` - `ReviewDetailResponse`):**
  ```json
  {
    "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
    "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
    "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
    "direction": "renter_to_supplier_listing",
    "authorId": "11111111-1111-1111-1111-111111111111",
    "subjectId": "22222222-2222-2222-2222-222222222222",
    "equipmentListingId": "33333333-3333-3333-3333-333333333333",
    "rating": 5,
    "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!",
    "authorState": "Active",
    "moderationState": "Visible",
    "version": 1,
    "createdAt": "2026-11-02T08:30:00.000Z",
    "updatedAt": null,
    "withdrawnAt": null,
    "canEditUntil": "2026-11-09T08:30:00.000Z"
  }
  ```

---

### 3.2 `PUT /api/v1/reviews/{reviewId}` (Chỉnh sửa đánh giá)

- **Đặc tính vòng đời:** Author được phép chỉnh sửa review trong thời hạn edit window (`now < canEditUntil`) miễn là chưa rút (`authorState == Active`). Nếu review đang bị Admin ẩn (`moderationState == Hidden`), tác giả vẫn được phép sửa, nhưng **thao tác sửa không tự động khôi phục moderationState** (nó vẫn giữ nguyên `Hidden`).
- **Quyền sở hữu và phạm vi xác thực:** Thao tác sửa kiểm tra quyền sở hữu (`actorId == review.AuthorId`), edit window (`now < review.CanEditUntil`), và `If-Match` trực tiếp trên dữ liệu review đã lưu trong PostgreSQL. Không bắt buộc và không gọi lại RentalService snapshot khi không có thay đổi eligibility.
- **Headers:**
  - `Authorization: Bearer <JWT>` (Bắt buộc).
  - `Idempotency-Key: <UUID>` (Bắt buộc).
  - `If-Match: "1"` (Bắt buộc; thiếu trả `428`, sai trả `412`).
- **Request Body (`UpdateReviewRequest`):**
  ```json
  {
    "rating": 4,
    "comment": "Cập nhật lại: máy quay rất tốt nhưng pin hơi chai nhẹ sau thời gian dài sử dụng."
  }
  ```
- **Response Headers:**
  - `ETag: "2"`
- **Response Body (`200 OK` - `ReviewDetailResponse`):**
  ```json
  {
    "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
    "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
    "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
    "direction": "renter_to_supplier_listing",
    "authorId": "11111111-1111-1111-1111-111111111111",
    "subjectId": "22222222-2222-2222-2222-222222222222",
    "equipmentListingId": "33333333-3333-3333-3333-333333333333",
    "rating": 4,
    "comment": "Cập nhật lại: máy quay rất tốt nhưng pin hơi chai nhẹ sau thời gian dài sử dụng.",
    "authorState": "Active",
    "moderationState": "Visible",
    "version": 2,
    "createdAt": "2026-11-02T08:30:00.000Z",
    "updatedAt": "2026-11-03T09:15:00.000Z",
    "withdrawnAt": null,
    "canEditUntil": "2026-11-09T08:30:00.000Z"
  }
  ```

---

### 3.3 `POST /api/v1/reviews/{reviewId}/withdraw` (Rút đánh giá)

- **Đặc tính:** Chuyển `authorState` sang `Withdrawn`. Tác giả có quyền rút cả review đang hiển thị (`Visible`) lẫn review đang bị ẩn (`Hidden`). Bảo toàn ràng buộc duy nhất `(itemId, direction)` trong cơ sở dữ liệu.
- **Quyền sở hữu và phạm vi xác thực:** Thao tác rút kiểm tra quyền sở hữu (`actorId == review.AuthorId`) và `If-Match` trực tiếp trên dữ liệu review đã lưu trong PostgreSQL. Không bắt buộc và không gọi lại RentalService snapshot khi không có thay đổi eligibility.
- **Headers:**
  - `Authorization: Bearer <JWT>` (Bắt buộc).
  - `Idempotency-Key: <UUID>` (Bắt buộc).
  - `If-Match: "2"` (Bắt buộc).
- **Request Body (`WithdrawReviewRequest`):**
  ```json
  {
    "reason": "Tôi muốn rút lại đánh giá do đã giải quyết hiểu lầm với đối tác."
  }
  ```
- **Response Headers:**
  - `ETag: "3"`
- **Response Body (`200 OK` - `ReviewDetailResponse`):**
  ```json
  {
    "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
    "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
    "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
    "direction": "renter_to_supplier_listing",
    "authorId": "11111111-1111-1111-1111-111111111111",
    "subjectId": "22222222-2222-2222-2222-222222222222",
    "equipmentListingId": "33333333-3333-3333-3333-333333333333",
    "rating": 4,
    "comment": "Cập nhật lại: máy quay rất tốt nhưng pin hơi chai nhẹ sau thời gian dài sử dụng.",
    "authorState": "Withdrawn",
    "moderationState": "Visible",
    "version": 3,
    "createdAt": "2026-11-02T08:30:00.000Z",
    "updatedAt": "2026-11-03T09:15:00.000Z",
    "withdrawnAt": "2026-11-04T10:00:00.000Z",
    "canEditUntil": "2026-11-09T08:30:00.000Z"
  }
  ```

---

### 3.4 `GET /api/v1/reviews/{reviewId}` (Xem chi tiết đánh giá riêng tư)

- **Mục đích:** Cung cấp endpoint cho chính Tác giả (`Author`), Đối tượng nhận (`Subject`) và Quản trị viên (`Admin`) đọc đầy đủ thông tin review (kể cả review chiều `supplier_to_renter` hoặc review bị ẩn/rút), đồng thời **lấy giá trị `ETag` hiện tại** phục vụ các thao tác mutation tiếp theo.
- **Phân quyền và Bảo vệ Quyền riêng tư:**
  - Nếu request không có Bearer JWT (chưa xác thực / anonymous): Middleware xác thực trả về `401 Unauthorized`.
  - Nếu đã xác thực: Cho phép truy cập nếu `actorId == authorId` hoặc `actorId == subjectId` hoặc `actor.IsAdmin == true`.
  - Nếu người dùng đã xác thực nhưng không phải Author, Subject hoặc Admin: Trả về `404 Not Found` (tuyệt đối không trả `403 Forbidden` để không để lộ sự tồn tại của review).
- **Response Headers:**
  - `ETag: "3"`
- **Response Body (`200 OK` - `ReviewDetailResponse`):**
  Cấu trúc DTO tương tự mục 3.3.

---

### 3.5 `GET /api/v1/reviews/pending` (Danh sách mục chờ đánh giá)

Dựa trên bảng chiếu (`review_projections`) đã hydrate đầy đủ và **kiểm tra loại trừ** bằng mệnh đề `NOT EXISTS` sang bảng `reviews` (kể cả review đã rút):
- **Query Parameters:**
  - `page`: Số nguyên dương, mặc định `1`.
  - `pageSize`: Số nguyên từ `1` đến `50`, mặc định `20`.
- **Response Body (`200 OK` - `PagedResult<PendingReviewItemResponse>`):**
  ```json
  {
    "items": [
      {
        "orderId": "5b824944-3993-5ef4-b296-2cf146fe96e2",
        "itemId": "c9a1585d-5eb7-577e-b3ed-cf72ee4258a8",
        "equipmentListingId": "33333333-3333-3333-3333-333333333333",
        "counterpartId": "22222222-2222-2222-2222-222222222222",
        "direction": "renter_to_supplier_listing",
        "completedAt": "2026-10-15T10:00:00.000Z",
        "deadlineAt": "2026-11-14T10:00:00.000Z",
        "daysRemaining": 12
      }
    ],
    "page": 1,
    "pageSize": 20,
    "totalCount": 1,
    "totalPages": 1
  }
  ```

---

### 3.6 `GET /api/v1/reviews/listings/{listingId}` & `GET /api/v1/reviews/suppliers/{supplierId}`

- **Đặc điểm:** Endpoint công khai (Public).
- **Quy tắc lọc dữ liệu (Allowlist Filter):**
  - Chỉ nhận các review có: `direction = 'renter_to_supplier_listing'` **AND** `authorState = 'Active'` **AND** `moderationState = 'Visible'`.
  - Tuyệt đối không trả về review có `authorState = 'Withdrawn'` hoặc `moderationState = 'Hidden'`.
  - Tuyệt đối không trả về review chiều `supplier_to_renter`.
- **Cấu trúc DTO Allowlist (`PublicReviewItemResponse`):** Chỉ lộ các trường an toàn, tên tác giả sử dụng nhãn trung tính ẩn danh cố định trong MVP (ví dụ: `"Khách hàng"`), không phát sinh tra cứu Auth lookup trên mỗi truy vấn danh sách:
  ```json
  {
    "items": [
      {
        "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
        "rating": 5,
        "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!",
        "createdAt": "2026-11-02T08:30:00.000Z",
        "updatedAt": null,
        "authorMaskedName": "Khách hàng"
      }
    ],
    "page": 1,
    "pageSize": 20,
    "totalCount": 1,
    "totalPages": 1
  }
  ```

---

### 3.7 `GET /api/v1/reviews/summary` (Tổng hợp điểm đánh giá)

- **Query Parameters:** `targetType=listing|supplier`, `targetId={UUID}`.
- **Quy tắc tính:** Truy vấn trực tiếp bảng `reviews` với điều kiện `authorState = 'Active' AND moderationState = 'Visible' AND direction = 'renter_to_supplier_listing'`.
- **Response Body (`200 OK` - `ReviewSummaryResponse`):**
  ```json
  {
    "targetType": "listing",
    "targetId": "33333333-3333-3333-3333-333333333333",
    "averageRating": 4.8,
    "reviewCount": 15,
    "ratingDistribution": {
      "5": 12,
      "4": 3,
      "3": 0,
      "2": 0,
      "1": 0
    }
  }
  ```
- **Khi `reviewCount == 0`:** `averageRating` trả về `null`.

---

### 3.8 `POST /api/v1/reviews/admin/{reviewId}/hide` & `restore` (Kiểm duyệt Admin)

- **Phân quyền:** Chỉ chấp nhận JWT của người dùng có role `Admin`.
- **Cơ chế:** Thao tác **chỉ thay đổi `moderationState`** (`Hidden` $\leftrightarrow$ `Visible`).
- **Quy tắc bảo vệ:** Nếu Admin thực hiện `restore` một review đã bị tác giả rút (`authorState == Withdrawn`), review đó chỉ chuyển `moderationState = 'Visible'`, nhưng vì `authorState` vẫn là `Withdrawn` nên review **tuyệt đối không xuất hiện trở lại** trên public listings hay summary!
- **Headers:**
  - `Authorization: Bearer <JWT>` (`Admin` role).
  - `Idempotency-Key: <UUID>`.
  - `If-Match: "3"`.
- **Request Body (`AdminModerationRequest`):**
  ```json
  {
    "reason": "Vi phạm tiêu chuẩn cộng đồng điều 4: ngôn từ kích động bạo lực."
  }
  ```
- **Response Headers:** `ETag: "4"`
- **Response Body (`200 OK`):**
  ```json
  {
    "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
    "authorState": "Active",
    "moderationState": "Hidden",
    "version": 4,
    "moderatedAt": "2026-11-04T14:30:00.000Z",
    "moderatorId": "99999999-9999-9999-9999-999999999999",
    "reason": "Vi phạm tiêu chuẩn cộng đồng điều 4: ngôn từ kích động bạo lực."
  }
  ```

---

## 4. Cơ chế Chống trùng lặp (Idempotency) và Khóa Lạc quan (Optimistic Concurrency)

### 4.1 Cơ chế Idempotency Fingerprinting

1. **Header bắt buộc:** `Idempotency-Key` trên toàn bộ các phương thức đột biến (`POST`, `PUT`).
2. **Khóa nhận diện Idempotency (Fingerprint):**
   $$\text{Fingerprint} = \text{SHA-256}(\text{ActorId} + \text{HTTP Method} + \text{CanonicalRoute} + \text{NormalizedBodySha256} + \text{IfMatchHeader})$$
3. **Thứ tự thực thi nghiêm ngặt trong Pipeline:**
   ```
   Step 1: Xác thực & Quyền truy cập (Auth Check)
             |
   Step 2: Kiểm tra Idempotency Receipt theo (actor_id, key)
             +---> Trùng Key & Trùng Fingerprint: Trả về kết quả cached (201/200 + Body + ETag)
             +---> Trùng Key & Khác Fingerprint: Trả về 409 Conflict (IDEMPOTENCY_KEY_REUSE_MISMATCH)
             |
   Step 3: Kiểm tra Điều kiện tiên quyết (Precondition Check)
             +---> Thiếu If-Match trên mutation: Trả về 428 Precondition Required
             +---> Sai phiên bản version: Trả về 412 Precondition Failed
             |
   Step 4: Thực thi Nghiệp vụ & Đột biến dữ liệu (Mutation)
             +---> Commit cùng lúc: Review Mutation + Idempotency Receipt + Audit Log
   ```
4. **Quy tắc an toàn giao dịch:**
   - Khi gọi RentalService HTTP snapshot, **không mở DB transaction**.
   - Lỗi dependency (timeout, 502) không bao giờ được ghi thành receipt thành công trong bảng idempotency.
   - Thời gian lưu trữ receipt mặc định: **30 ngày**.
   - Các request đồng thời cùng key được tuần tự hóa (serialize) bằng ràng buộc duy nhất trong database `UNIQUE (key, actor_id)`, ngăn ngừa trạng thái `Processing` bị treo vô hạn.

### 4.2 Cơ chế Optimistic Concurrency (Strong ETag & If-Match)

1. Mọi response trả về chi tiết review đều chứa header:
   `ETag: "<version>"` (ví dụ: `ETag: "1"`).
2. Khi thực hiện `PUT /api/v1/reviews/{reviewId}`, `withdraw`, `hide`, `restore`, client bắt buộc phải gửi header `If-Match: "<version>"`:
   - Thiếu header: Trả về `428 Precondition Required`.
   - Phiên bản không khớp: Trả về `412 Precondition Failed`.

---

## 5. Fixtures Mẫu Lỗi Nghiệp vụ Chuẩn (ProblemDetails Fixtures)

### 5.1 Lỗi 428 Precondition Required: Thiếu header `If-Match`

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-3",
  "title": "Precondition Required",
  "status": 428,
  "detail": "Yêu cầu chỉnh sửa hoặc thay đổi trạng thái đánh giá bắt buộc phải có header 'If-Match' chứa ETag phiên bản hiện tại.",
  "instance": "/api/v1/reviews/8f731888-eb87-43cf-8a21-72946c87e6aa",
  "code": "PRECONDITION_REQUIRED"
}
```

### 5.2 Lỗi 412 Precondition Failed: ETag không khớp

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.13",
  "title": "Precondition Failed",
  "status": 412,
  "detail": "Đánh giá đã bị thay đổi bởi thao tác khác (phiên bản gửi lên không khớp với cơ sở dữ liệu). Vui lòng tải lại trước khi thao tác.",
  "instance": "/api/v1/reviews/8f731888-eb87-43cf-8a21-72946c87e6aa",
  "code": "ETAG_PRECONDITION_FAILED"
}
```

### 5.3 Lỗi 409 Conflict: Tái sử dụng Idempotency-Key với nội dung khác

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung hoặc phiên bản khác.",
  "instance": "/api/v1/reviews",
  "code": "IDEMPOTENCY_KEY_REUSE_MISMATCH"
}
```

### 5.4 Lỗi 502 Bad Gateway: Lỗi xác thực với RentalService

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.3",
  "title": "Bad Gateway",
  "status": 502,
  "detail": "Không thể xác thực với dịch vụ RentalService nội bộ qua X-Api-Key.",
  "instance": "/api/v1/reviews",
  "code": "RENTAL_SERVICE_AUTHENTICATION_FAILED"
}
```

### 5.5 Lỗi 504 Gateway Timeout: RentalService không phản hồi kịp

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.5",
  "title": "Gateway Timeout",
  "status": 504,
  "detail": "Dịch vụ RentalService không phản hồi yêu cầu snapshot trong thời gian giới hạn (5 giây).",
  "instance": "/api/v1/reviews",
  "code": "RENTAL_SERVICE_TIMEOUT"
}
```
