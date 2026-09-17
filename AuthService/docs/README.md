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

`.env` chỉ phục vụ thay thế biến trong Docker Compose; nó **không** tự đưa biến vào tiến trình chạy bởi `dotnet run`. ASP.NET Core mặc định nạp environment variables, với `__` biểu thị cấu hình lồng nhau. Khi chạy local, dùng .NET User Secrets (đã được bật trong project API):

```bash
dotnet user-secrets set --project AuthService/src/Api "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=authservice;Username=authservice;Password=<password-trong-.env>"
dotnet user-secrets set --project AuthService/src/Api "ConnectionStrings:Redis" "localhost:6379"
dotnet user-secrets set --project AuthService/src/Api "Auth:Jwt:SigningKey" "<local-development-secret>"
```

User Secrets được lưu ngoài repository và chỉ phù hợp cho Development. Production phải truyền secrets bằng environment variables từ secret manager của hạ tầng.

`Api/appsettings.Production.json` không chứa secret hay connection string. Production phải truyền các giá trị qua environment variables hoặc secret store, ví dụ:

```text
ConnectionStrings__Postgres
ConnectionStrings__Redis
Auth__Jwt__Issuer
Auth__Jwt__Audience
Auth__Jwt__SigningKey
```
