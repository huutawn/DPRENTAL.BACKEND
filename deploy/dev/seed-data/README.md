# Seed thiết bị công trường qua API

Bộ script này thay thế `CatalogService/scripts/seed-catalog.py`. Chỉ script được thay đổi; không cần sửa code Auth, Catalog, Media hay Inventory.

Dữ liệu gồm 5 admin, 5 người dùng, 5 chủ tổ chức, 5 tổ chức Supplier, 10 danh mục, 20 thương hiệu, 50 loại thiết bị và 100 listing. Mỗi listing có 10–50 asset, tổng kế hoạch hiện tại là 3.115 asset. Serial ngẫu nhiên được lưu trước khi gọi API để giữ nguyên khi tiếp tục phiên seed.

Tên, mô tả và nhãn thông số là tiếng Việt có dấu. Mỗi thông số model có URL tài liệu hãng trong `dataset.json`. Cấu hình thị trường được ghi vào mô tả listing. Giá thuê và năm sản xuất là dữ liệu demo. Ảnh thiết bị minh họa theo loại, không phải ảnh xác thực model; giấy tờ tổ chức có ghi rõ dữ liệu demo và không có giá trị pháp lý.

## Cách chạy

Yêu cầu Python 3.10 trở lên trên Linux; không cần cài thư viện Python ngoài standard library. Gateway phải truy cập được Auth, Catalog, Media và Inventory; migration ứng dụng và quyền hệ thống phải tồn tại.

```bash
cd /opt/dprental/dev/scripts/seed-data
python3 seed-data.py --plan
python3 seed-data.py --apply --env-file /opt/dprental/dev/.env
python3 seed-data.py --verify
```

Mặc định Gateway là `https://dev-api.lazyops.cloud`. Có thể đặt `--base-url` và `--state-dir` riêng cho từng môi trường. `--plan` chỉ kiểm tra manifest và checksum, không gọi API ghi. `--apply` luôn chạy kiểm chứng sau khi tạo dữ liệu.

`--request-interval` điều chỉnh khoảng nghỉ giữa các request (mặc định 0,65 giây). Khi Gateway trả 429, script chờ theo `Retry-After` với số lần thử hữu hạn. Hàng nghìn asset có thể mất nhiều thời gian theo hạn mức API; chỉ giảm khoảng nghỉ khi môi trường đích cho phép.

Khóa bootstrap được đọc từ `AUTH_ADMIN_BOOTSTRAP_CREATION_KEY` trong biến môi trường hoặc `--env-file`; script không thực thi nội dung `.env`. Mật khẩu riêng cho từng tài khoản được tạo ngẫu nhiên và lưu tại `.state/state.json` với quyền 0600; thư mục state có quyền 0700. Không đưa thư mục này lên Git hay chia sẻ công khai.

## Tiếp tục và đối soát

Chạy lại cùng lệnh `--apply` để tiếp tục. Giữ nguyên state, URL và manifest. Script đọc lại bản ghi theo khóa nghiệp vụ và đối chiếu nội dung trước khi dùng; không tự ghi đè dữ liệu khác. Nếu POST mất phản hồi, script đối soát qua GET; nếu chưa xác định kết quả, giữ trạng thái pending và dừng thay vì tự POST lại.

Không xóa state để né lỗi pending. Khi database đã reset, dùng một thư mục state mới sau khi xác nhận dữ liệu cũ đã được dọn. Một state không được dùng đồng thời bởi hai tiến trình.

Các thao tác seed dùng API hiện hữu: bootstrap admin, tạo tài khoản, hoàn thiện hồ sơ, đăng ký và duyệt tổ chức, upload Media, tạo Catalog, xuất bản schema và type, tạo listing và asset, kích hoạt listing. Không có câu lệnh SQL seed dữ liệu nghiệp vụ.

## Kiểm tra

```bash
python3 -m unittest discover -s tests -v
python3 -m compileall -q .
python3 seed-data.py --plan
```

`--verify` đọc lại số lượng, chủ sở hữu, thông số, trạng thái, serial, giá thuê, ảnh và khả năng truy cập listing công khai; giấy tờ tổ chức phải được bảo vệ. Kết quả nằm trong `verification.json` ở thư mục state. Kiểm thử unit không thay thế việc chạy trên một stack ứng dụng riêng.

Script dọn dữ liệu nằm ở `../reset-data.sh`; việc chép script lên server không tự thực hiện dọn hoặc seed.

## Bảo toàn giá đã sửa trong phiên seed

Script không tự chấp nhận hoặc ghi đè giá đã thay đổi. Khi người vận hành đã xác nhận giữ giá mới, dừng seed, lưu toàn bộ giá được duyệt vào `listings[].rentalRate` trong state của listing tương ứng (dailyRate, weeklyRate, monthlyRate, currency), rồi tiếp tục cùng state. Giá này được dùng chung khi tạo/đối soát và nghiệm thu; các listing khác vẫn dùng giá trong bộ dữ liệu. Không sửa dataset hoặc fingerprint của phiên đang chạy. State chứa thông tin đăng nhập, không đưa lên Git.
