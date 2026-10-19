# Payment operations contract v1

> Cập nhật subtask 4: API xác nhận hoàn cọc đã yêu cầu `OperationKey`, `ExternalTransferReference`, `AuditReason`; trạng thái hoàn cọc, settlement, audit append-only và `RentalSettled` outbox được ghi trong cùng transaction. Bản ghi hoàn cọc cũ đã hoàn tất trước migration không được tạo audit giả; thao tác với key mới trên bản ghi đó trả `409`. Migration dừng với thông báo rõ nếu mã chuyển khoản hoàn cọc cũ bị trùng và cần đối soát.

Status: **Đã triển khai subtask 1–7 của Supplier Payable, Manual Payout & Payment Operations.** Phần payout destination hiện có contract, API và migration; xem `docs/payment-payout-destination-mvp.md` cho luồng đang áp dụng. Các ghi chú bên dưới về destination "chưa triển khai" là lịch sử thiết kế.

## Ranh giới và quyết định

- Mỗi settlement có đúng một `SupplierPayable` cho một `RentalItemId` và `SupplierId`. Tiền lấy từ `RentalItemSettlementRecord.SupplierPayable` đã snapshot; không tính lại từ dữ liệu Rental hay gộp nhiều supplier của cùng order.
- Tạo một `SupplierPayout` cho từng settlement **có `SupplierPayable > 0`**, số tiền/currency/supplier/item bất biến. Nếu payable bằng 0 thì ledger vẫn ghi nhận nhưng không có nghĩa vụ chuyển khoản. Trạng thái `PENDING_PAYOUT` → `COMPLETED` sau khi admin thực sự chuyển khoản. Không có API đổi trạng thái tùy ý.
- `RentalSettled` tiếp tục theo contract hiện hành: ledger ghi nhận supplier payable là đủ; nếu có refund due thì chờ xác nhận hoàn cọc. Payout supplier được theo dõi độc lập, không chặn hay phát lại `RentalSettled`.
- Payment không nhận tài khoản supplier từ Rental. MVP dùng **Payment-owned SupplierPayoutDestination**: admin nhập và xác minh thông tin nhận tiền với supplier ngoài hệ thống; Payment lưu bản ghi theo `SupplierId` và tạo `DestinationRef` cho phiên bản bất biến đó. Thay đổi tài khoản phải tạo phiên bản mới, không sửa bản ghi cũ. Payout chỉ lưu `DestinationRef`; dữ liệu ngân hàng không nằm trong Rental, payout DTO hoặc audit DTO. Nếu chưa có destination đã xác minh, payout ở `PENDING_PAYOUT` và không thể xác nhận hoàn tất.
- Chuyển khoản supplier và hoàn cọc vẫn do admin làm ngoài hệ thống. Không gọi SePay để chuyển tiền. Tự động hoàn tiền/chuyển tiền qua provider là TODO sau MVP.

## Write contracts

| API | Quyền | Body và hiệu ứng |
| --- | --- | --- |
| `POST /api/v1/admin/payouts/{payoutId}/destination` | Admin `adm`/`adp`, client audience | `OperationKey`, `DestinationRef`, `AuditReason`. Payment phải tra `SupplierPayoutDestination` đã xác minh và kiểm tra `destination.SupplierId == payout.SupplierId` trước khi gắn; không tin reference do admin gửi là đủ bằng chứng. Bước tra cứu này cần migration/triển khai tiếp, chưa có trong API hiện tại. |
| `POST /api/v1/admin/payouts/{payoutId}/complete` | Admin `adm`/`adp`, client audience | `OperationKey`, `ExternalTransferReference`, `AuditReason`. Server lấy `CompletedBy` từ `sub`, `CompletedAt` từ UTC clock. Chỉ hoàn tất payout pending có destination hợp lệ. |
| `POST /api/v1/manual-refunds/{requirementId}/complete` | Admin `adm`/`adp`, client audience | `OperationKey`, `ExternalTransferReference`, `AuditReason`. Trạng thái hoàn cọc, audit và outbox được ghi trong cùng transaction. |
| `POST /api/v1/admin/payments/unmatched/{transactionId}/resolve` | Admin `adm`/`adp`, client audience | `InvoiceRef`, `OperationKey`, `ExternalReference`, `AuditReason`. Chỉ nhận giao dịch `UNMATCHED` tiền vào, đúng ngân hàng/tài khoản, số tiền, mã thanh toán và còn trong hạn theo cả thời điểm ngân hàng lẫn thời điểm xử lý. Ghi resolution bất biến, invoice `PAID` và `InvoicePaid` outbox trong cùng transaction. Giao dịch trễ/sai tiền tiếp tục ở review; replay cùng key/payload không phát event lặp. |

`OperationKey` dài 1–64 ký tự; `ExternalReference`/`ExternalTransferReference` dài 1–256; `AuditReason` dài 1–500. Loại bỏ khoảng trắng đầu/cuối, từ chối chuỗi rỗng. Khóa idempotency cần ràng buộc với loại thao tác, target và fingerprint payload; replay cùng key/payload trả cùng kết quả với `IsReplay=true`, khác payload trả `409`. Một target đã hoàn tất với reference khác trả `409`. Ghi cập nhật trạng thái và audit record **append-only** trong cùng transaction. Không cho request đặt actor, timestamp, amount hoặc status.

### Contract nguồn DestinationRef đã chốt; migration làm sau

- Bảng Payment `supplier_payout_destinations`: `Id`, `DestinationRef` (unique), `SupplierId`, `Version`, `BankCode`, `AccountNumber`, `AccountHolderName`, `VerificationRef`, `VerifiedBy`, `VerifiedAt`, `CreatedAt`. Không trả số tài khoản đầy đủ qua API payout/queue; chỉ admin vận hành được đọc dữ liệu cần chuyển khoản. Cần bảo vệ dữ liệu ngân hàng khi lưu và truy cập theo cấu hình bảo mật của Payment.
- Admin đăng ký phiên bản destination sau khi kiểm tra quyền sở hữu tài khoản ngoài hệ thống. `VerificationRef` trỏ tới hồ sơ/bằng chứng xác minh; Payment không tự tuyên bố xác minh qua SePay. Trùng `SupplierId + Version` hoặc `DestinationRef` bị từ chối.
- Khi gắn destination, truy vấn bản ghi `VerifiedAt != null`, so sánh `SupplierId` với payout trong cùng transaction. Lưu reference của **phiên bản đã chọn** trên payout; tài khoản được cập nhật sau đó không thay đổi đích của payout cũ.
- Migration tạo bảng và bổ sung truy vấn kiểm tra sẽ được làm sau. Cho tới khi có bước kiểm tra đó, API `/destination` hiện chỉ lưu chuỗi admin gửi và **chưa đạt điều kiện an toàn để xác nhận payout thật**.

## Read contracts

| API | Người đọc | Dữ liệu |
| --- | --- | --- |
| `GET /api/v1/invoices/{invoiceRef}` | Renter sở hữu | Đã có; QR/thông tin chuyển khoản để thanh toán. |
| `GET /api/v1/invoices/{invoiceRef}/status` | Renter sở hữu | Trạng thái, số tiền, deadline; không lộ thông tin nhận tiền hoặc webhook secret. |
| `GET /api/v1/transactions/{id}` | Admin | Transaction và lý do cần review; không trả raw webhook body, account number hoặc credential. |
| `GET /api/v1/settlements/{id}` | Admin, renter sở hữu order, supplier sở hữu item | Các số tiền đã snapshot và trạng thái; supplier chỉ thấy phần item của mình. |
| `GET /api/v1/supplier/payouts` | Supplier từ JWT `sub` | Chỉ các payout có `SupplierId == sub`; không nhận supplier ID tùy ý từ query. |
| `GET /api/v1/admin/payments/review?kind=...` | Admin | Chọn một trong `AwaitingPayment`, `UnmatchedTransaction`, `LateTransaction`, `ReviewRequiredTransaction`, `SettlementPendingClose`; phân trang từng hàng đợi. `LateTransaction` là giao dịch `MANUAL_REFUND_REQUIRED` có reason `LATE_PAYMENT`. |
| `GET /api/v1/admin/manual-refunds` | Admin | Yêu cầu hoàn cọc pending. |
| `GET /api/v1/admin/payouts` | Admin | Payout pending, gồm trạng thái destination. |

Các danh sách dùng `page` (từ 1), `pageSize` (1–100, mặc định 20), `from`, `to`; danh sách payout/refund còn nhận `status`. Review dùng `kind` thay cho `status`. Response gồm `items`, `page`, `pageSize`, `totalCount`. Sắp xếp ổn định theo `CreatedAt`, rồi ID. Cần lọc theo quyền **trong truy vấn DB**, không tải toàn bộ rồi lọc ở controller. Admin dùng claim hiện có `adm`/`adp`; supplier/renter dùng client audience và `sub`, đối chiếu ownership từ Payment DB. Không dùng service token để xem queue admin.

## Trạng thái hoàn thành và audit

- `SupplierPayout` hoàn tất chỉ khi admin ghi mã giao dịch chuyển khoản thật. Unique constraint bảo đảm một settlement chỉ có một payout và một `OperationKey` không bị dùng sai.
- `ManualRefundRequirement` vẫn là nguồn sự thật cho refund; operation key và immutable audit đã được triển khai. Không sửa nghĩa của `RentalSettled`.
- Audit lưu operation ID/key, loại thao tác, target, actor, external reference, lý do và UTC time. Không sửa/xóa audit đã ghi.
- API đọc trả DTO riêng, không serialize entity, provider credential, số tài khoản đầy đủ hoặc raw webhook payload.
