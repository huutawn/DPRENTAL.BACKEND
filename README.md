# DPRENTAL API Gateway

API Gateway .NET 10 cho DPRENTAL. Gateway chỉ là reverse proxy và security boundary bổ sung; không chứa nghiệp vụ hoặc tham chiếu project tới service khác.

## Cấu trúc

```text
Gateway/src/ApiGateway/
  Authentication/ # JWT RS256 và JWKS của AuthService
  Health/         # liveness/readiness
  Observability/  # correlation ID và logging scope
  Proxy/          # YARP composition
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

Không bật CORS hay tin `X-Forwarded-*` cho tới khi có danh sách origin và CIDR của ingress/proxy tin cậy.
