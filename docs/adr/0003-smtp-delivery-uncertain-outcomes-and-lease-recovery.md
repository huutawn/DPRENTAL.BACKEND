# ADR-0003: SMTP Delivery Engine, Uncertain Outcomes, and Lease Crash Recovery

## Status
Accepted

## Context
NotificationService provides email delivery for high-severity transactional notifications (`RentalOrderPaid`, `RentalSupplierConfirmationRequested`, `RentalHandoverOverdue`, `ReturnConfirmationOverdue`, `RentalOrderCompleted`).
Email delivery involves network calls to transactional SMTP providers (or cloud email gateways). Network communication with SMTP providers is inherently fallible:
1. Provider connection timeout before connection establishment.
2. Provider read/send timeout while transmitting the message body.
3. Socket disconnect after the client sends the final `.` in the `DATA` phase, but before the server returns the `250 OK` acknowledgment.
4. Worker process crash (OOM, container restart, node termination) while holding a delivery lease.

Blindly retrying an email when the SMTP server accepted the message but the socket timed out causes duplicate emails (e.g. sending multiple payment receipts to a customer). Marking the email as failed when it actually delivered causes false audit logs.

## Decision
1. **Persistent Delivery Job Model**:
   - Every email dispatch is tracked by a record in `notification.delivery_jobs`.
   - Fields: `id`, `notification_id`, `channel`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `lease_token`, `lease_expires_at`, `provider_message_id`, `last_error_code`, `last_error_detail`, `created_at`, `updated_at`.
   - Allowed statuses: `Pending`, `Processing`, `Succeeded`, `RetryScheduled`, `Uncertain`, `Failed`, `Suppressed`.

2. **Lease-Based Worker Concurrency (`FOR UPDATE SKIP LOCKED`)**:
   - Delivery worker instances run as background hosted services (`EmailDeliveryWorker`).
   - Batches of pending jobs are claimed using atomic query:
     ```sql
     UPDATE notification.delivery_jobs
     SET status = 'Processing',
         lease_token = @WorkerInstanceId,
         lease_expires_at = NOW() + INTERVAL '5 minutes',
         attempt_count = attempt_count + 1,
         updated_at = NOW()
     WHERE id IN (
         SELECT id FROM notification.delivery_jobs
         WHERE status IN ('Pending', 'RetryScheduled')
           AND next_attempt_at <= NOW()
         ORDER BY next_attempt_at ASC, id ASC
         LIMIT @BatchSize
         FOR UPDATE SKIP LOCKED
     )
     RETURNING *;
     ```
   - Database transactions are strictly committed or released BEFORE opening the SMTP socket connection. Database transactions MUST NEVER remain open during SMTP network I/O.

3. **Classification of Delivery Outcomes**:
   - **Definitive Success (`Succeeded`)**: Provider returned `250 OK` with provider message ID. Job marked `Succeeded`, `sent_at = NOW()`.
   - **Deterministic Non-Retryable Failure (`Failed`)**: Permanent rejection (e.g. `550 User unknown`, `553 Invalid recipient`, template syntax error, recipient inactive in Auth). Job marked `Failed` without further retries.
   - **Transient Retryable Failure (`RetryScheduled`)**: Connection refused, DNS lookup failure before connection, `421 Service not available`, rate limiting (`451 Too many requests`). Job scheduled for retry with exponential backoff and jitter up to `max_attempts = 5`.
   - **Uncertain Outcome (`Uncertain`)**: Network disconnect, socket read timeout, or TLS reset occurring AFTER the `DATA` payload has been written to the socket.
     - The worker CANNOT determine if the mail was queued by the MTA or dropped.
     - The job status transitions to `Uncertain`.
     - **BLIND RETRY IS STRICTLY PROHIBITED** for `Uncertain` jobs.
     - An alert is emitted to structured logging with correlation IDs for administrative inspection and provider log reconciliation.

4. **Crash Recovery & Lease Expiry Worker**:
   - A dedicated reconciliation loop (`EmailLeaseReconciliationWorker`) periodically scans for jobs stuck in `Processing` with `lease_expires_at < NOW()`.
   - If a job was in `Processing` and the crash occurred before network dispatch (tracked via local execution stage audit or short lease threshold), it is transitioned back to `RetryScheduled` (if attempts < max_attempts) or `Uncertain`.
   - Stale leases are safely recovered without human intervention while preserving the safety invariant of zero accidental duplicate sends.

5. **Sandbox Mode for Dev/Staging**:
   - In non-production environments, an SMTP sink/sandbox (e.g. Mailhog, Mailpit, or NullEmailSender) is used to prevent accidental real-world email dispatch.

## Consequences
### Positive
- Strict prevention of duplicate transactional emails.
- Worker crash tolerance: unreleased locks expire automatically and are recovered safely.
- No database connection starvation during slow SMTP transmissions.

### Trade-offs / Risks
- `Uncertain` status requires operational awareness or manual/automated log reconciliation if provider does not support message inquiry APIs.
