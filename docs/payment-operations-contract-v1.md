# Payment operations contract v1

> Cập nhật subtask 4: API xác nhận hoàn cọc đã yêu cầu `OperationKey`, `ExternalTransferReference`, `AuditReason`; trạng thái hoàn cọc, settlement, audit append-only và `RentalSettled` outbox được ghi trong cùng transaction. Bản ghi hoàn cọc cũ đã hoàn tất trước migration không được tạo audit giả; thao tác với key mới trên bản ghi đó trả `409`. Migration dừng với thông báo rõ nếu mã chuyển khoản hoàn cọc cũ bị trùng và cần đối soát.

Status: **Đã triển khai subtask 1–7 của Supplier Payable, Manual Payout & Payment Operations.** Tính năng payout destination do Payment quản lý đã bị gỡ: payout không còn gắn tài khoản đích.

## Ranh giới và quyết định

- Mỗi settlement có đúng một `SupplierPayable` cho một `RentalItemId` và `SupplierId`. Tiền lấy từ `RentalItemSettlementRecord.SupplierPayable` đã snapshot; không tính lại từ dữ liệu Rental hay gộp nhiều supplier của cùng order.
- Tạo một `SupplierPayout` cho từng settlement **có `SupplierPayable > 0`**, số tiền/currency/supplier/item bất biến. Nếu payable bằng 0 thì ledger vẫn ghi nhận nhưng không có nghĩa vụ chuyển khoản. Trạng thái `PENDING_PAYOUT` → `COMPLETED` sau khi admin thực sự chuyển khoản. Không có API đổi trạng thái tùy ý.
- `RentalSettled` tiếp tục theo contract hiện hành: ledger ghi nhận supplier payable là đủ; nếu có refund due thì chờ xác nhận hoàn cọc. Payout supplier được theo dõi độc lập, không chặn hay phát lại `RentalSettled`.
- Payment không lưu tài khoản ngân hàng của supplier. Admin đọc tài khoản mặc định của supplier từ Rental rồi chuyển khoản; payout DTO và audit DTO không chứa dữ liệu ngân hàng. Các cột `DestinationRef`, `DestinationId`, `PreviousDestinationRef` của `financial_operation_audits` chỉ còn giữ cho bản ghi cũ (bảng append-only), bản ghi mới để null.
- Chuyển khoản supplier và hoàn cọc vẫn do admin làm ngoài hệ thống. Không gọi SePay để chuyển tiền. Tự động hoàn tiền/chuyển tiền qua provider là TODO sau MVP.

## Write contracts

| API | Quyền | Body và hiệu ứng |
| --- | --- | --- |
| `POST /api/v1/payment/admin/payouts/{payoutId}/complete` | Admin `adm`/`adp`, client audience | `OperationKey`, `ExternalTransferReference`, `AuditReason`. Server lấy `CompletedBy` từ `sub`, `CompletedAt` từ UTC clock. Chỉ hoàn tất payout đang `PENDING_PAYOUT`. |
| `POST /api/v1/payment/manual-refunds/{requirementId}/complete` | Admin `adm`/`adp`, client audience | `OperationKey`, `ExternalTransferReference`, `AuditReason`. Trạng thái hoàn cọc, audit và outbox được ghi trong cùng transaction. |
| `POST /api/v1/payment/admin/payments/unmatched/{transactionId}/resolve` | Admin `adm`/`adp`, client audience | `InvoiceRef`, `OperationKey`, `ExternalReference`, `AuditReason`. Chỉ nhận giao dịch `UNMATCHED` tiền vào, đúng ngân hàng/tài khoản, số tiền, mã thanh toán và còn trong hạn theo cả thời điểm ngân hàng lẫn thời điểm xử lý. Ghi resolution bất biến, invoice `PAID` và `InvoicePaid` outbox trong cùng transaction. Giao dịch trễ/sai tiền tiếp tục ở review; replay cùng key/payload không phát event lặp. |

`OperationKey` dài 1–64 ký tự; `ExternalReference`/`ExternalTransferReference` dài 1–256; `AuditReason` dài 1–500. Loại bỏ khoảng trắng đầu/cuối, từ chối chuỗi rỗng. Khóa idempotency cần ràng buộc với loại thao tác, target và fingerprint payload; replay cùng key/payload trả cùng kết quả với `IsReplay=true`, khác payload trả `409`. Một target đã hoàn tất với reference khác trả `409`. Ghi cập nhật trạng thái và audit record **append-only** trong cùng transaction. Không cho request đặt actor, timestamp, amount hoặc status.

## Read contracts

| API | Người đọc | Dữ liệu |
| --- | --- | --- |
| `GET /api/v1/payment/invoices/{invoiceRef}` | Renter sở hữu | Đã có; QR/thông tin chuyển khoản để thanh toán. |
| `GET /api/v1/payment/invoices/{invoiceRef}/status` | Renter sở hữu | Trạng thái, số tiền, deadline; không lộ thông tin nhận tiền hoặc webhook secret. |
| `GET /api/v1/payment/transactions/{id}` | Admin | Transaction và lý do cần review; không trả raw webhook body, account number hoặc credential. |
| `GET /api/v1/payment/settlements/{id}` | Admin, renter sở hữu order, supplier sở hữu item | Các số tiền đã snapshot và trạng thái; supplier chỉ thấy phần item của mình. |
| `GET /api/v1/payment/supplier/payouts` | Supplier từ JWT `sub` | Chỉ các payout có `SupplierId == sub`; không nhận supplier ID tùy ý từ query. |
| `GET /api/v1/payment/admin/payments/review?kind=...` | Admin | Chọn một trong `AwaitingPayment`, `UnmatchedTransaction`, `LateTransaction`, `ReviewRequiredTransaction`, `SettlementPendingClose`; phân trang từng hàng đợi. `LateTransaction` là giao dịch `MANUAL_REFUND_REQUIRED` có reason `LATE_PAYMENT`. |
| `GET /api/v1/payment/admin/manual-refunds` | Admin | Yêu cầu hoàn cọc pending. |
| `GET /api/v1/payment/admin/payouts` | Admin | Payout pending. |

Các danh sách dùng `page` (từ 1), `pageSize` (1–100, mặc định 20), `from`, `to`; danh sách payout/refund còn nhận `status`. Review dùng `kind` thay cho `status`. Response gồm `items`, `page`, `pageSize`, `totalCount`. Sắp xếp ổn định theo `CreatedAt`, rồi ID. Cần lọc theo quyền **trong truy vấn DB**, không tải toàn bộ rồi lọc ở controller. Admin dùng claim hiện có `adm`/`adp`; supplier/renter dùng client audience và `sub`, đối chiếu ownership từ Payment DB. Không dùng service token để xem queue admin.

## Trạng thái hoàn thành và audit

- `SupplierPayout` hoàn tất chỉ khi admin ghi mã giao dịch chuyển khoản thật. Unique constraint bảo đảm một settlement chỉ có một payout và một `OperationKey` không bị dùng sai.
- `ManualRefundRequirement` vẫn là nguồn sự thật cho refund; operation key và immutable audit đã được triển khai. Không sửa nghĩa của `RentalSettled`.
- Audit lưu operation ID/key, loại thao tác, target, actor, external reference, lý do và UTC time. Không sửa/xóa audit đã ghi.
- API đọc trả DTO riêng, không serialize entity, provider credential, số tài khoản đầy đủ hoặc raw webhook payload.
