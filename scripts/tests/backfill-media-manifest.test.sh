#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tool="$script_dir/backfill-media-manifest.sh"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

# Test 1: Valid manifest succeeds
cat <<'EOF' > "$tmp_dir/valid.json"
[
  {
    "recordType": "UserAvatar",
    "recordId": "11111111-1111-1111-1111-111111111111",
    "legacyUrl": "https://example.com/avatar.png",
    "status": "Mapped",
    "mediaId": "22222222-2222-2222-2222-222222222222",
    "fileType": "image/png",
    "sizeBytes": 2048,
    "visibility": "public"
  },
  {
    "recordType": "BusinessLicense",
    "recordId": "33333333-3333-3333-3333-333333333333",
    "legacyUrl": "https://example.com/license.pdf",
    "status": "Mapped",
    "mediaId": "44444444-4444-4444-4444-444444444444",
    "fileType": "application/pdf",
    "sizeBytes": 10240,
    "visibility": "private"
  }
]
EOF

DRY_RUN=true "$tool" "$tmp_dir/valid.json"
echo "Test 1 (Valid manifest): PASSED"

# Test 1b: application sends one checked transaction to PostgreSQL.
cat <<'EOF' > "$tmp_dir/docker"
#!/usr/bin/env bash
cat > "$BACKFILL_SQL_CAPTURE"
EOF
chmod +x "$tmp_dir/docker"
BACKFILL_SQL_CAPTURE="$tmp_dir/backfill.sql" PATH="$tmp_dir:$PATH" "$tool" "$tmp_dir/valid.json"
rg -q '^BEGIN;' "$tmp_dir/backfill.sql"
rg -q '^COPY media_manifest' "$tmp_dir/backfill.sql"
rg -q '^COMMIT;' "$tmp_dir/backfill.sql"
echo "Test 1b (Transactional database mapping): PASSED"

# Test 2: Unmapped URL halts cutover
cat <<'EOF' > "$tmp_dir/unmapped.json"
[
  {
    "recordType": "UserAvatar",
    "recordId": "11111111-1111-1111-1111-111111111111",
    "legacyUrl": "https://example.com/avatar.png",
    "status": "Pending",
    "mediaId": "",
    "fileType": "image/png",
    "sizeBytes": 2048,
    "visibility": "public"
  }
]
EOF

if DRY_RUN=true "$tool" "$tmp_dir/unmapped.json" >/dev/null 2>&1; then
  echo "Test 2 (Unmapped URL) FAILED: Expected exit code 1"
  exit 1
else
  echo "Test 2 (Unmapped URL halts cutover): PASSED"
fi

# Test 3: Inaccessible URL halts cutover
cat <<'EOF' > "$tmp_dir/inaccessible.json"
[
  {
    "recordType": "BusinessLicense",
    "recordId": "33333333-3333-3333-3333-333333333333",
    "legacyUrl": "https://example.com/broken.pdf",
    "status": "Inaccessible",
    "mediaId": "",
    "fileType": "application/pdf",
    "sizeBytes": 0,
    "visibility": "private"
  }
]
EOF

if DRY_RUN=true "$tool" "$tmp_dir/inaccessible.json" >/dev/null 2>&1; then
  echo "Test 3 (Inaccessible URL) FAILED: Expected exit code 1"
  exit 1
else
  echo "Test 3 (Inaccessible URL halts cutover): PASSED"
fi

echo "All backfill-media-manifest script tests passed successfully!"
