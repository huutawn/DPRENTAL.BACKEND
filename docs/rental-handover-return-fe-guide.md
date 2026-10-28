# FE Guide — Bàn giao, yêu cầu trả, thu hồi và bất đồng

Ngày đối chiếu: 28/10/2026. Bản tích hợp: `659efa41`, branch `rental/feat/handover-implementaion`. Đối tượng: FE Renter, Supplier, Admin và mock API.

## 1. Phạm vi và mức độ sẵn sàng

Tài liệu nối bốn task thành một luồng sản phẩm. **Có code trên branch không đồng nghĩa đã deploy lên môi trường FE đang gọi.**

| Phần | Nguồn đối chiếu | Cách FE sử dụng |
| --- | --- | --- |
| Task 1: handover, acknowledgement, cancel-not-delivered | Đã merge qua `810add97` | Mock theo mục 5 |
| Task 2: tạo request, đọc Units/ReturnRequests, đóng request unit | Đã tích hợp các commit tới `62705b27` | Mock theo mục 6 |
| Task 3: Supplier inspection và thu hồi | Đã merge tại `10e57a24` | Mock theo mục 7; DAMAGED đã bắt buộc mô tả + ảnh |
| Task 4: report issue, deadline, Admin resolution | Đã merge tại `659efa41` | Route/DTO có implementation, mock theo mục 8 |

Cả bốn task đã nằm trong bản tích hợp trên. FE cần xác nhận staging đang chạy cùng phiên bản và đã áp dụng migration return issues trước khi chuyển từ mock sang API thật. Các lỗi còn mở sau review được ghi tại mục 11; tài liệu không khẳng định bản tích hợp đã đạt mọi kiểm thử.

Nguồn nghiệp vụ: [Project Guide](PROJECT_GUIDE.md). Realtime: [hướng dẫn SignalR](rental-realtime-client-integration.md). Chi tiết backend: [Returns](../RentalService/docs/RETURNS.md).

## 2. Khái niệm FE cần phân biệt

- **A = Supplier**, chủ thiết bị; **B = Renter**, người thuê.
- Order có nhiều SubOrder theo Supplier; mỗi SubOrder có nhiều RentalItem.
- RentalItem = một listing + số lượng + khoảng thuê. Ví dụ một item có 3 máy.
- RentalUnitAssignment = một máy được gán cho item. FE dùng AssignmentId khi thao tác từng máy.
- HandoverRecord = Supplier đã bàn giao và Inventory đã xác nhận.
- ReturnRequest = Renter khai báo đã trả. Chưa chứng minh Supplier đã nhận, chưa giải phóng máy.
- ReturnRecord = Supplier/Admin ghi nhận thực nhận và tình trạng. Inventory tiếp tục xử lý bất đồng bộ.
- `ClosedAt` của request unit = đã ghi nhận thu hồi tại Rental, **không** phải bằng chứng Inventory đã giải phóng máy.

Không suy ra trạng thái từng item từ trạng thái Order. Một item đã trả đủ trong khi item khác vẫn đang thuê là hợp lệ.

```mermaid
flowchart TD
  A[Supplier chấp nhận Đơn con] --> B[Item AWAITING_HANDOVER]
  B --> C[Supplier gửi bàn giao: 202 đang xử lý]
  C --> D{Inventory xác nhận?}
  D -->|Chưa xác nhận| C
  D -->|Thành công| E[HandoverRecord + IN_RENTAL]
  E --> F[Renter xác nhận đã nhận: state giữ nguyên]
  E --> G[Renter khai báo trả một hoặc nhiều máy]
  G --> H[ReturnRequest: Item vẫn IN_RENTAL]
  H --> I[Supplier nhận máy và kiểm tra NORMAL / DAMAGED]
  I --> J[ReturnRecord + Inventory ApplyReturnedUnits]
  J --> K{Đã nhận đủ máy tại Rental?}
  K -->|Chưa| E
  K -->|Đủ| L[PENDING_SETTLEMENT, chưa COMPLETED]
  H --> M[Issue hoặc quá 24h: chờ xử lý thủ công]
  M --> N[Admin xác nhận thực nhận hoặc bác yêu cầu]
  N -->|Xác nhận| J
```

## 3. Quy ước HTTP, ID và mock

Base path public: **`/api/v1/rental`**. Các route bên dưới viết tương đối với base path.

- Gửi user authentication theo app hiện có; không đưa API key nội bộ lên browser.
- JSON camelCase; timestamp ISO 8601 có offset, ưu tiên UTC `Z`. Hiển thị theo `Asia/Ho_Chi_Minh`.
- Enum item/assignment/condition serialize `UPPER_SNAKE_CASE`: `IN_RENTAL`, `PENDING_SETTLEMENT`, `NORMAL`, `RETURNED_DAMAGED`.
- **Ngoại lệ Task 4 dùng PascalCase** do converter riêng: request/unit status là `PendingSupplierConfirmation`, `Disputed`, `Confirmed`, `Rejected`, `Resolved` (Resolved chỉ có ở request); decision là `ConfirmReceived`/`RejectRequest`; reporterRole là `Renter`/`Supplier`/`Admin`/`System`. Không đổi toàn bộ status sang cùng một case.
- Field kiểu string không tự đổi case: handover operation có thể là `Processing`, `Pending`, `Succeeded`; cancel response dùng `Cancelled`. Không dùng chung enum item cho operation status.
- Header `Idempotency-Key`: bắt buộc cho handover, cancel, return request, Supplier return, report issue và Admin resolve; tối đa 64 ký tự.
- Mỗi thao tác mới tạo một key. Retry do timeout/mất mạng giữ nguyên key và payload. Khi sửa payload, tạo key mới sau khi đã xác định kết quả operation cũ.
- Acknowledgement không yêu cầu key; server giữ thông tin xác nhận đầu tiên.

Các JSON trong tài liệu là **fragment phục vụ mock**, không phải đầy đủ response order. Khi mock GET thật, đặt item trong `subOrders[].items[]` của Renter hoặc `items[]` của Supplier detail.

Fixture cố định: một item 3 máy. Mock clock ban đầu `2026-10-28T10:00:00Z`; kịch bản quá hạn chủ động advance tới `2026-10-29T08:05:00Z`. Thời điểm người dùng khai báo không được vượt mock clock:

| Alias | ID |
| --- | --- |
| itemId | `10000000-0000-4000-8000-000000000001` |
| u1 | `20000000-0000-4000-8000-000000000001` |
| u2 | `20000000-0000-4000-8000-000000000002` |
| u3 | `20000000-0000-4000-8000-000000000003` |
| requestId | `30000000-0000-4000-8000-000000000001` |
| mediaId | `40000000-0000-4000-8000-000000000001` |

Ảnh mock chỉ là reference. Khi dùng BE thật, phải upload qua MediaService trước, dùng ảnh Private/Ready do đúng actor tải lên. Tối đa 10 media/unit, không trùng hoặc GUID rỗng. Contract upload/preview thuộc MediaService, không tự dựng URL từ MediaId.

## 4. Màn hình và điều kiện thao tác

| Vai trò / nút | Điều kiện chính | Sau thao tác |
| --- | --- | --- |
| A — Bàn giao | Item của A, AWAITING_HANDOVER, đủ assignment, chưa có operation đang xử lý | Hiển thị chờ xác nhận, chưa báo thuê đã bắt đầu |
| B — Đã nhận thiết bị | Có HandoverRecord và chưa acknowledgement | Hiển thị thời điểm đã xác nhận; state giữ nguyên |
| B — Hủy vì chưa được giao | AWAITING_HANDOVER, tới mốc D+1 giờ VN | Có thể nhận 409 nếu bàn giao đang xử lý/đối soát |
| B — Khai báo đã trả | IN_RENTAL; chọn unit có canRequestReturn=true | Request hiện ở lịch sử; item vẫn đang thuê |
| A — Xác nhận nhận lại | Unit đã giao và chưa ghi nhận trả | Cho phép chọn một phần, nhập inspection từng máy |
| A/B — Báo vấn đề (Task 4) | Request còn unit actionable | Hiển thị Disputed, không tự đổi Item state |
| Admin — Xử lý thủ công (Task 4) | Có quyền Admin, chọn unit chưa xử lý | ConfirmReceived hoặc RejectRequest |

Đây là điều kiện UI; BE vẫn quyết định cuối cùng. Khi trả 409 do màn hình cũ, reload detail rồi giải thích nguyên nhân. Không ẩn lỗi bằng cách tự đổi state trên client.

### Hạn chưa bàn giao khác hạn xác nhận trả

- Task 1: từ 00:00 giờ VN ngày D+1, B có thể hủy vì chưa nhận máy. D là ExpectedStartDate. Auto-expire mặc định D+2 theo grace cấu hình.
- Bàn giao pending/processing/đối soát chặn hủy, kể cả đã quá hạn. Hiển thị “Đang đối soát bàn giao”.
- Task 4: `confirmationDeadlineAt` bằng ReturnRequest.SubmittedAt + 24 giờ. Không dùng ActualReturnAt do B tự khai để tính hạn.
- Hết hạn xác nhận trả chỉ cảnh báo/chuyển xử lý thủ công; không tự xác nhận hoặc trả tiền.

## 5. Task 1 — Bàn giao và xác nhận nhận

### A gửi bàn giao

`POST /items/{itemId}/handover` — `202 Accepted`.

```json
{
  "actualHandoverAt": "2026-10-27T08:00:00Z",
  "note": "Giao đủ 3 máy tại công trình",
  "confirmed": true,
  "units": [
    { "assignmentId": "20000000-0000-4000-8000-000000000001", "condition": "NORMAL", "note": null, "mediaIds": ["40000000-0000-4000-8000-000000000001"] },
    { "assignmentId": "20000000-0000-4000-8000-000000000002", "condition": "NORMAL", "note": null, "mediaIds": ["40000000-0000-4000-8000-000000000001"] },
    { "assignmentId": "20000000-0000-4000-8000-000000000003", "condition": "NORMAL", "note": null, "mediaIds": ["40000000-0000-4000-8000-000000000001"] }
  ]
}
```

Phải gửi đủ 3 assignment; mỗi máy có ảnh; thời điểm không được ở tương lai. ID lấy từ `units[].assignmentId` trong Supplier suborder detail. Mock dùng lại một ảnh chung; UI thực tế nên ghi bằng chứng rõ cho từng máy.

Response mới gửi:

```json
{ "handoverRecordId": null, "handoverAt": "2026-10-27T08:00:00Z", "status": "Processing" }
```

UI: “Đã gửi bàn giao — đang chờ hệ thống xác nhận”. Chặn submit mới. Inventory lỗi/chưa trả lời không được mock thành thành công ngay.

Sau Inventory xác nhận: GET detail có `status: "IN_RENTAL"` và `handover` khác null. Replay POST cùng key có thể trả `Succeeded` kèm HandoverRecordId. `handover` trong GET hiện không chứa record ID, nên nút B xác nhận dựa trên sự tồn tại object này, không đòi ID từ GET.

### B xác nhận nhận

`POST /items/{itemId}/acknowledge-receipt`, body `{"note":"Đã nhận đủ máy"}` — `200 OK`:

```json
{
  "itemId": "10000000-0000-4000-8000-000000000001",
  "handoverRecordId": "50000000-0000-4000-8000-000000000001",
  "receivedAt": "2026-10-27T08:15:00Z",
  "receivedBy": "60000000-0000-4000-8000-000000000001",
  "note": "Đã nhận đủ máy"
}
```

Replay giữ thời điểm/note đầu tiên. Không chuyển IN_RENTAL lần nữa. Không lấy acknowledgement làm điều kiện bắt buộc cho request return: điều kiện BE dựa trên handover đã xác nhận.

### B hủy chưa giao

`POST /items/{itemId}/cancel-not-delivered`, body `{"reason":"Chưa nhận được máy"}` — `202 Accepted`:

```json
{ "itemId": "10000000-0000-4000-8000-000000000001", "status": "Cancelled", "cancelledAt": "2026-10-28T10:00:00Z", "refundAmount": 6000000 }
```

Item đã hủy nhưng refund còn bất đồng bộ. Không hiển thị “Đã nhận tiền hoàn” từ response này. Chỉ item bị hủy được hoàn tiền thuê + cọc tương ứng.

## 6. Task 2 — B khai báo trả một phần

GET `/orders/{orderId}` trả fragment item:

```json
{
  "rentalItemId": "10000000-0000-4000-8000-000000000001",
  "quantity": 3,
  "status": "IN_RENTAL",
  "units": [
    { "assignmentId": "20000000-0000-4000-8000-000000000001", "status": "HANDED_OVER", "hasOpenReturnRequest": false, "canRequestReturn": true },
    { "assignmentId": "20000000-0000-4000-8000-000000000002", "status": "HANDED_OVER", "hasOpenReturnRequest": false, "canRequestReturn": true },
    { "assignmentId": "20000000-0000-4000-8000-000000000003", "status": "HANDED_OVER", "hasOpenReturnRequest": false, "canRequestReturn": true }
  ],
  "returnRequests": []
}
```

Renter không nhận AssetId/AllocationId. AssignmentId là opaque ID để gửi lại; UI không hiển thị UUID như tên máy. Có thể đánh nhãn ổn định “Máy 1/2/3” theo thứ tự assignment; không giả định đã có serial public.

`POST /items/{itemId}/return-requests` — `200 OK`:

```json
{
  "actualReturnAt": "2026-10-28T08:00:00Z",
  "note": "Đã trả máy thứ nhất tại kho",
  "units": [
    { "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001", "mediaIds": [] }
  ]
}
```

Ảnh optional ở request của B; cần gửi collection `[]` nếu không có. Không gửi field `condition`; inspection thực tế do A thực hiện.

Response `200 OK` tại bản tích hợp Task 1–4:

```json
{
  "returnRequestId": "30000000-0000-4000-8000-000000000001",
  "itemId": "10000000-0000-4000-8000-000000000001",
  "actualReturnAt": "2026-10-28T08:00:00Z",
  "note": "Đã trả máy thứ nhất tại kho",
  "submittedAt": "2026-10-28T08:05:00Z",
  "units": [
    { "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001", "assignmentId": "20000000-0000-4000-8000-000000000001", "mediaIds": [], "closedAt": null, "rejectedAt": null, "status": "PendingSupplierConfirmation", "isOverdue": false, "overdueNotifiedAt": null }
  ],
  "status": "PendingSupplierConfirmation",
  "confirmationDeadlineAt": "2026-10-29T08:05:00Z",
  "isOverdue": false,
  "issues": [],
  "resolutions": []
}
```

Response unit serialize cả alias `assignmentId`; FE dùng `rentalUnitAssignmentId` làm khóa chính của request unit, không phụ thuộc alias. `units[].status` ở đây là trạng thái request unit (PascalCase), khác `item.units[].status` là trạng thái assignment (UPPER_SNAKE_CASE).

Sau POST, reload GET: u1 có `hasOpenReturnRequest=true`, `canRequestReturn=false`; u2/u3 vẫn chọn được. Item vẫn IN_RENTAL, chưa có ReturnRecord và không có lệnh giải phóng Inventory.

Renter/Supplier xem lịch sử qua `returnRequests` trong detail. GET phản ánh ClosedAt mới nhất; replay POST luôn trả snapshot cũ, nên replay có thể vẫn `closedAt: null` sau khi A đã nhận. Không dùng replay response để ghi đè lịch sử GET mới hơn.

## 7. Task 3 — A kiểm tra và ghi nhận thực nhận

`POST /items/{itemId}/returns` — `200 OK`. Không gọi `/return-requests/{id}/confirm`; không có route này trong implementation hiện tại.

```json
{
  "actualReturnAt": "2026-10-28T09:00:00Z",
  "note": "Nhận lại máy thứ nhất",
  "units": [
    {
      "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001",
      "condition": "DAMAGED",
      "description": "Vỏ máy bị nứt",
      "mediaIds": ["40000000-0000-4000-8000-000000000001"]
    }
  ]
}
```

Label UI là “Thời điểm thực nhận”; field API vẫn `actualReturnAt`. Không gửi `actualReceivedAt`. A tự xác minh và nhập thời điểm, không tự động lấy khai báo của B làm sự thật.

| Input | NORMAL | DAMAGED — yêu cầu Task 3 |
| --- | --- | --- |
| Description | Optional, <= 2.000 ký tự | Bắt buộc, không chỉ khoảng trắng, <= 2.000 ký tự |
| MediaIds | Có thể `[]` | Ít nhất một ảnh Private/Ready do A tải lên |
| ActualReturnAt | Không tương lai, không trước handover | Cùng quy tắc |

Trả thành công 1/3 máy:

```json
{ "returnRecordId": "70000000-0000-4000-8000-000000000001", "submittedAt": "2026-10-28T09:05:00Z", "remainingUnits": 2, "status": "IN_RENTAL", "deductionDeadline": null }
```

GET sau đó: request unit u1 có ClosedAt = `2026-10-28T09:05:00Z`, assignment `RETURNED_DAMAGED`. Hai máy còn lại tiếp tục thuê. ReturnRecord được ghi tại Rental, chưa chứng minh Inventory đã block/giải phóng.

Đếm “A đã nhận X/3 máy” từ assignment đã ghi nhận trả; đếm “B đã khai báo trả” riêng từ request. Không dùng số unit trong ReturnRequests làm số máy đã thu hồi.

| Sau A nhận | Item | Khấu trừ |
| --- | --- | --- |
| Còn máy chưa nhận | IN_RENTAL | Chưa chốt chỉ vì một batch NORMAL |
| Đã nhận đủ, bất kỳ batch nào có damage + có cọc | PENDING_SETTLEMENT | Deadline = server time ghi nhận đủ + 48h |
| Đã nhận đủ, không damage | PENDING_SETTLEMENT | FinalDeductionAmount = 0 |
| Đã nhận đủ, damage nhưng không cọc | PENDING_SETTLEMENT | FinalDeductionAmount = 0, không mở deadline |

Chưa hiển thị COMPLETED hoặc “Đã hoàn cọc” từ POST này. Inventory xử lý NORMAL bằng đóng allocation; DAMAGED bằng đóng allocation và chặn đơn mới. Settlement/Payment tiếp tục riêng.

Renter/Supplier detail chưa có field public biểu thị trạng thái operation Inventory. Hiển thị “Đã ghi nhận thu hồi”, không suy ra “Đã đồng bộ Inventory” từ assignment hoặc ClosedAt. Admin detail có `inventoryUnits` (mục 8). Mock giữ trạng thái Inventory riêng để mô phỏng pending/success/rejected, không thêm field tự đặt vào response Renter/Supplier.

## 8. Task 4 — Bất đồng, quá hạn và xử lý thủ công

### Báo vấn đề

`POST /items/{itemId}/return-requests/{returnRequestId}/report-issue` — `200 OK`, Idempotency-Key bắt buộc. Response là `ReturnRequestResponse` đầy đủ, cùng shape với request trong GET detail.

```json
{
  "reason": "Chưa được xác nhận dù đã trả tại kho",
  "note": "Đề nghị kiểm tra với nhân viên nhận máy",
  "mediaIds": [],
  "assignmentIds": ["20000000-0000-4000-8000-000000000001"]
}
```

A hoặc B được báo trên unit còn actionable. Reason bắt buộc <= 500; Note <= 2.000; MediaIds optional tối đa 10, đúng actor upload. Bỏ assignmentIds nghĩa là áp dụng cho toàn bộ unit còn actionable của request. Unit đã confirmed/rejected không báo mới.

Response mẫu sau B báo issue; cũng là shape trong `item.returnRequests`:

```json
{
  "returnRequestId": "30000000-0000-4000-8000-000000000001",
  "itemId": "10000000-0000-4000-8000-000000000001",
  "actualReturnAt": "2026-10-28T08:00:00Z",
  "note": "Đã trả máy thứ nhất tại kho",
  "submittedAt": "2026-10-28T08:05:00Z",
  "status": "Disputed",
  "confirmationDeadlineAt": "2026-10-29T08:05:00Z",
  "isOverdue": false,
  "units": [
    { "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001", "assignmentId": "20000000-0000-4000-8000-000000000001", "mediaIds": [], "closedAt": null, "rejectedAt": null, "status": "Disputed", "isOverdue": false, "overdueNotifiedAt": null }
  ],
  "issues": [
    {
      "issueId": "80000000-0000-4000-8000-000000000001",
      "returnRequestId": "30000000-0000-4000-8000-000000000001",
      "reporterId": "60000000-0000-4000-8000-000000000001",
      "reporterRole": "Renter",
      "reason": "Chưa được xác nhận dù đã trả tại kho",
      "note": "Đề nghị kiểm tra với nhân viên nhận máy",
      "mediaIds": [],
      "reportedAt": "2026-10-28T10:00:00Z",
      "assignmentIds": ["20000000-0000-4000-8000-000000000001"]
    }
  ],
  "resolutions": []
}
```

Issue là lịch sử immutable. A báo thêm thì append report khác với reporterRole `Supplier`, không sửa report của B. Chỉ unit được chọn chuyển Disputed; unit khác của cùng request giữ trạng thái hiện có.

### Status và badge

Unit: `PendingSupplierConfirmation` → `Disputed` → `Confirmed` hoặc `Rejected`. Supplier có thể xác nhận trực tiếp từ pending. Actionable được suy ra bằng `closedAt === null && rejectedAt === null`; FE không gửi field actionable.

Request tổng: còn unit mở và có disputed → `Disputed`; còn unit mở khác → `PendingSupplierConfirmation`; tất cả confirmed → `Confirmed`; tất cả rejected → `Rejected`; đã xử lý hết nhưng kết quả hỗn hợp → `Resolved`.

**Field thực tế là `isOverdue`, không phải `overdueConfirmation`.** Đây là badge độc lập: có thể vừa “Có bất đồng” vừa “Quá hạn xác nhận”. Chỉ còn unit actionable mới overdue hiện tại. `overdueNotifiedAt` là lịch sử worker đã ghi cảnh báo, không quyết định badge.

Tại `now >= confirmationDeadlineAt`, GET trả `isOverdue: true` cho unit actionable và request còn actionable unit, ngay cả khi worker chưa chạy. Worker poll khoảng 60 giây nên `overdueNotifiedAt` có thể còn null. FE refetch tại deadline hoặc khi focus; SignalR cảnh báo không được bảo đảm tới đúng giây hết hạn.

Mock sau đó: advance clock, đặt isOverdue=true; khi giả lập worker thành công mới gán overdueNotifiedAt. Unit được xác nhận/bác thì isOverdue=false, giữ lịch sử timestamp. Không chuyển RentalItem, tạo ReturnRecord hoặc release/refund do hết hạn.

### Admin API — có implementation, Admin UI có thể làm sau

- `GET /admin/return-requests?page=1&pageSize=20&disputed=true&overdue=true`: hai filter kết hợp AND; bỏ filter để không giới hạn theo tiêu chí đó. Có thêm `itemId`, `orderId`. Page tối thiểu 1; pageSize được clamp 1–100.
- `GET /admin/return-requests/{returnRequestId}`: chi tiết xử lý.
- `POST /items/{itemId}/return-requests/{returnRequestId}/resolve`: quyết định có audit và Idempotency-Key.
- ConfirmReceived: chọn assignment, actualReturnAt, inspection từng máy theo Task 3; DAMAGED có mô tả/ảnh do Admin upload. Ghi nhận Admin là actor, không giả mạo Supplier.
- RejectRequest: chọn assignment, DecisionNote bắt buộc; không tạo ReturnRecord hoặc gọi Inventory. RejectedAt khác ClosedAt. B có thể tạo request mới nếu máy vẫn đủ điều kiện.

Các API này yêu cầu user Admin; service token hoặc Supplier/Renter thông thường không được gọi. Không cho UI cập nhật state item tùy ý.

**ConfirmReceived — body**, response `200 OK` là ReturnRequestResponse, không phải RecordReturnResponse:

```json
{
  "decision": "ConfirmReceived",
  "decisionNote": "Đã đối chiếu biên nhận tại kho",
  "assignmentIds": ["20000000-0000-4000-8000-000000000001"],
  "resolutionMediaIds": [],
  "actualReturnAt": "2026-10-28T09:00:00Z",
  "units": [
    { "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001", "condition": "NORMAL", "description": null, "mediaIds": [] }
  ]
}
```

`units` phải khớp chính xác tập assignmentIds, không trùng. `decisionNote` bắt buộc <=2.000 ký tự. Media mức resolution là optional, tối đa 10; không thay thế yêu cầu ảnh từng máy DAMAGED. Thời điểm mock xử lý là `2026-10-28T10:00:00Z`.

**RejectRequest — body** (kịch bản thay thế ConfirmReceived, không áp dụng liên tiếp lên cùng unit đã confirmed):

```json
{
  "decision": "RejectRequest",
  "decisionNote": "Chưa có chứng cứ thiết bị đã được mang trả",
  "assignmentIds": ["20000000-0000-4000-8000-000000000001"],
  "resolutionMediaIds": []
}
```

Sau ConfirmReceived: unit có `status: "Confirmed"`, closedAt khác null, rejectedAt=null. Sau RejectRequest: `status: "Rejected"`, rejectedAt khác null, closedAt=null. Cả hai hết actionable và isOverdue=false; giữ issues cũ. Request chỉ chứa u1 có status tương ứng; request nhiều unit áp dụng quy tắc tổng ở trên.

Append một phần tử vào `resolutions` (ví dụ RejectRequest):

```json
{
  "resolutionId": "90000000-0000-4000-8000-000000000001",
  "returnRequestId": "30000000-0000-4000-8000-000000000001",
  "adminId": "60000000-0000-4000-8000-000000000002",
  "decision": "RejectRequest",
  "decisionNote": "Chưa có chứng cứ thiết bị đã được mang trả",
  "resolutionMediaIds": [],
  "resolvedAt": "2026-10-28T10:00:00Z",
  "assignmentIds": ["20000000-0000-4000-8000-000000000001"]
}
```

Admin detail và resolve response có decisionNote. GET detail dành cho Renter/Supplier trả decisionNote=null, nhưng vẫn có decision, actor, media và thời điểm. Không copy nguyên Admin response vào cache/public view của Renter/Supplier.

Sau reject, B có thể tạo request mới cho cùng assignment nếu canRequestReturn=true; request cũ vẫn Rejected. Nếu A thực nhận sau đó bằng `/returns`, không sửa lịch sử request bị bác. Một lần resolve có một decision; muốn nhận u1 và bác u2 phải gửi hai operation với hai key.

**Admin list — response mẫu**, tại clock `2026-10-29T08:05:00Z`, trước xử lý:

```json
{
  "items": [
    {
      "returnRequestId": "30000000-0000-4000-8000-000000000001",
      "itemId": "10000000-0000-4000-8000-000000000001",
      "orderId": "a0000000-0000-4000-8000-000000000001",
      "orderCode": "DH-2026-MOCK-001",
      "renterId": "60000000-0000-4000-8000-000000000001",
      "supplierId": "60000000-0000-4000-8000-000000000003",
      "submittedAt": "2026-10-28T08:05:00Z",
      "confirmationDeadlineAt": "2026-10-29T08:05:00Z",
      "status": "Disputed",
      "isOverdue": true,
      "totalUnits": 1,
      "actionableUnits": 1,
      "disputedUnits": 1,
      "confirmedUnits": 0,
      "rejectedUnits": 0
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
```

**Admin detail envelope:** `{ request, orderId, orderCode, supplierId, renterId, itemStatus, inventoryUnits }`. `request` dùng ReturnRequestResponse đầy đủ ở trên; `itemStatus` dùng UPPER_SNAKE_CASE. `inventoryUnits` chỉ có trên Admin detail, gồm các assignment thuộc request; phần tử ví dụ sau NORMAL đã được Inventory xác nhận:

```json
{
  "rentalUnitAssignmentId": "20000000-0000-4000-8000-000000000001",
  "assetId": "b0000000-0000-4000-8000-000000000001",
  "allocationId": "c0000000-0000-4000-8000-000000000001",
  "assignmentStatus": "RETURNED_NORMAL",
  "returnRecordId": "70000000-0000-4000-8000-000000000001",
  "returnCondition": "NORMAL",
  "allocationClosedAt": "2026-10-28T10:00:10Z",
  "inventoryDispositionRef": null
}
```

Trước Inventory xác nhận, allocationClosedAt còn null dù assignmentStatus đã RETURNED_NORMAL/RETURNED_DAMAGED. Với DAMAGED thành công, inventoryDispositionRef phải có giá trị. Detail này không có receipt status/error, nên allocationClosedAt=null chỉ đủ hiển thị “Chưa có xác nhận Inventory”, chưa phân biệt pending với rejected.

## 9. Đồng bộ, lỗi và retry

GET nguồn dữ liệu chính:

- B: `/orders/{orderId}`.
- A: `/supplier/sub-orders/{subOrderId}`.
- SignalR chỉ báo cần reload: `RenterOrderChanged`, `SupplierSubOrderChanged`.
- Task 1: HANDOVER_CONFIRMED, HANDOVER_ACKNOWLEDGED, ITEM_CANCELLED/HANDOVER_EXPIRED.
- Task 2: RETURN_REQUESTED. Thu hồi: RETURN_SUBMITTED; Inventory xác nhận: RETURN_RECORDED.
- Task 4: RETURN_DISPUTED, RETURN_CONFIRMATION_OVERDUE, RETURN_RESOLVED. Đây là changeType trong hai typed event hiện có, không phải tên channel SignalR mới.
- Admin ConfirmReceived có thể gửi RETURN_SUBMITTED và RETURN_RESOLVED với cùng version; coalesce refetch, không cần hiện hai toast giống nhau.

Sau mutation reload detail. Khi reconnect/focus tab cũng reload; notification không có replay đảm bảo. So sánh Renter `version`, Supplier `orderVersion` theo đúng resource. Không dùng một global version cho nhiều SubOrder.

Trong lúc chờ handover, có thể polling detail mỗi 5 giây khi màn hình đang mở, dừng sau 60 giây và cho nút “Kiểm tra lại”; timeout UI không đồng nghĩa operation BE thất bại. Không tự gửi lại mutation bằng key mới.

ProblemDetails business error hiện có dùng **title làm mã lỗi**, không mặc định có `code`:

```json
{ "title": "HANDOVER_IN_PROGRESS", "status": 409, "detail": "Yêu cầu bàn giao đang chờ xử lý hoặc đối soát." }
```

Đây là fragment; response có thể thêm type, instance, traceId hoặc errors với model validation.

| Trường hợp | HTTP/mã đã có | Xử lý FE |
| --- | --- | --- |
| Key đổi payload | 409 IDEMPOTENCY_CONFLICT | Không retry mù; tải lại dữ liệu và kiểm tra thao tác trước |
| Request unit đã có yêu cầu mở | 409 RETURN_REQUEST_ALREADY_OPEN | Reload và mở request hiện có |
| Chưa có HandoverRecord | 409 HANDOVER_NOT_RECORDED | Chưa cho B xác nhận đã nhận |
| Bàn giao pending/đối soát | 409 HANDOVER_IN_PROGRESS | Chờ xử lý; không cho hủy hoặc gửi mới |
| Sai trạng thái handover | 409 INVALID_STATUS | Reload item |
| Thời điểm trả tương lai | 400 Return.FutureTime | Sửa thời điểm |
| Máy đã thu hồi | 409 Return.AlreadyReturned | Reload units; bỏ khỏi selection |
| Issue nhắm unit ngoài request | 400 ReturnIssue.AssignmentNotInRequest | Reload request và sửa selection |
| Issue nhắm unit đã xử lý | 409 ReturnIssue.UnitNotActionable | Reload; không cho báo lại trên unit đó |
| Không còn unit actionable để báo issue | 409 ReturnIssue.NoActionableUnits | Hiển thị kết quả xử lý hiện tại |
| Resolve unit ngoài request | 400 ReturnResolution.AssignmentNotInRequest | Sửa selection |
| Resolve unit đã xử lý | 409 ReturnResolution.UnitNotActionable | Reload, không gửi quyết định thứ hai |
| Inspection Admin không khớp assignmentIds | 400 ReturnResolution.UnitsMismatch | Sửa form trước submit |
| DAMAGED thiếu mô tả/ảnh qua Supplier | 400 Return.InvalidEvidence | Yêu cầu bổ sung dữ liệu |
| DAMAGED thiếu mô tả/ảnh qua Admin | 400 Return.DamagedDescriptionRequired / Return.DamagedMediaRequired | Yêu cầu bổ sung dữ liệu |
| Hết phiên / sai quyền / không tìm thấy | 401 / 403 / 404 | Theo auth flow; không lộ dữ liệu item khác |
| Media/dependency tạm lỗi | HTTP theo ProblemDetails, thường 503 | Giữ form/key; cho retry đúng operation |

Với lỗi không nhận diện được, hiển thị thông điệp chung và traceId nếu có. Validate decision bằng whitelist string `ConfirmReceived`/`RejectRequest`, luôn gửi units/media collections đúng kiểu; BE còn lỗi validation được ghi ở mục 11.

## 10. Bộ kịch bản mock FE cần có

Mock server giữ state theo item + assignment + request + idempotency key. Có đồng hồ điều chỉnh được; không dùng timeout thực 24 giờ. Không gọi Kafka/Inventory trực tiếp từ browser.

| Case | Thiết lập / hành động | Kết quả cần thấy |
| --- | --- | --- |
| H01 | A gửi đủ 3 máy, Inventory pending | 202, không có record, item AWAITING_HANDOVER |
| H02 | Cho Inventory xác nhận H01 | IN_RENTAL, handover hiện; B được acknowledgement |
| H03 | Replay handover cùng key | Không thêm record; payload khác trả 409 |
| H04 | A khác gọi; thiếu máy/ảnh; thời điểm tương lai | Thất bại, không đổi state |
| H05 | B acknowledgement hai lần, đổi note lần hai | Giữ thông tin lần đầu, state không đổi |
| H06 | D+1 chưa giao, không pending | B hủy; item Cancelled, chưa khẳng định refund xong |
| H07 | D+1 nhưng bàn giao pending/đối soát | Hủy bị chặn, hiển thị chờ đối soát |
| R01 | B khai trả u1 trong 3 máy | Request mở; item IN_RENTAL; u2/u3 vẫn chọn được |
| R02 | Key mới khai trả u1 lần nữa | 409; replay key cũ trả snapshot |
| R03 | B chọn unit chưa giao/đã nhận lại | Bị từ chối; không tạo request |
| R04 | Request chứa u1,u2; A chỉ nhận u1 | Chỉ u1 ClosedAt; u2 mở; item IN_RENTAL |
| R05 | A nhận NORMAL | Ghi nhận thu hồi; Inventory pending không được hiển thị đã sync |
| R06 | A nhận DAMAGED thiếu mô tả/ảnh | Validation lỗi theo Task 3; không tạo record |
| R07 | A nhận đủ; batch trước có damage + cọc | PENDING_SETTLEMENT, deadline +48h; chưa COMPLETED |
| R08 | A nhận đủ toàn NORMAL hoặc damage không cọc | PENDING_SETTLEMENT, khấu trừ 0; chưa hoàn tất tài chính |
| D01 | A/B report issue trên unit mở | Request/unit Disputed (PascalCase); item/Inventory không đổi |
| D02 | Advance clock tới 24h; còn unit mở | Badge overdue; không auto-confirm |
| D03 | Admin xác nhận u1, bác u2 | u1 Confirmed/ClosedAt; u2 Rejected/RejectedAt; giữ audit |
| D04 | B tạo lại request cho u2 đã bị bác | Được nếu assignment chưa trả và không có request mở khác |
| D05 | Partial confirm trong request disputed | Unit nhận thành Confirmed; unit disputed còn lại giữ Disputed |
| D06 | Đúng deadline nhưng worker chưa chạy | isOverdue=true; overdueNotifiedAt=null; không auto-confirm |
| D07 | Resolve cùng key/payload rồi replay | Trả snapshot cũ; không thêm resolution/ReturnRecord |
| D08 | Supplier/Renter gọi Admin API | 403; không hiển thị dữ liệu quản trị |
| D09 | GET Renter/Supplier sau Admin resolve | DecisionNote=null; không lộ ghi chú quản trị |
| X01 | Mất mạng sau submit; retry key cũ | Trả cùng response, không duplicate |
| X02 | Mất SignalR/reconnect, GET về chậm | Resync; response cũ không ghi đè version mới |

Tiêu chí FE hoàn thành mock: đủ các màn hình/nhánh trên; phân biệt dữ liệu “B khai báo”, “A/Admin thực nhận”, “Inventory xác nhận”; dùng đúng case enum và tên field theo bản tích hợp. Trước kết nối staging, đối chiếu Swagger và phiên bản deploy trong mục 1.

## 11. Điểm còn mở sau review ảnh hưởng cách FE tích hợp

- Worker còn lỗi rollback khi nhiều request cùng order: trường hợp enqueue cảnh báo đầu lỗi, xử lý request sau thành công có thể lưu dấu đã cảnh báo sai. FE không dùng overdueNotifiedAt để kết luận người dùng đã nhận notification; badge dựa isOverdue từ GET. Mock luồng chuẩn vẫn giữ cảnh báo idempotent; lỗi này cần BE sửa, không mô phỏng như hành vi mong muốn.
- Admin resolve thiếu guard decision không hợp lệ/null collections. Mock dữ liệu không hợp lệ nên trả 400 theo kỳ vọng; BE hiện có thể trả 500 ở các payload này. Không đổi thành success hoặc auto-retry bằng key mới. FE validation hỗ trợ UX, không thay thế bản sửa BE.
- Admin/Supplier hiện còn hai đoạn workflow thu hồi riêng; BE sẽ cần hợp nhất. FE tiếp tục dùng hai contract đã ghi rõ, không dựa vào cấu trúc implementation.
- Lỗi event Rental → Recommendation và exit code maintenance nằm ngoài flow HTTP FE. Không suy ra bản release đã đạt kiểm thử từ việc mock hoạt động; các vấn đề này vẫn đang chờ xử lý.
