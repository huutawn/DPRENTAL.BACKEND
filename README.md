# ReviewService

Dịch vụ ReviewService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tham khảo cấu trúc RentalService.

## Cấu trúc

```text
ReviewService/
  src/
    Common/          # Kiểu dùng chung, Result/Error và utility
    Domain/          # Entities, repository contracts và EF Core persistence
    Application/     # DTOs, services, mapping
    Api/             # Controllers, Filters, HTTP composition root
  tests/             # Kiểm thử
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ và Git hooks
```

Cấu trúc chi tiết và trách nhiệm từng thư mục: [docs/README.md](docs/README.md).
Quy tắc làm việc trong repository: [AGENTS.md](AGENTS.md).

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
# Đặt POSTGRES_PASSWORD riêng trong .env; đổi POSTGRES_PORT nếu cổng đã được dùng.
docker compose up -d
```

PostgreSQL lắng nghe ở cổng cấu hình trong `.env`. File `.env` không được commit.

## Chạy API cục bộ

Nạp thông tin PostgreSQL từ `.env` vào môi trường của tiến trình API:

```bash
set -a
. ./.env
set +a
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
dotnet run --project ReviewService/src/Api/ReviewService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build ReviewService.sln
dotnet test ReviewService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/review/develop`, rồi build và test trước khi push.

## Common và xử lý lỗi

Template có `Result`/`Result<T>` và `Error`/`ErrorType` (tham khảo AuthService).
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang 400/401/403/404/409/410/422/500. Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, trả 500 ProblemDetails kèm `traceId`,
không trả exception message hoặc payload; request bị hủy không chuyển thành lỗi nghiệp vụ.

`Domain/IRepositories/IRepo<TEntity, TKey>` là contract CRUD có CancellationToken,
không phụ thuộc EF/HTTP. Repository theo aggregate chỉ kế thừa khi cần đủ các thao tác;
nếu không, định nghĩa contract nhỏ theo use case. Implementation persistence ở
`Domain/Repositories` theo bố cục hiện tại của RentalService; không sinh generic EF
repository hoặc entity nghiệp vụ giả. `GetAllAsync` chỉ dùng cho tập dữ liệu nhỏ;
với dữ liệu lớn, contract chuyên biệt phải hỗ trợ phân trang.

Template test kiểm tra Result invariant, ánh xạ error và exception qua HTTP ở
Production, gồm fallback khi client không nhận JSON. Không sao chép mã lỗi,
constraint database hay dependency đặc thù nghiệp vụ AuthService.

## CI/CD dev

Workflow `.github/workflows/reviewservice-dev.yml` chạy khi push vào
`review/develop-ci-cd`, theo pipeline Rental: validate deployment scripts, publish
runtime/migrator, kiểm tra quyền đọc GHCR rồi deploy bằng entrypoint quản trị.
Hai image `dprental-review` và `dprental-review-migrator` dùng tag full commit SHA.
Environment `dev` cần `GHCR_IMAGE_NAMESPACE` và `DEV_DEPLOY_ENABLED=true`;
runner mang label `dprental-deploy`. Credentials đi qua stdin, không qua argv.

Gateway được cập nhật trên `gateway/develop`: ba GET listing/supplier/summary
được anonymous, các API Review/admin còn lại cần Bearer. Review tự kiểm tra
quyền admin/người dùng. CORS cho phép `If-Match` và expose `ETag`. Các route
Review đứng sau Rental trong thứ tự merge nên giữ nguyên chỉ số route cũ.

Pre-deploy ở `/opt/dprental/dev` dùng database/role `reviewservice` riêng trên
PostgreSQL hiện có, cấu hình `REVIEW_POSTGRES_DB`, `REVIEW_POSTGRES_USER`,
`REVIEW_POSTGRES_PASSWORD`, cùng hai image mapping `REVIEW_SERVICE_IMAGE` và
`REVIEW_MIGRATOR_IMAGE`. Secret chỉ nằm trong `.env` root-only. Các API key
Rental/Media lấy từ cấu hình nội bộ hiện có; JWT dùng JWKS của AuthService.
Compose và examples tại `../deploy/dev` là nguồn cấu hình triển khai.

Pre-deploy chỉ chuẩn bị cấu hình/database rỗng; không chạy migration hoặc
restart container. Tag `predeploy` là placeholder, không phải image phát hành.
Trước lần deploy đầu, publish và deploy Gateway chứa routes Review, rồi push
Review workflow. `dprental-deploy review <SHA>` tạo candidate image mappings
từ SHA thực tế, pull image, kiểm tra dependencies, backup database Review,
chạy migrator, recreate riêng Review và kiểm tra readiness/API qua Gateway.
Chỉ ghi release thành công sau khi các checks pass.

Deployment dùng lock/journal chung và không tự rollback schema. Nếu lỗi sau
mutation, xem `/opt/dprental/dev/.deployment-pending` cùng journal tương ứng
trong `.deployments`; quản trị viên cần đánh giá database/container và recovery
trước khi gỡ pending. Không tự xóa pending để retry mù. Backup cấu hình trước
pre-deploy nằm trong `/opt/dprental/dev/backups/review-predeploy-*`.
