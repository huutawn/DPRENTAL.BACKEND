# Quy Tắc Kỹ Thuật Chung Cho Developer & Coding Agent

> **Ngữ cảnh nghiệp vụ & Sản phẩm**:
> Trước khi thực hiện thay đổi liên quan đến domain, invariant, trạng thái hoặc luồng nghiệp vụ, bắt buộc phải đọc **[Project Guide & MVP Domain Invariants](docs/PROJECT_GUIDE.md)** và requirement/acceptance criteria của task được giao.

---

## 1. Nguyên Tắc Thay Đổi Mã Nguồn

- **Đọc và hiểu trước khi sửa**: Luôn đọc kỹ implementation hiện tại, luồng gọi và các test liên quan trước khi chỉnh sửa.
- **Tái sử dụng và đơn giản hóa**: Ưu tiên tận dụng các abstraction, helper và pattern sẵn có; giữ thiết kế đơn giản, thực dụng, không over-engineer.
- **Một nguồn chân lý duy nhất (Single Source of Truth)**: Mỗi hành vi nghiệp vụ chỉ có duy nhất một implementation chính thức; không tạo phương án thay thế song song gây trùng lặp.
- **Dọn dẹp triệt để khi thay thế**: Khi thay đổi hoặc nâng cấp logic, phải cập nhật toàn bộ callers/tests và xóa bỏ hoàn toàn đường code cũ/obsolete trong cùng phạm vi task; không để lại shims hay mã thừa.
- **Đúng phạm vi task (Scoped Implementation)**: Tập trung giải quyết trọn vẹn yêu cầu và acceptance criteria của task; tuyệt đối không tự ý mở rộng phạm vi thành đợt refactor toàn hệ thống nếu task không yêu cầu.

---

## 2. Phân Tầng Kiến Trúc & Trách Nhiệm

Giữ nguyên cấu trúc project hiện tại của service (`Domain`, `Application`, `Api`, `tests`), phân định trách nhiệm rõ ràng giữa các tầng:

- **Application (Trung tâm nghiệp vụ)**:
  - **Application Service**: Là nơi duy nhất sở hữu và điều phối toàn bộ quyết định nghiệp vụ: xác thực quyền và sở hữu, kiểm tra business invariants, tính toán tài chính/thời hạn, điều phối state transitions, xử lý idempotency, điều phối transaction và tích hợp ngoại vi.
  - **Policies & Validators**: Toàn bộ policy nghiệp vụ, validator dữ liệu và policy bảo mật/phân quyền hỗ trợ cho use case phải nằm tại tầng `Application` và được Application Service trực tiếp sử dụng.
- **Domain & Entities (Dữ liệu thuần túy & Mapping)**:
  - Entity chỉ lưu giữ trạng thái dữ liệu, cấu trúc bảng, quan hệ (relationships, foreign keys), mapping ORM và các thao tác dữ liệu cơ bản nội bộ.
  - Tuyệt đối không đặt workflow, công thức nghiệp vụ, quyết định quyền, deadline hay state transition vào Domain; không chuyển nghiệp vụ sang constructor, computed property hay extension method để né quy tắc.
- **Repository (Truy vấn & Persistence)**:
  - Chỉ chịu trách nhiệm truy vấn, lưu trữ dữ liệu, projection, pagination, transaction và concurrency ở mức persistence.
  - Thực thi các bộ lọc, sắp xếp, tổng hợp dựa trên tiêu chí nghiệp vụ do Application truyền vào; Repository không tự ý đưa ra quyết định nghiệp vụ.
- **API Controllers & Background Workers (Giao tiếp & Dispatch)**:
  - Chỉ xử lý các concern giao tiếp (transport): routing, serialization/deserialization, trích xuất context xác thực, bắt lỗi và map kết quả trả về (`ProblemDetails`, status code, Kafka ACK); chuyển tiếp hoàn toàn xử lý sang Application Service.
  - Không chứa quyết định nghiệp vụ; không truy cập database hay tầng persistence trực tiếp để né Application Service.

---

## 3. Nguyên Lý SOLID & Thiết Kế

- **SRP (Single Responsibility)**: Mỗi class, service hoặc hàm chỉ tập trung giải quyết một trách nhiệm nghiệp vụ hoặc một use case cụ thể; tránh các god service hoặc god repository ôm đồm quá nhiều trách nhiệm.
- **OCP (Open/Closed)**: Tận dụng các component và abstraction sẵn có; chỉ mở rộng khi có yêu cầu nghiệp vụ thực tế phát sinh từ task; không xây trước abstraction phỏng đoán tương lai.
- **LSP (Liskov Substitution)**: Mọi implementation phải tuân thủ nghiêm ngặt contract của abstraction (kiểu trả về, mã lỗi, hành vi biên); không giả vờ thành công hoặc âm thầm trả về tập rỗng cho chức năng chưa hỗ trợ.
- **ISP (Interface Segregation)**: Interface nhỏ gọn, thiết kế xoay quanh nhu cầu thực tế của consumer; không tạo interface máy móc kiểu 1-class-1-interface nếu không có consumer riêng biệt hoặc nhu cầu kiểm thử thực tế.
- **DIP (Dependency Inversion)**: Các tầng cấp cao phụ thuộc vào abstraction thông qua Dependency Injection; tầng Domain hoàn toàn độc lập, không tham chiếu ngược lại tầng Application hay API.

---

## 4. Truy Cập Dữ Liệu & ORM (LINQ / EF Core)

- **Ưu tiên Server-side Evaluation**: Mọi điều kiện lọc (`Where`), sắp xếp (`OrderBy`), gom nhóm (`GroupBy`), đếm (`CountAsync`) và tổng hợp dữ liệu phải được biên dịch và thực thi trực tiếp tại database engine.
- **Chiếu dữ liệu (Projection)**: Luôn dùng `.Select()` để chỉ tải về các trường dữ liệu cần thiết cho DTO/Record, giảm tải bộ nhớ và băng thông.
- **Truy vấn chỉ đọc**: Bắt buộc sử dụng `.AsNoTracking()` cho toàn bộ các truy vấn đọc để tránh chi phí tracking không cần thiết.
- **Phân trang & Bất đồng bộ**: Mọi truy vấn tập dữ liệu lớn phải có phân trang; luôn thực thi async (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`) và truyền `CancellationToken`.
- **Tránh N+1 và In-Memory Filtering**: Sử dụng eager loading vừa đủ (`Include`/`ThenInclude`) hoặc projection phẳng để triệt tiêu lỗi N+1; tuyệt đối không tải toàn bộ bảng vào bộ nhớ (`ToList`, `AsEnumerable`) rồi mới lọc hay đếm.
- **Không làm rò rỉ IQueryable**: Repository contract tuyệt đối không trả về `IQueryable<T>`; bắt buộc trả về kiểu dữ liệu cụ thể (`Task<T?>`, `Task<IReadOnlyList<T>>`, `Task<PagedResult<T>>`).
- **Tuân thủ ranh giới Persistence**: Service, Controller, Worker và Consumer tuyệt đối không truy cập `DbContext` trực tiếp, không ép kiểu abstraction thành `DbContext` và không tự khởi tạo concrete repository bằng `new`. Mọi tương tác database phải thông qua abstraction persistence được inject qua DI container.

---

## 5. Quy Chuẩn Sử Dụng Native SQL

- **Ngoại lệ hẹp có căn cứ**: Chỉ sử dụng Native SQL khi LINQ/EF Core không có API tương đương (tính năng đặc thù database như Advisory Locks, Sequences) hoặc gặp hạn chế dịch LINQ cụ thể đã được kiểm chứng. Độ phức tạp của query không tự động trở thành lý do dùng SQL.
- **Chứng minh hiệu năng**: Nếu sử dụng SQL với lý do tối ưu hiệu năng, bắt buộc phải có kết quả đo lường (benchmarking) hoặc Execution Plan (`EXPLAIN ANALYZE`) đối chứng với phương án LINQ tương đương.
- **An toàn SQL Injection**: 100% tham số đầu vào phải được parameterize (sử dụng interpolated string của EF Core hoặc parameter object chuẩn); tuyệt đối không cộng chuỗi SQL thô.
- **Không chứa quyết định nghiệp vụ trong SQL**: SQL chỉ thực hiện thao tác dữ liệu kỹ thuật; toàn bộ validation, kiểm tra quyền hạn, deadline và công thức tính toán tài chính phải nằm tại Application Service.
- **Bắt buộc có Integration Test**: Mọi câu lệnh Native SQL phải có integration test chạy thực tế với cơ sở dữ liệu để kiểm chứng cú pháp và tính đúng đắn.

---

## 6. Chất Lượng Mã Nguồn & An Toàn Hệ Thống

- **Code dễ đọc & Tập trung**: Mã nguồn tự tài liệu hóa, đặt tên biểu cảm theo ngôn ngữ miền (ubiquitous language); mỗi hàm/file giữ kích thước vừa phải, tập trung đúng trách nhiệm.
- **Kiểm soát đầu vào & Phân quyền**: Validate toàn bộ dữ liệu đầu vào; kiểm tra xác thực (authentication), phân quyền (authorization) và quyền sở hữu tài nguyên (chống IDOR) tại tầng Application trước khi xử lý nghiệp vụ.
- **Xử lý lỗi nhất quán**: Sử dụng mẫu `Result` / `Error` cho các lỗi nghiệp vụ dự đoán được; không throw exception để điều khiển luồng logic; map lỗi sang ProblemDetails chuẩn tại tầng API; không để lộ exception stack trace hay thông tin nội bộ ra client.
- **Concurrency & Idempotency**: Mọi thao tác có side-effect hoặc nhạy cảm phải đảm bảo an toàn tranh chấp (locking / optimistic concurrency) và an toàn khi retry (idempotency key / operation receipt).
- **Giao dịch (Transaction) an toàn**: Quản lý transaction/savepoint thông qua abstraction của persistence; không giữ database transaction mở khi thực hiện các cuộc gọi mạng ngoại vi (HTTP, Kafka).
- **Bảo mật dữ liệu**: Không log hoặc trả về thông tin nhạy cảm (mật khẩu, token, private identifiers, internal notes).

---

## 7. Kiểm Chứng & Báo Cáo

- **Kiểm thử theo hành vi**: Viết unit test cho toàn bộ business rules, invariants, calculations và workflows tại tầng Application; viết integration test cho persistence, LINQ query, Native SQL, concurrency và API endpoints.
- **Quy tắc chạy kiểm tra**:
  - **Task thay đổi mã nguồn**: Bắt buộc chạy đầy đủ `dotnet build` và `dotnet test` cho các project bị ảnh hưởng; đảm bảo toàn bộ tests pass 100% trước khi kết luận hoàn thành.
  - **Task chỉ thay đổi tài liệu**: Không chạy build/test ứng dụng; thực hiện review diff và chạy `git diff --check` để bảo đảm định dạng sạch.
- **Báo cáo minh bạch**: Báo cáo rõ ràng kết quả kiểm tra thực tế, các lệnh đã chạy, và nêu cụ thể bất kỳ giới hạn hoặc phần việc nào chưa được kiểm chứng.
