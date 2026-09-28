#!/usr/bin/env bash
set -euo pipefail

# Local-only AuthService data setup. The secret remains in the ignored root .env.
stack_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
set -a
. "$stack_root/.env"
set +a

: "${MEDIA_SERVICE_CLIENT_SECRET:?Set MEDIA_SERVICE_CLIENT_SECRET in the root .env}"
: "${POSTGRES_USER:?Set POSTGRES_USER in the root .env}"
: "${POSTGRES_DB:?Set POSTGRES_DB in the root .env}"

if [[ "$MEDIA_SERVICE_CLIENT_SECRET" == replace-with-* ]]; then
    echo 'Set a real local MEDIA_SERVICE_CLIENT_SECRET in .env first.' >&2
    exit 1
fi

secret_hash="$(printf '%s' "$MEDIA_SERVICE_CLIENT_SECRET" | sha256sum | awk '{print toupper($1)}')"
audience="${AUTH_JWT_AUDIENCE:-dprental-clients}"

docker compose --project-directory "$stack_root" --env-file "$stack_root/.env" \
    -f "$stack_root/compose.yaml" exec -T postgres \
    psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
BEGIN;
INSERT INTO auth."ServicePrincipals"
    ("Id", "ClientId", "Name", "SecretHash", "IsActive", "CreatedAtUtc")
VALUES
    ('11111111-1111-4111-8111-111111111111', 'local-catalog-service',
     'CatalogService local', '$secret_hash', TRUE, NOW())
ON CONFLICT ("Id") DO UPDATE
SET "SecretHash" = EXCLUDED."SecretHash", "IsActive" = TRUE;

INSERT INTO auth."ServicePrincipalScopes"
    ("Id", "ServicePrincipalId", "Scope", "Audience")
SELECT gen_random_uuid(), '11111111-1111-4111-8111-111111111111', scope, '$audience'
FROM (VALUES ('media.verify'), ('media.read'), ('media.publish')) AS requested(scope)
ON CONFLICT ("ServicePrincipalId", "Scope", "Audience") DO NOTHING;
COMMIT;
SQL

echo 'Local CatalogService principal is ready (clientId: local-catalog-service).'
