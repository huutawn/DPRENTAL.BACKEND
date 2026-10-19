# RecommendationService

Dịch vụ RecommendationService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) chuẩn hóa theo RentalService.

## Cấu trúc

```text
RecommendationService/
  src/
    Common/          # Kiểu dùng chung, Result/Error và assembly marker
    Domain/          # Entities, IRepositories, Repositories, Migrations, DependencyInjection
    Application/     # Background, DTOs, IServices, Kafka, Services, Validation
    Api/             # Controllers, Exceptions, Filters, Properties, Program.cs
  tests/             # Kiểm thử
Directory.Build.props
Directory.Packages.props
Dockerfile
docker-compose.yml
docs/                # Tài liệu kỹ thuật
scripts/             # Scripts hỗ trợ và Git hooks
```

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
dotnet run --project RecommendationService/src/Api/RecommendationService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build RecommendationService.sln
dotnet test RecommendationService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/recommendation/develop`, rồi build và test trước khi push.

## Common, Kiến trúc và Xử lý lỗi

Service sử dụng `Result`/`Result<T>` và `Error`/`ErrorType` (theo chuẩn RentalService).
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang HTTP status codes (400, 401, 403, 404, 409, 410, 422, 500). Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, bắt các ngoại lệ ngoài dự kiến và trả về RFC 7807 ProblemDetails
kèm `traceId` an toàn ở HTTP 500; không làm lộ exception message, stack trace hay dữ liệu nhạy cảm.

Repository contracts được định nghĩa nhỏ gọn theo từng use case hoặc aggregate tại `Domain/IRepositories`;
không sử dụng generic CRUD (`IRepo<TEntity, TKey>`) để tránh phụ thuộc máy móc và vi phạm ISP.
EF Core DbContext và repository implementations nằm tại `Domain/Repositories`, được đăng ký DI
qua extension method `AddRecommendationServiceDomainRepositories` trong namespace
`Dprental.RecommendationService.Domain.DependencyInjection`.

Các tác vụ chạy ngầm (`Application/Background`) và tích hợp sự kiện phân tán (`Application/Kafka`)
tuân theo quy chuẩn của RentalService: đóng vai trò dispatch và transport, xử lý ACK/retry/deduplication,
chuyển tiếp toàn bộ xử lý nghiệp vụ sang Application Service và không trực tiếp thao tác persistence.

Cấu trúc persistence này là **ngoại lệ kiến trúc đã chọn** theo bố cục tham chiếu của RentalService
(không tách project Infrastructure riêng). Tầng Application và API controllers tuyệt đối không được
thao tác persistence trực tiếp hay inject `DbContext`. Mọi truy xuất dữ liệu từ Application phải
thông qua repository abstraction được inject qua DI container.
