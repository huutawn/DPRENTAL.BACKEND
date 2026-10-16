#!/usr/bin/env bash

set -euo pipefail

readonly BASE_REMOTE="origin"
readonly BASE_BRANCH="payment/develop"
readonly BASE_REF="refs/remotes/${BASE_REMOTE}/${BASE_BRANCH}"
readonly SOLUTION_FILE="PaymentService.sln"

fail() {
  printf 'Kiểm tra trước khi push: %s\n' "$*" >&2
  exit 1
}

repository_root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail "phải chạy bên trong Git worktree."
cd "$repository_root"

git remote get-url "$BASE_REMOTE" >/dev/null 2>&1 || fail "chưa cấu hình remote '${BASE_REMOTE}'."

# Kiểm tra xem branch origin/payment/develop đã tồn tại trên remote chưa
if git ls-remote --exit-code --heads "$BASE_REMOTE" "$BASE_BRANCH" >/dev/null 2>&1; then
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
else
  remote_status=$?
  if [ "$remote_status" -ne 2 ]; then
    fail "không thể kiểm tra ${BASE_REMOTE}/${BASE_BRANCH} (git ls-remote trả về ${remote_status}); push đã bị chặn."
  fi
  printf 'Branch %s/%s chưa tồn tại trên remote; bỏ qua kiểm tra ancestry cho lần push đầu tiên.\n' "$BASE_REMOTE" "$BASE_BRANCH"
fi

printf 'Đang build %s (Release)...\n' "$SOLUTION_FILE"
dotnet build "$SOLUTION_FILE" --configuration Release --nologo || fail "build thất bại; push đã bị chặn."

printf 'Đang kiểm tra pending EF model changes...\n'
if dotnet dotnet-ef --version >/dev/null 2>&1; then
  ef_command=(dotnet dotnet-ef)
elif command -v dotnet-ef >/dev/null 2>&1; then
  ef_command=(dotnet-ef)
elif [ -x "${HOME}/.dotnet/tools/dotnet-ef.exe" ]; then
  ef_command=("${HOME}/.dotnet/tools/dotnet-ef.exe")
else
  fail "không tìm thấy dotnet-ef; hãy chạy 'dotnet tool restore' trước khi push."
fi

"${ef_command[@]}" migrations has-pending-model-changes --project PaymentService/src/Domain/PaymentService.Domain.csproj --startup-project PaymentService/src/Api/PaymentService.Api.csproj --configuration Release --no-build || fail "kiểm tra EF model thất bại hoặc còn model changes chưa được tạo migration; push đã bị chặn."

printf 'Đang chạy test %s (Release)...\n' "$SOLUTION_FILE"
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test "$SOLUTION_FILE" --configuration Release --no-build --nologo || fail "test thất bại; push đã bị chặn."

printf 'Đã đạt toàn bộ kiểm tra trước khi push.\n'
