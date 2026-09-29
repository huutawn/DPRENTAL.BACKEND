#!/usr/bin/env bash
set -euo pipefail

# Applies an already-imported MediaService manifest to the expanded Auth schema.
# DRY_RUN=true validates only. Legacy URL columns remain for a later contract migration.
manifest_file="${1:-manifest.json}"
dry_run="${DRY_RUN:-false}"

python3 - "$manifest_file" "$dry_run" <<'PY'
import csv
import io
import json
import subprocess
import sys
import uuid
from urllib.parse import urlparse

path, dry_run = sys.argv[1:]
if dry_run.lower() not in ("true", "false"):
    raise SystemExit("DRY_RUN must be true or false")

try:
    with open(path, encoding="utf-8") as source:
        records = json.load(source)
except (OSError, ValueError) as error:
    raise SystemExit(f"Cannot read manifest: {error}") from error

if not isinstance(records, list):
    raise SystemExit("Manifest must be a JSON array")

kinds = {
    "UserAvatar": ("image/jpeg", "image/png", "image/webp"),
    "OrganizationLogo": ("image/jpeg", "image/png", "image/webp"),
    "BusinessLicense": ("application/pdf", "image/jpeg", "image/png", "image/webp"),
}
seen = set()
rows = []
for index, record in enumerate(records):
    if not isinstance(record, dict):
        raise SystemExit(f"Record {index} must be an object")
    kind = record.get("recordType")
    if kind not in kinds:
        raise SystemExit(f"Record {index}: invalid recordType")
    try:
        record_id = uuid.UUID(record["recordId"])
        media_id = uuid.UUID(record["mediaId"])
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        raise SystemExit(f"Record {index}: invalid recordId or mediaId") from error
    if record_id.int == 0 or media_id.int == 0:
        raise SystemExit(f"Record {index}: empty UUID")
    key = (kind, record_id)
    if key in seen:
        raise SystemExit(f"Record {index}: duplicate recordType and recordId")
    seen.add(key)
    url = record.get("legacyUrl")
    parsed = urlparse(url) if isinstance(url, str) else None
    if not parsed or parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise SystemExit(f"Record {index}: invalid legacyUrl")
    if record.get("status") != "Mapped":
        raise SystemExit(f"Record {index}: file has not been imported")
    if record.get("fileType") not in kinds[kind]:
        raise SystemExit(f"Record {index}: unsupported fileType")
    size = record.get("sizeBytes")
    limit = 20 * 1024 * 1024 if kind == "BusinessLicense" else 10 * 1024 * 1024
    if type(size) is not int or not 0 < size <= limit:
        raise SystemExit(f"Record {index}: invalid sizeBytes")
    expected_visibility = "private" if kind == "BusinessLicense" else "public"
    if record.get("visibility") != expected_visibility:
        raise SystemExit(f"Record {index}: incorrect visibility")
    rows.append((kind, str(record_id), url, str(media_id)))

print(f"Validated {len(rows)} imported media mappings")
if dry_run.lower() == "true":
    print("DRY_RUN: no database changes")
    raise SystemExit(0)

csv_buffer = io.StringIO()
writer = csv.writer(csv_buffer, lineterminator="\n")
writer.writerows(rows)
sql = '''BEGIN;
LOCK TABLE auth."Users", auth."Organizations" IN SHARE ROW EXCLUSIVE MODE;
CREATE TEMP TABLE media_manifest (kind text NOT NULL, record_id uuid NOT NULL, old_url text NOT NULL, media_id uuid NOT NULL) ON COMMIT DROP;
COPY media_manifest (kind, record_id, old_url, media_id) FROM STDIN WITH (FORMAT csv);
''' + csv_buffer.getvalue() + r'''\.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM auth."Users" u WHERE u."AvatarUrl" IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM media_manifest m WHERE m.kind = 'UserAvatar' AND m.record_id = u."Id" AND m.old_url = u."AvatarUrl")
    ) OR EXISTS (
        SELECT 1 FROM auth."Organizations" o WHERE o."LogoUrl" IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM media_manifest m WHERE m.kind = 'OrganizationLogo' AND m.record_id = o."Id" AND m.old_url = o."LogoUrl")
    ) OR EXISTS (
        SELECT 1 FROM auth."Organizations" o WHERE o."BusinessLicenseDocumentUrl" IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM media_manifest m WHERE m.kind = 'BusinessLicense' AND m.record_id = o."Id" AND m.old_url = o."BusinessLicenseDocumentUrl")
    ) OR EXISTS (
        SELECT 1 FROM media_manifest m WHERE
        (m.kind = 'UserAvatar' AND NOT EXISTS (SELECT 1 FROM auth."Users" u WHERE u."Id" = m.record_id AND u."AvatarUrl" = m.old_url)) OR
        (m.kind = 'OrganizationLogo' AND NOT EXISTS (SELECT 1 FROM auth."Organizations" o WHERE o."Id" = m.record_id AND o."LogoUrl" = m.old_url)) OR
        (m.kind = 'BusinessLicense' AND NOT EXISTS (SELECT 1 FROM auth."Organizations" o WHERE o."Id" = m.record_id AND o."BusinessLicenseDocumentUrl" = m.old_url))
    ) THEN
        RAISE EXCEPTION 'Manifest does not exactly cover the current legacy URLs';
    END IF;
END $$;
UPDATE auth."Users" u SET "AvatarMediaId" = m.media_id FROM media_manifest m WHERE m.kind = 'UserAvatar' AND u."Id" = m.record_id;
UPDATE auth."Organizations" o SET "LogoMediaId" = m.media_id FROM media_manifest m WHERE m.kind = 'OrganizationLogo' AND o."Id" = m.record_id;
UPDATE auth."Organizations" o SET "BusinessLicenseDocumentMediaId" = m.media_id FROM media_manifest m WHERE m.kind = 'BusinessLicense' AND o."Id" = m.record_id;
COMMIT;
'''

# The container supplies POSTGRES_USER and POSTGRES_DB; no password is put on argv.
command = ["docker", "compose", "exec", "-T", "postgres", "sh", "-c",
           'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"']
result = subprocess.run(command, input=sql, text=True, capture_output=True, check=False)
if result.returncode:
    raise SystemExit(f"Database backfill failed; transaction rolled back:\n{result.stderr}")
print("Applied manifest to AuthService in one transaction")
PY
