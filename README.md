# DPRENTAL API Gateway

API Gateway .NET 10 cho DPRENTAL. Gateway chỉ là reverse proxy và security boundary bổ sung; không chứa nghiệp vụ hoặc tham chiếu project tới service khác.

## Cấu trúc Project

Gateway được tổ chức thành project chạy Ocelot và project kiểm thử riêng biệt:

```text
Gateway/
  ocelot/
    Authentication/    # JWT RS256 và JWKS của AuthService, ApiKeyRoutePolicy
    Errors/            # GatewayError, GatewayExceptionMiddleware, Error response writer
    Health/            # Health checks (JwksReadinessHealthCheck, GatewayConfigurationHealthCheck)
    Proxy/             # Ocelot composition và configuration validation
    Security/          # IdentityContext, IdentityExtractor, Security middlewares, DelegatingHandlers, HeaderSanitizer, GatewayHeaderConstants, GatewaySecurityOptions
  tests/ApiGateway.Tests/
```
Gateway nhận JWT được AuthService ký và chỉ tải public key từ JWKS. Không đặt private key, token, hoặc password vào `appsettings*.json`.

## CatalogService

Gateway chuyển tiếp nguyên path/query/body của `/api/v1/catalog` tới `catalog-service:8080`.
Bốn route GET công khai là categories, categories/{id}, equipment-types và equipment-types/{id}.
Template detail kết thúc bằng `/` để Ocelot chỉ match một segment ID; URL caller không cần slash cuối.
Các request Catalog còn lại yêu cầu JWT Bearer với audience `Gateway:Authentication:Jwt:Audience`;
Catalog tự kiểm tra role `Admin` cho POST/PATCH/publish/disable. Gateway không thêm nghiệp vụ Catalog.

Token `accessToken` trong cookie được chuyển thành Authorization Bearer; browser cookies và identity
headers không được chuyển tới Catalog. Token dành riêng cho `inventory-api` không được dùng cho Catalog.
Gateway tiếp tục chuyển nguyên status và ProblemDetails từ Catalog.

Chạy Gateway trực tiếp ở Development dùng Catalog `http://localhost:47297`. Docker Compose của Gateway
ánh xạ routes 8..13 qua `CATALOG_SERVICE_HOST`, `CATALOG_SERVICE_PORT`, `CATALOG_SERVICE_SCHEME`;
mặc định `catalog-service:8080` trên network `dprental-network`. Catalog phải tham gia cùng network này.
Khi chạy Catalog trên host, đặt host thành `host.docker.internal` và port thành `47297`.

Swagger UI Development có CatalogService tại `/catalog/swagger/v1/swagger.json`. Catalog chỉ xuất Swagger
ở Development; khi Catalog chạy Staging/Production, route tài liệu trả 401 nếu anonymous hoặc 404 với
token hợp lệ. API Catalog vẫn hoạt động.

VPS sử dụng Compose trong `deploy/dev` của platform repository: thêm `CATALOG_SERVICE_IMAGE` và
`CATALOG_MIGRATOR_IMAGE` vào `release.env`, mount public key Auth, chạy catalog-migrate trước khi start
catalog-service và deploy Gateway chứa routes mới. Xem quy trình Catalog trong `deploy/dev/README.md`.
Gateway readiness chỉ kiểm tra JWKS/configuration, không thay thế readiness của Catalog.

## Chạy local & Kiểm thử

### Chạy automated test suite

Chạy toàn bộ bộ kiểm thử tự động (unit tests và integration tests bao gồm JWT, CORS, Timeout):

```bash
dotnet test Gateway/Gateway.sln
```

### Khởi động Gateway chạy local (kết nối AuthService trên máy host)

Khởi động AuthService tại `http://localhost:64837` (cổng HTTP chuẩn theo `launchSettings.json` của AuthService), sau đó khởi động Gateway với môi trường `Development`:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project Gateway/ocelot --urls "http://127.0.0.1:5050"
```

Hoặc dùng Visual Studio / Rider / VS Code với profile `Gateway.Api` đã cấu hình sẵn cổng `http://localhost:5050`.
### Build & Chạy thử bằng Docker

Build Docker image multi-stage .NET 10:

```bash
docker build -t dprental-gateway:latest -f Dockerfile .
```

Chạy container Gateway kết nối tới AuthService:

```bash
docker run -d --name dprental-gateway \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e Gateway__Authentication__Jwt__Issuer=dprental-authservice-dev \
  -e Gateway__Authentication__Jwt__Audience=dprental-clients \
  -e Gateway__Authentication__Jwt__JwksUri=http://auth-service:5000/.well-known/jwks.json \
  -e Routes__0__DownstreamHostAndPorts__0__Host=auth-service \
  -e Routes__0__DownstreamHostAndPorts__0__Port=5000 \
  -e Routes__1__DownstreamHostAndPorts__0__Host=auth-service \
  -e Routes__1__DownstreamHostAndPorts__0__Port=5000 \
  -e Routes__2__DownstreamHostAndPorts__0__Host=auth-service \
  -e Routes__2__DownstreamHostAndPorts__0__Port=5000 \
  -e Routes__3__DownstreamHostAndPorts__0__Host=auth-service \
  -e Routes__3__DownstreamHostAndPorts__0__Port=5000 \
  -e Gateway__Security__Cors__AllowedOrigins__0=http://localhost:3000 \
  dprental-gateway:latest
```

### Quick start — Docker stack tổng

Từ thư mục gốc `DPRENTAL/` (nơi có `compose.yaml`), cần có ba checkout con `Gateway/`, `AuthService/` và `MediaService/`, rồi chạy:

```bash
cp .env.example .env
# Đặt giá trị riêng cho POSTGRES_PASSWORD, AUTH_ADMIN_BOOTSTRAP_CREATION_KEY,
# MEDIA_POSTGRES_PASSWORD và MEDIA_MINIO_ROOT_PASSWORD.
docker compose up --build -d
docker compose ps
```

Stack tự build Gateway, AuthService và MediaService; khởi động PostgreSQL riêng cho từng service, MinIO private, Valkey và Kafka; tạo RSA key pair local và chạy migration trước khi nhận traffic. Service principal và service API key được cấp qua API quản trị AuthService.

Gateway là điểm vào công khai tại `http://localhost:5050`:

- [Swagger UI cho AuthService và MediaService](http://localhost:5050/swagger/index.html). Chọn service trong menu ở đầu trang.
- `GET /health/live` (hoặc `/_health/live`): kiểm tra tiến trình Gateway.
- `GET /health/ready` (hoặc `/_health/ready`): yêu cầu JWKS và cấu hình proxy sẵn sàng.
    - `POST /api/v1/auth/register`: được proxy nguyên đường dẫn tới AuthService.
    - `GET/POST /api/v1/inventory/{everything}`: được proxy nguyên đường dẫn tới InventoryService (scheme `InventoryBearer` với audience isolation).
    - `GET/POST /api/v1/rental/{everything}`: được proxy nguyên đường dẫn tới RentalService (Bearer forwarding, bao gồm `/api/v1/rental/me/calendar`).
    - `/rental/swagger/v1/swagger.json`: OpenAPI schema của RentalService (AllowAnonymous).

API key là credential dùng chung cho các API nội bộ, dùng header `X-Api-Key` (sử dụng cùng giá trị bí mật với `Auth:AdminBootstrap:CreationKey`). Gateway cho bốn route nội bộ của MediaService (`verify`, metadata, `access`, `visibility`) đi qua mà không yêu cầu Bearer token, chuyển tiếp `X-Api-Key` tới MediaService và loại bỏ header này khỏi các route khác. MediaService tự kiểm tra key trực tiếp, nên gọi thẳng trong mạng nội bộ không bỏ qua xác thực.

Để thử API nội bộ, gửi request kèm header `X-Api-Key: <creation-key>` tới Gateway trên một trong bốn route nội bộ (ví dụ: `GET /api/v1/media/{mediaId}/verify`). Request không cần Bearer token và không cần tạo service principal ở AuthService. Upload file (`POST /api/v1/media/uploads`) vẫn tiếp tục yêu cầu user JWT hợp lệ.

AuthService, MediaService và PostgreSQL Media chỉ mở trong Docker network. Các port local của hạ tầng gồm PostgreSQL Auth `55432`, MinIO `9002` (console `9003`), Valkey `56379` và Kafka `29092`; chúng chỉ bind vào loopback. Dừng stack bằng `docker compose down`; named volumes được giữ lại.

## Gateway error response

Các middleware do Gateway sở hữu (CORS, giới hạn payload và lỗi nội bộ) trả payload JSON:

```json
{
  "code": "Gateway.Security.CorsOriginDenied",
  "message": "The request origin is not allowed by CORS policy.",
  "traceId": "b4777951-602a-4f76-b067-fcd27c68a6d2"
}
```

Danh mục mã lỗi canonical chuẩn hóa của Gateway:

| HTTP Status | Mã lỗi (`code`) | Ý nghĩa |
| :--- | :--- | :--- |
| `403 Forbidden` | `Gateway.Security.CorsOriginDenied` | Nguồn gốc (Origin) của request không nằm trong whitelist CORS. |
| `413 Payload Too Large` | `Gateway.Request.PayloadTooLarge` | Kích thước request body vượt quá ngưỡng cho phép (MaxRequestBodySize). |
| `429 Too Many Requests` | `Gateway.RateLimitExceeded` | Số lượng request vượt quá hạn mức cho phép (Rate Limit Exceeded). |
| `500 Internal Error` | `Gateway.UnexpectedFailure` | Lỗi không mong đợi phát sinh trong nội bộ Gateway. |

Ocelot xử lý xác thực và lỗi proxy theo phản hồi riêng: route được bảo vệ trả `401` khi thiếu hoặc sai JWT; lỗi kết nối upstream trả `502`; timeout upstream trả `503` trên Ocelot 25.0. Những phản hồi này không dùng payload JSON ở trên. Phản hồi và lỗi nghiệp vụ từ service đích được chuyển tiếp nguyên trạng.

Trong production, cấu hình bằng environment variables, ví dụ:

```text
Gateway__Authentication__Jwt__Issuer
Gateway__Authentication__Jwt__Audience
Gateway__Authentication__Jwt__JwksUri
Routes__0__DownstreamHostAndPorts__0__Host
Routes__0__DownstreamHostAndPorts__0__Port
GlobalConfiguration__Timeout
```

## Gateway Security & Request Protection (DPR-78)

Gateway đóng vai trò là ranh giới bảo mật vững chắc trước khi request chạm tới các downstream services:

1. **Header Sanitization & Chống giả mạo danh tính**:
   - Client không được phép tự ý gửi các tiêu đề nội bộ: `X-User-Id`, `X-Party-Id`, `X-Role`, `X-Service-Name`, `X-Permissions`, `X-Scope`.
   - Gateway tự động loại bỏ (remove) các tiêu đề này ngay tại middleware đầu vào và tại delegating handler của Ocelot.
   - Nếu token JWT hợp lệ, Gateway trích xuất thông tin danh tính thật (`sub`, `party_id`, `role`) và chuyển tiếp qua `X-User-Id`, `X-Party-Id`, `X-Role`, `X-Service-Name`, `X-Correlation-ID`.
   - Nếu JWT không hợp lệ hoặc request ẩn danh, Gateway tuyệt đối không chuyển tiếp bất kỳ identity header nào.

2. **Kiểm soát nguồn gốc CORS (Cross-Origin Resource Sharing)**:
   - Chỉ các Origin nằm trong danh sách whitelist `Gateway:Security:Cors:AllowedOrigins` mới được phép truy cập.
   - Request hoặc preflight OPTIONS từ Origin không nằm trong whitelist sẽ bị chủ động từ chối với mã lỗi `403 Forbidden` (`Gateway.Security.CorsOriginDenied`).
   - Preflight hợp lệ trả về `204 No Content` với các header CORS đầy đủ (`Access-Control-Allow-Origin`, `Access-Control-Allow-Methods`, `Access-Control-Allow-Headers`, `Access-Control-Max-Age`).

3. **Bảo vệ dung lượng Request (Request Size Protection)**:
   - Cấu hình giới hạn kích thước tối đa qua `Gateway:Security:RequestSize:MaxRequestBodySizeBytes` (mặc định 10 MB).
   - Request có `Content-Length` hoặc stream payload vượt quá ngưỡng sẽ bị chặn ngay với mã lỗi `413 Payload Too Large` (`Gateway.Request.PayloadTooLarge`).

4. **Tiêu đề bảo mật HTTP (Security Headers)**:
   - Mọi response trả về từ Gateway đều được gắn các tiêu đề bảo mật tiêu chuẩn:
     - `X-Content-Type-Options: nosniff`
     - `X-Frame-Options: DENY`
     - `X-XSS-Protection: 0`
     - `Referrer-Policy: strict-origin-when-cross-origin`
     - `Content-Security-Policy: default-src 'self'; frame-ancestors 'none';`
     - `Strict-Transport-Security` (khi request là HTTPS).


5. **Kiểm soát tần suất request (Rate Limiting)**:
   - Áp dụng hạn mức riêng cho nhóm đọc (`GET`, `HEAD`) và nhóm ghi (`POST`, `PUT`, `PATCH`, `DELETE`, ...).
   - Sử dụng `PartitionedRateLimiter` với cửa sổ trượt (sliding window 60s, 6 phân đoạn, không xếp hàng):
     - **Đọc (`GET`, `HEAD`)**: 300 req/phút/user đã xác thực | 1.200 req/phút/IP (không có user ID hợp lệ).
     - **Ghi (`POST`, `PUT`, `PATCH`, `DELETE`)**: 30 req/phút/user đã xác thực | 120 req/phút/IP (không có user ID hợp lệ).
   - User đã xác thực chỉ chịu quota user, không chịu thêm quota IP.
   - Quota được cộng chung trên toàn bộ route proxy trong từng nhóm đọc/ghi; đổi endpoint/query cùng user không tạo quota mới.
   - Miễn trừ cho `OPTIONS` (preflight) và các endpoint health check (`/health/*`, `/_health/*`).
   - Khi vượt quota: Gateway trả về `429 Too Many Requests`, header `Retry-After` (giây, tối thiểu 1) và JSON lỗi `Gateway.RateLimitExceeded`.
   - CORS cấu hình `ExposedHeaders` bao gồm `Retry-After` để frontend có thể đọc và hiển thị thời gian chờ.
   - Cấu hình qua section `Gateway:Security:RateLimiting` hoặc biến môi trường `RATE_LIMIT_WINDOW_SECONDS`, `RATE_LIMIT_SEGMENTS_PER_WINDOW`, `RATE_LIMIT_USER_READ`, `RATE_LIMIT_USER_WRITE`, `RATE_LIMIT_IP_READ`, `RATE_LIMIT_IP_WRITE`.

## Timeout và lỗi kết nối downstream

Gateway dùng timeout của Ocelot để giới hạn thời gian chờ downstream:

1. **Upstream Timeout (503 Service Unavailable trên Ocelot 25.0)**:
   - Ocelot dùng `GlobalConfiguration:Timeout` (giây; mặc định cấu hình của Gateway là 10). Docker Compose ánh xạ `DEFAULT_TIMEOUT_SECONDS` vào khóa này; Development đặt 15 giây.
   - Khi downstream không phản hồi kịp, Ocelot ngắt request và trả `503`.

2. **Connection Failure (502 Bad Gateway)**:
   - Khi downstream service từ chối kết nối hoặc gặp lỗi mạng, Ocelot trả `502`.

3. **Client Cancellation**:
   - Khi client chủ động ngắt kết nối (`RequestAborted`), tín hiệu hủy được chuyển tiếp xuống downstream service để giải phóng tài nguyên.

4. **Không tự động retry**:
   - Gateway không cấu hình retry cho Ocelot; request `POST` bị timeout không được gửi lại. Kiểm thử tích hợp đếm số lần upstream nhận request.

Cấu hình mẫu trong production qua biến môi trường:

```text
Gateway__Security__Cors__AllowedOrigins__0=https://app.dprental.vn
Gateway__Security__ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
Gateway__Security__RequestSize__MaxRequestBodySizeBytes=10485760
GlobalConfiguration__Timeout=10
```

## Kiểm tra sức khỏe

Gateway cung cấp các probe giám sát riêng biệt theo chuẩn Kubernetes / Container:

* **Liveness Probe (`/health/live` và `/_health/live`)**:
  - Trả về `200 OK` khi tiến trình Gateway đang chạy bình thường.
* **Readiness Probe (`/health/ready` và `/_health/ready`)**:
  - Trả về `200 OK` chỉ khi Gateway đã sẵn sàng nhận traffic thực tế:
    1. Đã tải và nạp thành công khóa công khai JWKS từ AuthService (`auth-jwks`).
    2. Cấu hình các tuyến đường của Ocelot đã được nạp (`proxy-config`).
  - Trả về `503 Service Unavailable` nếu các phụ thuộc cốt lõi chưa khả dụng.

## Kiểm tra trước khi push

Để tránh push commit chưa đồng bộ với nhánh nền `gateway/develop` hoặc làm gãy build/test, bật Git hook cho local clone:

```bash
# Linux / macOS
./scripts/setup-git-hooks.sh

# Windows PowerShell
.\scripts\setup-git-hooks.ps1
```

Hook sẽ tự động chạy [pre-push-check.sh](scripts/pre-push-check.sh):
1. Fetch `origin/gateway/develop` và kiểm tra commit hiện tại đã chứa nhánh nền này (`git merge-base --is-ancestor`).
2. Build `Gateway/Gateway.sln` ở cấu hình `Release`.
3. Chạy test `Gateway/Gateway.sln`.

## Docker artifact

Dockerfile ở repository root xây dựng runtime image tối giản cho Gateway:

```bash
docker build --target runtime -t ghcr.io/<org>/dprental-gateway:<git-sha> .
```

Runtime lắng nghe cổng `8080`, chạy non-root user `$APP_UID` và tích hợp sẵn `HEALTHCHECK` tự động thăm dò endpoint `/health/ready`.

## CI/CD Gateway cho dev

Workflow [gateway-dev.yml](.github/workflows/gateway-dev.yml) chỉ chạy khi có push vào `gateway/develop-ci-cd`; nó không chạy cho pull request và không kiểm tra branch nền hoặc chạy test. Job publish build và đẩy Docker image lên GHCR một lần, sau đó job deploy chạy trên VPS. Kiểm tra build và test .NET vẫn có trong [pre-push hook](scripts/pre-push-check.sh) ở máy lập trình viên, nhưng hook này có thể bị bỏ qua và không thay thế CI.

Mỗi push sẽ publish image GHCR bất biến bằng full Git SHA:

```text
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-gateway:<git-sha>
```

Thiết lập GitHub Environment variable `GHCR_IMAGE_NAMESPACE` bằng namespace GHCR viết thường (ví dụ `huutawn`). Workflow gắn OCI source metadata để package mới tự liên kết repository. Với package đã từng được push thủ công, vào package GHCR và cấp repository workflow quyền **Write** trong phần Actions access trước lần CI đầu tiên. Tạo GitHub Environment tên `dev` với các secrets sau:

```text
DEV_VPS_HOST              # IP hoặc hostname VPS
DEV_VPS_USER              # user deploy thuộc group docker
DEV_VPS_SSH_PRIVATE_KEY   # private key riêng của user deploy
DEV_VPS_KNOWN_HOSTS       # dòng host key theo định dạng known_hosts
DEV_VPS_PORT              # tùy chọn, mặc định 22
```

Workflow nạp deploy key vào `ssh-agent` qua stdin và kiểm tra host key đã lưu trong `DEV_VPS_KNOWN_HOSTS`. Nó truyền `GITHUB_TOKEN` cùng deploy script qua stdin của kết nối SSH, không đặt token trong câu lệnh SSH. Trên VPS, script đăng nhập GHCR bằng Docker config tạm rồi thực thi [deploy-gateway.sh](scripts/deploy-gateway.sh) từ commit đang phát hành. Docker credential tạm bị xóa ngay khi script kết thúc; user SSH của workflow cần quyền đọc `.env` và ghi `release.env`.

Script cập nhật **chỉ duy nhất biến `GATEWAY_IMAGE`** trong `/opt/dprental/dev/release.env`, bảo toàn nguyên vẹn mọi image khác (`AUTH_SERVICE_IMAGE`, `AUTH_MIGRATOR_IMAGE`...). Nó pull duy nhất image `gateway`, khởi động lại container với `--no-deps --force-recreate gateway`, chờ Gateway đạt trạng thái healthy và thực hiện kiểm thử khói (smoke tests) cả nội bộ lẫn qua domain công khai.

Ví dụ cấp quyền ghi `release.env` cho user GitHub Actions trên VPS (thay `deploy` bằng user thực tế):

```bash
sudo chown deploy:deploy /opt/dprental/dev/release.env
sudo chmod 600 /opt/dprental/dev/release.env
```

User `deploy` phải thuộc group `docker`; quyền Docker tương đương quyền quản trị VPS nên deploy key chỉ dùng cho automation này. Khóa host SSH có thể lấy từ máy tin cậy bằng `ssh-keyscan -H <vps-host>` rồi lưu nguyên kết quả vào `DEV_VPS_KNOWN_HOSTS`. Nếu chỉ muốn triển khai sau khi merge PR, cấu hình branch protection/ruleset cho `gateway/develop-ci-cd` để yêu cầu PR và chặn push trực tiếp; event `push` tự nó không bảo đảm commit đến từ PR.
