# RentalService

Dịch vụ RentalService cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService.

## Cấu trúc

```text
RentalService/
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
dotnet run --project RentalService/src/Api/RentalService.Api.csproj
```

API dùng cổng riêng trong `launchSettings.json`. Cấu hình kết nối trống sẽ làm API dừng ngay lúc khởi động.

## Build và Test

```bash
dotnet build RentalService.sln
dotnet test RentalService.sln
```

## Git hook

Sau khi khởi tạo Git trong thư mục service, chạy `./scripts/setup-git-hooks.sh` (hoặc bản `.ps1` trên PowerShell). Hook kiểm tra branch nền `origin/rental/develop`, rồi build và test trước khi push.

## Common và xử lý lỗi

Template có `Result`/`Result<T>` và `Error`/`ErrorType` (tham khảo AuthService).
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang 400/401/403/404/409/410/422/500. Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, trả 500 ProblemDetails kèm `traceId`,
không trả exception message hoặc payload; request bị hủy không chuyển thành lỗi nghiệp vụ.

`Domain/IRepositories/IRepo<TEntity, TKey>` là contract CRUD có CancellationToken,
không phụ thuộc EF/HTTP. Repository theo aggregate chỉ kế thừa khi cần đủ các thao tác;
nếu không, định nghĩa contract nhỏ theo use case. Implementation persistence ở
`Domain/Repositories` theo bố cục hiện tại của AuthService; không sinh generic EF
repository hoặc entity nghiệp vụ giả. `GetAllAsync` chỉ dùng cho tập dữ liệu nhỏ;
với dữ liệu lớn, contract chuyên biệt phải hỗ trợ phân trang.

Template test kiểm tra Result invariant, ánh xạ error và exception qua HTTP ở
Production, gồm fallback khi client không nhận JSON. Không sao chép mã lỗi,
constraint database hay dependency đặc thù nghiệp vụ AuthService.

## Mô hình Rental Domain MVP

Dịch vụ triển khai mô hình nghiệp vụ canonical theo AGENTS.md và thiết kế mới:
- Toàn bộ ValueObjects đã được inline vào 11 entities sở hữu kèm DataAnnotations và Fluent API trong `RentalServiceDbContext`.
- Các enums nghiệp vụ (`RentalCartStatus`, `RentalOrderStatus`, `RentalItemStatus`, `RentalUnitAssignmentStatus`, `UnitCondition`, `DeductionProposalStatus`, `DisputeStatus`, `ActorRole`) và `ErrorType` được đặt tập trung tại `Common/Enums`.
- Ngoại lệ kiến trúc: EF Core persistence được đặt trong `Domain/Repositories` (tương đồng AuthService), không tách riêng project Infrastructure.
- Hai Aggregate Roots:
  1. `RentalCart`: Quản lý các dòng `RentalCartItem`, checkout all-or-nothing, abandon và optimistic concurrency qua `Version`.
  2. `RentalOrder`: Quản lý `RentalItem`, `RentalUnitAssignment`, `RentalStateTransition`, `RentalTermsAcceptance`, `HandoverRecord`, `ReturnRecord`, `DeductionProposal`, `Dispute`. Trạng thái đơn được tổng hợp authoritative từ các items (`RecalculateStatus`).
## API Lịch thuê cá nhân (Renter Calendar)

- Endpoint: `GET /api/v1/rental/me/calendar?month=YYYY-MM&page=1&pageSize=50`
- Yêu cầu xác thực: JWT Bearer Token (Human authenticated). Danh tính người thuê lấy authoritative từ `sub`. Admin cũng chỉ truy cập lịch của chính mình trên `/me`.
- Tham số truy vấn:
  - `month`: Định dạng `YYYY-MM` (mặc định tháng hiện tại theo giờ Việt Nam).
  - `page`: Trang hiện tại (mặc định 1).
  - `pageSize`: Kích thước trang (mặc định 50, tối đa 100).
- Dữ liệu trả về:
  - `days`: Đầy đủ các ngày trong tháng kèm `rentedQuantity` (tổng quantity của các RentalItem khớp ngày, tính trước phân trang).
  - `entries`: Danh sách các mục thuê (`orderId`, `orderCode`, `rentalItemId`, `equipmentListingId`, tên snapshot `listingTitle`/`listingTitleSnapshot`, `quantity`, `startDate`, `endDate`, `status`) kèm metadata phân trang.
  - Các trạng thái RentalItem được hiển thị: `AwaitingHandover`, `InRental`, `PendingSettlement`, `Completed`.
  - Ngày thuê được lưu end-exclusive trong Rental và tự động chuyển đổi sang end-inclusive cho calendar projection.
  - Tuyệt đối không để lộ thông tin physical asset, serial number, allocation reference hoặc lịch bảo trì của Supplier.
