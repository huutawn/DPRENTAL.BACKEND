# CatalogService

Dịch vụ CatalogService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp chuẩn hóa tương đồng với AuthService.

## Cấu trúc

```text
CatalogService/
  src/
    Common/          # PagedResult, PageRequest, Result, Error catalog và kiểu dùng chung
    Domain/          # Entities, DTOs contract (GetEquipmentsRequest, EquipmentListItemDto,...), DbContext và Repositories
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

- **Tiền tố API**: Toàn bộ endpoint của controller tự động ghép tiền tố `api/v1/catalog` (vd: `GET /api/v1/catalog/equipments`).
- **Phân trang và DTO**: Dùng duy nhất `PagedResult<T>` (`items`, `page`, `pageSize`, `totalCount`, `totalPages`) và `PageRequest`. DTO chia sẻ giữa repository, service và controller.
- **Xử lý lỗi**: Kết quả trả về qua `Result` và catalog mã lỗi `CatalogErrors` / `CatalogErrorCode`. Controller chuyển lỗi sang HTTP `ProblemDetails` qua extension tập trung.
- **Xác thực JWT RS256**: Xác thực token qua key ring chứa public RSA key (tối thiểu 2048-bit), hỗ trợ Authorization header và cookie `accessToken`. Mặc định yêu cầu authentication, các endpoint discovery và health check mở anonymous.
- **Quan sát (Observability)**: Correlation ID ổn định trong mỗi request và response header `X-Correlation-ID`. Tracing OpenTelemetry W3C với service name `dprental-catalog-service`.
- **Bảo mật mạng**: Forwarded headers với proxy CIDR và cấu hình CORS chặt chẽ.

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

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/catalog/develop`, rồi build và test trước khi push.
