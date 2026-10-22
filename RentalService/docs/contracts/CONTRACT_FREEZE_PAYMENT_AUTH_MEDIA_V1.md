# DPRental — Contract Specification Freeze v1
## Rental Integration with Payment, Auth/Profile, and Media Services

- **Document Version:** 1.0.0 (Frozen Specification)
- **Effective Date:** 2026-10-08
- **Consumer Service:** `RentalService`
- **Provider Services:** `PaymentService`, `AuthService`, `MediaService`, `InventoryService`

---

## 1. Executive Summary & Service Boundaries

This document defines the frozen application contracts between **RentalService** and collaborating platform services.
It establishes explicit capability boundaries, request/response models, event schemas, error codes, and validation rules to allow independent parallel development and mocking.

### Status Matrix

| Boundary | Contract Version | Consumer Status | Provider Status | Agreement Level |
|---|---|---|---|---|
| **Rental ↔ Payment** | `v1` | Implemented (Consumer Client & DTOs) | Unimplemented (External Repository) | **PROPOSED** (Routes/Scopes pending Provider Sign-off) |
| **Rental ↔ Auth/Profile**| `v1` | Implemented (`IRentalCallerContext` & Policy) | Implemented (`AuthService` JWT spec) | **PROPOSED / PENDING CONFORMANCE** (Subject & alias mapping implemented) |
| **Rental ↔ Media** | `v1` | Implemented (`IRentalMediaClient` & Validator) | Implemented (Internal Media Gateway) | **PROPOSED / PENDING CONFORMANCE** (`X-Api-Key` internal endpoint contract) |
| **Rental ↔ Inventory** | `v1` | Implemented (Consumer Stubs & Schemas) | Partial / In Progress | **PROPOSED** (Consumer contract drafted) |

---

## 2. Payment Service Contract (v1)

### 2.1. Architectural Responsibilities

- **RentalService Ownership:** Decides commercial amounts (rental charges, deposits, deduction calculations, refund amounts). Stores immutable checkout snapshots and external references (`InvoiceRef`, `PaymentRef`, `RefundRef`, `SettlementRef`).
- **PaymentService Ownership:** Owns invoice lifecycle, payment methods, bank collection/gateway callbacks, platform commission fee deductions, refund disbursement, and supplier payout scheduling.
- **Refund Routing Rule:** All refunds are automatically disbursed back to the **original payment source**. Rental requests do NOT capture or pass customer bank accounts.
- **Currency & Integer Rule:** All currency amounts for `VND` MUST be whole non-negative integers (`decimal` with scale 0 in JSON). Floating point types (`double`/`float`) are forbidden.
- **Zero-Total Invoices:** If an order has total payable amount 0 (promotional/sponsored), Payment MUST still issue an invoice, record zero collection, and publish `InvoicePaid`. Rental does not fabricate local payments.

### 2.2. Operations (`IPaymentRentalClient`)

#### 1. `CreateInvoice`
- **Proposed Route:** `POST /api/v1/invoices`
- **Required Scope:** `payment.invoice.write`
- **Idempotency:** Replay-safe by `OperationKey`. Same `OperationKey` + payload returns existing invoice with `IsReplay = true`. Conflicting payload returns `DUPLICATE_OPERATION_KEY`.
- **Request:**
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "orderCode": "ORD-20261008-001",
  "renterId": "9f7a77e2-cf77-4df6-aef9-3a3f5a2a2222",
  "currency": "VND",
  "paymentDeadline": "2026-10-08T08:30:00+07:00",
  "items": [
    {
      "rentalItemId": "a1111111-1111-1111-1111-111111111111",
      "supplierId": "s2222222-2222-2222-2222-222222222222",
      "rentalAmount": 500000,
      "depositAmount": 1000000
    }
  ],
  "operationKey": "op-create-inv-001"
}
```
- **Response:**
```json
{
  "invoiceRef": "INV-20261008-00001",
  "totalAmount": 1500000,
  "currency": "VND",
  "paymentDeadline": "2026-10-08T08:30:00+07:00",
  "isReplay": false
}
```

#### 2. `CancelInvoice`
- **Proposed Route:** `POST /api/v1/invoices/{invoiceRef}/cancel`
- **Semantics:** Cancels an unpaid invoice if the rental order timed out or was cancelled prior to payment. If the invoice has already been collected, returns `INVOICE_ALREADY_PAID` (does not refund).
- **Request:**
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "invoiceRef": "INV-20261008-00001",
  "reason": "Payment window expired (30m timeout)",
  "operationKey": "op-cancel-inv-001"
}
```
- **Response:**
```json
{
  "invoiceRef": "INV-20261008-00001",
  "cancelled": true,
  "cancelledAt": "2026-10-08T08:31:00+07:00",
  "isReplay": false
}
```

#### 3. `RequestRefund`
- **Proposed Route:** `POST /api/v1/refunds`
- **Required Scope:** `payment.refund.write`
- **Semantics:** Disburses refund back to original source for supplier rejections, supplier confirmation timeouts, or late-payment terminal orders.
- **Request:**
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "invoiceRef": "INV-20261008-00001",
  "paymentRef": "PAY-20261008-00001",
  "items": [
    {
      "rentalItemId": "a1111111-1111-1111-1111-111111111111",
      "refundRentalAmount": 500000,
      "refundDepositAmount": 1000000
    }
  ],
  "reason": "Supplier rejected item",
  "operationKey": "op-refund-001"
}
```
- **Response:**
```json
{
  "refundRef": "REF-20261008-00001",
  "operationKey": "op-refund-001",
  "status": "Accepted",
  "totalRefundAmount": 1500000,
  "currency": "VND",
  "isReplay": false
}
```

#### 4. `SettleRental`
- **Proposed Route:** `POST /api/v1/settlements`
- **Required Scope:** `payment.settlement.write`
- **Preconditions:** MUST ONLY be called after all physical units are returned and deduction proposal is finalized.
- **Rule:** Refunded deposit amount MUST strictly equal `DepositAmount - FinalDeductionAmount`.
- **Request:**
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "rentalItemId": "a1111111-1111-1111-1111-111111111111",
  "invoiceRef": "INV-20261008-00001",
  "paymentRef": "PAY-20261008-00001",
  "supplierId": "s2222222-2222-2222-2222-222222222222",
  "depositAmount": 1000000,
  "finalDeductionAmount": 250000,
  "refundedDepositAmount": 750000,
  "currency": "VND",
  "decisionRef": "DISP-RES-001",
  "operationKey": "op-settle-001"
}
```
- **Response:**
```json
{
  "settlementRef": "SETTLE-20261008-00001",
  "operationKey": "op-settle-001",
  "status": "Accepted",
  "finalDeductionAmount": 250000,
  "refundedDepositAmount": 750000,
  "currency": "VND",
  "isReplay": false
}
```

### 2.3. Asynchronous Domain Events

All events conform to the platform standard envelope:

```json
{
  "messageId": "11111111-1111-1111-1111-111111111111",
  "messageType": "InvoicePaid",
  "version": 1,
  "producer": "payment-service",
  "aggregateId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "occurredAt": "2026-10-08T08:15:30Z",
  "correlationId": "corr-001",
  "causationId": null,
  "payload": { ... }
}
```

#### Event: `InvoicePaid`
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "renterId": "9f7a77e2-cf77-4df6-aef9-3a3f5a2a2222",
  "invoiceRef": "INV-20261008-00001",
  "paymentRef": "PAY-20261008-00001",
  "paidAmount": 1500000,
  "currency": "VND",
  "paidAt": "2026-10-08T08:15:30Z"
}
```
*Rental Consumer Logic:* Validates `PaidAmount == TotalPayableAmount`, `Currency == order.Currency`, and matching `InvoiceRef`. If the order is already in a terminal state (`Expired`/`Cancelled`), flags `LatePaymentRefundRequired = true` and emits refund request.

#### Event: `RefundCompleted`
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "invoiceRef": "INV-20261008-00001",
  "paymentRef": "PAY-20261008-00001",
  "refundRef": "REF-20261008-00001",
  "operationKey": "op-refund-001",
  "totalRefundedAmount": 1500000,
  "currency": "VND",
  "items": [
    {
      "rentalItemId": "a1111111-1111-1111-1111-111111111111",
      "refundedRentalAmount": 500000,
      "refundedDepositAmount": 1000000
    }
  ],
  "completedAt": "2026-10-08T08:20:00Z"
}
```
*Rental Consumer Logic:* Records permanent audit record that money returned to renter. Does NOT complete item or replace settlement.

#### Event: `RentalSettled`
```json
{
  "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
  "rentalItemId": "a1111111-1111-1111-1111-111111111111",
  "invoiceRef": "INV-20261008-00001",
  "paymentRef": "PAY-20261008-00001",
  "settlementRef": "SETTLE-20261008-00001",
  "operationKey": "op-settle-001",
  "finalDeductionAmount": 250000,
  "refundedDepositAmount": 750000,
  "currency": "VND",
  "refundRef": "REF-20261008-00002",
  "completedAt": "2026-10-08T12:00:00Z"
}
```
*Rental Consumer Logic:* Confirms financial closure (`RecordPaymentSettlementReceipt`). Evaluates aggregate completion if Inventory closure receipt is also verified. Emitted even when `RefundedDepositAmount == 0`.

---

## 3. Auth & Profile Service Contract (v1)

### 3.1. Identity Model & Claims Mapping

RentalService relies strictly on verified JWT claims delivered in the `Authorization: Bearer <token>` header:

| Claim | JSON Type | Description | Rental Invariant |
|---|---|---|---|
| `sub` | string (UUID) | Authenticated User ID or Service Identifier | Valid UUID identifies interactive user or service client. |
| `pc` | boolean | Profile Completed (`true` / `false`) | Legacy claim; Checkout now checks profile completion directly via AuthService `GET /api/v1/auth/users/me`. |
| `r` | string array or comma list | Roles assigned to user | Normalized canonical roles and aliases (`adm` ↔ `admin`, `adp` ↔ `admin_platform`, `cus` ↔ `customer`, `own` ↔ `owner`, `mem` ↔ `member`). |
| `p` | string array or comma list | Granular permissions | Permission catalog matching AuthService. |
| `u` | string | User alias or Service indicator | Value `svc` indicates background service token (with `UserId = null`). Audit identity is strictly tracked by `UserId`. |

### 3.2. Authorization Boundaries

1. **Checkout Eligibility:**
   - Caller must be authenticated (`IsAuthenticated == true`).
   - Caller must NOT be a service token (`IsService == false`).
   - Profile completion is verified directly via AuthService `GET /api/v1/auth/users/me` using the request's Bearer token and internal API key. JWT claim `pc` is not evaluated for checkout.
2. **Resource Ownership Rules:**
   - **Renter:** `caller.UserId == RentalOrder.RenterId`.
   - **Supplier:** `caller.UserId == RentalItem.SupplierId`. Supplier ID at checkout is immutable from Inventory listing snapshot.
   - **Admin:** `caller.HasRole("admin") || caller.HasRole("admin_platform")`.
   - **Service Token Isolation:** Service tokens are strictly prohibited from performing user lifecycle actions (SubmitHandover, SubmitReturn, DeductionProposal, Dispute).
   - **No Ownership Endpoint:** AuthService owns authentication; RentalService owns resource authorization. Rental does NOT replicate user database tables.

---

## 4. Media Service Contract (v1)

### 4.1. Integration Mechanism

- **Authentication:** `X-Api-Key` internal header (matching active gateway and MediaService deployment).
- **Persistence Boundary:** RentalService strictly persists `MediaId` GUID arrays. Rental NEVER stores binary streams, S3 keys, or pre-signed temporary URLs.

### 4.2. Operations (`IRentalMediaClient`)

1. `VerifyMedia(mediaId)`: `GET /api/v1/media/{mediaId}/verify`
   - Confirms media exists, status is `Ready`, and returns `UploadedByUserId`. Does not return visibility.
2. `GetMetadata(mediaId)`: `GET /api/v1/media/{mediaId}`
   - Returns visibility (`Private` vs `Public`) and status.
3. `CreatePrivateReadAccess`: `POST /api/v1/media/{mediaId}/access`
   - Wire request: `{ "action": "read", "download": false }`.
   - Wire response: `{ "mediaId": "...", "url": "...", "expiresAtUtc": "..." }`.
   - Called only after Rental checks actor authorization (renter, supplier, or admin).
### 4.3. Frozen Evidence Attachment Rules

- **Private Visibility Mandatory:** All handover, return, damage, and dispute media MUST have visibility `Private`. Public media is strictly rejected with `MEDIA_MUST_BE_PRIVATE` (visibility will not be auto-converted).
- **Uploader Ownership:** The `UploaderUserId` on the media item MUST strictly match the actor submitting the operation (`MEDIA_UPLOADER_MISMATCH`).
- **Batch Limits:** Each evidence submission accepts between 1 and 10 media IDs (`MediaContractSpec.MaxEvidenceMediaCount`).
- **Sanity Checks:** Duplicate media IDs and `Guid.Empty` are rejected.
- **Atomic Verification:** If any media item fails verification, the entire operation is rejected. Evidence is never partially accepted.

---

## 5. Error Code Catalog

| Capability | Error Code | HTTP Status | Meaning |
|---|---|---|---|
| **Payment** | `INVOICE_ALREADY_PAID` | 409 Conflict | Invoice already paid; cancel rejected. |
| **Payment** | `INVOICE_NOT_FOUND` | 404 Not Found | Specified invoice reference does not exist. |
| **Payment** | `TOTAL_AMOUNT_MISMATCH` | 400 Bad Request | Item amounts do not sum to total invoice. |
| **Payment** | `DUPLICATE_OPERATION_KEY` | 409 Conflict | Operation key replayed with different parameters. |
| **Payment** | `REFUND_EXCEEDS_PAID_AMOUNT` | 409 Conflict | Requested refund exceeds captured payment. |
| **Payment** | `SETTLEMENT_EXCEEDS_DEPOSIT` | 409 Conflict | Final deduction exceeds held deposit. |
| **Auth** | `Auth.Unauthorized` | 401 Unauthorized | Caller is not authenticated. |
| **Auth** | `Checkout.ProfileIncomplete` | 403 Forbidden | Renter has not completed identity profile (verified via AuthService GET /users/me). |
| **Auth** | `Auth.Forbidden` | 403 Forbidden | Caller lacks ownership of the order/item. |
| **Auth** | `Auth.ServiceTokenNotAllowed` | 403 Forbidden | Service token attempted interactive user operation. |
| **Media** | `MEDIA_NOT_FOUND` | 404 Not Found | Media ID does not exist. |
| **Media** | `MEDIA_NOT_READY` | 409 Conflict | Media processing not complete (status != 'Ready'). |
| **Media** | `MEDIA_MUST_BE_PRIVATE` | 400 Bad Request | Media is Public; evidence requires Private visibility. |
| **Media** | `MEDIA_UPLOADER_MISMATCH` | 403 Forbidden | Media uploader does not match operation actor. |
| **Media** | `MEDIA_LIMIT_EXCEEDED` | 400 Bad Request | More than 10 media items attached in single batch. |
| **Media** | `DUPLICATE_MEDIA_ID` | 409 Conflict | Duplicate media GUID in request list. |
