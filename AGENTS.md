# Quy Tắc Kỹ Thuật Chung Cho RecommendationService & Coding Agent

Tài liệu này quy định các tiêu chuẩn kiến trúc, thiết kế phần mềm, nguyên tắc thay đổi mã nguồn và quy trình kiểm chứng bắt buộc khi phát triển và bảo trì `RecommendationService`.

---

## 1. Nguyên Tắc Thay Đổi Mã Nguồn

- **Đọc và hiểu trước khi sửa**: Luôn kiểm tra toàn bộ callers, references và test liên quan trước khi sửa đổi bất kỳ interface, class hoặc phương thức nào.
- **Tái sử dụng và đơn giản hóa**: Ưu tiên tận dụng abstraction, helper và pattern sẵn có; giữ thiết kế đơn giản, thực dụng, tuân theo cấu trúc tham chiếu chuẩn của `RentalService`.
- **Một nguồn chân lý duy nhất (Single Source of Truth)**: Mỗi hành vi nghiệp vụ chỉ có duy nhất một implementation chính thức; không tạo implementation song song, helper trùng lặp hoặc phương án thay thế gây phân mảnh code.
- **Dọn dẹp triệt để khi thay thế**: Khi refactor hoặc thay thế logic, phải cập nhật toàn bộ callers/tests và xóa bỏ hoàn toàn code cũ/obsolete (kể cả shims hay comments thừa) trong cùng phạm vi task.
- **Đúng phạm vi task (Scoped Implementation)**: Tập trung giải quyết trọn vẹn yêu cầu và acceptance criteria của task; không tự ý mở rộng phạm vi ngoài yêu cầu.

---

## 2. Phân Tầng Kiến Trúc & Trách Nhiệm

RecommendationService áp dụng phân tầng theo cấu trúc tham chiếu `RentalService` với 4 project: `Common`, `Domain`, `Application`, `Api`:

```text
RecommendationService/src/
  Common/
    CommonAssemblyMarker.cs
    Error.cs
    Result.cs
  Domain/
    DependencyInjection/
    Entities/
    IRepositories/
    Migrations/
    Repositories/
    DomainAssemblyMarker.cs
  Application/
    Background/
    DTOs/
    IServices/
    Kafka/
    Services/
    Validation/
    ApplicationAssemblyMarker.cs
  Api/
    Controllers/
    Exceptions/
    Filters/
    Properties/
    Program.cs
```

### Common (Nền tảng dùng chung)
- Chứa các kiểu dữ liệu nền tảng độc lập nghiệp vụ: `Result`, `Result<T>`, `Error`, `ErrorType` và assembly marker.
- Không tham chiếu tới bất kỳ project nào khác trong solution (`Domain`, `Application`, `Api`).

### Domain (Dữ liệu, Repository Contract & Ngoại Lệ Persistence)
- Sở hữu entity, repository contracts (`IRepositories`), EF Core `DbContext` và repository implementations (`Repositories`), migration (`Migrations`), và đăng ký DI persistence (`DependencyInjection`).
- **Ngoại lệ kiến trúc đã chọn**: EF Core, mapping và repository implementation nằm trực tiếp trong `Domain` theo bố cục tham chiếu của `RentalService` (không tách project Infrastructure riêng).
- **Ranh giới bắt buộc**: Mặc dù persistence nằm trong Domain, các tầng `Application` và `Api` tuyệt đối **không được thao tác persistence trực tiếp**, không inject `DbContext` hay concrete repository. Mọi thao tác lưu trữ phải thông qua interface abstraction được khai báo trong `IRepositories` và inject qua DI container.

### Application (Trung tâm nghiệp vụ & Use Cases)
- Sở hữu use case, logic kiểm tra quyền, validation nghiệp vụ và điều phối luồng xử lý.
- Khi có nghiệp vụ thực tế, code được nhóm theo use case trong các thư mục tương ứng (`DTOs`, `IServices`, `Services`, `Validation`).
- **Background Workers (`Application/Background`)**: Nơi chứa các hosted services/workers chạy ngầm định kỳ (polling outbox, retry event, sync dữ liệu đề xuất, dọn dẹp cache).
- **Kafka Integration (`Application/Kafka`)**: Nơi chứa contract sự kiện, message models, producer và consumer handlers cho các luồng event phân tán.
- Phụ thuộc vào `Domain` (entities và repository contracts) và `Common`. Tuyệt đối không tham chiếu ngược lại `Api` và không truy cập `DbContext` trực tiếp.

### Api Controllers & Background Workers (Giao tiếp & Dispatch)
- Chỉ xử lý các concern giao tiếp (transport): routing, serialization/deserialization, trích xuất context xác thực/header, bắt lỗi tập trung (`GlobalExceptionHandler`), ánh xạ lỗi (`ExpectedErrorFilterExtensions`) sang `ProblemDetails`, map kết quả trả về (`ProblemDetails`, HTTP status code, Kafka ACK/Retry/Dead-Letter); chuyển tiếp hoàn toàn xử lý sang Application Service.
- Đăng ký persistence thông qua extension method `AddRecommendationServiceDomainRepositories` tại namespace `Dprental.RecommendationService.Domain.DependencyInjection`.
- Không chứa quyết định nghiệp vụ và không trực tiếp gọi DbContext hay database để né Application Service.
---

## 3. Nguyên Lý SOLID & Thiết Kế

- **SRP (Single Responsibility)**: Mỗi class, service, controller hoặc hàm chỉ tập trung giải quyết một trách nhiệm cụ thể. Giữ kích thước file và hàm vừa phải:
  - Một phương thức/hàm không nên vượt quá 50 dòng code nếu không có lý do đặc thù.
  - Một file class nên tập trung vào một khái niệm domain hoặc use case duy nhất.
- **OCP (Open/Closed)**: Tận dụng các abstraction và extension methods sẵn có; chỉ mở rộng khi có yêu cầu nghiệp vụ thực tế, không xây trước abstraction phỏng đoán tương lai.
- **LSP (Liskov Substitution)**: Mọi implementation phải tuân thủ nghiêm ngặt contract của abstraction (kiểu trả về, mã lỗi, hành vi biên); không giả vờ thành công hoặc âm thầm trả về tập rỗng cho chức năng chưa hỗ trợ.
- **ISP (Interface Segregation)**: Interface nhỏ gọn, thiết kế xoay quanh nhu cầu thực tế của consumer. Không dùng CRUD interface chung kiểu `IRepo<TEntity, TKey>`; mỗi aggregate/use case định nghĩa contract repository nhỏ và chuyên biệt theo đúng nhu cầu.
- **DIP (Dependency Inversion)**: Các tầng cấp cao phụ thuộc vào abstraction persistence qua Dependency Injection; không khởi tạo concrete repository bằng `new`.

---

## 4. Truy Cập Dữ Liệu & ORM (EF Core / PostgreSQL)

- **Server-side Evaluation**: Mọi điều kiện lọc (`Where`), sắp xếp (`OrderBy`), gom nhóm (`GroupBy`), đếm (`CountAsync`) phải được biên dịch và thực thi trực tiếp tại database engine.
- **Projection**: Luôn dùng `.Select()` để chỉ nạp các trường dữ liệu cần thiết cho DTO/Record, giảm tải bộ nhớ và băng thông.
- **Truy vấn chỉ đọc**: Bắt buộc sử dụng `.AsNoTracking()` cho toàn bộ các truy vấn đọc để tránh chi phí change tracker.
- **Phân trang & Bất đồng bộ**: Mọi truy vấn tập dữ liệu lớn phải có phân trang; luôn thực thi bất đồng bộ (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`) và luôn nhận/truyền `CancellationToken`.
- **Tránh N+1 và In-Memory Filtering**: Sử dụng eager loading vừa đủ (`Include`/`ThenInclude`) hoặc projection phẳng; tuyệt đối không tải toàn bộ bảng vào bộ nhớ (`ToList`, `AsEnumerable`) rồi mới lọc hay đếm.
- **Không rò rỉ IQueryable**: Repository contract tuyệt đối không trả về `IQueryable<T>`; bắt buộc trả về kiểu dữ liệu cụ thể (`Task<T?>`, `Task<IReadOnlyList<T>>`, `Task<PagedResult<T>>`).
- **Tuân thủ ranh giới Persistence**: Service và Controller tuyệt đối không truy cập `DbContext` trực tiếp. Mọi tương tác database phải thông qua repository abstraction được inject qua DI container.

---

## 5. Quy Chuẩn Xử Lý Lỗi, Cancellation & An Toàn

- **Xử lý lỗi nhất quán**: Sử dụng mẫu `Result` / `Error` cho các lỗi nghiệp vụ dự đoán được; không throw exception để điều khiển luồng logic.
- **Ánh xạ lỗi HTTP**: Controller dùng `this.ToProblem(result.Error)` để ánh xạ sang HTTP status code tương ứng (400, 401, 403, 404, 409, 410, 422, 500). Tuyệt đối không gọi `ToProblem(Error.None)`.
- **Bắt lỗi toàn cục**: `GlobalExceptionHandler` bắt các lỗi ngoài dự kiến và trả về RFC 7807 `ProblemDetails` kèm `traceId` an toàn ở HTTP 500, không làm lộ stack trace hay thông tin nhạy cảm.
- **Cancellation**: Mọi phương thức bất đồng bộ thực hiện I/O (database, HTTP, message broker) bắt buộc phải nhận và chuyển tiếp `CancellationToken`. Hủy request không được biến thành lỗi nghiệp vụ sai lệch.
- **Giao dịch (Transaction) an toàn**: Quản lý transaction/savepoint thông qua abstraction của persistence; tuyệt đối không giữ database transaction mở khi thực hiện các cuộc gọi mạng ngoại vi (HTTP calls, Kafka message publishing).
- **Concurrency & Idempotency**: Mọi worker hoặc consumer có side-effect phải đảm bảo an toàn tranh chấp (concurrency) và xử lý lặp lại an toàn (idempotency key, message deduplication).
- **Bảo mật dữ liệu**: Không ghi log hoặc trả về thông tin nhạy cảm (passwords, connection strings, secret tokens, private keys).
---

## 6. Kiểm Chứng & Bằng Chứng Kiểm Thử (Test Evidence)

- **Yêu cầu kiểm thử bắt buộc**:
  - Mọi thay đổi mã nguồn phải đi kèm kiểm thử tương ứng (unit test hoặc integration test).
  - Duy trì toàn bộ bộ kiểm thử hiện có của service (100% pass rate).
  - Kiểm tra tính hợp lệ của DI registration, database readiness failure (HTTP 503) và liveness (HTTP 200).
  - Kiểm tra ranh giới phụ thuộc giữa các assembly theo đúng chiều `Api → Application → Domain → Common`.
- **Quy trình nghiệm thu trước khi bàn giao**:
  1. Build Release thành công: `dotnet build -c Release`
  2. Toàn bộ tests đạt: `dotnet test -c Release`
  3. Định dạng mã nguồn sạch: `dotnet format --verify-no-changes --no-restore`
  4. Diff sạch: `git diff --check`
- **Báo cáo minh bạch**: Báo cáo đầy đủ và chính xác các lệnh đã thực thi, số lượng test đạt, các thay đổi cụ thể và các giới hạn môi trường kiểm thử (ví dụ: health test dùng môi trường test có kiểm soát, không kết nối PostgreSQL vật lý).
