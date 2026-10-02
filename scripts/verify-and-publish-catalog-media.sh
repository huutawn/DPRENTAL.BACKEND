#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Script bảo trì an toàn: Xác minh và Public toàn bộ Media đang được Catalog tham chiếu
# - Đọc distinct MediaId từ catalog.EquipmentTypeMedia và catalog.Categories
# - Gọi GET /api/v1/media/{id}/verify qua header X-Api-Key (kiểm tra Ready và MIME ảnh)
# - Gọi PUT /api/v1/media/{id}/visibility {"visibility":"public"}
# - Tham chiếu lịch sử không đối chiếu uploader với admin chạy script
# - Báo rõ ID lỗi và thoát với mã lỗi != 0 để chặn cutover nếu có lỗi
# ==============================================================================

MEDIA_SERVICE_URL="${MEDIA_SERVICE_URL:-${MEDIA_SERVICE_INTERNAL_URL:-http://localhost:5004}}"
MEDIA_SERVICE_URL="${MEDIA_SERVICE_URL%/}"
MEDIA_SERVICE_API_KEY="${MEDIA_SERVICE_API_KEY:-dev-catalog-media-internal-key}"

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGDATABASE="${PGDATABASE:-dprental_catalog}"
PGUSER="${PGUSER:-postgres}"
PGPASSWORD="${PGPASSWORD:-postgres}"
PGCONTAINER="${PGCONTAINER:-}"
export PGPASSWORD

echo "===> Bắt đầu quy trình kiểm tra và public Catalog Media..."
echo "MediaService URL: ${MEDIA_SERVICE_URL}"
echo "Postgres: ${PGUSER}@${PGHOST}:${PGPORT}/${PGDATABASE}"

# 1. Đọc danh sách MediaId duy nhất từ database
MEDIA_QUERY="
SELECT DISTINCT \"MediaId\"::text FROM catalog.\"EquipmentTypeMedia\"
UNION
SELECT DISTINCT \"ImageMediaId\"::text FROM catalog.\"Categories\" WHERE \"ImageMediaId\" IS NOT NULL;
"

if ! command -v curl >/dev/null 2>&1; then
    echo "Lỗi: Không tìm thấy lệnh 'curl'." >&2
    exit 1
fi

MEDIA_IDS=""
if command -v psql >/dev/null 2>&1; then
    MEDIA_IDS=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -t -A -c "$MEDIA_QUERY" 2>/dev/null || true)
elif [ -n "$PGCONTAINER" ] && command -v docker >/dev/null 2>&1; then
    MEDIA_IDS=$(docker exec "$PGCONTAINER" psql -U "$PGUSER" -d "$PGDATABASE" -t -A -c "$MEDIA_QUERY" 2>/dev/null || true)
elif command -v docker >/dev/null 2>&1 && docker ps --format '{{.Names}}' | grep -q 'catalogservice-pg-test'; then
    MEDIA_IDS=$(docker exec catalogservice-pg-test psql -U "$PGUSER" -d "$PGDATABASE" -t -A -c "$MEDIA_QUERY" 2>/dev/null || true)
else
    echo "Lỗi: Không tìm thấy lệnh 'psql' và không tìm thấy container PostgreSQL." >&2
    exit 1
fi

if [ -z "$MEDIA_IDS" ]; then
    echo "Không tìm thấy media asset nào đang được Catalog tham chiếu. Quy trình hoàn tất an toàn."
    exit 0
fi

TOTAL_COUNT=0
SUCCESS_COUNT=0
FAILED_COUNT=0
declare -a FAILED_DETAILS=()

ALLOWED_MIMES=("image/jpeg" "image/png" "image/webp")

for MEDIA_ID in $MEDIA_IDS; do
    # Bỏ qua dòng trống hoặc format không phải GUID
    if [[ ! "$MEDIA_ID" =~ ^[0-9a-fA-F-]{36}$ ]]; then
        continue
    fi

    TOTAL_COUNT=$((TOTAL_COUNT + 1))
    echo -n "[$TOTAL_COUNT] Đang xử lý MediaId: $MEDIA_ID ... "

    # 2. Gọi xác minh media
    VERIFY_RESPONSE=$(curl -s -w "\n%{http_code}" \
        -H "X-Api-Key: ${MEDIA_SERVICE_API_KEY}" \
        "${MEDIA_SERVICE_URL}/api/v1/media/${MEDIA_ID}/verify" 2>/dev/null || echo -e "\n000")

    HTTP_STATUS=$(echo "$VERIFY_RESPONSE" | tail -n 1)
    RESPONSE_BODY=$(echo "$VERIFY_RESPONSE" | sed '$d')

    if [ "$HTTP_STATUS" != "200" ]; then
        echo "THẤT BẠI (Verify HTTP $HTTP_STATUS)"
        FAILED_COUNT=$((FAILED_COUNT + 1))
        FAILED_DETAILS+=("MediaId: $MEDIA_ID - Verify trả về HTTP $HTTP_STATUS: $RESPONSE_BODY")
        continue
    fi

    # Kiểm tra Status và ContentType
    STATUS=$(echo "$RESPONSE_BODY" | grep -o '"status"[[:space:]]*:[[:space:]]*"[^"]*"' | cut -d'"' -f4 || true)
    CONTENT_TYPE=$(echo "$RESPONSE_BODY" | grep -o '"contentType"[[:space:]]*:[[:space:]]*"[^"]*"' | cut -d'"' -f4 || true)

    if [ "$STATUS" != "Ready" ]; then
        echo "THẤT BẠI (Status: $STATUS != Ready)"
        FAILED_COUNT=$((FAILED_COUNT + 1))
        FAILED_DETAILS+=("MediaId: $MEDIA_ID - Trạng thái chưa Ready: '$STATUS'")
        continue
    fi

    IS_VALID_MIME=false
    for MIME in "${ALLOWED_MIMES[@]}"; do
        if [ "$CONTENT_TYPE" = "$MIME" ]; then
            IS_VALID_MIME=true
            break
        fi
    done

    if [ "$IS_VALID_MIME" = false ]; then
        echo "THẤT BẠI (ContentType không hợp lệ: $CONTENT_TYPE)"
        FAILED_COUNT=$((FAILED_COUNT + 1))
        FAILED_DETAILS+=("MediaId: $MEDIA_ID - MIME không hợp lệ: '$CONTENT_TYPE'. Chỉ chấp nhận image/jpeg, image/png, image/webp")
        continue
    fi

    # 3. Đổi visibility sang public
    VISIBILITY_RESPONSE=$(curl -s -w "\n%{http_code}" \
        -X PUT \
        -H "Content-Type: application/json" \
        -H "X-Api-Key: ${MEDIA_SERVICE_API_KEY}" \
        -d '{"visibility":"public"}' \
        "${MEDIA_SERVICE_URL}/api/v1/media/${MEDIA_ID}/visibility" 2>/dev/null || echo -e "\n000")

    VIS_HTTP_STATUS=$(echo "$VISIBILITY_RESPONSE" | tail -n 1)
    VIS_BODY=$(echo "$VISIBILITY_RESPONSE" | sed '$d')

    if [ "$VIS_HTTP_STATUS" != "200" ]; then
        echo "THẤT BẠI (Visibility HTTP $VIS_HTTP_STATUS)"
        FAILED_COUNT=$((FAILED_COUNT + 1))
        FAILED_DETAILS+=("MediaId: $MEDIA_ID - Đổi visibility trả về HTTP $VIS_HTTP_STATUS: $VIS_BODY")
        continue
    fi

    echo "OK (Đã xác minh và Public)"
    SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
done

echo ""
echo "=============================================================================="
echo "KẾT QUẢ XỬ LÝ MEDIA CATALOG:"
echo "Tổng số media đã duyệt : $TOTAL_COUNT"
echo "Thành công             : $SUCCESS_COUNT"
echo "Thất bại               : $FAILED_COUNT"
echo "=============================================================================="

if [ "$FAILED_COUNT" -gt 0 ]; then
    echo "CẢNH BÁO: Phát hiện $FAILED_COUNT media asset không hợp lệ hoặc không thể public:" >&2
    for DETAIL in "${FAILED_DETAILS[@]}"; do
        echo " - $DETAIL" >&2
    done
    echo "" >&2
    echo "LỖI: Chặn cutover triển khai cho tới khi toàn bộ media được xử lý thành công." >&2
    exit 1
fi

echo "Toàn bộ media assets của Catalog đã sẵn sàng và công khai. Cutover an toàn!"
exit 0
