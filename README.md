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
## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/catalog/develop`, rồi build và test trước khi push.
