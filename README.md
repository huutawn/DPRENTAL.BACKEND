# CatalogService

Dịch vụ CatalogService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp chuẩn hóa tương đồng với AuthService.

## Cấu trúc

```text
CatalogService/
  src/
    Common/          # PagedResult, PageRequest, Result, Error catalog và kiểu dùng chung
    Domain/          # Entities, DTOs contract (GetEquipmentTypesRequest, EquipmentTypeListItemDto,...), DbContext và Repositories
    Application/     # Services, Validators, Options, KeyRing
    Api/             # Controllers, Routing, Security (JWT RS256, ForwardedHeaders), Observability, Exception handling
  tests/             # Unit, Integration và API boundary tests
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ và Git hooks
```

## Tính năng và chuẩn hóa theo AuthService

- **Tiền tố API**: Toàn bộ endpoint của controller tự động ghép tiền tố `api/v1/catalog` (vd: `GET /api/v1/catalog/equipment-types`).
- **Phân trang và DTO**: Dùng duy nhất `PagedResult<T>` (`items`, `page`, `pageSize`, `totalCount`, `totalPages`) và `PageRequest`. DTO chia sẻ giữa repository, service và controller.
- **Xử lý lỗi**: Kết quả trả về qua `Result` và catalog mã lỗi `CatalogErrors` / `CatalogErrorCode`. Controller chuyển lỗi sang HTTP `ProblemDetails` qua extension tập trung. `title` là mã lỗi ổn định (machine contract), `detail` là thông báo cho người đọc. Lỗi biên dùng `Catalog.InvalidRequest` (400 model binding), `Catalog.Unauthorized` (401), `Catalog.Forbidden` (403), `Catalog.Uncategorized` (500).
- **Xác thực JWT RS256**: Xác thực token bằng public RSA key (tối thiểu 2048-bit) tải từ JWKS của AuthService (`Auth:Jwt:JwksUri`), có cache, tự tải lại khi gặp `kid` mới và không gọi AuthService mỗi request, hỗ trợ Authorization header và cookie `accessToken`. Mặc định yêu cầu authentication, các endpoint discovery và health check mở anonymous.
- **Quan sát (Observability)**: Correlation ID ổn định trong mỗi request và response header `X-Correlation-ID`. ID nằm trong log scope của mọi dòng log và trong extension `correlationId` của `ProblemDetails`; header không hợp lệ (quá 64 ký tự hoặc có ký tự ngoài chữ, số, `-`, `_`, `.`) được thay bằng ID mới. Log console dạng JSON (Development dùng `simple`), ghi kết quả các thao tác ghi của Category/EquipmentType; không log header hay token. Tracing OpenTelemetry W3C với service name `dprental-catalog-service`.
- **Bảo mật mạng**: Forwarded headers với proxy CIDR và cấu hình CORS chặt chẽ.

## Quyền truy cập API

Catalog đọc nguyên các claim do AuthService phát hành, không tin header định danh (`X-User-Id`, `X-Role`, `X-Organization-Id`): `sub` (user ID), `r` (role: `adm` Admin, `adp` AdminPlatform, `own` Owner, `mem` Member, `cus` Customer), `p` (permission), `o` (organization, mỗi organization một claim).

| API (tiền tố `/api/v1/catalog`) | Quyền |
| --- | --- |
| `GET /categories`, `GET /categories/{id}` | Công khai |
| `GET /equipment-types`, `GET /equipment-types/{id}` | Công khai |
| `GET /equipment-types/{id}/schema` | Công khai (Type phải Published, trả schema tại `CurrentSchemaVersion`) |
| `GET /equipment-types/{id}/schemas/{version}` | Công khai (Type Published hoặc Disabled; chỉ trả version đã publish) |
| `GET /brands`, `GET /brands/{id}` | Công khai (chỉ active; inactive hoặc không tồn tại trả `404`) |
| `GET /admin/brands` | Policy `CatalogBrandManage`: role `adm` (active và inactive, lọc `isActive`) |
| `GET /admin/brands/{id}` | Policy `CatalogBrandManage`: role `adm` (đọc mọi trạng thái) |
| `POST /brands`, `PATCH /brands/{id}` | Policy `CatalogBrandManage`: role `adm` |
| `POST /brands/{id}/disable`, `POST /brands/{id}/enable` | Policy `CatalogBrandManage`: role `adm` (idempotent) |
| `POST /equipment-types/{id}/specifications/validate` | Authenticated (User JWT hoặc Service token; type Published, schema đã publish; Admin không được bypass) |
| `POST /categories`, `PATCH /categories/{id}` | Policy `CatalogCategoryManage`: role `adm` |
| `POST /equipment-types`, `PATCH /equipment-types/{id}` | Policy `CatalogEquipmentManage`: role `adm` |
| `POST /equipment-types/{id}/publish`, `POST /equipment-types/{id}/disable` | Policy `CatalogEquipmentManage`: role `adm` |
| `GET /equipment-types/{id}/spec-schemas`, `POST /equipment-types/{id}/spec-schemas` | Policy `CatalogEquipmentManage`: role `adm` |
| `PUT /equipment-types/{id}/spec-schemas/{version}`, `POST /equipment-types/{id}/spec-schemas/{version}/publish` | Policy `CatalogEquipmentManage`: role `adm` |
| `GET /equipment-types/{id}/spec-schemas/{version}` | Công khai (Admin đọc mọi trạng thái; public đọc bản published của type Published/Disabled) |
| `GET /health/live`, `GET /health/ready` (không có tiền tố) | Công khai |
API ghi trả `401` khi thiếu/token không hợp lệ, `403` khi token hợp lệ nhưng không đủ quyền. Các role `own`, `adp`, `mem`, `cus` và service token (`u = svc`) không cấp quyền quản lý catalog; tên `Admin`/`admin` không thay thế mã `adm` trong token.

**Dữ liệu canonical và specification schema có version**:
- `EquipmentType` là dữ liệu platform canonical dùng chung, không còn thuộc tính `OrganizationId` hay logic ownership theo organization. Request gửi `organizationId` bị từ chối `400 Bad Request`.
- Specification schema có vòng đời riêng theo version (`EquipmentTypeSpecSchema`), lưu định nghĩa `fields` bằng PostgreSQL `jsonb`.
- Mỗi equipment type có tối đa một bản draft schema (`PublishedAt = null`).
- Publish schema cập nhật `CurrentSchemaVersion` của equipment type. Equipment type chỉ được publish khi đã có schema được publish.
- Response detail (`GET /equipment-types/{id}`) bổ sung `primarySpecs: [{ key, label, unit }]`, trích xuất các field `primary=true` của current schema và sắp xếp theo `displayOrder`, rồi `key` (ordinal).
- Endpoint canonical validation `POST /equipment-types/{id}/specifications/validate` xác thực dữ liệu đặc tả kỹ thuật theo schema version đã publish, nhận `{ schemaVersion, specValues }` và trả `200 { valid, errors }` với cấu trúc lỗi `{ field, code, message }` (`REQUIRED`, `TYPE_MISMATCH`, `OUT_OF_RANGE`, `INVALID_ENUM_OPTION`, `UNKNOWN_FIELD`).

**Dữ liệu canonical Brand**:
- `Brand` là dữ liệu platform canonical dùng chung toàn platform, do Admin quản lý, không có Organization ownership. Request gửi `organizationId` bị từ chối `400 Bad Request`.
- Bảng `catalog.Brands`: `Id` (UUID, sinh ở ứng dụng), `Code` (trim/uppercase, tối đa 50 ký tự), `Name` (Unicode NFC, trim/gom khoảng trắng, tối đa 200 ký tự), `Slug` (trim/lowercase, tối đa 150 ký tự dạng ASCII alphanumeric phân cách bằng dấu `-`), `LogoMediaId` (UUID, tùy chọn), `IsActive` (boolean, tạo mới mặc định active), `CreatedAt` (UTC), `UpdatedAt` (UTC).
- Unique index `IX_Brands_Code` và `IX_Brands_Slug` áp dụng cho cả Brand inactive, trùng lặp trả về `409 Conflict`.
- Concurrency token PostgreSQL `xmin` ngăn mất cập nhật khi ghi đồng thời, trả về `409 Conflict`.
- Logo quản lý qua MediaService: verify Ready, MIME `image/jpeg`, `image/png`, `image/webp`, uploader khớp operator trước khi gọi `SetVisibilityAsync(..., "public")` và lưu Brand. Không verify lại khi PATCH giữ nguyên logo hoặc gỡ logo (`logoMediaId: null`). GET dựng URL qua `MediaUrlResolver` không gọi MediaService. Nếu database lưu thất bại sau khi đã public media, media asset vẫn giữ trạng thái public (đây là giới hạn chấp nhận được để tránh giữ transaction DB trong khi gọi external HTTP MediaService).
- Disable/Enable có tính idempotent: gọi lại cùng trạng thái không thay đổi timestamp `UpdatedAt`.
## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
# Đặt POSTGRES_PASSWORD riêng trong .env; đổi POSTGRES_PORT nếu cổng đã được dùng.
docker compose up -d
```

PostgreSQL lắng nghe ở cổng cấu hình trong `.env`. File `.env` không được commit.

## Chạy API cục bộ

Nạp thông tin PostgreSQL và JWT từ `.env` vào môi trường của tiến trình API:

```bash
set -a
. ./.env
set +a
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
dotnet run --project CatalogService/src/Api/CatalogService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build CatalogService.sln
dotnet test CatalogService.sln
```

## Quy trình Triển khai và Rollback (AddEquipmentTypeSpecSchemaAndDropOrganizationId)

Migration `AddEquipmentTypeSpecSchemaAndDropOrganizationId` chuyển `EquipmentType` thành canonical platform data, loại bỏ cột ownership `OrganizationId` và bổ sung hệ thống specification schema có versioning (`EquipmentTypeSpecSchema`, `CurrentSchemaVersion`).

### 1. Khoảng dừng ghi và Backup
1. **Khoảng dừng ghi**: Chuyển API sang chế độ bảo trì hoặc tạm dừng tiếp nhận các tác vụ ghi trên CatalogService trước khi chạy migration. Dừng phiên bản API cũ trong cửa sổ triển khai vì không tương thích với việc drop column `OrganizationId`.
2. **Sao lưu đầy đủ**: Tạo bản sao lưu toàn vẹn cơ sở dữ liệu trước khi thực hiện:
   ```bash
   pg_dump -h <host> -p <port> -U <user> -Fc -d catalogservice > catalogservice_pre_specschema.dump
   ```

### 2. Migration và Backfill
Migration thực hiện trong cùng một transaction:
1. Tạo bảng `EquipmentTypeSpecSchemas` và partial unique index bảo đảm tối đa một draft cho mỗi equipment type.
2. Thêm cột `CurrentSchemaVersion` vào `EquipmentTypes`.
3. Tự động backfill mỗi `EquipmentType` hiện có một **published v1** với `{"fields":[]}` và cập nhật `CurrentSchemaVersion=1`. Timestamps schema lấy thời điểm migration (`NOW()`), bảo toàn nguyên vẹn ID, Code, Status, timestamps của type, Category và Media.
4. Drop index `IX_EquipmentTypes_OrganizationId` và drop column `OrganizationId`.
5. Thiết lập composite foreign key `FK_EquipmentTypes_EquipmentTypeSpecSchemas_CurrentSchema` trên `(Id, CurrentSchemaVersion)`.

Chạy migration:
```bash
dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
```

### 3. Deploy và Mở ghi
1. Khởi động phiên bản API mới.
2. Chạy smoke test xác thực các endpoint public, admin CRUD và spec-schema endpoints.
3. Mở lại luồng ghi cho hệ thống.

### 4. Quy trình Rollback
Do việc drop column `OrganizationId` và quản lý schema không thể hoàn nguyên tự động, phương thức `Down` ném `NotSupportedException`. Khi cần rollback:
1. Dừng tiến trình API mới.
2. Khôi phục cơ sở dữ liệu từ bản sao lưu:
   ```bash
   pg_restore -h <host> -p <port> -U <user> -d catalogservice --clean catalogservice_pre_specschema.dump
   ```
3. Khởi động lại phiên bản API trước đó.

## Quy trình Triển khai (AddBrands)

Migration `AddBrands` bổ sung bảng `catalog.Brands` và các unique index đi kèm mà không ảnh hưởng tới dữ liệu của các bảng hiện có (`EquipmentTypes`, `Categories`, `EquipmentTypeMedia`, `EquipmentTypeSpecSchemas`).

### 1. Chạy migration trước khi deploy
Chạy forward migration:
```bash
dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
```

### 2. Quy trình Rollback
Phương thức `Down` của migration `AddBrands` ném `NotSupportedException` nhằm từ chối rollback phá hủy dữ liệu. Khi rollback phiên bản ứng dụng, bảng `catalog.Brands` được giữ nguyên trong cơ sở dữ liệu.
## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/catalog/develop`, rồi build và test trước khi push.

## Deploy qua Gateway

Build hai artifact từ cùng commit Catalog đã commit:

```bash
export CATALOG_RELEASE_SHA="$(git rev-parse HEAD)"
export IMAGE_NAMESPACE=your-org
# Thay your-org bằng namespace GHCR của bạn; đăng nhập registry trước khi push.
docker build --target runtime -t "ghcr.io/$IMAGE_NAMESPACE/dprental-catalog:$CATALOG_RELEASE_SHA" .
docker build --target migrator -t "ghcr.io/$IMAGE_NAMESPACE/dprental-catalog-migrator:$CATALOG_RELEASE_SHA" .
docker push "ghcr.io/$IMAGE_NAMESPACE/dprental-catalog:$CATALOG_RELEASE_SHA"
docker push "ghcr.io/$IMAGE_NAMESPACE/dprental-catalog-migrator:$CATALOG_RELEASE_SHA"
```

Runtime chạy non-root trên cổng 8080; Docker healthcheck gọi `/health/ready`. Migrator chạy EF Core
10.0.12 và nhận `ConnectionStrings__Postgres` qua environment, sử dụng history table
`catalog.__EFMigrationsHistory`. Migrator không cần JWT hoặc private key.

Đặt hai image tags vào `release.env` của deployment platform, cập nhật Compose từ `deploy/dev`,
chạy `catalog-migrate` trước rồi mới khởi động Catalog. Gateway phải dùng phiên bản chứa các route
`/api/v1/catalog/categories` và `/api/v1/catalog/equipment-types`. Không công khai port Catalog lên VPS.

Catalog xác thực JWT trực tiếp. Các biến bắt buộc:

- `ConnectionStrings__Postgres`
- `Auth__Jwt__Issuer`, `Auth__Jwt__Audience` (khớp Auth và Gateway)
- `Auth__Jwt__Keys__0__KeyId` (khớp kid của token)
- `Auth__Jwt__Keys__0__PublicKeyPem=/run/secrets/auth_jwt_public_key`

Chỉ mount public key. Khi Auth đổi signing key, cập nhật key ring Catalog trước khi phát hành token
bằng key mới và giữ key cũ đến khi token cũ hết hạn. Catalog hiện dùng static key ring, không tải JWKS.
Quy trình backup, migration và smoke test nằm trong `deploy/dev/README.md` của platform repository.

## CI/CD CatalogService cho dev

Workflow [catalogservice-dev.yml](.github/workflows/catalogservice-dev.yml) chạy khi push vào
`catalog/develop-ci-cd`. Mẫu publish/SSH deploy tham chiếu `media/develop-ci-cd`; pipeline chạy trực tiếp
publish rồi deploy, kiểm tra build/test .NET nằm ở pre-push hook local. Hai image runtime và migrator dùng cùng
full commit SHA, không dùng `latest`:

```text
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-catalog:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-catalog-migrator:<git-sha>
```

Trong GitHub Environment `dev`, dùng variable `GHCR_IMAGE_NAMESPACE` (namespace viết thường, ví dụ
`huutawn`) và cùng các secrets với Media/Auth:

| Secret | Giá trị |
| --- | --- |
| `DEV_VPS_HOST` | Host/IP VPS |
| `DEV_VPS_USER` | User deploy có quyền Docker, đọc `.env`, ghi thư mục deploy và `release.env` |
| `DEV_VPS_SSH_PRIVATE_KEY` | SSH deploy key |
| `DEV_VPS_KNOWN_HOSTS` | Host key đã xác nhận của VPS |
| `DEV_VPS_PORT` | Tùy chọn, mặc định `22` |

`GITHUB_TOKEN` được GitHub cấp tự động. Nếu package Catalog đã được push thủ công, cấp repository
workflow quyền Write trong Actions access của cả hai package. Workflow dùng concurrency group
`dprental-dev` như Media/Auth để tránh các workflow trong cùng repository ghi `release.env` đồng thời.
Không đưa PostgreSQL password, JWT private key hoặc `.env` lên GitHub.

Script [deploy-catalogservice.sh](scripts/deploy-catalogservice.sh) chạy qua SSH stdin, login GHCR bằng
Docker config tạm và chỉ cập nhật hai image Catalog trong `release.env`. Script validate Compose,
pull image, start PostgreSQL, stop Catalog, backup schema `catalog` nếu có, chạy migrator, recreate
Catalog, chờ Docker health rồi kiểm tra readiness nội bộ, Gateway readiness và hai public GET.
Backup mặc định ở `/opt/dprental/dev/backups/catalog`, custom format `.dump`, directory mode 700,
file mode 600; gồm cả `catalog.__EFMigrationsHistory`. Lần deploy đầu chưa có schema sẽ bỏ qua backup.
Backup/migration lỗi dừng deploy; nếu Catalog đã stop thì service vẫn dừng để xử lý lỗi. Không tự
rollback image hoặc schema vì refactor xóa supplier columns. Backup retention/restore do operator quản lý.

### Chuẩn bị `/opt/dprental/dev` trước push đầu tiên

1. **Compose:** đồng bộ các phần Catalog trong `deploy/dev/compose.yaml` của platform:
   `catalog-service`, `catalog-migrate` (profile `migration`), public-key secret và network `services`.
   Gateway cần routes 8..13 trỏ `catalog-service:8080` và image Gateway chứa route Catalog.
   Runtime không expose host port; migrator dùng ENTRYPOINT trong image, không cần override kiểu Media.
2. **`.env`:** không cần biến Catalog mới. Kiểm tra các biến sẵn có `DEV_DOMAIN`, `POSTGRES_DB`,
   `POSTGRES_USER`, `POSTGRES_PASSWORD`, `AUTH_JWT_ISSUER`, `AUTH_JWT_AUDIENCE`,
   `AUTH_JWT_ACTIVE_KEY_ID`, `CORS_ALLOWED_ORIGIN_0`. Giữ DB hiện tại; Catalog dùng schema `catalog`.
   Public key của secret `auth_jwt_public_key` phải khớp active key id. Dùng `Staging` cho dev deploy.
3. **`release.env`:** thêm hai dòng dưới trước khi cập nhật Compose. Namespace phải khớp variable
   `GHCR_IMAGE_NAMESPACE`; pipeline thay tag `bootstrap` bằng SHA vừa publish trước khi pull.

   ```dotenv
   CATALOG_SERVICE_IMAGE=ghcr.io/huutawn/dprental-catalog:bootstrap
   CATALOG_MIGRATOR_IMAGE=ghcr.io/huutawn/dprental-catalog-migrator:bootstrap
   ```

   Giữ các image service khác. Cập nhật `GATEWAY_IMAGE` tới revision đã có route Catalog và deploy
   Gateway trước lần Catalog release đầu tiên. Script Catalog không tự deploy Gateway hoặc sync Compose.
4. User SSH cần quyền ghi thư mục `/opt/dprental/dev` vì script thay `release.env` bằng atomic rename,
   và cần quyền tạo `backups/catalog`. Đặt `release.env` mode 600; không commit release/secrets thật.
5. Kiểm tra render trên VPS:

   ```bash
   cd /opt/dprental/dev
   docker compose --env-file .env --env-file release.env config --quiet
   ```

Sau khi chuẩn bị, commit và push nhánh `catalog/develop-ci-cd` sẽ kích hoạt pipeline. Workflow không
chạy cho PR; đặt branch rules nếu cần chỉ release sau merge. Không chạy auto-release đầu tiên trước
khi public key và Gateway routes đã sẵn sàng.

Kiểm tra script tại local (Docker/HTTP được mô phỏng, không deploy VPS):

```bash
bash -n scripts/deploy-catalogservice.sh scripts/tests/deploy-catalogservice.test.sh
bash scripts/tests/deploy-catalogservice.test.sh
```
