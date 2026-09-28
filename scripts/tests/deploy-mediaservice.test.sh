#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
deployer="$repository_root/scripts/deploy-mediaservice.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'deploy-mediaservice.test.sh: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  local needle="$1"
  local haystack="$2"

  if [ -f "$haystack" ]; then
    grep -Fq -- "$needle" "$haystack" || fail "file '$haystack' không chứa chuỗi '$needle'"
  else
    printf '%s\n' "$haystack" | grep -Fq -- "$needle" || fail "nội dung không chứa chuỗi '$needle'"
  fi
}

assert_not_contains() {
  local needle="$1"
  local haystack="$2"

  if [ -f "$haystack" ]; then
    if grep -Fq -- "$needle" "$haystack"; then
      fail "file '$haystack' chứa chuỗi cấm '$needle'"
    fi
  else
    if printf '%s\n' "$haystack" | grep -Fq -- "$needle"; then
      fail "nội dung chứa chuỗi cấm '$needle'"
    fi
  fi
}

assert_order() {
  local first="$1"
  local second="$2"
  local file="$3"

  local first_line second_line
  first_line="$(grep -nF -- "$first" "$file" | head -n 1 | cut -d: -f1)" || fail "không tìm thấy: $first trong $file"
  second_line="$(grep -nF -- "$second" "$file" | head -n 1 | cut -d: -f1)" || fail "không tìm thấy: $second trong $file"

  [ "$first_line" -lt "$second_line" ] || \
    fail "thứ tự sai: '$first' (dòng $first_line) phải xuất hiện trước '$second' (dòng $second_line)"
}

deploy_directory="$temporary_directory/deploy"
fake_bin="$temporary_directory/bin"
docker_log="$temporary_directory/docker.log"
curl_log="$temporary_directory/curl.log"
deploy_log="$temporary_directory/deploy.log"
docker_config_log="$temporary_directory/docker-config-path"
release_sha='0123456789abcdef0123456789abcdef01234567'

mkdir -p "$fake_bin" "$deploy_directory"

setup_test_env() {
  rm -rf "$deploy_directory"
  mkdir -p "$deploy_directory"
  printf '%s\n' \
    'DEV_DOMAIN=dev-api.example.test' \
    'POSTGRES_DB=mediaservice' \
    'POSTGRES_USER=mediaservice' \
    'POSTGRES_PASSWORD=secret-postgres-pw' \
    > "$deploy_directory/.env"

  printf '%s\n' \
    'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:auth-prev' \
    'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:auth-prev' \
    'MEDIA_SERVICE_IMAGE=ghcr.io/example/dprental-media:media-prev' \
    'MEDIA_MIGRATOR_IMAGE=ghcr.io/example/dprental-media-migrator:media-prev' \
    > "$deploy_directory/release.env"

  touch "$deploy_directory/compose.yaml"
  : > "$docker_log"
  : > "$curl_log"
  : > "$deploy_log"
}

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
  exit 0
fi

if [ "${1:-}" = inspect ]; then
  if [ "${FAIL_HEALTH:-}" = 1 ]; then
    printf 'unhealthy\n'
    exit 0
  fi
  printf 'healthy\n'
  exit 0
fi

case " $* " in
  *' compose '*' config '*)
    if [ "${FAIL_COMPOSE_CONFIG:-}" = 1 ]; then
      exit 1
    fi
    exit 0
    ;;
  *' exec -T postgres psql '*'SELECT 1 FROM information_schema.schemata WHERE schema_name = '\''media'\'';'*)
    if [ "${FAIL_SCHEMA_CHECK:-}" = 1 ]; then
      exit 1
    fi
    if [ "${SCHEMA_EXISTS:-1}" = 1 ]; then
      printf '1\n'
    else
      printf '0\n'
    fi
    exit 0
    ;;
  *' exec -T postgres pg_dump '*)
    if [ "${FAIL_BACKUP:-}" = 1 ]; then
      exit 1
    fi
    printf 'PGDUMP_CUSTOM_BINARY_DATA\n'
    exit 0
    ;;
  *' run --rm media-migrate '*)
    if [ "${FAIL_MIGRATION:-}" = 1 ]; then
      exit 1
    fi
    cat >/dev/null
    exit 0
    ;;
  *' exec -T media-service '*)
    if [ "${FAIL_INTERNAL_HEALTH:-}" = 1 ]; then
      exit 1
    fi
    cat >/dev/null
    exit 0
    ;;
  *' ps -q media-service '*)
    printf 'media-service-container-123\n'
    exit 0
    ;;
esac
EOF
chmod +x "$fake_bin/docker"

cat > "$fake_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >> "$CURL_LOG"

if [ "${FAIL_CURL:-}" = 1 ]; then
  exit 1
fi

case " $* " in
  *'/api/v1/media/'*'/content'*)
    if [ "${FAIL_GATEWAY_MEDIA_ROUTE:-}" = 1 ]; then
      exit 1
    fi
    if [ "${GATEWAY_MEDIA_HTTP_CODE:-}" != "" ]; then
      printf '%s' "$GATEWAY_MEDIA_HTTP_CODE"
      exit 0
    fi
    printf '404'
    exit 0
    ;;
  *'/health/ready'*)
    if [ "${FAIL_GATEWAY_HEALTH:-}" = 1 ]; then
      exit 1
    fi
    exit 0
    ;;
esac
EOF
chmod +x "$fake_bin/curl"

# 1. Test happy path với backup, migration, restart và healthcheck
setup_test_env

PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains "MEDIA_SERVICE_IMAGE=ghcr.io/example/dprental-media:$release_sha" "$deploy_directory/release.env"
assert_contains "MEDIA_MIGRATOR_IMAGE=ghcr.io/example/dprental-media-migrator:$release_sha" "$deploy_directory/release.env"
assert_contains 'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:auth-prev' "$deploy_directory/release.env"
assert_contains 'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:auth-prev' "$deploy_directory/release.env"

assert_contains 'login ghcr.io --username example --password-stdin' "$docker_log"
assert_not_contains 'test-token' "$docker_log"
assert_not_contains 'test-token' "$deploy_log"
[ ! -e "$(< "$docker_config_log")" ] || fail 'Docker config tạm vẫn tồn tại sau deploy.'

assert_contains 'compose --project-directory' "$docker_log"
assert_contains 'config' "$docker_log"
assert_contains '--profile migration pull media-service media-migrate' "$docker_log"
assert_contains 'exec -T postgres psql -U mediaservice -d mediaservice -tAc SELECT 1 FROM information_schema.schemata WHERE schema_name = '\''media'\'';' "$docker_log"
assert_contains 'exec -T postgres pg_dump -U mediaservice -d mediaservice -n media -Fc' "$docker_log"
assert_contains '--profile migration run --rm media-migrate' "$docker_log"
assert_contains 'up -d --no-deps --force-recreate media-service' "$docker_log"
assert_contains 'inspect --format {{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}} media-service-container-123' "$docker_log"
assert_contains 'exec -T media-service perl -MIO::Socket::INET -e $s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' "$docker_log"
assert_contains 'https://dev-api.example.test/health/ready' "$curl_log"
assert_contains 'https://dev-api.example.test/api/v1/media/11111111-1111-1111-1111-111111111111/content' "$curl_log"
assert_contains "MediaService revision $release_sha đã healthy" "$deploy_log"

# Kiểm tra thứ tự: backup -> migration -> restart
assert_order 'pg_dump' 'run --rm media-migrate' "$docker_log"
assert_order 'run --rm media-migrate' 'up -d --no-deps --force-recreate media-service' "$docker_log"
assert_order 'up -d --no-deps --force-recreate media-service' 'inspect' "$docker_log"

# Kiểm tra file backup được tạo và phân quyền hạn chế
backup_files=("$deploy_directory"/backups/media/media-"$release_sha"-*.dump)
[ -f "${backup_files[0]}" ] || fail 'file backup không được tạo.'
backup_dir_perm="$(stat -c %a "$deploy_directory/backups/media")"
backup_file_perm="$(stat -c %a "${backup_files[0]}")"
[ "$backup_dir_perm" = "700" ] || fail "quyền thư mục backup phải là 700, thực tế: $backup_dir_perm"
[ "$backup_file_perm" = "600" ] || fail "quyền file backup phải là 600, thực tế: $backup_file_perm"

# 2. Test luồng khi schema media chưa tồn tại (bỏ qua backup)
setup_test_env
PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  SCHEMA_EXISTS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains 'bỏ qua backup trước migration' "$deploy_log"
assert_not_contains 'pg_dump' "$docker_log"
assert_contains '--profile migration run --rm media-migrate' "$docker_log"

# 3. Test backup thất bại: phải dừng deploy, KHÔNG chạy migration và KHÔNG restart
setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  FAIL_BACKUP=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Backup thất bại nhưng script deploy không dừng.'
fi
assert_contains 'tạo bản backup schema media thất bại' "$deploy_log"
assert_not_contains '--profile migration run --rm media-migrate' "$docker_log"
assert_not_contains 'up -d --no-deps --force-recreate media-service' "$docker_log"

# 4. Test migration thất bại: phải dừng deploy, KHÔNG restart service
setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  FAIL_MIGRATION=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Migration thất bại nhưng script deploy không dừng.'
fi
assert_contains 'migration Media thất bại' "$deploy_log"
assert_contains 'không tự rollback schema' "$deploy_log"
assert_not_contains 'up -d --no-deps --force-recreate media-service' "$docker_log"

# 5. Test health check thất bại: phải dừng deploy
setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  MEDIA_DEPLOY_HEALTH_ATTEMPTS=2 \
  FAIL_HEALTH=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Docker health thất bại nhưng script deploy không dừng.'
fi
assert_contains 'MediaService không đạt trạng thái healthy' "$deploy_log"

# 6. Test Gateway health và Gateway route thất bại: phải dừng deploy
setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  FAIL_GATEWAY_HEALTH=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Gateway health thất bại nhưng script deploy không dừng.'
fi
assert_contains 'health endpoint công khai qua Gateway thất bại' "$deploy_log"

setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  FAIL_GATEWAY_MEDIA_ROUTE=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Gateway media route không kết nối được nhưng script deploy không dừng.'
fi
assert_contains 'route Media qua Gateway không đạt phản hồi hợp lệ sau' "$deploy_log"
setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  GATEWAY_MEDIA_HTTP_CODE=502 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'Gateway media route trả 502 nhưng script deploy không dừng.'
fi
assert_contains 'route Media qua Gateway không đạt phản hồi hợp lệ sau' "$deploy_log"
assert_contains 'mã HTTP cuối: 502' "$deploy_log"
# 6b. Test Gateway route ban đầu trả 502 nhưng hồi phục sau retry (transient 502 recovery)
setup_test_env
retry_state_file="$temporary_directory/retry_state"
rm -f "$retry_state_file"

cat > "$fake_bin/curl" <<EOF
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "\$*" >> "$curl_log"
case " \$* " in
  *'/api/v1/media/'*'/content'*)
    if [ ! -f "$retry_state_file" ]; then
      touch "$retry_state_file"
      printf '502'
      exit 0
    fi
    printf '404'
    exit 0
    ;;
  *'/health/ready'*)
    exit 0
    ;;
esac
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
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains 'Đang chờ route Media qua Gateway sẵn sàng' "$deploy_log"
assert_contains "MediaService revision $release_sha đã healthy" "$deploy_log"

# Khôi phục curl giả lập mặc định
cat > "$fake_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CURL_LOG"
if [ "${FAIL_CURL:-}" = 1 ]; then
  exit 1
fi
case " $* " in
  *'/api/v1/media/'*'/content'*)
    if [ "${FAIL_GATEWAY_MEDIA_ROUTE:-}" = 1 ]; then
      exit 1
    fi
    if [ "${GATEWAY_MEDIA_HTTP_CODE:-}" != "" ]; then
      printf '%s' "$GATEWAY_MEDIA_HTTP_CODE"
      exit 0
    fi
    printf '404'
    exit 0
    ;;
  *'/health/ready'*)
    if [ "${FAIL_GATEWAY_HEALTH:-}" = 1 ]; then
      exit 1
    fi
    exit 0
    ;;
esac
EOF
chmod +x "$fake_bin/curl"

setup_test_env
if PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  FAIL_INTERNAL_HEALTH=1 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
  fail 'MediaService internal health ready thất bại nhưng script deploy không dừng.'
fi
assert_contains 'health ready nội bộ của MediaService thất bại' "$deploy_log"
# 7. Test SHA không hợp lệ
setup_test_env
if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" "$deployer" invalid >/dev/null 2>&1; then
  fail 'SHA không hợp lệ lại được chấp nhận.'
fi

# 8. Test thiếu token GHCR
setup_test_env
if PATH="$fake_bin:$PATH" DPR_DEPLOY_DIRECTORY="$deploy_directory" GHCR_USERNAME=example "$deployer" "$release_sha" >/dev/null 2>&1; then
  fail 'Thiếu GHCR_TOKEN lại được chấp nhận.'
fi

# 9. Test SSH delivery qua stdin pipe
setup_test_env
{ printf '%s\n' example test-token; cat "$deployer"; } | \
  PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  EXPECTED_GHCR_TOKEN=test-token \
  MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -c 'IFS= read -r GHCR_USERNAME; IFS= read -r GHCR_TOKEN; export GHCR_USERNAME GHCR_TOKEN; exec bash -s -- "$1"' -- "$release_sha" > "$deploy_log"

assert_contains "MEDIA_SERVICE_IMAGE=ghcr.io/example/dprental-media:$release_sha" "$deploy_directory/release.env"
assert_contains "MediaService revision $release_sha đã healthy" "$deploy_log"

printf 'Đã đạt toàn bộ kiểm thử cho script triển khai MediaService.\n'
