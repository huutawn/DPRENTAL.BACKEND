#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly HEALTH_ATTEMPTS="${PAYMENT_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${PAYMENT_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${PAYMENT_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai PaymentService: %s\n' "$*" >&2
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
  local payment_image="$1"
  local payment_migrator_image="$2"
  local integrity_image="$3"
  local integrity_migrator_image="$4"
  local temporary_file

  temporary_file="$(mktemp "$DEPLOY_DIRECTORY/.release.env.XXXXXX")" || \
    fail 'không thể tạo file release tạm thời.'
  trap 'rm -f "$temporary_file"' RETURN

  awk \
    -v payment_image="$payment_image" \
    -v payment_migrator_image="$payment_migrator_image" \
    -v integrity_image="$integrity_image" \
    -v integrity_migrator_image="$integrity_migrator_image" \
    '
      /^PAYMENT_SERVICE_IMAGE=/ {
        print "PAYMENT_SERVICE_IMAGE=" payment_image
        payment_found = 1
        next
      }
      /^PAYMENT_MIGRATOR_IMAGE=/ {
        print "PAYMENT_MIGRATOR_IMAGE=" payment_migrator_image
        payment_migrator_found = 1
        next
      }
      /^PAYMENT_INTEGRITY_HOST_IMAGE=/ {
        print "PAYMENT_INTEGRITY_HOST_IMAGE=" integrity_image
        integrity_found = 1
        next
      }
      /^PAYMENT_INTEGRITY_MIGRATOR_IMAGE=/ {
        print "PAYMENT_INTEGRITY_MIGRATOR_IMAGE=" integrity_migrator_image
        integrity_migrator_found = 1
        next
      }
      { print }
      END {
        if (!payment_found || !payment_migrator_found || !integrity_found || !integrity_migrator_found) {
          exit 42
        }
      }
    ' "$RELEASE_FILE" > "$temporary_file" || fail 'release.env thiếu image PaymentService hoặc migrator.'

  chmod 0600 "$temporary_file"
  mv "$temporary_file" "$RELEASE_FILE"
  trap - RETURN
}

wait_for_service_health() {
  local service="$1"
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$("${compose_command[@]}" ps -q "$service")"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ %s healthy (%s/%s, trạng thái: %s)...\n' \
        "$service" "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container %s được tạo (%s/%s)...\n' \
        "$service" "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail "$service không đạt trạng thái healthy; không tự rollback schema hoặc image."
}

smoke_test_gateway_health() {
  local domain="$1"

  curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
    "https://${domain}/health/ready" >/dev/null || \
    fail 'health endpoint công khai qua Gateway thất bại; không tự rollback schema hoặc image.'
}

smoke_test_auth_jwks() {
  "${compose_command[@]}" exec -T auth-service \
    perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /.well-known/jwks.json HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' </dev/null || \
    fail 'JWKS nội bộ của AuthService thất bại; không tự rollback schema hoặc image.'
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

current_payment_image="$(read_release_value PAYMENT_SERVICE_IMAGE)"
current_payment_migrator_image="$(read_release_value PAYMENT_MIGRATOR_IMAGE)"
current_integrity_image="$(read_release_value PAYMENT_INTEGRITY_HOST_IMAGE)"
current_integrity_migrator_image="$(read_release_value PAYMENT_INTEGRITY_MIGRATOR_IMAGE)"
payment_image="$(image_repository "$current_payment_image"):$release_sha"
payment_migrator_image="$(image_repository "$current_payment_migrator_image"):$release_sha"
integrity_image="$(image_repository "$current_integrity_image"):$release_sha"
integrity_migrator_image="$(image_repository "$current_integrity_migrator_image"):$release_sha"
dev_domain="$(read_environment_value DEV_DOMAIN)"

printf 'Đang phát hành PaymentService revision %s...\n' "$release_sha"
update_release_images "$payment_image" "$payment_migrator_image" "$integrity_image" "$integrity_migrator_image"

"${compose_command[@]}" --profile migration pull payment-service payment-migrate integrity-host integrity-migrate
"${compose_command[@]}" up -d postgres integrity-postgres
"${compose_command[@]}" --profile migration run --rm payment-migrate </dev/null
"${compose_command[@]}" --profile migration run --rm integrity-migrate </dev/null
"${compose_command[@]}" up -d --no-deps --force-recreate integrity-host
wait_for_service_health integrity-host
"${compose_command[@]}" up -d --no-deps --force-recreate payment-service

wait_for_service_health payment-service
smoke_test_gateway_health "$dev_domain"
smoke_test_auth_jwks

printf 'PaymentService revision %s đã healthy; Gateway health và JWKS nội bộ đã pass.\n' "$release_sha"
