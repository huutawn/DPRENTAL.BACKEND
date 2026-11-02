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

## 5. Database Rollback & Migration Preflight
Before applying migrations:
1. Verify database connectivity:
   ```bash
   pg_isready -h localhost -p 5432 -U postgres -d dprental_notification
   ```
2. Apply pending migrations:
   ```bash
   dotnet ef database update --project NotificationService/src/Domain --startup-project NotificationService/src/Api
   ```
3. Rollback (if necessary to rollback to initial clean state):
   ```bash
   dotnet ef database update 0 --project NotificationService/src/Domain --startup-project NotificationService/src/Api
   ```
