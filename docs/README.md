# Tài liệu AuthService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern dùng chung (`Result`, `Error`, enums, utility) | Nghiệp vụ xác thực |
| `Domain` | Entities, Domain Models, Repository contracts, EF Core DbContext, Repositories, Migrations và data persistence | Business logic xác thực cấp cao, HTTP pipeline, controller |
| `Application` | DTOs, Services, Security, Mapping và điều phối domain | Controller, HTTP pipeline, DbContext |
| `Api` | Controllers, Filters, middleware, DI/composition root | Business logic |

Chi tiết về `Result` và `Error` trong `Common`: xem [common-result-error.md](common-result-error.md).

Domain sở hữu repository contracts (interfaces), EF Core DbContext, repository implementations và migrations. Application gọi các repository contract này; API composition root đăng ký persistence Domain qua `AddAuthDomainPersistence`.
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
     "Host=localhost;Port=5433;Database=authservice;Username=authservice;Password=<password-trong-.env>" \
     --project AuthService/src/Api/AuthService.Api.csproj
   ```

   `.env` chỉ cấp credential cho Docker Compose; `dotnet run` không tự đọc file này. User Secrets được ASP.NET Core nạp tự động khi chạy môi trường `Development` và không được lưu trong repository.

3. Tạo và cấp RSA key ring theo phần JWT bên dưới.

4. Tạo và cấp khóa bootstrap quản trị viên theo phần **Bootstrap tài khoản quản trị viên** bên dưới.

5. Chạy API:

   ```bash
   dotnet run --project AuthService/src/Api/AuthService.Api.csproj
   ```

   Development lắng nghe tại `https://localhost:64836` và `http://localhost:64837`. Kiểm tra public key bằng:

   ```bash
   curl http://localhost:64837/.well-known/jwks.json
   ```

Nếu startup báo `Auth:Jwt:ActiveKeyId must be configured` hoặc `Auth:Jwt:Keys must contain at least one verification key`, key ring chưa được cấp đầy đủ.

## Bootstrap tài khoản quản trị viên

`POST /api/v1/auth/admins` tạo tài khoản với role `Admin`. Endpoint yêu cầu khóa bí mật trong header `X-Admin-Creation-Key`; không có khóa hoặc khóa không khớp sẽ nhận `401`. API không khởi động nếu chưa cấu hình `Auth:AdminBootstrap:CreationKey`.

Tạo một khóa ngẫu nhiên 256-bit cho local development. Lệnh không in khóa ra terminal; thư mục `.local-secrets/` đã bị Git bỏ qua:

```bash
mkdir -p .local-secrets
openssl rand -hex 32 | tr -d '\n' > .local-secrets/admin-bootstrap.key
chmod 600 .local-secrets/admin-bootstrap.key
```

Không truyền khóa qua command-line. Cấp khóa vào .NET User Secrets bằng JSON đọc từ file:

```bash
jq -n \
  --rawfile creationKey .local-secrets/admin-bootstrap.key \
  '{"Auth:AdminBootstrap:CreationKey": $creationKey}' \
  | dotnet user-secrets set --project AuthService/src/Api/AuthService.Api.csproj
```

Sau khi API chạy, gửi request tạo admin từ một HTTP client được cấu hình để giữ secret an toàn. Request có body sau và header `X-Admin-Creation-Key` bằng đúng giá trị đã cấp:

```http
POST /api/v1/auth/admins
Content-Type: application/json
X-Admin-Creation-Key: <admin-bootstrap-key>

{
  "email": "admin@example.com",
  "password": "Password123!",
  "fullName": "Administrator"
}
```

Request thành công trả `201 Created` cùng `accountId`; email đã tồn tại trả `409`. Bất kỳ ai có khóa đều có thể tạo tài khoản role `Admin`, vì vậy chỉ dùng qua kênh tin cậy, không đưa header vào log/trace, và rotate khóa trong secret manager sau khi bootstrap xong.

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
Auth__AdminBootstrap__CreationKey
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

## Đăng nhập Google bằng Firebase

Đăng nhập Google hiện dùng Firebase Authentication làm identity provider. Frontend xử lý popup/redirect Google bằng Firebase Web SDK, lấy Firebase ID token rồi gửi token đó qua Next BFF. AuthService chỉ verify Firebase ID token, đồng bộ user nội bộ và phát access token/refresh token của hệ thống.

```text
POST /api/v1/auth/firebase/login
Content-Type: application/json

{
  "idToken": "<firebase-id-token>"
}
```

Response thành công là token pair dạng JSON để Next BFF tự set cookie `HttpOnly` trên domain frontend:

```json
{
  "accessToken": "...",
  "refreshToken": "..."
}
```

AuthService không set cookie cho endpoint Firebase login. Các endpoint Google OAuth cũ đã bị gỡ:

```text
GET /api/v1/auth/google/login
GET /api/v1/auth/google/callback
POST /api/v1/auth/google/exchange
GET /signin-google
```

Cấu hình backend:

```text
Auth:Firebase:Enabled=true
Auth:Firebase:ProjectId=<firebase-project-id>
Auth:Firebase:CredentialsJson=<service-account-json>
# hoặc:
Auth:Firebase:CredentialsPath=/run/secrets/firebase-service-account.json
```

Production truyền bằng environment variables, ví dụ:

```text
Auth__Firebase__Enabled=true
Auth__Firebase__ProjectId=<firebase-project-id>
Auth__Firebase__CredentialsJson=<service-account-json>
```

Nếu `Auth:Firebase:Enabled=true` thì phải có `ProjectId` và một trong hai giá trị `CredentialsJson` hoặc `CredentialsPath`; thiếu config làm service fail ngay khi khởi động. Firebase service account là secret, không commit vào source control.

Quy tắc AuthService áp dụng khi nhận Firebase ID token:

1. Token phải hợp lệ với Firebase project đã cấu hình.
2. Token phải có email và email đã verified.
3. `firebase.sign_in_provider` phải là `google.com`.
4. Provider subject ưu tiên `firebase.identities["google.com"][0]`, fallback về Firebase UID.

Người dùng đã đăng nhập có thể quản lý liên kết Google:

```text
GET /api/v1/auth/users/me/external-logins

POST /api/v1/auth/users/me/external-logins/Google/link
Content-Type: application/json

{
  "idToken": "<firebase-id-token>"
}

DELETE /api/v1/auth/users/me/external-logins/Google
```

`POST .../link` dùng khi user đang đăng nhập bằng email/password và bấm liên kết Google. FE lấy Firebase ID token rồi gửi lên AuthService; AuthService verify token và gắn provider `Google` vào user hiện tại. Nếu user đã có Google link, hoặc Google account đó đã thuộc user khác, API trả `409 ExternalLogin.AlreadyLinked`. `DELETE .../Google` chỉ cho gỡ khi tài khoản còn credential khác, ví dụ đã có mật khẩu.

Với topology `Caddy -> Gateway -> AuthService`, Gateway ghi đè `X-Forwarded-Proto` và `X-Forwarded-Host` bằng public origin mà Caddy đã xác thực. AuthService chỉ được tin hai header này từ Docker network nội bộ chứa Gateway, vì vậy môi trường deploy cần thêm:

```text
Auth__ForwardedHeaders__Enabled=true
Auth__ForwardedHeaders__KnownNetworks__0=<CIDR của Docker services network, ví dụ 172.31.0.0/16>
# Tùy chọn, mặc định đã là 1:
Auth__ForwardedHeaders__ForwardLimit=1
```

Không dùng `EDGE_NETWORK_CIDR` cho AuthService: AuthService không nằm trong network `edge`; peer trực tiếp của nó là Gateway trong `services`. Lấy CIDR thật trên host bằng `docker network inspect dprental-dev_services --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'`, đặt vào biến Compose (ví dụ `SERVICES_NETWORK_CIDR`), rồi truyền thành `Auth__ForwardedHeaders__KnownNetworks__0`. Các giá trị này không phải secret.

Quy tắc liên kết tài khoản, theo thứ tự:

1. Đã có liên kết `(Google, sub)` thì dùng user đó; user bị khóa thì từ chối.
2. Chưa liên kết mà email chưa được Google xác minh thì từ chối, không liên kết.
3. Chưa liên kết, email đã xác minh và trùng user có sẵn thì thêm liên kết vào user đó.
4. Không có user nào dùng email đó thì tạo user mới với mật khẩu ngẫu nhiên (chỉ đăng nhập được qua Google) và không có role nào.

Bảng `auth.ExternalLogins` được tạo bởi migration `AddExternalLogins`; áp dụng bằng `dotnet ef database update` như phần Refresh token và session. Migration `RemoveGoogleLoginTickets` gỡ bảng ticket cũ vì flow Firebase không dùng ticket nội bộ nữa.

### Organization identifier uniqueness

Organization registration checks slug and tax code against approved organizations (Active or Suspended). Pending and Rejected applications may share identifiers. Approval checks both identifiers again; filtered unique indexes enforce this rule for concurrent approvals. Apply the `AllowUnapprovedOrganizationIdentifiers` migration before deploying this behavior. Rolling back requires resolving duplicate identifiers among Pending/Rejected organizations before restoring the global unique indexes.

### Quản lý Organization dành cho Admin

Admin thao tác qua `/api/v1/auth/admin/organizations`:

- `GET /api/v1/auth/admin/organizations`: Lấy danh sách organization có phân trang, tìm kiếm và lọc kết hợp bằng AND (`status`, `type`, `province`, `overdueOnly`, `remindedOnly`). Hai cờ `overdueOnly` và `remindedOnly` mặc định `false`; khi `true` chỉ lấy hồ sơ `Pending` tương ứng. Yêu cầu permission `identity.organizations.read`.
- `GET /api/v1/auth/admin/organizations/{id}`: Chi tiết organization dành cho Admin. Yêu cầu permission `identity.organizations.read`.
- `PUT /api/v1/auth/admin/organizations/{id}`: Cập nhật toàn bộ thông tin liên hệ và hiển thị: `name`, `contactEmail`, `contactPhone`, `address`, `province`, `ward`, `website`, `logoMediaId`, `description`. Các trường tùy chọn gửi `null` để xóa. Giữ nguyên thông tin pháp lý và trạng thái. Trả `204`. Logo mới phải là ảnh `Ready` do chính admin upload. Yêu cầu permission `identity.organizations.update`.
- `POST /api/v1/auth/admin/organizations/{id}/suspend`: Khóa organization đang `Active`, yêu cầu body `{ "reason": "..." }` (bắt buộc, trim, tối đa 500 ký tự). Trả `204`. Chuyển trạng thái sang `Suspended` và lưu `suspensionReason` để Owner có thể xem qua `/mine` hoặc `GET /{id}`. Gọi lặp trả `204` không đổi lý do. `Pending` hoặc `Rejected` trả `409`. Yêu cầu permission `identity.organizations.suspend`.
- `POST /api/v1/auth/admin/organizations/{id}/activate`: Mở khóa organization đang `Suspended`, xóa `suspensionReason` hiện tại. Trả `204`. Gọi lặp trả `204`. Yêu cầu permission `identity.organizations.suspend`.
- `GET /api/v1/auth/admin/organizations/{id}/history`: Lấy nhật ký audit lịch sử của organization có phân trang, sắp xếp mới nhất trước. Mỗi dòng gồm ID, actor ID, thời điểm, hành động, kết quả và lý do nếu có. Yêu cầu permission `identity.organizations.read`.
- `PATCH /api/v1/auth/admin/organizations/{id}/approve` và `PATCH /api/v1/auth/admin/organizations/{id}/reject`: Duyệt hoặc từ chối hồ sơ `Pending`; từ chối yêu cầu body `{ "reason": "..." }`. Admin không được tự duyệt/từ chối org mình là thành viên. Yêu cầu permission `identity.organizations.suspend`.

Client phải cập nhật cùng đợt phát hành: PUT không còn chỉ nhận tên mà yêu cầu đầy đủ liên hệ/địa chỉ; suspend không còn nhận body rỗng. Hai API đều trả `204` khi gọi lặp mà dữ liệu/trạng thái không đổi, không ghi audit mới. Nếu logo bị cập nhật đồng thời trong lúc kiểm tra media, PUT trả `409` để client đọc lại hồ sơ rồi gửi lại. JWT permission chỉ là lớp kiểm tra đầu tiên; service kiểm tra quyền global trong DB cho mọi thao tác admin.

Migration `AddOrganizationSuspensionAndAuditReason` bổ sung cột `SuspensionReason` cho bảng `Organizations`, cột `Reason` và index `(Target, Timestamp)` cho bảng `AuditLogs`, đồng thời tách lý do từ chối cũ dạng `Success: ...` sang trường `Reason`. Áp dụng migration trước khi deploy ứng dụng mới.
