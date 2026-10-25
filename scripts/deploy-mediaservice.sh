#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly BACKUP_DIRECTORY="${MEDIA_BACKUP_DIRECTORY:-$DEPLOY_DIRECTORY/backups/media}"
readonly HEALTH_ATTEMPTS="${MEDIA_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${MEDIA_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${MEDIA_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai MediaService thất bại: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Sử dụng: %s [RELEASE_SHA]\n' "$0" >&2
  exit 1
}

require_file() {
  [ -f "$1" ] || fail "không tìm thấy file bắt buộc: $1"
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

backup_media_schema_if_exists() {
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
      "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'media';" </dev/null)"; then
    fail 'không thể kiểm tra sự tồn tại của schema media trong PostgreSQL; dừng triển khai.'
  fi

  schema_exists="$(printf '%s' "$schema_exists" | tr -d '[:space:]')"

  if [ "$schema_exists" = "1" ]; then
    backup_file="$BACKUP_DIRECTORY/media-${release_sha}-$(date +%Y%m%d%H%M%S).dump"
    printf 'Đang tạo bản sao lưu custom format cho schema media tại %s...\n' "$backup_file"
    if ! "${compose_command[@]}" exec -T postgres \
        pg_dump -U "$postgres_user" -d "$postgres_db" -n media -Fc </dev/null > "$backup_file"; then
      rm -f "$backup_file"
      fail 'tạo bản backup schema media thất bại; dừng triển khai.'
    fi

    if [ ! -s "$backup_file" ]; then
      rm -f "$backup_file"
      fail 'file backup schema media rỗng; dừng triển khai.'
    fi

    "${compose_command[@]}" exec -T postgres pg_restore --list < "$backup_file" >/dev/null || fail 'backup archive không hợp lệ.'
    chmod 0600 "$backup_file" || fail 'không thể giới hạn quyền truy cập cho file backup.'
    printf 'Đã sao lưu schema media thành công: %s\n' "$backup_file"
  elif [ -z "$schema_exists" ]; then
    printf 'Schema media chưa tồn tại trong cơ sở dữ liệu; bỏ qua backup trước migration.\n'
  else
    fail 'kết quả kiểm tra schema media không hợp lệ.'
  fi
}

wait_for_media_health() {
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$("${compose_command[@]}" ps -q media-service)"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ MediaService healthy (%s/%s, trạng thái: %s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container MediaService được tạo (%s/%s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'MediaService không đạt trạng thái healthy; không tự rollback schema hoặc image.'
}

smoke_test_media_internal_ready() {
  # Perl variables must reach Perl unchanged.
  # shellcheck disable=SC2016
  "${compose_command[@]}" exec -T media-service \
    perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' </dev/null || \
    fail 'health ready nội bộ của MediaService thất bại; không tự rollback schema hoặc image.'
}

smoke_test_gateway_health() {
  local domain="$1"
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    if public_curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
      "https://${domain}/health/ready" >/dev/null; then
      return 0
    fi
    printf 'Đang chờ Gateway health sẵn sàng (%s/%s)...\n' "$attempt" "$HEALTH_ATTEMPTS"
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'health endpoint công khai qua Gateway thất bại sau nhiều lần thử; không tự rollback schema hoặc image.'
}

# shellcheck source=scripts/deploy-common.sh
source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

[ "$#" -le 1 ] || usage
release_sha="${1:-${GITHUB_SHA:-}}"
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'release SHA phải là commit SHA gồm đúng 40 ký tự hex thường.'

require_file "$ENVIRONMENT_FILE"
require_file "$RELEASE_FILE"
require_file "$COMPOSE_FILE"
begin_deployment media

dev_domain="$(read_environment_value DEV_DOMAIN)"

printf 'Đang phát hành MediaService revision %s...\n' "$release_sha"
prepare_release MEDIA_SERVICE_IMAGE=dprental-media MEDIA_MIGRATOR_IMAGE=dprental-media-migrator

"${compose_command[@]}" --profile migration pull media-service media-migrate
require_dependencies postgres media-minio auth-service
require_completed_dependency media-minio-init

deployment_phase mutation

backup_media_schema_if_exists

deployment_phase migration
"${compose_command[@]}" --profile migration run --rm --no-deps --pull never media-migrate </dev/null || \
  fail 'migration Media thất bại; không tự rollback schema hoặc image.'

deployment_phase runtime
"${compose_command[@]}" up -d --no-deps --force-recreate --pull never --no-build media-service

deployment_phase health
wait_for_media_health
smoke_test_media_internal_ready
"${compose_command[@]}" exec -T gateway wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready </dev/null
deployment_phase public-smoke
smoke_test_gateway_health "$dev_domain"

commit_release

printf 'MediaService revision %s đã healthy; Gateway health đã pass.\n' "$release_sha"
