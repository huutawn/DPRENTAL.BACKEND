# NotificationService — Implementation Contracts & Task Specifications (N02–N08)

## 1. Overview & Roadmap Architecture

This document establishes the official technical specifications, file ownership, testing matrices, and prerequisite requirements for implementation phases **N02 through N08** of `NotificationService`.

```text
┌────────────────────────────────────────────────────────────────────────┐
│  Phase 1 Roadmap (Execution Order):                                    │
│  [N01] Contract & Verification Review (DONE)                           │
│    │                                                                   │
│    ▼                                                                   │
│  [N02] Foundation & Persistence Layer                                  │
│    │                                                                   │
│    ▼                                                                   │
│  [N04] Policy, Recipient & Template Engine                             │
│    │                                                                   │
│    ├─────────────────────────────┬──────────────────────────┐          │
│    ▼                             ▼                          │          │
│  [N03] Kafka Ingestion Pipeline [N05] Inbox REST API        │          │
│    │                             │                          │          │
│    └─────────────────────────────┼──────────────────────────┘          │
│                                  ▼                                     │
│  [N06] Email Delivery Engine  [N07] Security & Realtime Hub            │
│    │                             │                                     │
│    └─────────────────────────────┼─────────────────────────────────────┘
│                                  ▼
│  [N08] Full Testing Suite, CI & Operational Readiness                  │
└────────────────────────────────────────────────────────────────────────┘
```
*Execution Order*: N01 → N02 → N04 → N03/N05 → N06/N07 → N08.

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

## 3. Detailed Specifications for Tasks N01–N08

### 3.1. Task N01: Contract & Verification Review (Đối chiếu và sửa contract trước implementation)
- **Objective**: Verify and align all 10 target events, payloads, schemas, fixtures, and contracts. Eliminate double numbering in roadmap.
- **Completed Items**:
  - 10 events verified against upstream `RentalService` source code.
  - JSON schemas and fixtures created for valid, invalid identity, missing recipient, unsupported version, duplicate, same-version sibling, and out-of-order stale events.
  - Contracts updated: Read-all requires server-side authoritative cutoff (`cutoffUtc` not accepted from client); Pagination uses deterministic `CreatedAt DESC, Id DESC` (not a cross-request transactional snapshot); Realtime SignalR strictly acts as cache invalidation requiring client refetch (no client badge direct increment).
  - Stale action suppression invariant formalized via `notification.aggregate_watermarks`.

### 3.2. Task N02: Foundation & Persistence (Cơ sở hạ tầng dữ liệu và lưu trữ)
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
  - `NotificationService.Domain/Repositories/NotificationServiceDbContext.cs`
  - `NotificationService.Domain/Repositories/Configurations/*.cs`
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
  - Stores template identity/version, minimal standardized render data, event version, and target resource; does NOT store raw Kafka payloads.
  - Indexes created for unread queries and delivery job polling.
  - Application owns validation & business rules; Entity holds state & mapping; Repository owns persistence, queries, locking, transactions.
  - Ingestion transaction atomically commits inbox receipt, notifications, watermarks, and delivery jobs without holding network I/O.
  - Startup configuration validation; readiness checks database & migrations. Kafka/SMTP degradation reported without failing liveness.

### 3.3. Task N04: Policy, Recipient & Template (Chính sách, Người nhận & Bản mẫu nội dung)
- **Objective**: Build notification channel policies, preference evaluation policies, stale action suppression policies, recipient resolution, and code-versioned vi-VN template rendering before connecting Kafka.
- **File Ownership**:
  - `NotificationService.Application/Policies/NotificationChannelPolicy.cs`
  - `NotificationService.Application/Policies/PreferencePolicy.cs`
  - `NotificationService.Application/Policies/StaleActionSuppressionPolicy.cs`
  - `NotificationService.Application/Recipients/RecipientResolutionService.cs`
  - `NotificationService.Application/Templates/NotificationTemplateRenderer.cs`
  - `NotificationService.Application/Templates/NotificationTemplateCatalog.cs`
- **Acceptance Criteria**:
  - Built and tested independently before Kafka integration.
  - Recipient resolved only from valid IDs in event contract; deduped by `(NotificationType, recipient_user_id)`.
  - vi-VN template engine versioned in code, with placeholder allowlists, length limits, HTML encoding, and safe deep link allowlists.
  - Missing `orderCode` handled with generic text and resource ID without external lookups.
  - `LatePaymentRefundRequested`: strictly worded as refund *requested/processing*; NEVER asserts refund or settlement completed.
  - Category preferences checked during job creation and rechecked prior to email dispatch. Re-enabling a preference does not retroactively create jobs.

### 3.4. Task N03: Kafka Ingestion (Tiếp nhận sự kiện từ Kafka)
- **Objective**: Implement Kafka consumer loop on `rental.events.v1`, envelope deserialization, allowlist filtering, transactional inbox deduplication, event handlers, and DLQ quarantine.
- **File Ownership**:
  - `NotificationService.Application/Kafka/Consumers/KafkaConsumerHostedService.cs`
  - `NotificationService.Application/Kafka/Contracts/KafkaMessageEnvelope.cs`
  - `NotificationService.Application/Kafka/Validation/KafkaEnvelopeValidator.cs`
  - `NotificationService.Application/Kafka/Quarantine/IKafkaDeadLetterPublisher.cs`
  - `NotificationService.Application/Kafka/Quarantine/KafkaDeadLetterPublisher.cs`
  - `NotificationService.Application/Kafka/Handlers/IKafkaEventHandler.cs`
  - `NotificationService.Application/Kafka/Handlers/*.cs` (All 10 handlers)
  - `NotificationService.Application/Kafka/Dispatch/KafkaEventDispatcher.cs`
- **Acceptance Criteria**:
  - Dedicated consumer group `notification-service-v1` with manual offset commit (`EnableAutoCommit = false`, `EnableAutoOffsetStore = false`). Offset advanced only after durable persistence or DLQ acknowledgement.
  - Transport worker delegates entirely to Application ingestion service; handlers transform events into notification intents without sending emails or accessing DbContext directly.
  - Unknown event types from allowed producer on topic logged as informational and skipped with offset committed (no quarantine).
  - Envelope/payload validation failures routed to DLQ `rental.events.v1.notification.dlq`.
  - Messages lacking valid message ID use `topic:partition:offset` for quarantine identity (no fake business IDs).
  - Transient retry with exponential backoff and jitter; prolonged dependency outage pauses partition consumption without dropping messages.
  - DLQ and DB committed safely without falsely claiming cross-system distributed transactions.

### 3.5. Task N05: Inbox REST API (Giao diện REST Hộp thư thông báo)
- **Objective**: Implement 6 REST endpoints for notifications query, unread count, single read, read-all with server cutoff, and category preferences.
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
- **Acceptance Criteria**:
  - All 6 endpoints implemented (`GET /api/v1/notifications`, `GET /api/v1/notifications/unread-count`, `PATCH /api/v1/notifications/{id}/read`, `PATCH /api/v1/notifications/read-all`, `GET /api/v1/notifications/preferences`, `PUT /api/v1/notifications/preferences`).
  - Queries use `.AsNoTracking()`, `.Select()` projections, server-side count/filters, and `CancellationToken`.
  - Mark-read is idempotent (preserves original `ReadAt`); returns 404 for non-existent or unowned notification.
  - Read-all uses a single filtered batch database update with server-generated `cutoffUtc` (`TimeProvider.GetUtcNow()`); client cannot supply cutoff.
  - Consistent DTO validation and ProblemDetails error mapping; user identity extracted solely from Bearer JWT (`sub`).

### 3.6. Task N06: Email Delivery (Tiến trình gửi Email có bảo vệ Lease)
- **Objective**: Implement transactional SMTP email worker with PostgreSQL `FOR UPDATE SKIP LOCKED`, lease fencing, crash recovery, and `Uncertain` state handling.
- **File Ownership**:
  - `NotificationService.Application/Email/IEmailSender.cs`
  - `NotificationService.Application/Email/SmtpEmailSender.cs`
  - `NotificationService.Application/Email/Workers/EmailDeliveryWorker.cs`
  - `NotificationService.Application/Email/Workers/EmailLeaseReconciliationWorker.cs`
  - `NotificationService.Application/Email/Options/SmtpOptions.cs`
- **Acceptance Criteria**:
  - Single real SMTP adapter; dev/test uses real SMTP sink (MailHog/test sink).
  - Claim jobs using lease and `FOR UPDATE SKIP LOCKED`; completion requires matching lease token.
  - Persists `dispatch-start` before calling SMTP. If worker crashes or timeout occurs after `DATA` phase, transitions to `Uncertain` (blind retry strictly forbidden).
  - Max 5 retries for verifiable pre-DATA errors; permanent failures marked `Failed`; inactive/unverified/preference/stale marked `Suppressed`.
  - Detailed sanitized attempt audit; no DB transactions open during SMTP I/O.

### 3.7. Task N07: Security & Realtime (Bảo mật & Kênh thông báo tức thời)
- **Objective**: Implement JWT/JWKS authentication, human vs service principal validation, Auth internal contact client, and SignalR realtime hub.
- **File Ownership**:
  - `NotificationService.Api/Security/*.cs`
  - `NotificationService.Api/Hubs/NotificationHub.cs`
  - `NotificationService.Application/IServices/INotificationRealtimeNotifier.cs`
  - `NotificationService.Api/Services/SignalRNotificationRealtimeNotifier.cs`
  - `NotificationService.Application/Clients/IAuthContactClient.cs`
  - `NotificationService.Application/Clients/AuthContactClient.cs`
- **Acceptance Criteria**:
  - Bearer JWT validated via AuthService JWKS; requires human GUID `sub`; rejects service principal (`u: "svc"`), no admin bypass.
  - SignalR hub `/api/v1/notifications/hubs/inbox` supports header/cookie auth, rejects query string tokens (`?access_token=`), enforces origin allowlist, and auto-disconnects on token expiration.
  - Server manages user group `user:{sub}`; emits `NotificationInboxChanged` only after DB transaction commits. Client refetches badges/list upon receipt.
  - `AuthContactClient` batches requests (max 100 users); emails sent only to active, email-verified accounts.

### 3.8. Task N08: Kiểm thử, CI và vận hành (Testing, CI & Operational Readiness)
- **Objective**: Comprehensive unit, integration, and contract tests across all scenarios; CI pipeline configuration; metrics and operational runbooks.
- **Acceptance Criteria**:
  - Unit tests for matrix, templates, suppression, siblings, financial wording.
  - Integration tests for PostgreSQL migrations, unique constraints, concurrent ingestion, mark-read, lease fencing.
  - Kafka tests for duplicates, commits, DLQ.
  - SMTP tests for pre/post-DATA failures, lease recovery.
  - SignalR tests for JWT, IDOR, group isolation, commit-bound dispatch.
  - CI pipeline, non-root Docker build, metrics, and operational runbook.
