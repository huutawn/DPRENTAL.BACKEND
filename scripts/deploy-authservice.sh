#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly HEALTH_ATTEMPTS="${AUTH_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${AUTH_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${AUTH_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai AuthService: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Cách dùng: %s <full-git-sha>\n' "${0##*/}" >&2
  exit 2
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

  line="$(grep -E "^${variable_name}=" "$ENVIRONMENT_FILE" | tail -n 1)" || \
    fail ".env chưa có ${variable_name}."
  printf '%s\n' "${line#*=}"
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
      /^AUTH_SERVICE_IMAGE=/ {
        print "AUTH_SERVICE_IMAGE=" runtime_image
        runtime_found = 1
        next
      }
      /^AUTH_MIGRATOR_IMAGE=/ {
        print "AUTH_MIGRATOR_IMAGE=" migrator_image
        migrator_found = 1
        next
      }
      { print }
      END {
        if (!runtime_found || !migrator_found) {
          exit 42
        }
      }
    ' "$RELEASE_FILE" > "$temporary_file" || fail 'release.env thiếu image AuthService hoặc migrator.'

  chmod 0600 "$temporary_file"
  mv "$temporary_file" "$RELEASE_FILE"
  trap - RETURN
}

wait_for_auth_health() {
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$(${compose_command[@]} ps -q auth-service)"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ AuthService healthy (%s/%s, trạng thái: %s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container AuthService được tạo (%s/%s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'AuthService không đạt trạng thái healthy; không tự rollback schema hoặc image.'
}

smoke_test_gateway() {
  local domain="$1"

  curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
    "https://${domain}/health/ready" >/dev/null || \
    fail 'health endpoint công khai qua Gateway thất bại; không tự rollback schema hoặc image.'
  curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
    "https://${domain}/.well-known/jwks.json" >/dev/null || \
    fail 'JWKS công khai qua Gateway thất bại; không tự rollback schema hoặc image.'
}

[ "$#" -eq 1 ] || usage
release_sha="$1"
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'release SHA phải là commit SHA gồm đúng 40 ký tự hex thường.'

require_file "$ENVIRONMENT_FILE"
require_file "$RELEASE_FILE"
require_file "$COMPOSE_FILE"

readonly -a compose_command=(docker compose --project-directory "$DEPLOY_DIRECTORY" --file "$COMPOSE_FILE" --env-file "$ENVIRONMENT_FILE" --env-file "$RELEASE_FILE")

current_runtime_image="$(read_release_value AUTH_SERVICE_IMAGE)"
current_migrator_image="$(read_release_value AUTH_MIGRATOR_IMAGE)"
runtime_image="$(image_repository "$current_runtime_image"):$release_sha"
migrator_image="$(image_repository "$current_migrator_image"):$release_sha"
dev_domain="$(read_environment_value DEV_DOMAIN)"

printf 'Đang phát hành AuthService revision %s...\n' "$release_sha"
update_release_images "$runtime_image" "$migrator_image"

"${compose_command[@]}" config --quiet
"${compose_command[@]}" pull auth-service
"${compose_command[@]}" --profile migration pull auth-migrate
"${compose_command[@]}" up -d postgres valkey
"${compose_command[@]}" --profile migration run --rm auth-migrate
"${compose_command[@]}" up -d --no-deps --force-recreate auth-service

wait_for_auth_health
smoke_test_gateway "$dev_domain"

printf 'AuthService revision %s đã healthy và smoke test qua Gateway thành công.\n' "$release_sha"
