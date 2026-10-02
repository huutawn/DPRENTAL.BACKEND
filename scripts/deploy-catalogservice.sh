#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly BACKUP_DIRECTORY="${CATALOG_BACKUP_DIRECTORY:-$DEPLOY_DIRECTORY/backups/catalog}"
readonly HEALTH_ATTEMPTS="${CATALOG_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${CATALOG_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${CATALOG_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai CatalogService thất bại: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Sử dụng: %s [RELEASE_SHA]\n' "$0" >&2
  exit 1
}

require_file() {
  [ -f "$1" ] || fail "không tìm thấy file bắt buộc: $1"
}

image_repository() {
  local image="$1"
  local last_component="${image##*/}"

  [[ "$image" != *@* ]] || fail "image trong release.env phải dùng tag, không dùng digest."
  [[ "$last_component" == *:* ]] || fail "image trong release.env phải có tag: $image"
  printf '%s\n' "${image%:*}"
}

read_release_value() {
  local variable_name="$1"
  local line

  line="$(grep -E "^${variable_name}=" "$RELEASE_FILE" | tail -n 1)" || \
    fail "release.env chưa có ${variable_name}."
  printf '%s\n' "${line#*=}"
}

read_environment_value() {
  local variable_name="$1"
  local line
  local value

  line="$(grep -E "^${variable_name}=" "$ENVIRONMENT_FILE" | tail -n 1)" || \
    fail ".env chưa có ${variable_name}."
  value="${line#*=}"
  value="${value%\"}"
  value="${value#\"}"
  value="${value%\'}"
  value="${value#\'}"
  printf '%s\n' "$value"
}

update_release_images() {
  local runtime_image="$1"
  local migrator_image="$2"
  local temporary_file

  temporary_file="$(mktemp "$DEPLOY_DIRECTORY/.release.env.XXXXXX")" || \
    fail 'không thể tạo file release tạm thời.'
  trap 'rm -f "$temporary_file"' RETURN

  awk \
    -v runtime_image="$runtime_image" \
    -v migrator_image="$migrator_image" \
    '
      /^CATALOG_SERVICE_IMAGE=/ {
        print "CATALOG_SERVICE_IMAGE=" runtime_image
        runtime_found = 1
        next
      }
      /^CATALOG_MIGRATOR_IMAGE=/ {
        print "CATALOG_MIGRATOR_IMAGE=" migrator_image
        migrator_found = 1
        next
      }
      { print }
      END {
        if (!runtime_found || !migrator_found) {
          exit 42
        }
      }
    ' "$RELEASE_FILE" > "$temporary_file" || fail 'release.env thiếu image CatalogService hoặc migrator.'

  chmod 0600 "$temporary_file"
  mv "$temporary_file" "$RELEASE_FILE"
  trap - RETURN
}

backup_catalog_schema_if_exists() {
  local postgres_user
  local postgres_db
  local schema_exists
  local backup_file

  postgres_user="$(read_environment_value POSTGRES_USER)"
  postgres_db="$(read_environment_value POSTGRES_DB)"

  install -d -m 0700 "$BACKUP_DIRECTORY" || \
    fail "không thể tạo thư mục backup: $BACKUP_DIRECTORY"

  if ! schema_exists="$("${compose_command[@]}" exec -T postgres \
      psql -U "$postgres_user" -d "$postgres_db" -tAc \
      "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'catalog';" </dev/null)"; then
    fail 'không thể kiểm tra sự tồn tại của schema catalog trong PostgreSQL; dừng triển khai.'
  fi

  schema_exists="$(printf '%s' "$schema_exists" | tr -d '[:space:]')"

  if [ "$schema_exists" = "1" ]; then
    backup_file="$BACKUP_DIRECTORY/catalog-${release_sha}-$(date +%Y%m%d%H%M%S).dump"
    printf 'Đang tạo bản sao lưu custom format cho schema catalog tại %s...\n' "$backup_file"
    if ! "${compose_command[@]}" exec -T postgres \
        pg_dump -U "$postgres_user" -d "$postgres_db" -n catalog -Fc </dev/null > "$backup_file"; then
      rm -f "$backup_file"
      fail 'tạo bản backup schema catalog thất bại; dừng triển khai.'
    fi

    if [ ! -s "$backup_file" ]; then
      rm -f "$backup_file"
      fail 'file backup schema catalog rỗng; dừng triển khai.'
    fi

    chmod 0600 "$backup_file" || fail 'không thể giới hạn quyền truy cập cho file backup.'
    printf 'Đã sao lưu schema catalog thành công: %s\n' "$backup_file"
  elif [ -z "$schema_exists" ]; then
    printf 'Schema catalog chưa tồn tại trong cơ sở dữ liệu; bỏ qua backup trước migration.\n'
  else
    fail 'kết quả kiểm tra schema catalog không hợp lệ; dừng triển khai.'
  fi
}

wait_for_catalog_health() {
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$("${compose_command[@]}" ps -q catalog-service)"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ CatalogService healthy (%s/%s, trạng thái: %s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container CatalogService được tạo (%s/%s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'CatalogService không đạt trạng thái healthy; không tự rollback schema hoặc image.'
}

smoke_test_catalog_internal_ready() {
  "${compose_command[@]}" exec -T catalog-service \
    wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready </dev/null || \
    fail 'health ready nội bộ của CatalogService thất bại; không tự rollback schema hoặc image.'
}

smoke_test_gateway_health() {
  local domain="$1"
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    if curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
      "https://${domain}/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    printf 'Đang chờ Gateway health sẵn sàng (%s/%s)...\n' "$attempt" "$HEALTH_ATTEMPTS"
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'health endpoint công khai qua Gateway thất bại sau nhiều lần thử; không tự rollback schema hoặc image.'
}

[ "$#" -le 1 ] || usage
release_sha="${1:-${GITHUB_SHA:-}}"
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'release SHA phải là commit SHA gồm đúng 40 ký tự hex thường.'

require_file "$ENVIRONMENT_FILE"
require_file "$RELEASE_FILE"
require_file "$COMPOSE_FILE"
[ -n "${GHCR_USERNAME:-}" ] || fail 'thiếu GitHub username để pull image private.'
[ -n "${GHCR_TOKEN:-}" ] || fail 'thiếu GitHub token để pull image private.'
docker_config_dir="$(mktemp -d)" || fail 'không thể tạo Docker config tạm thời.'
trap 'rm -rf "$docker_config_dir"' EXIT
export DOCKER_CONFIG="$docker_config_dir"
docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin <<< "$GHCR_TOKEN" >/dev/null
unset GHCR_TOKEN

readonly -a compose_command=(docker compose --project-directory "$DEPLOY_DIRECTORY" --file "$COMPOSE_FILE" --env-file "$ENVIRONMENT_FILE" --env-file "$RELEASE_FILE")

current_runtime_image="$(read_release_value CATALOG_SERVICE_IMAGE)"
current_migrator_image="$(read_release_value CATALOG_MIGRATOR_IMAGE)"
runtime_image="$(image_repository "$current_runtime_image"):$release_sha"
migrator_image="$(image_repository "$current_migrator_image"):$release_sha"
dev_domain="$(read_environment_value DEV_DOMAIN)"

printf 'Đang phát hành CatalogService revision %s...\n' "$release_sha"
update_release_images "$runtime_image" "$migrator_image"

"${compose_command[@]}" config --quiet || fail 'cấu hình Compose không hợp lệ.'
"${compose_command[@]}" --profile migration pull catalog-service catalog-migrate
"${compose_command[@]}" up -d --wait --wait-timeout 90 postgres

"${compose_command[@]}" stop catalog-service
backup_catalog_schema_if_exists

"${compose_command[@]}" --profile migration run --rm catalog-migrate </dev/null || \
  fail 'migration Catalog thất bại; không tự rollback schema hoặc image.'

"${compose_command[@]}" up -d --no-deps --force-recreate catalog-service

wait_for_catalog_health
smoke_test_catalog_internal_ready
smoke_test_gateway_health "$dev_domain"

curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
  "https://${dev_domain}/api/v1/catalog/categories" >/dev/null || \
  fail 'GET categories qua Gateway thất bại; kiểm tra Gateway đã có route Catalog.'
curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
  "https://${dev_domain}/api/v1/catalog/equipment-types?page=1&pageSize=1" >/dev/null || \
  fail 'GET equipment-types qua Gateway thất bại; kiểm tra Gateway đã có route Catalog.'

printf 'CatalogService revision %s đã healthy; Gateway health, Catalog readiness và public GET đã pass.\n' "$release_sha"
