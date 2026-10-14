#!/usr/bin/env bash
# Chạy trên VPS. Preview chỉ đọc; execute dọn dữ liệu của project Compose này.
set -euo pipefail
umask 077

fail() { printf 'Reset: %s\n' "$*" >&2; exit 1; }
mode=${1:---preview}
[[ $# -le 1 && ( "$mode" == --preview || "$mode" == --execute ) ]] || fail 'Cách dùng: reset-data.sh [--preview|--execute]'
directory=${DPR_DEPLOY_DIRECTORY:-/opt/dprental/dev}
[[ -d "$directory" ]] || fail "Không tìm thấy thư mục: $directory"
cd "$directory"
for file in compose.yaml .env release.env; do
  [[ -f "$file" ]] || fail "Không tìm thấy file: $file"
done
command -v python3 >/dev/null || fail 'Cần cài python3'
command -v flock >/dev/null || fail 'Cần cài flock'
compose=(docker compose --project-directory "$directory" --file "$directory/compose.yaml" --env-file "$directory/.env" --env-file "$directory/release.env")
# Parse resolved configuration without printing credentials or sourcing .env.
mapfile -t plan < <("${compose[@]}" --profile migration config --format json | python3 -c '
import json, sys
c = json.load(sys.stdin)
assert c["name"] == "dprental-dev", "Project Compose không đúng"
print(c["name"])
keys = ["postgres-data", "media-minio-data", "valkey-data", "kafka-data"]
if "meilisearch" in c["services"]:
    keys.append("meilisearch-data")
print(len(keys))
for key in keys:
    v = c["volumes"][key]
    assert not v.get("external"), "Không được dọn volume external"
    print(v["name"])
print(c["services"]["postgres"]["image"])
for service in ("auth", "media", "catalog", "inventory", "rental", "payment"):
    name = service + "-migrate"
    if name in c["services"]:
        print(name)
')
[[ ${#plan[@]} -ge 8 && "${plan[0]}" == dprental-dev ]] || fail 'Không lập được kế hoạch dọn dữ liệu'
volume_count=${plan[1]}
[[ "$volume_count" == 4 || "$volume_count" == 5 ]] || fail "Số volume không hợp lệ"
volumes=("${plan[@]:2:volume_count}")
image=${plan[$((2 + volume_count))]}
migrations=("${plan[@]:$((3 + volume_count))}")
for volume in "${volumes[@]}"; do
  [[ "$volume" == dprental-dev_* ]] || fail "Volume không thuộc kế hoạch: $volume"
  owner=$(docker volume inspect --format '{{index .Labels "com.docker.compose.project"}}' "$volume")
  [[ "$owner" == dprental-dev ]] || fail "Nhãn chủ sở hữu volume không khớp: $volume"
done
docker image inspect "$image" >/dev/null
printf 'Thư mục: %s\n' "$directory"
printf 'Volume sẽ được dọn: %s\n' "${volumes[@]}"
printf 'Migration sẽ chạy lại: %s\n' "${migrations[@]}"
printf 'Toàn bộ tài khoản, dữ liệu nghiệp vụ và file upload sẽ được dọn. Bản sao lưu được giữ trên server.\n'
[[ "$mode" == --execute ]] || exit 0
[[ -t 0 ]] || fail 'Thực thi cần terminal tương tác'
read -r -p 'Nhập RESET dprental-dev để tiếp tục: ' confirmation
[[ "$confirmation" == 'RESET dprental-dev' ]] || fail 'Đã hủy'
exec 9>"$directory/.reset-data.lock"
flock -n 9 || fail 'Một tiến trình dọn dữ liệu khác đang chạy'
mkdir -p "$directory/backups"
backup=$(mktemp -d "$directory/backups/reset-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")
trap 'printf "Dọn dữ liệu thất bại (dòng %s). Stack có thể đang dừng hoặc mới dọn một phần. Bản sao lưu: %s. Không khởi động dịch vụ ghi trước khi khôi phục.\n" "$LINENO" "$backup" >&2' ERR
# API keys are configuration: preserve their UID/permissions so existing .env keys remain valid.
meili_keys() {
  "${compose[@]}" config --format json | python3 -c '
import datetime, json, pathlib, subprocess, sys, urllib.request, urllib.error
configuration = json.load(sys.stdin)
if "meilisearch" not in configuration["services"]:
    sys.exit(0)
mode, backup = sys.argv[1], pathlib.Path(sys.argv[2])
ids = subprocess.check_output(["docker", "ps", "-q", "--filter", "label=com.docker.compose.project=dprental-dev", "--filter", "label=com.docker.compose.service=meilisearch"], text=True).split()
assert len(ids) == 1, "Expected one running Meilisearch container"
container = json.loads(subprocess.check_output(["docker", "inspect", ids[0]], text=True))[0]
address = next(network["IPAddress"] for network in container["NetworkSettings"]["Networks"].values() if network["IPAddress"])
master = configuration["services"]["meilisearch"]["environment"]["MEILI_MASTER_KEY"]
def request(path, payload=None, key=master):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request("http://" + address + ":7700" + path, data=data,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as response:
        return json.load(response)
path = backup / "meilisearch-key-config.json"
if mode == "save":
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
        expiry = item["expiresAt"]
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
    key = configuration["services"]["inventory-service"]["environment"]["Meilisearch__ApiKey"]
    request("/indexes", key=key)
    print("Đã khôi phục API keys; key Inventory trong .env vẫn hợp lệ.")
' "$1" "$backup"
}

meili_keys save
# Stop writers and storage before taking consistent filesystem snapshots.
"${compose[@]}" --profile migration stop --timeout 60
for volume in "${volumes[@]}"; do
  consumers=$(docker ps -q --filter "volume=$volume")
  [[ -z "$consumers" ]] || fail "Volume vẫn đang được sử dụng: $volume; stack được giữ ở trạng thái dừng"
  docker run --rm --network none --read-only --user 0 --entrypoint tar \
    --mount "type=volume,src=$volume,dst=/source,readonly" \
    "$image" -C /source -czf - . > "$backup/$volume.tar.gz"
  tar -tzf "$backup/$volume.tar.gz" >/dev/null
done
cp compose.yaml .env release.env "$backup/"
sha256sum "$backup/"*.tar.gz > "$backup/SHA256SUMS"
printf 'Đã kiểm tra bản sao lưu: %s\n' "$backup"
# Never use down -v: TLS/configuration volumes must survive.
"${compose[@]}" --profile migration down --timeout 60
docker volume rm "${volumes[@]}"
storage=(postgres valkey kafka media-minio)
[[ "$volume_count" == 4 ]] || storage+=(meilisearch)
"${compose[@]}" up -d --wait --wait-timeout 180 "${storage[@]}"
meili_keys restore
"${compose[@]}" run --rm --no-deps media-minio-init
for migration in "${migrations[@]}"; do
  "${compose[@]}" --profile migration run --rm --no-deps "$migration"
done
"${compose[@]}" up -d --wait --wait-timeout 240
"${compose[@]}" ps
printf 'Đã dọn dữ liệu; kiểm tra khởi động Compose đã qua. Bản sao lưu: %s\nTạo lại admin qua API bootstrap hiện hữu.\n' "$backup"
