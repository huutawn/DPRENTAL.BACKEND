# DPRENTAL API Gateway

API Gateway .NET 10 cho DPRENTAL. Gateway chỉ là reverse proxy và security boundary bổ sung; không chứa nghiệp vụ hoặc tham chiếu project tới service khác.

## Cấu trúc Clean Architecture

Gateway được tổ chức theo kiến trúc Clean Architecture rõ ràng:

```text
Gateway/src/ApiGateway/
  Domain/
    Security/        # Hằng số header (GatewayHeaderConstants), IdentityContext, IIdentityContextAccessor
    Traffic/         # Hằng số và tên chính sách điều phối (TrafficPolicyNames, ResilienceConstants)
    Observability/   # Tên metric chuẩn hóa (GatewayMetricNames), IGatewayMetrics
  Application/
    Security/        # Cấu hình GatewaySecurityOptions, Validator, IHeaderSanitizer, IIdentityExtractor
    Traffic/         # Cấu hình GatewayTrafficOptions, Validator
    Observability/   # Cấu hình GatewayObservabilityOptions, Validator
  Infrastructure/
    Security/
      Middlewares/   # HeaderSanitization, GatewayCors, RequestSizeProtection, SecurityHeaders
      Transforms/    # TrustedIdentityTransformProvider cho YARP
      ForwardedHeaders/ # Cấu hình mạng tin cậy CIDR và KnownProxies
    Traffic/
      RateLimiting/  # GatewayRateLimitingConfiguration phân vùng Anonymous, Authenticated, Sensitive
      Resilience/    # UpstreamTimeoutMiddleware, NoUnsafeRetryPolicy
    Observability/   # GatewayRequestLoggingMiddleware, GatewayMetrics, GatewayMetricsMiddleware
  Security/          # Extension methods đăng ký DI và Middleware Pipeline Bảo mật
  Traffic/           # Extension methods đăng ký DI và Middleware Pipeline Điều phối lưu lượng
  Observability/     # DistributedTracing, RequestLogging, Metrics DI & Middleware
  Authentication/    # JWT RS256 và JWKS của AuthService
  Health/            # Health checks (JwksReadinessHealthCheck, GatewayConfigurationHealthCheck)
  Proxy/             # YARP composition và error mapping
Gateway/tests/ApiGateway.Tests/
```
Gateway nhận JWT được AuthService ký và chỉ tải public key từ JWKS. Không đặt private key, token, hoặc password vào `appsettings*.json`.

## Chạy local & Kiểm thử

### Chạy automated test suite

Chạy toàn bộ bộ kiểm thử tự động (unit tests và integration tests bao gồm JWT, CORS, Rate Limit, Timeout):

```bash
dotnet test Gateway/Gateway.sln
```

### Khởi động Gateway chạy local

Khởi động AuthService tại `http://localhost:5000` (hoặc cấu hình endpoint tương ứng), sau đó chạy Gateway với môi trường `Development`:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project Gateway/src/ApiGateway --urls "http://127.0.0.1:5050"
```

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
  -e ReverseProxy__Clusters__auth-service__Destinations__primary__Address=http://auth-service:5000/ \
  -e Gateway__Security__Cors__AllowedOrigins__0=http://localhost:3000 \
  dprental-gateway:latest
```

Các endpoint public và health checks:

- `/health/live` (hoặc `/_health/live`): kiểm tra tiến trình Gateway đang hoạt động (kèm đầy đủ security headers).
- `/health/ready` (hoặc `/_health/ready`): yêu cầu Gateway đã tải được JWKS hợp lệ từ AuthService và cấu hình proxy sẵn sàng.
- `/api/v1/auth/{**catch-all}`: chuyển tiếp tới AuthService và bỏ prefix `/api/v1/auth`.
- `/swagger` & `/swagger/{**catch-all}`: chuyển tiếp trực tiếp tới Swagger UI và OpenAPI JSON (`/swagger/v1/swagger.json`) của AuthService.
## Gateway error response

Chỉ lỗi do Gateway tạo ra (JWT, authorization, upstream không sẵn sàng hoặc exception) có payload JSON cố định:

```json
{
  "code": "Gateway.Authentication.Required",
  "message": "Authentication is required to access this resource.",
  "traceId": "b4777951-602a-4f76-b067-fcd27c68a6d2"
}
```

Danh mục mã lỗi canonical chuẩn hóa của Gateway:

| HTTP Status | Mã lỗi (`code`) | Ý nghĩa |
| :--- | :--- | :--- |
| `401 Unauthorized` | `Gateway.Authentication.Required` | Thiếu Bearer token trên tài nguyên yêu cầu xác thực. |
| `401 Unauthorized` | `Gateway.Authentication.InvalidToken` | Token không hợp lệ, sai chữ ký hoặc đã hết hạn. |
| `403 Forbidden` | `Gateway.Authorization.Forbidden` | Người dùng không đủ quyền truy cập tài nguyên. |
| `403 Forbidden` | `Gateway.Security.CorsOriginDenied` | Nguồn gốc (Origin) của request không nằm trong whitelist CORS. |
| `413 Payload Too Large` | `Gateway.Request.PayloadTooLarge` | Kích thước request body vượt quá ngưỡng cho phép (MaxRequestBodySize). |
| `429 Too Many Requests` | `Gateway.Traffic.RateLimitExceeded` | Vượt quá số lượng request cho phép trong khung thời gian (Rate Limit). |
| `502 Bad Gateway` | `Gateway.Upstream.Unavailable` | Downstream service không thể kết nối, từ chối socket hoặc lỗi mạng. |
| `504 Gateway Timeout` | `Gateway.Upstream.Timeout` | Downstream service không phản hồi trong ngân sách thời gian chờ (Timeout Budget). |
| `500 Internal Error` | `Gateway.UnexpectedFailure` | Lỗi không mong đợi phát sinh trong nội bộ Gateway. |

`code` và `message` theo cùng contract ổn định của `AuthService.Common.Error`; `traceId` khớp `X-Correlation-ID` để tra log. Response hợp lệ hoặc lỗi nghiệp vụ từ service đích được Gateway chuyển tiếp nguyên trạng, không bị bọc lại.
## Theo dõi trace phân tán

Gateway dùng W3C `traceparent` và OpenTelemetry để giữ cùng một trace khi YARP chuyển request tới service đích. Root trace được sample 100% ở Development và 10% ở các môi trường khác; quyết định sample từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình đầy đủ signal endpoint trong environment (không ghi token collector vào source control):

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Gateway không thêm JWT, authorization header, request/response body, claim, hay correlation ID vào trace attributes. Endpoint `/_health/*` không tạo trace.

Trong production, cấu hình bằng environment variables, ví dụ:

```text
Gateway__Authentication__Jwt__Issuer
Gateway__Authentication__Jwt__Audience
Gateway__Authentication__Jwt__JwksUri
ReverseProxy__Clusters__auth-service__Destinations__primary__Address
```
## Gateway Security & Request Protection (DPR-78)

Gateway đóng vai trò là ranh giới bảo mật vững chắc trước khi request chạm tới các downstream services:

1. **Header Sanitization & Chống giả mạo danh tính**:
   - Client không được phép tự ý gửi các tiêu đề nội bộ: `X-User-Id`, `X-Party-Id`, `X-Role`, `X-Service-Name`, `X-Permissions`, `X-Scope`.
   - Gateway tự động loại bỏ (remove) các tiêu đề này ngay tại middleware đầu vào và tại transform của YARP.
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

## Gateway Resilience & Traffic Control

Gateway cung cấp cơ chế điều phối lưu lượng và phục hồi mạng tin cậy:

1. **Upstream Timeout (504 Gateway Timeout)**:
   - Ngân sách thời gian chờ mặc định được cấu hình qua `Gateway:Traffic:Resilience:DefaultTimeoutSeconds` (mặc định: 10 giây).
   - Khi downstream service không phản hồi trong ngân sách, Gateway chủ động ngắt kết nối và trả về HTTP `504 Gateway Timeout` (`Gateway.Upstream.Timeout`).

2. **Service Unavailable / Connection Failure (502 / 503)**:
   - Khi downstream service bị sập, từ chối kết nối hoặc lỗi DNS, Gateway trả về mã lỗi canonical `502 Bad Gateway` (`Gateway.Upstream.Unavailable`) mà không gây crash tiến trình.

3. **Rate Limiting (429 Too Many Requests)**:
   - Hỗ trợ 3 phân tầng kiểm soát tần suất truy cập:
     - **Anonymous**: Phân vùng theo Client IP (mặc định: 20 req/phút).
     - **Authenticated**: Phân vùng theo `UserId` từ token JWT (mặc định: 100 req/phút).
     - **Sensitive**: Áp dụng cho các endpoint nhạy cảm như login, auth sessions (mặc định: 5 req/phút).
   - Khi vượt ngưỡng, Gateway trả về HTTP `429 Too Many Requests` (`Gateway.Traffic.RateLimitExceeded`) kèm tiêu đề `Retry-After`.
   - Gateway chỉ bảo vệ hạ tầng và tài nguyên mạng, **không chứa business quota** (các quy tắc nghiệp vụ như "Supplier chỉ được đăng 20 máy/ngày" thuộc về Domain Service).

4. **Client Cancellation**:
   - Khi client chủ động ngắt kết nối (`RequestAborted`), tín hiệu hủy được chuyển tiếp xuống downstream service để giải phóng tài nguyên.

5. **Cấm tự ý Retry các lệnh nghiệp vụ không an toàn (No Unsafe Retries)**:
   - Gateway tuyệt đối **không tự động retry** các lệnh nghiệp vụ không an toàn (`POST`, `PATCH`, `CONNECT`).
   - Ví dụ: `POST /rentals` gặp timeout sẽ lập tức trả về 504 thay vì tự gửi lại 3 lần, tránh nguy cơ tạo trùng lặp giao dịch (Duplicate Business Effects).

Cấu hình mẫu trong production qua biến môi trường:
```text
Gateway__Security__Cors__AllowedOrigins__0=https://app.dprental.vn
Gateway__Security__ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
Gateway__Security__RequestSize__MaxRequestBodySizeBytes=10485760
Gateway__Traffic__Resilience__DefaultTimeoutSeconds=10
Gateway__Traffic__RateLimiting__Anonymous__PermitLimit=20
Gateway__Traffic__RateLimiting__Authenticated__PermitLimit=100
Gateway__Traffic__RateLimiting__Sensitive__PermitLimit=5
```

## Observability & Operational Readiness

### 1. Ghi log có cấu trúc (Structured Logging)

Mỗi request đi qua Gateway đều xuất một bản ghi structured log chứa đầy đủ ngữ cảnh để tra cứu (tracing/correlation) mà **tuyệt đối không làm lộ dữ liệu nhạy cảm**:

* **Thuộc tính bắt buộc được ghi nhận**:
  - `Timestamp`: Thời gian UTC chuẩn ISO 8601.
  - `CorrelationId`: Mã tương quan từ `X-Correlation-ID` (đồng bộ giữa Gateway và downstream).
  - `TraceId`: W3C TraceId tương thích chuẩn OpenTelemetry.
  - `Route`: Mã định danh route hoặc pattern đường dẫn YARP.
  - `Method`: Phương thức HTTP (GET, POST, PUT, DELETE,...).
  - `StatusCode`: Mã trạng thái phản hồi HTTP.
  - `DurationMs`: Thời gian xử lý request tính bằng mili-giây.
  - `Upstream`: Tên cụm dịch vụ downstream đích (ClusterId) hoặc `gateway`.
* **Ranh giới bảo mật (Zero Leakage)**:
  - Tuyệt đối **không ghi log** `Authorization` header, Bearer token, JWT, refresh token, mật khẩu, API key hay nội dung nhạy cảm của request body.

### 2. Thu thập chỉ số đo lường (Metrics)

Hệ thống sử dụng `System.Diagnostics.Metrics.Meter` với tên chuẩn `Dprental.Gateway`, tích hợp trực tiếp với OpenTelemetry:

| Tên Metric | Kiểu Instrument | Mô tả |
| :--- | :--- | :--- |
| `gateway_request_total` | Counter (`long`) | Tổng số lượng request đi qua Gateway. |
| `gateway_request_duration_seconds` | Histogram (`double`) | Phân phối thời gian xử lý request qua Gateway. |
| `gateway_4xx_total` | Counter (`long`) | Tổng số lượng phản hồi lỗi client 4xx. |
| `gateway_5xx_total` | Counter (`long`) | Tổng số lượng phản hồi lỗi server 5xx. |
| `gateway_upstream_timeout_total` | Counter (`long`) | Tổng số request bị upstream timeout (504). |
| `gateway_rate_limit_total` | Counter (`long`) | Tổng số request bị từ chối do vượt rate limit (429). |

* **Quy tắc gán nhãn (Labels/Dimensions)**:
  - Nhãn cho phép (Low-cardinality): `route`, `method`, `status`, `upstream`.
  - **Tuyệt đối cấm sử dụng**: `userId`, `email`, `orderId` hoặc bất kỳ dữ liệu định danh người dùng (PII) làm label để tránh bùng nổ cardinality và rò rỉ dữ liệu riêng tư.

### 3. Kiểm tra sức khỏe & Tính sẵn sàng (Health Checks)

Gateway cung cấp các probe giám sát riêng biệt theo chuẩn Kubernetes / Container:

* **Liveness Probe (`/health/live` và `/_health/live`)**:
  - Trả về `200 OK` khi tiến trình Gateway đang chạy bình thường.
* **Readiness Probe (`/health/ready` và `/_health/ready`)**:
  - Trả về `200 OK` chỉ khi Gateway đã sẵn sàng nhận traffic thực tế:
    1. Đã tải và nạp thành công khóa công khai JWKS từ AuthService (`auth-jwks`).
    2. Cấu hình các tuyến đường (routes) và cụm dịch vụ (clusters) của Reverse Proxy đã sẵn sàng (`proxy-config`).
  - Trả về `503 Service Unavailable` nếu các phụ thuộc cốt lõi chưa khả dụng.
