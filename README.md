# DPRENTAL service scaffold

Tạo service .NET 10 theo cây thư mục của `AuthService`:

```bash
cd /duong-dan-muon-tao-service
python3 /path/to/DPRENTAL/main/scripts/create-service.py CatalogService
```

Service được tạo tại `./CatalogService` của **thư mục đang đứng**, bất kể script nằm ở đâu. Tên phải theo dạng `PascalCaseService`. Script tạo đủ 43 thư mục của `AuthService`, gồm `Clients`, `IServices`, `Services`, `IRepositories` và `Repositories`; mỗi thư mục có `.gitkeep`. Script cần Python 3 và không sao chép mã nghiệp vụ, secrets hay `.git` từ `AuthService`.

Trong thư mục vừa tạo, đọc `README.md` để cấu hình PostgreSQL, chạy API và bật Git hook. Script chỉ sinh file hook; bạn tự khởi tạo Git và chạy `./scripts/setup-git-hooks.sh` khi cần.

Kiểm tra generator:

```bash
python3 -m unittest discover -s scripts/tests -v
```


## Tra cứu các service trên Git

Hệ thống triển khai theo mô hình microservices, mã nguồn của từng service được lưu trên các nhánh riêng biệt theo nguyên tắc:

```text
<tên-service>/develop-ci-cd
```

Ví dụ nhánh chính thức (develop CI/CD) của các service:

- **Auth Service**: `auth/develop-ci-cd`
- **Catalog Service**: `catalog/develop-ci-cd`
- **Inventory Service**: `inventory/develop-ci-cd`
- **Rental Service**: `rental/develop-ci-cd`
- **Payment Service**: `payment/develop-ci-cd`
- **Media Service**: `media/develop-ci-cd`
- **Recommendation Service**: `recommendation/develop-ci-cd`
- **API Gateway**: `gateway/develop-ci-cd`

### Cách kiểm tra và chuyển nhánh

Liệt kê tất cả các nhánh remote:

```bash
git fetch origin
git branch -r
```

Lọc các nhánh `develop-ci-cd` của các service:

```bash
git branch -r | grep "develop-ci-cd"
```

Checkout sang nhánh của service cần làm việc (ví dụ `catalog`):

```bash
git checkout -b catalog/develop-ci-cd origin/catalog/develop-ci-cd
```
## Common và xử lý lỗi

Template có `Result`/`Result<T>` và `Error`/`ErrorType` (tham khảo AuthService).
Application trả `Result` cho lỗi dự kiến; controller dùng `this.ToProblem(result.Error)`
để ánh xạ sang 400/401/403/404/409/410/422/500. Không gọi `ToProblem(Error.None)`.
`GlobalExceptionHandler` đã đăng ký tại API, trả 500 ProblemDetails kèm `traceId`,
không trả exception message hoặc payload; request bị hủy không chuyển thành lỗi nghiệp vụ.

`Domain/IRepositories/IRepo<TEntity, TKey>` là contract CRUD có CancellationToken,
không phụ thuộc EF/HTTP. Repository theo aggregate chỉ kế thừa khi cần đủ các thao tác;
nếu không, định nghĩa contract nhỏ theo use case. Implementation persistence ở
`Domain/Repositories` theo bố cục hiện tại của AuthService; không sinh generic EF
repository hoặc entity nghiệp vụ giả. `GetAllAsync` chỉ dùng cho tập dữ liệu nhỏ;
với dữ liệu lớn, contract chuyên biệt phải hỗ trợ phân trang.

Template test kiểm tra Result invariant, ánh xạ error và exception qua HTTP ở
Production, gồm fallback khi client không nhận JSON. Không sao chép mã lỗi,
constraint database hay dependency đặc thù nghiệp vụ AuthService.
