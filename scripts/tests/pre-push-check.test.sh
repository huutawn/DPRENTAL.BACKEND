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
  rg --fixed-strings --quiet "$expected" "$file" || fail "không tìm thấy '$expected' trong $file"
}

remote_repository="$temporary_directory/remote.git"
seed_repository="$temporary_directory/seed"
work_repository="$temporary_directory/work"
fake_bin="$temporary_directory/bin"
dotnet_log="$temporary_directory/dotnet.log"

mkdir "$fake_bin"
git init --bare --initial-branch=auth/develop "$remote_repository" >/dev/null
git init --initial-branch=auth/develop "$seed_repository" >/dev/null
git -C "$seed_repository" config user.email test@example.invalid
git -C "$seed_repository" config user.name 'Pre-push test'
touch "$seed_repository/baseline"
git -C "$seed_repository" add baseline
git -C "$seed_repository" commit -m baseline >/dev/null
git -C "$seed_repository" remote add origin "$remote_repository"
git -C "$seed_repository" push --quiet --set-upstream origin auth/develop
git clone --quiet "$remote_repository" "$work_repository"
git -C "$work_repository" checkout --quiet -b feature origin/auth/develop

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

touch "$seed_repository/latest-base"
git -C "$seed_repository" add latest-base
git -C "$seed_repository" commit -m latest-base >/dev/null
git -C "$seed_repository" push --quiet

if (
  cd "$work_repository"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" "$validator"
); then
  fail 'branch chưa đồng bộ lại được cho qua.'
fi
[ ! -e "$dotnet_log" ] || fail 'build hoặc test đã chạy khi branch chưa đồng bộ.'

git -C "$work_repository" merge --no-edit origin/auth/develop >/dev/null
(
  cd "$work_repository"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" "$validator"
) >/dev/null
assert_contains 'build AuthService.sln --configuration Release --nologo' "$dotnet_log"
assert_contains 'test AuthService.sln --configuration Release --no-build --nologo' "$dotnet_log"

if (
  cd "$work_repository"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" DOTNET_FAIL=build "$validator"
) >/dev/null 2>&1; then
  fail 'build thất bại lại được cho qua.'
fi

if (
  cd "$work_repository"
  PATH="$fake_bin:$PATH" DOTNET_LOG="$dotnet_log" DOTNET_FAIL=test "$validator"
) >/dev/null 2>&1; then
  fail 'test thất bại lại được cho qua.'
fi

printf 'Đã đạt regression test cho kiểm tra trước khi push.\n'
