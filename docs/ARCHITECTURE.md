# NotificationService — Architecture Design Document (Phase 1 MVP)

## 1. System Overview & Context

NotificationService is an independent, event-driven microservice in the DPRental platform. Its primary responsibility is consuming business domain events from upstream services (`RentalService`), resolving recipient identities, rendering channel-specific notification templates, persisting user notifications in PostgreSQL, dispatching transactional emails, and notifying connected clients in realtime via SignalR.

```text
                           ┌───────────────────────────────┐
                           │      RentalService            │
                           │   (Transactional Outbox)      │
                           └──────────────┬────────────────┘
                                          │
                                          │ Kafka (rental.events.v1)
                                          ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│                             NotificationService                                  │
│                                                                                  │
│  ┌────────────────────────────────────────────────────────────────────────────┐  │
│  │                            Kafka Consumer Layer                            │  │
│  │  - Envelope Validation   - Idempotency & Inbox   - Recipient Resolution     │  │
│  │  - Policy & Watermark    - Template Rendering    - Failure Quarantine       │  │
│  └─────────────────────────────────────┬──────────────────────────────────────┘  │
│                                        │                                         │
│                                        ▼                                         │
│  ┌────────────────────────────────────────────────────────────────────────────┐  │
│  │                  PostgreSQL Transaction (schema: notification)             │  │
│  │  - inbox_receipts   - notifications   - aggregate_watermarks   - delivery  │  │
│  └────────┬────────────────────────────┬─────────────────────────────┬────────┘  │
│           │                            │                             │           │
│           ▼                            ▼                             ▼           │
│  ┌──────────────────┐        ┌──────────────────┐          ┌──────────────────┐  │
│  │  REST Inbox API  │        │ SignalR Hub API  │          │ Email Delivery   │  │
│  │  /api/v1/notifi- │        │ /api/v1/notifi-  │          │ Worker Loop      │  │
│  │  cations/*       │        │ cations/hubs/    │          │ (Lease / SMTP)   │  │
│  │  (JWT sub query) │        │ inbox            │          │                  │  │
│  └────────▲─────────┘        └────────▲─────────┘          └────────┬─────────┘  │
└───────────┼───────────────────────────┼─────────────────────────────┼────────────┘
            │                           │                             │
    HTTP / JSON                   WebSocket (WSS)                   SMTP (TLS)
            │                           │                             │
            ▼                           ▼                             ▼
┌──────────────────────────────────────────────────┐         ┌─────────────────────┐
│             API Gateway (Ocelot)                 │         │ Transactional SMTP  │
│     (Reverse Proxy, JWT Auth, Route Guard)       │         │ Provider (Mailhog/  │
└───────────────────────▲──────────────────────────┘         │ SendGrid/SES)       │
                        │                                    └─────────────────────┘
                 Browser / Mobile
```

---

## 2. Architectural Layers & Structure

NotificationService follows Clean Architecture, organized into four projects with strict unidirectional dependency flow:

```text
NotificationService.Api
  ├── Controllers/ (REST endpoints: notifications, preferences)
  ├── Hubs/ (SignalR NotificationHub: inbox invalidation)
  ├── Security/ (JWT Bearer, JWKS configuration, claims extraction)
  ├── Filters/ (ExpectedErrorFilterExtensions, ProblemDetails mapping)
  ├── Observability/ (OpenTelemetry tracing, metrics, health checks)
  └── Program.cs
        │
        ▼ depends on
NotificationService.Application
  ├── IServices/ (Query, Read, Delivery, Preference service contracts)
  ├── Services/ (NotificationQueryService, NotificationReadService, etc.)
  ├── Kafka/ (Consumers, Envelope DTOs, Event Handlers, Quarantine)
  ├── Policies/ (ChannelPolicy, StaleActionSuppressionPolicy, PreferencePolicy)
  ├── Templates/ (Versioned Template definitions, ITemplateRenderer, vi-VN resources)
  ├── Email/ (IEmailSender, SmtpEmailSender, EmailDeliveryWorker)
  ├── DTOs/ (API requests, responses, pagination)
  ├── Validation/ (FluentValidators for query and mutation models)
  └── DependencyInjection/ (Service collection registration)
        │
        ▼ depends on
NotificationService.Domain
  ├── Entities/ (Notification, NotificationInboxReceipt, NotificationDeliveryJob,
  │              NotificationPreference, NotificationAggregateWatermark)
  ├── IRepositories/ (Repository abstractions)
  ├── Repositories/ (EF Core repository implementations)
  └── Migrations/ (EF Core PostgreSQL migrations for schema 'notification')
        │
        ▼ depends on
NotificationService.Common
  ├── Error.cs (Typed error model)
  ├── Result.cs (Result<T> pattern for functional error handling)
  ├── Enums/ (NotificationType, NotificationCategory, DeliveryStatus, etc.)
  └── Utils/ (TimeProvider helpers, cryptographic utilities)
```

### Architectural Invariants:
1. **Core Independence**: `Domain` contains pure data models and persistence mappings. It has NO dependencies on `DbContext`, Kafka libraries, or SMTP clients.
2. **Business Logic Ownership**: `Application` owns ALL business rules, recipient resolution, watermark checks, template rendering, and transaction coordination.
3. **Transport Layer Purity**: `Api` controllers, SignalR hubs, and background workers perform transport-level routing, authentication context extraction, and error translation; they never bypass `Application` services.

---

## 3. Kafka Event Ingestion Pipeline

### 3.1. Consumer Configuration
- **Topic**: `rental.events.v1`
- **Consumer Group**: `notification-service-v1`
- **Offset Commit**: Manual commit (`EnableAutoCommit = false`), performed strictly AFTER PostgreSQL transaction commit.
- **Partition Key**: `RentalOrderId` (guaranteeing in-order delivery per order aggregate across partitions).
- **Dead-Letter / Quarantine Topic**: `rental.events.v1.notification.dlq`

### 3.2. Processing Pipeline Steps
For every consumed Kafka record:
```text
1. Intake Record ──► 2. Validate Envelope ──► 3. Allowlist Check ──► 4. Idempotency Check
                                                                           │
                                                    ┌──────────────────────┘
                                                    ▼
5. Recipient Resolution ──► 6. Watermark Check ──► 7. Template Render ──► 8. Atomic DB Tx
                                                                                 │
                                                    ┌────────────────────────────┘
                                                    ▼
9. Commit DB Tx ──► 10. Commit Kafka Offset ──► 11. Realtime Invalidation Signal
```

1. **Intake & Metadata Extraction**: Read raw JSON payload, Kafka message key, partition, and offset.
2. **Envelope Validation**: Validate envelope version (`version == 1`), `messageId` (non-empty GUID), `producer == "RentalService"`, and `occurredAt`.
3. **Allowlist Verification**: Verify `messageType` is one of the 10 supported Phase 1 MVP types. If unlisted:
   - If from an allowed topic with known producer but unknown message type: log as ignored and commit offset (no quarantine).
   - If payload is malformed or envelope invalid: write to DLQ / quarantine table.
4. **Idempotency Pre-Check**: Check `notification.inbox_receipts` for `(consumer_name = 'notification-service-v1', message_id = envelope.MessageId)`. If already present:
   - Message is a duplicate. Immediately commit Kafka offset and acknowledge without side effects.
5. **Recipient Identity Resolution**: Extract authoritative recipient GUID (`RenterId` or `SupplierId`) directly from event payload. Enforce zero-inference policy (reject and quarantine if GUID is empty).
6. **Aggregate Watermarking & Stale Suppression**: Check `notification.aggregate_watermarks` for `(aggregate_id, recipient_user_id)`. If incoming `orderVersion < last_order_version` or order is in a superseding terminal state, suppress actionable calls-to-action.
7. **Template Rendering**: Merge event payload with versioned `vi-VN` template, escaping dynamic values to prevent HTML injection.
8. **Atomic PostgreSQL Transaction**: In a single database transaction:
   - Insert `NotificationInboxReceipt` (`status = Processed`).
   - Insert `Notification` record(s) for resolved recipient(s).
   - Insert `NotificationDeliveryJob` record(s) for channels enabled by policy and preferences (e.g. Email).
   - Upsert `NotificationAggregateWatermark` with latest version and state.
9. **Kafka Offset Commit**: Synchronously commit Kafka offset.
10. **Realtime Invalidation**: Post-commit dispatch of `NotificationInboxChanged` to SignalR hub group `user:{recipientUserId}`.

---

## 4. PostgreSQL Persistence Architecture

NotificationService operates exclusively within the `notification` schema in PostgreSQL.

### 4.1. Tables & Schema Definitions

```sql
CREATE SCHEMA IF NOT EXISTS notification;

-- 1. Notifications Table
CREATE TABLE notification.notifications (
    id UUID PRIMARY KEY,
    recipient_user_id UUID NOT NULL,
    notification_type VARCHAR(64) NOT NULL,
    category VARCHAR(32) NOT NULL,
    severity VARCHAR(16) NOT NULL,
    title VARCHAR(256) NOT NULL,
    body TEXT NOT NULL,
    resource_type VARCHAR(64) NOT NULL,
    resource_id VARCHAR(128) NOT NULL,
    source_event_id UUID NOT NULL,
    source_producer VARCHAR(64) NOT NULL,
    source_event_type VARCHAR(64) NOT NULL,
    deep_link_url VARCHAR(512),
    is_actionable BOOLEAN NOT NULL DEFAULT FALSE,
    action_url VARCHAR(512),
    occurred_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    read_at TIMESTAMPTZ,
    CONSTRAINT uq_notifications_source_recipient_type
        UNIQUE (source_event_id, recipient_user_id, notification_type)
);

CREATE INDEX ix_notifications_recipient_created
    ON notification.notifications (recipient_user_id, created_at DESC, id DESC);

CREATE INDEX ix_notifications_recipient_unread
    ON notification.notifications (recipient_user_id, read_at)
    WHERE read_at IS NULL;

-- 2. Inbox Receipts Table (Idempotency)
CREATE TABLE notification.inbox_receipts (
    id UUID PRIMARY KEY,
    consumer_name VARCHAR(64) NOT NULL,
    message_id UUID NOT NULL,
    producer VARCHAR(64) NOT NULL,
    message_type VARCHAR(64) NOT NULL,
    status VARCHAR(32) NOT NULL, -- Processed, Ignored, Quarantined
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    error_code VARCHAR(64),
    error_detail TEXT,
    CONSTRAINT uq_inbox_receipts_consumer_message
        UNIQUE (consumer_name, message_id)
);

CREATE INDEX ix_inbox_receipts_processed_at
    ON notification.inbox_receipts (processed_at DESC);

-- 3. Delivery Jobs Table (Email & Outbound Channels)
CREATE TABLE notification.delivery_jobs (
    id UUID PRIMARY KEY,
    notification_id UUID NOT NULL REFERENCES notification.notifications(id) ON DELETE CASCADE,
    channel VARCHAR(32) NOT NULL, -- Email
    status VARCHAR(32) NOT NULL,  -- Pending, Processing, Succeeded, RetryScheduled, Uncertain, Failed, Suppressed
    attempt_count INT NOT NULL DEFAULT 0,
    max_attempts INT NOT NULL DEFAULT 5,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lease_token VARCHAR(64),
    lease_expires_at TIMESTAMPTZ,
    provider_message_id VARCHAR(128),
    last_error_code VARCHAR(64),
    last_error_detail TEXT,
    sent_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_delivery_jobs_notification_channel
        UNIQUE (notification_id, channel)
);

CREATE INDEX ix_delivery_jobs_pending_claim
    ON notification.delivery_jobs (status, next_attempt_at ASC)
    WHERE status IN ('Pending', 'RetryScheduled');

CREATE INDEX ix_delivery_jobs_lease_recovery
    ON notification.delivery_jobs (status, lease_expires_at ASC)
    WHERE status = 'Processing';

-- 4. User Notification Preferences Table
CREATE TABLE notification.preferences (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    category VARCHAR(32) NOT NULL, -- ORDER, PAYMENT, HANDOVER, RETURN
    email_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_preferences_user_category
        UNIQUE (user_id, category)
);

CREATE INDEX ix_preferences_user_id
    ON notification.preferences (user_id);

-- 5. Aggregate Watermarks Table
CREATE TABLE notification.aggregate_watermarks (
    aggregate_id UUID NOT NULL,
    recipient_user_id UUID NOT NULL,
    last_order_version BIGINT NOT NULL DEFAULT 0,
    terminal_state VARCHAR(32),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (aggregate_id, recipient_user_id)
);
```

---

## 5. Recipient Resolution & Notification Matrix

### 5.1. Target Event Verification & Source Evidence
Code inspection of `RentalService` (commit `159702d3a94e824e4da6da8af2bfc9867006119e`) confirms the real use case, transactional outbox enlistment, and recipient fields for all 10 target events:

| # | Event Name | Producer | Real Use Case & Source File | Recipient In Payload | Channels (Phase 1) | Financial & Domain Rule |
|---|------------|----------|-----------------------------|----------------------|--------------------|-------------------------|
| 1 | `RentalOrderCreated` | `RentalService` | `CheckoutService.cs` line 591 (`CreateOrderAsync`) | `RenterId` (Guid), `Items[].SupplierId` | In-app: Yes<br>Email: No | Prompts payment before deadline; suppressed if already paid/expired. |
| 2 | `RentalOrderPaid` | `RentalService` | `PaymentWorkflowService.cs` line 186 (`ProcessInvoicePaidAsync`) | `RenterId` (Guid) | In-app: Yes<br>Email: Yes | Confirms payment received; advances aggregate watermark. |
| 3 | `RentalSupplierConfirmationRequested` | `RentalService` | `InventoryAllocationResultHandler.cs` line 586 (`ExtendHold` success) | `SupplierId` (Guid) | In-app: Yes<br>Email: Yes | Prompts supplier to confirm suborder before deadline. |
| 4 | `RentalOrderExpired` | `RentalService` | `RentalOperationExecutor.Payment.cs` line 434; `ExpiryWorkflowService.cs` line 302 | `RenterId` (Guid) | In-app: Yes<br>Email: No | Terminal state; cancels pending payment prompts. |
| 5 | `RentalOrderCancelled` | `RentalService` | `RentalOperationExecutor.Cancellation.cs` line 283; `ItemCancellationService.cs` line 282 | `RenterId` (Guid) | In-app: Yes<br>Email: No | Terminal state; informs renter of cancellation reason. |
| 6 | `RentalItemHandoverCompleted` | `RentalService` | `InventoryAllocationResultHandler.cs` line 859 (`MarkAssetsHandedOver` confirmed by Inventory) | `RenterId` (Guid), `SupplierId` (Guid) | In-app: Renter<br>Email: No | Notifies renter that equipment handover is verified by inventory. |
| 7 | `RentalHandoverOverdue` | `RentalService` | `HandoverOverdueService.cs` line 171 via `HandoverOverdueWorker` | `SupplierId` (Guid), `RenterId` (Guid) | In-app: Both<br>Email: Supplier | Alerts supplier of overdue handover obligation. |
| 8 | `ReturnConfirmationOverdue` | `RentalService` | `ReturnConfirmationOverdueService.cs` line 102 via `ReturnConfirmationOverdueWorker` | `SupplierId` (Guid), `RenterId` (Guid) | In-app: Both<br>Email: Supplier | Alerts supplier of overdue return inspection obligation. |
| 9 | `RentalOrderCompleted` | `RentalService` | `RentalSettledConsumer.cs` line 127; `ItemCancellationService.cs` lines 300, 528 | `RenterId` (Guid) | In-app: Yes<br>Email: Yes | Order fully settled; final completion statement. |
| 10 | `LatePaymentRefundRequested` | `RentalService` | `PaymentWorkflowService.cs` line 482 (`StageFullRefundAndReleaseAsync`) | `RenterId` (Guid) | In-app: Yes<br>Email: No | **CRITICAL**: Informs that refund processing is REQUESTED. NEVER states refund succeeded! |

---

## 6. In-App Inbox REST API Specification

All REST endpoints require a valid Human JWT Bearer token issued by `AuthService`. The user identity is extracted from claim `sub`. Service tokens (`u: "svc"`) are rejected with `403 Forbidden`.

### 6.1. Endpoints Overview
- `GET /api/v1/notifications`: List notifications with stable pagination and optional category/read filters.
- `GET /api/v1/notifications/unread-count`: Returns the exact unread count via an aggregate database query.
- `PATCH /api/v1/notifications/{id}/read`: Marks a single notification as read (idempotent, ownership validated).
- `PATCH /api/v1/notifications/read-all`: Marks all unread notifications as read up to a server-side cutoff timestamp.
- `GET /api/v1/notifications/preferences`: Returns user email preferences across categories.
- `PUT /api/v1/notifications/preferences`: Updates user email preferences.

### 6.2. Pagination Snapshot Semantics
- Query parameters:
  - `page`: int, default 1, min 1.
  - `pageSize`: int, default 20, min 1, max 100.
  - `status`: string, `all` (default) or `unread`.
  - `category`: string optional (`ORDER`, `PAYMENT`, `HANDOVER`, `RETURN`, `SYSTEM`).
- Ordering is strictly deterministic: `ORDER BY created_at DESC, id DESC`.
- Queries execute as `.AsNoTracking()` projections to `NotificationListItemDto`.

### 6.3. Server-Side Cutoff for Mark-All-Read
`PATCH /api/v1/notifications/read-all` does not accept a client-provided cutoff timestamp. The server determines `cutoffUtc` via its authoritative UTC clock (`TimeProvider.GetUtcNow()`).
  ```sql
  UPDATE notification.notifications
  SET read_at = @ServerNow
  WHERE recipient_user_id = @UserId
    AND read_at IS NULL
    AND created_at <= @CutoffUtc;
  ```
- This guarantees that new notifications arriving concurrently during user interaction are NOT inadvertently marked as read before the user has seen them.

---

## 7. Realtime Notification Hub (SignalR)

### 7.1. Hub Design & Security
- **Hub Endpoint**: `/api/v1/notifications/hubs/inbox`
- **Transport**: WebSocket preferred; Long Polling supported as fallback.
- **Authentication**:
  - Authentication occurs during the initial HTTP handshake / negotiate request.
  - The token is extracted from the `Authorization: Bearer <token>` header or HTTP-only cookie `accessToken`.
  - **Query string tokens (`?access_token=...`) are explicitly REJECTED** to prevent token leakage in proxy logs.
- **Group Isolation**:
  - Upon connection, the hub automatically assigns the connection to group `user:{sub}`.
  - Clients cannot join, leave, or broadcast to arbitrary groups.

### 7.2. Realtime Payload & Semantics
The hub emits a single invalidation event to connected clients:
- **Event Name**: `NotificationInboxChanged`
- **Payload**:
  ```json
  {
    "notificationId": "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d",
    "changeType": "CREATED",
    "occurredAt": "2026-11-02T03:00:00Z"
  }
  ```
- **Client Contract**: The event serves as a cache invalidation signal. Clients MUST NOT treat the realtime message as the authoritative source of truth. Upon receiving `NotificationInboxChanged`, frontend triggers an unread-count update and conditionally refetches the top page of the inbox.

---

## 8. Transactional Email Delivery Engine

### 8.1. Architecture & Leased Processing
Email dispatch is decoupled from Kafka consumption via the `notification.delivery_jobs` queue:
1. `EmailDeliveryWorker` runs as a hosted background service (`IHostedService`).
2. Jobs are claimed using PostgreSQL `FOR UPDATE SKIP LOCKED` with a lease duration of 5 minutes (`lease_expires_at = NOW() + INTERVAL '5 minutes'`).
3. Database transactions are committed BEFORE initiating network calls to the SMTP provider.

### 8.2. Recipient Contact Resolution Contract
NotificationService does not store or own user profile data. Before dispatching email:
- Worker queries `AuthService` via internal batch endpoint:
  `POST /api/v1/auth/internal/users/notification-contacts:batch`
- Returns: `{ userId, email, isActive, isEmailVerified }`.
- If `isActive == false` or `isEmailVerified == false`, job is transitioned to `Suppressed`.

### 8.3. State Transitions & Uncertain Outcome Handling
```text
  ┌──────────────┐
  │   Pending    │
  └──────┬───────┘
         │ Worker Lease Claim
         ▼
  ┌──────────────┐
  │  Processing  │
  └──────┬───────┘
         ├──────────────────────────┬─────────────────────────┬────────────────────────┐
         │ 250 OK                   │ Transient SMTP error    │ Permanent 5xx error    │ Network timeout after
         ▼                          ▼ (attempts < max)        ▼                        │ DATA payload sent
  ┌──────────────┐           ┌──────────────┐          ┌──────────────┐                ▼
  │  Succeeded   │           │RetryScheduled│          │    Failed    │         ┌──────────────┐
  └──────────────┘           └──────────────┘          └──────────────┘         │  Uncertain   │
                                                                                └──────────────┘
```

- **Uncertain State Rule**: If the network connection drops or times out after sending the email `DATA` payload, NotificationService cannot know if the SMTP server queued the message. The job is marked `Uncertain`. **BLIND RETRY IS STRICTLY FORBIDDEN**. An alert is raised for operational audit and provider log reconciliation.

---

## 9. Security & Boundary Architecture

### 9.1. Identity & Token Verification
- Public keys are loaded dynamically from AuthService via JWKS at `/.well-known/jwks.json`.
- Tokens are cached and refreshed before expiry; failed key discovery triggers graceful retry.
- In-app APIs validate issuer (`dprental-auth-service`), audience (`dprental-platform`), expiration, and signature.

### 9.2. API Gateway & Internal Route Protection
- Gateway (`ServiceOnlyRouteBlockingMiddleware`) blocks all routes matching `/internal/` with HTTP 403 Forbidden.
- External requests cannot access internal endpoints (`/api/v1/auth/internal/*`).
- Service-to-service communication is secured over the internal Docker network using mutual shared key (`InternalAuthentication:ApiKey` / `X-Internal-Api-Key`).

---

## 10. Observability & Health Checks

### 10.1. OpenTelemetry & Distributed Tracing
- NotificationService implements W3C `traceparent` propagation.
- Traces are exported via OTLP (`/v1/traces`).
- Incoming Kafka messages carry W3C trace headers (`traceparent`, `tracestate`) which are extracted to link Kafka consumption spans to upstream RentalService checkout/payment traces.

### 10.2. Key Operational Metrics
- `notification_events_consumed_total`: Counter of Kafka messages consumed by `messageType` and `status`.
- `notification_duplicates_detected_total`: Counter of idempotent duplicates skipped.
- `notification_created_total`: Counter of user notifications persisted by `notification_type` and `category`.
- `notification_email_jobs_total`: Counter of email delivery outcomes (`succeeded`, `retry_scheduled`, `failed`, `uncertain`, `suppressed`).
- `notification_email_duration_seconds`: Histogram of SMTP send latency.
- `notification_realtime_connections`: Current count of active SignalR WebSocket connections.

### 10.3. Health Endpoints
- **Liveness** (`GET /health/live`): Process is running and responding to HTTP requests.
- **Readiness** (`GET /health/ready`): PostgreSQL connection is healthy and database migrations are up to date. Degraded Kafka or SMTP connectivity does not cause liveness failure to prevent unnecessary container restart loops.
