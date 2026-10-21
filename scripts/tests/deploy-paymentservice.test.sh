#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
deployer="$repository_root/scripts/deploy-paymentservice.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'Test triển khai PaymentService: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  local expected="$1"
  local file="$2"
  rg --fixed-strings --quiet -- "$expected" "$file" || fail "không tìm thấy '$expected' trong $file"
}

assert_not_contains() {
  local unexpected="$1"
  local file="$2"
  if rg --fixed-strings --quiet -- "$unexpected" "$file"; then
    fail "tìm thấy nội dung không mong muốn '$unexpected' trong $file"
  fi
}

deploy_directory="$temporary_directory/deploy"
fake_bin="$temporary_directory/bin"
docker_log="$temporary_directory/docker.log"
curl_log="$temporary_directory/curl.log"
deploy_log="$temporary_directory/deploy.log"
docker_config_log="$temporary_directory/docker-config-path"
release_sha='0123456789abcdef0123456789abcdef01234567'

mkdir -p "$deploy_directory" "$fake_bin"
printf '%s\n' 'DEV_DOMAIN=dev-api.example.test' > "$deploy_directory/.env"
printf '%s\n' \
  'PAYMENT_SERVICE_IMAGE=ghcr.io/example/dprental-payment:previous' \
  'PAYMENT_MIGRATOR_IMAGE=ghcr.io/example/dprental-payment-migrator:previous' \
  'PAYMENT_INTEGRITY_HOST_IMAGE=ghcr.io/example/dprental-payment-integrity:previous' \
  'PAYMENT_INTEGRITY_MIGRATOR_IMAGE=ghcr.io/example/dprental-payment-integrity-migrator:previous' \
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
  *' run --rm payment-migrate '* | *' run --rm integrity-migrate '* | *' exec -T auth-service '*)
    cat >/dev/null
    ;;
  *' ps -q integrity-host '*)
    printf 'integrity-host-container\n'
    ;;
  *' ps -q payment-service '*)
    printf 'payment-service-container\n'
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
  PAYMENT_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains "PAYMENT_SERVICE_IMAGE=ghcr.io/example/dprental-payment:$release_sha" "$deploy_directory/release.env"
assert_contains "PAYMENT_MIGRATOR_IMAGE=ghcr.io/example/dprental-payment-migrator:$release_sha" "$deploy_directory/release.env"
assert_contains "PAYMENT_INTEGRITY_HOST_IMAGE=ghcr.io/example/dprental-payment-integrity:$release_sha" "$deploy_directory/release.env"
assert_contains "PAYMENT_INTEGRITY_MIGRATOR_IMAGE=ghcr.io/example/dprental-payment-integrity-migrator:$release_sha" "$deploy_directory/release.env"
assert_contains 'login ghcr.io --username example --password-stdin' "$docker_log"
assert_not_contains 'test-token' "$docker_log"
[ ! -e "$(< "$docker_config_log")" ] || fail 'Docker config tạm vẫn tồn tại sau deploy.'
assert_contains '--profile migration pull payment-service payment-migrate integrity-host integrity-migrate' "$docker_log"
assert_contains 'up -d payment-postgres integrity-postgres' "$docker_log"
assert_contains '--profile migration run --rm payment-migrate' "$docker_log"
assert_contains '--profile migration run --rm integrity-migrate' "$docker_log"
assert_contains 'up -d --no-deps --force-recreate integrity-host' "$docker_log"
assert_contains 'up -d --no-deps --force-recreate payment-service' "$docker_log"
assert_contains 'exec -T auth-service perl -MIO::Socket::INET -e $s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /.well-known/jwks.json HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' "$docker_log"
payment_migration_line="$(awk '/--profile migration run --rm payment-migrate/ { print NR; exit }' "$docker_log")"
integrity_migration_line="$(awk '/--profile migration run --rm integrity-migrate/ { print NR; exit }' "$docker_log")"
integrity_runtime_line="$(awk '/up -d --no-deps --force-recreate integrity-host/ { print NR; exit }' "$docker_log")"
payment_runtime_line="$(awk '/up -d --no-deps --force-recreate payment-service/ { print NR; exit }' "$docker_log")"
[ "$payment_migration_line" -lt "$integrity_migration_line" ] || fail 'Migration Payment phải chạy trước migration IntegrityHost.'
[ "$integrity_migration_line" -lt "$integrity_runtime_line" ] || fail 'Migration IntegrityHost phải chạy trước khi recreate host.'
[ "$integrity_runtime_line" -lt "$payment_runtime_line" ] || fail 'IntegrityHost phải đạt healthy trước khi recreate Payment API.'
assert_contains 'https://dev-api.example.test/health/ready' "$curl_log"
assert_contains "PaymentService revision $release_sha đã healthy" "$deploy_log"

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" "$deployer" invalid >/dev/null 2>&1; then
  fail 'SHA không hợp lệ lại được chấp nhận.'
fi

PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GITHUB_SHA="$release_sha" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  PAYMENT_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- < "$deployer" >/dev/null

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" GHCR_USERNAME=example "$deployer" "$release_sha" >/dev/null 2>&1; then
  fail 'Thiếu GHCR_TOKEN lại được chấp nhận.'
fi

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" DOCKER_LOG="$docker_log" GHCR_USERNAME=example GHCR_TOKEN=test-token FAIL_DOCKER_LOGIN=1 "$deployer" "$release_sha" >/dev/null 2>&1; then
  fail 'Đăng nhập GHCR thất bại nhưng deploy vẫn tiếp tục.'
fi

if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" GITHUB_SHA=invalid "$deployer" >/dev/null 2>&1; then
  fail 'GITHUB_SHA không hợp lệ lại được chấp nhận.'
fi

printf 'Đã đạt regression test cho script triển khai PaymentService.\n'
