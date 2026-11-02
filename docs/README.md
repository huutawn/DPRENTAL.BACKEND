# Tài liệu Kỹ thuật ReviewService (Contract Freeze trước DPR-274)

Tài liệu này cung cấp toàn bộ hướng dẫn kiến trúc, thiết kế cơ sở dữ liệu, hợp đồng API v1, tích hợp sự kiện Kafka, fixtures kiểm thử và ma trận phụ thuộc liên dịch vụ cho `ReviewService` trong hệ sinh thái DPRENTAL.

---

## 1. Mục lục Tài liệu Thiết kế, Hợp đồng và Fixtures

| Tài liệu / Thư mục | Nội dung chính | Trạng thái |
| :--- | :--- | :--- |
| **[docs/ARCHITECTURE.md](ARCHITECTURE.md)** | Ranh giới hệ thống, mô hình xác thực `X-Api-Key` nội bộ không qua Bearer JWT, Zero-Trust với client input, Gateway route blocking, luồng dữ liệu hai nhánh (Dual-path), giao thức đối soát Full-Window Sweep, ma trận phụ thuộc và bản đồ bằng chứng. | `Freeze-Ready` |
| **[docs/contracts/review-api-v1.md](contracts/review-api-v1.md)** | Đặc tả hợp đồng API v1 (12 endpoints bao gồm `GET /api/v1/reviews/{reviewId}`), DTO Allowlist, vòng đời hai trục (`authorState` $\times$ `moderationState`), cơ chế Idempotency fingerprinting (`SHA-256`), Strong ETag/`If-Match` (`428`/`412`), chuẩn đếm Unicode Scalar Values (1.000 runes), và ProblemDetails fixtures. | `Freeze-Ready` |
| **[docs/contracts/review-eligibility-v1.md](contracts/review-eligibility-v1.md)** | Điều kiện hoàn tất nghiệp vụ tại RentalService (3 điều kiện tiên quyết), hợp đồng Snapshot nội bộ tối thiểu (bỏ `orderCode`), `RentalItemStatus` literals chuẩn hóa, xử lý timeout khấu trừ 48h (hoàn cọc 100%), xử lý lệch dữ liệu (Mismatch handling), công thức deadline UTC exclusive và bảng ca biên mili-giây. | `Freeze-Ready` |
| **[docs/contracts/review-events-v1.md](contracts/review-events-v1.md)** | Đặc tả Kafka Envelope v1 trên topic `rental.events.v1`, consumer group riêng `review-service-rental-events`, Transactional Inbox deduplication, HTTP hydration trước khi mở DB transaction, retry hữu hạn 3 lần/DLQ (`rental.events.review.dlq`), tính độc lập `orderVersion`, và endpoint đối soát định kỳ toàn cửa sổ 30 ngày. | `Freeze-Ready` |
| **[docs/contracts/review-visibility-v1.md](contracts/review-visibility-v1.md)** | Ma trận hiển thị cho 4 tổ hợp trạng thái hai trục, chính sách bảo vệ quyền riêng tư người thuê (Private Renter Reviews: chỉ author, subject, admin đọc; không public score hay summary người thuê), tác giả sửa/rút review bị Admin ẩn, khôi phục review đã rút, và tính toán Authoritative Rating Summary (count=0 trả về `null`). | `Freeze-Ready` |
| **[docs/erd.md](erd.md)** | Sơ đồ Mermaid ERD, định nghĩa bảng PostgreSQL (DDL cho 7 bảng), 2 trục `author_state` & `moderation_state`, bỏ cờ đã review trên projection (dùng `NOT EXISTS` sang `reviews`), khóa ngoại nội bộ, ràng buộc duy nhất `UNIQUE (item_id, direction)`, và khóa lạc quan `version BIGINT` với Strong ETag. | `Freeze-Ready` |
| **[docs/fixtures/manifest.json](fixtures/manifest.json)** | Danh mục manifest chính thức quản lý 3 nhóm fixtures: Invalid Syntax (400 parser), Invalid DTO (validation), Business Failures (ProblemDetails), và Valid Requests/Responses cho toàn bộ 4 tổ hợp trạng thái. | `Freeze-Ready` |
| **[scripts/tests/validate-docs.py](../scripts/tests/validate-docs.py)** | Công cụ kiểm tra tự động toàn bộ JSON fixtures, manifest, relative links, whitespace, CRLF và tính nhất quán chéo giữa các tài liệu. | `Verified` |

---

## 2. Ranh giới các lớp trong ReviewService Skeleton

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility Unicode Runes) | Nghiệp vụ đặc thù domain khác |
| `Domain` | Entities, repository contracts, EF Core DbContext và repositories theo bố cục RentalService | HTTP pipeline, controller |
| `Application` | DTOs, Services, Mapping, Kafka Consumers, HTTP Clients và điều phối domain | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, middleware, filters và DI/composition root | Business logic trực tiếp |

Domain sở hữu repository contracts, EF Core DbContext và repository implementations theo cấu trúc RentalService. API đăng ký persistence qua `AddReviewServiceDomainRepositories`.

---

## 3. Đặc tả Chương trình Kiểm chứng Nghiệp vụ (Acceptance Test Specifications)

Khi triển khai code thực tế tại DPR-274, bộ test suite bắt buộc phải bao phủ đầy đủ 6 nhóm kịch bản:

### 3.1 Nhóm 1: Hoàn tất nghiệp vụ và Luồng Rental Completion
- **Kịch bản 1.1 (Normal return):** Trả máy bình thường $\rightarrow$ Khấu trừ bằng 0 $\rightarrow$ Inventory đóng allocation $\rightarrow$ Payment xác nhận quyết toán $\rightarrow$ `completedAt` được persist $\rightarrow$ Đủ điều kiện đánh giá.
- **Kịch bản 1.2 (Deduction timeout 48h):** Sau 48h trả máy không có đề xuất khấu trừ từ supplier $\rightarrow$ Worker chốt `FinalDeductionAmount = 0m` (hoàn cọc 100% cho renter) $\rightarrow$ Quyết toán $\rightarrow$ `completedAt` được thiết lập $\rightarrow$ Đủ điều kiện đánh giá bình thường.
- **Kịch bản 1.3 (Deduction dispute):** Trả máy có tranh chấp $\rightarrow$ Mục thuê chờ ở `PendingSettlement` $\rightarrow$ Giải quyết tranh chấp $\rightarrow$ Payment quyết toán $\rightarrow$ `completedAt` thiết lập tại thời điểm giải ngân mới $\rightarrow$ Kích hoạt hạn 30 ngày từ thời điểm mới.
- **Kịch bản 1.4 (Cancelled item):** Đơn hoặc mục thuê bị hủy (`Cancelled`/`Rejected`) $\rightarrow$ `completedAt == null` $\rightarrow$ Từ chối tạo review với mã lỗi `RENTAL_ITEM_NOT_COMPLETED`.
- **Kịch bản 1.5 (Prohibited inferences):** Mục thuê chỉ mới có `ReturnsCompletedAt` nhưng chưa hoàn tất quyết toán $\rightarrow$ API từ chối tạo review.

### 3.2 Nhóm 2: Xác thực `X-Api-Key` và Bảo vệ Gateway
- **Kịch bản 2.1 (Valid key without Bearer):** Gọi `GET /internal/.../review-snapshot` với `X-Api-Key` đúng $\rightarrow$ Trả về `200 OK` mà không cần token Bearer.
- **Kịch bản 2.2 (Missing / Invalid / Multiple keys):** Thiếu header `X-Api-Key`, truyền sai giá trị, hoặc truyền nhiều header $\rightarrow$ RentalService trả về `401 Unauthorized` $\rightarrow$ ReviewService ánh xạ thành `502 Bad Gateway` (`RENTAL_SERVICE_AUTHENTICATION_FAILED`).
- **Kịch bản 2.3 (Bearer cannot substitute for API key):** Gửi Bearer JWT nhưng không có `X-Api-Key` tới endpoint snapshot internal $\rightarrow$ Trả về `401 Unauthorized`.
- **Kịch bản 2.4 (Gateway blocks internal):** Gọi `/internal/...` từ Internet qua API Gateway $\rightarrow$ Bị chặn bởi `ServiceOnlyRouteBlockingMiddleware` với mã `403 Forbidden` hoặc `404 Not Found`.
- **Kịch bản 2.5 (Header Sanitization):** Client gửi kèm `X-Api-Key` qua Gateway $\rightarrow$ Gateway tự động loại bỏ header trước khi proxy; không cho phép client mạo danh service.

### 3.3 Nhóm 3: Snapshot, Phân quyền và Bảo mật Renter
- **Kịch bản 3.1 (Participant mapping):** ReviewService lấy `RenterId` và `SupplierId` từ Snapshot; từ chối người gọi không phải là bên tham gia với mã `403 Forbidden` (`ACTOR_NOT_PARTICIPANT`).
- **Kịch bản 3.2 (Zero trust client input):** Request body truyền `authorId` hoặc `subjectId` giả mạo $\rightarrow$ Bị bỏ qua hoặc từ chối; hệ thống luôn lấy từ JWT và Snapshot.
- **Kịch bản 3.3 (Renter privacy):** Review chiều `supplier_to_renter` chỉ có thể truy vấn bởi chính Supplier tác giả, Renter đối tượng và Admin (`GET /api/v1/reviews/{reviewId}`). Người dùng thứ ba gọi API nhận `404 Not Found`.
- **Kịch bản 3.4 (No public renter summary):** Tuyệt đối không cung cấp điểm tổng hợp hoặc danh sách public của người thuê trên bất kỳ endpoint nào.

### 3.4 Nhóm 4: Bất đồng bộ Kafka, Hydration, Deduplication và Replay
- **Kịch bản 4.1 (Inbox deduplication):** Gửi cùng một sự kiện `RentalItemCompleted` (trùng `messageId`) nhiều lần $\rightarrow$ Chỉ xử lý ghi projection và hydrate 1 lần duy nhất; các lần sau no-op và commit offset.
- **Kịch bản 4.2 (Hydration success before DB transaction):** Consumer nhận event $\rightarrow$ Gọi Snapshot HTTP hydrate `supplierId` trước khi mở DB transaction $\rightarrow$ Ghi `is_eligible = true` vào `review_projections`.
- **Kịch bản 4.3 (Hydration failure & Retry):** Gọi Rental Snapshot bị lỗi mạng/502/504 $\rightarrow$ Retry tối đa 3 lần với backoff 1s/2s/4s có jitter $\rightarrow$ Mục thuê giữ `is_eligible = false` (chưa hiển thị trên pending list).
- **Kịch bản 4.4 (Poison message & DLQ):** Event sai schema hoặc không thể hydrate sau 3 lần retry $\rightarrow$ Đẩy vào topic `rental.events.review.dlq` $\rightarrow$ Chỉ commit offset topic chính sau khi nhận broker ACK từ DLQ.
- **Kịch bản 4.5 (OrderVersion invariance):** Trong đơn hàng có 2 item, item 2 hoàn tất với `orderVersion = 7` $\rightarrow$ Không làm mất hoặc loại bỏ event hoàn tất của item 1 (`orderVersion = 3`).

### 3.5 Nhóm 5: Dữ liệu Lịch sử, Full-Window Sweep và Replay
- **Kịch bản 5.1 (Missing timestamp):** Mục thuê lịch sử không có timestamp quyết toán chính xác $\rightarrow$ `completedAt = null` $\rightarrow$ Không đủ điều kiện đánh giá.
- **Kịch bản 5.2 (Replay invariance):** Chạy lại replay Kafka hoặc chạy reconciliation sweep cho mục thuê đã hoàn tất trong quá khứ $\rightarrow$ Giữ nguyên `completedAt` gốc, không reset deadline; nếu đã quá 30 ngày thì lập tức hết hạn.
- **Kịch bản 5.3 (Backdated persistence captured by sweep):** Mục thuê được persist muộn tại RentalService với `completedAt` cũ trong vòng 30 ngày $\rightarrow$ Vòng quét sweep kế tiếp sau 5 phút quét lại toàn bộ window $\rightarrow$ Tự động bắt và đưa vào `review_projections`.
- **Kịch bản 5.4 (Sweep checkpoint recovery):** Tiến trình sweep crash giữa trang 2 $\rightarrow$ Lần chạy sau đọc checkpoint từ `reconciliation_checkpoints` và tiếp tục phân trang an toàn.

### 3.6 Nhóm 6: Ca biên UTC, Unicode, Idempotency, ETag và 4 Tổ hợp Vòng đời
- **Kịch bản 6.1 (UTC exclusive boundary):**
  - Gửi lúc `completedAt + 30 ngày - 1ms` $\rightarrow$ Thành công `201 Created`.
  - Gửi lúc `completedAt + 30 ngày` $\rightarrow$ Từ chối `422 Unprocessable Entity` (`REVIEW_CREATION_WINDOW_EXPIRED`).
- **Kịch bản 6.2 (Unicode scalar counting):**
  - Bình luận có 1.000 ký tự tiếng Việt có dấu và emoji ghép $\rightarrow$ Hợp lệ.
  - Bình luận có 1.001 runes $\rightarrow$ Từ chối `400 Bad Request` (`COMMENT_EXCEEDS_MAX_LENGTH`).
- **Kịch bản 6.3 (Idempotency conflict & replay):**
  - Gửi lại request với cùng `Idempotency-Key` và cùng fingerprint $\rightarrow$ Trả về kết quả cached ban đầu (`201/200`) kèm ETag đã lưu.
  - Gửi lại với cùng `Idempotency-Key` nhưng đổi rating hoặc body $\rightarrow$ Trả về `409 Conflict` (`IDEMPOTENCY_KEY_REUSE_MISMATCH`).
- **Kịch bản 6.4 (Strong ETag & If-Match):**
  - Thiếu header `If-Match` trên mutation $\rightarrow$ Trả về `428 Precondition Required`.
  - Gửi `If-Match` sai phiên bản (ví dụ `"1"` thay vì `"2"`) $\rightarrow$ Trả về `412 Precondition Failed`.
  - Wildcard `*` trên mutation bị từ chối.
- **Kịch bản 6.5 (Bốn tổ hợp vòng đời):**
  - `Active` + `Visible`: Hiển thị công khai và tính điểm summary.
  - `Active` + `Hidden`: Tác giả vẫn được phép sửa trong edit window (sửa xong vẫn giữ `Hidden`); tác giả được quyền rút review.
  - `Withdrawn` + `Visible`: Đã rút, không hiển thị public.
  - `Withdrawn` + `Hidden`: Admin restore chỉ chuyển `moderationState = 'Visible'`, review vẫn là `Withdrawn` và không xuất hiện trở lại.
- **Kịch bản 6.6 (Empty summary):**
  - Thiết bị chưa có review nào $\rightarrow$ `reviewCount = 0` và `averageRating = null` (không trả về 0.0).

---

## 4. Bản đồ Bằng chứng Kỹ thuật (Evidence Map)

| Danh mục | Chi tiết kỹ thuật | Trạng thái xác minh | Ghi chú nguồn / Căn cứ |
| :--- | :--- | :--- | :--- |
| **Rental Commit HEAD** | `761db851c3dbb594c4a155d3bcbaf1c7722bd384` | `[confirmed-by-source]` | Commit HEAD trên nhánh chính RentalService |
| **Review Commit HEAD** | `cf6efe5e431ffebfa5ef12e5a80a2eea3c85dd60` | `[confirmed-by-source]` | Commit HEAD trên nhánh chính ReviewService |
| **Working Tree State** | Working tree có 9 unstaged deletions (`.gitkeep`) và các untracked folders từ template | `[confirmed-by-source]` | Giữ nguyên các chỉnh sửa ngoài phạm vi; không can thiệp code |
| **Rental Middleware** | `InternalApiKeyMiddleware.cs` | `[confirmed-by-source]` | Xác thực `X-Api-Key` bằng `FixedTimeEquals`, gán `InternalApiKeyVerified = true` |
| **Rental Controller Auth**| `RentalInternalOperationsController.cs` | `[confirmed-by-source]` | Controller internal hiện dùng `[Authorize]` ở cấp lớp, cần policy mới cho snapshot |
| **Rental Status Literals** | `RentalItemStatus` enum (0..8) | `[confirmed-by-source]` | `PendingPayment`, `PendingSupplierConfirmation`, `AwaitingHandover`, `InRental`, `PendingSettlement`, `Completed`, `Rejected`, `Expired`, `Cancelled` |
| **Rental Event Topic** | `KafkaContractSpec.RentalEvents = "rental.events.v1"` | `[confirmed-by-source]` | Topic thực tế trong source là `rental.events.v1` |
| **Rental Envelope Serialization** | `KafkaMessageEnvelope<TPayload>` | `[confirmed-by-source]` | `OrganizationId` có cờ `JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)` |
| **Deduction Timeout 48h** | `NeedsDeductionTimeout` trong `RentalItem.Deductions.cs` | `[confirmed-by-source]` | Quá 48h không có đề xuất, worker chốt `FinalDeductionAmount = 0m` (hoàn cọc 100% cho renter) |
| **Review DLQ Topic** | `rental.events.review.dlq` | `[design-decision]` | Quyết định thiết kế mới cho ReviewService, chưa có trong source |
| **Vòng đời 2 trục** | `authorState` (`Active`/`Withdrawn`) $\times$ `moderationState` (`Visible`/`Hidden`) | `[design-decision]` | Thiết kế giải quyết triệt để xung đột giữa quyền tác giả và kiểm duyệt |
| **Concurrency Token** | `BIGINT version` tăng rõ ràng bởi Application + Strong ETag `"1"` | `[design-decision]` | Bỏ `xmin`, bắt buộc `If-Match` cho mọi mutation, thiếu trả `428`, sai trả `412` |
| **Idempotency Key & Fingerprint** | `SHA-256(actorId + method + canonicalRoute + body + ifMatch)` | `[design-decision]` | Nhận diện request trùng lặp chính xác, commit atomic cùng mutation |
| **Full-Window Sweep** | Quét toàn bộ cửa sổ 30 ngày mỗi 5 phút, keyset theo `itemId` | `[design-decision]` | Đảm bảo bắt trọn các mục hoàn tất muộn (backdated persistence) |
| **Quy định 30 ngày/7 ngày**| Deadline tạo & sửa UTC exclusive | `[stakeholder-approved]` | Quyết định stakeholder phiên trước, so sánh UTC exclusive |
| **Đánh giá hai chiều** | `renter_to_supplier_listing` & `supplier_to_renter` | `[stakeholder-approved]` | Quyết định stakeholder phiên trước; rating 1–5, comment $\le$ 1.000 Unicode runes |
| **Quyền riêng tư Renter** | Review nhắm renter chỉ tác giả, đối tượng và Admin thấy | `[stakeholder-approved]` | Tuyệt đối không public điểm hoặc danh sách review người thuê |
| **Snapshot Endpoint** | `GET /internal/orders/{orderId}/items/{itemId}/review-snapshot` | `[unimplemented-dependency]` | Cần bổ sung vào RentalService phục vụ ReviewService |
| **Reconciliation Endpoint**| `GET /internal/orders/review-reconciliation` | `[unimplemented-dependency]` | Cần bổ sung vào RentalService phục vụ full-window sweep |

---

## 5. Ma trận Phụ thuộc và Bàn giao Liên Dịch vụ (Cross-Service Dependency Matrix)

| Bên liên quan | Team sở hữu | Thay đổi cần bàn giao | Trạng thái triển khai | Bằng chứng nghiệm thu (Acceptance Evidence) |
| :--- | :--- | :--- | :--- | :--- |
| **RentalService** | Rental Core Team | Endpoint snapshot `GET /internal/orders/{orderId}/items/{itemId}/review-snapshot` với policy API-key-only | `[unimplemented-dependency]` | Contract test kiểm tra `X-Api-Key` hợp lệ trả `200 OK` không cần Bearer; sai key trả `401`. |
| **RentalService** | Rental Core Team | Endpoint đối soát `GET /internal/orders/review-reconciliation` với phân trang keyset theo `itemId` | `[unimplemented-dependency]` | Integration test trả về danh sách item `Completed` trong cửa sổ 30 ngày kèm `nextCursor`. |
| **RentalService** | Rental Core Team | Lưu trữ bền vững `completedAt` khi thanh toán quyết toán và phát cùng giá trị qua Outbox | `[confirmed-by-source: RentalItem.Settlement.cs]` | Event `RentalItemCompleted` mang `completedAt` trùng khớp với cơ sở dữ liệu. |
| **API Gateway** | Gateway Team | Cấu hình Ocelot cho các route public của ReviewService; chặn triệt để `/internal/*` | `[confirmed-by-source: ServiceOnlyRouteBlockingMiddleware.cs]` | Test gọi `/internal/reviews/...` qua Gateway bị chặn `403 Forbidden` hoặc `404 Not Found`. |
| **AuthService** | Auth Team | Duy trì phát hành JWT chứa claim `sub` (UserId) và `roles` (Admin) | `[confirmed-by-source: AuthRentalContractAndPolicyTests.cs]` | Token verification qua JWKS hoạt động ổn định. |
| **ReviewService**| Review Team | Triển khai use cases, DTOs, ERD, Inbox deduplication, HTTP client, và tests | Chuẩn bị triển khai tại **DPR-274** | Toàn bộ 6 nhóm Acceptance Test Specifications chạy pass. |

---

## 6. Tiêu chí Đóng băng Hợp đồng (Freeze-Ready Declaration)

Hệ thống ReviewService đạt điều kiện **Freeze-Ready trước DPR-274** dựa trên các căn cứ nghiệm thu sau:

1. **Không còn quyết định chặn Schema hoặc API:**
   - Đã thống nhất mô hình vòng đời hai trục (`authorState` $\times$ `moderationState`).
   - Đã chốt duy nhất một concurrency token `BIGINT version` đi kèm Strong ETag `"1"` và mã lỗi `428`/`412`.
   - Đã chốt DTO snapshot tối thiểu, loại bỏ `orderCode`, khóa cứng literals `RentalItemStatus`.
   - Đã chốt giao thức đối soát định kỳ Full-Window Sweep 5 phút/lần.
2. **Kiểm tra tự động hoàn tất 100%:**
   - Công cụ `python3 scripts/tests/validate-docs.py` đã kiểm tra toàn bộ 25 fixtures JSON, manifest, relative links, whitespace, CRLF và tính nhất quán chéo $\rightarrow$ **0 cảnh báo, 0 lỗi**.
   - Lệnh `git diff --check` thực thi sạch sẽ, không có lỗi định dạng hay khoảng trắng thừa.
3. **Phân định ranh giới rõ ràng:**
   - Mọi dependency chưa triển khai tại RentalService và Gateway được ghi nhận minh bạch kèm team sở hữu và tiêu chí nghiệm thu.
   - **Tuyên bố ranh giới:** Việc công bố Contract Freeze xác nhận thiết kế schema/API đã khép kín và sẵn sàng để viết code; không đồng nghĩa với việc tích hợp end-to-end đã hoạt động trên môi trường runtime.
