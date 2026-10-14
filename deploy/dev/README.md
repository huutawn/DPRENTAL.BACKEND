# DPRENTAL development deployment

Bundle này vận hành môi trường dev trên một VPS theo topology production-like. Frontend và Gateway cùng ở Docker network `edge`; AuthService, MediaService, CatalogService và PostgreSQL chỉ phục vụ qua network `services`. AuthService dùng schema `auth`, MediaService dùng schema `media`, CatalogService dùng schema `catalog` trong cùng database PostgreSQL; mỗi service chỉ truy cập bảng của mình qua code. MinIO nối cả `services` và `edge` để Caddy chuyển tiếp GET URL đã ký. Frontend chỉ gọi Gateway qua `http://gateway:8080`, không truy cập trực tiếp database. Trên VPS Opsi, Caddy chỉ bind loopback `127.0.0.1:18080` và `127.0.0.1:18443`; Opsi tunnel proxy sở hữu public `80` và `443` rồi chuyển tiếp vào hai cổng này.

Không dùng file này cho local development. Local stack tiếp tục dùng `compose.yaml` ở repository root và tự build source.

## Điều kiện trước khi triển khai

- Hai DNS record proxied qua Cloudflare phải trỏ tới VPS: `DEV_DOMAIN=dev-api.lazyops.cloud` và `DEV_APP_DOMAIN=dev-app.lazyops.cloud`. Opsi tunnel proxy phải active ở public TCP `80` và `443`.
- Cloudflare SSL/TLS encryption mode phải là **Full (strict)**. Caddy cấp và gia hạn origin certificate qua ACME; không dùng Flexible vì sẽ tạo vòng lặp HTTPS và làm mất bảo mật kết nối origin.
- VPS có Docker Engine và Docker Compose plugin.
- VPS đã `docker login ghcr.io` bằng token chỉ có quyền đọc image packages.
- Các image được build/push từ máy phát hành hoặc CI với tag commit SHA. VPS chỉ `pull`, không build source.
- AuthService và MediaService image có Dockerfile runtime chính thức; mỗi migration image chứa migrations của đúng release. Media migrator phải có `/tools/dotnet-ef` và source tại `/src/MediaService`, giống job `media-migrate` trong Compose local.
- AuthService, MediaService và Gateway có `/health/live` và `/health/ready`. Gateway chỉ được khởi động sau khi hai service readiness đạt.

## Chuẩn bị VPS

```bash
sudo install -d -m 0750 /opt/dprental/dev/secrets
sudo chown "$USER":"$USER" /opt/dprental/dev /opt/dprental/dev/secrets
cd /opt/dprental/dev
cp .env.example .env
cp release.env.example release.env
chmod 600 .env
chmod 600 release.env
```

Trước release đầu tiên, thay `your-org` trong `release.env` bằng đúng giá trị
GitHub Environment `GHCR_IMAGE_NAMESPACE`; CI chỉ thay commit SHA, không sửa
image repository.

Trong `.env`, đặt `MEDIA_MINIO_ROOT_USER` và `MEDIA_MINIO_ROOT_PASSWORD`. MediaService không cần cấu hình GUID service principal: nó xác minh chữ ký JWT bằng JWKS của AuthService, claim `u=svc`, GUID trong `sub` và claim `grants`. AuthService quản lý cấp API key với các quyền theo service (`media: verify, read, publish` hoặc quyền toàn cục `*/*`) khi Business Service cần gọi API nội bộ. MediaService dùng cùng `POSTGRES_DB`, `POSTGRES_USER` và `POSTGRES_PASSWORD` với AuthService; hai schema và hai migration history tách biệt, nhưng dùng chung user nên chưa có cách ly bằng quyền PostgreSQL. Image `MEDIA_MINIO_IMAGE` được ghim bằng digest đã dùng trong kiểm thử local; kiểm tra image này có lệnh `mc` trước khi thay digest. Trong `release.env`, đặt `MEDIA_SERVICE_MIGRATOR_IMAGE` và `MEDIA_SERVICE_IMAGE` sau khi build image qua CI/CD.

Tạo hai PEM file dưới `secrets/`; chúng không được commit. AuthService runtime chạy bằng UID `1654`, vì vậy file phải chỉ cho UID này đọc được:

```bash
sudo chown 1654:1654 secrets/auth-jwt-private.pem secrets/auth-jwt-public.pem
sudo chmod 400 secrets/auth-jwt-private.pem secrets/auth-jwt-public.pem
```

JWT issuer/audience trong `.env` phải khớp giữa Gateway và AuthService. Không tái sử dụng private key local được Compose root tự sinh.

Hiện AuthService nhận PostgreSQL connection string qua environment variable, nên `POSTGRES_PASSWORD` nằm trong `.env` trên VPS và không được commit. Đây là giới hạn tạm thời của ứng dụng hiện tại; trước khi dùng topology này cho production, thay bằng Docker secret hoặc secret manager được AuthService đọc trực tiếp. JWT private key đã dùng file secret ngay từ đầu.

## Quy trình phát hành

1. Mỗi CI/CD cập nhật đúng image SHA trong `release.env`: Gateway, AuthService/Auth migrator, MediaService/Media migrator, CatalogService/Catalog migrator, InventoryService/Inventory migrator hoặc Frontend. Không sửa image tag trong `.env`.
2. Render cấu hình để phát hiện biến hoặc secret file thiếu:

   ```bash
   docker compose --env-file .env --env-file release.env config --quiet
   ```

3. Nếu database chung đã có dữ liệu, sao lưu database trước khi chạy Media migration: `RemoveMediaOwner` và `RemoveMediaPurpose` xóa dữ liệu các cột cũ trong schema `media`; rollback ứng dụng đòi hỏi khôi phục backup. Pull image, chạy migration trước khi khởi động MediaService mới:

   ```bash
   docker compose --env-file .env --env-file release.env pull
   docker compose --env-file .env --env-file release.env --profile migration pull auth-migrate media-migrate
   docker compose --env-file .env --env-file release.env up -d postgres valkey media-minio media-minio-init
   docker compose --env-file .env --env-file release.env --profile migration run --rm auth-migrate
   docker compose --env-file .env --env-file release.env --profile migration run --rm media-migrate
   docker compose --env-file .env --env-file release.env up -d --remove-orphans
   ```

4. Xác nhận Gateway readiness qua `https://<DEV_DOMAIN>/health/ready` và trang Frontend qua `https://<DEV_APP_DOMAIN>/`, sau đó kiểm thử JWKS, login và upload file qua Gateway. Với `ASPNETCORE_ENVIRONMENT=Staging`, Swagger của Gateway và MediaService không được bật.

## Frontend

Frontend image được build với `NEXT_PUBLIC_API_URL=https://dev-api.lazyops.cloud`, vì giá trị `NEXT_PUBLIC_*` được nhúng vào JavaScript khi build. Tại runtime, `BACKEND_API_URL=http://gateway:8080` chỉ dùng bởi các Next.js route handler (login/refresh/logout và BFF proxy), nên không quay vòng qua Cloudflare và không công khai Gateway nội bộ. Riêng Google OAuth phải chuyển trình duyệt qua `NEXT_PUBLIC_API_URL`; `FRONTEND_ORIGIN=https://<DEV_APP_DOMAIN>` bảo đảm callback không dùng hostname nội bộ của Docker.

Caddy route `DEV_DOMAIN` tới Gateway, riêng đường dẫn `/dprental-media/*` chuyển GET/HEAD/OPTIONS đến MinIO mà không đổi Host hoặc path để giữ chữ ký S3 hợp lệ. Bucket vẫn private; URL Public ổn định là `/api/v1/media/{mediaId}/content`, endpoint này chuyển hướng đến URL MinIO ký ngắn hạn. `DEV_APP_DOMAIN` tới Frontend. Cloudflare chỉ là reverse proxy phía trước Caddy; không thêm route AuthService hoặc PostgreSQL vào Caddy.

Không tự động chạy rollback migration. Chỉ rollback application image khi migration tương thích ngược; schema thay đổi phá vỡ tương thích phải theo expand-and-contract.

## Thêm service mới

Mỗi service mới phải có image SHA riêng, migration job riêng, health endpoint và credentials/secret namespace riêng. AuthService và MediaService cùng một PostgreSQL database trong dev nhưng mỗi service sở hữu schema riêng; không service nào được đọc hoặc ghi trực tiếp bảng của service khác. Nếu cần cách ly bằng quyền database, tạo PostgreSQL role riêng cho từng schema trước khi mở rộng mô hình này.

## Phát hành CatalogService

Catalog sử dụng cùng PostgreSQL dev nhưng chỉ truy cập schema `catalog`; migration history là
`catalog.__EFMigrationsHistory`. Runtime chỉ mount `auth_jwt_public_key`, không mount private key.
Issuer/audience và key id phải khớp AuthService; key ring Catalog hiện là cấu hình tĩnh nên khi rotate
signing key cần cập nhật Catalog và giữ key cũ cho đến khi token cũ hết hạn.

1. Commit/build/push runtime (`Dockerfile --target runtime`) và migrator (`--target migrator`) của
   Catalog bằng cùng commit SHA. Cập nhật `CATALOG_SERVICE_IMAGE` và `CATALOG_MIGRATOR_IMAGE` trong
   release.env. Deploy Gateway có các route Catalog mới bằng pipeline Gateway hiện có.
2. Đồng bộ compose.yaml từ bundle này lên VPS. Hai biến image Catalog là bắt buộc khi render Compose,
   kể cả khi chỉ triển khai một service khác; thêm chúng vào release.env trước khi thay compose.yaml.
3. Render cấu hình, pull đúng hai artifacts và đảm bảo PostgreSQL đang chạy:

   ```bash
   docker compose --env-file .env --env-file release.env config --quiet
   docker compose --env-file .env --env-file release.env pull catalog-service
   docker compose --env-file .env --env-file release.env --profile migration pull catalog-migrate
   docker compose --env-file .env --env-file release.env up -d postgres
   ```

4. Nếu Catalog đã chạy:
   - **Ghi nhận image cũ**: Lưu lại SHA đang chạy của `CATALOG_SERVICE_IMAGE` và `CATALOG_MIGRATOR_IMAGE` để sẵn sàng khôi phục nếu migration/release thất bại.
   - **Dừng ghi và sao lưu**: Tạm dừng ghi và stop catalog-service. Sao lưu đầy đủ schema `catalog` (hoặc toàn bộ database) trước khi migrate:
     ```bash
     docker compose --env-file .env --env-file release.env exec -T postgres pg_dump -U authservice -d authservice -n catalog -Fc > backups/catalog/catalog_pre_migrate_$(date +%Y%m%d%H%M%S).dump
     ```
     Migration kiểm tra Code trùng giữa organizations và dừng nguyên transaction nếu có trùng; sửa dữ liệu nguồn rồi chạy lại. Không tự đổi mã hoặc gộp equipment.
5. Chạy migration trước runtime và **kiểm tra exit code**:
   ```bash
   docker compose --env-file .env --env-file release.env --profile migration run --rm catalog-migrate
   ```
   - **Nếu migration thất bại (exit code khác 0)**: Do migration chạy trong transaction và được rollback, schema vẫn ở phiên bản cũ tương thích với runtime trước. **Phục hồi ngay runtime cũ**: đưa `CATALOG_SERVICE_IMAGE` và `CATALOG_MIGRATOR_IMAGE` trong `release.env` về lại SHA cũ đã lưu ở bước 4, rồi khởi động lại `catalog-service` cũ để khôi phục dịch vụ cho người dùng trước khi phân tích nguyên nhân.
   - **Nếu migration thành công (exit code 0)**: Khởi động runtime mới:
     ```bash
     docker compose --env-file .env --env-file release.env up -d --no-deps --wait catalog-service
     ```
6. Kiểm tra readiness nội bộ và các GET công khai qua Gateway:
   - **CẢNH BÁO**: Tuyệt đối không dùng `/health/ready` của Gateway làm bằng chứng CatalogService hoạt động; Gateway chỉ kiểm tra JWKS nội bộ và trả `200` ngay cả khi CatalogService đang dừng và trả `502`.
   - Bắt buộc kiểm tra readiness Catalog nội bộ và các endpoint nghiệp vụ thực tế:
     ```bash
     docker compose --env-file .env --env-file release.env exec -T catalog-service wget --quiet --spider http://127.0.0.1:8080/health/ready
     curl --fail --show-error "https://$DEV_DOMAIN/api/v1/catalog/categories"
     curl --fail --show-error "https://$DEV_DOMAIN/api/v1/catalog/equipment-types?page=1&pageSize=20"
     ```

   `$DEV_DOMAIN` là hostname trong .env; export biến này trong shell trước khi chạy curl.
   GET chỉ trả Published; Draft/Disabled detail trả 404. POST/PATCH/publish/disable không có token phải
   trả 401, user không phải Admin trả 403 tại Catalog. Token chỉ có audience inventory-api bị Gateway
   reject. Không đặt token/credentials trong command-line smoke tests; dùng client đăng nhập để kiểm tra
   các thao tác Admin. Staging không bật Swagger; `/catalog/swagger/v1/swagger.json` trả 401 với request
   anonymous do fallback authorization, hoặc 404 khi có token hợp lệ. Không dùng endpoint này làm healthcheck.

Catalog không có dependency runtime tới MediaService: media là MediaId references. Gateway không phụ
thuộc Catalog healthy để khởi động; gateway /health/ready kiểm tra JWKS/configuration, phải kiểm tra
Catalog riêng như bước 6. Compose không tự apply migrations khi start runtime.

## Phát hành InventoryService

Pipeline `.github/workflows/inventoryservice-dev.yml` trên branch `inventory/develop-ci-cd`
publish runtime/migrator với cùng commit SHA rồi gọi
`scripts/deploy-inventoryservice.sh`. Dùng GitHub Environment `dev`, variable
`GHCR_IMAGE_NAMESPACE` và các secrets `DEV_VPS_SSH_PRIVATE_KEY`, `DEV_VPS_KNOWN_HOSTS`,
`DEV_VPS_HOST`, `DEV_VPS_USER`, `DEV_VPS_PORT` (mặc định 22), giống pipeline Catalog.

Trước release đầu, đồng bộ compose và thêm vào `.env` các biến `KAFKA_IMAGE`,
`INVENTORY_JWT_SERVICE_AUDIENCE`, `INVENTORY_MEDIA_TIMEOUT_SECONDS` từ `.env.example`.
Thêm `INVENTORY_SERVICE_IMAGE` và `INVENTORY_MIGRATOR_IMAGE` vào `release.env` với namespace
GHCR thực tế. Dùng các biến `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` hiện có.
Không commit `.env` hoặc `release.env` thật.

Inventory dùng chung PostgreSQL/database/credentials với Auth và Catalog, chỉ sở hữu schema
`inventory`; migration history là `inventory.__EFMigrationsHistory`. Volume mới là `kafka-data`;
không thêm PostgreSQL container, volume hoặc mật khẩu riêng.
JWT dùng JWKS Auth, hai audiences human/service; Media API key dùng bootstrap key hiện có.
Gateway route 6/7 trỏ tới `inventory-service:8080` và chờ Inventory healthy khi khởi động.
Gateway đã có route `/api/v1/inventory/{everything}`; UI Swagger Development bổ sung Inventory.
Staging không bật Swagger.

Script pull images, khởi động các dependencies, stop Inventory, backup schema inventory nếu
schema inventory đã tồn tại vào `backups/inventory`, rồi chạy `inventory-migrate`.
Migration hoặc readiness lỗi sẽ dừng release, không tự rollback schema/image.
`release.env` ghi SHA mới trước migration; failure cần người vận hành xử lý và chạy lại.
Lần khởi tạo stack cần chạy Auth/Media/Catalog migrations theo hướng dẫn trước khi deploy Inventory.
Compose không tự migrate Inventory khi `up` runtime.

Kiểm tra readiness nội bộ bằng `wget` trong container và public Gateway `/health/ready`.
API Inventory yêu cầu token; Gateway health không chứng minh request Inventory đi hết downstream.
Kiểm tra calendar bằng client đã đăng nhập có quyền tương ứng sau triển khai.
Kafka dev chỉ có một broker, replication factor 1; backup database không bao gồm Kafka volume.
Nếu Inventory đã tồn tại trong database riêng, restore schema inventory và chuyển migration history
của Inventory từ public sang inventory trước release đầu; không chuyển history của service khác.
Nếu database chung đã chứa Inventory với history public cũ, thực hiện cutover history trước khi migrate.

### Chuyển migration history Inventory cũ

Chỉ áp dụng khi Inventory đã migrate bằng phiên bản lưu history ở `public`.
Dừng Inventory cũ, backup toàn bộ database trước khi chạy SQL dưới đây trong database chung.
Danh sách ID chỉ gồm các migrations Inventory hiện có; giữ nguyên records của Auth/Media/Catalog.
Nếu đang dùng database riêng, restore dữ liệu schema inventory và các history records tương ứng
vào database chung trước bước này. Không start Inventory cũ sau cutover.

```sql
BEGIN;
CREATE SCHEMA IF NOT EXISTS inventory;
CREATE TABLE IF NOT EXISTS inventory."__EFMigrationsHistory" (
    "MigrationId" varchar(150) PRIMARY KEY,
    "ProductVersion" varchar(32) NOT NULL
);
INSERT INTO inventory."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT "MigrationId", "ProductVersion" FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
    '20261001035544_InitialInventoryBaseline',
    '20261001043209_HardenInventoryContracts',
    '20261001080639_AddKafkaFoundationAndSagaRuntime',
    '20261002011635_RefactorInventoryBoundary',
    '20261002024109_HardenInventoryBoundary'
)
ON CONFLICT ("MigrationId") DO NOTHING;
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
    '20261001035544_InitialInventoryBaseline',
    '20261001043209_HardenInventoryContracts',
    '20261001080639_AddKafkaFoundationAndSagaRuntime',
    '20261002011635_RefactorInventoryBoundary',
    '20261002024109_HardenInventoryBoundary'
);
COMMIT;
```

Đây là bước cutover một lần; deploy mới trên database trống không cần chạy SQL này.


## Inventory Meilisearch

Compose requires `MEILI_IMAGE=getmeili/meilisearch:v1.55.0`, a random private
`MEILI_MASTER_KEY` and a separate `INVENTORY_MEILISEARCH_API_KEY` in the VPS `.env`
(mode 0600). The Inventory key must allow search, document add/delete, index
create/get/update/delete/swap, settings get/update and tasks get, with `indexes: ["*"]` on this instance dedicated to Inventory. Meilisearch treats
`indexSwap` as a global task; index-restricted keys can submit the swap but cannot
read its task, returning 404 and leaving bootstrap unready. Keep the action list
limited to those operations; the operational key does not need key-management,
task deletion/cancellation, dumps or snapshots permissions. Provision it using the protected Meilisearch
keys API; do not commit keys or place them in shell arguments. Meilisearch is bound
to host loopback 7700 and the private services network, with a persistent volume.

Inventory uses a five-second HTTP timeout, 60-second task deadline and 5,000 candidate
limit. Deploy the matching Inventory application/migrator release and drain old
projectors before the search recovery migration. Changing compose/.env alone does
not deploy application code or change a running container's environment.

Gateway routes 16/17 are the public Inventory availability POST routes. They must
point to `inventory-service`, not `rental-service`; stale numbered overrides caused
404 responses from Rental. Route numbers depend on the Gateway image: validate
its embedded route paths whenever upgrading that image.

The global-task requirement is documented in the [Meilisearch asynchronous operations specification](https://specs.meilisearch.dev/specifications/text/0191-swap-indexes-api.html). Verified on v1.55.0: the restricted key received 404 for a successful swap; recreating its UID with global index visibility preserved the application credential and allowed bootstrap to complete.
