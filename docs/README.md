# Tài liệu Kỹ thuật MediaService

## 1. Kiến trúc phân lớp & Ranh giới trách nhiệm

MediaService áp dụng mô hình phân lớp sạch (Clean Architecture) tương tự AuthService nhằm tách bạch ranh giới trách nhiệm:

| Dự án | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu, Enums (`MediaStatus`, `UploadSessionStatus`, ...), Result, Error dùng chung | Logic nghiệp vụ, truy cập dữ liệu, HTTP controller |
| `Domain` | Entities, `IRepositories`, `Repositories`, `IStorage`, `Storage`, `Configurations`, `Migrations`, `MediaDbContext` | HTTP pipeline, ASP.NET Core framework, controller |
| `Application` | DTOs, Policies (kiểm tra MIME, magic bytes, sanitize tên file), `IServices`, `Services` | Entity Framework DbContext, HTTP Controller, xử lý giao thức HTTP |
| `Api` | `Controllers/MediaController`, Composition root, cấu hình DI, Security (JWT), Middleware, Health Checks | Logic nghiệp vụ cốt lõi, truy vấn cơ sở dữ liệu trực tiếp |

## 2. Quản lý cấu hình & Bảo mật

MediaService sử dụng 2 nhóm cấu hình bắt buộc:

1. **Chuỗi kết nối PostgreSQL**:
   - Khóa: `ConnectionStrings:Postgres`
   - Định dạng: `Host=<host>;Port=<port>;Database=<db>;Username=<user>;Password=<password>`

2. **Cấu hình Object Storage (MinIO/S3)**:
   - Khóa section: `ObjectStorage`
   - `ObjectStorage:Endpoint`: Địa chỉ dịch vụ MinIO (ví dụ: `http://127.0.0.1:9000` hoặc `127.0.0.1:9000`).
   - `ObjectStorage:Bucket`: Tên bucket lưu trữ media (ví dụ: `dprental-media`).
   - `ObjectStorage:AccessKey`: Tên tài khoản hoặc Access Key truy cập MinIO.
   - `ObjectStorage:SecretKey`: Mật khẩu hoặc Secret Key tương ứng.
   - `ObjectStorage:UseSsl` *(tùy chọn)*: Giá trị boolean (`true`/`false`). Nếu không thiết lập, hệ thống tự động suy diễn dựa trên scheme của `Endpoint`.
   - `ObjectStorage:BrowserEndpoint` *(tùy chọn)*: Địa chỉ MinIO mà trình duyệt truy cập được để đọc URL GET ký sẵn. Nếu không cấu hình, dùng `Endpoint`.
   - `ObjectStorage:AllowedOrigins` *(tùy chọn)*: Các origin được phép gọi MediaService qua CORS.
   - `ObjectStorage:ReadUrlExpirySeconds` *(tùy chọn)*: Thời hạn URL đọc ký sẵn, mặc định 300 giây.
   - `Jwt:Issuer`, `Jwt:Audience`, `Jwt:JwksUri`: Thông tin xác thực JWT RS256 từ AuthService. Không có khóa ký JWT mặc định trong MediaService.
   - `Jwt:ServicePrincipals`: Ánh xạ `sub` (GUID service principal) sang tên service sở hữu; token service cần scope `media.verify`.
### Cơ chế Startup Validation
Khi dịch vụ khởi động, extension `ValidateRequiredConfiguration()` và `ObjectStorageOptionsValidator` (thông qua `ValidateOnStart()`) sẽ được kích hoạt:
- Nếu thiếu hoặc để trống `ConnectionStrings:Postgres`: Ứng dụng lập tức dừng và ném ngoại lệ `InvalidOperationException` thông báo rõ ràng: `"Thiếu cấu hình bắt buộc: ConnectionStrings:Postgres."`
- Nếu thiếu hoặc để trống bất kỳ trường nào trong `ObjectStorage` (`Endpoint`, `Bucket`, `AccessKey`, `SecretKey`): Ứng dụng lập tức dừng và ném ngoại lệ thông báo chi tiết danh sách các trường bị thiếu.

### Nguyên tắc bảo mật Secret
- `.env`: Chỉ dùng để truyền biến môi trường vào các container trong `docker-compose.yml`. File này đã được thêm vào `.gitignore` và tuyệt đối không commit lên Git.
- `appsettings.json` và `appsettings.Development.json`: Chỉ lưu các cấu hình khung hoặc giá trị không nhạy cảm. Không bao giờ lưu mật khẩu, AccessKey hoặc SecretKey vào đây.
- **Local Development**: Luôn nạp thông tin bí mật thông qua **.NET User Secrets** của project `MediaService.Api.csproj`.

## 3. Cơ chế Health Checks (Liveness & Readiness)

Dịch vụ cung cấp 2 endpoint giám sát sức khỏe tiêu chuẩn:

### 3.1. Liveness Probe (`GET /health/live`)
- **Mục đích**: Xác định tiến trình dịch vụ đang chạy và phản hồi được HTTP request.
- **Hành vi**: Không phụ thuộc vào cơ sở dữ liệu hay MinIO. Trả về `200 OK` (`Healthy`) ngay khi Web Application đang chạy.

### 3.2. Readiness Probe (`GET /health/ready`)
- **Mục đích**: Xác định dịch vụ đã sẵn sàng tiếp nhận lưu lượng hay chưa.
- **Hành vi**: Kiểm tra đồng thời 2 thành phần phụ thuộc hạ tầng với tag `"ready"`:
  1. `postgres`: Kết nối tới PostgreSQL bằng chuỗi kết nối đã cấu hình và thực thi lệnh truy vấn kiểm tra (`SELECT 1;`).
  2. `minio`: Sử dụng thông tin xác thực (`AccessKey`, `SecretKey`) để kiểm tra sự tồn tại của bucket `Bucket` thông qua API MinIO S3 (`BucketExistsAsync`).
- **Kết quả**:
  - `200 OK` (`Healthy`): Cả PostgreSQL và bucket MinIO đều phản hồi thành công và bucket tồn tại.
  - `503 Service Unavailable` (`Unhealthy`): Một trong các điều kiện sau xảy ra:
    - Không kết nối được tới PostgreSQL (sai host, port, credentials hoặc service chưa bật).
    - Không kết nối được tới MinIO (sai endpoint, timeout).
    - Thông tin xác thực MinIO sai (AccessKey hoặc SecretKey không hợp lệ).
    - Bucket được cấu hình không tồn tại trên MinIO.

## 4. Hướng dẫn thiết lập & Chạy cục bộ

### Bước 1: Khởi động hạ tầng với Docker Compose
```bash
# Tạo file .env từ template mẫu
cp .env.example .env

# Khởi động PostgreSQL, MinIO và container tự động tạo bucket
docker compose up -d
```

Sau khi khởi động:
- PostgreSQL lắng nghe tại `127.0.0.1:5434`.
- MinIO API lắng nghe tại `127.0.0.1:9000`.
- MinIO Web Console lắng nghe tại `127.0.0.1:9001` (truy cập bằng trình duyệt với `mediaadmin` / `mediasecretpassword`).
- Container `minio-init` sẽ tạo bucket `dprental-media` và đặt quyền `private`.

### Bước 2: Thiết lập User Secrets cho .NET
Chạy các lệnh sau tại thư mục gốc repository để cấp thông tin cho API:

```bash
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=127.0.0.1;Port=5434;Database=mediaservice;Username=mediaservice;Password=mediasecret" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:Endpoint" "http://127.0.0.1:9000" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:Bucket" "dprental-media" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:AccessKey" "mediaadmin" \
  --project MediaService/src/Api/MediaService.Api.csproj

dotnet user-secrets set "ObjectStorage:SecretKey" "mediasecretpassword" \
  --project MediaService/src/Api/MediaService.Api.csproj
```

### Bước 3: Chạy ứng dụng API
```bash
dotnet run --project MediaService/src/Api/MediaService.Api.csproj
```

Kiểm tra trạng thái sẵn sàng:
```bash
curl -i http://localhost:5000/health/ready
```
Kết quả mong đợi: `HTTP/1.1 200 OK` kèm nội dung `Healthy`.

## 5. Xử lý sự cố & Troubleshooting

### 5.1. Ứng dụng văng lỗi ngay khi chạy (`InvalidOperationException`)
- **Triệu chứng**: Thông báo lỗi `Thiếu cấu hình bắt buộc: ConnectionStrings:Postgres` hoặc `Thiếu cấu hình ObjectStorage bắt buộc: ...`.
- **Nguyên nhân**: Chưa thiết lập User Secrets hoặc thiếu một trong các biến cấu hình bắt buộc.
- **Khắc phục**: Chạy lại các lệnh `dotnet user-secrets set` ở Bước 2 và xác minh danh sách secret bằng `dotnet user-secrets list --project MediaService/src/Api/MediaService.Api.csproj`.

### 5.2. `/health/ready` trả về `503 Service Unavailable`
- **Kiểm tra PostgreSQL**:
  - Chạy `docker compose ps` để kiểm tra container `postgres` có trạng thái `healthy` không.
  - Thử kết nối thủ công: `pg_isready -h 127.0.0.1 -p 5434 -U mediaservice -d mediaservice`.
- **Kiểm tra MinIO**:
  - Đăng nhập vào MinIO Console tại `http://127.0.0.1:9001` bằng tài khoản quản trị.
  - Kiểm tra xem bucket `dprental-media` đã được tạo hay chưa.
  - Nếu bucket chưa có: chạy `docker compose restart minio-init` hoặc tự tạo bucket trên giao diện console.
  - Kiểm tra lại AccessKey và SecretKey đã cấp trong User Secrets có khớp với `MINIO_ROOT_USER` và `MINIO_ROOT_PASSWORD` trong `.env` không.

### 5.3. Git hook pre-push chặn quá trình `git push`
- **Lỗi 1: Branch hiện tại chưa chứa commit mới nhất từ `origin/media/develop`**:
  - Khắc phục: Chạy `git fetch origin media/develop && git merge origin/media/develop` (hoặc `rebase`), giải quyết xung đột (nếu có) trước khi push lại.
- **Lỗi 2: Build hoặc Test thất bại**:
  - Khắc phục: Khắc phục lỗi biên dịch hoặc các bài kiểm thử bị hỏng, đảm bảo lệnh `dotnet build MediaService.sln --configuration Release` và `dotnet test MediaService.sln --configuration Release --no-build` vượt qua thành công.
- **Lưu ý đối với lần push đầu tiên**: Nếu branch `origin/media/develop` chưa từng tồn tại trên remote, hook sẽ phát hiện qua `git ls-remote` và tự động cho phép bỏ qua kiểm tra ancestry mà vẫn đảm bảo quá trình build/test chạy đầy đủ.

## 6. Hợp đồng API Upload Media & Xác minh

Tất cả các endpoint nghiệp vụ media đều sử dụng tiền tố chuẩn hóa `/api/v1/media`:

### 6.1. Tải file trực tiếp (`POST /api/v1/media/uploads`)
- **Xác thực**: Yêu cầu User Access Token (Bearer) hợp lệ; header tự khai báo danh tính không được tin cậy.
- **Header tùy chọn**: `Idempotency-Key` để gửi lại cùng request mà không tạo media khác.
- **Body**: `multipart/form-data` gồm `file` (binary), `purpose`, `ownerEntityId`; `sha256Checksum` là tùy chọn. Tên, MIME và kích thước lấy từ phần `file`, không cần khai báo riêng.
- **Quy tắc policy ban đầu**:
  - `equipment-image` & `organization-logo`: nhận `image/jpeg`, `image/png`, `image/webp`, tối đa 10 MiB.
  - `equipment-document`: nhận `application/pdf`, tối đa 20 MiB.
  - Purpose khác: bị từ chối cho đến khi được cấu hình.
  - `ownerService` và `ownerEntityType` được xác định từ cấu hình purpose, không nhận từ client.
- **Kết quả**: HTTP 200 OK sau khi MediaService lưu file vào MinIO, xác minh và chuyển media sang `Ready`:
```json
{
  "mediaId": "uuid",
  "status": "Ready",
  "originalFileName": "may-xuc.jpg",
  "contentType": "image/jpeg",
  "sizeBytes": 1250340,
  "previewUrl": "https://storage.example/signed-read-url",
  "previewUrlExpiresAtUtc": "2026-09-27T10:05:00Z"
}
```
MediaService tự xác minh file signature, MIME và checksum nếu có. File không hợp lệ bị từ chối; object tạm được dọn qua worker. `previewUrl` chỉ dành cho người upload, hết hạn sau `ObjectStorage:ReadUrlExpirySeconds`; nếu ký URL lỗi sau khi file đã lưu, hai trường preview là null và `mediaId` vẫn sử dụng được. Retry cùng `Idempotency-Key` và cùng file trả cùng `mediaId` cùng một URL preview mới. Client không cần gọi MinIO hoặc endpoint `/complete`.

### 6.2. Xác minh MediaId nội bộ (`GET /api/v1/media/{mediaId}/verify`)
- **Xác thực**: Yêu cầu Service Token AuthService (`u=svc`, scope `media.verify`), có `sub` nằm trong `Jwt:ServicePrincipals`. MediaService chỉ trả Media Ready thuộc service đó.
- **Mục đích**: Cho phép Business Service kiểm tra trạng thái Media trước khi lưu tham chiếu vào entity nghiệp vụ.
- **Kết quả**: HTTP 200 OK
```json
{
  "mediaId": "uuid",
  "status": "Ready",
  "ownerService": "CatalogService",
  "ownerEntityType": "Equipment",
  "ownerEntityId": "equipment-123",
  "purpose": "equipment-image",
  "uploadedByUserId": "user-456",
  "originalFileName": "may-xuc.jpg",
  "contentType": "image/jpeg",
  "sizeBytes": 1250340,
  "createdAtUtc": "2026-09-25T10:00:00Z"
}
```

## 7. Hướng dẫn thiết lập tài khoản MinIO giới hạn quyền cho Môi trường Triển khai

Trong môi trường sản xuất (Production), **tuyệt đối không sử dụng tài khoản Root (`MINIO_ROOT_USER`)** cho MediaService. Thay vào đó, tạo một Service Account với chính sách IAM tối thiểu (Least Privilege Principle) chỉ trên bucket `dprental-media`:

### 7.1. Tạo IAM Policy giới hạn
Tạo file policy `mediaservice-policy.json`:
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "MediaBucketLocationAndList",
      "Effect": "Allow",
      "Action": [
        "s3:GetBucketLocation",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::dprental-media"
      ]
    },
    {
      "Sid": "MediaObjectOperations",
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject"
      ],
      "Resource": [
        "arn:aws:s3:::dprental-media/*"
      ]
    }
  ]
}
```

### 7.2. Áp dụng Policy và Tạo Service Account bằng MinIO Client (`mc`)
```bash
# 1. Cấu hình alias kết nối tới MinIO Cluster
mc alias set localminio http://127.0.0.1:9000 mediaadmin mediasecretpassword

# 2. Tạo policy mediaservice-policy
mc admin policy create localminio mediaservice-policy mediaservice-policy.json

# 3. Tạo service account dành riêng cho MediaService với policy vừa tạo
mc admin user svcacct add \
  --access-key "mediaservice-app" \
  --secret-key "strong-service-account-secret-key" \
  --policy mediaservice-policy \
  localminio mediaadmin
```

### 7.3. Cập nhật cấu hình dịch vụ MediaService
Cung cấp AccessKey `mediaservice-app` và SecretKey `strong-service-account-secret-key` vào cấu hình `ObjectStorage:AccessKey` và `ObjectStorage:SecretKey` của MediaService. Tài khoản này chỉ có quyền thao tác trên bucket `dprental-media`, không có quyền quản trị cluster hay truy cập các bucket khác.
