#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel 2>/dev/null)" || {
  printf 'Thiết lập Git hook: phải chạy bên trong Git worktree.\n' >&2
  exit 1
}

cd "$repository_root"

[ -x .githooks/pre-push ] || {
  printf 'Thiết lập Git hook: .githooks/pre-push phải có quyền thực thi.\n' >&2
  exit 1
}

[ -x scripts/pre-push-check.sh ] || {
  printf 'Thiết lập Git hook: scripts/pre-push-check.sh phải có quyền thực thi.\n' >&2
  exit 1
}

git config --local core.hooksPath .githooks
printf 'Đã bật Git hook cho clone này: %s\n' "$(git config --local --get core.hooksPath)"
