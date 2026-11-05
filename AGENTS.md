# Quy tắc kỹ thuật cho ReviewService

Tham khảo cách tổ chức và hướng dẫn của `../RentalService/AGENTS.md`.
Tài liệu cấu trúc của service: [docs/README.md](docs/README.md).
Đọc requirement và acceptance criteria trước khi thay đổi; không áp dụng nghiệp vụ,
contract hoặc cấu hình riêng của RentalService vào ReviewService.

## 1. Khám phá trước khi sửa

- Luôn đọc implementation, callers, dependencies, tests và xác định module sở hữu.
- Dùng codebase-memory-mcp theo thứ tự: `search_graph`, `trace_path`,
  `get_code_snippet`, `query_graph`, `get_architecture`.
- Dùng tìm kiếm file/text cho cấu hình, scripts, Dockerfile, tài liệu, literal hoặc
  khi graph thiếu coverage; báo rõ giới hạn nếu graph không khả dụng.
- Kiểm tra hành vi tương đương đã có trước khi thêm file, type, interface hay pipeline.

## 2. Một implementation chính thức

- Ưu tiên sửa, đơn giản hóa hoặc thay thế code hiện có; không tạo implementation song song.
- Khi thay thế, cập nhật tất cả callers, tests, tài liệu, cấu hình và dependencies;
  xóa code chết, adapter lỗi thời, comment và cấu hình không còn dùng trong cùng task.
- Không để code bị comment, tên `NewService`/`ServiceV2` hoặc feature flag để né việc thay thế.
- Compatibility tạm thời phải có lý do, owner, điều kiện gỡ bỏ và tests cho migration/removal.
- Chỉ tạo abstraction cho nhu cầu thực tế; interface nhỏ và thuộc consumer.

## 3. Nguyên Lý SOLID, DRY & Thiết Kế

- **SRP (Single Responsibility)**: Mỗi class, service, controller hoặc handler chỉ tập trung giải quyết một trách nhiệm nghiệp vụ hoặc một use case review cụ thể; tránh god service hay god repository ôm đồm nhiều vai trò.
- **OCP (Open/Closed)**: Tận dụng các abstraction và handler contract sẵn có; mở rộng chức năng qua việc thêm mới handler hoặc validator theo hợp đồng thay vì sửa đổi logic cốt lõi khi không có yêu cầu thay đổi.
- **LSP (Liskov Substitution)**: Mọi implementation phải tuân thủ nghiêm ngặt contract của abstraction (kiểu trả về, mã lỗi, trạng thái review hợp lệ); không giả vờ thành công hoặc âm thầm bỏ qua lỗi.
- **ISP (Interface Segregation)**: Interface nhỏ gọn, thiết kế xoay quanh nhu cầu thực tế của consumer (phân tách rõ repository contract, query service, command service); không tạo interface máy móc 1-class-1-interface nếu không có consumer riêng biệt.
- **DIP (Dependency Inversion)**: Các tầng cấp cao phụ thuộc vào abstraction thông qua Dependency Injection; tầng Domain và Application hoàn toàn độc lập, không tham chiếu ngược lại Api.
- **DRY (Don't Repeat Yourself)**: Triệt tiêu sự trùng lặp tri thức và logic nghiệp vụ đánh giá (state machine của review, moderation, authorization, scoring); mỗi quy tắc nghiệp vụ chỉ có một nguồn chân lý duy nhất. Tái sử dụng các helper và service thay vì sao chép mã nguồn; không trừu tượng hóa gượng ép khi hai đoạn code chỉ trùng lặp ngẫu nhiên mà không cùng lý do thay đổi.

## 4. Cấu trúc và ranh giới

Giữ bố cục `ReviewService/src/{Common,Domain,Application,Api}` và
`ReviewService/tests/Api.Tests` tham khảo RentalService. Nhóm code theo capability
bên trong từng tầng; không tạo hệ thống folder kỹ thuật toàn cục mới.

- **Application**: điều phối use case, phân quyền/quyền sở hữu, transaction, idempotency
  và tích hợp; policies/validators phục vụ use case nằm cùng capability.
- **Domain**: entity, invariant và state transition thuần nghiệp vụ khi có nhu cầu thực;
  không phụ thuộc HTTP hoặc vendor SDK trong logic domain.
- **Persistence**: skeleton hiện đặt contracts tại `Domain/IRepositories`, EF Core và
  implementations tại `Domain/Repositories`. Giữ ranh giới hiện có trong task cấu trúc;
  không thêm implementation Infrastructure song song. Repository không quyết định nghiệp vụ.
- **Api, workers, consumers**: chỉ xử lý transport/context, dispatch đến Application
  và map kết quả; không truy cập database để né Application.
- **Common**: chỉ chứa khái niệm ổn định dùng chung; không biến Utils thành nơi chứa nghiệp vụ.
- Không đọc/ghi bảng thuộc service khác; giao tiếp qua application contract, HTTP hoặc events.
- Không circular dependency, global mutable state hoặc interface chỉ forwarding vô nghĩa.
- `.gitkeep` chỉ giữ cấu trúc; không xem thư mục rỗng là feature đã triển khai.

Không sao chép quy tắc Domain chỉ chứa dữ liệu của RentalService: invariant thuần
nghiệp vụ vẫn được đặt tại Domain theo Engineering Rules của task.

## 5. Chất lượng và an toàn

- Giữ public behavior trừ khi requirement đổi; breaking change phải cập nhật mọi caller đã biết.
- Validate input tại boundary; kiểm tra quyền và invariant trước side effect.
- Dùng `Result`/`Error` hiện có cho lỗi dự kiến, map sang ProblemDetails tại API;
  không nuốt lỗi hoặc fallback âm thầm sang hành vi không an toàn.
- Truyền `CancellationToken` qua I/O; đóng tài nguyên; retry, timeout và background work phải hữu hạn.
- Validate cấu hình lúc startup; không log secret, token, credentials hoặc sensitive payload.
- Không thêm dependency nếu standard library hoặc dependency hiện có đáp ứng được.
- Mỗi file một trách nhiệm; ưu tiên dưới 400 dòng, review tách trách nhiệm khi trên 600;
  không quá 1.000 trừ generated code, migration hoặc ngoại lệ có giải thích.
- Ưu tiên hàm dưới khoảng 60 dòng; không tạo god service hoặc abstraction phỏng đoán.

## 6. Database và tích hợp

- Repository trả kiểu cụ thể, không lộ `IQueryable`; query lớn phải có phân trang.
- Ưu tiên LINQ server-side, projection, async và `AsNoTracking` cho truy vấn chỉ đọc;
  tránh N+1 và tải toàn bảng rồi lọc trong bộ nhớ.
- Native SQL chỉ khi có hạn chế EF/LINQ cụ thể hoặc bằng chứng hiệu năng;
  parameterize mọi input và có integration test với database thực.
- Schema change cần migration; kiểm tra tương thích persisted fields.
  Migration phá hủy phải có chiến lược bảo vệ dữ liệu; dùng expand/contract khi các phiên bản cùng chạy.
- Transaction có boundary rõ ràng, không giữ mở trong cuộc gọi mạng chậm.
- Thao tác retry phải idempotent hoặc phát hiện trùng; kiểm tra concurrency/state hiện tại.

## 7. Kiểm chứng và báo cáo

- Behavior change phải cập nhật tests: success, failure, invalid input, authorization,
  concurrency/idempotency khi liên quan. Không mock chính hành vi đang kiểm thử.
- Chạy targeted tests; chạy regression rộng hơn khi thay đổi shared behavior.
  Với source change, chạy `dotnet build ReviewService.sln`, `dotnet test ReviewService.sln`
  và formatting/static checks phù hợp. Không thay integration tests bằng unit tests tại boundary.
- Với task chỉ tài liệu/thư mục `.gitkeep`, review cấu trúc, links và chạy `git diff --check`;
  không cần build/test ứng dụng vì không thay đổi mã hoặc project/configuration.
- Review toàn bộ diff, cả untracked files; tìm references còn sót của symbol/path đã bỏ;
  kiểm tra duplicate path, kiến trúc, secret/debug/generated artifacts và tài liệu.
- Dùng shell chuẩn, giữ exit code và không che warnings/errors. Không bỏ qua failure để báo pass.
- Báo rõ thay đổi, code cũ được sửa/xóa, quyết định kiến trúc, lệnh kiểm tra và kết quả thực tế,
  checks chưa chạy cùng lý do chính xác, rủi ro hoặc technical debt còn lại.
- Không tuyên bố hoàn thành hoặc verified nếu thiếu bằng chứng.
