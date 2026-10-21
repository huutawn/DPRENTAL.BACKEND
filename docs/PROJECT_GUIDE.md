# Project Guide & Domain Invariants — PaymentService

Tài liệu này là nguồn ngữ cảnh chính thức về nghiệp vụ, ranh giới trách nhiệm, quy chuẩn tài chính và kiến trúc kiểm chứng tính toàn vẹn (Payment Integrity) của **PaymentService** trong hệ sinh thái DPRENTAL.

---

## 1. Ranh Giới Trách Nhiệm: RentalService vs PaymentService

| Nghiệp vụ | RentalService sở hữu | PaymentService sở hữu |
|---|---|---|
| **Đặt thuê & Giá gốc** | Tạo đơn hàng, xác định thời gian thuê, tính tiền thuê và tiền cọc cơ bản. | Lưu snapshot hóa đơn cố định (`InvoiceItem`), cấp mã thanh toán (`PaymentCode`), sinh ảnh VietQR. |
| **Thanh toán ngân hàng** | Nhận thông báo trạng thái đơn hàng (qua sự kiện `InvoicePaid`). | Tiếp nhận Webhook SePay, chuẩn hóa dữ liệu ngân hàng, đối soát mã thanh toán, chuyển trạng thái `PAID` hoặc `EXPIRED` (thanh toán trễ). |
| **Quyết định Settlement** | Đánh giá hư hỏng, quyết định khấu trừ (`FinalDeductionAmount`), tiền phải hoàn (`RefundedDepositAmount`), số quyết định (`DecisionRef`), gửi `POST /api/v1/settlements`. | Đối chiếu quyết định với snapshot hóa đơn gốc, tính phí sàn (`PlatformFeeAmount`), xác định nghĩa vụ nhà cung cấp (`SupplierPayable`), tạo sổ cái và yêu cầu hoàn cọc (`ManualRefundRequirement`). |
| **Hoàn cọc thủ công** | Nhận sự kiện `RentalSettled` để cập nhật trạng thái kết thúc đơn hàng. | Quản lý yêu cầu hoàn cọc, ghi nhận xác nhận chuyển khoản của Admin (`ExternalTransferReference`), phát sự kiện `RentalSettled`. |

**Quy tắc bất biến:**
- PaymentService không đọc cơ sở dữ liệu của RentalService và không phụ thuộc vào bất kỳ bảng nào của Rental.
- Mọi quyết định tài chính của PaymentService dựa trên snapshot tại thời điểm thanh toán hóa đơn.

---

## 2. Vòng Đời Trạng Thái & Quy Chuẩn Tiền Tệ

### 2.1. Tiền tệ VND & Làm tròn
- Tiền tệ duy nhất: **VND** (Việt Nam Đồng). Số tiền bắt buộc là **số nguyên không âm** (không có phần thập phân). Kiểm tra bằng `VndAmountValidator`.
- Tỷ lệ phí nền tảng mặc định: `10%` (`PlatformFeeRate = 0.10m`).
- Quy tắc làm tròn: Làm tròn tới VND gần nhất theo quy tắc nửa lên (`Math.Round(amount, 0, MidpointRounding.AwayFromZero)`).
- Phí nền tảng chỉ tính trên tiền thuê (`RentalAmount`), **không tính phí trên tiền cọc** (`DepositAmount`).

### 2.2. Trạng thái Hóa Đơn (`InvoiceStatus`)
```text
[PENDING] ──(Thanh toán trước deadline)──> [PAID]
   │
   ├──(Quá deadline hoặc Hủy)────────────> [EXPIRED] / [CANCELLED]
   └──(Hóa đơn 0 đồng khi tạo)────────────> [PAID] (tự động phát InvoicePaid)
```

- **Hóa đơn 0 đồng:** Khởi tạo ngay trạng thái `PAID`, gán mã giao dịch hệ thống và phát sự kiện `InvoicePaid` trong cùng transaction.
- **Thanh toán trễ:** Nếu giao dịch ngân hàng xảy ra tại hoặc sau `PaymentDeadline`, hóa đơn chuyển `EXPIRED` (nếu đang `PENDING`), lưu transaction trạng thái `MANUAL_REFUND_REQUIRED` để Admin xử lý hoàn tiền thủ công. Không chuyển hóa đơn sang `PAID`.

### 2.3. Settlement & Nghĩa Vụ Nhà Cung Cấp (`SupplierPayable`)
- Trong MVP, `SupplierPayable` được ghi nhận vào sổ cái tài chính (`SettlementLedgerEntry`) là đủ điều kiện tài chính cho nghĩa vụ nhà cung cấp; hệ thống không chờ lệnh chi tiền thực tế (payout).
- Công thức tính toán:
  - `PlatformFeeAmount = Round(RentalAmount * 0.10, 0, AwayFromZero)`
  - `DepositRefundDue = DepositAmount - FinalDeductionAmount` (với `0 <= FinalDeductionAmount <= DepositAmount`)
  - `SupplierPayable = RentalAmount - PlatformFeeAmount + FinalDeductionAmount`
- Ràng buộc cân bằng tiền: `SupplierPayable + PlatformFeeAmount + DepositRefundDue == RentalAmount + DepositAmount`.

---

## 3. Hoàn Tiền Thủ Công (Manual Refund)

- Nếu `DepositRefundDue > 0`: Hệ thống tạo bản ghi `ManualRefundRequirement` trạng thái `PENDING_MANUAL_REFUND`.
- Admin thực hiện chuyển khoản ngoài hệ thống cho người thuê, sau đó gọi `POST /api/v1/manual-refunds/{id}/complete` kèm `ExternalTransferReference`.
- Sự kiện Kafka `RentalSettled` **chỉ được phép ghi vào outbox** khi:
  1. Khoản hoàn cọc bằng 0 (`DepositRefundDue == 0`), HOẶC
  2. Yêu cầu hoàn cọc thủ công đã được Admin xác nhận `COMPLETED`.

---

## 4. Idempotency, Concurrency & Row-level Locks

- **Idempotency Key:** Mọi thao tác ghi nhạy cảm (`CreateInvoice`, `CancelInvoice`, `SettleRentalItem`, `CompleteManualRefund`) yêu cầu `OperationKey`.
- **Replay an toàn:**
  - Nếu `OperationKey` đã tồn tại và fingerprint payload trùng khớp: trả về kết quả trước đó với `IsReplay = true` (HTTP 200 OK).
  - Nếu `OperationKey` trùng nhưng fingerprint khác: trả về 409 Conflict (`DUPLICATE_OPERATION_KEY`).
- **Khóa hàng (`SELECT ... FOR UPDATE`):**
  - Mọi thao tác cập nhật hóa đơn, matching webhook, settlement và hoàn cọc bắt buộc thực thi dưới khóa hàng PostgreSQL trong giao dịch để ngăn race condition và double-spending.

---

## 5. Kiến Trúc Kiểm Chứng Toàn Vẹn (Payment Integrity)

### 5.1. Mô hình Phân Tách Tiến Trình & Khóa Ký
- **Payment API:** Xử lý nghiệp vụ, tiếp nhận HTTP, không sở hữu và không nạp khóa riêng ECDSA. Chỉ giữ public key để xác minh receipt.
- **IntegrityHost:** Tiến trình độc lập (cổng `8081`), kết nối cơ sở dữ liệu bằng chứng riêng (`IntegrityDbContext` trên `integrity_evidence`), độc quyền sở hữu khóa riêng ECDSA P-256 (`IIntegritySigningAdapter`).

### 5.2. Chuỗi Merkle Sổ Cái Bất Biến (`transaction_journal`)
- Mọi biến động tài chính cốt lõi ghi nhận 1 bản ghi append-only vào `transaction_journal`.
- Schema Version 4: Payload được chuẩn hóa JSON kinh điển, mã hóa AES-GCM-256 với AAD ràng buộc `(schemaVersion, environment, streamId, version, eventId)`.
- Ràng buộc băm chuỗi: Mỗi bản ghi chứa `PreviousHash` trỏ tới `EntryHash` của bản ghi liền trước; bản ghi đầu tiên trỏ tới `GenesisHash`.

### 5.3. Phiên Bản Giao Thức (Protocol Versions)
- **Journal Schema:** Version 4 (chuẩn hóa kiểu dữ liệu VND, fee rate, UTC microsecond ISO 8601, AAD ràng buộc đầy đủ).
- **Verification Receipt:** Version 2 (signed receipt body bao phủ Schema, Issuer, Audience, Environment, ReceiptId, KeyId, RequestHash, CallerIdentity, CallerNonce, Purpose, IntegrityStatus, Usability, ActualAmount, ActualCurrency, ActualStatus, EntityClaims, TTL).
- **Attestation Message:** Version 2 (`DPRENTAL:ATTESTATION:V2`).
- **Kafka Proof:** Version 3 (gắn đầy đủ metadata, routing topic/key và chữ ký chứng thực).

### 5.4. Ranh Giới Đe Dọa & Giới Hạn Cần Biết
- AES-GCM bảo vệ dữ liệu khi lưu trữ (at-rest); TLS/mTLS bảo vệ dữ liệu trên đường truyền (in-transit). PaymentService vẫn giải mã để xử lý nghiệp vụ.
- Chữ ký DPRENTAL chứng minh nội dung đã được IntegrityHost xác nhận; không tự chứng minh tiền thực tế đã chuyển tại ngân hàng.
- Kiểm chứng nhanh (Fast Check) qua HTTP xác minh snapshot và bằng chứng tại thời điểm gọi (SLO p99 < 1s). Scanner kiểm tra toàn bộ lịch sử theo chu kỳ; không tuyên bố phát hiện tức thời mọi thay đổi ngầm ở mọi bản ghi.
- Hai database trên cùng một VPS không bảo đảm an toàn khi root hệ thống hoặc khóa ký bị kiểm soát.

---

## 6. Danh Mục Route & Scopes Hiện Hành

| Phương thức | Đường dẫn | Scopes / Quyền | Mục đích |
|---|---|---|---|
| `POST` | `/api/v1/invoices` | `u=svc`, `payment.invoice.write` | Tạo mới hoặc replay hóa đơn từ đơn hàng Rental. |
| `POST` | `/api/v1/invoices/{ref}/cancel` | `u=svc`, `payment.invoice.write` | Hủy hóa đơn chưa thanh toán. |
| `GET` | `/api/v1/invoices/{ref}` | `aud=dprental-clients`, `sub == RenterId` | Lấy thông tin hóa đơn và ảnh VietQR SePay. |
| `POST` | `/api/v1/payment/webhooks` | `Authorization: Apikey <key>` | Tiếp nhận webhook chuyển khoản từ cổng SePay. |
| `POST` | `/api/v1/settlements` | `u=svc`, `payment.settlement.write` | Tiếp nhận lệnh settlement và ghi sổ nghĩa vụ tài chính. |
| `POST` | `/api/v1/manual-refunds/{id}/complete` | `aud=dprental-clients`, role `adm`/`adp` | Admin xác nhận đã hoàn cọc thủ công. |
| `POST` | `/api/v1/payment/verifications` | `u=svc`, `payment.transaction.verify` | Kiểm chứng tính toàn vẹn và khả năng sử dụng của giao dịch. |
| `GET` | `/api/v1/payment/history` | `payment.transaction.history` hoặc Admin | Tra cứu lịch sử journal phân trang cursor. |
