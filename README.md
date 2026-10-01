# InventoryService

Dịch vụ InventoryService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
InventoryService/
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
dotnet run --project InventoryService/src/Api/InventoryService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build InventoryService.sln
dotnet test InventoryService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/inventory/develop`, rồi build và test trước khi push.

## Hợp đồng dùng chung và xử lý lỗi

Thiết kế tham chiếu AuthService, với một implementation cho mỗi hành vi:

- `Common/Result.cs`: Result và Result<T> cho lỗi dự kiến; giữ API và invariant hiện tại. Truy cập Value của failure ném InvalidOperationException.
- `Common/Error.cs`: Error/ErrorType được tách khỏi Result; giữ nguyên mã enum và factory hiện tại.
- `Domain/IRepositories/IRepo.cs`: hợp đồng đọc theo ID được dùng bởi năm repository. Không cung cấp GetAll hoặc CRUD tự lưu: Inventory ghi qua IInventoryWriteTransaction để giữ advisory lock, idempotency và atomic commit. GetById không kiểm tra quyền; use case theo tổ chức phải dùng GetByIdForOrganizationAsync.
- `Api/Filters/ExpectedErrorFilterExtensions.cs`: controller gọi `this.ToProblem(result.Error)` khi Result thất bại. Validation → 400, Unauthorized → 401, Forbidden → 403, NotFound → 404, Conflict → 409, Expired → 410, Unprocessable → 422, Failure → 500. Error.None bị từ chối.
- `Api/Exceptions/GlobalExceptionHandler.cs`: exception ngoài dự kiến trả 500 ProblemDetails với mã Inventory.Unexpected và traceId; log loại exception cùng traceId, không log message/payload. Hủy request được trả về middleware để xử lý theo lifecycle của kết nối. Không suy đoán lỗi database bất kỳ là conflict; lỗi nghiệp vụ đã nhận diện tiếp tục thuộc transaction/use case.

ProblemDetails cũng được cấu hình traceId cho lỗi tại API boundary. Các primitive trong Common không phụ thuộc ASP.NET; HTTP mapping và exception handling chỉ nằm tại Api. Chưa tách package dùng chung giữa các service hoặc thêm paging khi chưa có use case Application cần chúng.
