# Tài liệu Kỹ thuật MediaService

## 1. Kiến trúc phân lớp & Ranh giới trách nhiệm

MediaService áp dụng mô hình phân lớp sạch (Clean Architecture) tương tự AuthService nhằm tách bạch ranh giới trách nhiệm:

| Dự án | Trách nhiệm | Không được chứa |
| --- | --- | --- |
| `Common` | Kiểu dữ liệu, hằng số, tiện ích nền tảng dùng chung trên toàn hệ thống | Logic nghiệp vụ, truy cập dữ liệu, HTTP controller |
| `Domain` | Mô hình nghiệp vụ (Domain Entities, Value Objects), Domain Exceptions, Repository Interfaces | HTTP pipeline, ASP.NET Core framework, controller |
| `Application` | Use Cases, DTOs, Application Services, Interfaces điều phối domain | Entity Framework DbContext, HTTP Controller, xử lý giao thức HTTP |
| `Api` | Composition root, cấu hình DI, Endpoints, Middleware, Health Checks, Swagger | Logic nghiệp vụ cốt lõi, truy vấn cơ sở dữ liệu trực tiếp |

### Nguyên tắc khởi tạo hiện tại
Ở giai đoạn khởi tạo ban đầu:
- Các project `Common`, `Domain`, `Application` được giữ ở dạng khung (skeleton) với assembly markers.
- Không tạo entity, repository, DTO, migration hoặc service nghiệp vụ giả định trước khi có use case thực tế.
- Các abstraction nghiệp vụ (như lưu trữ metadata, tạo upload token) sẽ chỉ được bổ sung khi có nghiệp vụ cụ thể.

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
   - `ObjectStorage:UseSsl` *(tùy chọn)*: Giá trị boolean (`true`/`false`). Nếu không thiết lập, hệ thống tự động suy diễn dựa trên scheme của `Endpoint` (`https://` => `true`, ngược lại `false`).

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
