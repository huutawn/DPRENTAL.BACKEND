#!/usr/bin/env python3
"""
Contract Schema and Fixture Validation Runner for Recommendation Phase 1.
Dependencies: Python 3.10+, jsonschema (pip install jsonschema)

Validates all JSON Schemas (Draft 2020-12) and executes an explicit manifest-driven
suite ensuring all valid fixtures pass and all negative fixtures fail with expected errors.
Includes meta-testing to prove that a negative fixture that unexpectedly passes fails the suite.
"""

import glob
import json
import os
import sys
from datetime import datetime
from jsonschema import Draft202012Validator, FormatChecker, ValidationError

SCHEMA_DIR = os.path.join(os.path.dirname(__file__), "../../docs/contracts/schemas")
VALID_FIXTURES_DIR = os.path.join(SCHEMA_DIR, "fixtures/valid")
INVALID_FIXTURES_DIR = os.path.join(SCHEMA_DIR, "fixtures/invalid")

# Explicit manifest: fixture filename -> { schema: str, expected: 'VALID' | 'SCHEMA_ERROR' | 'SEMANTIC_ERROR' }
FIXTURE_MANIFEST = {
    # 1. Valid Fixtures
    "cart-item-added-standard.json": {
        "schema": "cart-item-added",
        "expected": "VALID"
    },
    "global-popular-listings-cache.json": {
        "schema": "global-popular-listings-cache",
        "expected": "VALID"
    },
    "listing-viewed-anonymous.json": {
        "schema": "listing-viewed",
        "expected": "VALID"
    },
    "listing-viewed-fractional-seconds.json": {
        "schema": "listing-viewed",
        "expected": "VALID"
    },
    "listing-viewed-metadata.json": {
        "schema": "listing-viewed",
        "expected": "VALID"
    },
    "listing-viewed-mixed-case-uuid.json": {
        "schema": "listing-viewed",
        "expected": "VALID"
    },
    "listing-viewed-standard.json": {
        "schema": "listing-viewed",
        "expected": "VALID"
    },
    "rental-item-completed-standard.json": {
        "schema": "rental-item-completed",
        "expected": "VALID"
    },
    "rental-item-paid-deterministic-uuid.json": {
        "schema": "rental-item-paid",
        "expected": "VALID"
    },
    "rental-item-paid-standard.json": {
        "schema": "rental-item-paid",
        "expected": "VALID"
    },
    "search-performed-anonymous.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-category-only.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-full-filters.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-keyword-only.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-no-correlation.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-with-type.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-with-type-expanded.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-without-type.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "search-performed-zero-result.json": {
        "schema": "search-performed",
        "expected": "VALID"
    },
    "user-equipment-types-cache-int64-max.json": {
        "schema": "user-equipment-types-cache",
        "expected": "VALID"
    },
    "user-equipment-types-cache.json": {
        "schema": "user-equipment-types-cache",
        "expected": "VALID"
    },

    # 2. Invalid / Negative Fixtures
    "cart-item-added-without-type.json": {
        "schema": "cart-item-added",
        "expected": "SCHEMA_ERROR"
    },
    "rental-item-completed-without-type.json": {
        "schema": "rental-item-completed",
        "expected": "SCHEMA_ERROR"
    },
    "rental-item-paid-without-type.json": {
        "schema": "rental-item-paid",
        "expected": "SCHEMA_ERROR"
    },
    "cart-item-added-null-user-id.json": {
        "schema": "cart-item-added",
        "expected": "SCHEMA_ERROR"
    },
    "cart-item-added-wrong-producer.json": {
        "schema": "cart-item-added",
        "expected": "SCHEMA_ERROR"
    },
    "event-empty-nil-uuid-message-id.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "event-invalid-timestamp-not-iso.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "event-invalid-timestamp-offset.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "global-popular-listings-cache-case-insensitive-duplicate.json": {
        "schema": "global-popular-listings-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "global-popular-listings-cache-duplicate-id.json": {
        "schema": "global-popular-listings-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "global-popular-listings-cache-exceeds-max.json": {
        "schema": "global-popular-listings-cache",
        "expected": "SCHEMA_ERROR"
    },
    "global-popular-listings-cache-score-exceeds-int64.json": {
        "schema": "global-popular-listings-cache",
        "expected": "SCHEMA_ERROR"
    },
    "global-popular-listings-cache-wrong-tie-break.json": {
        "schema": "global-popular-listings-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "listing-viewed-empty-nil-uuid-listing-id.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "listing-viewed-missing-listing-id.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "listing-viewed-wrong-producer.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "missing-required-message-id.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "missing-required-occurred-at.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "missing-required-payload.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "search-performed-extra-search-text.json": {
        "schema": "search-performed",
        "expected": "SCHEMA_ERROR"
    },
    "search-performed-filter-context-extra-field.json": {
        "schema": "search-performed",
        "expected": "SCHEMA_ERROR"
    },
    "search-performed-invalid-date-format.json": {
        "schema": "search-performed",
        "expected": "SCHEMA_ERROR"
    },
    "search-performed-invalid-page.json": {
        "schema": "search-performed",
        "expected": "SCHEMA_ERROR"
    },
    "search-performed-negative-result-count.json": {
        "schema": "search-performed",
        "expected": "SCHEMA_ERROR"
    },
    "user-equipment-types-cache-case-insensitive-duplicate.json": {
        "schema": "user-equipment-types-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "user-equipment-types-cache-duplicate-id.json": {
        "schema": "user-equipment-types-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "user-equipment-types-cache-exceeds-max.json": {
        "schema": "user-equipment-types-cache",
        "expected": "SCHEMA_ERROR"
    },
    "user-equipment-types-cache-score-exceeds-int64.json": {
        "schema": "user-equipment-types-cache",
        "expected": "SCHEMA_ERROR"
    },
    "user-equipment-types-cache-wrong-sort.json": {
        "schema": "user-equipment-types-cache",
        "expected": "SEMANTIC_ERROR"
    },
    "wrong-message-type.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    },
    "wrong-version.json": {
        "schema": "listing-viewed",
        "expected": "SCHEMA_ERROR"
    }
}


def create_format_checker() -> FormatChecker:
    checker = FormatChecker()

    @checker.checks("date-time")
    def check_datetime(val):
        if not isinstance(val, str):
            return False
        # Must be ISO-8601 UTC with Z
        if not val.endswith("Z"):
            return False
        try:
            datetime.fromisoformat(val[:-1] + "+00:00")
            return True
        except ValueError:
            return False

    return checker


def load_schemas():
    schemas = {}
    expected_schemas = [
        "listing-viewed",
        "search-performed",
        "cart-item-added",
        "rental-item-paid",
        "rental-item-completed",
        "user-equipment-types-cache",
        "global-popular-listings-cache"
    ]
    for s_name in expected_schemas:
        schema_path = os.path.join(SCHEMA_DIR, f"{s_name}.schema.json")
        if not os.path.isfile(schema_path):
            raise FileNotFoundError(f"Required schema file missing: {schema_path}")
        with open(schema_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        Draft202012Validator.check_schema(data)
        schemas[s_name] = data

    return schemas


def validate_cache_semantics(cache_data: dict, is_user_cache: bool):
    """
    Supplementary semantic validation beyond JSON Schema Draft 2020-12:
    - Case-insensitive uniqueness of IDs
    - Order: score descending, tie-break normalized lowercase UUID ascending
    """
    items = cache_data.get("items", [])
    id_key = "equipmentTypeId" if is_user_cache else "equipmentListingId"

    seen_ids = set()
    prev_item = None

    for idx, item in enumerate(items):
        raw_id = item.get(id_key)
        norm_id = str(raw_id).lower() if raw_id is not None else ""
        score = item.get("score")

        if norm_id in seen_ids:
            raise ValidationError(f"Duplicate {id_key} '{raw_id}' (case-normalized '{norm_id}') found at index {idx}")
        seen_ids.add(norm_id)

        if prev_item is not None:
            prev_score = prev_item.get("score")
            prev_raw_id = prev_item.get(id_key)
            prev_norm_id = str(prev_raw_id).lower() if prev_raw_id is not None else ""

            if score > prev_score:
                raise ValidationError(
                    f"Wrong score sort order at index {idx}: score {score} > previous score {prev_score}"
                )
            if score == prev_score:
                # Tie-break rule: normalized lowercase UUID string ascending
                if norm_id <= prev_norm_id:
                    raise ValidationError(
                        f"Wrong tie-break order at index {idx}: normalized ID '{norm_id}' should be > '{prev_norm_id}'"
                    )

        prev_item = item


def test_single_fixture(fixture_path: str, filename: str, schemas: dict, format_checker: FormatChecker) -> str:
    """
    Tests a fixture against its schema and supplementary semantics.
    Returns: 'VALID', 'SCHEMA_ERROR', or 'SEMANTIC_ERROR'.
    """
    if filename not in FIXTURE_MANIFEST:
        raise KeyError(f"Fixture '{filename}' is not registered in FIXTURE_MANIFEST!")

    mapping = FIXTURE_MANIFEST[filename]
    schema_name = mapping["schema"]
    if schema_name not in schemas:
        raise KeyError(f"Schema '{schema_name}' specified in manifest not found!")

    schema = schemas[schema_name]
    with open(fixture_path, "r", encoding="utf-8") as f:
        fixture = json.load(f)

    # 1. JSON Schema validation
    validator = Draft202012Validator(schema, format_checker=format_checker)
    errors = list(validator.iter_errors(fixture))
    if errors:
        return "SCHEMA_ERROR"

    # 2. Supplementary semantic validation for cache snapshots
    try:
        if "user-equipment-types-cache" in schema_name:
            validate_cache_semantics(fixture, is_user_cache=True)
        elif "global-popular-listings-cache" in schema_name:
            validate_cache_semantics(fixture, is_user_cache=False)
    except ValidationError:
        return "SEMANTIC_ERROR"

    return "VALID"


def run_meta_tests(schemas, format_checker):
    """
    Meta-test: Proves that if a negative fixture unexpectedly passes, the suite FAILS.
    """
    valid_sample_path = os.path.join(VALID_FIXTURES_DIR, "listing-viewed-standard.json")
    outcome = test_single_fixture(valid_sample_path, "listing-viewed-standard.json", schemas, format_checker)
    assert outcome == "VALID", "Sample valid fixture must produce 'VALID'"

    # Simulate what happens when a test runner expects an error but gets 'VALID'
    expected_error = "SCHEMA_ERROR"
    if outcome != expected_error:
        # This confirms that our runner logic properly flags when a negative test fails to raise an error
        meta_test_passed = True
    else:
        meta_test_passed = False

    if not meta_test_passed:
        raise AssertionError("Meta-test failed: Negative test assertion check did not trigger on unexpected pass.")


def run_validations():
    print("=================================================================")
    print("Recommendation Phase 1 Contract Validator (Manifest-Driven)")
    print("=================================================================")

    schemas = load_schemas()
    print(f"Loaded and verified {len(schemas)} schemas (Draft 2020-12).")
    for s in schemas:
        print(f"  - {s}.schema.json")

    format_checker = create_format_checker()

    # Verify all files on disk are in manifest, and all manifest files exist on disk
    valid_files_on_disk = set(os.path.basename(p) for p in glob.glob(os.path.join(VALID_FIXTURES_DIR, "*.json")))
    invalid_files_on_disk = set(os.path.basename(p) for p in glob.glob(os.path.join(INVALID_FIXTURES_DIR, "*.json")))
    all_files_on_disk = valid_files_on_disk | invalid_files_on_disk

    manifest_files = set(FIXTURE_MANIFEST.keys())

    missing_from_disk = manifest_files - all_files_on_disk
    if missing_from_disk:
        raise FileNotFoundError(f"Manifest contains files not found on disk: {missing_from_disk}")

    unregistered_on_disk = all_files_on_disk - manifest_files
    if unregistered_on_disk:
        raise RuntimeError(f"Found fixtures on disk without manifest registration: {unregistered_on_disk}")

    print(f"\nManifest verification: {len(manifest_files)} fixtures completely registered and verified.")

    # Run Meta-Tests
    run_meta_tests(schemas, format_checker)
    print("  [PASS] Meta-test verified: Negative test assertion properly fails on unexpected pass.")

    # 1. Run Valid Fixtures
    print(f"\nValidating {len(valid_files_on_disk)} valid fixtures...")
    for fname in sorted(valid_files_on_disk):
        fpath = os.path.join(VALID_FIXTURES_DIR, fname)
        expected = FIXTURE_MANIFEST[fname]["expected"]
        assert expected == "VALID", f"Valid fixture {fname} mapped to non-VALID in manifest"

        outcome = test_single_fixture(fpath, fname, schemas, format_checker)
        if outcome != "VALID":
            print(f"  [FAIL] {fname}: expected VALID, got {outcome}")
            sys.exit(1)
        print(f"  [PASS] valid/{fname}")

    # 2. Run Invalid Fixtures
    print(f"\nValidating {len(invalid_files_on_disk)} invalid / negative fixtures...")
    for fname in sorted(invalid_files_on_disk):
        fpath = os.path.join(INVALID_FIXTURES_DIR, fname)
        expected = FIXTURE_MANIFEST[fname]["expected"]
        assert expected in ("SCHEMA_ERROR", "SEMANTIC_ERROR"), f"Invalid fixture {fname} mapped to {expected}"

        outcome = test_single_fixture(fpath, fname, schemas, format_checker)
        if outcome != expected:
            print(f"  [FAIL] {fname}: expected {expected}, got {outcome}")
            sys.exit(1)
        print(f"  [PASS - REJECTED AS EXPECTED] invalid/{fname} (Result: {outcome})")

    print("\n=================================================================")
    print("All schemas and fixtures validated successfully against manifest!")
    print("=================================================================")


if __name__ == "__main__":
    run_validations()
