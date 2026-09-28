# MediaService

Dịch vụ Media cho hệ sinh thái DPRENTAL, được xây dựng bằng .NET 10 theo kiến trúc phân lớp sạch (Clean Architecture) tương đồng với AuthService. Dịch vụ nhận file trong một request multipart, lưu trên MinIO, xác minh metadata trong PostgreSQL và cung cấp API nội bộ cho Business Service kiểm tra MediaId trước khi lưu tham chiếu.

## Upload file trong một request

Khi chạy bằng Docker Compose chung tại thư mục `DPRENTAL/`, mở Swagger UI ở `http://localhost:5050/swagger/index.html` rồi chọn AuthService hoặc MediaService trong menu. Login ở AuthService tạo cookie `accessToken` trên Gateway origin; Gateway xác thực và chuyển thành Bearer để MediaService xác thực lại bằng JWKS. API `/verify`, `/access` và `/visibility` vẫn yêu cầu service token theo scope tương ứng. Xem hướng dẫn bootstrap principal local trong `Gateway/README.md`.

1. **Client đã đăng nhập gọi `POST /api/v1/media/uploads`** bằng `multipart/form-data`. Form gồm `file` và `sha256Checksum` tùy chọn. Không cần tạo entity trước khi upload. Có thể gửi header `Idempotency-Key` để retry cùng file mà không tạo Media khác. Swagger UI cho phép chọn file trực tiếp.

2. **MediaService xử lý file trong request đó**: áp dụng giới hạn theo định dạng file, ghi file vào MinIO, xác minh signature/MIME/checksum, rồi chuyển Media sang `Ready`. Response HTTP 200 gồm `mediaId`, metadata và `previewUrl` ký ngắn hạn để người upload xem file đã lưu. Nếu việc ký URL lỗi sau khi file đã lưu, `previewUrl` là null nhưng upload vẫn thành công.

3. **Business Service gọi `GET /api/v1/media/{mediaId}/verify`** bằng service token để xác nhận MediaId đang `Ready`, đúng người upload và định dạng phù hợp với nghiệp vụ trước khi lưu tham chiếu vào entity nghiệp vụ. Business Service giữ quan hệ entity–media và kiểm tra quyền người dùng trước các thao tác đọc hoặc công bố.

Migrations `RemoveMediaOwner` và `RemoveMediaPurpose` xóa các cột owner cũ cùng cột purpose khỏi `media.media_assets`; dữ liệu trong các cột này sẽ mất. Trước khi áp dụng trên database đã có dữ liệu, dừng phiên bản MediaService cũ và sao lưu database. Muốn quay lại bản cũ phải khôi phục backup, không thể đảo migration để lấy lại owner hoặc purpose. Bản mới không tương thích với ứng dụng cũ còn đang dùng các cột owner. Idempotency key của request upload cũ có thể trả 409 sau khi bỏ owner/purpose khỏi payload hash; client cần dùng key mới khi gửi request theo contract mới.

Client không cần gọi MinIO trực tiếp hoặc gọi `/complete`. Bản ghi upload nội bộ vẫn phục vụ idempotency, retry và dọn dẹp request bị gián đoạn.

## Lấy thông tin, xem và tải Media

Business Service chỉ lưu `mediaId` trong entity. Khi trả entity, service kiểm tra quyền và có thể lấy metadata bằng `GET /api/v1/media/{mediaId}` (scope `media.read`). Metadata gồm tên file, MIME/dung lượng đã xác minh, status, visibility và thời điểm tạo; không chứa URL hay khóa lưu trữ.

Để xem/tải file Private, Business Service xác nhận người dùng có quyền trên entity **và mediaId thực sự được entity tham chiếu**, rồi gọi `POST /api/v1/media/{mediaId}/access` bằng service token scope `media.read`:

```json
{ "action": "read", "download": false }
```

MediaService trả `{ "mediaId": "...", "url": "...", "expiresAtUtc": "..." }`. Business Service chuyển URL này cho client ở thời điểm cần xem/tải; `download: true` yêu cầu Content-Disposition attachment. URL GET MinIO ký sẵn mặc định hết hạn sau 300 giây (`ObjectStorage:ReadUrlExpirySeconds`, cho phép 1–900 giây). Browser phải truy cập được `ObjectStorage:BrowserEndpoint` qua HTTPS trong môi trường triển khai. URL ký sẵn là quyền tạm thời: có thể chứa bucket, object key ngẫu nhiên và access-key ID theo giao thức S3; không lưu URL, không ghi vào log, không dùng nó làm khóa nghiệp vụ. Bucket luôn Private.

Khi entity được công bố, Business Service gọi `PUT /api/v1/media/{mediaId}/visibility` bằng scope `media.publish`, gửi `visibility` (`public` hoặc `private`) sau khi đã kiểm tra entity đang tham chiếu mediaId. Business Service quyết định file nào có thể Public theo nghiệp vụ; MediaService chỉ yêu cầu file `Ready` và service token scope `media.publish`. URL Public ổn định là `GET /api/v1/media/{mediaId}/content` (thêm `?download=true` nếu cần); MediaService kiểm tra trạng thái mỗi lần gọi rồi chuyển hướng tới URL MinIO ký ngắn hạn. Sau khi đổi về Private, URL Public này không cấp quyền mới; URL đã ký có thể còn hiệu lực cho đến khi hết hạn.

Service token phải có claim `u=svc`, `sub` ánh xạ trong `Jwt:ServicePrincipals` và claim `s` đúng scope (`media.verify`, `media.read` hoặc `media.publish`). AuthService cần cấp các scope này cho service nghiệp vụ tương ứng. Hiện workspace chưa có CatalogService/OrganizationService để tích hợp phía Business Service. MVP upload hỗ trợ JPEG, PNG, WebP và PDF; video chưa được hỗ trợ.

## Cấu trúc Solution

```text
MediaService/
  src/
    Common/          # Kết quả và trạng thái dùng giữa các lớp
    Domain/          # Media entities, PostgreSQL, MinIO và migrations
    Application/     # Upload, xác minh và dọn dẹp
    Api/             # HTTP, JWT, cấu hình và DI
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

Hướng phụ thuộc chuẩn: `Api -> Application -> Domain -> Common`.

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
- `.env` cấu hình `MINIO_CORS_ALLOWED_ORIGINS` nếu trình duyệt cần đọc URL GET ký sẵn bằng `fetch` khác origin.

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

MediaService xác minh JWT RS256 bằng JWKS của AuthService. Môi trường Development đã có issuer, audience và JWKS URI mẫu; chỉnh `Jwt:JwksUri` nếu AuthService chạy ở địa chỉ khác. API xác minh nội bộ chỉ chấp nhận service token có scope `media.verify` và `sub` được ánh xạ trong `Jwt:ServicePrincipals` (ví dụ cấu hình `Jwt:ServicePrincipals:<service-principal-guid>=CatalogService`). Header `X-User-Id` và `X-Service-Name` không cấp quyền truy cập.

Trước khi chạy API trên cơ sở dữ liệu mới, áp dụng migration. Factory thiết kế chỉ đọc `ConnectionStrings__Postgres` từ môi trường; không lưu mật khẩu trong source:

```bash
set -a
. ./.env
set +a
ConnectionStrings__Postgres="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
  dotnet ef database update --project MediaService/src/Domain/MediaService.Domain.csproj
```

2. Khởi chạy API:

```bash
dotnet run --project MediaService/src/Api/MediaService.Api.csproj
```

Trong môi trường Development, mở `/swagger` trên địa chỉ mà API in ra khi khởi chạy. Chọn `POST /api/v1/media/uploads`, nhập user bearer token bằng nút **Authorize**, chọn file rồi bấm **Execute**. Response trả `mediaId` khi file đã `Ready`.

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
