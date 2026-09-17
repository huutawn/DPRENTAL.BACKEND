# Tài liệu AuthService

## Ranh giới các lớp

| Lớp | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu và concern thật sự dùng chung | Nghiệp vụ xác thực |
| `Domain` | Entity, value object, repository contract, invariant | HTTP, EF Core, Redis |
| `Application` | Use case và điều phối domain | Controller, persistence implementation |
| `Api` | HTTP contract, middleware, DI/composition root | Business logic |

Chi tiết về `Result` và `Error` trong `Common`: xem [common-result-error.md](common-result-error.md).

Không được để `Domain` phụ thuộc vào hạ tầng. Khi bắt đầu persistence, repository implementation thuộc module hạ tầng cụ thể và interface vẫn nằm trong `Domain` (hoặc application consumer nếu đó là contract do application sở hữu).

## Cấu hình môi trường

`Api/appsettings.Development.json` chỉ chứa giá trị không nhạy cảm. Docker Compose lấy credential local từ `.env`: sao chép `.env.example` thành `.env`, rồi thay password. `.env` đã bị Git bỏ qua.

`.env` chỉ phục vụ thay thế biến trong Docker Compose; nó **không** tự đưa biến vào tiến trình chạy bởi `dotnet run`. ASP.NET Core mặc định nạp environment variables, với `__` biểu thị cấu hình lồng nhau. Khi chạy local, cấp khóa qua .NET User Secrets (đã được bật trong project API); production phải lấy giá trị từ secret manager của hạ tầng.

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

Sinh RSA key pair cục bộ bằng ít nhất 2048-bit (khuyến nghị 3072-bit), rồi đưa nội dung PEM vào secret store phù hợp:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-private.pem
openssl pkey -in jwt-private.pem -pubout -out jwt-public.pem
```

Không truyền private PEM qua command-line. Dùng giao diện hoặc cơ chế import an toàn của secret manager/User Secrets để lưu giá trị đa dòng.

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

JWKS được cache công khai tối đa 5 phút. Để rotate key an toàn: thêm key pair mới nhưng chưa active, deploy để JWKS công bố cả hai public key, chờ cache hết hạn, đổi `ActiveKeyId`, sau đó giữ public key cũ ít nhất bằng access-token lifetime cộng clock skew trước khi gỡ nó.
