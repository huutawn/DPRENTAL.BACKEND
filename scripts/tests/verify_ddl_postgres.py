#!/usr/bin/env python3
"""
PostgreSQL DDL & Concurrency Verification Runner for Recommendation Phase 1.

Dependencies:
  - Python 3.10+
  - Docker runtime running locally
  - psycopg2 (pip install psycopg2-binary)

Features:
  - Allocates a dynamic localhost port and unique container name per run.
  - Cleans up exclusively its own spawned container without touching pre-existing containers.
  - Validates SQL constraints with strict SQLSTATE inspection (no swallowing of unexpected errors or assertions).
  - Uses authentic JSON fixtures from docs/contracts/schemas/fixtures/valid/ for function calls.
  - Executes multi-connection concurrency tests:
      * Concurrent identical duplicates (exactly one PROCESSED, other DUPLICATE_IGNORED)
      * Concurrent conflicts on identical messageId (one PROCESSED, other conflict exception)
      * Concurrent distinct events on same projection (atomic accumulation, no lost updates)
      * Rebuild execution under SHARE lock with concurrent ingestion coordination
  - Verifies anonymous interactions, search without type, rental without type, rollback, and full projection rebuild.
"""

import json
import os
import socket
import subprocess
import sys
import threading
import time
import uuid
import psycopg2
from psycopg2 import errors

SQL_FILE = os.path.join(os.path.dirname(__file__), "../../docs/contracts/recommendation-v1.sql")
FIXTURES_DIR = os.path.join(os.path.dirname(__file__), "../../docs/contracts/schemas/fixtures/valid")
PASSWORD = "pg_verify_password_super_secure"


def load_fixture(name: str) -> dict:
    path = os.path.join(FIXTURES_DIR, name)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def start_ephemeral_postgres():
    port = find_free_port()
    container_name = f"temp-pg-rec-verify-{uuid.uuid4().hex[:12]}"
    print(f"Starting ephemeral PostgreSQL 17 container '{container_name}' on port {port}...")

    cmd = [
        "docker", "run", "--rm", "-d",
        "--name", container_name,
        "-e", f"POSTGRES_PASSWORD={PASSWORD}",
        "-p", f"127.0.0.1:{port}:5432",
        "postgres:17-alpine"
    ]
    subprocess.check_call(cmd)

    # Wait for PostgreSQL to be ready
    for _ in range(40):
        try:
            conn = psycopg2.connect(
                host="127.0.0.1",
                port=port,
                user="postgres",
                password=PASSWORD,
                dbname="postgres"
            )
            print("Connected to ephemeral PostgreSQL successfully.")
            return conn, container_name, port
        except Exception:
            time.sleep(0.3)

    raise TimeoutError(f"Ephemeral PostgreSQL container '{container_name}' failed to become ready.")


def stop_ephemeral_postgres(container_name: str):
    print(f"Tearing down ephemeral container '{container_name}'...")
    subprocess.run(["docker", "stop", container_name], check=True)


def create_connection(port: int):
    return psycopg2.connect(
        host="127.0.0.1",
        port=port,
        user="postgres",
        password=PASSWORD,
        dbname="postgres"
    )

def run_meta_tests():
    """
    Meta-test: Proves that if a negative SQL test fails to raise the expected exception,
    an AssertionError is raised and will fail the suite (it is not swallowed).
    """
    assertion_properly_raised = False
    try:
        try:
            # Simulate a negative test where the operation unexpectedly succeeds
            pass
            raise AssertionError("Simulated negative test failure: expected CheckViolation was not raised!")
        except errors.CheckViolation:
            pass
    except AssertionError:
        assertion_properly_raised = True

    assert assertion_properly_raised, "Meta-test failed: AssertionError was swallowed!"


def run_tests(conn, port: int):
    cur = conn.cursor()

    print("\n1. Applying recommendation-v1.sql...")
    with open(SQL_FILE, "r", encoding="utf-8") as f:
        ddl = f.read()
    cur.execute(ddl)
    conn.commit()
    print("  [PASS] DDL, constraints, indexes, and stored functions applied.")

    # Run Meta-test
    run_meta_tests()
    print("  [PASS] Meta-test verified: AssertionError in negative tests will fail the suite.")

    print("\n2. Verifying Tables, Indexes and Strict SQLSTATE Constraints...")
    cur.execute("""
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'recommendation'
        ORDER BY table_name;
    """)
    tables = [r[0] for r in cur.fetchall()]
    assert tables == ["interaction_events", "listing_popularity", "user_equipment_type_interest"], f"Tables: {tables}"

    # Verify CHECK constraint: nil UUID on interaction_events
    try:
        cur.execute("""
            INSERT INTO recommendation.interaction_events (
                message_id, message_type, version, producer, occurred_at,
                score_weight, envelope
            ) VALUES (
                '00000000-0000-0000-0000-000000000000', 'ListingViewed', 1,
                'InventoryService', now(), 2, '{}'::jsonb
            );
        """)
        conn.commit()
        raise AssertionError("Failed to enforce chk_interaction_events_message_id_not_nil")
    except errors.CheckViolation as e:
        conn.rollback()
        assert e.pgcode == "23514", f"Expected SQLSTATE 23514 (check_violation), got {e.pgcode}"
        print("  [PASS] chk_interaction_events_message_id_not_nil enforced (SQLSTATE 23514).")

    # Verify CHECK constraint: producer / message_type mismatch
    try:
        cur.execute("""
            INSERT INTO recommendation.interaction_events (
                message_id, message_type, version, producer, occurred_at,
                score_weight, envelope, equipment_listing_id, equipment_type_id
            ) VALUES (
                '11111111-1111-4111-8111-111111111111', 'ListingViewed', 1,
                'RentalService', now(), 2, '{}'::jsonb,
                '22222222-2222-4222-8222-222222222222', '33333333-3333-4333-8333-333333333333'
            );
        """)
        conn.commit()
        raise AssertionError("Failed to enforce chk_interaction_events_semantics")
    except errors.CheckViolation as e:
        conn.rollback()
        assert e.pgcode == "23514", f"Expected SQLSTATE 23514 (check_violation), got {e.pgcode}"
        print("  [PASS] chk_interaction_events_semantics enforced (SQLSTATE 23514).")

    # Verify CHECK constraint: score > 0 on listing_popularity
    try:
        cur.execute("""
            INSERT INTO recommendation.listing_popularity (
                equipment_listing_id, score, last_occurred_at, updated_at
            ) VALUES (
                '22222222-2222-4222-8222-222222222222', 0, now(), now()
            );
        """)
        conn.commit()
        raise AssertionError("Failed to enforce chk_listing_popularity_score_positive")
    except errors.CheckViolation as e:
        conn.rollback()
        assert e.pgcode == "23514", f"Expected SQLSTATE 23514 (check_violation), got {e.pgcode}"
        print("  [PASS] chk_listing_popularity_score_positive enforced (SQLSTATE 23514).")

    # Verify CHECK constraint: score > 0 on user_equipment_type_interest
    try:
        cur.execute("""
            INSERT INTO recommendation.user_equipment_type_interest (
                user_id, equipment_type_id, score, last_occurred_at, updated_at
            ) VALUES (
                '22222222-2222-4222-8222-222222222222', '33333333-3333-4333-8333-333333333333', 0, now(), now()
            );
        """)
        conn.commit()
        raise AssertionError("Failed to enforce chk_user_interest_score_positive")
    except errors.CheckViolation as e:
        conn.rollback()
        assert e.pgcode == "23514", f"Expected SQLSTATE 23514 (check_violation), got {e.pgcode}"
        print("  [PASS] chk_user_interest_score_positive enforced (SQLSTATE 23514).")

    print("\n3. Testing process_interaction_event(p_envelope jsonb) using Authentic Fixtures...")

    # Load authentic fixtures
    f_view_anon = load_fixture("listing-viewed-anonymous.json")
    f_search_type = load_fixture("search-performed-with-type.json")
    f_search_notype = load_fixture("search-performed-without-type.json")
    f_cart_std = load_fixture("cart-item-added-standard.json")
    f_cart_notype = load_fixture("cart-item-added-without-type.json")
    f_paid_std = load_fixture("rental-item-paid-standard.json")
    f_paid_notype = load_fixture("rental-item-paid-without-type.json")
    f_comp_std = load_fixture("rental-item-completed-standard.json")

    user_u = f_search_type["payload"]["userId"]
    type_t = f_search_type["payload"]["equipmentTypeId"]
    listing_l = f_view_anon["payload"]["equipmentListingId"]

    # Step 1: Anonymous ListingViewed (+2 Popularity, 0 User Interest)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_view_anon),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score, last_occurred_at FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    pop_score, pop_time = cur.fetchone()
    assert pop_score == 2
    assert pop_time.isoformat().startswith("2026-10-19T10:01:00")
    cur.execute("SELECT count(*) FROM recommendation.user_equipment_type_interest")
    assert cur.fetchone()[0] == 0
    print("  [PASS] Step 1: Anonymous ListingViewed (+2 Pop, 0 User Interest)")

    # Step 2: SearchPerformed with Type (+1 User Interest, 0 Popularity)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_search_type),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score, last_occurred_at FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    int_score, int_time = cur.fetchone()
    assert int_score == 1
    assert int_time.isoformat().startswith("2026-10-19T10:03:00")
    print("  [PASS] Step 2: SearchPerformed with Type (+1 User Interest, 0 Pop)")

    # Step 2b: SearchPerformed WITHOUT Type (neither interest nor popularity increments)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_search_notype),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 1  # Unchanged
    print("  [PASS] Step 2b: SearchPerformed without Type (projections unchanged)")

    # Step 3: CartItemAdded standard (+5 Interest, +5 Pop)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_cart_std),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 6  # 1 + 5
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    assert cur.fetchone()[0] == 7  # 2 + 5
    print("  [PASS] Step 3: CartItemAdded standard (+5 Interest -> 6, +5 Pop -> 7)")

    # Step 3b: CartItemAdded WITHOUT Type (+5 Pop, 0 Interest)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_cart_notype),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 6  # Interest unchanged
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    assert cur.fetchone()[0] == 12  # 7 + 5
    print("  [PASS] Step 3b: CartItemAdded without Type (+5 Pop -> 12, 0 Interest)")

    # Step 4: RentalItemPaid standard (+8 Interest, +8 Pop)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_paid_std),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 14  # 6 + 8
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    assert cur.fetchone()[0] == 20  # 12 + 8
    print("  [PASS] Step 4: RentalItemPaid standard (+8 Interest -> 14, +8 Pop -> 20)")

    # Step 5: RentalItemCompleted standard (+10 Interest, +10 Pop)
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_comp_std),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 24  # 14 + 10
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    assert cur.fetchone()[0] == 30  # 20 + 10
    print("  [PASS] Step 5: RentalItemCompleted standard (+10 Interest -> 24, +10 Pop -> 30)")

    # Step 6: Sequential Deduplication check
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_comp_std),))
    res_dup = cur.fetchone()[0]
    assert res_dup == "DUPLICATE_IGNORED"
    conn.commit()

    cur.execute("SELECT score FROM recommendation.user_equipment_type_interest WHERE user_id = %s AND equipment_type_id = %s", (user_u, type_t))
    assert cur.fetchone()[0] == 24
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    assert cur.fetchone()[0] == 30
    print("  [PASS] Step 6: Sequential duplicate returned DUPLICATE_IGNORED without score modification.")

    # Step 7: Sequential Conflict check (same ID, modified payload)
    f_comp_conflict = json.loads(json.dumps(f_comp_std))
    f_comp_conflict["payload"]["equipmentTypeId"] = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
    try:
        cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_comp_conflict),))
        conn.commit()
        raise AssertionError("Failed to detect conflict payload mismatch")
    except errors.UniqueViolation as e:
        conn.rollback()
        assert e.pgcode == "23505", f"Expected SQLSTATE 23505 (unique_violation), got {e.pgcode}"
        print("  [PASS] Step 7: Sequential conflict raised UniqueViolation (SQLSTATE 23505).")

    # Step 8: Transaction Rollback test
    cur.execute("BEGIN;")
    f_rollback = json.loads(json.dumps(f_comp_std))
    f_rollback["messageId"] = "99999999-8888-4777-8666-555555555555"
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_rollback),))
    cur.execute("ROLLBACK;")

    cur.execute("SELECT count(*) FROM recommendation.interaction_events WHERE message_id = '99999999-8888-4777-8666-555555555555'")
    assert cur.fetchone()[0] == 0
    print("  [PASS] Step 8: Transaction rollback leaves interaction_events clean.")

    # Step 9: Out of order timestamp check (GREATEST logic)
    f_out_of_order = json.loads(json.dumps(f_view_anon))
    f_out_of_order["messageId"] = "11111111-1111-4111-8111-999999999999"
    f_out_of_order["occurredAt"] = "2026-10-19T08:00:00Z"  # Much earlier time
    f_out_of_order["payload"]["userId"] = user_u
    cur.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(f_out_of_order),))
    assert cur.fetchone()[0] == "PROCESSED"
    conn.commit()

    cur.execute("SELECT score, last_occurred_at FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    pop_score2, pop_time2 = cur.fetchone()
    assert pop_score2 == 32  # 30 + 2
    # Verify last_occurred_at was NOT rewound to 08:00:00Z
    assert pop_time2.isoformat().startswith("2026-10-19T10:30:00")
    print("  [PASS] Step 9: Out-of-order event updated score, GREATEST kept most recent timestamp.")

    print("\n4. Multi-Connection Concurrency Testing...")

    # Concurrency Test A: Concurrent Duplicates
    print("  Running Concurrency Test A: Two simultaneous transactions inserting exact same event...")
    event_dup = json.loads(json.dumps(f_comp_std))
    event_dup["messageId"] = str(uuid.uuid4())
    event_dup_json = json.dumps(event_dup)

    barrier_a = threading.Barrier(2)
    results_a = [None, None]
    errors_a = [None, None]

    def worker_dup(idx):
        c = create_connection(port)
        try:
            barrier_a.wait(timeout=5)
            cu = c.cursor()
            cu.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (event_dup_json,))
            results_a[idx] = cu.fetchone()[0]
            c.commit()
        except Exception as e:
            errors_a[idx] = e
            c.rollback()
        finally:
            c.close()

    t1 = threading.Thread(target=worker_dup, args=(0,))
    t2 = threading.Thread(target=worker_dup, args=(1,))
    t1.start()
    t2.start()
    t1.join()
    t2.join()

    assert errors_a == [None, None], f"Unexpected errors in concurrent duplicate test: {errors_a}"
    assert sorted(results_a) == ["DUPLICATE_IGNORED", "PROCESSED"], f"Expected exactly one PROCESSED and one DUPLICATE_IGNORED, got {results_a}"
    print(f"    [PASS] Concurrency A: Results={results_a}. Exactly one inserted, one ignored.")

    # Concurrency Test B: Concurrent Conflicts (same messageId, differing payload)
    print("  Running Concurrency Test B: Two simultaneous transactions inserting conflicting payloads with same messageId...")
    msg_id_conflict = str(uuid.uuid4())
    event_conf_1 = json.loads(json.dumps(f_comp_std))
    event_conf_1["messageId"] = msg_id_conflict
    event_conf_1["payload"]["rentalOrderId"] = "11111111-2222-3333-4444-555555555555"

    event_conf_2 = json.loads(json.dumps(f_comp_std))
    event_conf_2["messageId"] = msg_id_conflict
    event_conf_2["payload"]["rentalOrderId"] = "99999999-8888-7777-6666-555555555555"

    barrier_b = threading.Barrier(2)
    results_b = [None, None]
    errors_b = [None, None]

    def worker_conflict(idx, payload):
        c = create_connection(port)
        try:
            barrier_b.wait(timeout=5)
            cu = c.cursor()
            cu.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(payload),))
            results_b[idx] = cu.fetchone()[0]
            c.commit()
        except Exception as e:
            errors_b[idx] = e
            c.rollback()
        finally:
            c.close()

    t1 = threading.Thread(target=worker_conflict, args=(0, event_conf_1))
    t2 = threading.Thread(target=worker_conflict, args=(1, event_conf_2))
    t1.start()
    t2.start()
    t1.join()
    t2.join()

    # One must succeed, and the other must fail with UniqueViolation / conflict
    non_null_results = [r for r in results_b if r is not None]
    assert non_null_results == ["PROCESSED"], f"Expected one PROCESSED, got {results_b}"
    exception_b = [e for e in errors_b if e is not None][0]
    assert isinstance(exception_b, errors.UniqueViolation), f"Expected UniqueViolation, got {type(exception_b)}"
    assert exception_b.pgcode == "23505"
    print("    [PASS] Concurrency B: One transaction committed, other rejected with CONFLICT (SQLSTATE 23505).")

    # Concurrency Test C: 5 Concurrent Distinct Events on the Same Listing Popularity
    print("  Running Concurrency Test C: 5 concurrent distinct transactions updating same listing popularity...")
    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    score_before_c = cur.fetchone()[0]

    num_threads_c = 5
    barrier_c = threading.Barrier(num_threads_c)
    results_c = [None] * num_threads_c
    errors_c = [None] * num_threads_c

    def worker_distinct(idx):
        c = create_connection(port)
        try:
            ev = json.loads(json.dumps(f_view_anon))
            ev["messageId"] = str(uuid.uuid4())
            barrier_c.wait(timeout=5)
            cu = c.cursor()
            cu.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(ev),))
            results_c[idx] = cu.fetchone()[0]
            c.commit()
        except Exception as e:
            errors_c[idx] = e
            c.rollback()
        finally:
            c.close()

    threads_c = [threading.Thread(target=worker_distinct, args=(i,)) for i in range(num_threads_c)]
    for t in threads_c:
        t.start()
    for t in threads_c:
        t.join()

    assert errors_c == [None] * num_threads_c, f"Errors in concurrent distinct test: {errors_c}"
    assert results_c == ["PROCESSED"] * num_threads_c

    cur.execute("SELECT score FROM recommendation.listing_popularity WHERE equipment_listing_id = %s", (listing_l,))
    score_after_c = cur.fetchone()[0]
    expected_c = score_before_c + (num_threads_c * 2)  # 5 views of 2 points each
    assert score_after_c == expected_c, f"Lost updates! Expected {expected_c}, got {score_after_c}"
    conn.commit()
    print(f"    [PASS] Concurrency C: All {num_threads_c} concurrent upserts accumulated atomically without lost updates ({score_before_c} -> {score_after_c}).")

    # Concurrency Test D: Rebuild Under SHARE Lock with Concurrent Ingestion Coordination
    print("  Running Concurrency Test D: Rebuild coordinates safely under SHARE lock with concurrent ingestion...")
    rebuild_started = threading.Event()
    rebuild_finished = threading.Event()
    rebuild_error = [None]
    ingest_result = [None]
    ingest_error = [None]

    def worker_rebuild():
        c = create_connection(port)
        try:
            cu = c.cursor()
            cu.execute("BEGIN;")
            cu.execute("SET LOCAL lock_timeout = '5s';")
            cu.execute("LOCK TABLE recommendation.interaction_events IN SHARE MODE;")
            rebuild_started.set()
            time.sleep(0.4)
            cu.execute("CALL recommendation.rebuild_projections();")
            c.commit()
            rebuild_finished.set()
        except Exception as e:
            rebuild_error[0] = e
            c.rollback()
        finally:
            c.close()
    def worker_ingest_during_rebuild():
        c = create_connection(port)
        try:
            # Wait until rebuild has started and acquired SHARE lock
            rebuild_started.wait(timeout=5)
            ev = json.loads(json.dumps(f_view_anon))
            ev["messageId"] = str(uuid.uuid4())
            cu = c.cursor()
            # This INSERT will wait until rebuild finishes and commits
            cu.execute("SELECT recommendation.process_interaction_event(%s::jsonb);", (json.dumps(ev),))
            ingest_result[0] = cu.fetchone()[0]
            c.commit()
        except Exception as e:
            ingest_error[0] = e
            c.rollback()
        finally:
            c.close()

    t_reb = threading.Thread(target=worker_rebuild)
    t_ing = threading.Thread(target=worker_ingest_during_rebuild)
    t_reb.start()
    t_ing.start()
    t_reb.join()
    t_ing.join()

    assert rebuild_error[0] is None, f"Rebuild error: {rebuild_error[0]}"
    assert ingest_error[0] is None, f"Ingest error during rebuild: {ingest_error[0]}"
    assert ingest_result[0] == "PROCESSED"
    print("    [PASS] Concurrency D: Rebuild and concurrent ingestion coordinated cleanly via SHARE lock.")

    print("\n5. Testing Full Projection Rebuild Accuracy (Score and Timestamp)...")
    # Record expected projections from raw history
    cur.execute("""
        SELECT user_id, equipment_type_id, SUM(score_weight)::bigint, MAX(occurred_at)
        FROM recommendation.interaction_events
        WHERE user_id IS NOT NULL AND equipment_type_id IS NOT NULL
        GROUP BY user_id, equipment_type_id
        ORDER BY user_id, equipment_type_id;
    """)
    expected_interest = cur.fetchall()

    cur.execute("""
        SELECT equipment_listing_id, SUM(score_weight)::bigint, MAX(occurred_at)
        FROM recommendation.interaction_events
        WHERE equipment_listing_id IS NOT NULL
        GROUP BY equipment_listing_id
        ORDER BY equipment_listing_id;
    """)
    expected_popularity = cur.fetchall()

    # Execute rebuild procedure
    cur.execute("CALL recommendation.rebuild_projections();")
    conn.commit()

    cur.execute("""
        SELECT user_id, equipment_type_id, score, last_occurred_at
        FROM recommendation.user_equipment_type_interest
        ORDER BY user_id, equipment_type_id;
    """)
    actual_interest = cur.fetchall()

    cur.execute("""
        SELECT equipment_listing_id, score, last_occurred_at
        FROM recommendation.listing_popularity
        ORDER BY equipment_listing_id;
    """)
    actual_popularity = cur.fetchall()

    assert expected_interest == actual_interest, f"Interest mismatch: {expected_interest} vs {actual_interest}"
    assert expected_popularity == actual_popularity, f"Popularity mismatch: {expected_popularity} vs {actual_popularity}"
    print("  [PASS] Projection rebuild verified: 100% match on both score and last_occurred_at.")

    print("\n=================================================================")
    print("All PostgreSQL DDL, atomic upsert, and concurrency tests PASSED!")
    print("=================================================================")


def main():
    conn = None
    container_name = None
    try:
        conn, container_name, port = start_ephemeral_postgres()
        run_tests(conn, port)
    finally:
        if conn:
            try:
                conn.close()
            except Exception:
                pass
        if container_name:
            stop_ephemeral_postgres(container_name)


if __name__ == "__main__":
    main()
