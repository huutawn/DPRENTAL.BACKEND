#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
deployer="$repository_root/scripts/deploy-catalogservice.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'deploy-catalogservice.test.sh: %s\n' "$*" >&2
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
    'POSTGRES_DB=catalogservice' \
    'POSTGRES_USER=catalogservice' \
    'POSTGRES_PASSWORD=secret-postgres-pw' \
    > "$deploy_directory/.env"

  printf '%s\n' \
    'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:auth-prev' \
    'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:auth-prev' \
    'CATALOG_SERVICE_IMAGE=ghcr.io/example/dprental-catalog:catalog-prev' \
    'CATALOG_MIGRATOR_IMAGE=ghcr.io/example/dprental-catalog-migrator:catalog-prev' \
    > "$deploy_directory/release.env"

  touch "$deploy_directory/compose.yaml"
  : > "$docker_log"
  : > "$curl_log"
  : > "$deploy_log"
  : > "$docker_config_log"
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
  *' compose '*' config --quiet '*)
    if [ "${FAIL_COMPOSE_CONFIG:-}" = 1 ]; then
      exit 1
    fi
    exit 0
    ;;
  *' pull catalog-service catalog-migrate '*)
    [ "${FAIL_PULL:-}" != 1 ]
    ;;
  *' exec -T postgres psql '*'SELECT 1 FROM information_schema.schemata WHERE schema_name = '\''catalog'\'';'*)
    if [ "${FAIL_SCHEMA_CHECK:-}" = 1 ]; then
      exit 1
    fi
    if [ "${SCHEMA_EXISTS:-1}" = 1 ]; then
      printf '1\n'
    else
      printf '\n'
    fi
    exit 0
    ;;
  *' exec -T postgres pg_dump '*)
    if [ "${FAIL_BACKUP:-}" = 1 ]; then
      exit 1
    fi
    [ "${EMPTY_BACKUP:-}" != 1 ] || exit 0
    printf 'PGDUMP_CUSTOM_BINARY_DATA\n'
    exit 0
    ;;
  *' run --rm catalog-migrate '*)
    if [ "${FAIL_MIGRATION:-}" = 1 ]; then
      exit 1
    fi
    cat >/dev/null
    exit 0
    ;;
  *' stop catalog-service '*)
    [ "${FAIL_STOP:-}" != 1 ]
    ;;
  *' exec -T catalog-service '*)
    if [ "${FAIL_INTERNAL_HEALTH:-}" = 1 ]; then
      exit 1
    fi
    cat >/dev/null
    exit 0
    ;;
  *' ps -q catalog-service '*)
    printf 'catalog-service-container-123\n'
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
  *'/health/ready'*)
    if [ "${FAIL_GATEWAY_HEALTH:-}" = 1 ]; then
      exit 1
    fi
    exit 0
    ;;
  *'/api/v1/catalog/'*)
    [ "${FAIL_PUBLIC_API:-}" != 1 ]
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
  CATALOG_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains "CATALOG_SERVICE_IMAGE=ghcr.io/example/dprental-catalog:$release_sha" "$deploy_directory/release.env"
assert_contains "CATALOG_MIGRATOR_IMAGE=ghcr.io/example/dprental-catalog-migrator:$release_sha" "$deploy_directory/release.env"
assert_contains 'AUTH_SERVICE_IMAGE=ghcr.io/example/dprental-auth:auth-prev' "$deploy_directory/release.env"
assert_contains 'AUTH_MIGRATOR_IMAGE=ghcr.io/example/dprental-auth-migrator:auth-prev' "$deploy_directory/release.env"

assert_contains 'login ghcr.io --username example --password-stdin' "$docker_log"
assert_not_contains 'test-token' "$docker_log"
assert_not_contains 'test-token' "$deploy_log"
[ ! -e "$(< "$docker_config_log")" ] || fail 'Docker config tạm vẫn tồn tại sau deploy.'

assert_contains 'compose --project-directory' "$docker_log"
assert_contains 'config' "$docker_log"
assert_contains '--profile migration pull catalog-service catalog-migrate' "$docker_log"
assert_contains 'up -d --wait --wait-timeout 90 postgres' "$docker_log"
assert_contains 'exec -T postgres psql -U catalogservice -d catalogservice -tAc SELECT 1 FROM information_schema.schemata WHERE schema_name = '\''catalog'\'';' "$docker_log"
assert_contains 'exec -T postgres pg_dump -U catalogservice -d catalogservice -n catalog -Fc' "$docker_log"
assert_contains '--profile migration run --rm catalog-migrate' "$docker_log"
assert_contains 'up -d --no-deps --force-recreate catalog-service' "$docker_log"
assert_contains 'inspect --format {{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}} catalog-service-container-123' "$docker_log"
assert_contains 'exec -T catalog-service wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready' "$docker_log"
assert_contains 'https://dev-api.example.test/health/ready' "$curl_log"
assert_contains 'https://dev-api.example.test/api/v1/catalog/categories' "$curl_log"
assert_contains 'https://dev-api.example.test/api/v1/catalog/equipment-types?page=1&pageSize=1' "$curl_log"
assert_contains "CatalogService revision $release_sha đã healthy" "$deploy_log"

# Kiểm tra thứ tự: stop -> backup -> migration -> restart
assert_order 'stop catalog-service' 'pg_dump' "$docker_log"
assert_order 'pg_dump' 'run --rm catalog-migrate' "$docker_log"
assert_order 'run --rm catalog-migrate' 'up -d --no-deps --force-recreate catalog-service' "$docker_log"
assert_order 'up -d --no-deps --force-recreate catalog-service' 'inspect' "$docker_log"

# Kiểm tra file backup được tạo và phân quyền hạn chế
backup_files=("$deploy_directory"/backups/catalog/catalog-"$release_sha"-*.dump)
[ -f "${backup_files[0]}" ] || fail 'file backup không được tạo.'
backup_dir_perm="$(stat -c %a "$deploy_directory/backups/catalog")"
backup_file_perm="$(stat -c %a "${backup_files[0]}")"
[ "$backup_dir_perm" = "700" ] || fail "quyền thư mục backup phải là 700, thực tế: $backup_dir_perm"
[ "$backup_file_perm" = "600" ] || fail "quyền file backup phải là 600, thực tế: $backup_file_perm"

# 2. Test luồng khi schema catalog chưa tồn tại (bỏ qua backup)
setup_test_env
PATH="$fake_bin:$PATH" \
  DOCKER_LOG="$docker_log" \
  DOCKER_CONFIG_LOG="$docker_config_log" \
  CURL_LOG="$curl_log" \
  DPR_DEPLOY_DIRECTORY="$deploy_directory" \
  GHCR_USERNAME=example \
  GHCR_TOKEN=test-token \
  EXPECTED_GHCR_TOKEN=test-token \
  CATALOG_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  SCHEMA_EXISTS=0 \
  bash -s -- "$release_sha" < "$deployer" > "$deploy_log"

assert_contains 'bỏ qua backup trước migration' "$deploy_log"
assert_not_contains 'pg_dump' "$docker_log"
assert_contains '--profile migration run --rm catalog-migrate' "$docker_log"

# Failure cases: run the actual script; only Docker/HTTP boundaries are simulated.
for scenario in FAIL_DOCKER_LOGIN FAIL_COMPOSE_CONFIG FAIL_PULL FAIL_SCHEMA_CHECK FAIL_STOP FAIL_BACKUP EMPTY_BACKUP FAIL_MIGRATION FAIL_HEALTH FAIL_GATEWAY_HEALTH FAIL_INTERNAL_HEALTH FAIL_PUBLIC_API; do
  setup_test_env
  if env PATH="$fake_bin:$PATH" \
    DOCKER_LOG="$docker_log" DOCKER_CONFIG_LOG="$docker_config_log" CURL_LOG="$curl_log" \
    DPR_DEPLOY_DIRECTORY="$deploy_directory" GHCR_USERNAME=example GHCR_TOKEN=test-token \
    EXPECTED_GHCR_TOKEN=test-token CATALOG_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
    CATALOG_DEPLOY_HEALTH_ATTEMPTS=2 "$scenario=1" \
    bash -s -- "$release_sha" < "$deployer" > "$deploy_log" 2>&1; then
    fail "$scenario không dừng deploy."
  fi
  assert_not_contains "CatalogService revision $release_sha đã healthy" "$deploy_log"
  assert_not_contains 'test-token' "$deploy_log"
  [ ! -e "$(< "$docker_config_log")" ] || fail 'Docker config chưa được xóa sau lỗi.'
  case "$scenario" in
    FAIL_DOCKER_LOGIN|FAIL_COMPOSE_CONFIG|FAIL_PULL|FAIL_SCHEMA_CHECK|FAIL_STOP|FAIL_BACKUP|EMPTY_BACKUP)
      assert_not_contains 'run --rm catalog-migrate' "$docker_log"
      assert_not_contains 'up -d --no-deps --force-recreate catalog-service' "$docker_log"
      ;;
    FAIL_MIGRATION)
      assert_contains 'migration Catalog thất bại' "$deploy_log"
      assert_not_contains 'up -d --no-deps --force-recreate catalog-service' "$docker_log"
      ;;
    FAIL_HEALTH) assert_contains 'không đạt trạng thái healthy' "$deploy_log" ;;
    FAIL_GATEWAY_HEALTH) assert_contains 'health endpoint công khai qua Gateway thất bại' "$deploy_log" ;;
    FAIL_INTERNAL_HEALTH) assert_contains 'health ready nội bộ của CatalogService thất bại' "$deploy_log" ;;
    FAIL_PUBLIC_API) assert_contains 'GET categories qua Gateway thất bại' "$deploy_log" ;;
  esac
done

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
  CATALOG_DEPLOY_HEALTH_INTERVAL_SECONDS=0 \
  bash -c 'IFS= read -r GHCR_USERNAME; IFS= read -r GHCR_TOKEN; export GHCR_USERNAME GHCR_TOKEN; exec bash -s -- "$1"' -- "$release_sha" > "$deploy_log"

assert_contains "CATALOG_SERVICE_IMAGE=ghcr.io/example/dprental-catalog:$release_sha" "$deploy_directory/release.env"
assert_contains "CatalogService revision $release_sha đã healthy" "$deploy_log"

printf 'Đã đạt toàn bộ kiểm thử cho script triển khai CatalogService.\n'
