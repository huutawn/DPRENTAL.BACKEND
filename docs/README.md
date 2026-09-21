# Tài liệu AuthService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern thật sự dùng chung, gồm `IRepo<TEntity, TKey>` CRUD | Nghiệp vụ xác thực |
| `Domain` | DTOs, Entities, Models, Utils, Validators, `IRepositories`, repository implementation, DbContext, EF Core và migration | HTTP pipeline, controller |
| `Application` | Services, IServices, Security, Mapping, third-party integration và điều phối domain | Controller, HTTP pipeline |
| `Api` | Controllers, Filters, middleware, DI/composition root | Business logic |

Chi tiết về `Result` và `Error` trong `Common`: xem [common-result-error.md](common-result-error.md).

Domain là nơi sở hữu DTO, entity/model, utility/validator và persistence của Auth. `Domain/IRepositories` giữ contract theo nghiệp vụ; các contract đó kế thừa `Common/IRepo<TEntity, TKey>` khi cần CRUD chuẩn. Application chỉ gọi repository contract và giữ mapper giữa model/DTO; API chỉ đăng ký dependency tại composition root, controller và filter support.

## Cấu hình môi trường

`Api/appsettings.Development.json` chỉ chứa giá trị không nhạy cảm. Docker Compose lấy credential local từ `.env`: sao chép `.env.example` thành `.env`, rồi thay password. `.env` đã bị Git bỏ qua.

`.env` chỉ phục vụ thay thế biến trong Docker Compose; nó **không** tự đưa biến vào tiến trình chạy bởi `dotnet run`. ASP.NET Core mặc định nạp environment variables, với `__` biểu thị cấu hình lồng nhau. Khi chạy local, cấp khóa qua .NET User Secrets (đã được bật trong project API); production phải lấy giá trị từ secret manager của hạ tầng.

## Chạy API cục bộ

1. Khởi động PostgreSQL và Valkey:

   ```bash
   cp .env.example .env
   docker compose up -d
   ```

2. Cấp `ConnectionStrings:Postgres` từ .NET User Secrets. Giá trị phải dùng password đã đặt trong `.env`:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Postgres" \
     "Host=localhost;Port=5432;Database=authservice;Username=authservice;Password=<password-trong-.env>" \
     --project AuthService/src/Api/AuthService.Api.csproj
   ```

   `.env` chỉ cấp credential cho Docker Compose; `dotnet run` không tự đọc file này. User Secrets được ASP.NET Core nạp tự động khi chạy môi trường `Development` và không được lưu trong repository.

3. Tạo và cấp RSA key ring theo phần JWT bên dưới.

4. Chạy API:

   ```bash
   dotnet run --project AuthService/src/Api/AuthService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:64836` và `http://localhost:64837`. Kiểm tra public key bằng:

   ```bash
   curl http://localhost:64837/.well-known/jwks.json
   ```

Nếu startup báo `Auth:Jwt:ActiveKeyId must be configured` hoặc `Auth:Jwt:Keys must contain at least one verification key`, key ring chưa được cấp đầy đủ.

## JWT access token

AuthService dùng RS256 và key ring. Private key chỉ có tại AuthService để ký; consumer chỉ cần public key từ `GET /.well-known/jwks.json`. Không dùng `SigningKey` đối xứng.

```text
Auth:Jwt:Issuer
Auth:Jwt:Audience
Auth:Jwt:AccessTokenLifetimeMinutes
Auth:Jwt:ActiveKeyId
Auth:Jwt:Keys:0:KeyId
Auth:Jwt:Keys:0:PublicKeyPem
Auth:Jwt:Keys:0:PrivateKeyPem
```

Mỗi entry `Keys` có public PEM và `kid` duy nhất. Entry trùng `ActiveKeyId` bắt buộc có thêm private PEM; các key đã retire chỉ giữ public PEM để xác thực access token cũ. PEM không được đặt trong `appsettings*.json`, log, hoặc source control.

Sinh RSA key pair cục bộ bằng ít nhất 2048-bit (khuyến nghị 3072-bit), rồi đưa nội dung PEM vào secret store phù hợp. Thư mục `.local-secrets/` đã bị Git bỏ qua:

```bash
mkdir -p .local-secrets
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .local-secrets/jwt-private.pem
openssl pkey -in .local-secrets/jwt-private.pem -pubout -out .local-secrets/jwt-public.pem
chmod 600 .local-secrets/jwt-private.pem
```

Không truyền private PEM qua command-line. Với local development, `dotnet user-secrets` có thể nhận JSON từ stdin; lệnh sau đọc PEM trực tiếp từ file và không đưa private key vào command-line history:

```bash
jq -n \
  --rawfile publicKey .local-secrets/jwt-public.pem \
  --rawfile privateKey .local-secrets/jwt-private.pem \
  '{
    "Auth:Jwt:ActiveKeyId": "local-2026-01",
    "Auth:Jwt:Keys:0:KeyId": "local-2026-01",
    "Auth:Jwt:Keys:0:PublicKeyPem": $publicKey,
    "Auth:Jwt:Keys:0:PrivateKeyPem": $privateKey
  }' \
  | dotnet user-secrets set --project AuthService/src/Api/AuthService.Api.csproj
```

Với môi trường local, dùng các giá trị sau trong User Secrets. `Issuer`, `Audience` và lifetime đã có trong `appsettings.Development.json`; chỉ cần cấp key ring nếu không đổi mặc định đó:

```text
Auth:Jwt:ActiveKeyId = local-2026-01
Auth:Jwt:Keys:0:KeyId = local-2026-01
Auth:Jwt:Keys:0:PublicKeyPem = <toàn bộ nội dung jwt-public.pem>
Auth:Jwt:Keys:0:PrivateKeyPem = <toàn bộ nội dung jwt-private.pem>
```

Sau khi cấp key ring, chạy lại `dotnet run --project AuthService/src/Api/AuthService.Api.csproj`. Khi API đã chạy, chỉ kiểm tra public key qua JWKS:

```bash
curl http://localhost:64837/.well-known/jwks.json
```

Ví dụ tên environment variables ở production:

```text
ConnectionStrings__Postgres
ConnectionStrings__Redis
Auth__Jwt__Issuer
Auth__Jwt__Audience
Auth__Jwt__AccessTokenLifetimeMinutes
Auth__Jwt__ActiveKeyId
Auth__Jwt__Keys__0__KeyId
Auth__Jwt__Keys__0__PublicKeyPem
Auth__Jwt__Keys__0__PrivateKeyPem
```

JWT được kiểm tra ngay khi service khởi động: issuer, audience, lifetime 1–60 phút, active key, `kid` duy nhất, PEM hợp lệ, RSA ít nhất 2048-bit và private/public key của active entry phải khớp. Access token mặc định hết hạn sau 15 phút.

## Refresh token và session

Refresh token là chuỗi ngẫu nhiên chỉ dùng một lần; database chỉ lưu SHA-256 hash, không lưu giá trị token thô. Session có hạn tuyệt đối 30 ngày. Mỗi lần `POST /api/v1/auth/refresh` thành công sẽ thu hồi token vừa dùng và trả một cặp token mới. Gửi lại token cũ sẽ thu hồi toàn bộ session và nhận `401` chung.

`POST /api/v1/auth/logout` nhận `RefreshTokenRequest` và thu hồi toàn bộ session, luôn trả `204` để client có thể gọi lặp lại an toàn. Login chưa nằm trong phạm vi hiện tại; service login sau này phải gọi `IAuthSessionService.CreateSessionAsync` sau khi xác thực User.

Sau khi pull thay đổi, áp dụng migration local (migration này xóa refresh token cũ, không xóa User):

```bash
dotnet ef database update --project AuthService/src/Domain/AuthService.Domain.csproj --startup-project AuthService/src/Api/AuthService.Api.csproj
```

JWKS được cache công khai tối đa 5 phút. Để rotate key an toàn: thêm key pair mới nhưng chưa active, deploy để JWKS công bố cả hai public key, chờ cache hết hạn, đổi `ActiveKeyId`, sau đó giữ public key cũ ít nhất bằng access-token lifetime cộng clock skew trước khi gỡ nó.

## Theo dõi trace phân tán

API dùng W3C `traceparent` và OpenTelemetry ASP.NET Core instrumentation. Khi request đến từ Gateway, AuthService tạo server span có cùng `TraceId` và dùng span Gateway gửi sang làm parent; vì vậy collector hiển thị hai service trong một trace liên tục.

```text
Telemetry:Tracing:SamplingRatio       # 0.0 đến 1.0; development = 1.0, môi trường khác = 0.1
Telemetry:Otlp:TracesEndpoint         # URL HTTP/protobuf tuyệt đối, kết thúc bằng /v1/traces
```

`TracesEndpoint` để trống thì không export trace, phù hợp khi local chưa chạy collector. Production cấu hình bằng:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Sampler là `ParentBased(TraceIdRatioBased)`: trace đã lấy mẫu từ Gateway luôn tiếp tục được lấy mẫu tại AuthService, còn request không có parent dùng tỷ lệ của service. Chỉ metadata HTTP tiêu chuẩn được instrumentation ghi nhận; không thêm token, authorization header, body, claim hoặc PEM key vào trace attributes.

## Đăng nhập bằng Google

Đăng nhập Google dùng OAuth 2.0 authorization code kèm PKCE, do backend xử lý. Sau khi Google xác nhận danh tính, AuthService vẫn phát JWT và cookie `accessToken`/`refreshToken` như `POST /api/v1/auth/login`.

```text
GET /api/v1/auth/google/login?returnUrl=<url>     # chuyển trình duyệt sang Google
GET /signin-google                                 # Google gọi về, do thư viện xử lý
GET /api/v1/auth/google/callback?returnUrl=<url>   # tạo phiên, set cookie, chuyển về returnUrl
```

Lỗi được trả bằng cách chuyển về `returnUrl?error=<mã lỗi>` (ví dụ `ExternalLogin.EmailNotVerified`) vì lúc này user đang ở trên trình duyệt. `returnUrl` phải thuộc `Auth:Google:AllowedReturnUrls` (so sánh theo origin), nếu không endpoint trả `400`.

Cấu hình (chưa có `ClientId` và `ClientSecret` thì tính năng tắt và cả hai endpoint trả `404`):

```text
Auth:Google:ClientId                  # có thể để trong appsettings, không phải bí mật
Auth:Google:ClientSecret              # chỉ cấp qua User Secrets hoặc biến môi trường
Auth:Google:AllowedReturnUrls:0       # mặc định http://localhost:3000
```

Trên Google Cloud Console tạo OAuth client loại **Web application**. `Authorized redirect URIs` phải là callback của Google middleware, không phải endpoint controller `google/callback`:

```text
https://dev-api.lazyops.cloud/signin-google
http://localhost:64837/signin-google
```

`Authorized JavaScript origins` không cần thiết cho luồng này vì browser chỉ được chuyển hướng tới endpoint backend; frontend không gọi Google Identity Services trực tiếp.

`returnUrl` là URL frontend nhận browser sau khi đăng nhập. Giá trị cấu hình chỉ so sánh theo origin, do đó không thêm path:

```text
https://dev-app.lazyops.cloud
http://localhost:3000
```

Cấp secret local:

```bash
dotnet user-secrets set "Auth:Google:ClientId" "<client-id>" --project AuthService/src/Api/AuthService.Api.csproj
dotnet user-secrets set "Auth:Google:ClientSecret" "<client-secret>" --project AuthService/src/Api/AuthService.Api.csproj
```

Production truyền các biến môi trường sau vào AuthService; không đưa `ClientSecret` vào source control:

```text
Auth__Google__ClientId=<google-oauth-client-id>
Auth__Google__ClientSecret=<google-oauth-client-secret>
Auth__Google__AllowedReturnUrls__0=https://dev-app.lazyops.cloud
# Chỉ thêm khi môi trường này cần hỗ trợ frontend local:
Auth__Google__AllowedReturnUrls__1=http://localhost:3000
```

Không có biến cấu hình redirect URI: Google handler tạo URI từ request host và callback path `/signin-google`. Khi TLS kết thúc ở reverse proxy, proxy phải chuyển `X-Forwarded-Proto: https` và application phải xử lý forwarded headers trước authentication; nếu không handler có thể gửi `http://dev-api.lazyops.cloud/signin-google` cho Google và bị từ chối do không khớp redirect URI.

Quy tắc liên kết tài khoản, theo thứ tự:

1. Đã có liên kết `(Google, sub)` thì dùng user đó; user bị khóa thì từ chối.
2. Chưa liên kết mà email chưa được Google xác minh thì từ chối, không liên kết.
3. Chưa liên kết, email đã xác minh và trùng user có sẵn thì thêm liên kết vào user đó.
4. Không có user nào dùng email đó thì tạo user mới với mật khẩu ngẫu nhiên (chỉ đăng nhập được qua Google) và không có role nào.

Bảng `auth.ExternalLogins` được tạo bởi migration `AddExternalLogins`; áp dụng bằng `dotnet ef database update` như phần Refresh token và session.
