# Backfill paid invoices into Analytics

This operator-only console repairs missing `InvoicePaidFinancial` events for historical `PAID` invoices. It does not run Payment API or its background workers, replay bank webhooks, modify invoices, or write to Analytics tables. No public endpoint or automatic startup backfill is added.

The tool reuses `PaymentOutboxFactory`, `JournalEntryFactory`, invoice locking and persistence, `IntegrityHostClient`, and `VerifiedWriteContext`. The existing invoice repository adds bounded candidate selection and stable event checks; no parallel invoice repository or financial rules are introduced. Historical repair was previously unsupported, so there is no old implementation to remove.

## Configuration and execution

Run from the repository root. Supply the same Payment connection, encryption and Integrity verification configuration as the target Payment environment using its secret manager or environment, never CLI arguments:

- `ConnectionStrings__Postgres` (including Payment's configured search path).
- `PaymentEncryption__ActiveKeyId` and `PaymentEncryption__DataEncryptionKeyBase64` or `PaymentEncryption__KeyFilePath`; include old encryption keys if applicable.
- `IntegrityHost__BaseUrl`, `IntegrityHost__InternalApiKey`.
- `IntegrityVerification__Issuer`, `IntegrityVerification__Audience`, `IntegrityVerification__Environment`, and the verification public keys.

Back up Payment data before applying. Choose an explicit UTC cutoff; only invoices with `PaidAt` strictly before that cutoff are selected. For the dev incident, the original deployment boundary was `2026-10-28T09:43:00Z`. Do not use this timestamp for unrelated environments.

```sh
dotnet build PaymentService/tools/AnalyticsBackfill/AnalyticsBackfill.csproj -c Release
# Read-only preview; apply defaults to false.
dotnet run --project PaymentService/tools/AnalyticsBackfill/AnalyticsBackfill.csproj -c Release --no-build -- --before 2026-10-28T09:43:00Z --limit 100
# Explicit write mode, only after reviewing the preview.
dotnet run --project PaymentService/tools/AnalyticsBackfill/AnalyticsBackfill.csproj -c Release --no-build -- --before 2026-10-28T09:43:00Z --limit 100 --apply true
```

Limit defaults to 100 and must be 1–100. Each invocation has a two-minute timeout. Repeat with the same cutoff until preview reports zero candidates. A zero process exit code means the batch was inspected/enqueued successfully, not that Kafka delivery or Analytics ingestion has completed.

## Guarantees and recovery

- Preview makes no writes and does not call IntegrityHost, but requires the Payment encryption configuration to construct the shared services.
- Application verifies the original **payment reference**, invoice amount, currency and order against signed IntegrityHost evidence before opening a transaction. Under the invoice stream and row locks it rechecks eligibility, stable event identity and the verified invoice claim.
- Each invoice commits one outbox message and a new linked journal atomically. The journal has a fresh operation ID and extends the existing hash chain; historical journals and invoices remain unchanged. Payload `paidAt` retains the original bank payment time; `occurredAt` records repair time.
- Repeated/concurrent runs skip existing deterministic event IDs. Existing financial outbox messages are excluded even if still awaiting confirmation/delivery; diagnose their existing publisher state instead of creating duplicates.
- Missing payment references, unconfirmed journals, invalid or expired evidence stop the batch with a nonzero exit code. Earlier invoices may already be committed; rerun after fixing the cause. Never delete historical journals or edit existing event IDs to retry.
- IntegrityHost must confirm the new journal before the existing publisher can obtain an `analytics-service` publication proof and publish to `payment.analytics.v1`. This tool never imports signing private keys.

After an authorized apply, inspect Payment outbox confirmation/publication, Kafka topic/consumer lag and Analytics dead letters; compare the Analytics API count and rental-only GMV to source invoices using the same Vietnam date range. Supplier/item charts also depend on Rental snapshots. Refreshing the dashboard alone does not repair missing financial events.
