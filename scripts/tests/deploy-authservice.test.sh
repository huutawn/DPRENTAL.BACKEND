#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
deployer="$repository_root/scripts/deploy-authservice.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'Test triển khai AuthService: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  local expected="$1"
  local file="$2"
  rg --fixed-strings --quiet -- "$expected" "$file" || fail "không tìm thấy '$expected' trong $file"
}

deploy_directory="$temporary_directory/deploy"
fake_bin="$temporary_directory/bin"
docker_log="$temporary_directory/docker.log"
curl_log="$temporary_directory/curl.log"
deploy_log="$temporary_directory/deploy.log"
release_sha='0123456789abcdef0123456789abcdef01234567'

mkdir -p "$deploy_directory" "$fake_bin"
printf '%s\n' 'DEV_DOMAIN=dev-api.example.test' > "$deploy_directory/.env"
printf '%s\n' \
  'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:previous' \
  'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:previous' \
  > "$deploy_directory/release.env"
touch "$deploy_directory/compose.yaml"

cat > "$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >> "$DOCKER_LOG"

if [ "${1:-}" = inspect ]; then
  printf 'healthy\n'
  exit 0
fi

case " $* " in
  *' run --rm auth-migrate '* | *' exec -T auth-service '*)
    cat >/dev/null
    ;;
  *' ps -q auth-service '*)
    printf 'auth-service-container\n'
    ;;
esac
EOF
chmod +x "$fake_bin/docker"

cat > "$fake_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CURL_LOG"
EOF
chmod +x "$fake_bin/curl"

PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  AUTH_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains "AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:$release_sha" "$deploy_directory/release.env"
assert_contains "AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:$release_sha" "$deploy_directory/release.env"
assert_contains '--profile migration pull auth-service auth-migrate' "$docker_log"
assert_contains '--profile migration run --rm auth-migrate' "$docker_log"
assert_contains 'up -d --no-deps --force-recreate auth-service' "$docker_log"
assert_contains 'exec -T auth-service wget --quiet --tries=1 --output-document=/dev/null http://127.0.0.1:8080/.well-known/jwks.json' "$docker_log"
assert_contains 'https://dev-api.example.test/health/ready' "$curl_log"
assert_contains "AuthService revision $release_sha đã healthy" "$deploy_log"

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" "$deployer" invalid >/dev/null 2>&1; then
  fail 'SHA không hợp lệ lại được chấp nhận.'
fi

printf 'Đã đạt regression test cho script triển khai AuthService.\n'
