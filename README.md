# DPRENTAL API Gateway

API Gateway .NET 10 cho DPRENTAL. Gateway chỉ là reverse proxy và security boundary bổ sung; không chứa nghiệp vụ hoặc tham chiếu project tới service khác.

## Cấu trúc Clean Architecture

Gateway được tổ chức theo kiến trúc Clean Architecture rõ ràng:

```text
Gateway/src/ApiGateway/
  Domain/
    Security/        # Hằng số header (GatewayHeaderConstants), IdentityContext, IIdentityContextAccessor
    Traffic/         # Hằng số và tên chính sách điều phối (TrafficPolicyNames, ResilienceConstants)
  Application/
    Security/        # Cấu hình GatewaySecurityOptions, Validator, IHeaderSanitizer, IIdentityExtractor
    Traffic/         # Cấu hình GatewayTrafficOptions, Validator
  Infrastructure/
    Security/
      Middlewares/   # HeaderSanitization, GatewayCors, RequestSizeProtection, SecurityHeaders
      Transforms/    # TrustedIdentityTransformProvider cho YARP
      ForwardedHeaders/ # Cấu hình mạng tin cậy CIDR và KnownProxies
    Traffic/
      RateLimiting/  # GatewayRateLimitingConfiguration phân vùng Anonymous, Authenticated, Sensitive
      Resilience/    # UpstreamTimeoutMiddleware, NoUnsafeRetryPolicy
  Security/          # Extension methods đăng ký DI và Middleware Pipeline Bảo mật
  Traffic/           # Extension methods đăng ký DI và Middleware Pipeline Điều phối lưu lượng
  Authentication/    # JWT RS256 và JWKS của AuthService
  Health/            # liveness/readiness
  Observability/     # correlation ID và distributed tracing
  Proxy/             # YARP composition và error mapping
Gateway/tests/ApiGateway.Tests/
```
Gateway nhận JWT được AuthService ký và chỉ tải public key từ JWKS. Không đặt private key, token, hoặc password vào `appsettings*.json`.

## Chạy local

Khởi động AuthService tại `http://localhost:5000`, cấu hình issuer/audience của nó như trong `src/ApiGateway/appsettings.Development.json`, rồi chạy:

```bash
dotnet run --project Gateway/src/ApiGateway
```

Các endpoint public:

- `/_health/live` kiểm tra tiến trình Gateway.
- `/_health/ready` yêu cầu Gateway đã tải được JWKS hợp lệ.
- `/api/v1/auth/{**catch-all}` chuyển tiếp tới AuthService và bỏ prefix `/api/v1/auth`.

## Gateway error response

Chỉ lỗi do Gateway tạo ra (JWT, authorization, upstream không sẵn sàng hoặc exception) có payload JSON cố định:

```json
{
  "code": "Gateway.Authentication.Required",
  "message": "Authentication is required to access this resource.",
  "traceId": "b4777951-602a-4f76-b067-fcd27c68a6d2"
}
```

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
