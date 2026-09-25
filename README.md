# MediaService

Dịch vụ Media cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService. Dịch vụ cung cấp API tải media trực tiếp từ client lên MinIO Object Storage bằng URL ký sẵn (Presigned PUT URL), lưu trữ và xác minh metadata tệp trong PostgreSQL, cùng API nội bộ cho các Business Service xác thực MediaId trước khi lưu tham chiếu.

## Luồng Tải Media Trực Tiếp (Client Direct Upload)

1. **Khởi tạo phiên (`POST /api/v1/media/uploads`)**:
   - Client đã đăng nhập gửi thông tin tệp: `purpose`, `ownerEntityId`, `fileName`, `contentType`, `sizeBytes`, `sha256Checksum` (tùy chọn) cùng tiêu đề `Idempotency-Key`.
   - MediaService xác định `ownerService` và `ownerEntityType` từ cấu hình purpose (không nhận từ client) và kiểm tra dung lượng/MIME theo policy.
   - Trả về `mediaId`, `uploadId`, `uploadUrl` (URL PUT ký sẵn 15 phút), `method` và `requiredHeaders`.

2. **Tải binary trực tiếp lên MinIO**:
   - Client (Browser/Mobile) thực hiện `PUT` binary trực tiếp lên MinIO thông qua `uploadUrl`, không truyền tải dữ liệu qua MediaService.

3. **Hoàn tất & Xác minh (`POST /api/v1/media/uploads/{uploadId}/complete`)**:
   - Chỉ người tạo phiên mới có quyền gọi Complete.
   - MediaService đọc stream tệp thực tế từ MinIO (ngoài transaction PostgreSQL) để kiểm tra dung lượng, MIME, file signature (magic bytes) và checksum SHA-256 (nếu client đã khai báo).
   - Nếu hợp lệ: chuyển tệp sang khóa lưu trữ chính thức (service-only write key) ngẫu nhiên, chuyển trạng thái MediaAsset thành `Ready` và MediaUploadSession thành `Completed`.
   - Nếu sai định dạng/quá dung lượng/checksum sai: chuyển thành `Rejected` và đưa object tạm vào hàng đợi dọn dẹp.
   - Gọi Complete lại cho phiên đã hoàn tất sẽ trả cùng kết quả (idempotent), không tạo media mới.

4. **Xác minh MediaId nội bộ (`GET /api/v1/media/{mediaId}/verify`)**:
   - Business Service dùng service token gọi endpoint này để kiểm tra MediaId đang ở trạng thái `Ready`, đúng `purpose`, đúng đối tượng và đúng người upload trước khi lưu tham chiếu vào entity nghiệp vụ.

## Cấu trúc Solution

```text
MediaService/
  src/
    Common/          # Kiểu dùng chung, tiện ích nền tảng (dạng khung)
    Domain/          # Domain models, rules (dạng khung, không tạo entity/migration giả định)
    Application/     # Use cases, interfaces (dạng khung, không tạo DTO/service giả định)
    Api/             # HTTP endpoints (/health/live, /health/ready), DI composition root, Options
  tests/
    Api.Tests/       # Kiểm thử tích hợp và unit test
Directory.Build.props
Directory.Packages.props
docker-compose.yml
.env.example
Dockerfile
docs/                # Tài liệu kỹ thuật chi tiết
scripts/             # Script tiện ích và Git hooks
```

Hướng phụ thuộc chuẩn: `Api -> Application -> Domain -> Common`. Các project nghiệp vụ hiện tại được giữ ở dạng khung chuẩn chỉ và sẽ được mở rộng khi có use case nghiệp vụ cụ thể.

## Quản lý NuGet tập trung (CPM)

MediaService sử dụng Central Package Management (CPM) quản lý tại `Directory.Packages.props` ở thư mục gốc:

- Không đặt thuộc tính `Version` trong các file `.csproj`.
- Khai báo version thư viện tại `Directory.Packages.props`.
- Tùy chọn `CentralPackageVersionOverrideEnabled` được đặt thành `false` để đảm bảo đồng nhất phiên bản toàn solution.

## Chạy hạ tầng cục bộ (Docker Compose)

Hạ tầng bao gồm PostgreSQL và MinIO (cùng container khởi tạo bucket tự động):

```bash
cp .env.example .env
docker compose up -d
```

Các cổng mặc định trên máy chủ (chỉ mở cho localhost `127.0.0.1`):
- PostgreSQL: `127.0.0.1:5434` (tránh xung đột cổng `5433` của AuthService)
- MinIO API: `127.0.0.1:9000`
- MinIO Web Console: `127.0.0.1:9001`
- Bucket `dprental-media` được tự động tạo với quyền riêng tư (`private`).

> **Lưu ý quan trọng**: File `.env` chỉ cung cấp biến môi trường cho Docker Compose. Khi chạy API bằng `dotnet run`, bạn cần cấu hình credentials qua .NET User Secrets theo hướng dẫn bên dưới.

## Cấu hình User Secrets & Chạy API

1. Cấp cấu hình kết nối qua .NET User Secrets (dùng cùng thông tin đã khai báo trong `.env`):

```bash
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=127.0.0.1;Port=5434;Database=mediaservice;Username=mediaservice;Password=mediasecret" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:Endpoint" "http://127.0.0.1:9000" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:Bucket" "dprental-media" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:AccessKey" "mediaadmin" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:SecretKey" "mediasecretpassword" \
  --project MediaService/src/Api/MediaService.Api.csproj
```

2. Khởi chạy API:

```bash
dotnet run --project MediaService/src/Api/MediaService.Api.csproj
```

3. Kiểm tra trạng thái sức khỏe:
- Liveness probe: `GET http://localhost:5000/health/live` (hoặc cổng HTTP do ứng dụng lắng nghe)
- Readiness probe: `GET http://localhost:5000/health/ready`

Readiness kiểm tra đồng thời kết nối PostgreSQL và sự tồn tại của bucket MinIO. Nếu thiếu cấu hình bắt buộc, ứng dụng sẽ báo lỗi ngay khi khởi động.

## Kiểm tra trước khi push (Git Hooks)

Cài đặt Git hook một lần cho mỗi máy dev:

Trên Linux/macOS (Bash):
```bash
./scripts/setup-git-hooks.sh
```

Trên Windows (PowerShell):
```powershell
./scripts/setup-git-hooks.ps1
```

Pre-push hook sẽ:
1. Kiểm tra sự tồn tại của branch `origin/media/develop`. Nếu branch đã tồn tại, kiểm tra xem branch hiện tại đã merge commit mới nhất từ `origin/media/develop` chưa.
2. Với lần push đầu tiên (khi `origin/media/develop` chưa có trên remote), hook tự động bỏ qua kiểm tra ancestry.
3. Luôn thực hiện build `MediaService.sln` và chạy toàn bộ bài test ở cấu hình `Release`.

Chạy kiểm tra thủ công:
```bash
./scripts/pre-push-check.sh
```

Chạy kiểm thử cho chính script pre-push:
```bash
./scripts/tests/pre-push-check.test.sh
```

## Docker Artifact

Build Docker image runtime:

```bash
docker build -t mediaservice:runtime -f Dockerfile .
```

Container runtime chạy non-root user `$APP_UID`, mở cổng `8080` và tích hợp healthcheck qua `/health/ready`.

Chi tiết kiến trúc và xử lý sự cố xem thêm tại [docs/README.md](docs/README.md).
