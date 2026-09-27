# AuthService

Service xác thực cho hệ thống DPRENTAL, xây dựng bằng .NET theo kiến trúc phân lớp.

## Cấu trúc

```text
AuthService/
  src/
    Common/          # Kiểu dùng chung, enums, Result/Error và utility
    Domain/          # Entities, domain models, repository contracts, EF Core DbContext, migrations và data persistence
    Application/     # DTOs, services, security, mapping
    Api/             # Controllers, Filters, HTTP/security composition root
  tests/              # Kiểm thử
Directory.Build.props
Directory.Packages.props
docs/                 # Tài liệu kỹ thuật
docker-compose.yml
```

Hướng phụ thuộc chính: `Api -> Application -> Domain -> Common`; API composition root đăng ký persistence Domain (`AddAuthDomainPersistence`); các endpoint trao đổi DTO thuộc Application.

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
docker compose up -d
```

PostgreSQL lắng nghe ở `localhost:5433`; Valkey (Redis-compatible) ở `localhost:6379`. `.env` không được commit.

Trước khi chạy API, cấp connection string, RSA JWT key ring bằng User Secrets hoặc secret manager theo [hướng dẫn local](docs/README.md#chạy-api-cục-bộ). Không đặt password hoặc signing key vào `appsettings*.json`.

## Quản lý version NuGet tập trung

AuthService sử dụng Central Package Management (CPM) để quản lý toàn bộ version thư viện tại file `Directory.Packages.props` ở thư mục gốc. Tùy chọn `CentralPackageVersionOverrideEnabled` được đặt thành `false` nhằm ngăn từng project tự ghi đè version chung.

- **Đổi version package có sẵn**:
  Cập nhật thuộc tính `Version` trong thẻ `<PackageVersion Include="<TênPackage>" Version="<VersionMới>" />` tại `Directory.Packages.props`. Mọi project tham chiếu package này sẽ tự nhận version mới.

- **Thêm package mới vào solution**:
  1. Thêm thẻ `<PackageVersion Include="<TênPackage>" Version="<Version>" />` vào `Directory.Packages.props`.
  2. Thêm thẻ `<PackageReference Include="<TênPackage>" />` vào file `.csproj` của project cần dùng (không khai báo thuộc tính `Version`).

- **Dùng package đã có cho project khác**:
  Chỉ cần thêm `<PackageReference Include="<TênPackage>" />` vào file `.csproj` của project đó; version được tự động kế thừa từ file chung.

## Kiểm tra trước khi push

Mỗi developer cần cài hook một lần cho mỗi clone. Hook fetch `origin/auth/develop`, chặn push nếu branch hiện tại chưa chứa commit mới nhất của branch này, sau đó build và chạy toàn bộ test của `AuthService.sln` ở cấu hình Release.
Máy chạy test cần Docker đang hoạt động; các test PostgreSQL dùng chung container `authservice-pg-test` trên cổng 5435 và tự khởi động container khi cần.

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
docker build --target runtime -t ghcr.io/<org>/dprental-auth:<git-sha> .

# Image chạy EF Core migration một lần trước khi khởi động API
docker build --target migrator -t ghcr.io/<org>/dprental-auth-migrator:<git-sha> .
```

Runtime lắng nghe cổng `8080`, chạy non-root UID `1654` và cung cấp `/health/live` cùng `/health/ready`. Readiness kiểm tra kết nối PostgreSQL. Image migrator chứa EF Core migration bundle và các file `appsettings*.json`, không chứa SDK, source code hay `dotnet-ef`. Bundle nhận cấu hình môi trường, connection string và JWT key file từ Compose như migrator trước đó; không đưa secret vào image.

## CI/CD AuthService cho dev

Workflow [authservice-dev.yml](.github/workflows/authservice-dev.yml) chỉ chạy khi có push vào `auth/develop-ci-cd`; nó không chạy cho pull request và không kiểm tra branch nền hoặc chạy test. Job publish build và đẩy hai Docker image lên GHCR một lần, sau đó job deploy chạy trên VPS. Kiểm tra build và test .NET vẫn có trong [pre-push hook](scripts/pre-push-check.sh) ở máy lập trình viên, nhưng hook này có thể bị bỏ qua và không thay thế CI.

Mỗi push sẽ publish hai image GHCR bất biến bằng full Git SHA:

```text
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-auth:<git-sha>
ghcr.io/<GHCR_IMAGE_NAMESPACE>/dprental-auth-migrator:<git-sha>
```

Thiết lập GitHub Environment variable `GHCR_IMAGE_NAMESPACE` bằng namespace GHCR viết thường (ví dụ `huutawn`). Workflow gắn OCI source metadata để package mới tự liên kết repository. Với package đã từng được push thủ công, vào từng package GHCR và cấp repository workflow quyền **Write** trong phần Actions access trước lần CI đầu tiên. Tạo GitHub Environment tên `dev` với các secrets sau; không đặt PostgreSQL password, JWT private key hoặc nội dung `.env` vào GitHub:

```text
DEV_VPS_HOST              # IP hoặc hostname VPS
DEV_VPS_USER              # user deploy thuộc group docker
DEV_VPS_SSH_PRIVATE_KEY   # private key riêng của user deploy
DEV_VPS_KNOWN_HOSTS       # dòng host key theo định dạng known_hosts
DEV_VPS_PORT              # tùy chọn, mặc định 22
```

Workflow nạp deploy key vào `ssh-agent` qua stdin và kiểm tra host key đã lưu trong `DEV_VPS_KNOWN_HOSTS`. Nó truyền `GITHUB_TOKEN` cùng deploy script qua stdin của kết nối SSH, không đặt token trong câu lệnh SSH. Trên VPS, script đăng nhập GHCR bằng Docker config tạm rồi thực thi [deploy-authservice.sh](scripts/deploy-authservice.sh) từ commit đang phát hành. Docker credential tạm bị xóa ngay khi script kết thúc; user SSH của workflow cần quyền đọc `.env` và ghi `release.env`. `.env` và PEM JWT vẫn chỉ tồn tại trên VPS. `release.env` phải đã có hai image AuthService với tag để script thay bằng SHA mới. Script pull hai image bằng một lệnh Compose, chạy migration một lần, chỉ recreate `auth-service`, chờ healthcheck, rồi gọi `/health/ready` qua `DEV_DOMAIN` và kiểm tra JWKS trực tiếp trong AuthService container. Gateway không public JWKS; Gateway tự tải JWKS qua Docker network nội bộ. Nó không rollback database hoặc image nếu migration/healthcheck lỗi.

Ví dụ cấp quyền ghi `release.env` cho user GitHub Actions trên VPS (thay `deploy` bằng user thực tế):

```bash
sudo chown deploy:deploy /opt/dprental/dev/release.env
sudo chmod 600 /opt/dprental/dev/release.env
```

User `deploy` phải thuộc group `docker`; quyền Docker tương đương quyền quản trị VPS nên deploy key chỉ dùng cho automation này. Khóa host SSH có thể lấy từ máy tin cậy bằng `ssh-keyscan -H <vps-host>` rồi lưu nguyên kết quả vào `DEV_VPS_KNOWN_HOSTS`. Nếu chỉ muốn triển khai sau khi merge PR, cấu hình branch protection/ruleset cho `auth/develop-ci-cd` để yêu cầu PR và chặn push trực tiếp; event `push` tự nó không bảo đảm commit đến từ PR.

## Theo dõi trace phân tán

AuthService tiếp tục W3C `traceparent` do Gateway gửi tới và dùng OpenTelemetry để server span của AuthService thuộc cùng trace. Root trace được lấy mẫu 100% ở Development và 10% ở môi trường khác; quyết định lấy mẫu từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình endpoint đầy đủ trong environment. Không đưa token hay credential của collector vào source control:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Trace không được bổ sung JWT, authorization header, request/response body, claim hoặc private key làm attribute.
