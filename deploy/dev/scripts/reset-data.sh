#!/usr/bin/env bash
# Chạy trên VPS. Preview chỉ đọc; execute dọn dữ liệu; restore phục hồi snapshot.
set -euo pipefail
umask 077

fail() { printf 'Reset: %s\n' "$*" >&2; exit 1; }

mode="--preview"
project="dprental-dev"
directory="${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}"
restore_backup=""
non_interactive=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --preview)
      mode="--preview"
      shift
      ;;
    --execute)
      mode="--execute"
      shift
      ;;
    --restore)
      mode="--restore"
      shift
      [[ $# -gt 0 ]] || fail "Thiếu đường dẫn thư mục backup cho --restore"
      restore_backup="$1"
      shift
      ;;
    --project)
      shift
      [[ $# -gt 0 ]] || fail "Thiếu tên project cho --project"
      project="$1"
      shift
      ;;
    --directory)
      shift
      [[ $# -gt 0 ]] || fail "Thiếu đường dẫn thư mục cho --directory"
      directory="$1"
      shift
      ;;
    --non-interactive)
      non_interactive=1
      shift
      ;;
    *)
      fail "Tham số không hợp lệ: $1. Cách dùng: reset-data.sh [--preview|--execute|--restore <backup>] [--project <name>] [--directory <dir>] [--non-interactive]"
      ;;
  esac
done

# Bổ sung cấu hình project dùng chung: mặc định dprental-dev, chỉ chấp nhận thêm project có tiền tố dprental-e2e-
if [[ -n "${DPR_COMPOSE_PROJECT:-}" ]]; then
  project="$DPR_COMPOSE_PROJECT"
fi
if [[ "$project" != "dprental-dev" && "$project" != dprental-e2e-* ]]; then
  fail "Project '$project' không hợp lệ. Chỉ chấp nhận 'dprental-dev' hoặc tiền tố 'dprental-e2e-'."
fi

[[ -d "$directory" ]] || fail "Không tìm thấy thư mục: $directory"
cd "$directory"
for file in compose.yaml .env release.env; do
  [[ -f "$file" ]] || fail "Không tìm thấy file: $file"
done

command -v python3 >/dev/null || fail 'Cần cài python3'
command -v flock >/dev/null || fail 'Cần cài flock'
command -v docker >/dev/null || fail 'Cần cài docker'

compose=(docker compose --project-name "$project" --project-directory "$directory" --file "$directory/compose.yaml" --env-file "$directory/.env" --env-file "$directory/release.env")

# Khóa .deployment.lock cho execute và restore từ trước khi resolve cấu hình đến khi nghiệm thu sau khởi động
if [[ "$mode" == "--execute" || "$mode" == "--restore" ]]; then
  exec 9>"$directory/.deployment.lock"
  flock -n 9 || fail "Deployment hoặc thao tác dữ liệu khác đang chạy (.deployment.lock bị khóa)"
fi

# Preflight: kiểm tra dung lượng đĩa khả dụng trước khi dừng stack
avail_mb=$(df -m "$directory" | awk 'NR==2 {print $4}')
if [[ "$mode" == "--execute" && $avail_mb -lt 2000 ]]; then
  fail "Dung lượng đĩa trống ($avail_mb MB) không đủ cho backup (yêu cầu tối thiểu 2000 MB)"
fi

# Parse resolved configuration without printing credentials or sourcing .env.
export DPR_PROJECT="$project"
plan_text=$("${compose[@]}" --profile migration config --format json | python3 -c '
import json, os, sys
from pathlib import Path

c = json.load(sys.stdin)
expected_project = os.environ.get("DPR_PROJECT", "dprental-dev")
if expected_project != "dprental-dev" and not expected_project.startswith("dprental-e2e-"):
    raise ValueError(f"Project Compose không hợp lệ: {expected_project}")
actual_name = c.get("name")
assert actual_name == expected_project, f"Project Compose không đúng: {actual_name} != {expected_project}"

allowed = {"postgres-data", "integrity-postgres-data", "media-minio-data", "valkey-data", "kafka-data", "meilisearch-data", "caddy-data", "caddy-config"}
unknown = set(c.get("volumes", {})) - allowed
if unknown:
    raise ValueError("Storage chưa phân loại: " + ", ".join(sorted(unknown)))

allowed_binds = {"Caddyfile", "firebase-service-account.json", "kafka-log4j2.yaml"}
for service_name, service in c.get("services", {}).items():
    for mount in service.get("volumes", []):
        kind = mount.get("type")
        if kind == "bind":
            source = Path(mount.get("source", ""))
            if source.name not in allowed_binds or not mount.get("read_only"):
                raise ValueError(f"Bind mount chưa phân loại hoặc ghi được trong {service_name}: {source}")
            if source.is_symlink() or not source.is_file():
                raise ValueError(f"Bind mount không phải regular file tin cậy: {source}")
        elif kind != "volume" or mount.get("source") not in allowed:
            raise ValueError(f"Mount chưa phân loại hoặc anonymous volume trong {service_name}")

for vol_name, volume in c.get("volumes", {}).items():
    if volume.get("driver", "local") != "local" or volume.get("driver_opts"):
        raise ValueError(f"Volume driver/options chưa được hỗ trợ: {vol_name}")

allowed_secrets = {"auth_jwt_private_key", "auth_jwt_public_key"}
for secret_name, secret_info in c.get("secrets", {}).items():
    if secret_name not in allowed_secrets:
        raise ValueError(f"Secret chưa phân loại: {secret_name}")
    sec_file = secret_info.get("file")
    if sec_file:
        sp = Path(sec_file)
        if sp.is_symlink() or not sp.is_file():
            raise ValueError(f"Secret file không phải regular file tin cậy: {sec_file}")

print(c["name"])
keys = ["postgres-data", "integrity-postgres-data", "media-minio-data", "valkey-data", "kafka-data"]
if "meilisearch" in c.get("services", {}):
    keys.append("meilisearch-data")
print(len(keys))
for key in keys:
    v = c["volumes"][key]
    assert not v.get("external"), "Không được dọn volume external"
    print(v["name"])
print(c["services"]["postgres"]["image"])

pending = {name for name in c.get("services", {}) if name.endswith("-migrate")}
ordered = []
while pending:
    ready = sorted(name for name in pending if not (set(c["services"][name].get("depends_on", {})) & pending))
    if not ready:
        raise ValueError("Migration dependencies có chu trình")
    ordered.extend(ready)
    pending.difference_update(ready)
for name in ordered:
    print(name)
') || fail 'Không resolve được kế hoạch Compose'

mapfile -t plan <<< "$plan_text"
[[ ${#plan[@]} -ge 8 && "${plan[0]}" == "$project" ]] || fail 'Không lập được kế hoạch dọn dữ liệu'
volume_count=${plan[1]}
[[ "$volume_count" == 5 || "$volume_count" == 6 ]] || fail "Số volume không hợp lệ"
volumes=("${plan[@]:2:volume_count}")
image=${plan[$((2 + volume_count))]}
migrations=("${plan[@]:$((3 + volume_count))}")

for volume in "${volumes[@]}"; do
  [[ "$volume" == "${project}_"* ]] || fail "Volume không thuộc kế hoạch: $volume"
  owner=$(docker volume inspect --format '{{index .Labels "com.docker.compose.project"}}' "$volume" 2>/dev/null || true)
  if [[ -n "$owner" && "$owner" != "$project" ]]; then
    fail "Nhãn chủ sở hữu volume không khớp: $volume ($owner != $project)"
  fi
done

# Preflight: kiểm tra image inspect postgres và migration images
docker image inspect "$image" >/dev/null 2>&1 || fail "Image $image không tồn tại hoặc không thể đọc"

# Preflight: kiểm tra quyền đọc các khóa được giữ lại
for key_file in Caddyfile firebase-service-account.json kafka-log4j2.yaml; do
  if [[ -f "$key_file" && ! -r "$key_file" ]]; then
    fail "Không có quyền đọc file cấu hình: $key_file"
  fi
done
if [[ -d "secrets" ]]; then
  for secret_file in secrets/*; do
    if [[ -f "$secret_file" && ! -r "$secret_file" ]]; then
      fail "Không có quyền đọc file khóa: $secret_file"
    fi
  done
fi

printf 'Project Compose: %s\n' "$project"
printf 'Thư mục: %s\n' "$directory"
printf 'Volume sẽ được dọn (%d): %s\n' "$volume_count" "${volumes[*]}"
printf 'Migration sẽ chạy lại (%d): %s\n' "${#migrations[@]}" "${migrations[*]}"
printf 'Toàn bộ tài khoản, dữ liệu nghiệp vụ và file upload sẽ được dọn. Bản sao lưu được giữ trên server.\n'

# ==============================================================================
# CHẾ ĐỘ RESTORE
# ==============================================================================
if [[ "$mode" == "--restore" ]]; then
  [[ -n "$restore_backup" && -d "$restore_backup" ]] || fail "Thư mục backup không tồn tại: $restore_backup"
  [[ -f "$restore_backup/SHA256SUMS" ]] || fail "Không tìm thấy SHA256SUMS trong: $restore_backup"
  printf 'Kiểm tra toàn vẹn bản sao lưu trước khi restore...\n'
  (cd "$restore_backup" && sha256sum --check SHA256SUMS >/dev/null) || fail "Checksum SHA256SUMS trong backup không khớp"

  if [[ $non_interactive -eq 0 ]]; then
    [[ -t 0 ]] || fail 'Thực thi restore cần terminal tương tác hoặc cờ --non-interactive'
    read -r -p "Nhập RESTORE $project để tiếp tục: " confirmation
    [[ "$confirmation" == "RESTORE $project" ]] || fail 'Đã hủy restore'
  fi

  printf 'Dừng toàn bộ stack trước khi khôi phục volume...\n'
  "${compose[@]}" --profile migration down --timeout 60 2>/dev/null || true

  for archive in "$restore_backup"/*.tar.gz; do
    [[ -f "$archive" ]] || continue
    vol_name=$(basename "$archive" .tar.gz)
    printf 'Khôi phục volume: %s\n' "$vol_name"
    docker volume rm "$vol_name" 2>/dev/null || true
    docker volume create --name "$vol_name" --label "com.docker.compose.project=$project" >/dev/null
    docker run --rm --network none --user 0 --entrypoint tar \
      --mount "type=volume,src=$vol_name,dst=/dest" \
      -i "$image" -C /dest -xzf - < "$archive"
  done
    # Đảm bảo quyền sở hữu 1000:1000 cho kafka-data
    docker volume create --name "${project}_kafka-data" --label "com.docker.compose.project=$project" >/dev/null 2>&1 || true
    docker run --rm -v "${project}_kafka-data:/data" "$image" chown -R 1000:1000 /data 2>/dev/null || true

  # Khôi phục file cấu hình mount nếu có
  if [[ -d "$restore_backup/mounted-config" ]]; then
    for cfg in "$restore_backup/mounted-config"/*; do
      [[ -f "$cfg" ]] || continue
      cp -p "$cfg" "$directory/"
    done
  fi

  storage=(postgres integrity-postgres valkey kafka media-minio)
  [[ "$volume_count" == 5 ]] || storage+=(meilisearch)

  printf 'Khởi động storage services...\n'
  "${compose[@]}" up -d --wait --wait-timeout 180 "${storage[@]}"

  # Khôi phục Meilisearch key config nếu có
  if [[ -f "$restore_backup/meilisearch-key-config.json" ]]; then
    python3 -c '
import datetime, json, pathlib, subprocess, sys, urllib.request, urllib.error
project = sys.argv[1]
backup_file = pathlib.Path(sys.argv[2])
ids = subprocess.check_output(["docker", "ps", "-q", "--filter", f"label=com.docker.compose.project={project}", "--filter", "label=com.docker.compose.service=meilisearch"], text=True).split()
if not ids:
    sys.exit(0)
container = json.loads(subprocess.check_output(["docker", "inspect", ids[0]], text=True))[0]
address = next(network["IPAddress"] for network in container["NetworkSettings"]["Networks"].values() if network["IPAddress"])
conf = json.loads(subprocess.check_output(["docker", "compose", "--project-name", project, "config", "--format", "json"], text=True))
master = conf["services"]["meilisearch"]["environment"]["MEILI_MASTER_KEY"]

def request(path, payload=None, key=master):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request("http://" + address + ":7700" + path, data=data,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as response:
        return json.load(response)

for item in json.loads(backup_file.read_text(encoding="utf-8")):
    expiry = item.get("expiresAt")
    if expiry and datetime.datetime.fromisoformat(expiry.replace("Z", "+00:00")) <= datetime.datetime.now(datetime.timezone.utc):
        continue
    try:
        request("/keys/" + item["uid"])
    except urllib.error.HTTPError as error:
        if error.code == 404:
            request("/keys", item)
print("Đã khôi phục Meilisearch API keys từ backup.")
' "$project" "$restore_backup/meilisearch-key-config.json"
  fi

  printf 'Khởi động toàn bộ ứng dụng sau restore...\n'
  "${compose[@]}" up -d --wait --wait-timeout 240
  "${compose[@]}" ps
  printf 'Restore hoàn tất thành công từ bản sao lưu: %s\n' "$restore_backup"
  exit 0
fi

# ==============================================================================
# CHẾ ĐỘ PREVIEW
# ==============================================================================
if [[ "$mode" == "--preview" ]]; then
  printf 'Chế độ --preview: Kế hoạch hợp lệ, không thực hiện thay đổi dữ liệu.\n'
  exit 0
fi

# ==============================================================================
# CHẾ ĐỘ EXECUTE
# ==============================================================================
if [[ $non_interactive -eq 0 ]]; then
  [[ -t 0 ]] || fail 'Thực thi cần terminal tương tác hoặc cờ --non-interactive'
  read -r -p "Nhập RESET $project để tiếp tục: " confirmation
  [[ "$confirmation" == "RESET $project" ]] || fail 'Đã hủy'
fi

mkdir -p "$directory/backups"
backup=$(mktemp -d "$directory/backups/reset-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")
chmod 0700 "$backup"
trap 'printf "Dọn dữ liệu thất bại (dòng %s). Stack có thể đang dừng hoặc mới dọn một phần. Bản sao lưu: %s. Không khởi động dịch vụ ghi trước khi khôi phục.\n" "$LINENO" "$backup" >&2' ERR

stage_log() {
  printf '[%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >> "$backup/stage.log"
}
stage_log "BẮT ĐẦU RESET: project=$project directory=$directory"

meili_keys() {
  "${compose[@]}" config --format json | python3 -c '
import datetime, json, pathlib, subprocess, sys, urllib.request, urllib.error
project = sys.argv[1]
action = sys.argv[2]
backup_dir = pathlib.Path(sys.argv[3])
configuration = json.load(sys.stdin)
if "meilisearch" not in configuration.get("services", {}):
    sys.exit(0)

ids = subprocess.check_output(["docker", "ps", "-q", "--filter", f"label=com.docker.compose.project={project}", "--filter", "label=com.docker.compose.service=meilisearch"], text=True).split()
if not ids:
    sys.exit(0)

container = json.loads(subprocess.check_output(["docker", "inspect", ids[0]], text=True))[0]
address = next(network["IPAddress"] for network in container["NetworkSettings"]["Networks"].values() if network["IPAddress"])
master = configuration["services"]["meilisearch"]["environment"]["MEILI_MASTER_KEY"]

def request(path, payload=None, key=master):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request("http://" + address + ":7700" + path, data=data,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as response:
        return json.load(response)

path = backup_dir / "meilisearch-key-config.json"
if action == "save":
    keys, offset = [], 0
    while True:
        page = request("/keys?limit=1000&offset=" + str(offset))["results"]
        keys.extend({name: item[name] for name in ("uid", "name", "description", "actions", "indexes", "expiresAt")} for item in page)
        if len(page) < 1000:
            break
        offset += len(page)
    path.write_text(json.dumps(keys), encoding="utf-8")
    path.chmod(0o600)
    print("Đã sao lưu cấu hình Meilisearch API keys (không in secret).")
else:
    for item in json.loads(path.read_text(encoding="utf-8")):
        expiry = item.get("expiresAt")
        if expiry and datetime.datetime.fromisoformat(expiry.replace("Z", "+00:00")) <= datetime.datetime.now(datetime.timezone.utc):
            continue
        try:
            existing = request("/keys/" + item["uid"])
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            request("/keys", item)
        else:
            assert existing["actions"] == item["actions"] and existing["indexes"] == item["indexes"], "API key permissions differ"
    inv_key = configuration.get("services", {}).get("inventory-service", {}).get("environment", {}).get("Meilisearch__ApiKey")
    if inv_key:
        try:
            request("/indexes", key=inv_key)
            print("Đã khôi phục API keys; key Inventory trong .env vẫn hợp lệ.")
        except urllib.error.HTTPError as error:
            if error.code in (401, 403):
                print("Lưu ý: Key Inventory hiện tại chưa khớp; Meilisearch đang dùng default keys.")
            else:
                raise
' "$project" "$1" "$backup"
}

# 1. Chặn truy cập ghi từ bên ngoài, dừng writers trước snapshot trong khi Meilisearch còn hoạt động
stage_log "DỪNG WRITERS: ngăn ghi ngoài trước snapshot"
"${compose[@]}" stop auth-service media-service catalog-service inventory-service rental-service payment-service analytics-service recommendation-service frontend 2>/dev/null || true

# 2. Xuất cấu hình Meilisearch keys trong giai đoạn đã ngăn writers nhưng Meilisearch còn hoạt động
stage_log "XUẤT MEILISEARCH KEYS"
meili_keys save

# 3. Dừng toàn bộ stack (kể cả storage) trước snapshot filesystem đồng bộ
stage_log "DỪNG STORAGE"
"${compose[@]}" --profile migration stop --timeout 60

# 4. Sao lưu đồng bộ các data volume
stage_log "SAO LƯU VOLUMES"
for volume in "${volumes[@]}"; do
  consumers=$(docker ps -q --filter "volume=$volume")
  [[ -z "$consumers" ]] || fail "Volume vẫn đang được sử dụng: $volume; stack được giữ ở trạng thái dừng"
  docker run --rm --network none --read-only --user 0 --entrypoint tar \
    --mount "type=volume,src=$volume,dst=/source,readonly" \
    "$image" -C /source -czf - . > "$backup/$volume.tar.gz"
  tar -tzf "$backup/$volume.tar.gz" >/dev/null
done

# 5. Sao lưu file cấu hình triển khai và mounted configs
stage_log "SAO LƯU CONFIGS"
cp -p compose.yaml .env release.env "$backup/"
mkdir -p "$backup/mounted-config"
for configuration in Caddyfile firebase-service-account.json kafka-log4j2.yaml; do
  [[ ! -f "$configuration" ]] || cp -p "$configuration" "$backup/mounted-config/"
done
if [[ -d "secrets" ]]; then
  mkdir -p "$backup/secrets"
  cp -p secrets/* "$backup/secrets/" 2>/dev/null || true
fi

# Chụp manifest metadata
python3 -c '
import json, pathlib, subprocess, sys
backup_dir = pathlib.Path(sys.argv[1])
project = sys.argv[2]
manifest = {
    "project": project,
    "timestamp": subprocess.check_output(["date", "-u", "+%Y-%m-%dT%H:%M:%SZ"], text=True).strip(),
    "volumes": sys.argv[3].split(),
    "migrations": sys.argv[4].split(),
    "images": subprocess.check_output(["docker", "ps", "-a", "--filter", f"label=com.docker.compose.project={project}", "--format", "{{.Names}}\t{{.Image}}"], text=True).strip().splitlines()
}
try:
    manifest["gitRevision"] = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
except Exception:
    manifest["gitRevision"] = "none"
(backup_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
' "$backup" "$project" "${volumes[*]}" "${migrations[*]}"

find "$backup" -type f ! -name SHA256SUMS -exec sha256sum {} + > "$backup/SHA256SUMS"
sha256sum --check "$backup/SHA256SUMS" >/dev/null
printf 'Đã kiểm tra toàn vẹn bản sao lưu: %s\n' "$backup"
stage_log "KIỂM TRA BẢN SAO LƯU THÀNH CÔNG: $backup"

# 6. Hạ stack và xóa các data volume đã xác định (không dùng down -v hoặc prune)
stage_log "XÓA DATA VOLUMES"
"${compose[@]}" --profile migration down --timeout 60
docker volume rm "${volumes[@]}"

# 7. Khởi động storage và khôi phục Meilisearch keys
stage_log "KHỞI ĐỘNG STORAGE"
  # Đảm bảo quyền sở hữu 1000:1000 cho kafka-data
  docker volume create --name "${project}_kafka-data" --label "com.docker.compose.project=$project" >/dev/null 2>&1 || true
  docker run --rm -v "${project}_kafka-data:/data" "$image" chown -R 1000:1000 /data 2>/dev/null || true
storage=(postgres integrity-postgres valkey kafka media-minio)
[[ "$volume_count" == 5 ]] || storage+=(meilisearch)
"${compose[@]}" up -d --wait --wait-timeout 180 "${storage[@]}"

stage_log "KHÔI PHỤC MEILISEARCH KEYS"
meili_keys restore

# 8. Chạy initialization và migrations theo thứ tự phụ thuộc
stage_log "CHẠY MINIO INIT"
"${compose[@]}" run --rm --no-deps media-minio-init

stage_log "KHỞI TẠO SCHEMAS POSTGRES"
"${compose[@]}" exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS catalog;
CREATE SCHEMA IF NOT EXISTS inventory;
CREATE SCHEMA IF NOT EXISTS media;
CREATE SCHEMA IF NOT EXISTS notification;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS recommendation;
CREATE SCHEMA IF NOT EXISTS rental;
"' 2>/dev/null || true

stage_log "CHẠY MIGRATIONS"
for migration in "${migrations[@]}"; do
  printf 'Chạy migration: %s\n' "$migration"
  "${compose[@]}" --profile migration run --rm --no-deps "$migration"
done

# 9. Khởi động toàn bộ ứng dụng và kiểm tra readiness
stage_log "KHỞI ĐỘNG ỨNG DỤNG"
"${compose[@]}" up -d --wait --wait-timeout 240
"${compose[@]}" ps

stage_log "HOÀN TẤT RESET THÀNH CÔNG"
printf 'Đã dọn dữ liệu; kiểm tra khởi động Compose đã qua. Bản sao lưu: %s\nTạo lại admin qua API bootstrap hiện hữu.\n' "$backup"
