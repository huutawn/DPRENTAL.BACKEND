-- ============================================================================
-- DPRENTAL RecommendationService - Phase 1 Official Database Schema
-- File: docs/contracts/recommendation-v1.sql
-- Source of Truth: EF Core Migration (20261019043638_InitialRecommendationSchema)
-- Target Engine: PostgreSQL 17
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. Schema Definition
-- ----------------------------------------------------------------------------
CREATE SCHEMA IF NOT EXISTS recommendation;

-- ----------------------------------------------------------------------------
-- 2. Table: recommendation.interaction_events
-- Stores raw behavioral events for deduplication, audit, and projection replay.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS recommendation.interaction_events (
    message_id              UUID                        NOT NULL,
    message_type            VARCHAR(64)                 NOT NULL,
    version                 INTEGER                     NOT NULL,
    producer                VARCHAR(64)                 NOT NULL,
    occurred_at             TIMESTAMPTZ                 NOT NULL,
    received_at             TIMESTAMPTZ                 NOT NULL DEFAULT clock_timestamp(),
    correlation_id          TEXT                        NULL,
    user_id                 UUID                        NULL,
    equipment_listing_id    UUID                        NULL,
    equipment_type_id       UUID                        NULL,
    score_weight            SMALLINT                    NOT NULL,
    envelope                JSONB                       NOT NULL,

    -- Primary Key
    CONSTRAINT pk_interaction_events PRIMARY KEY (message_id),

    -- Non-nil UUID checks
    CONSTRAINT chk_interaction_events_message_id_not_nil
        CHECK (message_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_interaction_events_user_id_not_nil
        CHECK (user_id IS NULL OR user_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_interaction_events_listing_id_not_nil
        CHECK (equipment_listing_id IS NULL OR equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_interaction_events_type_id_not_nil
        CHECK (equipment_type_id IS NULL OR equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid),

    -- Envelope protocol checks
    CONSTRAINT chk_interaction_events_version
        CHECK (version = 1),
    CONSTRAINT chk_interaction_events_producer
        CHECK (producer IN ('InventoryService', 'RentalService')),
    CONSTRAINT chk_interaction_events_score_weight_positive
        CHECK (score_weight > 0),

    -- Semantic validation per event type and weight mapping
    CONSTRAINT chk_interaction_events_semantics
        CHECK (
            (
                message_type = 'ListingViewed'
                AND producer = 'InventoryService'
                AND score_weight = 2
                AND equipment_listing_id IS NOT NULL
                AND equipment_type_id IS NOT NULL
            )
            OR
            (
                message_type = 'SearchPerformed'
                AND producer = 'InventoryService'
                AND score_weight = 1
                AND equipment_listing_id IS NULL
            )
            OR
            (
                message_type = 'CartItemAdded'
                AND producer = 'RentalService'
                AND score_weight = 5
                AND user_id IS NOT NULL
                AND equipment_listing_id IS NOT NULL
            )
            OR
            (
                message_type = 'RentalItemPaid'
                AND producer = 'RentalService'
                AND score_weight = 8
                AND user_id IS NOT NULL
                AND equipment_listing_id IS NOT NULL
            )
            OR
            (
                message_type = 'RentalItemCompleted'
                AND producer = 'RentalService'
                AND score_weight = 10
                AND user_id IS NOT NULL
                AND equipment_listing_id IS NOT NULL
            )
        )
);

-- ----------------------------------------------------------------------------
-- 3. Table: recommendation.user_equipment_type_interest
-- Materialized projection of user interest per equipment type.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS recommendation.user_equipment_type_interest (
    user_id                 UUID                        NOT NULL,
    equipment_type_id       UUID                        NOT NULL,
    score                   BIGINT                      NOT NULL,
    last_occurred_at        TIMESTAMPTZ                 NOT NULL,
    updated_at              TIMESTAMPTZ                 NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT pk_user_equipment_type_interest PRIMARY KEY (user_id, equipment_type_id),
    CONSTRAINT chk_user_interest_user_id_not_nil
        CHECK (user_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_user_interest_type_id_not_nil
        CHECK (equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_user_interest_score_positive
        CHECK (score > 0)
);

-- ----------------------------------------------------------------------------
-- 4. Table: recommendation.listing_popularity
-- Materialized projection of global listing popularity.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS recommendation.listing_popularity (
    equipment_listing_id    UUID                        NOT NULL,
    score                   BIGINT                      NOT NULL,
    last_occurred_at        TIMESTAMPTZ                 NOT NULL,
    updated_at              TIMESTAMPTZ                 NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT pk_listing_popularity PRIMARY KEY (equipment_listing_id),
    CONSTRAINT chk_listing_popularity_id_not_nil
        CHECK (equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT chk_listing_popularity_score_positive
        CHECK (score > 0)
);

-- ----------------------------------------------------------------------------
-- 5. Indexes (Generated by EF Migration)
-- ----------------------------------------------------------------------------

-- History and auditing queries
CREATE INDEX IF NOT EXISTS idx_interaction_events_occurred_at
    ON recommendation.interaction_events (occurred_at DESC);

CREATE INDEX IF NOT EXISTS idx_interaction_events_user_occurred
    ON recommendation.interaction_events (user_id, occurred_at DESC)
    WHERE user_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_interaction_events_listing_occurred
    ON recommendation.interaction_events (equipment_listing_id, occurred_at DESC)
    WHERE equipment_listing_id IS NOT NULL;

-- Ranking queries (score DESC, tie-break UUID ASC)
CREATE INDEX IF NOT EXISTS idx_user_interest_ranking
    ON recommendation.user_equipment_type_interest (user_id, score DESC, equipment_type_id ASC);

CREATE INDEX IF NOT EXISTS idx_listing_popularity_ranking
    ON recommendation.listing_popularity (score DESC, equipment_listing_id ASC);

-- Cache refresh and change tracking
CREATE INDEX IF NOT EXISTS idx_user_interest_updated_at
    ON recommendation.user_equipment_type_interest (updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_listing_popularity_updated_at
    ON recommendation.listing_popularity (updated_at DESC);


-- ============================================================================
-- 6. Application Layer Ingestion & Persistence Flow (Official Implementation)
-- ============================================================================
-- In accordance with Single Source of Truth, all ingestion orchestration,
-- schema validation, score calculation, and idempotency logic are owned by
-- RecommendationService.Application (InteractionIngestionService) and persisted
-- via Domain repository abstractions (InteractionEventRepository,
-- UserEquipmentTypeInterestRepository, ListingPopularityRepository, InteractionUnitOfWork).
--
-- Flow per incoming event inside an explicit READ COMMITTED database transaction:
--
-- Step 6.1: Concurrency-safe Raw Event Ingestion
--   INSERT INTO recommendation.interaction_events (
--       message_id, message_type, version, producer, occurred_at, received_at,
--       correlation_id, user_id, equipment_listing_id, equipment_type_id,
--       score_weight, envelope
--   ) VALUES (
--       @messageId, @messageType, @version, @producer, @occurredAt, clock_timestamp(),
--       @correlationId, @userId, @equipmentListingId, @equipmentTypeId,
--       @scoreWeight, @envelope::jsonb
--   )
--   ON CONFLICT (message_id) DO NOTHING
--   RETURNING message_id;
--
-- Step 6.2: Deduplication vs Conflict Evaluation
--   If RETURNING message_id is NULL (row already exists):
--     SELECT (envelope = @envelope::jsonb) AS is_identical
--     FROM recommendation.interaction_events
--     WHERE message_id = @messageId;
--
--     - If is_identical = TRUE: Event is an idempotent duplicate.
--       Projections are NOT updated. Returns DUPLICATE_IGNORED and commits offset.
--     - If is_identical = FALSE: Event is a payload conflict.
--       Returns CONFLICT_PAYLOAD_MISMATCH, transaction commits or aborts, and consumer
--       routes original record to Dead Letter Queue (DLQ) with x-dlq-error-reason.
--
-- Step 6.3: Atomic Projection Updates (Only executed if Step 6.1 inserted a new row)
--   A. User Interest Projection (only when both userId and equipmentTypeId are present):
--      INSERT INTO recommendation.user_equipment_type_interest (
--          user_id, equipment_type_id, score, last_occurred_at, updated_at
--      ) VALUES (
--          @userId, @equipmentTypeId, @scoreWeight, @occurredAt, clock_timestamp()
--      )
--      ON CONFLICT (user_id, equipment_type_id) DO UPDATE SET
--          score = recommendation.user_equipment_type_interest.score + EXCLUDED.score,
--          last_occurred_at = GREATEST(recommendation.user_equipment_type_interest.last_occurred_at, EXCLUDED.last_occurred_at),
--          updated_at = clock_timestamp();
--
--   B. Listing Popularity Projection (for every event with equipmentListingId):
--      INSERT INTO recommendation.listing_popularity (
--          equipment_listing_id, score, last_occurred_at, updated_at
--      ) VALUES (
--          @equipmentListingId, @scoreWeight, @occurredAt, clock_timestamp()
--      )
--      ON CONFLICT (equipment_listing_id) DO UPDATE SET
--          score = recommendation.listing_popularity.score + EXCLUDED.score,
--          last_occurred_at = GREATEST(recommendation.listing_popularity.last_occurred_at, EXCLUDED.last_occurred_at),
--          updated_at = clock_timestamp();
--
-- Step 6.4: Projection Rebuild Procedure (Executed via CLI --rebuild-projections)
--   Inside a single transaction:
--     SET LOCAL lock_timeout = '5s';
--     LOCK TABLE recommendation.interaction_events IN SHARE MODE;
--
--     TRUNCATE recommendation.user_equipment_type_interest;
--     INSERT INTO recommendation.user_equipment_type_interest (
--         user_id, equipment_type_id, score, last_occurred_at, updated_at
--     )
--     SELECT
--         user_id,
--         equipment_type_id,
--         SUM(score_weight)::BIGINT AS score,
--         MAX(occurred_at) AS last_occurred_at,
--         clock_timestamp() AS updated_at
--     FROM recommendation.interaction_events
--     WHERE user_id IS NOT NULL AND equipment_type_id IS NOT NULL
--     GROUP BY user_id, equipment_type_id;
--
--     TRUNCATE recommendation.listing_popularity;
--     INSERT INTO recommendation.listing_popularity (
--         equipment_listing_id, score, last_occurred_at, updated_at
--     )
--     SELECT
--         equipment_listing_id,
--         SUM(score_weight)::BIGINT AS score,
--         MAX(occurred_at) AS last_occurred_at,
--         clock_timestamp() AS updated_at
--     FROM recommendation.interaction_events
--     WHERE equipment_listing_id IS NOT NULL
--     GROUP BY equipment_listing_id;
