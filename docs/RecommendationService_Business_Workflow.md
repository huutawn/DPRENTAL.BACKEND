# RecommendationService — Business Workflow

## 1. Mục tiêu nghiệp vụ

RecommendationService hỗ trợ cá nhân hóa nội dung thiết bị được hiển thị cho người dùng dựa trên hành vi sử dụng hệ thống.

Trong Phase 1, hệ thống **không sử dụng Machine Learning hoặc Deep Learning**. Kết quả gợi ý được xác định bằng các quy tắc và điểm số từ hành vi thực tế của người dùng.

> **Nguồn chuẩn kỹ thuật (Single Source of Truth)**: Toàn bộ bảng điểm, trọng số hành vi, format JSON envelope và DDL được định nghĩa chuẩn hóa và đóng băng tại [`docs/contracts/recommendation-v1.md`](contracts/recommendation-v1.md).

RecommendationService tập trung vào hai bài toán chính:

1. **Người dùng đã đăng nhập**
   - Xác định người dùng đang quan tâm đến những `EquipmentType` nào.
   - Xếp hạng mức độ quan tâm giữa các loại thiết bị.
   - Sử dụng kết quả này để ưu tiên các thiết bị phù hợp hơn khi hiển thị.

2. **Người dùng chưa đăng nhập**
   - Không tạo hồ sơ sở thích cá nhân.
   - Hiển thị các thiết bị phổ biến dựa trên mức độ được xem, thêm vào giỏ và thuê thực tế.

---

## 2. Phạm vi Phase 1

Phase 1 hỗ trợ các hành vi sau:

- Tìm kiếm thiết bị.
- Xem chi tiết thiết bị.
- Thêm thiết bị vào giỏ thuê.
- Thanh toán thành công cho thiết bị thuê.
- Hoàn tất một lượt thuê.
- Xác định mức độ quan tâm của người dùng theo `EquipmentType`.
- Xác định mức độ phổ biến của từng thiết bị.
- Trả kết quả gợi ý cho người dùng đã đăng nhập.
- Trả kết quả phổ biến cho người dùng chưa đăng nhập.

Phase 1 chưa xử lý:

- Gợi ý theo người dùng tương tự.
- Collaborative Filtering.
- Machine Learning / Deep Learning.
- Embedding hoặc Vector Search.
- Phân tích nội dung tự do của từ khóa tìm kiếm.
- Cá nhân hóa cho anonymous user bằng session.
- Negative feedback như dislike hoặc “không quan tâm”.
- Time-decay phức tạp.
- A/B testing recommendation.

---

## 3. Đối tượng nghiệp vụ

### 3.1. Người dùng đã đăng nhập

Người dùng đã đăng nhập được xác định bằng `UserId`.

Hệ thống theo dõi các hành vi liên quan đến thiết bị để hình thành hồ sơ sở thích theo `EquipmentType`.

Ví dụ:

```text
User A
├── xem Sony A7 IV
├── xem Canon R6
├── thêm Sony A7 IV vào giỏ
└── thuê Sony A7 IV thành công

→ Người dùng có mức quan tâm cao đối với:
   Mirrorless Camera
```

Recommendation không coi một Listing cụ thể là sở thích lâu dài của người dùng.

Mục tiêu chính là xác định:

```text
User
→ quan tâm EquipmentType nào
→ mức độ quan tâm bao nhiêu
```

### 3.2. Người dùng chưa đăng nhập

Anonymous user không có hồ sơ cá nhân.

Hệ thống không tạo `SessionId` trong Phase 1.

Kết quả hiển thị dựa trên mức độ phổ biến chung của thiết bị.

Ví dụ:

```text
Listing A → được thuê nhiều
Listing B → được xem nhiều
Listing C → thường được thêm vào giỏ
```

Các Listing có tín hiệu hành vi mạnh hơn được ưu tiên cao hơn.

---

## 4. Các hành vi được ghi nhận

### 4.1. SearchPerformed

Được ghi nhận khi người dùng thực hiện tìm kiếm thiết bị.

Thông tin nghiệp vụ quan trọng:

- UserId nếu người dùng đã đăng nhập.
- Từ khóa tìm kiếm.
- EquipmentType được chọn nếu có.
- Các bộ lọc liên quan nếu có.
- Thời điểm tìm kiếm.

Quy tắc:

```text
Search có EquipmentTypeId được chỉ định trực tiếp
→ tăng mức quan tâm EquipmentType đó (+1)

Search chỉ có raw keyword hoặc category đơn thuần
→ không suy diễn thành EquipmentTypeId trong Phase 1
→ không tăng interest score
```
Search là tín hiệu yếu vì người dùng có thể chỉ đang khảo sát.

### 4.2. ListingViewed

Được ghi nhận khi người dùng mở trang chi tiết của một thiết bị.

Ví dụ:

```text
User A
→ xem Sony A7 IV
→ EquipmentType = Mirrorless Camera
```

Nếu người dùng đã đăng nhập:

```text
Mirrorless Camera
→ tăng interest score
```

Nếu anonymous:

```text
Sony A7 IV
→ tăng popularity
```

View là tín hiệu rõ hơn Search nhưng vẫn chưa chứng minh ý định thuê.

### 4.3. CartItemAdded

Được ghi nhận khi thiết bị được thêm thành công vào giỏ thuê.

Ví dụ:

```text
User A
→ thêm Sony A7 IV vào giỏ
→ EquipmentType = Mirrorless Camera
```

Hành vi này có trọng số cao hơn View vì thể hiện ý định thuê rõ ràng hơn.

Nếu thao tác thêm giỏ không thành công thì không ghi nhận hành vi này.

### 4.4. RentalItemPaid

Được ghi nhận khi khoản thanh toán tương ứng với thiết bị thuê đã được xác nhận thành công.

Ví dụ:

```text
User A
→ đã thanh toán RentalItem của Sony A7 IV

Mirrorless Camera
→ tăng mạnh interest score
```

Đây là một trong những tín hiệu mạnh nhất vì người dùng đã thực sự trả tiền để thuê thiết bị.

### 4.5. RentalItemCompleted

Được ghi nhận khi vòng đời thuê của một RentalItem đã hoàn tất.

Ví dụ:

```text
Sony A7 IV
→ đã thuê
→ đã sử dụng
→ đã hoàn trả
→ RentalItem Completed
```

Đây là tín hiệu mạnh vì phản ánh hành vi thuê thực tế đã hoàn thành.

---

## 5. Mức độ quan trọng của hành vi

Phase 1 sử dụng điểm số theo quy tắc.

| Hành vi | Điểm |
|---|---:|
| Search theo EquipmentType | +1 |
| Xem Listing | +2 |
| Thêm vào giỏ | +5 |
| Thanh toán RentalItem | +8 |
| Hoàn tất RentalItem | +10 |

Nguyên tắc nghiệp vụ:

```text
Completed Rental
>
Paid Rental
>
Cart
>
View
>
Search
```

Các giá trị trên là mức khởi đầu và có thể điều chỉnh sau khi kiểm thử.

---

## 6. Hồ sơ sở thích của người dùng

Mỗi người dùng đã đăng nhập có thể có điểm quan tâm cho nhiều `EquipmentType`.

Ví dụ:

```text
User A

Mirrorless Camera   92
Lens                67
Gimbal              40
Tripod              18
```

Điểm này được hình thành từ toàn bộ hành vi của người dùng.

Ví dụ:

```text
Mirrorless Camera

2 lần Search     = 2 × 1  = 2
6 lần View       = 6 × 2  = 12
3 lần Add Cart   = 3 × 5  = 15
2 lần Paid       = 2 × 8  = 16
3 lần Completed  = 3 × 10 = 30

Tổng = 75
```

Điểm càng cao thì người dùng càng được xem là quan tâm đến EquipmentType đó.

---

## 7. Quy trình cá nhân hóa cho người dùng đã đăng nhập

Khi cần hiển thị danh sách gợi ý:

```text
User
↓
xác định các EquipmentType được quan tâm nhiều nhất
↓
xếp hạng EquipmentType theo InterestScore
↓
ưu tiên Listing thuộc các EquipmentType đó
↓
lọc Listing không còn phù hợp
↓
trả danh sách cuối cùng
```

Ví dụ:

```text
Camera    90
Lens      70
Gimbal    30
```

Kết quả có thể ưu tiên:

```text
1. Camera Listing
2. Camera Listing
3. Lens Listing
4. Camera Listing
5. Lens Listing
6. Gimbal Listing
```

Không bắt buộc số lượng Listing giữa các EquipmentType phải bằng nhau.

---

## 8. Popularity của Listing

Ngoài sở thích theo EquipmentType, hệ thống theo dõi mức độ phổ biến của từng Listing.

| Hành vi | Điểm popularity |
|---|---:|
| View | +2 |
| Add Cart | +5 |
| Paid | +8 |
| Completed | +10 |

Bảng điểm popularity chuẩn đồng bộ hoàn toàn với bảng trọng số tại `docs/contracts/recommendation-v1.md`.

Ví dụ:

```text
Sony A7 IV

100 Views       = 200 (100 × 2)
20 Add Cart     = 100 (20 × 5)
10 Paid         = 80  (10 × 8)
8 Completed     = 80  (8 × 10)

PopularityScore = 460
```

Listing có điểm cao hơn được coi là phổ biến hơn.

---

## 9. Quy trình anonymous recommendation

Anonymous user không có UserId nên không có hồ sơ sở thích.

Workflow:

```text
Anonymous user
↓
lấy danh sách Listing có PopularityScore cao
↓
lọc Listing không còn được phép hiển thị
↓
trả danh sách phổ biến
```

Anonymous user không cần có lịch sử cá nhân.

---

## 10. Người dùng mới đăng nhập nhưng chưa có lịch sử

Nếu user đã đăng nhập nhưng chưa có hành vi đủ để hình thành sở thích:

```text
Authenticated user
↓
không có EquipmentType interest
↓
fallback sang global popularity
```

Sau khi có hành vi, kết quả dần chuyển sang cá nhân hóa.

---

## 11. Xử lý Listing không còn hợp lệ

Recommendation chỉ thể hiện mức độ quan tâm hoặc popularity.

Một Listing được gợi ý có thể sau đó:

- Bị ẩn.
- Không còn Active.
- Không còn nhận booking.
- Không còn phù hợp với bộ lọc hiện tại.
- Không còn đủ số lượng khả dụng.

Nguyên tắc:

```text
RecommendationScore
≠
quyền được hiển thị
```

Listing chỉ được trả nếu vẫn thỏa các quy tắc nghiệp vụ hiện tại.

---

## 12. Recommendation và Availability

Recommendation không quyết định thiết bị còn khả dụng hay không.

Ví dụ:

```text
Sony A7 IV
Recommendation Score = 95
```

nhưng user chọn:

```text
01/11 → 03/11
Quantity = 2
```

và không còn đủ thiết bị thì Listing đó có thể bị loại khỏi kết quả.

Recommendation chỉ trả lời:

> Người dùng có khả năng quan tâm Listing hoặc EquipmentType nào?

Recommendation không trả lời:

> Listing này chắc chắn thuê được trong khoảng thời gian nào?

---

## 13. Recommendation và Search

Search và Recommendation có mục tiêu khác nhau.

Search trả lời:

> Thiết bị nào phù hợp với từ khóa và filter hiện tại?

Recommendation trả lời:

> Trong những lựa chọn phù hợp, cái nào có khả năng phù hợp người dùng hơn?

Ví dụ user search:

```text
"máy khoan"
```

nhưng lịch sử chủ yếu là Camera thì không được trả Camera thay cho máy khoan.

Workflow:

```text
Search intent
↓
xác định candidate phù hợp
↓
Recommendation chỉ hỗ trợ ưu tiên candidate
```

Trong Phase 1, cá nhân hóa kết quả search có thể chưa cần áp dụng; Search chỉ cần đóng vai trò behavioral signal.

---

## 14. Recommendation trên trang Home

Home là nơi Recommendation có giá trị cao nhất trong Phase 1.

### Logged-in

Hiển thị nhóm:

```text
Dành cho bạn
```

dựa trên EquipmentType interest.

### Anonymous

Hiển thị:

```text
Thiết bị phổ biến
```

dựa trên Listing popularity.

---

## 15. Không tạo anonymous profile trong Phase 1

Anonymous interaction vẫn có thể dùng để tăng global popularity.

Ví dụ:

```text
Anonymous xem Sony A7 IV
→ Listing popularity +2
```

Nhưng không tạo hồ sơ:

```text
anonymous-user-123
→ Camera interest
```

Không merge anonymous history sau khi login.

---

## 16. Duplicate interaction

Một cùng hành vi nghiệp vụ có thể được nhận lại nhiều lần do retry.

Ví dụ:

```text
RentalItemCompleted
MessageId = ABC
```

được xử lý hai lần.

Kết quả chỉ được tính một lần:

```text
lần đầu → +10
lần sau → bỏ qua
```

Không được tăng thành +20.

---

## 17. Interaction đến trễ

Recommendation chấp nhận kết quả cập nhật chậm.

Ví dụ user vừa hoàn tất thuê Camera nhưng sở thích chưa cập nhật ngay thì recommendation cũ vẫn có thể được sử dụng tạm thời.

Việc cập nhật chậm vài giây hoặc vài phút không được xem là lỗi nghiệp vụ.

---

## 18. Kết quả recommendation bị cũ

Kết quả cũ vẫn được phép sử dụng.

Recommendation là tính năng hỗ trợ trải nghiệm, không quyết định giao dịch.

Do đó:

```text
stale recommendation
→ chấp nhận được
```

---

## 19. Recommendation không được ảnh hưởng luồng thuê

Bất kỳ lỗi Recommendation nào cũng không được ngăn cản:

```text
Search
View Listing
Add Cart
Checkout
Payment
Supplier confirmation
Handover
Return
Settlement
```

Nếu không có recommendation thì dùng danh sách mặc định hoặc danh sách phổ biến.

---

## 20. Quy tắc xử lý một interaction

```text
Nhận interaction
↓
Interaction hợp lệ?
├── Không → bỏ qua / ghi nhận lỗi
└── Có
     ↓
Interaction đã xử lý?
├── Có → bỏ qua
└── Chưa
     ↓
Ghi nhận interaction
     ↓
Có UserId?
├── Có → cập nhật EquipmentType interest
└── Không → không tạo personal interest
     ↓
Có ListingId?
├── Có → cập nhật Listing popularity
└── Không → bỏ qua popularity
     ↓
Hoàn tất
```

---

## 21. Quy tắc tính interest

Một EquipmentType chỉ được tăng điểm khi hành vi xác định rõ loại thiết bị.

Ví dụ hợp lệ:

```text
ListingViewed
ListingId = A
EquipmentTypeId = Camera

→ Camera +2
```

Ví dụ:

```text
SearchPerformed
Query = "sony"
EquipmentTypeId = null

→ lưu interaction
→ không cộng EquipmentType
```

Không suy đoán EquipmentType từ raw text trong Phase 1.

---

## 22. Quy tắc tính popularity

Listing popularity chỉ tăng khi hành vi gắn với Listing cụ thể.

Ví dụ:

```text
View Listing A
→ Listing A +2

Add Listing A to Cart
→ Listing A +5
```
Paid Listing A
→ Listing A +8
```

Search chung không gắn với một Listing cụ thể nên không làm tăng Listing popularity.

---

## 23. Quantity không nhân điểm trong Phase 1

Nếu user thuê:

```text
Sony A7 IV × 3
```

Phase 1 vẫn tính theo một hành vi:

```text
Paid → +8
Completed → +10
```

không nhân theo số lượng.

Quantity lớn không nhất thiết đồng nghĩa sở thích mạnh hơn theo cùng tỷ lệ.

---

## 24. Các tình huống không tạo recommendation signal

Không tăng điểm khi:

- Add Cart thất bại.
- Checkout thất bại.
- Thanh toán chưa xác nhận.
- RentalItem chưa thực sự hoàn tất.
- Listing detail không tồn tại.
- Search request không hợp lệ.
- Interaction bị duplicate.
- Không xác định được EquipmentType đối với rule cần EquipmentType.

---

## 25. Tình huống hủy hoặc reject

Phase 1 không áp dụng negative score.

Ví dụ:

```text
User thêm Camera vào Cart
→ +5

sau đó xóa Cart
→ không trừ -5
```

Tương tự:

```text
RentalItem từng Paid
sau đó Supplier Reject
```

không tự động đảo toàn bộ interest score.

Hành vi trước đó vẫn thể hiện user từng quan tâm thiết bị.

Negative behavior thuộc phase sau.

---

## 26. Business workflow tổng thể

```text
                 HÀNH VI NGƯỜI DÙNG
                         │
         ┌───────────────┼─────────────────┐
         │               │                 │
       Search           View              Cart
         │               │                 │
         └───────────────┼─────────────────┘
                         │
                     thuê thiết bị
                         │
                    Paid / Completed
                         │
                         ▼
                GHI NHẬN INTERACTION
                         │
              ┌──────────┴──────────┐
              │                     │
              ▼                     ▼
        USER INTEREST         LISTING POPULARITY
       theo EquipmentType
              │                     │
              │                     │
       logged-in user             anonymous
              │                     │
              ▼                     ▼
       Personalized             Popular
       Recommendation        Recommendation
              │                     │
              └──────────┬──────────┘
                         ▼
                 DANH SÁCH CUỐI
                         │
              lọc Listing hợp lệ
                         │
                         ▼
                       USER
```

---

## 27. Ví dụ nghiệp vụ hoàn chỉnh — Logged-in user

### Bước 1 — Search

```text
User A search Camera

Camera +1
```

### Bước 2 — View

```text
View Sony A7 IV

Camera +2
Sony A7 IV popularity +2
```

Tổng:

```text
Camera = 3
```

### Bước 3 — Add Cart

```text
Add Sony A7 IV

Camera +5
Sony A7 IV popularity +5
```

Tổng:

```text
Camera = 8
```

### Bước 4 — Paid

```text
RentalItemPaid

Camera +8
Sony A7 IV popularity +8
```

Tổng:

```text
Camera = 16
```

### Bước 5 — Completed

```text
RentalItemCompleted

Camera +10
Sony A7 IV popularity +10
```

Tổng:

```text
Camera = 26
```

Lần sau user vào Home, Camera được ưu tiên mạnh hơn các EquipmentType khác.

---

## 28. Ví dụ nghiệp vụ hoàn chỉnh — Anonymous user

Anonymous:

```text
View Sony A7 IV
View Sony A7 IV
View Canon R6
```

Không tạo personal interest.

Chỉ tăng:

```text
Sony A7 IV popularity +4 (2 views × 2)
Canon R6 popularity +2    (1 view × 2)
```

Khi anonymous mở Home:

```text
→ hiển thị các Listing phổ biến toàn hệ thống
```

---

## 29. Ví dụ nhiều EquipmentType

User có:

```text
Camera
View ×5
Cart ×2
Paid ×1

Lens
View ×4
Cart ×1

Gimbal
View ×2
```

Điểm:

```text
Camera
5×2 + 2×5 + 1×8
= 28

Lens
4×2 + 1×5
= 13

Gimbal
2×2
= 4
```

Ranking:

```text
1. Camera
2. Lens
3. Gimbal
```

---

## 30. Tiêu chí hoàn thành Phase 1

Recommendation Phase 1 được xem là hoàn thành khi:

1. Hệ thống ghi nhận được Search, View, Cart, Paid và Completed.
2. Duplicate interaction không làm tăng điểm nhiều lần.
3. Logged-in user hình thành được interest profile theo EquipmentType.
4. Anonymous user không tạo profile cá nhân.
5. Listing popularity được tính từ hành vi thực tế.
6. Logged-in user nhận kết quả ưu tiên theo EquipmentType interest.
7. User chưa có lịch sử fallback sang popular.
8. Anonymous nhận popular listings.
9. Listing không còn hợp lệ không xuất hiện chỉ vì recommendation score cao.
10. Recommendation lỗi hoặc chậm không ảnh hưởng luồng thuê chính.
11. Kết quả cũ được phép tiếp tục sử dụng.
12. Không sử dụng ML/DL trong Phase 1.

---

## 31. Hướng mở rộng sau Phase 1

Các hướng có thể phát triển trong tương lai:

- Anonymous personalization.
- Session-based behavior.
- Time-decay.
- Negative feedback.
- Cart removal signal.
- Related EquipmentType.
- Frequently rented together.
- Similar Listing.
- Brand preference.
- Price-range preference.
- Location preference.
- Collaborative Filtering.
- Content-based recommendation.
- Embedding.
- Machine Learning ranking.
- A/B testing.

Các tính năng này không thuộc phạm vi business workflow Phase 1.
