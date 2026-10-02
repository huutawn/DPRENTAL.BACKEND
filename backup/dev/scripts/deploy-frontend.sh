#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly HEALTH_ATTEMPTS="${FRONTEND_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${FRONTEND_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${FRONTEND_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai Frontend: %s\n' "$*" >&2
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

  [[ "$image" != *@* ]] || fail 'image trong release.env phải dùng tag, không dùng digest.'
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

update_release_image() {
  local frontend_image="$1"
  local temporary_file

  temporary_file="$(mktemp "$DEPLOY_DIRECTORY/.release.env.XXXXXX")" || \
    fail 'không thể tạo file release tạm thời.'
  trap 'rm -f "$temporary_file"' RETURN

  awk \
    -v frontend_image="$frontend_image" \
    '
      /^FRONTEND_IMAGE=/ {
        print "FRONTEND_IMAGE=" frontend_image
        frontend_found = 1
        next
      }
      { print }
      END {
        if (!frontend_found) {
          exit 42
        }
      }
    ' "$RELEASE_FILE" > "$temporary_file" || fail 'release.env thiếu image Frontend.'

  chmod 0600 "$temporary_file"
  mv "$temporary_file" "$RELEASE_FILE"
  trap - RETURN
}

wait_for_frontend_health() {
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$(${compose_command[@]} ps -q frontend)"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ Frontend healthy (%s/%s, trạng thái: %s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container Frontend được tạo (%s/%s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'Frontend không đạt trạng thái healthy; không tự rollback image.'
}

reload_caddy() {
  "${compose_command[@]}" exec -T caddy \
    caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile || \
    fail 'không thể reload Caddy với route Frontend mới.'
}

wait_for_public_frontend() {
  local domain="$1"
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    if curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
      "https://${domain}/" >/dev/null; then
      return 0
    fi
    printf 'Đang chờ Frontend public qua Caddy/Cloudflare (%s/%s)...\n' \
      "$attempt" "$HEALTH_ATTEMPTS"
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'frontend công khai qua Caddy/Cloudflare thất bại; không tự rollback image.'
}

[ "$#" -eq 1 ] || usage
release_sha="$1"
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'release SHA phải là commit SHA gồm đúng 40 ký tự hex thường.'

require_file "$ENVIRONMENT_FILE"
require_file "$RELEASE_FILE"
require_file "$COMPOSE_FILE"

readonly -a compose_command=(docker compose --project-directory "$DEPLOY_DIRECTORY" --file "$COMPOSE_FILE" --env-file "$ENVIRONMENT_FILE" --env-file "$RELEASE_FILE")

current_frontend_image="$(read_release_value FRONTEND_IMAGE)"
frontend_image="$(image_repository "$current_frontend_image"):$release_sha"
frontend_domain="$(read_environment_value DEV_APP_DOMAIN)"

printf 'Đang phát hành Frontend revision %s...\n' "$release_sha"
update_release_image "$frontend_image"

"${compose_command[@]}" config --quiet
"${compose_command[@]}" pull frontend
"${compose_command[@]}" up -d --no-deps --force-recreate frontend

wait_for_frontend_health
reload_caddy
wait_for_public_frontend "$frontend_domain"

printf 'Frontend revision %s đã healthy; smoke test công khai đã pass.\n' "$release_sha"
