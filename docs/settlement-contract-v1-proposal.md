# Rental–Payment settlement contract v1 (proposal)

Status: **Contract v1 đang triển khai — chờ Rental/Payment cùng freeze**. Payment đã có endpoint settlement, migration, xác nhận hoàn cọc thủ công và outbox publisher. Chuyển khoản vẫn do admin thực hiện ngoài hệ thống.

## Ranh giới trách nhiệm

- Rental quyết định `DepositAmount`, `FinalDeductionAmount`, `RefundedDepositAmount` và `DecisionRef` sau khi hoàn tất nghiệp vụ thu hồi/tranh chấp. Rental không gửi phí nền tảng hoặc tiền phải trả supplier.
- Payment kiểm tra quyết định với `InvoiceItem` snapshot của hóa đơn đã thanh toán, tính phí và ghi nhận các nghĩa vụ tài chính. Không đọc Rental DB hoặc tự đánh giá hư hỏng.
- Payment chỉ phát `RentalSettled` khi những nghĩa vụ bắt buộc cho `RentalItem` đã được đánh dấu hoàn tất. Tạo yêu cầu hoàn cọc không đồng nghĩa đã hoàn tiền. Team đã chốt `SupplierPayable` được ghi nhận vào ledger là đủ cho nghĩa vụ supplier trong MVP; không chờ payout thực tế.

## Contract Rental hiện đã đề xuất

`POST /api/v1/settlements` với service token `aud=payment-api`, scope `payment.settlement.write`. Request hiện có trên Rental:

```text
RentalOrderId, RentalItemId, InvoiceRef, PaymentRef, SupplierId,
DepositAmount, FinalDeductionAmount, RefundedDepositAmount,
Currency, DecisionRef, OperationKey
```

Response hiện có: `SettlementRef`, `OperationKey`, `Status`, `FinalDeductionAmount`, `RefundedDepositAmount`, `Currency`, `IsReplay`. Rental hiểu response là xác nhận **đã nhận lệnh**; việc đóng tài chính được xác nhận qua `RentalSettled`.

Quyết định đã chốt: **giữ tên wire field `RefundedDepositAmount`** để tương thích Rental. Trong *request* và response `PENDING_MANUAL_REFUND`, field này là số tiền **phải hoàn**; chỉ trong event `RentalSettled` (sau financial closure) nó mới biểu thị số tiền **đã hoàn**. Client không được coi response tiếp nhận là xác nhận chuyển khoản.

`RentalSettled` v1 mà Rental hiện định consume mang `RentalOrderId`, `RentalItemId`, `InvoiceRef`, `PaymentRef`, `SettlementRef`, `OperationKey`, `FinalDeductionAmount`, `RefundedDepositAmount`, `Currency`, `RefundRef?`, `CompletedAt`. Dùng envelope Payment hiện có (`messageId`, `messageType`, `version`, `producer`, `aggregateId`, `occurredAt`, `correlationId`, `causationId`, `payload`). Chưa tự ý thêm field vào event này.

## Quy tắc Payment phải thực thi

1. Tìm đúng hóa đơn bằng `InvoiceRef`, xác nhận `RentalOrderId`, `PaymentRef`, `Currency` và trạng thái thanh toán hợp lệ. Tìm đúng một `InvoiceItem` theo `RentalItemId`; đối chiếu `SupplierId` và `DepositAmount` với snapshot. `RenterId` lấy từ hóa đơn, không nhận từ request.
2. VND là số nguyên không âm theo `VndAmountValidator`; `0 <= FinalDeductionAmount <= DepositAmount`; `RefundedDepositAmount == DepositAmount - FinalDeductionAmount`. `DecisionRef` và `OperationKey` bắt buộc, không rỗng.
3. Snapshot `PlatformFeeRate = 0.10` trên từng settlement. `PlatformFeeAmount = Round(RentalAmount * PlatformFeeRate, 0, MidpointRounding.AwayFromZero)` tính riêng theo `RentalItem`; làm tròn tới VND gần nhất, trường hợp đúng nửa VND làm tròn lên vì số tiền không âm. `SupplierPayable = RentalAmount - PlatformFeeAmount + FinalDeductionAmount`. Không tính phí trên deposit. `SupplierPayable` được ghi vào ledger; payout vật lý nằm ngoài task này.
4. Nếu tiền hoàn cọc phải trả `> 0`, tạo đúng một `ManualRefundRequirement` gắn `RentalItemId`, `RenterId`, `PaymentRef`, amount, reason, trạng thái `PENDING_MANUAL_REFUND`. Admin chuyển khoản ngoài hệ thống rồi xác nhận `COMPLETED`, `ExternalTransferReference`, `CompletedAt`, `CompletedBy`. Khi phát `RentalSettled`, `RefundRef` chính là `ExternalTransferReference` đã xác nhận. Nếu số tiền phải hoàn bằng 0, không tạo requirement và `RefundRef = null`.
5. Retry cùng `OperationKey` và cùng payload trả cùng `SettlementRef` với `IsReplay=true`; cùng key khác payload trả conflict. Một `RentalItemId` chỉ có một quyết định settlement có hiệu lực; yêu cầu với key khác nhưng quyết định khác phải bị từ chối. DB unique constraints và transaction phải bảo vệ concurrency.
6. `RentalSettled` chỉ được ghi một lần vào outbox khi điều kiện đóng tài chính đạt. Publisher vận chuyển event nằm ở phần triển khai, không được coi outbox đã gửi thành công chỉ vì đã persist.

## HTTP và lỗi đề xuất để hai bên duyệt

| Tình huống | HTTP | Mã lỗi đề xuất |
| --- | --- | --- |
| Thiếu/sai định dạng ID, reference, VND, breakdown | 400 | `Payment.InvalidSettlement` / `Payment.SettlementBreakdownMismatch` |
| Chưa xác thực / không có service scope | 401 / 403 | theo auth middleware |
| Không có hóa đơn hoặc item trong hóa đơn | 404 | `INVOICE_NOT_FOUND` / `Payment.InvoiceItemNotFound` |
| Hóa đơn chưa thanh toán; snapshot/ref không khớp; settlement khác đã tồn tại | 409 | `Payment.InvoiceNotPaid` / `Payment.SettlementSnapshotMismatch` / `DUPLICATE_SETTLEMENT` |
| Deduction vượt deposit | 400 | `SETTLEMENT_EXCEEDS_DEPOSIT` |
| Cùng `OperationKey` nhưng payload khác | 409 | `DUPLICATE_OPERATION_KEY` |

Các mã chưa có trong `PaymentContractSpec` là **đề xuất**, cần Rental/Payment thống nhất trước khi freeze. Không trả raw exception hoặc dữ liệu ngân hàng ra client.

## Quyết định đã chốt và phần còn cần phối hợp

1. **Đã chốt:** giữ `RefundedDepositAmount`; tiền phải hoàn trên request được phân biệt với tiền đã hoàn bằng trạng thái và event. Khi refund due bằng 0, event mang giá trị 0.
2. **Đã chốt:** làm tròn phí đến VND gần nhất. Quy ước chi tiết để code nhất quán là `MidpointRounding.AwayFromZero` trên từng RentalItem; nếu team muốn kiểu xử lý đúng nửa VND khác thì cần thay trước khi freeze.
3. **Đã chốt:** ghi nhận `SupplierPayable` vào ledger là đủ cho MVP; chỉ còn chờ hoàn cọc thủ công (nếu > 0) trước `RentalSettled`.
4. **Admin confirmation contract:** đề xuất `POST /api/v1/manual-refunds/{requirementId}/complete`, admin gửi `ExternalTransferReference`; server lấy `CompletedAt` từ UTC clock và `CompletedBy` từ token, không cho client tự đặt. Route và quyền admin sẽ được xác nhận khi triển khai subtask 5 theo claim Auth hiện hành.
5. **Response status:** đề xuất `PENDING_MANUAL_REFUND` và `COMPLETED`. `COMPLETED` chỉ có khi financial closure đã được commit; `RentalSettled` outbox được ghi cùng transaction. Các tên này cần Rental xác nhận trước khi freeze.

## Phạm vi triển khai sau khi freeze

Subtask 2–6 đã có domain calculation, persistence/migration, API, manual refund confirmation và outbox publisher. Admin phải chuyển khoản thật trước khi gọi `POST /api/v1/manual-refunds/{requirementId}/complete`; token phải có audience `dprental-clients` và role Auth `adm` hoặc `adp`. Cùng mã chuyển khoản được replay; mã khác bị từ chối. Xác nhận và `RentalSettled` outbox commit cùng transaction.

Kafka mặc định tắt. Để phát event, cấu hình `Kafka:Enabled=true`, `Kafka:BootstrapServers` và topic `payment.events.v1` (nếu broker yêu cầu xác thực, cấu hình thêm SecurityProtocol/SaslMechanism/SaslUsername/SaslPassword hoặc SslCaLocation). Publisher chỉ đánh dấu `PublishedAt` sau khi broker xác nhận persist. Lỗi gửi giữ message pending để retry. Nếu broker đã nhận nhưng DB chưa commit thì message có thể được gửi lại; consumer phải deduplicate bằng `messageId`. Cần broker Kafka thực và Rental consumer để xác nhận end-to-end ngoài test với transport fake.

TODO sau MVP: tự động hoàn tiền qua provider thay cho chuyển khoản admin. Không suy ra tiền đã chuyển từ việc tạo `ManualRefundRequirement` hoặc nhận lệnh settlement. Late fee và charge vượt deposit nằm ngoài MVP.
