# NotificationService — Operational Runbook & Production Guide

This runbook documents operational procedures, incident response workflows, and monitoring guides for `NotificationService`.

---

## 1. Service Health & Readiness Probes

### 1.1. Endpoints
- **Liveness Probe**: `GET /health/live`
  - Returns `200 OK` when the process is alive.
  - Does NOT check downstream dependencies to prevent cascading container restarts.
- **Readiness Probe**: `GET /health/ready`
  - Returns `200 OK` when PostgreSQL database is reachable and migrations are applied.
  - Returns `503 Service Unavailable` if database connectivity is lost.

### 1.2. Preflight & Smoke Test
```bash
# 1. Check Liveness
curl -fsS http://localhost:8080/health/live

# 2. Check Readiness (Database connectivity)
curl -fsS http://localhost:8080/health/ready

# 3. Check OpenAPI / Swagger UI
curl -fsS http://localhost:8080/swagger/v1/swagger.json
```

---

## 2. Kafka DLQ & Quarantine Runbook

### 2.1. Topics
- **Main Intake Topic**: `rental.events.v1`
- **Consumer Group**: `notification-service-v1`
- **Dead Letter Queue (DLQ)**: `rental.events.v1.notification.dlq`

### 2.2. When Are Messages Quarantined?
1. Malformed JSON payloads that cannot be deserialized into `KafkaMessageEnvelope`.
2. Invalid or missing `EventId` (not a valid GUID or empty).
3. Invalid producer (not `RentalService`).
4. Unsupported contract version (not `version = 1`).
5. Missing or invalid recipient ID (Zero-Inference Policy enforcement).

### 2.3. DLQ Inspection & Replay Procedure
```bash
# 1. Read quarantined messages from DLQ without committing offsets
kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic rental.events.v1.notification.dlq \
  --from-beginning \
  --max-messages 10

# 2. Replay Procedure
# Once upstream issue is identified and patched, re-publish the sanitized JSON to rental.events.v1
kafka-console-producer \
  --bootstrap-server localhost:9092 \
  --topic rental.events.v1
```

---

## 3. Email Delivery & Lease Crash Recovery

### 3.1. Job States
- `Pending`: Queued for dispatch.
- `Processing`: Claimed by a worker with a 5-minute lease token.
- `Succeeded`: Accepted by SMTP provider (`SentAt` recorded).
- `RetryScheduled`: Pre-DATA transient error; scheduled with exponential backoff (max 5 attempts).
- `Uncertain`: Connection dropped or timed out after the `DATA` phase.
- `Failed`: Permanent failure (5xx SMTP error or max retries exceeded).
- `Suppressed`: Recipient inactive, email unverified, or category preference disabled.

### 3.2. Incident: Worker Crash & Expired Leases
If an `EmailDeliveryWorker` instance crashes while processing jobs:
- The jobs remain in `Processing` state with an active `lease_token`.
- The `EmailLeaseReconciliationWorker` runs every 60 seconds and automatically identifies jobs where `lease_expires_at <= NOW()`.
- Expired leases are atomically reset to `status = 'Pending'` with `lease_token = NULL`, enabling other active workers to claim them.

### 3.3. Incident: Handling `Uncertain` Jobs
**STRICT INVARIANT: BLIND RETRY IS FORBIDDEN FOR UNCERTAIN JOBS.**
When an SMTP connection drops after the `DATA` command, the remote mail server may have already accepted and delivered the email.
1. Query uncertain jobs:
   ```sql
   SELECT id, notification_id, recipient_user_id, last_error_code, last_error_detail, updated_at
   FROM notification.delivery_jobs
   WHERE status = 'Uncertain';
   ```
2. Check SMTP provider logs (e.g., Mailgun, SendGrid, or MailHog) for the matching recipient email and timestamp.
3. If confirmed sent:
   ```sql
   UPDATE notification.delivery_jobs
   SET status = 'Succeeded', updated_at = NOW()
   WHERE id = '<JobId>';
   ```
4. If confirmed NOT sent:
   ```sql
   UPDATE notification.delivery_jobs
   SET status = 'Pending', updated_at = NOW()
   WHERE id = '<JobId>';
   ```

---

## 4. SMTP Outage & Degradation
If SMTP provider experiences an outage:
- Jobs encounter pre-DATA connection errors and transition to `RetryScheduled` with exponential backoff.
- NotificationService continues ingesting Kafka events and serving in-app inbox REST APIs normally (inbox availability is isolated from SMTP outages).
- Once SMTP recovers, `EmailDeliveryWorker` drains the pending queue.

---

## 5. Database Migration, Preflight & Safe Rollback

### 5.1. Preflight Checklist
1. Stop intake consumer and delivery workers (`KafkaConsumerHostedService`, `EmailDeliveryWorker`).
2. Create full backup of the `notification` schema:
   ```bash
   pg_dump -h localhost -p 20992 -U notificationservice -n notification -Fc -f notification_backup_$(date +%s).dump notificationservice
   ```
3. Check migration status via `GET /health/ready` or EF tools:
   ```bash
   dotnet ef migrations has-pending-model-changes --project NotificationService/src/Domain --startup-project NotificationService/src/Api
   ```

### 5.2. Apply Upgrade Migration Chain
```bash
dotnet ef database update --project NotificationService/src/Domain --startup-project NotificationService/src/Api
```
- Applies `20261104042342_AddNotificationSchemaUpgrades` and `20261104075246_AddLinkageAndCorrectPaidMilestones`.
- Preserves all existing `notifications`, `inbox_receipts`, and `delivery_jobs`.
- Renames columns to snake_case matching PostgreSQL conventions (`status`, `read_at`, `lease_token`, etc.).
- Adds `rental_item_id` on notifications (`ix_notifications_rental_item`) and `parent_item_id` on aggregate watermarks (`ix_aggregate_watermarks_parent_item`).
- Corrects historical `PAID` watermarks: resets `terminal_state = NULL`, `is_terminal = false`, and sets `is_paid = true`.
- Preserves actual terminal states (`CANCELLED`, `COMPLETED`, `EXPIRED`, `REJECTED`).
- Backfills `is_paid = true` for orders with recorded `ORDER_PAID` / `RENTAL_ORDER_PAID` notifications.
- Backfills `rental_item_id` from valid UUIDs in `render_data_json ->> 'rentalItemId'` and `parent_item_id` on ReturnRequest watermarks.

### 5.3. Safe Rollback Procedure
If rollback is necessary:
```bash
# Rollback to previous schema upgrade
dotnet ef database update 20261104042342_AddNotificationSchemaUpgrades --project NotificationService/src/Domain --startup-project NotificationService/src/Api
```
Do NOT run `database update 0` as that would drop all tables and destroy audit history.

---

## 6. Job Reconciliation Runbook (Stale Suppression Recovery)

Due to previous blanket suppression on terminal states, informational result notifications (`ORDER_PAID`, `ORDER_COMPLETED`, `REFUND_COMPLETED`, `PAYOUT_COMPLETED`) may have had their email delivery jobs suppressed with `errorCode = 'STALE_ACTION_SUPPRESSED'`.

### 6.1. Candidate Audit (Dry-Run Projection)
Inject `IJobReconciliationService` or run via operational CLI/endpoint:
```csharp
var candidateReport = await reconciliationService.GetCandidatesAsync(limit: 100);
logger.LogInformation("Dry-run audit: Found {Count} candidate suppressed jobs to requeue: {@JobIds}",
    candidateReport.TotalCandidates, candidateReport.CandidateJobIds);
```
- Evaluates both legacy error codes: `STALE_ACTION_SUPPRESSED` and `STALE_EVENT_TERMINATED`.
- Verifies exact counts and job IDs before taking any mutation action (capped at 100 per request).
- Strictly excludes jobs with `DispatchStartedAt != null`, `SentAt != null`, `ProviderMessageId != null`, active leases (`LeaseExpiresAt > now`), or exhausted retry budgets (`AttemptCount >= MaxAttempts`).

### 6.2. Controlled Requeue Execution
When approved during a maintenance window with a mandatory operator/run ID:
```csharp
var execReport = await reconciliationService.RequeueJobsAsync(
    candidateReport.CandidateJobIds,
    operatorId: "operator-maint-20261104-01");

logger.LogInformation("Reconciliation executed: Operator '{Op}' requeued {Reconciled}/{Requested} jobs.",
    execReport.OperatorId, execReport.ReconciledCount, execReport.RequestedCount);
```
- Atomic conditional update via `DeliveryJobRepository`: if a background worker or another reconciler changed the job state in the interim, the update affects 0 rows and worker leases are preserved.
- Resets eligible jobs to `status = 'Pending'`, `lease_token = NULL`, `lease_expires_at = NULL`.
- Records non-PII audit trail in `last_error_detail` (`"Requeued by op:{operatorId} at {timestamp}; prev_code:{prevCode}"`).
- Completely idempotent: running a second time finds 0 candidates.
---

## 7. Realtime & Redis Backplane Configuration

### 7.1. Mandatory Redis Connection
- Redis connection string (`ConnectionStrings:Redis` or `Redis:ConnectionString`) is mandatory for all non-test environments.
- Startup validation fails fast with `InvalidOperationException` if Redis configuration is absent or invalid.
- Local in-memory SignalR without a backplane is strictly prohibited in multi-replica deployments.

### 7.2. Channel Prefixing & Isolation
- Channel prefix is automatically configured based on environment: `dprental:notification:{environment}`.
- Replicas belonging to the same deployment cluster must connect to the same Redis instance/cluster and share the same channel prefix.

### 7.3. Redis Resilience
- Configured with `AbortOnConnectFail = false` and 5 reconnection retries.
- Temporary Redis drops log warnings and increment metrics, but do NOT crash the host process or abort database transactions.

---

## 8. Authentication & JWKS Configuration

### 8.1. Mandatory Parameters
- `Jwt:Issuer`: Expected token issuer (e.g., `https://auth.dprental.vn`).
- `Jwt:Audience`: Expected token audience (e.g., `dprental-platform`).
- `Jwt:JwksUri`: Absolute HTTP/HTTPS URI to AuthService JWKS endpoint (e.g., `http://auth-service:8080/.well-known/jwks.json`).

### 8.2. Key Caching & Rotation Rules
- **Periodic Refresh**: Automatically refreshes keys every 15 minutes.
- **Stale Cache Fallback**: Allows cached keys up to 60 minutes if the JWKS endpoint is unreachable.
- **Fail-Closed**: Rejects all tokens after 60 minutes if the endpoint remains down.
- **Unknown Key ID Throttling**: Requests with unknown `kid` trigger a forced refresh, throttled to at most once per 30 seconds to prevent DDoS.
- **Human vs. Service JWT Policy**: Service tokens (`u: "svc"`) or tokens lacking a valid User GUID `sub` are rejected with `403 Forbidden` on user inbox REST endpoints and SignalR Hub negotiate. Query string `access_token` is strictly rejected with `401 Unauthorized`.
