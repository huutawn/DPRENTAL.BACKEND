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
