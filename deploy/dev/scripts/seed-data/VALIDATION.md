# Kết quả kiểm tra ngày 13/10/2026

Các kiểm tra đã chạy:

- 18 test Python: kiểm tra manifest, tiếng Việt có dấu, nguồn thông số, giới hạn máy mini, checksum, state/khóa, tiếp tục phiên seed, POST mất phản hồi, read model cập nhật chậm, HTTP, phân trang, quyền ẩn danh và chặn chuyển hướng mang thông tin xác thực.
- `python3 -m compileall -q deploy/dev/seed-data`, `bash -n deploy/dev/reset-data.sh`, kiểm tra khoảng trắng bằng `git diff --check`.
- `--apply` trên stack Docker riêng ở loopback: 15 tài khoản, 5 tổ chức Supplier Active, 10 category, 20 brand, 50 type và schema Published, 100 listing Active, 3.115 asset; 350 URL ảnh công khai đọc được.
- Ngắt phiên seed bằng SIGINT trong lúc đọc lại dữ liệu, rồi tiếp tục bằng cùng state.
- Chạy lại `--apply`: snapshot ID của tất cả operation, account và upload không thay đổi; không tạo upload hay bản ghi mới.
- Chạy riêng `--verify` bằng phiên đăng nhập mới: báo cáo số lượng, ảnh, quyền và đầu vào đạt.
- Kiểm tra user không đọc được quản trị Auth/Catalog, anonymous không đọc được listing nội bộ, owner không đọc được asset của owner khác, schema từ chối values thiếu trường bắt buộc. Giấy tờ tổ chức không được đọc công khai.
- `reset-data.sh --preview` trên VPS thực tế: xác định đúng bốn volume DPRENTAL và các migration; không thực hiện reset.

Test tích hợp dùng schema và cấu hình RBAC hệ thống tương ứng phiên bản triển khai, không sao chép tài khoản hoặc dữ liệu nghiệp vụ từ VPS. Container và database test tách biệt; Gateway test nâng hạn mức để kiểm tra số lượng lớn trong thời gian hợp lý. Hạn mức Gateway VPS được giữ nguyên.

Các image ứng dụng được kiểm tra:

| Dịch vụ | Commit image |
| --- | --- |
| Auth | `6038b9ccb6adc2c0bab57898cf8394c7e4f2b45e` |
| Catalog | `04428881f86857e7bdcbf01cf008c22f174e9ea9` |
| Inventory | `3a5f330b0cdc2580e9042dd82a258baf4a713df0` |
| Media | `1793ab451e56c685916d8518aedb3957c32364de` |
| Gateway | `7dcffdc48c453ba4dd360072ae048b6d456b56d3` |

Báo cáo số lượng không chứa mật khẩu nằm trong `verification-example.json`. Thông tin đăng nhập test không nằm trong bộ script gửi lên VPS.

Chưa chạy `reset-data.sh --execute` và chưa chạy `--apply` trên VPS: phạm vi hiện tại là thiết kế, kiểm tra và chép script, không dọn hoặc tiêm dữ liệu thật. Không chạy Ruff/ShellCheck vì hai công cụ này chưa được cài trong môi trường; kiểm tra cú pháp và test nêu trên đã thực hiện. Các thay đổi code ứng dụng sẵn có trong workspace được giữ nguyên; không build hoặc sửa ứng dụng để phục vụ script.

`dataset.json` là nguồn dữ liệu duy nhất lúc chạy. Bộ seed Catalog cũ và ảnh không còn sử dụng đã được bỏ. Script không mở kết nối SQL để seed dữ liệu nghiệp vụ; mọi thao tác tạo dữ liệu đều đi qua API đang tồn tại.
