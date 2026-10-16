# DPR-200: Handover & Active Rental Lifecycle — phân tích

Ngày khảo sát: 16/10/2026, nhánh `rental/feat/DPR-195-Rental-Query-Detail-Authorization` (đã có commit DPR-196 đến DPR-199).
Tài liệu này chỉ phân tích, chưa có dòng code nào được sửa.

## 1. Tóm tắt

| Task | Mục đích | Hiện trạng | Việc còn lại | Migration |
| --- | --- | --- | --- | --- |
| DPR-201 Bằng chứng bàn giao | Ghi chứng cứ tình trạng máy lúc giao | Endpoint đã chạy | Xác minh ảnh với Media service, ô xác nhận, sửa lỗi gửi lại khi Inventory từ chối | Không |
| DPR-202 Kích hoạt thuê | Dòng sang `IN_RENTAL`, máy vẫn bị khóa lịch | Gần như xong | Viết test cho 3 điều kiện của task | Không |
| DPR-203 Người thuê xác nhận đã nhận | Bằng chứng bổ sung, không đổi trạng thái | Có hàm domain và 3 cột | Service, endpoint, test | Không |
| DPR-204 Không giao được máy | Cho người thuê thoát và hoàn tiền khi supplier không giao | Chưa có gì | Toàn bộ: nhắc quá hạn, người thuê hủy, tự hủy | Có thể (xem mục 6) |

Thứ tự đề xuất: **201 + 202** (cùng một lệnh), rồi **203**, cuối cùng **204**.

## 2. Epic nằm ở đâu trong vòng đời

```text
PENDING_SUPPLIER_CONFIRMATION
        │ supplier nhận cả đơn con
        ▼
AWAITING_HANDOVER ──── quá hạn mà chưa giao (DPR-204) ────► CANCELLED
        │                (nhả lịch Inventory + hoàn 100%)
        │ supplier bàn giao (DPR-201 ghi bằng chứng, DPR-202 đổi trạng thái)
        ▼
IN_RENTAL      ◄── người thuê bấm "đã nhận" (DPR-203, không đổi trạng thái)
        │ trả máy (đã có, DPR-205)
        ▼
PENDING_SETTLEMENT → COMPLETED
```

Trạng thái nằm trên từng `RentalItem`. Trạng thái đơn cha được tính lại bởi `RentalOrder.EvaluateOrderStatusRollup`.

## 3. Các task này dùng để làm gì

Khiếu nại trong MVP là **tranh chấp tiền cọc** (AGENTS.md mục 15): trả máy hư → supplier đề xuất trừ cọc → người thuê đồng ý hoặc khiếu nại → Admin phân xử (`DeductionResponseService.ResolveDisputeAsync`). Admin phân xử bằng cách so tình trạng máy lúc giao và lúc trả.

| Task | Liên quan khiếu nại | Vai trò |
| --- | --- | --- |
| 201 | Có, là gốc | Ảnh và tình trạng từng máy lúc giao là mốc so sánh. Phải thật (xác minh với Media) và không sửa được |
| 203 | Có, bổ sung | Người thuê xác nhận đã nhận: bên thứ hai chứng thực việc giao máy |
| 202 | Không trực tiếp | Vòng đời: máy đang ở chỗ người thuê nên phải tiếp tục bị khóa lịch, không ai đặt trùng được |
| 204 | Là "khiếu nại" được tự động hóa | Người thuê không nhận được máy: hệ thống cho hủy và hoàn 100% mà không cần Admin |

## 4. DPR-201: bằng chứng bàn giao

### 4.1. Task yêu cầu

- Supplier ghi `ActualHandoverAt`, `MediaIds[]`, `Note`.
- Bắt buộc có ít nhất 1 ảnh và đánh dấu ô xác nhận.
- Bằng chứng không sửa được sau khi gửi.
- Bằng chứng gắn với dòng thuê, và khi cần thì gắn với từng máy vật lý.

### 4.2. Code đang có

| Phần | Vị trí | Ghi chú |
| --- | --- | --- |
| Endpoint | `RentalItemsController.HandoverItem` — `POST /api/v1/rental/items/{itemId}/handover` (dòng 48) | Bắt buộc `Idempotency-Key`, trả 202 |
| Request | `HandoverItemRequest(ActualHandoverAt, Note, Units[])`, mỗi unit `HandoverUnitObservationRequest(AssignmentId, Condition, Note, MediaIds)` | Ảnh theo **từng máy** |
| Service | `SupplierDecisionService.HandoverItemAsync` (dòng 139) | Kiểm tra quyền (policy DPR-199), thời gian không ở tương lai, đủ và đúng các máy, mỗi máy có ít nhất 1 media, idempotency theo fingerprint |
| Lưu dữ liệu | Tạo `HandoverRecord` ngay, gọi `RentalUnitAssignment.RecordHandoverEvidence` cho từng máy, gửi lệnh `MarkAssetsHandedOver` sang Inventory qua outbox Kafka, lưu receipt `Pending` | Một transaction |
| Ràng buộc DB | `handover_records.RentalItemId` có **unique index** | Một dòng chỉ có một bản ghi bàn giao |
| Bằng chứng theo máy | `RentalUnitAssignment.HandoverMediaIds`, `HandoverCondition`, `HandoverNote` | Đáp ứng "gắn với physical asset" |

### 4.3. Lỗ hổng

1. **Ảnh không được xác minh.** Service chỉ kiểm tra danh sách không rỗng. Không gọi `RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync`, trong khi `ReturnService` (dòng 94) đã gọi. Hệ quả: gắn được `MediaId` bịa, của người khác, chưa xử lý xong hoặc công khai. Bằng chứng như vậy vô giá trị khi khiếu nại.
2. **Thiếu ô xác nhận.** Request không có `confirmed`.
3. **Gửi lại sau khi Inventory từ chối bị lỗi.** `HandoverRecord` được lưu trước khi Inventory trả lời. Nếu Inventory từ chối, `HandleMarkAssetsHandedOverResultAsync` đánh dấu receipt thất bại, dòng vẫn `AWAITING_HANDOVER`, nhưng bản ghi bàn giao vẫn nằm trong DB. Supplier gửi lại với khóa mới sẽ tạo bản ghi thứ hai cho cùng dòng, vi phạm unique index, lỗi 500. Supplier bị kẹt.
4. **Mâu thuẫn bất biến.** Nếu sửa điểm 3 bằng cách ghi đè bản ghi cũ thì vi phạm yêu cầu "không sửa được".
5. **Receipt treo quá 30 phút** (`OperationRecoveryWorker`) chuyển sang cần đối soát thủ công; dòng cũng kẹt ở `AWAITING_HANDOVER` với một bản ghi bàn giao. Cùng gốc với điểm 3.

### 4.4. Thiết kế đề xuất

```text
POST /api/v1/rental/items/{itemId}/handover   (Idempotency-Key)

1. Kiểm tra key, AuthorizeSupplierCaller, request (confirmed = true, thời gian, đủ máy)
2. Xác minh media từng máy qua RentalEvidenceValidator        ← ngoài khóa, ngoài transaction
3. Khóa đơn, tải đơn, AuthorizeSupplierItem
4. Receipt cùng key: cùng nội dung → trả kết quả cũ; khác → 409 IDEMPOTENCY_CONFLICT
5. Dòng phải AWAITING_HANDOVER và chưa có handover đang chờ Inventory
6. Lưu receipt Pending, payload chứa toàn bộ bằng chứng; gửi MarkAssetsHandedOver
7. Commit, trả 202

Inventory trả kết quả:
  thành công → tạo HandoverRecord + bằng chứng từng máy từ payload receipt, máy HandedOver, dòng IN_RENTAL
  thất bại   → receipt Failed, KHÔNG có bản ghi nào; supplier gửi lại với key mới
```

| Điểm | Đề xuất | Lý do |
| --- | --- | --- |
| Ô xác nhận | Thêm `bool Confirmed` vào request, phải `true`, sai trả 400 `HANDOVER_NOT_CONFIRMED` | Đúng task; lưu thời điểm vào cột có sẵn `SupplierConfirmedAt` |
| Xác minh ảnh | Gọi validator cho từng máy, `expectedUploaderId = supplierId`, bắt buộc ≥ 1 | Giống trả máy; tối đa 10 tệp/máy, Ready, Private, không trùng |
| `MediaIds[]` cấp dòng | **Không** thêm cột; response trả thêm danh sách gộp | Bằng chứng theo từng máy chính xác hơn (AGENTS.md: truy vết theo `RentalUnitAssignment`); tránh migration |
| Thời điểm tạo bản ghi | Khi Inventory xác nhận thành công | Gửi lại được sau khi bị từ chối; bản ghi đã có là bất biến thật |
| Chặn gửi trùng khi đang chờ | Có receipt `MarkAssetsHandedOver` Pending/Processing cho dòng → 409 `HANDOVER_IN_PROGRESS` | Không gửi 2 lệnh song song sang Inventory |

Phương án thay thế cho điểm "thời điểm tạo bản ghi": giữ thứ tự cũ, khi đã có bản ghi mà receipt thất bại thì trả 409 và để Admin xử lý. Đơn giản hơn nhưng supplier bị kẹt, không khuyến nghị.

### 4.5. File bị ảnh hưởng

- `SupplierDecisionService.HandoverItemAsync`
- `InventoryAllocationResultHandler.HandleMarkAssetsHandedOverResultAsync`
- `HandoverItemRequest`, `HandoverItemResponse` (`SupplierDecisionDtos.cs`)
- `RentalItemsController` (mô tả Swagger)
- Có thể gom phần dựng bản ghi bàn giao vào một hàm domain `RentalItem.RecordHandover(...)` để handler chỉ gọi một chỗ

### 4.6. Test cần có

- Thiếu `confirmed` hoặc `false` → 400
- Media không tồn tại, của người khác, chưa Ready, công khai, trùng, quá 10 → lỗi tương ứng, không lưu gì
- Gửi thành công → chỉ receipt Pending, chưa có `HandoverRecord`
- Inventory thành công → có bản ghi, bằng chứng từng máy đúng, dòng `IN_RENTAL`
- Inventory từ chối → không có bản ghi; gửi lại với key mới thành công
- Cùng key cùng nội dung → trả kết quả cũ; cùng key khác nội dung → 409
- Gửi khi đang chờ Inventory → 409

## 5. DPR-202: kích hoạt thuê

### 5.1. Task yêu cầu

- Dòng `AWAITING_HANDOVER` ghi nhận bàn giao thì chuyển `IN_RENTAL`.
- Không nhả allocation của Inventory; máy được gán tiếp tục bị khóa.
- Gửi hai lần không tạo nhiều bản ghi hay nhiều lần chuyển trạng thái.

### 5.2. Code đang có

| Điều kiện | Đã đáp ứng bởi |
| --- | --- |
| Chuyển `IN_RENTAL` | `RentalItem.ApplyHandover` (RentalItem.cs dòng ~318), gọi từ `HandleMarkAssetsHandedOverResultAsync` khi Inventory báo thành công; ghi `RentalStateTransition` action `MarkAssetsHandedOver` |
| Trạng thái đơn cha | `RentalOrder.EvaluateOrderStatusRollup`: mọi dòng còn hiệu lực ở `IN_RENTAL`/`PENDING_SETTLEMENT`/`COMPLETED` thì đơn thành `InRental` |
| Không nhả allocation | Luồng bàn giao chỉ gửi `MarkAssetsHandedOver`, không có lệnh release |
| Không trùng | Idempotency theo key + fingerprint; unique index `handover_records.RentalItemId` |

### 5.3. Còn thiếu

1. **`ApplyHandover` không kiểm tra trạng thái hiện tại.** Nếu Inventory gửi trùng kết quả thành công (Kafka có thể giao lại), hàm sẽ chuyển trạng thái và ghi lịch sử thêm lần nữa. Cần: chỉ chuyển khi đang `AWAITING_HANDOVER`, và handler bỏ qua receipt đã `Succeeded`. Cần đọc lại handler xem đã có inbox chống trùng chưa trước khi sửa.
2. **Chưa có test** cho 3 điều kiện của task.

### 5.4. Test cần có

- Inventory thành công → dòng `IN_RENTAL`, đúng 1 transition, đơn cha `InRental` khi mọi dòng đã giao
- Kết quả thành công đến 2 lần → vẫn 1 bản ghi, 1 transition
- Không có outbox `ReleaseAllocation` nào sau bàn giao
- Đơn có 2 dòng, mới giao 1 → đơn cha vẫn `AwaitingHandover`

## 6. DPR-203: người thuê xác nhận đã nhận

### 6.1. Task yêu cầu

- `POST /rental-items/{id}/acknowledge-receipt`
- Lưu `ReceivedAt`, `ReceivedBy`
- Không thêm trạng thái bắt buộc; chưa xác nhận vẫn `IN_RENTAL`
- Mục tiêu: thêm bằng chứng, không làm máy trạng thái phình ra

### 6.2. Code đang có

- `HandoverRecord.Acknowledge(renterId, acknowledgedAt, note)` (internal)
- Cột `RenterAcknowledgedById`, `RenterAcknowledgedAt`, `RenterAcknowledgementNote`
- Chi tiết đơn của người thuê (DPR-196) đã trả `handover.renterAcknowledgedAt`
- Policy `AuthorizeRenterAction` (DPR-199) đã có, trả 404 cho người lạ

### 6.3. Thiết kế đề xuất

```text
POST /api/v1/rental/items/{itemId}/acknowledge-receipt   body: { note? }

1. Tìm đơn của dòng, AuthorizeRenterAction (người lạ → 404 RentalItem.NotFound)
2. Dòng phải có HandoverRecord, nếu không → 409 HANDOVER_NOT_RECORDED
3. Đã xác nhận rồi → trả lại giá trị cũ, không ghi đè (idempotent tự nhiên, không cần Idempotency-Key)
4. Acknowledge(renterId, now, note); ghi RentalStateTransition FromState = ToState, action RenterAcknowledgeReceipt
5. Trả { receivedAt, receivedBy }
```

| Điểm | Đề xuất |
| --- | --- |
| Route | Dùng `api/v1/rental/items/{itemId}/acknowledge-receipt` cho thống nhất với các route hiện có, thay vì `/rental-items/{id}` trong task |
| Tên trường | Giữ tên cột hiện tại, DTO trả `receivedAt` / `receivedBy` theo task. Không migration đổi tên |
| Trạng thái cho phép | Mọi trạng thái sau khi đã có bản ghi bàn giao (`IN_RENTAL` trở đi) |
| Lớp service | Service mới `HandoverAcknowledgementService` trong `Application/Services/Handover`, không nhét thêm vào `SupplierDecisionService` |

### 6.4. Test cần có

- Người thuê xác nhận → lưu đúng người và thời điểm, trạng thái dòng không đổi
- Xác nhận lần 2 → giữ thời điểm lần đầu
- Chưa bàn giao → 409
- Người lạ, supplier → 404 giống id không tồn tại
- Service token → 403

## 7. DPR-204: không giao được máy

### 7.1. Task yêu cầu

Sau ngày giao dự kiến mà chưa bàn giao:

- thông báo supplier và người thuê;
- cho người thuê yêu cầu hủy vì chưa nhận;
- nhả allocation;
- hoàn 100%.

Đến hết kỳ thuê dự kiến vẫn chưa giao thì tự hủy.

### 7.2. Code đang có (để tái dùng)

| Mảnh | Vị trí |
| --- | --- |
| Nhả lịch + hoàn tiền từng dòng | Luồng supplier từ chối / hết hạn xác nhận trong `RentalOperationExecutor.Supplier.cs` (dòng ~282–345): lên lịch `ReleaseAllocation` và `RequestRefund` qua receipt + outbox |
| Thực thi nhả lịch, hoàn tiền | `ExecuteReleaseAllocationAsync` (`RentalOperationExecutor.Inventory.cs` dòng 100), `ExecuteRequestRefundAsync` (`RentalOperationExecutor.Payment.cs` dòng 109) |
| Worker quét định kỳ | `ExpiryScannerWorker` (hết hạn thanh toán, hết hạn xác nhận), `OverdueProtectionWorker` (quá hạn trả máy) |
| Ngày theo giờ Việt Nam | `RentalItem.TodayInVietnam` |
| Trạng thái `Cancelled` | Có trong enum dòng và đơn, nhưng **chưa có hàm domain nào chuyển dòng sang `Cancelled`** |

### 7.3. Thiết kế đề xuất

| Phần | Kích hoạt | Việc |
| --- | --- | --- |
| A. Nhắc quá hạn | Worker: dòng `AWAITING_HANDOVER`, hôm nay (giờ VN) > `ExpectedStartDate` | Ghi outbox event `RentalHandoverOverdue` **một lần** cho mỗi dòng (chống trùng bằng receipt) |
| B. Người thuê hủy | `POST api/v1/rental/items/{itemId}/cancel-not-delivered` + `Idempotency-Key` | `AuthorizeRenterAction`; dòng phải `AWAITING_HANDOVER` và đã quá ngày giao; dòng → `CANCELLED`; lên lịch nhả lịch + hoàn 100% |
| C. Tự hủy | Worker: dòng `AWAITING_HANDOVER`, hôm nay > ngày dùng cuối | Như B, người thực hiện là `System` |

Thêm `RentalItem.ApplyNotDeliveredCancellation(actor, role, reason, at, operationId)`: chỉ chạy khi `AWAITING_HANDOVER`, ghi lịch sử, rồi `EvaluateOrderStatusRollup`.

### 7.4. Điểm phải chốt trước khi code

1. **Mốc "quá hạn giao":** đề xuất là hết ngày `ExpectedStartDate` theo giờ Việt Nam (00:00 ngày hôm sau), giống cách DPR-205 tính quá hạn trả.
2. **Mốc tự hủy:** hết ngày dùng cuối (ngày trước `ExpectedEndDate` vì Rental lưu ngày kết thúc không tính).
3. **"Hoàn 100%" gồm gì:** tiền thuê + tiền cọc của dòng đó. Giống luồng từ chối.
4. **Nhả lịch đã xác nhận:** sau khi supplier nhận, allocation ở Inventory đã được xác nhận. Cần kiểm tra `ReleaseAllocation` của Inventory có nhận allocation đã xác nhận không, hay phải dùng lệnh hủy khác. Đây là contract với service khác, phải xác nhận với team Inventory.
5. **Thông báo:** chưa có Notification service. Đề xuất chỉ phát event qua outbox; nội dung gửi cho ai là việc của service thông báo sau này.
6. **Hủy bàn giao đang chờ:** nếu supplier đã gửi bàn giao (receipt Pending) đúng lúc người thuê hủy, ai thắng? Đề xuất: có handover đang chờ Inventory thì từ chối hủy (409), vì máy có thể đã giao thật.
7. **Migration:** không cần nếu chống trùng thông báo bằng receipt. Cần nếu muốn cột `HandoverOverdueNotifiedAt` để quét nhanh hơn.

### 7.5. Test cần có

- Chưa quá ngày giao → không nhắc, người thuê hủy bị từ chối
- Quá ngày giao → 1 event, chạy worker lần 2 không thêm event
- Người thuê hủy → dòng `CANCELLED`, có receipt nhả lịch và hoàn tiền, đơn cha tính lại đúng
- Hủy lại cùng key → kết quả cũ; supplier khác/người lạ → 404
- Đang chờ Inventory xác nhận bàn giao → 409
- Hết kỳ thuê → tự hủy, actor `System`
- Bàn giao sau khi đã hủy → bị từ chối

## 8. Câu hỏi cần chốt

| # | Câu hỏi | Đề xuất |
| --- | --- | --- |
| 1 | DPR-201: tạo `HandoverRecord` lúc nào? | Khi Inventory xác nhận thành công |
| 2 | DPR-201: thêm cột `MediaIds` cấp dòng? | Không, dùng bằng chứng theo từng máy |
| 3 | DPR-203: route theo task hay theo code? | Theo code: `api/v1/rental/items/{itemId}/acknowledge-receipt` |
| 4 | DPR-204: các điểm 1–7 ở mục 7.4 | Như đề xuất trong mục đó; điểm 4 cần hỏi team Inventory |
| 5 | Nhánh làm việc | Tạo `rental/feat/DPR-200-Handover-Active-Rental-Lifecycle` từ nhánh DPR-195 (chứa policy DPR-199) |

## 9. Rủi ro chung

- Test Postgres chưa chạy được (Docker tắt). Phần khóa đơn, unique index và outbox cần chạy trên Postgres thật trước khi merge.
- Luồng bàn giao phụ thuộc Inventory trả kết quả qua Kafka. Test dùng fake producer/consumer không chứng minh được Inventory thật xử lý đúng.
- DPR-204 động tới tiền (hoàn 100%). Phải bảo đảm một dòng chỉ được hoàn một lần dù người thuê hủy và worker tự hủy chạy cùng lúc: cả hai đi qua khóa đơn và kiểm tra lại trạng thái `AWAITING_HANDOVER` bên trong khóa.
