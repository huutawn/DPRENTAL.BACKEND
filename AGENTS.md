# Quy Tắc Kỹ Thuật Chung Cho Developer & Coding Agent — NotificationService

> **Ngữ cảnh nghiệp vụ & Sản phẩm**:
> Trước khi thực hiện thay đổi liên quan đến domain, invariant, trạng thái hoặc luồng thông báo, bắt buộc phải đọc **[Project Guide & MVP Domain Invariants](docs/PROJECT_GUIDE.md)** và requirement/acceptance criteria của task được giao.

---

## 1. Nguyên Tắc Thay Đổi Mã Nguồn

- **Đọc và hiểu trước khi sửa**: Luôn đọc kỹ implementation hiện tại, luồng gọi, event contracts và các test liên quan trước khi chỉnh sửa.
- **Tái sử dụng và đơn giản hóa**: Ưu tiên tận dụng các abstraction, helper và pattern sẵn có; giữ thiết kế đơn giản, thực dụng, không over-engineer.
- **Một nguồn chân lý duy nhất (Single Source of Truth)**: Mỗi hành vi nghiệp vụ chỉ có duy nhất một implementation chính thức; không tạo phương án thay thế song song gây trùng lặp.
- **Dọn dẹp triệt để khi thay thế**: Khi thay đổi hoặc nâng cấp logic, phải cập nhật toàn bộ callers/tests và xóa bỏ hoàn toàn đường code cũ/obsolete trong cùng phạm vi task; không để lại shims hay mã thừa.
- **Đúng phạm vi task (Scoped Implementation)**: Tập trung giải quyết trọn vẹn yêu cầu và acceptance criteria của task; tuyệt đối không tự ý mở rộng phạm vi thành đợt refactor toàn hệ thống nếu task không yêu cầu.

---

## 2. Phân Tầng Kiến Trúc & Trách Nhiệm

Giữ nguyên cấu trúc project hiện tại của service (`Domain`, `Application`, `Common`, `Api`, `tests`), phân định trách nhiệm rõ ràng giữa các tầng:

- **Application (Trung tâm nghiệp vụ & Điều phối thông báo)**:
  - **Application Service**: Là nơi duy nhất sở hữu và điều phối toàn bộ quyết định nghiệp vụ thông báo: xác định người nhận (recipient resolution), áp dụng chính sách kênh (channel policy), kiểm tra tùy chọn người dùng (preferences), áp dụng template và render nội dung, ngăn chặn thông báo lỗi thời (stale action suppression via aggregate watermarks), quản lý trạng thái đọc hộp thư (read/unread count, mark-read, mark-all-read with cutoff), điều phối transaction và lập lịch gửi email.
  - **Policies & Validators**: Toàn bộ policy thông báo (NotificationChannelPolicy, StaleActionSuppressionPolicy, PreferencePolicy), validator envelope/DTO và policy bảo mật/phân quyền hỗ trợ cho use case phải nằm tại tầng `Application` và được Application Service trực tiếp sử dụng.
  - **Kafka Consumer Handlers**: Xử lý logic chuyển đổi từng loại event (`RentalOrderCreated`, `RentalOrderPaid`, v.v.) sang notification intent chuẩn; Handler thuộc tầng `Application`.
- **Domain & Entities (Dữ liệu thuần túy & Mapping)**:
  - Entity (`Notification`, `NotificationInboxReceipt`, `NotificationDeliveryJob`, `NotificationPreference`, `NotificationAggregateWatermark`) chỉ lưu giữ trạng thái dữ liệu, cấu trúc bảng, quan hệ, mapping ORM và các thao tác dữ liệu cơ bản nội bộ.
  - Tuyệt đối không đặt workflow, template rendering, logic gửi mail SMTP, kết nối Kafka, quyết định quyền hay network I/O vào Domain; không chuyển nghiệp vụ sang constructor, computed property hay extension method để né quy tắc.
- **Repository (Truy vấn & Persistence)**:
  - Chỉ chịu trách nhiệm truy vấn, lưu trữ dữ liệu, projection, pagination, transaction và concurrency ở mức persistence trên schema `notification`.
  - Thực thi các bộ lọc, sắp xếp, tổng hợp dựa trên tiêu chí nghiệp vụ do Application truyền vào; Repository không tự ý đưa ra quyết định nghiệp vụ.
- **API Controllers, Realtime Hubs & Background Workers (Giao tiếp & Dispatch)**:
  - Chỉ xử lý các concern giao tiếp (transport): routing REST, SignalR hub connection management, Kafka intake loop, BackgroundService timer/polling loops, serialization/deserialization, trích xuất context xác thực (JWT `sub`), bắt lỗi và map kết quả trả về (`ProblemDetails`, status code, Kafka ACK, SMTP dispatch); chuyển tiếp hoàn toàn xử lý sang Application Service.
  - Không chứa quyết định nghiệp vụ; không truy cập database hay tầng persistence trực tiếp để né Application Service.

---

## 3. Nguyên Lý SOLID, DRY & Thiết Kế

- **SRP (Single Responsibility)**: Mỗi class, service hoặc handler chỉ tập trung giải quyết một trách nhiệm nghiệp vụ hoặc một event/channel cụ thể; tránh các god service ôm đồm toàn bộ template và luồng gửi.
- **OCP (Open/Closed)**: Tận dụng các component và abstraction sẵn có (`INotificationEventHandler`, `IEmailSender`, `ITemplateRenderer`); mở rộng bằng cách thêm handler hoặc template mới theo contract; không sửa đổi cấu trúc core consumer khi chỉ thêm loại event.
- **LSP (Liskov Substitution)**: Mọi implementation phải tuân thủ nghiêm ngặt contract của abstraction (kiểu trả về, mã lỗi, hành vi biên); không giả vờ thành công hoặc âm thầm bỏ qua lỗi đối với channel chưa sẵn sàng.
- **ISP (Interface Segregation)**: Interface nhỏ gọn, thiết kế xoay quanh nhu cầu thực tế của consumer (tách biệt `INotificationQueryService`, `INotificationReadService`, `IEmailDeliveryService`); không tạo interface máy móc kiểu 1-class-1-interface nếu không có nhu cầu thực tế.
- **DIP (Dependency Inversion)**: Các tầng cấp cao phụ thuộc vào abstraction thông qua Dependency Injection; tầng Domain hoàn toàn độc lập, không tham chiếu ngược lại tầng Application hay API.
- **DRY (Don't Repeat Yourself)**: Triệt tiêu sự trùng lặp template, logic định tuyến thông báo và mapping dữ liệu; mỗi quy tắc format, URL redirect hoặc payload notification chỉ được quản lý tại một nguồn chân lý duy nhất. Tái sử dụng template renderer, contact resolver và handler abstractions thay vì sao chép mã nguồn.

---

## 4. Truy Cập Dữ Liệu & ORM (LINQ / EF Core)

- **Schema Cô Lập**: NotificationService chỉ thao tác trên schema `notification` của PostgreSQL. Tuyệt đối không đọc, ghi hoặc tham chiếu foreign key sang các schema khác (`rental.*`, `auth.*`, `payment.*`, `inventory.*`).
- **Ưu tiên Server-side Evaluation**: Mọi điều kiện lọc (`Where`), sắp xếp (`OrderBy`), gom nhóm (`GroupBy`), đếm (`CountAsync`) và tổng hợp dữ liệu phải được biên dịch và thực thi trực tiếp tại database engine.
- **Chiếu dữ liệu (Projection)**: Luôn dùng `.Select()` để chỉ tải về các trường dữ liệu cần thiết cho DTO/Record, giảm tải bộ nhớ và băng thông.
- **Truy vấn chỉ đọc**: Bắt buộc sử dụng `.AsNoTracking()` cho toàn bộ các truy vấn đọc (inbox list, unread count, preferences) để tránh chi phí tracking không cần thiết.
- **Phân trang & Bất đồng bộ**: Mọi truy vấn danh sách thông báo phải có phân trang ổn định (`CreatedAt DESC, Id DESC`); luôn thực thi async (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`) và truyền `CancellationToken`.
- **Tránh N+1 và In-Memory Filtering**: Tuyệt đối không tải toàn bộ thông báo vào bộ nhớ (`ToList`, `AsEnumerable`) rồi mới đếm hoặc lọc theo user/trạng thái đọc.
- **Không làm rò rỉ IQueryable**: Repository contract tuyệt đối không trả về `IQueryable<T>`; bắt buộc trả về kiểu dữ liệu cụ thể (`Task<T?>`, `Task<IReadOnlyList<T>>`, `Task<PagedResult<T>>`).
- **Tuân thủ ranh giới Persistence**: Service, Controller, Worker và Consumer tuyệt đối không truy cập `DbContext` trực tiếp, không ép kiểu abstraction thành `DbContext` và không tự khởi tạo concrete repository bằng `new`. Mọi tương tác database phải thông qua abstraction persistence được inject qua DI container.

---

## 5. Quy Chuẩn Sử Dụng Native SQL

- **Ngoại lệ hẹp có căn cứ**: Chỉ sử dụng Native SQL khi LINQ/EF Core không có API tương đương (ví dụ `FOR UPDATE SKIP LOCKED` khi worker claim delivery jobs, hoặc batch mark-all-read có cutoff thời gian). Độ phức tạp của query không tự động trở thành lý do dùng SQL.
- **Chứng minh hiệu năng**: Nếu sử dụng SQL với lý do tối ưu hiệu năng, bắt buộc phải có kết quả đo lường (benchmarking) hoặc Execution Plan (`EXPLAIN ANALYZE`) đối chứng với phương án LINQ tương đương.
- **An toàn SQL Injection**: 100% tham số đầu vào phải được parameterize (sử dụng interpolated string của EF Core hoặc parameter object chuẩn); tuyệt đối không cộng chuỗi SQL thô.
- **Không chứa quyết định nghiệp vụ trong SQL**: SQL chỉ thực hiện thao tác dữ liệu kỹ thuật; toàn bộ validation, kiểm tra quyền hạn và render template phải nằm tại Application.
- **Bắt buộc có Integration Test**: Mọi câu lệnh Native SQL phải có integration test chạy thực tế với PostgreSQL để kiểm chứng cú pháp và tính đúng đắn.

---

## 6. Chất Lượng Mã Nguồn & An Toàn Hệ Thống

- **Code dễ đọc & Tập trung**: Đặt tên biểu cảm theo ubiquitous language (`NotificationInboxReceipt`, `DeliveryJob`, `AggregateWatermark`, `RecipientResolution`); mỗi hàm/file giữ kích thước vừa phải.
- **Kiểm soát đầu vào & Phân quyền**: Validate toàn bộ dữ liệu đầu vào; kiểm tra xác thực (JWT Bearer từ AuthService JWKS), phân quyền và quyền sở hữu tài nguyên (chống IDOR bằng cách chỉ truy vấn dữ liệu thuộc `JWT.sub`).
- **Phân biệt Human JWT và Service JWT**:
  - Human JWT chứa claim `sub` trỏ tới User GUID: được phép truy cập Inbox API và SignalR Hub cá nhân.
  - Service JWT chứa claim `u: "svc"` hoặc sử dụng API Key nội bộ: chỉ dùng cho liên lạc giữa các service (ví dụ health, internal audit); tuyệt đối KHÔNG được truy cập hoặc thao tác hộp thư người dùng.
- **Xử lý lỗi nhất quán**: Sử dụng mẫu `Result` / `Error` cho các lỗi nghiệp vụ dự đoán được; không throw exception để điều khiển luồng logic; map lỗi sang ProblemDetails chuẩn tại tầng API; không để lộ exception stack trace hay thông tin nội bộ ra client.
- **Concurrency & Idempotency**:
  - Ingestion: Idempotency đảm bảo bằng bảng `notification.inbox_receipts` với unique constraint `(consumer_name, message_id)`.
  - Notification uniqueness: `UNIQUE (source_event_id, recipient_user_id, notification_type)`.
  - Delivery job uniqueness: `UNIQUE (notification_id, channel)`.
  - Mark-read / Mark-all-read: Idempotent và có server cutoff timestamp để tránh đánh dấu nhầm thông báo mới phát sinh đồng thời.
- **Giao dịch (Transaction) an toàn**:
  - Inbox receipt, notifications, watermarks và delivery jobs phải được lưu trong cùng 1 database transaction.
  - Tuyệt đối KHÔNG giữ database transaction mở trong lúc thực hiện I/O mạng ngoại vi (Kafka publish, SMTP send, HTTP external call).
- **Bảo mật dữ liệu**: Không log hoặc trả về thông tin nhạy cảm (mật khẩu, full card numbers, bank account details, JWT secrets, SMTP credentials, internal event payloads).

---

## 7. Kiểm Chứng & Báo Cáo

- **Kiểm thử theo hành vi**: Viết unit test cho toàn bộ business rules, policies, template rendering, watermark suppression tại tầng Application; viết integration test cho persistence EF Core, PostgreSQL constraints, Native SQL locking, REST endpoints và SignalR security.
- **Quy tắc chạy kiểm tra**:
  - **Task thay đổi mã nguồn**: Bắt buộc chạy đầy đủ `dotnet build` và `dotnet test` cho các project bị ảnh hưởng; đảm bảo toàn bộ tests pass 100% trước khi kết luận hoàn thành.
  - **Task chỉ thay đổi tài liệu**: Không chạy build/test ứng dụng; thực hiện review diff và chạy `git diff --check` để bảo đảm định dạng sạch.
- **Báo cáo minh bạch**: Báo cáo rõ ràng kết quả kiểm tra thực tế, các lệnh đã chạy, và nêu cụ thể bất kỳ giới hạn hoặc phần việc nào chưa được kiểm chứng.
