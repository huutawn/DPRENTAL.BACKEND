# __SERVICE_NAME__

Dịch vụ __SERVICE_NAME__ cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
__SERVICE_NAME__/
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
dotnet run --project __SERVICE_NAME__/src/Api/__SERVICE_NAME__.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build __SERVICE_NAME__.sln
dotnet test __SERVICE_NAME__.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/__SHORT_NAME__/develop`, rồi build và test trước khi push.
