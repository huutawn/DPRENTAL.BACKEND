#!/usr/bin/env bash

set -euo pipefail

readonly DEPLOY_DIRECTORY="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
readonly ENVIRONMENT_FILE="$DEPLOY_DIRECTORY/.env"
readonly RELEASE_FILE="$DEPLOY_DIRECTORY/release.env"
readonly COMPOSE_FILE="$DEPLOY_DIRECTORY/compose.yaml"
readonly HEALTH_ATTEMPTS="${ANALYTICS_DEPLOY_HEALTH_ATTEMPTS:-18}"
readonly HEALTH_INTERVAL_SECONDS="${ANALYTICS_DEPLOY_HEALTH_INTERVAL_SECONDS:-5}"
readonly HTTP_TIMEOUT_SECONDS="${ANALYTICS_DEPLOY_HTTP_TIMEOUT_SECONDS:-15}"

fail() {
  printf 'Triển khai AnalyticsService: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Cách dùng: %s <full-git-sha>\n' "${0##*/}" >&2
  exit 2
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

  line="$(grep -E "^${variable_name}=" "$ENVIRONMENT_FILE" | tail -n 1)" || \
    fail ".env chưa có ${variable_name}."
  printf '%s\n' "${line#*=}"
}

wait_for_analytics_health() {
  local container_id
  local health_status
  local attempt

  for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
    container_id="$("${compose_command[@]}" ps -q analytics-service)"
    if [ -n "$container_id" ]; then
      health_status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container_id")"
      if [ "$health_status" = healthy ]; then
        return 0
      fi
      printf 'Đang chờ AnalyticsService healthy (%s/%s, trạng thái: %s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS" "$health_status"
    else
      printf 'Đang chờ container AnalyticsService được tạo (%s/%s)...\n' \
        "$attempt" "$HEALTH_ATTEMPTS"
    fi
    sleep "$HEALTH_INTERVAL_SECONDS"
  done

  fail 'AnalyticsService không đạt trạng thái healthy; không tự rollback schema hoặc image.'
}

smoke_test_gateway_health() {
  local domain="$1"

  public_curl --fail --silent --show-error --max-time "$HTTP_TIMEOUT_SECONDS" \
    "https://${domain}/health/ready" >/dev/null || \
    fail 'health endpoint công khai qua Gateway thất bại; không tự rollback schema hoặc image.'
}

smoke_test_analytics_readiness() {
  # Perl variables must reach Perl unchanged.
  # shellcheck disable=SC2016
  "${compose_command[@]}" exec -T analytics-service \
    perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' </dev/null || \
    fail 'readiness nội bộ của AnalyticsService thất bại; không tự rollback schema hoặc image.'
}

# shellcheck source=scripts/deploy-common.sh
source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

[ "$#" -le 1 ] || usage
release_sha="${1:-${GITHUB_SHA:-}}"
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || fail 'release SHA phải là commit SHA gồm đúng 40 ký tự hex thường.'

require_file "$ENVIRONMENT_FILE"
require_file "$RELEASE_FILE"
require_file "$COMPOSE_FILE"
begin_deployment analytics

dev_domain="$(read_environment_value DEV_DOMAIN)"

printf 'Đang phát hành AnalyticsService revision %s...\n' "$release_sha"
prepare_release ANALYTICS_SERVICE_IMAGE=dprental-analytics ANALYTICS_MIGRATOR_IMAGE=dprental-analytics-migrator

"${compose_command[@]}" --profile migration pull analytics-service analytics-migrate
require_dependencies postgres auth-service kafka
deployment_phase mutation
backup_database postgres

deployment_phase migration
"${compose_command[@]}" --profile migration run --rm --no-deps --pull never analytics-migrate </dev/null
deployment_phase runtime
"${compose_command[@]}" up -d --no-deps --force-recreate --pull never --no-build analytics-service

deployment_phase health
wait_for_analytics_health
"${compose_command[@]}" exec -T gateway wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready </dev/null
deployment_phase public-smoke
smoke_test_gateway_health "$dev_domain"
smoke_test_analytics_readiness

commit_release

printf 'AnalyticsService revision %s đã healthy; Gateway health và readiness nội bộ đã pass.\n' "$release_sha"
