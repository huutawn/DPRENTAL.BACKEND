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
- **Xác thực JWT RS256**: Xác thực token qua key ring chứa public RSA key (tối thiểu 2048-bit), hỗ trợ Authorization header và cookie `accessToken`. Mặc định yêu cầu authentication, các endpoint discovery và health check mở anonymous.
- **Quan sát (Observability)**: Correlation ID ổn định trong mỗi request và response header `X-Correlation-ID`. ID nằm trong log scope của mọi dòng log và trong extension `correlationId` của `ProblemDetails`; header không hợp lệ (quá 64 ký tự hoặc có ký tự ngoài chữ, số, `-`, `_`, `.`) được thay bằng ID mới. Log console dạng JSON (Development dùng `simple`), ghi kết quả các thao tác ghi của Category/EquipmentType; không log header hay token. Tracing OpenTelemetry W3C với service name `dprental-catalog-service`.
- **Bảo mật mạng**: Forwarded headers với proxy CIDR và cấu hình CORS chặt chẽ.

## Quyền truy cập API

Catalog đọc nguyên claim `r` do AuthService phát hành: `adm` (Admin), `adp` (AdminPlatform), `own` (Owner), `mem` (Member), `cus` (Customer).

| API (tiền tố `/api/v1/catalog`) | Quyền |
| --- | --- |
| `GET /categories`, `GET /categories/{id}` | Công khai |
| `GET /equipment-types`, `GET /equipment-types/{id}` | Công khai |
| `POST /categories`, `PATCH /categories/{id}` | JWT hợp lệ, role `adm` |
| `POST /equipment-types`, `PATCH /equipment-types/{id}` | JWT hợp lệ, role `adm` |
| `POST /equipment-types/{id}/publish`, `POST /equipment-types/{id}/disable` | JWT hợp lệ, role `adm` |
| `GET /health/live`, `GET /health/ready` (không có tiền tố) | Công khai |

API ghi trả `401` khi thiếu/token không hợp lệ, `403` khi token hợp lệ nhưng thiếu `adm`. Các role `adp`, `own`, `mem`, `cus` không cấp quyền quản trị catalog; tên `Admin`/`admin` không thay thế mã `adm` trong token.

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

## Quy trình Triển khai và Rollback (RefactorEquipmentToEquipmentType)

Migration `RefactorEquipmentToEquipmentType` thực hiện refactor cấu trúc từ `Equipment` sang `EquipmentType`, loại bỏ các cột supplier (`OrganizationId`, `Brand`, `DailyPrice`, `RegionId`, `Specifications`).

### 1. Khoảng dừng ghi và Backup
1. **Khoảng dừng ghi**: Chuyển API sang chế độ bảo trì hoặc tạm dừng tiếp nhận các tác vụ ghi (write pause) trên CatalogService trước khi chạy migration. Không chạy phiên bản API cũ trên schema mới.
2. **Sao lưu đầy đủ**: Tạo bản sao lưu toàn vẹn cơ sở dữ liệu trước khi thực hiện:
   ```bash
   pg_dump -h <host> -p <port> -U <user> -Fc -d catalogservice > catalogservice_pre_refactor.dump
   ```

### 2. Preflight và Chạy Migration
Migration chạy trong một transaction khóa ghi `ACCESS EXCLUSIVE` trên các bảng liên quan và tự động kiểm tra trùng `Code` (duplicate-Code preflight). Nếu phát hiện `Code` bị trùng:
- Migration lập tức dừng lại với ngoại lệ `Cannot refactor Equipments to EquipmentTypes: duplicate Code values found: ...`.
- Transaction tự động rollback; cấu trúc schema và dữ liệu hiện tại được bảo toàn nguyên vẹn.

Chạy migration:
```bash
dotnet ef database update --project CatalogService/src/Domain/CatalogService.Domain.csproj --startup-project CatalogService/src/Api/CatalogService.Api.csproj
```

### 3. Deploy và Mở ghi
1. Khởi động phiên bản API mới với base route `/api/v1/catalog/equipment-types`.
2. Chạy smoke test xác thực các endpoint public và admin.
3. Mở lại luồng ghi cho hệ thống.

### 4. Quy trình Rollback
Do các cột supplier bị drop vĩnh viễn không thể khôi phục qua reverse migration, phương thức `Down` ném `NotSupportedException`. Khi cần rollback:
1. Dừng tiến trình API mới.
2. Khôi phục cơ sở dữ liệu từ bản sao lưu:
   ```bash
   pg_restore -h <host> -p <port> -U <user> -d catalogservice --clean catalogservice_pre_refactor.dump
   ```
3. Khởi động lại phiên bản API trước đó.

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
