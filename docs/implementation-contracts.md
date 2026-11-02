# NotificationService — Implementation Contracts & Task Specifications (N02–N08)

## 1. Overview & Roadmap Architecture

This document establishes the official technical specifications, file ownership, testing matrices, and prerequisite requirements for implementation phases **N02 through N08** of `NotificationService`.

```text
┌────────────────────────────────────────────────────────────────────────┐
│  Phase 1 Roadmap:                                                      │
│  [N01] Verify event paths, architecture, contracts & AGENTS.md (DONE)  │
│    │                                                                   │
│    ▼                                                                   │
│  [N02] PostgreSQL Schema, Entities & Persistence Layer                 │
│    │                                                                   │
│    ├─────────────────────────────┬──────────────────────────┐          │
│    ▼                             ▼                          ▼          │
│  [N03] Kafka Ingestion Pipeline [N04] REST Inbox API  [N05] SignalR Hub│
│    │                             │                          │          │
│    └─────────────────────────────┼──────────────────────────┘          │
│                                  ▼                                     │
│  [N06] Email Delivery Engine & Lease Crash Recovery                     │
│    │                                                                   │
│    ▼                                                                   │
│  [N07] Event Handlers & Stale Watermark Suppression (10 Events)        │
│    │                                                                   │
│    ▼                                                                   │
│  [N08] End-to-End Integration Verification & Operational Readiness     │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Cross-Service Workspace Dependencies & Gap Report

### 2.1. Inspected Source Code Revisions
- **DPRENTAL Root**: `109de834ca3532e542af7d5eb30d9f81a7ec68eb`
- **RentalService**: `159702d3a94e824e4da6da8af2bfc9867006119e`
- **AuthService**: `4ef9bb466a2e031dcd49e0ea69e741319591478a`
- **PaymentService**: `7b3aa68430dfd7a056274f6d6111f62f048a5690`
- **Gateway**: `d23cd533ede2d4b4142ff07bf3f7c34356be40a0`

### 2.2. Evidence Gap Report & Producer Implementation Status

| Service / Dependency | Required Feature / Path | Current Status | Impact on NotificationService | Work Required / Workspace |
|---|---|---|---|---|
| **RentalService** | 10 target events published to `rental.events.v1` with monotonic `orderVersion` | **PROVED & VERIFIED** | None. All 10 payloads, outbox writers, and use cases verified in `RentalService`. | None. Upstream contracts intact. |
| **AuthService** | `POST /api/v1/auth/internal/users/notification-contacts:batch` | **EVIDENCE GAP** (Not yet implemented in AuthService) | Blocks N06 live email contact resolution. N06 can mock/stub the HTTP client during unit tests, but live deployment requires AuthService update. | Add internal contact batch endpoint in `AuthService` workspace. |
| **AuthService** | JWT with distinct `u: "svc"` vs human `sub` | **PROVED & VERIFIED** | Implemented via `JwtClaimCatalog.Claims.TokenUse = "u"` and `TokenUses.Service = "svc"`. | None. |
| **Gateway** | Route `/api/v1/notifications/**` and hub `/api/v1/notifications/hubs/inbox` | **EVIDENCE GAP** (Route not yet mapped in Ocelot config) | Blocks public client access through Gateway. Direct access via Docker network works. | Add Ocelot upstream/downstream route in `Gateway` workspace. |
| **Gateway** | Block `/internal/` endpoints | **PROVED & VERIFIED** | `ServiceOnlyRouteBlockingMiddleware` blocks `/internal/` with 403 Forbidden. | None. |
| **PaymentService** | Direct DB or event interaction | **NOT APPLICABLE** | NotificationService has ZERO direct coupling with PaymentService DB or Kafka events. All payment status updates arrive projected through RentalService (`RentalOrderPaid`, `LatePaymentRefundRequested`). | None. |
| **Platform / Infra** | PostgreSQL `notification` schema & Kafka DLQ topic | **CONFIGURED VIA MIGRATIONS** | EF Core migrations in N02 create schema; topic created via compose/Kafka setup. | None. |

### 2.3. Readiness Verdict
- **Architecture & Core Contracts**: **FROZEN FOR IMPLEMENTATION** within NotificationService.
- **External Integration Gaps**:
  - `AuthService` internal contact batch endpoint is **PENDING IMPLEMENTATION** in `AuthService` workspace (Prerequisite for live E2E in N06/N08).
  - `Gateway` Ocelot routes are **PENDING REGISTRATION** in `Gateway` workspace (Prerequisite for frontend routing in N04/N05).
  - NotificationService implementation will proceed with strict mockable interfaces (`IAuthContactClient`), allowing 100% of internal service development and unit/integration testing without waiting for external deployments.

---

## 3. Detailed Specifications for Tasks N02–N08

### 3.1. Task N02: Database Schema, Entities & Persistence Layer

- **Objective**: Create the PostgreSQL database persistence layer for schema `notification`, including EF Core entities, DbContext, Fluent API mappings, indexes, migrations, and repository abstractions.
- **File Ownership**:
  - `NotificationService.Domain/Entities/Notification.cs`
  - `NotificationService.Domain/Entities/NotificationInboxReceipt.cs`
  - `NotificationService.Domain/Entities/NotificationDeliveryJob.cs`
  - `NotificationService.Domain/Entities/NotificationPreference.cs`
  - `NotificationService.Domain/Entities/NotificationAggregateWatermark.cs`
  - `NotificationService.Domain/IRepositories/INotificationRepository.cs`
  - `NotificationService.Domain/IRepositories/IInboxReceiptRepository.cs`
  - `NotificationService.Domain/IRepositories/IDeliveryJobRepository.cs`
  - `NotificationService.Domain/IRepositories/IPreferenceRepository.cs`
  - `NotificationService.Domain/IRepositories/IAggregateWatermarkRepository.cs`
  - `NotificationService.Domain/IRepositories/IUnitOfWork.cs`
  - `NotificationService.Domain/Persistence/NotificationDbContext.cs`
  - `NotificationService.Domain/Persistence/Configurations/*.cs`
  - `NotificationService.Domain/Repositories/*.cs`
  - `NotificationService.Domain/Migrations/*.cs`
- **Acceptance Criteria**:
  - PostgreSQL schema `notification` created and isolated from other service schemas.
  - Unique constraints verified:
    - `UNIQUE (consumer_name, message_id)` on `inbox_receipts`.
    - `UNIQUE (source_event_id, recipient_user_id, notification_type)` on `notifications`.
    - `UNIQUE (notification_id, channel)` on `delivery_jobs`.
    - `UNIQUE (user_id, category)` on `preferences`.
    - Primary key `(aggregate_id, recipient_user_id)` on `aggregate_watermarks`.
  - Indexes created for unread queries and delivery job polling.
- **Test Specifications**:
  - Persistence Integration Tests (`NotificationService.Tests.Integration/Persistence/`):
    - Verify EF Core migration runs cleanly against PostgreSQL testcontainer.
    - Test unique constraint enforcement and duplicate rejection.
    - Verify `.AsNoTracking()` queries and deterministic pagination sorting.

---

### 3.2. Task N03: Kafka Ingestion Pipeline, Envelope Validation & Quarantine

- **Objective**: Implement Kafka consumer loop on `rental.events.v1`, envelope deserialization, allowlist filtering, transactional inbox deduplication, and DLQ quarantine.
- **File Ownership**:
  - `NotificationService.Application/Kafka/Consumers/KafkaConsumerHostedService.cs`
  - `NotificationService.Application/Kafka/Contracts/KafkaMessageEnvelope.cs`
  - `NotificationService.Application/Kafka/Validation/KafkaEnvelopeValidator.cs`
  - `NotificationService.Application/Kafka/Quarantine/IKafkaDeadLetterPublisher.cs`
  - `NotificationService.Application/Kafka/Quarantine/KafkaDeadLetterPublisher.cs`
  - `NotificationService.Application/Kafka/Handlers/IKafkaEventHandler.cs`
  - `NotificationService.Application/Kafka/Dispatch/KafkaEventDispatcher.cs`
- **Acceptance Criteria**:
  - Consumer joins group `notification-service-v1` with manual offset commit (`EnableAutoCommit = false`).
  - Envelope validated against schema (version 1, non-empty UUID, valid producer).
  - Malformed or invalid envelopes written to `rental.events.v1.notification.dlq` and recorded in `inbox_receipts` as `Quarantined`.
  - Unknown event types from allowed producer on topic logged as informational and skipped with offset committed (no quarantine).
  - Replayed messages with existing `(consumer_name, message_id)` skipped idempotently.
- **Test Specifications**:
  - Unit Tests: Validate envelope schema against fixtures (`invalid-identity`, `unsupported-version`, `unknown-allowed-topic-type`).
  - Integration Tests: Test duplicate delivery handling and manual offset commit after DB transaction commit.

---

### 3.3. Task N04: In-App Inbox REST API & Preferences

- **Objective**: Implement REST endpoints for notifications query, unread count, single read, read-all with cutoff, and category preferences.
- **File Ownership**:
  - `NotificationService.Api/Controllers/NotificationsController.cs`
  - `NotificationService.Api/Controllers/PreferencesController.cs`
  - `NotificationService.Application/IServices/INotificationQueryService.cs`
  - `NotificationService.Application/IServices/INotificationReadService.cs`
  - `NotificationService.Application/IServices/IPreferenceService.cs`
  - `NotificationService.Application/Services/NotificationQueryService.cs`
  - `NotificationService.Application/Services/NotificationReadService.cs`
  - `NotificationService.Application/Services/PreferenceService.cs`
  - `NotificationService.Application/DTOs/*.cs`
  - `NotificationService.Application/Validation/QueryValidators.cs`
- **Acceptance Criteria**:
  - `GET /api/v1/notifications`: Paginated result with `created_at DESC, id DESC`.
  - `GET /api/v1/notifications/unread-count`: Returns exact unread count.
  - `PATCH /api/v1/notifications/{id}/read`: Idempotent single mark-read; returns 404 for non-owned or non-existent notification.
  - `PATCH /api/v1/notifications/read-all`: Server-side cutoff applied; does not mark newer notifications read.
  - `GET /api/v1/notifications/preferences`: Safe defaults returned when no record exists.
  - `PUT /api/v1/notifications/preferences`: Validates categories and updates preferences.
  - Service JWT (`u: "svc"`) rejected with 403 Forbidden.
- **Test Specifications**:
  - Unit Tests: Validate query validators, cutoff logic, and default preference resolution.
  - Controller Tests: Verify Bearer JWT extraction, IDOR ownership isolation, and ProblemDetails mapping.

---

### 3.4. Task N05: SignalR Realtime Hub for Inbox Invalidation

- **Objective**: Implement the SignalR inbox hub `/api/v1/notifications/hubs/inbox` for realtime cache invalidation signals.
- **File Ownership**:
  - `NotificationService.Api/Hubs/NotificationHub.cs`
  - `NotificationService.Application/IServices/INotificationRealtimeNotifier.cs`
  - `NotificationService.Api/Services/SignalRNotificationRealtimeNotifier.cs`
  - `NotificationService.Api/Security/SignalRAuthenticationExtensions.cs`
- **Acceptance Criteria**:
  - Hub endpoint `/api/v1/notifications/hubs/inbox` supports WebSocket and Long Polling.
  - Bearer token accepted via header or HTTP-only cookie `accessToken`.
  - Access tokens passed via query string `?access_token=` are strictly rejected.
  - Upon connection, user connection joins private group `user:{sub}`. Client-side arbitrary group join is blocked.
  - Service tokens (`u: "svc"`) rejected with 403 Forbidden.
  - `NotificationInboxChanged` dispatched post-commit to group `user:{recipientUserId}`.
- **Test Specifications**:
  - Integration Tests: Connect using test WebSocket client with valid/invalid tokens; verify message receipt and group isolation.

---

### 3.5. Task N06: Email Delivery Engine & Lease Crash Recovery

- **Objective**: Implement the leased email worker using `FOR UPDATE SKIP LOCKED`, transactional SMTP sender, crash lease recovery, and `Uncertain` status handling.
- **File Ownership**:
  - `NotificationService.Application/Email/IEmailSender.cs`
  - `NotificationService.Application/Email/SmtpEmailSender.cs`
  - `NotificationService.Application/Email/Clients/IAuthContactClient.cs`
  - `NotificationService.Application/Email/Clients/AuthContactClient.cs`
  - `NotificationService.Application/Email/Workers/EmailDeliveryWorker.cs`
  - `NotificationService.Application/Email/Workers/EmailLeaseReconciliationWorker.cs`
  - `NotificationService.Application/Email/Options/SmtpOptions.cs`
- **Acceptance Criteria**:
  - Worker claims jobs in batches using PostgreSQL `FOR UPDATE SKIP LOCKED` with 5-minute lease.
  - DB transaction committed before opening SMTP connection.
  - Contact resolution calls `IAuthContactClient`; job suppressed if recipient inactive or email unverified.
  - Delivery outcomes accurately classified: `Succeeded`, `Failed`, `RetryScheduled`, `Uncertain`, `Suppressed`.
  - When SMTP drops after `DATA` phase, job marked `Uncertain` with **BLIND RETRY STRICTLY FORBIDDEN**.
  - `EmailLeaseReconciliationWorker` safely recovers expired leases from crashed workers.
- **Test Specifications**:
  - Unit Tests: Mock SMTP sender to simulate timeout after DATA command; assert job transitions to `Uncertain` and retry count is not incremented.
  - Integration Tests: Verify concurrent workers claiming jobs via `FOR UPDATE SKIP LOCKED` without double-claiming.

---

### 3.6. Task N07: Event Handlers & Stale Watermark Suppression (10 Events)

- **Objective**: Implement concrete handlers for all 10 target events, recipient resolution, vi-VN template rendering, watermark tracking, and atomic DB transaction commit.
- **File Ownership**:
  - `NotificationService.Application/Kafka/Handlers/RentalOrderCreatedHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalOrderPaidHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalSupplierConfirmationRequestedHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalOrderExpiredHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalOrderCancelledHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalItemHandoverCompletedHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalHandoverOverdueHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/ReturnConfirmationOverdueHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/RentalOrderCompletedHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/LatePaymentRefundRequestedHandler.cs`
  - `NotificationService.Application/Policies/StaleActionSuppressionPolicy.cs`
  - `NotificationService.Application/Policies/NotificationChannelPolicy.cs`
  - `NotificationService.Application/Templates/NotificationTemplateRenderer.cs`
- **Acceptance Criteria**:
  - Recipient resolution extracts `RenterId` or `SupplierId` directly from verified payload. Empty GUID rejected to quarantine.
  - For multi-recipient events (`RentalHandoverOverdue`, `ReturnConfirmationOverdue`), generates separate notifications for Renter and Supplier.
  - Watermark tracking updates `last_order_version` and terminal states (`PAID`, `EXPIRED`, `CANCELLED`, `COMPLETED`).
  - Stale action suppression: if `orderVersion < watermark` or order is in terminal state, suppresses payment/confirmation CTA buttons.
  - Same-version sibling events sharing `orderVersion` processed independently if `messageId` differs.
  - `LatePaymentRefundRequested`: Wording strictly asserts refund *requested/processing*; NEVER asserts refund succeeded.
  - Single atomic PostgreSQL transaction commits inbox receipt, notification, delivery job, and watermark together.
- **Test Specifications**:
  - Handler Unit Tests: Exercise all 10 handlers against valid fixtures and assert expected DB records.
  - Watermark Suppression Tests: Test `RentalOrderCreated` arriving after `RentalOrderPaid` (assert CTA suppressed and informational template rendered).
  - Financial Wording Test: Verify `LatePaymentRefundRequested` template contains no false success claims.

---

### 3.7. Task N08: End-to-End Integration Verification & Operational Readiness

- **Objective**: Execute comprehensive end-to-end integration tests, verify OpenTelemetry tracing, health check probes, load tolerance, and finalize deployment documentation.
- **File Ownership**:
  - `NotificationService.Tests.Integration/E2E/*.cs`
  - `NotificationService.Api/Observability/*.cs`
  - `docs/RUNBOOK.md`
  - `docker-compose.yml`
- **Acceptance Criteria**:
  - End-to-end flow verified: publish Kafka event -> consume -> atomic DB persist -> realtime SignalR invalidation -> email worker delivery.
  - Health checks `/health/live` and `/health/ready` verified.
  - OpenTelemetry traces linked via Kafka `traceparent` headers to upstream RentalService checkout.
  - Clean runbook for production deployment, SMTP failure escalation, and DLQ reprocessing.
- **Test Specifications**:
  - Full E2E Test Suite with Testcontainers (PostgreSQL, Kafka, Mailhog).
  - Negative tests: poison pill quarantine, broker reconnect, SMTP timeout recovery.
