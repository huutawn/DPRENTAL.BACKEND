#!/usr/bin/env bash
# Shared release transaction; installed beside the reviewed service scripts.
# shellcheck disable=SC2034,SC2154

deployment_phase() {
  phase="$1"
  printf '%s\n' "$phase" > "$attempt_directory/phase"
  printf '%s %s\n' "$(date -u +%FT%TZ)" "$phase" >> "$attempt_directory/events"
}

cleanup_deployment() {
  local result=$?
  trap - EXIT
  rm -rf "${docker_config_dir:-}"
  if [ "$result" -ne 0 ]; then
    printf 'failed:%s\n' "$phase" > "$attempt_directory/result"
    if [ "$phase" = preflight ]; then
      rm -f "$DEPLOY_DIRECTORY/.deployment-pending"
    fi
    printf 'Deployment failed at %s; journal: %s. No automatic rollback.\n' "$phase" "$attempt_directory" >&2
  fi
  exit "$result"
}

begin_deployment() {
  umask 077
  [[ "${GHCR_IMAGE_NAMESPACE:-}" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || fail 'GHCR_IMAGE_NAMESPACE không hợp lệ.'
  [ -n "${GHCR_USERNAME:-}" ] && [ -n "${GHCR_TOKEN:-}" ] || fail 'thiếu GHCR credentials.'
  exec 9> "$DEPLOY_DIRECTORY/.deployment.lock"
  flock -w 600 9 || fail 'timeout chờ deployment lock.'
  [ ! -e "$DEPLOY_DIRECTORY/.deployment-pending" ] || fail 'deployment trước chưa được xử lý; xem .deployment-pending.'
  install -d -m 0700 "$DEPLOY_DIRECTORY/.deployments"
  attempt_directory="$(mktemp -d "$DEPLOY_DIRECTORY/.deployments/$1-$release_sha.XXXXXX")"
  phase=preflight
  docker_config_dir=''
  trap cleanup_deployment EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  cp "$RELEASE_FILE" "$attempt_directory/previous.env"
  candidate_file="$attempt_directory/candidate.env"
  cp "$RELEASE_FILE" "$candidate_file"
  printf '%s\n' "$attempt_directory" > "$DEPLOY_DIRECTORY/.deployment-pending"
  deployment_phase preflight
  docker_config_dir="$(mktemp -d)"
  export DOCKER_CONFIG="$docker_config_dir"
  docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin <<< "$GHCR_TOKEN" >/dev/null
  unset GHCR_TOKEN
  compose_command=(docker compose --project-name dprental-dev --project-directory "$DEPLOY_DIRECTORY" --file "$COMPOSE_FILE" --env-file "$ENVIRONMENT_FILE" --env-file "$candidate_file")
}

prepare_release() {
  local mapping key repository current temporary_file
  for mapping in "$@"; do
    key="${mapping%%=*}"
    repository="ghcr.io/$GHCR_IMAGE_NAMESPACE/${mapping#*=}"
    [ "$(grep -c "^$key=" "$RELEASE_FILE")" = 1 ] || fail "release.env phải có đúng một $key."
    current="$(read_release_value "$key")"
    [ "${current%:*}" = "$repository" ] && [[ "${current##*:}" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.-]*$ ]] || fail "image mapping không hợp lệ: $key."
    temporary_file="$candidate_file.next"
    awk -v key="$key" -v value="$repository:$release_sha" \
      'index($0, key "=") == 1 { print key "=" value; next } { print }' \
      "$candidate_file" > "$temporary_file"
    mv "$temporary_file" "$candidate_file"
  done
  "${compose_command[@]}" --profile migration config --quiet
}

require_dependencies() {
  local dependency container status
  for dependency in "$@"; do
    container="$("${compose_command[@]}" ps -q "$dependency")"
    [ -n "$container" ] || fail "dependency $dependency chưa chạy; không tự restart dependency."
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")"
    [[ "$status" = healthy || "$status" = running ]] || fail "dependency $dependency chưa sẵn sàng."
  done
}

require_completed_dependency() {
  local container status
  container="$("${compose_command[@]}" ps --all -q "$1")"
  [ -n "$container" ] || fail "initializer $1 chưa tồn tại."
  status="$(docker inspect --format '{{.State.Status}}:{{.State.ExitCode}}' "$container")"
  [ "$status" = exited:0 ] || fail "initializer $1 chưa hoàn tất thành công."
}

backup_database() {
  local database_service="$1" backup_file
  backup_file="$attempt_directory/$database_service.dump"
  # Expand database credentials inside the container, not the host shell.
  # shellcheck disable=SC2016
  "${compose_command[@]}" exec -T "$database_service" sh -c \
    'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' </dev/null > "$backup_file" || fail "backup $database_service thất bại."
  [ -s "$backup_file" ] || fail "backup $database_service rỗng."
  # Validate the archive header/TOC before any migration. Restore rehearsal is separate.
  "${compose_command[@]}" exec -T "$database_service" pg_restore --list < "$backup_file" >/dev/null || fail 'backup archive không hợp lệ.'
}

public_curl() {
  local code
  if curl "$@"; then return 0; else code=$?; fi
  case "$code" in
    5|6) printf 'Public smoke: DNS/proxy resolution failure; application health is separate.\n' >&2 ;;
    7|28|35|60) printf 'Public smoke: network/TLS/timeout; inspect Tunnel/Caddy and internal readiness.\n' >&2 ;;
    *) printf 'Public smoke: HTTP/upstream failure; inspect Tunnel/Caddy and Gateway logs.\n' >&2 ;;
  esac
  return "$code"
}

commit_release() {
  cmp -s "$RELEASE_FILE" "$attempt_directory/previous.env" || fail 'release.env đã bị writer khác sửa; cần recovery.'
  deployment_phase committing
  # Both paths are on the deployment filesystem, so rename is atomic.
  cp "$candidate_file" "$DEPLOY_DIRECTORY/.release-success.tmp"
  chmod 0600 "$DEPLOY_DIRECTORY/.release-success.tmp"
  mv "$DEPLOY_DIRECTORY/.release-success.tmp" "$RELEASE_FILE"
  printf 'success\n' > "$attempt_directory/result"
  deployment_phase success
  rm "$DEPLOY_DIRECTORY/.deployment-pending"
}
