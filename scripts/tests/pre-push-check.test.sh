#!/usr/bin/env bash

set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
validator="$repository_root/scripts/pre-push-check.sh"
temporary_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
  printf 'Test kiểm tra trước khi push: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  local expected="$1"
  local file="$2"
  grep -Fq "$expected" "$file" || fail "không tìm thấy '$expected' trong $file"
}

fake_bin="$temporary_directory/bin"
mkdir -p "$fake_bin"

cat > "$fake_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOTNET_LOG"

case "${1:-}" in
  build)
    [ "${DOTNET_FAIL:-}" != build ] || exit 31
    ;;
  test)
    [ "${DOTNET_FAIL:-}" != test ] || exit 32
    ;;
esac
EOF
chmod +x "$fake_bin/dotnet"

# -------------------------------------------------------------
# Test 1: Branch chưa đồng bộ với remote (ancestor check thất bại)
# -------------------------------------------------------------
remote_repo="$temporary_directory/remote.git"
seed_repo="$temporary_directory/seed"
work_repo="$temporary_directory/work"
dotnet_log="$temporary_directory/dotnet.log"

git init --bare --initial-branch=media/develop "$remote_repo" >/dev/null
git init --initial-branch=media/develop "$seed_repo" >/dev/null
git -C "$seed_repo" config user.email test@example.invalid
git -C "$seed_repo" config user.name 'Pre-push test'
touch "$seed_repo/baseline"
git -C "$seed_repo" add baseline
git -C "$seed_repo" commit -m baseline >/dev/null
git -C "$seed_repo" remote add origin "$remote_repo"
git -C "$seed_repo" push --quiet --set-upstream origin media/develop

git clone --quiet "$remote_repo" "$work_repo"
git -C "$work_repo" checkout --quiet -b feature origin/media/develop

# Tạo commit mới trên seed và push lên remote để work_repo bị outdated
touch "$seed_repo/latest-base"
git -C "$seed_repo" add latest-base
git -C "$seed_repo" commit -m latest-base >/dev/null
git -C "$seed_repo" push --quiet

if (
  cd "$work_repo"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" "$validator"
); then
  fail 'branch chưa đồng bộ lại được cho qua.'
fi
[ ! -e "$dotnet_log" ] || fail 'build hoặc test đã chạy khi branch chưa đồng bộ.'

# -------------------------------------------------------------
# Test 2: Lần push đầu tiên (branch media/develop chưa tồn tại trên remote)
# -------------------------------------------------------------
first_push_remote="$temporary_directory/first_push_remote.git"
first_push_work="$temporary_directory/first_push_work"
first_push_log="$temporary_directory/first_push.log"

git init --bare "$first_push_remote" >/dev/null
git init --initial-branch=media/develop "$first_push_work" >/dev/null
git -C "$first_push_work" config user.email test@example.invalid
git -C "$first_push_work" config user.name 'First push test'
touch "$first_push_work/initial"
git -C "$first_push_work" add initial
git -C "$first_push_work" commit -m "initial commit" >/dev/null
git -C "$first_push_work" remote add origin "$first_push_remote"

(
  cd "$first_push_work"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$first_push_log" "$validator"
) >/dev/null

assert_contains 'build MediaService.sln --configuration Release --nologo' "$first_push_log"
assert_contains 'test MediaService.sln --configuration Release --no-build --nologo' "$first_push_log"

# -------------------------------------------------------------
# Test 3: Branch đồng bộ hoàn toàn (pass kiểm tra ancestry và build/test)
# -------------------------------------------------------------
git -C "$work_repo" merge --no-edit origin/media/develop >/dev/null
(
  cd "$work_repo"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" "$validator"
) >/dev/null

assert_contains 'build MediaService.sln --configuration Release --nologo' "$dotnet_log"
assert_contains 'test MediaService.sln --configuration Release --no-build --nologo' "$dotnet_log"

# -------------------------------------------------------------
# Test 4: Build thất bại
# -------------------------------------------------------------
if (
  cd "$work_repo"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" DOTNET_FAIL=build "$validator"
) >/dev/null 2>&1; then
  fail 'build thất bại lại được cho qua.'
fi

# -------------------------------------------------------------
# Test 5: Test thất bại
# -------------------------------------------------------------
if (
  cd "$work_repo"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" DOTNET_FAIL=test "$validator"
) >/dev/null 2>&1; then
  fail 'test thất bại lại được cho qua.'
fi

printf 'Đã đạt toàn bộ kiểm thử cho script pre-push-check.sh.\n'
