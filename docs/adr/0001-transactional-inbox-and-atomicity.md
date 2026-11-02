# ADR-0001: Transactional Inbox and Notification Processing Atomicity

## Status
Accepted

## Context
NotificationService is an event-driven microservice in the DPRental ecosystem. It consumes business events published by `RentalService` on Kafka topic `rental.events.v1` and translates them into in-app notifications, realtime inbox invalidation signals, and transactional email delivery jobs.

Kafka provides at-least-once message delivery semantics across network boundaries. Duplicate messages, consumer group rebalances, worker crashes, and network partitions are expected failure modes. When consuming a message:
1. NotificationService must avoid creating duplicate user notifications or duplicate email delivery jobs upon message replay.
2. The storage of the inbox receipt, the creation of user notification records, the updating of aggregate watermarks, and the enqueuing of email delivery jobs must either all succeed or all fail together.
3. Kafka offset commits are asynchronous with respect to database transactions and can fail even after the database transaction has committed.

## Decision
1. **Single PostgreSQL Transaction**:
   Every consumed Kafka message is processed within a single PostgreSQL transaction covering four tables:
   - `notification.inbox_receipts`: stores message identity `(consumer_name, message_id, producer, message_type, status, processed_at)`.
   - `notification.notifications`: stores the rendered in-app notifications.
   - `notification.aggregate_watermarks`: updates the highest observed business version (`order_version`) for the aggregate.
   - `notification.delivery_jobs`: enqueues pending channel deliveries (e.g. email) with status `Pending`.

2. **Deduplication Key & Unique Constraints**:
   - `notification.inbox_receipts` enforces a `UNIQUE (consumer_name, message_id)` constraint.
   - `notification.notifications` enforces a `UNIQUE (source_event_id, recipient_user_id, notification_type)` constraint to prevent cross-consumer duplicate notification insertion.
   - `notification.delivery_jobs` enforces a `UNIQUE (notification_id, channel)` constraint.

3. **Two-Phase Commit Sequence**:
   - **Step 1 (Pre-check)**: Query `notification.inbox_receipts` for `(consumer_name, message_id)`. If present with status `Processed` or `Ignored`, immediately acknowledge (commit offset) and exit without side-effects.
   - **Step 2 (Business Processing)**: Execute recipient resolution, template rendering, and watermark checks.
   - **Step 3 (Atomic Commit)**: Insert inbox receipt, notification records, and delivery jobs within `BeginTransactionAsync`. Commit the transaction.
   - **Step 4 (Kafka Offset Commit)**: Synchronously commit the Kafka offset only after the database transaction commits successfully.
   - **Step 5 (Post-commit Dispatch)**: Trigger out-of-band realtime invalidation (`NotificationInboxChanged` via SignalR) to active connections.

4. **Failure & Replay Handling**:
   - If the database transaction fails (serialization failure, transient DB error), the transaction is rolled back, the Kafka offset is NOT committed, and the consumer retries with backoff.
   - If the database commits but the Kafka offset commit fails (network timeout, broker partition rebalance), the message will be re-delivered by Kafka. On re-delivery, the inbox receipt check (or the unique constraints on conflict) detects the duplicate and skips re-execution safely.
   - Poison messages (malformed JSON, contract violation, untrusted producer) that cannot be parsed or validated are routed to the durable Quarantine / Dead-Letter Queue (DLQ) after recording a quarantine receipt; they are never retried infinitely.

## Consequences
### Positive
- Strict idempotency: Replaying Kafka messages never generates duplicate in-app notifications or duplicate email dispatches.
- Atomic state: No orphaned notification records without inbox receipts, and no orphaned delivery jobs without parent notifications.
- Decoupled delivery: Network failures in SMTP or SignalR cannot abort or roll back the Kafka ingestion transaction.

### Trade-offs / Risks
- Ingestion latency includes PostgreSQL transaction commit time.
- Requires database connection pool sizing sufficient for consumer throughput.
