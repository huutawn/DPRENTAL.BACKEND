#!/usr/bin/env bash

set -euo pipefail

readonly BASE_REMOTE="origin"
readonly BASE_BRANCH="gateway/develop"
readonly BASE_REF="refs/remotes/${BASE_REMOTE}/${BASE_BRANCH}"
readonly SOLUTION_FILE="Gateway/Gateway.sln"

fail() {
  printf 'Kiểm tra trước khi push: %s\n' "$*" >&2
  exit 1
}

repository_root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail "phải chạy bên trong Git worktree."
cd "$repository_root"

git remote get-url "$BASE_REMOTE" >/dev/null 2>&1 || fail "chưa cấu hình remote '${BASE_REMOTE}'."

printf 'Đang fetch %s/%s...\n' "$BASE_REMOTE" "$BASE_BRANCH"
git fetch --no-tags "$BASE_REMOTE" "+refs/heads/${BASE_BRANCH}:${BASE_REF}" || \
  fail "không thể fetch ${BASE_REMOTE}/${BASE_BRANCH}; push đã bị chặn."

git rev-parse --verify --quiet "${BASE_REF}^{commit}" >/dev/null || \
  fail "không fetch được base ref ${BASE_REMOTE}/${BASE_BRANCH}; push đã bị chặn."

if git merge-base --is-ancestor "$BASE_REF" HEAD; then
  :
else
  merge_base_status=$?
  if [ "$merge_base_status" -eq 1 ]; then
    fail "branch hiện tại chưa chứa ${BASE_REMOTE}/${BASE_BRANCH} mới nhất. Hãy merge hoặc rebase rồi thử lại."
  fi

  fail "không thể so sánh HEAD với ${BASE_REMOTE}/${BASE_BRANCH}; push đã bị chặn."
fi

printf 'Đang build %s (Release)...\n' "$SOLUTION_FILE"
dotnet build "$SOLUTION_FILE" --configuration Release --nologo || fail "build thất bại; push đã bị chặn."

printf 'Đang chạy test %s (Release)...\n' "$SOLUTION_FILE"
dotnet test "$SOLUTION_FILE" --configuration Release --no-build --nologo || fail "test thất bại; push đã bị chặn."

printf 'Đã đạt toàn bộ kiểm tra trước khi push.\n'