#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
deployer="$repository_root/scripts/deploy-gateway.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'Test triển khai Gateway: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  local expected="$1"
  local file="$2"
  grep -F --quiet -- "$expected" "$file" || fail "không tìm thấy '$expected' trong $file"
}

assert_not_contains() {
  local unexpected="$1"
  local file="$2"
  if grep -F --quiet -- "$unexpected" "$file"; then
    fail "tìm thấy nội dung không mong muốn '$unexpected' trong $file"
  fi
}

deploy_directory="$temporary_directory/deploy"
fake_bin="$temporary_directory/bin"
docker_log="$temporary_directory/docker.log"
curl_log="$temporary_directory/curl.log"
docker_config_log="$temporary_directory/docker-config-path"
release_sha='0123456789abcdef0123456789abcdef01234567'

mkdir -p "$deploy_directory" "$fake_bin"
printf '%s\n' 'DEV_DOMAIN=dev-api.example.test' > "$deploy_directory/.env"
printf '%s\n' \
  'GATEWAY_IMAGE=ghcr.io/example/dprental-gateway:previous' \
  'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:previous' \
  'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:previous' \
  > "$deploy_directory/release.env"
touch "$deploy_directory/compose.yaml"

cat > "$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >> "$DOCKER_LOG"

if [ "${1:-}" = login ] && [ "${FAIL_DOCKER_LOGIN:-}" = 1 ]; then
  exit 1
fi

if [ "${1:-}" = login ]; then
  IFS= read -r token
  [ "$token" = "$EXPECTED_GHCR_TOKEN" ]
  printf '%s\n' "$DOCKER_CONFIG" > "$DOCKER_CONFIG_LOG"
fi

if [ "${1:-}" = inspect ]; then
  printf 'healthy\n'
  exit 0
fi

case " $* " in
  *' ps -q gateway '*)
    printf 'gateway-container\n'
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
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  GATEWAY_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  "$deployer" "$release_sha" >/dev/null

assert_contains "GATEWAY_IMAGE=ghcr.io/example/dprental-gateway:$release_sha" "$deploy_directory/release.env"
assert_contains 'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:previous' "$deploy_directory/release.env"
assert_contains 'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:previous' "$deploy_directory/release.env"

assert_contains 'pull gateway' "$docker_log"
assert_contains 'login ghcr.io --username example --password-stdin' "$docker_log"
assert_not_contains 'test-token' "$docker_log"
[ ! -e "$(< "$docker_config_log")" ] || fail 'Docker config tạm vẫn tồn tại sau deploy.'
assert_contains 'up -d --no-deps --force-recreate gateway' "$docker_log"
assert_not_contains 'auth-migrate' "$docker_log"
assert_not_contains 'migration' "$docker_log"
assert_not_contains 'pull auth-service' "$docker_log"
assert_not_contains 'recreate auth-service' "$docker_log"

assert_contains 'exec -T gateway wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready' "$docker_log"
assert_contains 'https://dev-api.example.test/health/ready' "$curl_log"

PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GITHUB_SHA="$release_sha" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  GATEWAY_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  "$deployer" >/dev/null

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" GHCR_USERNAME=example "$deployer" "$release_sha" >/dev/null 2>&1; then
  fail 'Thiếu GHCR_TOKEN lại được chấp nhận.'
fi

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" DOCKER_LOG="$docker_log" GHCR_USERNAME=example GHCR_TOKEN=test-token FAIL_DOCKER_LOGIN=1 "$deployer" "$release_sha" >/dev/null 2>&1; then
  fail 'Đăng nhập GHCR thất bại nhưng deploy vẫn tiếp tục.'
fi

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" "$deployer" invalid >/dev/null 2>&1; then
  fail 'SHA không hợp lệ lại được chấp nhận.'
fi

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" GITHUB_SHA=invalid "$deployer" >/dev/null 2>&1; then
  fail 'GITHUB_SHA không hợp lệ lại được chấp nhận.'
fi

printf 'Đã đạt regression test cho script triển khai Gateway.\n'
