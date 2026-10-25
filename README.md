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

## MediaId và dữ liệu URL cũ

AuthService lưu `AvatarMediaId`, `LogoMediaId` và `BusinessLicenseDocumentMediaId`; GET trả thêm URL để hiển thị. Avatar/logo dùng URL public qua Gateway. Giấy phép giữ private và GET chi tiết tổ chức chỉ cấp URL ký ngắn hạn sau khi kiểm tra quyền. Đăng ký tài khoản không nhận avatar; người dùng upload sau khi đăng nhập. PATCH/PUT hồ sơ dùng `removeAvatar: true` để xóa avatar.

Thiết lập `MediaService__InternalBaseUrl`, `MediaService__PublicGatewayUrl` và `MediaService__ApiKey` trước khi chạy AuthService. API key phải trùng key của MediaService; URL public phải là địa chỉ Gateway mà trình duyệt truy cập được. Trong production không dùng `localhost` cho URL public hoặc URL lưu trữ ký bởi MediaService. Giá trị cấu hình được kiểm tra lúc khởi động.

Migration `AddMediaIdAndOutbox` chỉ **thêm** cột UUID và outbox, giữ cột URL cũ để di trú. Trước cutover, khóa các thao tác ghi URL cũ, nhập file lịch sử vào MediaService, kiểm tra Ready, loại file, uploader và visibility, rồi lập manifest gồm `recordType`, `recordId`, `legacyUrl`, `status: "Mapped"`, `mediaId`, `fileType`, `sizeBytes`, `visibility`. Chạy `DRY_RUN=true scripts/backfill-media-manifest.sh manifest.json` để kiểm tra manifest; sau đó chạy script không có `DRY_RUN` từ thư mục Compose chứa service `postgres` để ánh xạ UUID trong một transaction. Script đối chiếu **toàn bộ** URL còn trong DB, từ chối bản ghi thiếu hoặc URL đã thay đổi. Xác nhận các URL mới đọc được rồi mới triển khai API mới. Cột URL cũ chỉ được bỏ bằng migration contract sau khi mọi instance cũ đã dừng và dữ liệu đã đối chiếu.

Outbox retry tối đa 5 lần; bản ghi `Failed` cần được điều tra rồi phát lại có kiểm soát bằng cách đặt `Status='Pending'`, `RetryCount=0` và `NextRetryAtUtc=NULL` theo `Id` trong bảng `auth.MediaOutboxMessages`. Một tác vụ `Processing` bị ngắt sẽ được worker nhận lại khi lease hết hạn. Ảnh đã gỡ khỏi hồ sơ vẫn public vì MediaService chưa có thông tin tham chiếu từ các service khác.

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

## CI/CD dev với self-hosted runner

Workflow chỉ nhận push vào `auth/develop-ci-cd`. Build/publish vẫn chạy trên
`ubuntu-24.04`, giữ nguyên Dockerfile, build context và toàn bộ targets/tag full SHA.
CI kiểm thử script deployment trên runner hosted; job deploy chỉ được gửi tới
`self-hosted, linux, x64, dprental-deploy` sau khi GitHub API xác nhận repository private và repository variable
`DEV_DEPLOY_ENABLED=true` được bật theo phê duyệt Phase 3. Mặc định job deploy được skip.

Deployment gọi entrypoint do root quản lý; không checkout shell để chạy bằng sudo,
không dùng SSH trong pipeline. Shared release state được khóa bằng `flock`, sử dụng
candidate, backup và phê duyệt có hạn theo SHA; chỉ ghi `release.env` sau health/smoke.

Xem [hướng dẫn vận hành và recovery](docs/SELF_HOSTED_DEPLOYMENT.md) và
[audit/validation](docs/CICD_AUDIT.md). Không cấp runner quyền Docker trực tiếp.

```bash
python3 -m unittest discover -s scripts/tests -p 'test_deploy*.py' -v
```

## Theo dõi trace phân tán

AuthService tiếp tục W3C `traceparent` do Gateway gửi tới và dùng OpenTelemetry để server span của AuthService thuộc cùng trace. Root trace được lấy mẫu 100% ở Development và 10% ở môi trường khác; quyết định lấy mẫu từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình endpoint đầy đủ trong environment. Không đưa token hay credential của collector vào source control:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Trace không được bổ sung JWT, authorization header, request/response body, claim hoặc private key làm attribute.
