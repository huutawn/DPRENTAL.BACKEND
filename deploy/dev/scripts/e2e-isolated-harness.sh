#!/usr/bin/env bash
# Harness thiết lập, chạy và nghiệm thu stack cô lập dprental-e2e-* trên ubuntu-vm
set -euo pipefail
umask 077

fail() { printf 'E2E-Harness: %s\n' "$*" >&2; exit 1; }

RUN_ID=$(date -u +%Y%m%d%H%M%S)
PROJECT="dprental-e2e-${RUN_ID}"
E2E_DIR="/opt/dprental/e2e-${RUN_ID}"
DEV_DIR="/opt/dprental/dev"
HOST_PORT=28080

printf '=== KHỞI TẠO TEST HARNESS STACK CÔ LẬP: %s ===\n' "$PROJECT"

# 1. Kiểm tra tài nguyên và preflight isolation
[[ -d "$DEV_DIR" ]] || fail "Không tìm thấy thư mục dev: $DEV_DIR"
avail_disk_mb=$(df -m "$DEV_DIR" | awk 'NR==2 {print $4}')
[[ $avail_disk_mb -ge 3000 ]] || fail "Không đủ dung lượng đĩa trống cho stack cô lập ($avail_disk_mb MB < 3000 MB)"

# Kiểm tra port loopback khả dụng
if ss -tln | grep -q ":${HOST_PORT} "; then
  fail "Cổng loopback :${HOST_PORT} đang bị chiếm dụng"
fi

# 2. Tạo thư mục cấu hình cô lập
mkdir -p "$E2E_DIR" "$E2E_DIR/secrets" "$E2E_DIR/backups"
chmod 0700 "$E2E_DIR"

# 3. Tạo cặp khóa riêng biệt cho Auth JWT và Integrity
openssl genrsa -out "$E2E_DIR/secrets/auth-jwt-private.pem" 2048 2>/dev/null
openssl rsa -in "$E2E_DIR/secrets/auth-jwt-private.pem" -pubout -out "$E2E_DIR/secrets/auth-jwt-public.pem" 2>/dev/null
chmod 0644 "$E2E_DIR/secrets/auth-jwt-private.pem"
chmod 0644 "$E2E_DIR/secrets/auth-jwt-public.pem"

# Tạo ECDSA P-256 key cho Integrity (PKCS#8 DER base64 theo chuẩn EcdsaAttestation)
openssl ecparam -name prime256v1 -genkey -noout -out "$E2E_DIR/secrets/payment-integrity-dev-v1-private.pem" 2>/dev/null
chmod 0644 "$E2E_DIR/secrets/payment-integrity-dev-v1-private.pem"
INTEGRITY_PRIV_B64=$(openssl pkcs8 -topk8 -nocrypt -in "$E2E_DIR/secrets/payment-integrity-dev-v1-private.pem" -outform DER 2>/dev/null | base64 -w0)
INTEGRITY_PUB_B64=$(openssl ec -in "$E2E_DIR/secrets/payment-integrity-dev-v1-private.pem" -pubout -outform DER 2>/dev/null | base64 -w0)
# Tạo encryption key 32 bytes ngẫu nhiên
DATA_ENC_KEY_B64=$(head -c 32 /dev/urandom | base64 -w0)

# 4. Sao chép release.env và render .env cô lập
cp -p "$DEV_DIR/release.env" "$E2E_DIR/release.env"
cp -p "$DEV_DIR/kafka-log4j2.yaml" "$E2E_DIR/kafka-log4j2.yaml"
[[ ! -f "$DEV_DIR/firebase-service-account.json" ]] || cp -p "$DEV_DIR/firebase-service-account.json" "$E2E_DIR/firebase-service-account.json"
[[ ! -f "$DEV_DIR/notification-smtp.env" ]] || cp -p "$DEV_DIR/notification-smtp.env" "$E2E_DIR/notification-smtp.env"
[[ -f "$E2E_DIR/notification-smtp.env" ]] || touch "$E2E_DIR/notification-smtp.env"
cp -p "$DEV_DIR/.env" "$E2E_DIR/.env"

# Ghi đè các cấu hình cô lập vào cuối file .env
cat <<EOF >> "$E2E_DIR/.env"

# ==============================================================================
# E2E ISOLATED STACK OVERRIDES (${PROJECT})
# ==============================================================================
COMPOSE_PROJECT_NAME=${PROJECT}
SECRETS_DIR=${E2E_DIR}/secrets
EDGE_NETWORK_CIDR=172.31.0.0/24
DEV_DOMAIN=127.0.0.1:${HOST_PORT}
DEV_APP_DOMAIN=127.0.0.1:${HOST_PORT}
CORS_ALLOWED_ORIGIN_0=http://127.0.0.1:${HOST_PORT}
CORS_ALLOWED_ORIGIN_1=http://localhost:${HOST_PORT}
GATEWAY_RATE_LIMIT_USER_READ=20000
GATEWAY_RATE_LIMIT_USER_WRITE=20000
GATEWAY_RATE_LIMIT_IP_READ=20000
GATEWAY_RATE_LIMIT_IP_WRITE=20000

POSTGRES_DB=dprental_e2e_${RUN_ID}
POSTGRES_USER=dprental_e2e_user
POSTGRES_PASSWORD=e2e_pg_pass_${RUN_ID}

INTEGRITY_POSTGRES_DB=integrity_evidence_e2e_${RUN_ID}
INTEGRITY_POSTGRES_USER=integrity_e2e_user
INTEGRITY_POSTGRES_PASSWORD=e2e_integ_pass_${RUN_ID}

VALKEY_PASSWORD=e2e_valkey_pass_${RUN_ID}

MEDIA_MINIO_ROOT_USER=minio_e2e_admin
MEDIA_MINIO_ROOT_PASSWORD=minio_e2e_secret_${RUN_ID}
MEDIA_MINIO_BUCKET=dprental-media-e2e
MEDIA_MINIO_ACCESS_KEY=minio_e2e_access_${RUN_ID}
MEDIA_MINIO_SECRET_KEY=minio_e2e_key_${RUN_ID}


AUTH_ADMIN_BOOTSTRAP_CREATION_KEY=e2e_bootstrap_admin_key_${RUN_ID}
INTERNAL_API_KEY=e2e_internal_api_key_${RUN_ID}
AUTH_JWT_PRIVATE_KEY_PATH=${E2E_DIR}/secrets/auth-jwt-private.pem
AUTH_JWT_PUBLIC_KEY_PATH=${E2E_DIR}/secrets/auth-jwt-public.pem

PAYMENT_SEPAY_API_KEY=e2e_sepay_api_key_${RUN_ID}
PAYMENT_RENTAL_API_KEY=e2e_payment_rental_key_${RUN_ID}
PAYMENT_INTEGRITY_API_KEY=e2e_integrity_api_key_${RUN_ID}
PAYMENT_SIGNING_PRIVATE_KEY_BASE64=${INTEGRITY_PRIV_B64}
PAYMENT_SIGNING_PUBLIC_KEY_BASE64=${INTEGRITY_PUB_B64}
PAYMENT_DATA_ENCRYPTION_KEY_BASE64=${DATA_ENC_KEY_B64}

KAFKA_JVM_PERFORMANCE_OPTS=-Xms128m -Xmx256m
EOF
chmod 0600 "$E2E_DIR/.env"

# Render Caddyfile cô lập cho loopback
cat <<EOF > "$E2E_DIR/Caddyfile"
{
    admin off
    auto_https off
}

:80 {
    encode gzip zstd

    @api path /api/* /recommendation/* /swagger/* /health/*
    handle @api {
        reverse_proxy gateway:8080
    }

    handle {
        reverse_proxy frontend:3000
    }
}
EOF

# Render compose.yaml với tên project cô lập và port loopback
python3 -c '
import sys, re
dev_compose_path = sys.argv[1]
e2e_compose_path = sys.argv[2]
host_port = sys.argv[3]
project = sys.argv[4]

content = open(dev_compose_path, encoding="utf-8").read()
# Đổi project name
content = re.sub(r"^name:\s*dprental-dev", f"name: {project}", content, flags=re.MULTILINE)
# Đổi port caddy thành loopback
content = re.sub(r"-\s*10\.10\.0\.2:18080:80", f"- 127.0.0.1:{host_port}:80", content)
content = re.sub(r"-\s*10\.10\.0\.2:18443:443", f"- 127.0.0.1:28443:443", content)
# Đổi secrets source dir
content = re.sub(r"/opt/dprental/dev", sys.argv[5], content)
# Thêm cấu hình giới hạn RAM cho Kafka
if "KAFKA_JVM_PERFORMANCE_OPTS" not in content:
    content = content.replace("KAFKA_NODE_ID: 1", "KAFKA_NODE_ID: 1\n      KAFKA_JVM_PERFORMANCE_OPTS: -Xms128m -Xmx256m")

with open(e2e_compose_path, "w", encoding="utf-8") as f:
    f.write(content)
' "$DEV_DIR/compose.yaml" "$E2E_DIR/compose.yaml" "$HOST_PORT" "$PROJECT" "$E2E_DIR"

chmod 0600 "$E2E_DIR/compose.yaml"

# 5. Cài đặt bản scripts mới vào $E2E_DIR/scripts
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
mkdir -p "$E2E_DIR/scripts"
cp -r "$SCRIPT_DIR"/* "$E2E_DIR/scripts/"
chmod +x "$E2E_DIR/scripts/reset-data.sh"
printf 'Cấu hình stack cô lập đã được render tại %s\n' "$E2E_DIR"

# 6. Kiểm tra isolation: không dùng shared volumes hoặc networks
compose_cmd=(docker compose --project-name "$PROJECT" --project-directory "$E2E_DIR" --file "$E2E_DIR/compose.yaml" --env-file "$E2E_DIR/.env" --env-file "$E2E_DIR/release.env")

"${compose_cmd[@]}" config >/dev/null || fail "Compose config của stack cô lập không hợp lệ"

# 7. Khởi động storage và chạy migrations trên stack cô lập
printf 'Khởi động storage cho stack cô lập...\n'
docker volume create --name "${PROJECT}_kafka-data" --label "com.docker.compose.project=$PROJECT" >/dev/null 2>&1 || true
docker run --rm -v "${PROJECT}_kafka-data:/data" postgres:17.11-alpine3.24 chown -R 1000:1000 /data 2>/dev/null || true
"${compose_cmd[@]}" up -d --wait --wait-timeout 180 postgres integrity-postgres valkey kafka media-minio meilisearch

printf 'Chạy minio-init và 10 migrations trên stack cô lập...\n'
"${compose_cmd[@]}" run --rm --no-deps media-minio-init
"${compose_cmd[@]}" exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS catalog;
CREATE SCHEMA IF NOT EXISTS inventory;
CREATE SCHEMA IF NOT EXISTS media;
CREATE SCHEMA IF NOT EXISTS notification;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS recommendation;
CREATE SCHEMA IF NOT EXISTS rental;
"' 2>/dev/null || true

for m in $("${compose_cmd[@]}" --profile migration config --services | grep migrate); do
  printf 'Migration: %s\n' "$m"
  "${compose_cmd[@]}" --profile migration run --rm --no-deps "$m"
done

printf 'Khởi động toàn bộ ứng dụng trên stack cô lập...\n'
"${compose_cmd[@]}" up -d --wait --wait-timeout 240
"${compose_cmd[@]}" ps

# Kiểm tra Gateway phản hồi trên loopback
curl -s -f "http://127.0.0.1:${HOST_PORT}/api/v1/catalog/categories" >/dev/null || fail "Gateway loopback không phản hồi tại :${HOST_PORT}"

# 8. Chạy reset-data.sh --preview trên stack cô lập
printf '=== THỬ NGHIỆM RESET-DATA --PREVIEW TRÊN STACK CÔ LẬP ===\n'
bash "$E2E_DIR/scripts/reset-data.sh" --project "$PROJECT" --directory "$E2E_DIR" --preview

# 9. Chạy reset-data.sh --execute trên stack cô lập
printf '=== THỰC THI RESET-DATA --EXECUTE TRÊN STACK CÔ LẬP ===\n'
bash "$E2E_DIR/scripts/reset-data.sh" --project "$PROJECT" --directory "$E2E_DIR" --execute --non-interactive

# Lấy đường dẫn snapshot backup vừa tạo
LATEST_BACKUP=$(ls -td "$E2E_DIR/backups/reset-"* | head -n 1)
[[ -n "$LATEST_BACKUP" && -d "$LATEST_BACKUP" ]] || fail "Không tìm thấy snapshot backup sau khi reset"
printf 'Snapshot backup tạo thành công: %s\n' "$LATEST_BACKUP"

# 10. Chạy seed-data.py --apply trên stack cô lập
printf '=== THỰC THI SEED-DATA VÀ NGHIỆM THU E2E MỐC CHẠY NGAY ===\n'
python3 "$E2E_DIR/scripts/seed-data/seed-data.py" \
  --project "$PROJECT" \
  --base-url "http://127.0.0.1:${HOST_PORT}" \
  --state-dir "$E2E_DIR/scripts/seed-data/.state" \
  --env-file "$E2E_DIR/.env" \
  --deploy-dir "$E2E_DIR" \
  --request-interval 0.05 \
  --apply

# 11. Thử nghiệm phục hồi RESTORE từ snapshot trên stack cô lập
printf '=== THỬ NGHIỆM RESTORE DỮ LIỆU TỪ SNAPSHOT TRÊN STACK CÔ LẬP ===\n'
bash "$E2E_DIR/scripts/reset-data.sh" --project "$PROJECT" --directory "$E2E_DIR" --restore "$LATEST_BACKUP" --non-interactive

printf '=== VERIFY SAU KHI RESTORE ===\n'
python3 "$E2E_DIR/scripts/seed-data/seed-data.py" \
  --project "$PROJECT" \
  --base-url "http://127.0.0.1:${HOST_PORT}" \
  --state-dir "$E2E_DIR/scripts/seed-data/.state" \
  --env-file "$E2E_DIR/.env" \
  --deploy-dir "$E2E_DIR" \
  --request-interval 0.05 \
  --verify

printf '=== NGHIỆM THU TOÀN DIỆN TRÊN STACK CÔ LẬP ĐẠT 100%% ===\n'
EOF
