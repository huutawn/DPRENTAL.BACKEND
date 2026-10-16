# Contract: Tạo hóa đơn qua Kafka (Rental ⇄ Payment) — v1

| Mục | Giá trị |
| --- | --- |
| Trạng thái | **PROPOSED** — chưa được team Payment xác nhận. Chỉ code theo contract này sau khi đổi sang **FROZEN**. |
| Bên gửi lệnh | RentalService |
| Bên xử lý lệnh | PaymentService |
| Thay thế cho | `POST /api/v1/invoices` (HTTP, Payment Contract v1) — chỉ phần **tạo hóa đơn** |
| Không thay đổi | `InvoicePaid`, `RefundCompleted`, HTTP `cancel`, `refund`, `settle` |

---

## 1. Mục tiêu và phạm vi

Rental hiện tạo hóa đơn bằng cách gọi HTTP đồng bộ tới Payment sau khi đơn đã được tạo (giữ đủ máy ở Inventory). Contract này chuyển bước đó sang Kafka:

```text
Rental ──CreateInvoice──►   payment.commands.v1  ──► Payment
Rental ◄──InvoiceCreated──  payment.events.v1    ◄── Payment
Rental ◄──InvoiceRejected── payment.events.v1    ◄── Payment
Rental ◄──InvoicePaid─────  payment.events.v1    ◄── Payment   (đã có, không đổi)
```

**Trong phạm vi:** lệnh `CreateInvoice`, hai event trả lời `InvoiceCreated` / `InvoiceRejected`, quy tắc idempotency, thứ tự, phân loại lỗi, rollout.

**Ngoài phạm vi:** hủy hóa đơn, hoàn tiền, quyết toán cọc, schema `InvoicePaid` (giữ nguyên như hiện tại), QR/VietQR (FE lấy trực tiếp từ Payment bằng `invoiceRef`).

**Nguyên tắc quan trọng:** broker ack lệnh `CreateInvoice` chỉ có nghĩa là message đã nằm trong Kafka, **không** có nghĩa Payment đã tạo hóa đơn. Rental chỉ coi hóa đơn tồn tại khi nhận được `InvoiceCreated`.

---

## 2. Topic

| Topic | Chiều | Bên ghi | Consumer group bên đọc | Ghi chú |
| --- | --- | --- | --- | --- |
| `payment.commands.v1` | Rental → Payment | RentalService | `payment-commands-consumer` | Topic mới |
| `payment.events.v1` | Payment → Rental | PaymentService | `rental-payment-events-consumer` | Đã có |
| `payment.commands.v1.dlq` | nội bộ Payment | PaymentService | — | Message lệnh lỗi schema / xung đột |
| `payment.events.v1.dlq` | nội bộ Rental | RentalService | — | Đã có |

Cấu hình topic:

- `cleanup.policy=delete`, retention **≥ 7 ngày** (lớn hơn nhiều so với hạn thanh toán 30 phút, đủ để consumer down vài giờ vẫn đọc lại được).
- Số partition: theo hạ tầng chung, **không thay đổi sau khi đã chạy production** vì đổi số partition sẽ làm đổi ánh xạ key → partition và phá thứ tự (mục 4).
- Phân quyền (ACL), nếu bật: chỉ principal của RentalService được **ghi** `payment.commands.v1`; chỉ PaymentService được **ghi** `payment.events.v1`. ACL thay cho scope `payment.invoice.write` của HTTP.

---

## 3. Envelope (dùng chung cho cả 3 message)

```json
{
  "messageId": "GUID",
  "messageType": "CreateInvoice | InvoiceCreated | InvoiceRejected",
  "version": 1,
  "producer": "RentalService | payment-service",
  "aggregateId": "RentalOrderId (GUID, định dạng D)",
  "occurredAt": "ISO-8601 có offset, khuyến nghị UTC (Z)",
  "correlationId": "RentalOrderId (GUID, định dạng D)",
  "causationId": "GUID dạng chuỗi | null",
  "payload": { }
}
```

| Field | Quy tắc |
| --- | --- |
| `messageId` | **Tất định** theo mục 6.1. Không dùng `Guid.NewGuid()`. |
| `messageType` | Đúng chính tả, PascalCase như bảng trên. Bên nhận so sánh không phân biệt hoa thường. |
| `version` | `1`. |
| `producer` | Rental ghi `"RentalService"` (giá trị code hiện tại). Payment ghi `"payment-service"` (giá trị code hiện tại). Bên nhận phải chấp nhận đúng các giá trị này. |
| `aggregateId` | Luôn là `RentalOrderId`. |
| `correlationId` | Rental đặt = `RentalOrderId`. Payment **copy nguyên** `correlationId` của lệnh sang event trả lời. |
| `causationId` | Lệnh `CreateInvoice`: `null`. Event trả lời: `messageId` của lệnh `CreateInvoice` gây ra nó, dạng chuỗi GUID. |
| `occurredAt` | Xem mục 6.2 — phải **ổn định** khi phát lại. |
| Field lạ | Bên nhận **bỏ qua** field không biết (để thêm field optional về sau không phá contract). |

---

## 4. Partition key và thứ tự

- Kafka message key = `RentalOrderId` (GUID định dạng `D`, ví dụ `6f2a1c3e-…`) cho **mọi** message trong contract này, ở cả hai chiều, và cho `InvoicePaid`.
- Kafka chỉ đảm bảo thứ tự trong một partition; cùng key ⇒ cùng partition ⇒ các event của một đơn được đọc đúng thứ tự ghi.
- **Yêu cầu với Payment:** với một đơn, `InvoiceCreated` phải được **ghi vào outbox trước** và **publish trước** `InvoicePaid`. Trường hợp đặc biệt: hóa đơn tổng tiền = 0 hiện được Payment đánh dấu `InvoicePaid` ngay khi tạo — khi đó cả hai event ghi trong cùng transaction, `InvoiceCreated` trước, và outbox publisher của Payment phải publish theo đúng thứ tự chèn cho cùng một key.

---

## 5. Message

### 5.1. `CreateInvoice` (Rental → Payment, topic `payment.commands.v1`)

`payload` chính là body của `POST /api/v1/invoices` hiện tại (`CreateInvoiceRequest`), **phẳng, không lồng thêm lớp**. Payment dùng lại DTO và `Validate()` đang có.

```json
{
  "messageId": "1b7d2c0e-5a43-0f9e-8c21-3d4e5f60718a",
  "messageType": "CreateInvoice",
  "version": 1,
  "producer": "RentalService",
  "aggregateId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "occurredAt": "2026-10-16T03:00:00Z",
  "correlationId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "causationId": null,
  "payload": {
    "rentalOrderId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
    "orderCode": "ORD-000123",
    "renterId": "aa11bb22-cc33-4d44-8e55-ff6677889900",
    "currency": "VND",
    "paymentDeadline": "2026-10-16T03:30:00Z",
    "items": [
      {
        "rentalItemId": "11111111-2222-4333-8444-555555555555",
        "supplierId": "99999999-8888-4777-8666-555555555555",
        "rentalAmount": 3000000,
        "depositAmount": 20000000
      },
      {
        "rentalItemId": "66666666-7777-4888-8999-000000000000",
        "supplierId": "12121212-3434-4565-8787-909090909090",
        "rentalAmount": 1500000,
        "depositAmount": 5000000
      }
    ],
    "operationKey": "3f9a0c1d2e4b5a6978c0d1e2f3a4b5c6"
  }
}
```

| Field | Kiểu | Bắt buộc | Quy tắc |
| --- | --- | --- | --- |
| `rentalOrderId` | GUID | có | Khác `Guid.Empty`, trùng `aggregateId` và message key |
| `orderCode` | string | có | 1–64 ký tự |
| `renterId` | GUID | có | Khác `Guid.Empty` |
| `currency` | string | có | Chỉ `"VND"` |
| `paymentDeadline` | datetime | có | Lớn hơn thời điểm Payment xử lý |
| `items` | array | có | ≥ 1 phần tử, không `null`, `rentalItemId` không trùng |
| `items[].rentalItemId` | GUID | có | Khác `Guid.Empty` |
| `items[].supplierId` | GUID | có | Khác `Guid.Empty` |
| `items[].rentalAmount` | number | có | Số nguyên VND, ≥ 0, ≤ giới hạn `VndAmountValidator` |
| `items[].depositAmount` | number | có | Số nguyên VND, ≥ 0, ≤ giới hạn `VndAmountValidator` |
| `operationKey` | string | có | 1–64 ký tự; Rental tính tất định = `ComputeOperationKey("CreateInvoice", rentalOrderId)` (32 ký tự hex) |

Số tiền là số nguyên VND. Bên nhận parse thành `decimal` và không phụ thuộc định dạng chuỗi (`3000000` và `3000000.00` là cùng giá trị).

### 5.2. `InvoiceCreated` (Payment → Rental, topic `payment.events.v1`)

Phát khi hóa đơn được tạo mới **hoặc** đã tồn tại cho đúng `operationKey` này (replay).

```json
{
  "messageId": "a4c1e9f0-7b32-1d5e-9f80-1a2b3c4d5e6f",
  "messageType": "InvoiceCreated",
  "version": 1,
  "producer": "payment-service",
  "aggregateId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "occurredAt": "2026-10-16T03:00:02Z",
  "correlationId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "causationId": "1b7d2c0e-5a43-0f9e-8c21-3d4e5f60718a",
  "payload": {
    "rentalOrderId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
    "operationKey": "3f9a0c1d2e4b5a6978c0d1e2f3a4b5c6",
    "invoiceRef": "INV-20261016-000123",
    "totalAmount": 29500000,
    "currency": "VND",
    "paymentDeadline": "2026-10-16T03:30:00Z"
  }
}
```

| Field | Kiểu | Quy tắc |
| --- | --- | --- |
| `rentalOrderId` | GUID | Lấy từ lệnh |
| `operationKey` | string | Lấy từ lệnh — Rental dùng để đối chiếu đúng lệnh |
| `invoiceRef` | string | 1–256 ký tự, không đổi suốt vòng đời hóa đơn |
| `totalAmount` | number | Tổng `rentalAmount + depositAmount` của mọi item, số nguyên VND |
| `currency` | string | `"VND"` |
| `paymentDeadline` | datetime | Hạn thanh toán Payment đã lưu (thường bằng giá trị trong lệnh) |

Không đưa vào event: thông tin ngân hàng, số tài khoản, nội dung QR, `paymentCode`.

### 5.3. `InvoiceRejected` (Payment → Rental, topic `payment.events.v1`)

Phát khi lệnh hợp lệ về schema nhưng **không thể** tạo hóa đơn vì lý do nghiệp vụ (bảng mục 7).

```json
{
  "messageId": "c7e2a1b0-9d84-3f6a-8b10-2a3b4c5d6e7f",
  "messageType": "InvoiceRejected",
  "version": 1,
  "producer": "payment-service",
  "aggregateId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "occurredAt": "2026-10-16T03:00:02Z",
  "correlationId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
  "causationId": "1b7d2c0e-5a43-0f9e-8c21-3d4e5f60718a",
  "payload": {
    "rentalOrderId": "6f2a1c3e-8b7d-4e21-9a10-2c3d4e5f6a7b",
    "operationKey": "3f9a0c1d2e4b5a6978c0d1e2f3a4b5c6",
    "errorCode": "INVALID_PAYMENT_DEADLINE",
    "reason": "Hạn thanh toán phải lớn hơn thời điểm tạo mới."
  }
}
```

| Field | Kiểu | Quy tắc |
| --- | --- | --- |
| `rentalOrderId` | GUID | Lấy từ lệnh |
| `operationKey` | string | Lấy từ lệnh |
| `errorCode` | string | Một trong các mã ở mục 7, tối đa 128 ký tự |
| `reason` | string | Mô tả ngắn, tối đa 500 ký tự, không chứa stack trace / dữ liệu nhạy cảm |

---

## 6. Idempotency

Kafka giao message **at-least-once**: mỗi message có thể tới nhiều lần. Cả hai bên phải xử lý trùng an toàn.

### 6.1. `messageId` tất định

Cả hai bên đều là .NET, dùng cùng thuật toán với `OutboxMessageIdGenerator` của Rental:

```csharp
string raw  = $"{producer}:{messageType}:{operationKey}";
byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
Guid   id   = new Guid(hash.AsSpan(0, 16));
```

| Message | `producer` trong công thức | `messageType` |
| --- | --- | --- |
| `CreateInvoice` | `RentalService` | `CreateInvoice` |
| `InvoiceCreated` | `payment-service` | `InvoiceCreated` |
| `InvoiceRejected` | `payment-service` | `InvoiceRejected` |

`operationKey` là giá trị trong payload lệnh.

### 6.2. Nội dung phát lại phải giống hệt

Rental (và Payment, nếu dùng cùng cơ chế) so sánh **toàn bộ envelope** đã chuẩn hóa (canonical JSON) khi gặp lại một `messageId`:

- Giống hệt → `Duplicate`, bỏ qua an toàn.
- Cùng `messageId` nhưng **khác nội dung** (kể cả chỉ khác `occurredAt`) → coi là xung đột, chuyển vào DLQ với mã `MESSAGE_CONFLICT`.

Vì vậy:

1. Mỗi event trả lời được dựng **một lần** và ghi vào outbox với `messageId` tất định; ghi lại cùng `messageId` thì **giữ bản đầu tiên** (`INSERT … ON CONFLICT DO NOTHING`), không bao giờ cập nhật nội dung.
2. `InvoiceCreated.occurredAt` = thời điểm tạo hóa đơn đã lưu (`Invoice.CreatedAt`), **không** phải thời điểm xử lý replay.
3. Publisher gửi lại đúng chuỗi JSON đã lưu trong outbox, không serialize lại.

### 6.3. Phía Payment khi nhận `CreateInvoice`

1. Inbox theo `messageId`: đã xử lý → bỏ qua và commit offset.
2. Gọi logic tạo hóa đơn hiện có (`InvoiceCreationService`), vốn đã idempotent theo `operationKey`:
   - Tạo mới → ghi hóa đơn + outbox `InvoiceCreated` trong **một transaction**.
   - `IsReplay = true` → ghi outbox `InvoiceCreated` (mục 6.2 đảm bảo không đổi nội dung nếu đã có).
3. Chỉ commit offset sau khi transaction ở bước 2 đã commit, hoặc message đã vào DLQ.

### 6.4. Phía Rental khi nhận event trả lời

1. Inbox theo `messageId` (đã có sẵn).
2. Đối chiếu `payload.operationKey` với receipt `CreateInvoice` của đơn; không khớp → bỏ qua, ghi log cảnh báo.
3. `AttachInvoice` đã idempotent: gắn lại cùng `invoiceRef` là thành công; khác `invoiceRef` → lỗi, không ghi đè.

---

## 7. Phân loại lỗi phía Payment

Nguyên tắc: **lỗi nghiệp vụ → phải trả `InvoiceRejected`**; **lỗi hạ tầng tạm thời → tự retry, không trả lời, không commit offset**; **lỗi schema → DLQ**.

| Tình huống | Mã lỗi (code Payment hiện có) | Payment làm gì | Rental nhận |
| --- | --- | --- | --- |
| Tạo mới thành công | — | Lưu hóa đơn + outbox | `InvoiceCreated` |
| Replay cùng `operationKey` | — (`IsReplay = true`) | Không tạo mới | `InvoiceCreated` (trùng, Rental dedupe) |
| GUID rỗng | `Payment.InvalidId` | Không retry | `InvoiceRejected` |
| Mã đơn sai | `Payment.InvalidOrderCode` | Không retry | `InvoiceRejected` |
| Tiền tệ khác VND | `Payment.InvalidCurrency` | Không retry | `InvoiceRejected` |
| `operationKey` sai | `Payment.InvalidOperationKey` | Không retry | `InvoiceRejected` |
| Không có item / item null / trùng item | `Payment.EmptyItems`, `Payment.NullItem`, `Payment.DuplicateItemId` | Không retry | `InvoiceRejected` |
| Số tiền không hợp lệ | các mã của `VndAmountValidator` | Không retry | `InvoiceRejected` |
| Quá hạn thanh toán | `INVALID_PAYMENT_DEADLINE` | Không retry | `InvoiceRejected` |
| Đơn đã có hóa đơn từ `operationKey` **khác** | `INVOICE_ALREADY_EXISTS` | Không retry, cảnh báo vận hành | `InvoiceRejected` |
| Hết lượt sinh mã thanh toán | `PAYMENT_CODE_COLLISION_EXHAUSTED` | Retry có backoff tối đa 3 vòng, sau đó reject | `InvoiceRejected` sau 3 vòng |
| DB / mạng lỗi, timeout | — | `Seek` lại offset, retry có backoff, **không** commit | Không nhận gì (chờ) |
| JSON hỏng, thiếu field envelope, sai `producer` | — | Ghi `payment.commands.v1.dlq`, commit offset | Không nhận gì |
| Cùng `messageId` nhưng khác nội dung | `MESSAGE_CONFLICT` | Ghi DLQ, commit offset | Không nhận gì |

Rental không đặt timeout riêng cho hóa đơn: nếu không nhận được trả lời, **hạn thanh toán của đơn** (30 phút) là chốt chặn cuối (mục 8).

---

## 8. Hành vi phía Rental

| Tình huống | Rental làm gì |
| --- | --- |
| Đơn được tạo (đủ hold) | Trong **cùng transaction** tạo đơn: ghi outbox `CreateInvoice` + receipt `CreateInvoice` (Pending). Trạng thái checkout operation: `invoiceStatus = Pending`. |
| Nhận `InvoiceCreated`, đơn `PENDING_PAYMENT`, chưa quá hạn, `totalAmount` và `currency` khớp đơn | `AttachInvoice(invoiceRef)`, receipt Succeeded, `invoiceStatus = Ready`. FE poll thấy `invoiceRef` và hiển thị QR. |
| Nhận `InvoiceCreated` nhưng `totalAmount`/`currency` **lệch** đơn | Không gắn hóa đơn; xử lý như bị từ chối với mã nội bộ `INVOICE_AMOUNT_MISMATCH`; ghi log lỗi. Hóa đơn bên Payment tự hết hạn theo `paymentDeadline`. |
| Nhận `InvoiceCreated` khi đơn đã `EXPIRED` / kết thúc | Đánh dấu inbox đã xử lý, không làm gì. Hóa đơn bên Payment tự hết hạn cùng hạn (`InvoiceExpiryWorker`). Nếu renter vẫn trả tiền muộn, luồng hoàn tiền thanh toán muộn hiện có xử lý. |
| Nhận `InvoiceRejected` | Gửi `ReleaseAllocation` cho mọi hold của đơn, kết thúc đơn, `invoiceStatus = Failed` kèm `errorCode` để FE hiển thị. |
| Không nhận được trả lời trước `paymentDeadline` | Luồng hết hạn thanh toán hiện có: release hold, đơn `EXPIRED`. |
| Nhận `InvoicePaid` | Không đổi so với hiện tại. |

---

## 9. Rollout

Rental giữ đường HTTP song song, bật/tắt bằng cấu hình `Kafka:CreateInvoiceViaKafka` (mặc định `false`).

1. Contract chuyển **FROZEN**.
2. Payment deploy consumer `payment.commands.v1` + event trả lời. Chưa có lệnh nào được gửi tới, không ảnh hưởng luồng hiện tại.
3. Rental deploy code mới với `CreateInvoiceViaKafka = false` (vẫn dùng HTTP).
4. Bật `true` trên staging, chạy bộ kiểm thử mục 10, sau đó bật production.
5. Rollback: đặt lại `false`. Lệnh đã nằm trong Kafka vẫn được Payment xử lý và Rental vẫn nhận trả lời.

Trong lúc chuyển, một đơn có thể vừa được tạo hóa đơn qua HTTP vừa qua Kafka. Điều này an toàn vì cả hai đường dùng **cùng `operationKey`**: Payment trả về cùng một hóa đơn, và Rental gắn lại cùng `invoiceRef` không gây lỗi.

---

## 10. Kịch bản kiểm thử chấp nhận

| # | Kịch bản | Kỳ vọng |
| --- | --- | --- |
| 1 | Happy path | Đơn có `invoiceRef`, `invoiceStatus = Ready`, tổng tiền khớp |
| 2 | Rental gửi `CreateInvoice` 2 lần (outbox retry) | Payment chỉ có 1 hóa đơn; Rental nhận `InvoiceCreated` trùng và dedupe |
| 3 | Payment phát `InvoiceCreated` 2 lần | Lần 2 là `Duplicate`, không vào DLQ |
| 4 | Payment down 10 phút rồi lên lại | Hóa đơn được tạo sau khi Payment lên, đơn vẫn thanh toán được |
| 5 | `paymentDeadline` đã qua khi Payment xử lý | `InvoiceRejected` với `INVALID_PAYMENT_DEADLINE`; Rental release hold |
| 6 | `InvoiceCreated` tới sau khi đơn `EXPIRED` | Rental bỏ qua, không gắn hóa đơn |
| 7 | `totalAmount` lệch | Rental không gắn hóa đơn, xử lý như bị từ chối |
| 8 | Hóa đơn tổng tiền = 0 | `InvoiceCreated` tới trước `InvoicePaid`; đơn sang chờ supplier xác nhận |
| 9 | JSON hỏng trên `payment.commands.v1` | Vào `payment.commands.v1.dlq`, partition không bị chặn |
| 10 | Đang chuyển đổi: cùng đơn tạo qua HTTP và Kafka | Một hóa đơn duy nhất, cùng `invoiceRef` |

---

## 11. Thay đổi cần làm so với code hiện tại

**RentalService**

- Làm phẳng `CreateInvoiceCommandPayload` thành đúng `CreateInvoiceRequest` (mục 5.1). Producer này chưa được dùng ở đâu nên đổi không ảnh hưởng gì.
- Gửi `CreateInvoice` qua outbox trong transaction tạo đơn; `ExecuteCreateInvoiceAsync` không gọi HTTP khi `CreateInvoiceViaKafka = true`.
- Thêm `InvoiceCreated`, `InvoiceRejected` vào `KafkaContractSpec.EventTypes` và vào bước intake của `PaymentEventsDispatcher` (hiện các loại này bị `Ignored`), kèm phần xử lý ở mục 8.
- Cập nhật `Application/Kafka/README.md` (mục 2.1 đã lỗi thời).

**PaymentService**

- Consumer `payment.commands.v1` (group `payment-commands-consumer`): tắt auto-commit, Inbox theo `messageId`, DLQ `payment.commands.v1.dlq`.
- Chấp nhận `producer = "RentalService"`.
- Phát `InvoiceCreated` / `InvoiceRejected` qua outbox với `messageId` tất định (mục 6.1) và nội dung ổn định (mục 6.2). Hiện `PaymentOutboxFactory` dùng `Guid.NewGuid()` và chỉ hỗ trợ `InvoicePaid`.
- Đảm bảo thứ tự `InvoiceCreated` trước `InvoicePaid` cho cùng một đơn (mục 4).
- `causationId` là chuỗi GUID (mục 3).

---

## 12. Câu hỏi mở (cần chốt trước khi FROZEN)

1. Số partition của `payment.commands.v1` trên môi trường chung.
2. Giới hạn retry cho `PAYMENT_CODE_COLLISION_EXHAUSTED` (đề xuất 3 vòng).
3. Payment có muốn dùng kiểu reply chung `PaymentCommandSucceeded` / `PaymentCommandRejected` (giống Inventory) thay cho `InvoiceCreated` / `InvoiceRejected` không. Đề xuất giữ tên riêng vì chỉ có một loại lệnh.
4. (Nội bộ Rental, không ảnh hưởng contract) trạng thái kết thúc của đơn khi nhận `InvoiceRejected`.

---

## Lịch sử thay đổi

| Ngày | Phiên bản | Nội dung |
| --- | --- | --- |
| 2026-10-16 | v1 PROPOSED | Bản đề xuất đầu tiên |
