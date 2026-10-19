-- ============================================================================
-- DPRENTAL RecommendationService - Phase 1 Reference DDL & Projection Scripts
-- File: docs/contracts/recommendation-v1.sql
-- Status: Frozen Architecture & Integration Contract Reference
-- Note: Reference DDL for PostgreSQL 17. Not an EF migration applied yet.
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
-- 5. Indexes
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
-- 6. Atomic Upsert & Ingestion Pattern (Reference SQL Transaction)
-- ============================================================================
-- Each incoming event is ingested inside a single ACID transaction:
-- 1. Check/Insert into interaction_events.
--    - If message_id does not exist: insert and proceed to projection upsert.
--    - If message_id exists with identical envelope: no-op (idempotent duplicate).
--    - If message_id exists with differing envelope: raise conflict error.
-- 2. Atomic upsert to user_equipment_type_interest (if user_id & equipment_type_id present).
-- 3. Atomic upsert to listing_popularity (if equipment_listing_id present).
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION recommendation.process_interaction_event(
    p_envelope JSONB
) RETURNS TEXT AS $$
DECLARE
    v_message_id            UUID;
    v_message_type          VARCHAR(64);
    v_version               INTEGER;
    v_producer              VARCHAR(64);
    v_occurred_at           TIMESTAMPTZ;
    v_correlation_id        TEXT;
    v_payload               JSONB;
    v_user_id               UUID;
    v_equipment_listing_id  UUID;
    v_equipment_type_id     UUID;
    v_score_weight          SMALLINT;
    v_inserted_id           UUID;
    v_existing_envelope     JSONB;
BEGIN
    -- 1. Extract envelope fields
    v_message_id     := (p_envelope->>'messageId')::UUID;
    v_message_type   := p_envelope->>'messageType';
    v_version        := (p_envelope->>'version')::INTEGER;
    v_producer       := p_envelope->>'producer';
    v_occurred_at    := (p_envelope->>'occurredAt')::TIMESTAMPTZ;
    v_correlation_id := p_envelope->>'correlationId';
    v_payload        := p_envelope->'payload';

    -- Extract payload entities (handling nulls explicitly)
    IF v_payload IS NOT NULL THEN
        v_user_id := CASE
            WHEN v_payload->>'userId' IS NOT NULL AND v_payload->>'userId' <> ''
            THEN (v_payload->>'userId')::UUID
            ELSE NULL
        END;

        v_equipment_listing_id := CASE
            WHEN v_payload->>'equipmentListingId' IS NOT NULL AND v_payload->>'equipmentListingId' <> ''
            THEN (v_payload->>'equipmentListingId')::UUID
            ELSE NULL
        END;

        v_equipment_type_id := CASE
            WHEN v_payload->>'equipmentTypeId' IS NOT NULL AND v_payload->>'equipmentTypeId' <> ''
            THEN (v_payload->>'equipmentTypeId')::UUID
            ELSE NULL
        END;
    END IF;

    -- Determine score weight strictly by event type
    CASE v_message_type
        WHEN 'SearchPerformed'     THEN v_score_weight := 1;
        WHEN 'ListingViewed'       THEN v_score_weight := 2;
        WHEN 'CartItemAdded'       THEN v_score_weight := 5;
        WHEN 'RentalItemPaid'      THEN v_score_weight := 8;
        WHEN 'RentalItemCompleted' THEN v_score_weight := 10;
        ELSE
            RAISE EXCEPTION 'UNKNOWN_MESSAGE_TYPE: %', v_message_type
                USING ERRCODE = 'data_exception';
    END CASE;

    -- 2. Concurrency-safe Insertion & Deduplication under READ COMMITTED
    -- Atomic INSERT ON CONFLICT DO NOTHING RETURNING handles two concurrent transactions safely.
    INSERT INTO recommendation.interaction_events (
        message_id,
        message_type,
        version,
        producer,
        occurred_at,
        received_at,
        correlation_id,
        user_id,
        equipment_listing_id,
        equipment_type_id,
        score_weight,
        envelope
    ) VALUES (
        v_message_id,
        v_message_type,
        v_version,
        v_producer,
        v_occurred_at,
        clock_timestamp(),
        v_correlation_id,
        v_user_id,
        v_equipment_listing_id,
        v_equipment_type_id,
        v_score_weight,
        p_envelope
    )
    ON CONFLICT (message_id) DO NOTHING
    RETURNING message_id INTO v_inserted_id;

    -- If row was not inserted, record already exists. Check duplicate vs conflict.
    IF v_inserted_id IS NULL THEN
        SELECT envelope INTO v_existing_envelope
        FROM recommendation.interaction_events
        WHERE message_id = v_message_id;

        IF v_existing_envelope = p_envelope THEN
            RETURN 'DUPLICATE_IGNORED';
        ELSE
            RAISE EXCEPTION 'CONFLICT_PAYLOAD_MISMATCH: Message ID % already exists with differing payload', v_message_id
                USING ERRCODE = 'unique_violation';
        END IF;
    END IF;

    -- 3. Only the transaction that successfully inserted updates projections
    -- Atomic upsert to User Interest (only if both user_id & equipment_type_id are present)
    IF v_user_id IS NOT NULL AND v_equipment_type_id IS NOT NULL THEN
        INSERT INTO recommendation.user_equipment_type_interest (
            user_id,
            equipment_type_id,
            score,
            last_occurred_at,
            updated_at
        ) VALUES (
            v_user_id,
            v_equipment_type_id,
            v_score_weight,
            v_occurred_at,
            clock_timestamp()
        )
        ON CONFLICT (user_id, equipment_type_id) DO UPDATE SET
            score = recommendation.user_equipment_type_interest.score + EXCLUDED.score,
            last_occurred_at = GREATEST(recommendation.user_equipment_type_interest.last_occurred_at, EXCLUDED.last_occurred_at),
            updated_at = clock_timestamp();
    END IF;

    -- Atomic upsert to Listing Popularity (for any event with equipment_listing_id)
    IF v_equipment_listing_id IS NOT NULL THEN
        INSERT INTO recommendation.listing_popularity (
            equipment_listing_id,
            score,
            last_occurred_at,
            updated_at
        ) VALUES (
            v_equipment_listing_id,
            v_score_weight,
            v_occurred_at,
            clock_timestamp()
        )
        ON CONFLICT (equipment_listing_id) DO UPDATE SET
            score = recommendation.listing_popularity.score + EXCLUDED.score,
            last_occurred_at = GREATEST(recommendation.listing_popularity.last_occurred_at, EXCLUDED.last_occurred_at),
            updated_at = clock_timestamp();
    END IF;

    RETURN 'PROCESSED';
END;
$$ LANGUAGE plpgsql;


-- ============================================================================
-- 7. Full Rebuild Query
-- Recomputes both projections from raw interaction_events history.
-- Coordinates with running ingestion by acquiring a SHARE lock in a single transaction.
-- ============================================================================

CREATE OR REPLACE PROCEDURE recommendation.rebuild_projections()
AS $$
BEGIN
    -- Set lock timeout to 5 seconds to prevent indefinite blocking
    SET LOCAL lock_timeout = '5s';

    -- Acquire SHARE lock on interaction_events: prevents concurrent ingestion writes
    -- during the rebuild window while allowing read queries.
    LOCK TABLE recommendation.interaction_events IN SHARE MODE;

    -- Rebuild User Interest projection
    TRUNCATE recommendation.user_equipment_type_interest;

    INSERT INTO recommendation.user_equipment_type_interest (
        user_id,
        equipment_type_id,
        score,
        last_occurred_at,
        updated_at
    )
    SELECT
        user_id,
        equipment_type_id,
        SUM(score_weight)::BIGINT AS score,
        MAX(occurred_at) AS last_occurred_at,
        clock_timestamp() AS updated_at
    FROM recommendation.interaction_events
    WHERE user_id IS NOT NULL
      AND equipment_type_id IS NOT NULL
    GROUP BY user_id, equipment_type_id;

    -- Rebuild Global Listing Popularity projection
    TRUNCATE recommendation.listing_popularity;

    INSERT INTO recommendation.listing_popularity (
        equipment_listing_id,
        score,
        last_occurred_at,
        updated_at
    )
    SELECT
        equipment_listing_id,
        SUM(score_weight)::BIGINT AS score,
        MAX(occurred_at) AS last_occurred_at,
        clock_timestamp() AS updated_at
    FROM recommendation.interaction_events
    WHERE equipment_listing_id IS NOT NULL
    GROUP BY equipment_listing_id;
END;
$$ LANGUAGE plpgsql;


-- ============================================================================
-- 8. Projection Delta Demonstration (Using Contract Fixtures)
-- ============================================================================
/*
  Step 1: Anonymous ListingViewed (+2 Popularity, +0 User Interest)
  SELECT recommendation.process_interaction_event('{
    "messageId": "11111111-1111-4111-8111-111111111112",
    "messageType": "ListingViewed",
    "version": 1,
    "producer": "InventoryService",
    "occurredAt": "2026-10-19T10:01:00Z",
    "correlationId": null,
    "payload": {
      "userId": null,
      "equipmentListingId": "33333333-3333-4333-8333-333333333333",
      "equipmentTypeId": "44444444-4444-4444-8444-444444444444"
    }
  }'::jsonb);

  Step 2: Authenticated SearchPerformed with Type (+1 User Interest, +0 Popularity)
  SELECT recommendation.process_interaction_event('{
    "messageId": "11111111-1111-4111-8111-111111111121",
    "messageType": "SearchPerformed",
    "version": 1,
    "producer": "InventoryService",
    "occurredAt": "2026-10-19T10:03:00Z",
    "correlationId": "corr-search-001",
    "payload": {
      "userId": "22222222-2222-4222-8222-222222222222",
      "equipmentTypeId": "44444444-4444-4444-8444-444444444444"
    }
  }'::jsonb);

  Step 3: CartItemAdded (+5 User Interest, +5 Popularity)
  SELECT recommendation.process_interaction_event('{
    "messageId": "11111111-1111-4111-8111-111111111131",
    "messageType": "CartItemAdded",
    "version": 1,
    "producer": "RentalService",
    "occurredAt": "2026-10-19T10:10:00Z",
    "correlationId": "corr-cart-001",
    "payload": {
      "userId": "22222222-2222-4222-8222-222222222222",
      "equipmentListingId": "33333333-3333-4333-8333-333333333333",
      "equipmentTypeId": "44444444-4444-4444-8444-444444444444",
      "cartId": "55555555-5555-4555-8555-555555555555",
      "cartItemId": "66666666-6666-4666-8666-666666666666"
    }
  }'::jsonb);

  Step 4: RentalItemPaid (+8 User Interest, +8 Popularity)
  SELECT recommendation.process_interaction_event('{
    "messageId": "11111111-1111-4111-8111-111111111141",
    "messageType": "RentalItemPaid",
    "version": 1,
    "producer": "RentalService",
    "occurredAt": "2026-10-19T10:20:00Z",
    "correlationId": "corr-order-001",
    "payload": {
      "userId": "22222222-2222-4222-8222-222222222222",
      "equipmentListingId": "33333333-3333-4333-8333-333333333333",
      "equipmentTypeId": "44444444-4444-4444-8444-444444444444",
      "rentalOrderId": "77777777-7777-4777-8777-777777777777",
      "rentalItemId": "88888888-8888-4888-8888-888888888888"
    }
  }'::jsonb);

  Step 5: RentalItemCompleted (+10 User Interest, +10 Popularity)
  SELECT recommendation.process_interaction_event('{
    "messageId": "11111111-1111-4111-8111-111111111151",
    "messageType": "RentalItemCompleted",
    "version": 1,
    "producer": "RentalService",
    "occurredAt": "2026-10-19T10:30:00Z",
    "correlationId": "corr-complete-001",
    "payload": {
      "userId": "22222222-2222-4222-8222-222222222222",
      "equipmentListingId": "33333333-3333-4333-8333-333333333333",
      "equipmentTypeId": "44444444-4444-4444-8444-444444444444",
      "rentalOrderId": "77777777-7777-4777-8777-777777777777",
      "rentalItemId": "88888888-8888-4888-8888-888888888888"
    }
  }'::jsonb);

  Step 6: Duplicate delivery of Step 5 -> DUPLICATE_IGNORED (scores unchanged)
  Step 7: Verification of Full Rebuild -> CALL recommendation.rebuild_projections();
*/
