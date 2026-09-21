# AuthService

Service xác thực cho hệ thống DPRENTAL, xây dựng bằng .NET theo kiến trúc phân lớp.

## Cấu trúc

```text
AuthService/
  src/
    Common/       # Primitive dùng chung, gồm IRepo<TEntity, TKey> CRUD
    Domain/       # DTOs, Entities, IRepositories, Models, Repositories, Utils, Validators, persistence
    Application/  # Services, IServices, Security, Mapping và application logic
    Api/          # Controllers, Filters, HTTP/security composition root
  docs/           # Tài liệu kỹ thuật
docker-compose.yml
```

Hướng phụ thuộc: `Api -> Application -> Domain`; các lớp có thể dùng `Common`. API tham chiếu trực tiếp Domain chỉ tại composition root để đăng ký persistence.

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
docker compose up -d
```

PostgreSQL lắng nghe ở `localhost:5432`; Valkey (Redis-compatible) ở `localhost:6379`. `.env` không được commit.

Trước khi chạy API, cấp connection string, RSA JWT key ring bằng User Secrets hoặc secret manager theo [hướng dẫn local](docs/README.md#chạy-api-cục-bộ). Không đặt password hoặc signing key vào `appsettings*.json`.

## Kiểm tra trước khi push

Mỗi developer cần cài hook một lần cho mỗi clone. Hook fetch `origin/auth/develop`, chặn push nếu branch hiện tại chưa chứa commit mới nhất của branch này, sau đó build và chạy toàn bộ test của `AuthService.sln` ở cấu hình Release.

Linux/macOS:

```bash
./scripts/setup-git-hooks.sh
```

Windows PowerShell:

```powershell
./scripts/setup-git-hooks.ps1
```

Để chạy kiểm tra thủ công trước khi push:

```bash
./scripts/pre-push-check.sh
```

Regression test cho logic hook (không cần GitHub hay restore .NET):

```bash
./scripts/tests/pre-push-check.test.sh
```

Khi branch bị chặn do chưa đồng bộ, merge hoặc rebase `origin/auth/develop` vào branch hiện tại, giải quyết conflict và chạy lại. Hook là guard local; các quy tắc bảo vệ branch và CI phía server vẫn nên được duy trì riêng.

## Docker artifact

Dockerfile ở repository root có hai target cho deploy, đều dùng cùng source revision:

```bash
# Image runtime của AuthService
docker build --target runtime -t ghcr.io/<org>/dprental-authservice:<git-sha> .

# Image chạy EF Core migration một lần trước khi khởi động API
docker build --target migrator -t ghcr.io/<org>/dprental-auth-migrator:<git-sha> .
```

Runtime lắng nghe cổng `8080`, chạy non-root UID `1654` và cung cấp `/health/live` cùng `/health/ready`. Readiness kiểm tra kết nối PostgreSQL; migrator nhận cùng cấu hình connection string và JWT key file với runtime.

## Theo dõi trace phân tán

AuthService tiếp tục W3C `traceparent` do Gateway gửi tới và dùng OpenTelemetry để server span của AuthService thuộc cùng trace. Root trace được lấy mẫu 100% ở Development và 10% ở môi trường khác; quyết định lấy mẫu từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình endpoint đầy đủ trong environment. Không đưa token hay credential của collector vào source control:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Trace không được bổ sung JWT, authorization header, request/response body, claim hoặc private key làm attribute.
