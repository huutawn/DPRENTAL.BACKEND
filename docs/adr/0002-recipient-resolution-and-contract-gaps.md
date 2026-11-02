# ADR-0002: Recipient Identity Resolution and Event Contract Gap Handling

## Status
Accepted

## Context
NotificationService receives events from upstream microservices (primarily `RentalService`). To create notifications and deliver emails, NotificationService must know the exact recipient user identifier (`RecipientUserId`).
- In-app notifications are partitioned by `recipient_user_id` so that users only see their own notifications.
- Email delivery requires resolving contact information (email address) from `AuthService` using `recipient_user_id`.

Risks:
1. Inferring recipient identities from untrusted fields (e.g. frontend query parameters, unverified claims).
2. Inferring recipients when an event payload omits `RenterId` or `SupplierId`.
3. Reading raw email addresses directly from Kafka payloads (which may be stale, unverified, or leak PII into message broker streams).
4. Direct querying of other services' databases (e.g. querying `rental.orders` or `auth.users`), which violates microservice boundaries and DPRental project rules.

## Decision
1. **Payload as Single Identity Source for Business Routing**:
   - The recipient identifier MUST be obtained directly from explicit identity fields in the verified event payload:
     - `RenterId`: Identifies the renting customer.
     - `SupplierId`: Identifies the equipment owner / supplier organization or user.
   - For multi-recipient events (such as `RentalHandoverOverdue` and `ReturnConfirmationOverdue`), separate notification records and delivery jobs are generated for each eligible recipient role (`RenterId` and `SupplierId`).
   - Duplicate recipient IDs within the same event intent are deduplicated before insertion.

2. **No Fallback to Cross-Service Database Access**:
   - NotificationService NEVER queries `rental.*`, `auth.*`, or `payment.*` tables.

3. **Handling of Inspected Event Paths & Gaps**:
   Code inspection of `RentalService` (at git commit `159702d3a94e824e4da6da8af2bfc9867006119e`) confirms the payload structure for all 10 Phase 1 MVP target events:
   - `RentalOrderCreated`: Contains `RenterId` (Guid) and `Items[].SupplierId` (Guid). Target recipient is `RenterId`.
   - `RentalOrderPaid`: Contains `RenterId` (Guid). Target recipient is `RenterId`.
   - `RentalSupplierConfirmationRequested`: Contains `SupplierId` (Guid). Target recipient is `SupplierId`.
   - `RentalOrderExpired`: Contains `RenterId` (Guid). Target recipient is `RenterId`.
   - `RentalOrderCancelled`: Contains `RenterId` (Guid). Target recipient is `RenterId`.
   - `RentalItemHandoverCompleted`: Contains `RenterId` (Guid) and `SupplierId` (Guid). Target recipient is `RenterId`.
   - `RentalHandoverOverdue`: Contains `SupplierId` (Guid) and `RenterId` (Guid). Target recipients are `SupplierId` (In-app + Email) and `RenterId` (In-app only).
   - `ReturnConfirmationOverdue`: Contains `SupplierId` (Guid) and `RenterId` (Guid). Target recipients are `SupplierId` (In-app + Email) and `RenterId` (In-app only).
   - `RentalOrderCompleted`: Contains `RenterId` (Guid). Target recipient is `RenterId`.
   - `LatePaymentRefundRequested`: Contains `RenterId` (Guid). Target recipient is `RenterId`.

4. **Zero-Inference Policy for Incomplete Payloads**:
   If any future or variant event arrives with an empty GUID (`00000000-0000-0000-0000-000000000000`), a missing recipient field, or an unparseable identifier:
   - The event MUST NOT be routed by guessing.
   - The event MUST NOT be delivered to admin users as a fallback.
   - The event is quarantined with outcome `MissingRecipientIdentity` and logged with structured alerts.

5. **Contact Resolution via AuthService API**:
   - Recipient email addresses are NEVER accepted from Kafka event payloads.
   - Email delivery workers resolve fresh contact details from `AuthService` via authenticated internal batch endpoint `POST /api/v1/auth/internal/users/notification-contacts:batch`.

## Consequences
### Positive
- Strict security against IDOR and cross-tenant information leakage.
- Clear auditability: every notification directly traces to an authoritative ID in the source event.
- Zero runtime dependency on Rental database.

### Trade-offs / Risks
- If upstream producer emits an event with an empty recipient GUID, the notification is rejected and flagged as a gap rather than silently patched.
