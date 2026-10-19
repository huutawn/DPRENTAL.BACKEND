# DPR-200: kế hoạch triển khai DPR-202, DPR-203, DPR-204

> Nhánh: `rental/feat/DPR-200-Handover-Active-Rental-Lifecycle` (tạo từ nhánh DPR-195)
> Đã xong: DPR-201 (đã stage, chờ commit). Tài liệu này là kế hoạch cho phần còn lại.
> Quy ước làm việc: mỗi bước giải thích thiết kế, chờ OK, code, build + test, stage, gợi ý commit message. Không tự commit.

## 0. Tổng quan

| Thứ tự | Task | Việc chính | Migration | Commit gợi ý |
| --- | --- | --- | --- | --- |
| 1 | DPR-202 | Bổ sung test cho phần guard đã làm trong DPR-201, dọn method thừa | Không | Gộp vào commit DPR-203 |
| 2 | DPR-203 | Renter xác nhận đã nhận máy | Không | `feat(rental): DPR-203 ...` |
| 3 | DPR-204 bước A | Nền domain: chuyển dòng sang `Cancelled`, sửa rollup đơn | Không | commit riêng |
| 4 | DPR-204 bước B | Renter chủ động hủy dòng không được giao, nhả lịch, hoàn 100% | Không | commit riêng |
| 5 | DPR-204 bước C | Worker thông báo và tự hủy khi hết kỳ thuê | Không | cùng commit bước B hoặc riêng |
| 6 | DPR-204 bước D | Test tổng, README, Swagger | Không | commit riêng |

Không task nào cần migration: DPR-203 dùng 3 cột đã có trên `handover_records`; DPR-204 dùng `rental_operation_receipts` để chống trùng.

---

## 1. DPR-202: kích hoạt thuê (phần còn lại)

### 1.1. Đã làm trong DPR-201
- `RentalItem.ApplyHandover` chỉ chạy khi dòng đang `AWAITING_HANDOVER`, ngược lại bỏ qua (không ghi đè trạng thái, không ghi thêm lịch sử).
- `HandleMarkAssetsHandedOverResultAsync` bỏ qua khi receipt đã `Succeeded`.
- `RentalItem.CompleteHandover` trả lại bản ghi cũ nếu dòng đã `IN_RENTAL`.
- Test: Inventory gửi kết quả thành công hai lần chỉ tạo một bản ghi và một dòng lịch sử.

### 1.2. Việc còn lại
| # | Việc | Chi tiết |
| --- | --- | --- |
| 1 | Test guard theo trạng thái | Dòng `Rejected` hoặc `Expired` nhận kết quả bàn giao thành công thì giữ nguyên trạng thái, receipt không bị đánh Failed nhầm |
| 2 | Test đơn nhiều dòng | Đơn có 2 dòng cùng supplier: bàn giao dòng 1 thì dòng 1 `IN_RENTAL`, đơn vẫn `AWAITING_HANDOVER`; bàn giao nốt dòng 2 thì đơn thành `IN_RENTAL` |
| 3 | Test không release allocation | Sau bàn giao không có lệnh `ReleaseAllocation` nào được tạo (task yêu cầu giữ allocation suốt kỳ thuê) |
| 4 | Dọn `RentalUnitAssignment.RecordHandover` | Hiện chỉ test và `OrderTestData` gọi. Giữ vì `CompleteHandover` dùng nó; đổi tên không cần thiết. Không làm gì thêm |

File: `tests/Api.Tests/Supplier/HandoverEvidenceTests.cs` (thêm test), không đổi code production.

---

## 2. DPR-203: renter xác nhận đã nhận máy

### 2.1. Yêu cầu Jira
`POST /rental-items/{id}/acknowledge-receipt`, lưu `ReceivedAt` và `ReceivedBy`, không thêm trạng thái bắt buộc. Đây là bằng chứng bổ sung khi có khiếu nại, không phải điều kiện để thuê bắt đầu.

### 2.2. Hiện trạng
| Phần | Vị trí | Ghi chú |
| --- | --- | --- |
| Hàm domain | `HandoverRecord.Acknowledge(renterId, at, note)` (internal) | Ghi đè vô điều kiện, chưa có guard |
| Cột DB | `RenterAcknowledgedById`, `RenterAcknowledgedAt`, `RenterAcknowledgementNote` | Đã có, không migration |
| Đọc ra | `OrderQueryMapper.ToHandover` đã trả `renterAcknowledgedAt` | Chưa trả `ReceivedBy` và ghi chú |
| Phân quyền | `RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order)` | Người lạ nhận `Order.NotFound`, service token nhận 403 |
| Mẫu service | `DeductionResponseService.RespondAsync` | Cùng luồng: lấy orderId, khóa đơn, tải đơn, phân quyền |
| Endpoint / service | Chưa có | |

### 2.3. Thiết kế

Route: `POST api/v1/rental/items/{itemId}/acknowledge-receipt`, đặt trong `RentalItemsController` cạnh `handover`.

Request: `AcknowledgeReceiptRequest(string? Note)`, `Note` tối đa 2000 ký tự, body có thể rỗng.
Response 200: `AcknowledgeReceiptResponse(Guid ItemId, Guid HandoverRecordId, DateTimeOffset ReceivedAt, Guid ReceivedBy, string? Note)`.

Luồng `HandoverAcknowledgementService.AcknowledgeAsync(itemId, request, caller, ct)`:
```text
1. Kiểm tra note <= 2000 ký tự             → 400 INVALID_NOTE
2. orderId = GetOrderIdByItemIdAsync        → không có: RentalItem.NotFound (404)
3. Khóa đơn, tải đơn
4. Tìm dòng trong đơn                       → không có: RentalItem.NotFound
5. AuthorizeRenterAction(caller, order)     → người lạ: Order.NotFound; service token: 403
6. Dòng chưa có HandoverRecord              → 409 HANDOVER_NOT_RECORDED
7. Đã có RenterAcknowledgedAt               → trả lại giá trị đã lưu, không ghi gì (idempotent)
8. Chưa có                                  → record.Acknowledge, ghi 1 RentalStateTransition
                                              (từ/đến cùng trạng thái dòng, hành động "RenterAcknowledgeReceipt"),
                                              lưu, trả 200
```

Quyết định thiết kế:
- Không dùng `Idempotency-Key`: gọi lại trả đúng giá trị cũ nên tự idempotent, khác với các thao tác có tiền hay lệnh sang service khác.
- Cho phép ở mọi trạng thái sau khi có bản ghi bàn giao (`IN_RENTAL`, `PENDING_SETTLEMENT`, `COMPLETED`), vì xác nhận muộn vẫn có giá trị làm bằng chứng.
- Gọi lần hai kèm `note` khác: giữ giá trị đầu, không ghi đè (bằng chứng không sửa được). Trả 200 với giá trị đã lưu.
- Giữ khóa đơn để thống nhất mẫu và tránh hai lần gọi đồng thời cùng ghi.
- `HandoverRecord.Acknowledge` thêm guard: nếu đã có thì không ghi đè, trả về `false`/`Result` để service biết là lặp lại.

### 2.4. File bị ảnh hưởng
| Loại | File |
| --- | --- |
| Thêm | `Application/Services/Handover/HandoverAcknowledgementService.cs` |
| Thêm | `Application/IServices/Handover/IHandoverAcknowledgementService.cs` |
| Thêm | `Application/DTOs/Handover/AcknowledgeReceiptDtos.cs` |
| Sửa | `Domain/Entities/HandoverRecord.cs` (guard), thêm hàm trên `RentalItem` nếu cần ghi transition |
| Sửa | `Api/Controllers/RentalItemsController.cs` (endpoint, Swagger) |
| Sửa | `Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs` (đăng ký) |
| Sửa | `OrderQueryMapper.ToHandover` + DTO: thêm `renterAcknowledgedBy` và ghi chú để renter/admin xem được |
| Sửa | `tests/Api.Tests/Controllers/SwaggerDocumentationTests.cs` (số operation tăng 1) |

### 2.5. Test
| Test | Kỳ vọng |
| --- | --- |
| Renter xác nhận thành công | 200, lưu đúng `ReceivedAt`, `ReceivedBy`, ghi chú, có 1 dòng lịch sử |
| Gọi lại cùng renter | 200, giá trị cũ, không thêm lịch sử |
| Gọi lại kèm ghi chú khác | Giữ ghi chú đầu |
| Người lạ | 404 giống hệt id không tồn tại (thêm vào `OwnershipIdorTests`) |
| Supplier của dòng gọi | 404 (không phải renter của đơn) |
| Service token | 403 |
| Chưa bàn giao | 409 `HANDOVER_NOT_RECORDED` |
| Dòng đã `COMPLETED` | 200 |
| Ghi chú > 2000 ký tự | 400 |
| Chi tiết đơn của renter | Sau khi xác nhận, `handover.renterAcknowledgedAt` có giá trị |

---

## 3. DPR-204: không giao được máy

### 3.1. Yêu cầu Jira
Sau ngày bàn giao dự kiến mà chưa bàn giao: thông báo supplier và renter, cho renter hủy, nhả allocation, hoàn 100%. Hết kỳ thuê vẫn chưa bàn giao thì tự hủy.

### 3.2. Hiện trạng đã kiểm chứng trong code
| Phát hiện | Vị trí | Hệ quả |
| --- | --- | --- |
| Không có method nào đặt dòng sang `Cancelled` | `RentalItem` | Cần method mới |
| `MarkCancelled` gọi `ApplyHoldExpired` | `RentalOrder.cs:738` | Dòng thành `Expired`, không phải `Cancelled`. Chưa có nơi nào gọi `MarkCancelled` |
| `EvaluateOrderStatusRollup` không có nhánh nào cho dòng `Cancelled` | `RentalOrder.cs:602` | Đơn có toàn dòng `Rejected`/`Expired`/`Cancelled` mà không có `Expired` nào sẽ kẹt ở `AWAITING_HANDOVER` |
| Release + refund của luồng supplier từ chối nằm trong hàm `private` `ApplySupplierRejectAsync` | `RentalOperationExecutor.Supplier.cs:228-345` | Cần tách phần "lên lịch nhả lịch và hoàn tiền" thành hàm dùng chung |
| Refund chờ Release qua `prerequisiteOperationId` | cùng file | Giữ nguyên thứ tự: nhả lịch xong mới hoàn tiền |
| Khi Inventory trả lời Release thành công, handler gọi hoàn tiền | `InventoryAllocationResultHandler.HandleReleaseAllocationResultAsync` | Dùng lại, không đổi |
| Hoàn tiền = `RentalAmount + DepositAmount` của dòng | `RefundItemRequest(item.Id, item.RentalAmount, item.DepositAmount)` | "Hoàn 100%" theo đúng cách luồng supplier từ chối đang làm |
| Mốc ngày giờ VN | `RentalItem.Overdue.cs`: `TodayInVietnam`, `CalculateOverdueAt` | Tái dùng |
| Mẫu worker | `OverdueProtectionWorker` + `IOverdueProtectionService` + `OverdueProtectionOptions` | Làm worker mới theo mẫu này |
| `RentalErrors.OrderCancellationForbidden` đã khai báo nhưng chưa dùng | `RentalErrors.cs:52` | Dùng lại cho trạng thái không cho hủy |

### 3.3. Định nghĩa mốc thời gian (cần bạn xác nhận ở mục 4)

Đặt `S = ExpectedStartDate`, `E = ExpectedEndDate` (kiểu `DateOnly`, giờ VN).

| Mốc | Công thức | Ý nghĩa |
| --- | --- | --- |
| Quá hạn giao | 00:00 giờ VN của ngày `S + 1` | Từ đây renter được hủy, hệ thống phát thông báo |
| Tự hủy | 00:00 giờ VN của ngày `E + 1`, tức `RentalItem.CalculateOverdueAt(E)` | Hết kỳ thuê mà vẫn chưa bàn giao |

### 3.4. Bước A: nền domain

| # | Việc | Chi tiết |
| --- | --- | --- |
| A1 | `RentalItem.ApplyNotDeliveredCancellation(Guid actorId, ActorRole role, DateTimeOffset utcNow, string reason, string operationKey)` | Chỉ chạy khi `AWAITING_HANDOVER`, đặt `Cancelled`, ghi `RentalStateTransition` (hành động `CancelNotDelivered`). Trả `Result`, sai trạng thái trả `RentalErrors.InvalidItemStatus` |
| A2 | Thêm nhánh rollup trong `EvaluateOrderStatusRollup` | Khi mọi dòng thuộc `Rejected`/`Expired`/`Cancelled` và có ít nhất một `Cancelled` thì đơn thành `Cancelled`, ghi transition. Đặt sau nhánh 3 hiện có. Thứ tự các nhánh cũ giữ nguyên |
| A3 | Kiểm tra lại các tình huống rollup khi có dòng `Cancelled` | (a) 1 dòng `InRental` + 1 dòng `Cancelled`: đơn lên `IN_RENTAL` (đã đúng nhờ `activeItems` loại `Cancelled`). (b) 1 `Rejected` + 1 `Cancelled`: đơn `Cancelled` (nhánh mới). (c) 1 `AwaitingHandover` + 1 `Cancelled`: đơn giữ `AWAITING_HANDOVER` |
| A4 | `RentalOrder.MarkCancelled` | Không ai gọi, giữ nguyên, không sửa trong task này để tránh phạm vi lan rộng. Ghi chú trong README là nó dành cho hủy đơn chưa thanh toán |

Test (đặt trong `tests/Api.Tests/Domain/`): A1 thành công, A1 sai trạng thái, ba tình huống rollup ở A3, gọi lại A1 không thêm lịch sử.

### 3.5. Bước B: renter chủ động hủy

**Endpoint:** `POST api/v1/rental/items/{itemId}/cancel-not-delivered`, header `Idempotency-Key` bắt buộc, trả 202 (hoàn tiền chạy bất đồng bộ).
Request: `CancelNotDeliveredRequest(string? Reason)`, tối đa 500 ký tự. Response: `CancelNotDeliveredResponse(Guid ItemId, string Status, DateTimeOffset CancelledAt, decimal RefundAmount)`.

**Service mới:** `ItemCancellationService.CancelByRenterAsync(itemId, request, idempotencyKey, caller, ct)`.

```text
 1. Kiểm tra Idempotency-Key (<= 64 ký tự), reason (<= 500)
 2. orderId theo itemId                         → RentalItem.NotFound
 3. Khóa đơn, tải đơn, tìm dòng
 4. AuthorizeRenterAction                       → người lạ 404, service token 403
 5. Receipt cùng key:
      cùng nội dung → trả kết quả đã lưu
      khác nội dung → 409 IDEMPOTENCY_CONFLICT
 6. Dòng đã Cancelled                           → trả kết quả cũ (idempotent, không hủy lại)
 7. Dòng không ở AWAITING_HANDOVER              → 409 Order.CancellationForbidden
 8. Chưa qua mốc quá hạn giao (3.3)             → 409 CANCEL_TOO_EARLY
 9. Có receipt MarkAssetsHandedOver Pending/Processing → 409 HANDOVER_IN_PROGRESS
10. ApplyNotDeliveredCancellation, rollup đơn
11. Lên lịch ReleaseAllocation và RequestRefund (hàm dùng chung, xem dưới)
12. Phát event RentalItemCancelled qua outbox
13. Lưu một lần (một transaction), trả 202
```

**Tách hàm dùng chung (refactor nhỏ, có test bảo vệ):** từ `ApplySupplierRejectAsync` rút ra
`ScheduleReleaseAndRefundAsync(order, item, actorId, reason, utcNow, ct)` làm đúng phần dòng 258-345 hiện có (receipt Release, enqueue lệnh Release, receipt Refund có `prerequisiteOperationId`). Luồng supplier từ chối gọi lại hàm này, hành vi không đổi. Các test từ chối hiện có (`SubOrderDecisionTests`, `SupplierDecisionIntegrationTests`) là lưới an toàn.

**Khóa chống đua:** renter hủy và worker tự hủy đều đi qua khóa đơn. Bên thua tải lại thấy dòng đã `Cancelled` ở bước 6 và trả kết quả cũ.

**Chống hoàn tiền hai lần:** khóa của receipt Release/Refund tính từ `ComputeOperationKey("...", "{orderId}:{itemId}")`, giống luồng supplier từ chối, nên mỗi dòng chỉ có tối đa một cặp. Hủy lặp lại không tạo cặp thứ hai.

### 3.6. Bước C: worker thông báo và tự hủy

**Service:** `IHandoverOverdueService` / `HandoverOverdueService` theo mẫu `OverdueProtectionService`:
- `GetDueOrderIdsAsync(batchSize)`: đơn có dòng `AWAITING_HANDOVER` đã qua mốc quá hạn giao hoặc mốc tự hủy.
- `ProcessOrderAsync(orderId)`: khóa đơn, tải đơn, xử lý từng dòng:
  1. Qua mốc tự hủy: hủy theo bước B với actor là hệ thống (`ActorRole.System`), lý do "Hết kỳ thuê mà chưa bàn giao". Bỏ qua dòng đang có handover Pending/Processing.
  2. Mới qua mốc quá hạn giao: phát event `RentalHandoverOverdue` một lần cho supplier và renter. Chống phát lặp bằng một receipt `HandoverOverdueNotice` cho mỗi dòng (khóa tính từ orderId:itemId); đã có thì bỏ qua.

**Worker:** `HandoverOverdueWorker` (BackgroundService) theo mẫu `OverdueProtectionWorker`. Mỗi đơn dùng một scope DI riêng, lỗi một đơn không làm dừng cả lượt quét.

**Cấu hình:** `HandoverOverdueOptions` (section `Rental:HandoverOverdue`): `Enabled` (mặc định true), `ScanIntervalSeconds` (60), `BatchSize` (50).

**Thông báo:** chỉ phát event qua outbox (`RentalHandoverOverdue`, `RentalItemCancelled`) vì chưa có Notification service. Thêm payload record vào `Application/Kafka/Contracts` và hằng số loại event vào `KafkaContractSpec.EventTypes`.

**Loại khỏi `OperationRecoveryWorker`:** thêm action mới (`CancelNotDelivered`, `HandoverOverdueNotice`) vào danh sách loại trừ nếu recovery worker quét theo `Action`, để không bị xử lý nhầm.

### 3.7. Bước D: kiểm tra tổng

| Test | Kỳ vọng |
| --- | --- |
| Hủy trước mốc quá hạn | 409 `CANCEL_TOO_EARLY`, không có receipt nào |
| Hủy sau mốc | Dòng `Cancelled`, có receipt Release + Refund (Refund chờ Release), có event outbox |
| Hủy lần hai cùng key | Trả kết quả cũ, vẫn một cặp Release/Refund |
| Cùng key khác nội dung | 409 `IDEMPOTENCY_CONFLICT` |
| Key khác khi đã hủy | Trả kết quả cũ, không tạo receipt mới |
| Dòng `IN_RENTAL` | 409 `Order.CancellationForbidden` |
| Đang chờ Inventory xử lý bàn giao | 409 `HANDOVER_IN_PROGRESS` |
| Người lạ / supplier / service token | 404 / 404 / 403 |
| Release thành công → hoàn tiền | Số tiền hoàn bằng `RentalAmount + DepositAmount` |
| Đơn 2 dòng, chỉ hủy 1 | Dòng kia và đơn không đổi, tiền hoàn chỉ của dòng đã hủy |
| Đơn 2 dòng cùng bị hủy | Đơn thành `Cancelled` |
| Worker: qua mốc quá hạn | Phát đúng một event `RentalHandoverOverdue`, quét lại không phát thêm |
| Worker: qua mốc tự hủy | Dòng `Cancelled`, có Release/Refund |
| Worker đua với renter hủy | Chỉ một cặp Release/Refund |
| Worker bỏ qua dòng đang có handover chờ | Không hủy |
| Luồng supplier từ chối sau refactor | Test cũ vẫn pass |

Cập nhật: README (mục luồng bàn giao và hủy), số operation trong Swagger test, mô tả endpoint.

### 3.8. File bị ảnh hưởng (DPR-204)
| Loại | File |
| --- | --- |
| Sửa | `Domain/Entities/RentalItem.cs` hoặc file `partial` mới `RentalItem.Cancellation.cs` |
| Sửa | `Domain/Entities/RentalOrder.cs` (rollup) |
| Sửa | `Application/Services/Operations/RentalOperationExecutor.Supplier.cs` (tách hàm dùng chung) |
| Thêm | `Application/Services/Cancellation/ItemCancellationService.cs` + interface |
| Thêm | `Application/Services/Overdue/HandoverOverdueService.cs` + interface |
| Thêm | `Application/Background/HandoverOverdueWorker.cs`, `Application/Options/HandoverOverdueOptions.cs` |
| Thêm | Payload event và hằng số trong `Application/Kafka/Contracts` |
| Sửa | `RentalItemsController`, `ApplicationServiceCollectionExtensions`, `OperationRecoveryWorker` (nếu cần) |
| Thêm | Test: `Domain/ItemCancellationDomainTests`, `Supplier` hoặc `Cancellation/ItemCancellationTests`, `Overdue/HandoverOverdueTests` |

---

## 4. Điểm cần bạn quyết định (chặn bước B)

| # | Câu hỏi | Đề xuất của tôi | Nếu khác thì |
| --- | --- | --- | --- |
| 1 | Mốc quá hạn giao | 00:00 giờ VN ngày `ExpectedStartDate + 1` | Đổi một hằng số trong domain |
| 2 | `ExpectedEndDate` là ngày cuối được dùng (inclusive)? | Có, tự hủy lúc 00:00 ngày `E + 1` giống mốc quá hạn trả máy | Dịch mốc tự hủy sớm một ngày |
| 3 | "Hoàn 100%" gồm gì | Tiền thuê + tiền cọc của riêng dòng đó | Đổi `RefundItemRequest` |
| 4 | Lệnh `ReleaseAllocation` có nhả được lịch đã xác nhận không | Giả định có, dùng đúng lệnh hiện có. **Tôi chưa kiểm chứng được điều này**, cần hỏi team Inventory | Nếu Inventory cần lệnh khác thì thêm lệnh/contract mới, ngoài phạm vi và cần thống nhất với Inventory |
| 5 | Có để renter hủy khi chưa qua mốc quá hạn giao không | Không (409 `CANCEL_TOO_EARLY`) | Bỏ bước 8 |
| 6 | Supplier có được tự báo "không giao được" để hủy sớm không | Không, ngoài phạm vi task | Thêm endpoint cho supplier |
| 7 | Thông báo | Chỉ phát event qua outbox, chưa gửi mail/push | Chờ Notification service |
| 8 | Cách chia commit | Bước A, bước B+C, bước D tách riêng | Gộp lại |

## 5. Rủi ro

| Rủi ro | Mức | Giảm thiểu |
| --- | --- | --- |
| Hoàn tiền hai lần khi renter hủy đua với worker | Cao | Khóa đơn; khóa receipt cố định theo `orderId:itemId`; test đua |
| Giả định Inventory nhả được lịch đã xác nhận (câu 4) | Cao | Xác nhận với team Inventory trước khi đưa lên môi trường thật. Nếu sai, lệnh Release thất bại và hoàn tiền không chạy vì chờ Release |
| Tách hàm dùng chung làm hỏng luồng supplier từ chối | Trung bình | Refactor thuần túy trước, chạy test từ chối, rồi mới thêm luồng mới |
| Rollup đơn sai khi trộn nhiều trạng thái dòng | Trung bình | Bảng tình huống ở A3 có test từng trường hợp |
| Test Postgres chưa chạy với migration thật | Thấp | Không có migration mới; vẫn chạy toàn bộ test khi Docker bật |
| Worker quét nhiều đơn làm chậm | Thấp | Giới hạn `BatchSize`, mỗi đơn một scope |
