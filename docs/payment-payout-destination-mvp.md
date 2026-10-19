# Supplier payout destination (MVP)

Subtask 7 is implemented on the Payment side. Payment owns the destination records; Rental supplies only `SupplierId` and settlement amounts. SePay confirms inbound payments and does not send supplier payouts.

## Admin flow

1. Verify the supplier's bank account and ownership outside Payment. Keep the full bank details in the controlled verification or payout profile referenced by `VerificationRef`. The registration request carries the account number over the admin API, but Payment persists only its last four digits and an HMAC fingerprint. Neither payout lists nor destination responses contain the full number.
2. Configure `PayoutDestination__FingerprintKey` as a stable secret of at least 32 UTF-8 bytes in the Payment process. Keep the same key across deployments so duplicate-account checks remain meaningful. Do not commit this key.
3. `POST /api/v1/admin/suppliers/{supplierId}/payout-destinations` with `OperationKey`, `BankCode`, `AccountHolderName`, `AccountNumber`, `VerificationRef`, `AuditReason`, and optional `DestinationRef` for a new version. The response contains `DestinationRef` and `Version`; identical retries return `IsReplay=true`. The admin must actually verify the account before calling this endpoint; Payment does not verify with a bank or SePay.
4. `GET /api/v1/admin/suppliers/{supplierId}/payout-destinations` lists masked, verified versions. Only platform admins can use these APIs.
5. `POST /api/v1/admin/payouts/{payoutId}/destination` with `DestinationRef`, **`Version`**, `OperationKey`, and `AuditReason`. Payment checks that the exact verified version belongs to the payout's supplier and saves its immutable `DestinationId`. A different version can be attached while the payout is pending; an audit row records the operation.
6. Transfer money outside Payment using the controlled verification or payout profile, then `POST /api/v1/admin/payouts/{payoutId}/complete` with `OperationKey`, `ExternalTransferReference`, and `AuditReason`. Payment rejects completion without a saved verified `DestinationId`. This endpoint records the transfer reference and audit; it does not transfer money itself.

Existing pending payouts with only a legacy free-text `DestinationRef` cannot be completed until an admin attaches a verified version. Existing completed payouts remain historical records; migration does not fabricate verification evidence for them. New destination versions are append-only at the database level. Registering a new version does not change `DestinationId` already saved on a payout.

Automated provider payout and refund remain post-MVP TODOs. This implementation does not establish an automated source of bank-account ownership or an automated check that the external transfer was actually made; the admin's verification record and transfer evidence remain operational controls.
