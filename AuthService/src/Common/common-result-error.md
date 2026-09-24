# Common: `Result` và `Error`

Ghi chú giải thích hai file trong `AuthService/src/Common`:

- [`Error.cs`](../src/Common/Error.cs): mô tả **một lỗi**.
- [`Result.cs`](../src/Common/Result.cs): **kết quả** của một thao tác, hoặc thành công, hoặc thất bại kèm `Error`.

---

## 1. Hiểu nhanh trong 1 phút

Hãy tưởng tượng bạn gửi một bưu kiện. Bưu điện trả lại cho bạn **một phiếu kết quả**:

- ✅ **Giao thành công**: phiếu ghi "thành công", kèm chữ ký người nhận (**giá trị**).
- ❌ **Giao thất bại**: phiếu ghi "thất bại", kèm **lý do** (sai địa chỉ, không có người nhận...).

Phiếu đó chính là `Result`, còn "lý do" chính là `Error`.

```csharp
// Đăng ký tài khoản: trả về Id của user, hoặc một lỗi
public async Task<Result<Guid>> Handle(RegisterCommand cmd)
{
    if (await _users.EmailExistsAsync(cmd.Email))
        return Error.Conflict("User.EmailTaken", "Email đã được sử dụng."); // ❌ thất bại

    var user = User.Create(cmd.Email, passwordHash);
    await _users.AddAsync(user);
    return user.Id;                                                      // ✅ thành công
}
```

Người gọi dùng như sau:

```csharp
var result = await handler.Handle(cmd);

if (result.IsFailure)
{
    // result.Error.Code    -> "User.EmailTaken"
    // result.Error.Message -> "Email đã được sử dụng."
    return;
}

Guid userId = result.Value; // chỉ đọc Value khi đã chắc chắn thành công
```

---

## 2. Tại sao cần hai file này?

### Vấn đề

Service xác thực có rất nhiều trường hợp **thất bại bình thường**, tức là không phải bug:

| Tình huống | Có phải bug không? |
| --- | --- |
| Sai email hoặc mật khẩu | Không, người dùng gõ nhầm |
| Email đã được đăng ký | Không |
| Refresh token hết hạn | Không |
| Tài khoản bị khoá | Không |
| Mất kết nối database | **Có**, đây là sự cố |

Vậy phải báo những thất bại "bình thường" này lên lớp trên như thế nào?

### Cách cũ: dùng `throw` exception

```csharp
public async Task<Guid> Register(...)
{
    if (emailExists) throw new EmailTakenException();
    ...
}
```

Nhược điểm:

1. **Nhìn chữ ký hàm không biết nó có thể lỗi.** `Task<Guid>` trông như lúc nào cũng thành công, nên người gọi dễ quên `try/catch`.
2. **Exception dành cho chuyện bất thường.** Sai mật khẩu xảy ra hằng ngày, không bất thường. Throw exception cũng tốn tài nguyên hơn, nhất là ở endpoint login hay bị tấn công dò mật khẩu.
3. **Mỗi người tự tạo một class exception**, lâu dần có hàng chục class, xử lý không thống nhất.

### Cách mới: trả về `Result`

```csharp
public async Task<Result<Guid>> Register(...)
{
    if (emailExists) return Error.Conflict("User.EmailTaken", "Email đã được sử dụng.");
    ...
}
```

Ưu điểm:

1. **Nhìn `Result<Guid>` là biết hàm có thể thất bại.**
2. **Mọi lỗi có chung một dạng** (`Code`, `Message`, `Type`), nên dễ xử lý thống nhất.
3. **Exception vẫn được giữ** cho sự cố thật như DB sập, timeout hay bug.

> **Quy tắc ghi nhớ**
> - Lỗi **đoán trước được** (người dùng nhập sai, dữ liệu không thoả điều kiện) → trả về `Result` với `Error`.
> - Lỗi **không đoán trước được** (hạ tầng hỏng, bug) → để exception bay lên, Api xử lý chung và trả 500.

---

## 3. `Error.cs` giải thích chi tiết

### 3.1. `ErrorType`: lỗi thuộc loại gì?

```csharp
public enum ErrorType { Failure, Validation, NotFound, Conflict, Unauthorized, Forbidden }
```

| Loại | Ý nghĩa | Ví dụ trong auth | HTTP (do Api quyết định) |
| --- | --- | --- | --- |
| `Validation` | Dữ liệu vào không hợp lệ | Email sai định dạng, mật khẩu quá ngắn | 400 |
| `Unauthorized` | Chưa xác thực được bạn là ai | Sai mật khẩu, token hết hạn | 401 |
| `Forbidden` | Biết bạn là ai nhưng không có quyền | User thường gọi API admin | 403 |
| `NotFound` | Không tìm thấy | Không có user với id này | 404 |
| `Conflict` | Xung đột với dữ liệu hiện có | Email đã tồn tại | 409 |
| `Failure` | Thất bại chung | Gửi email xác thực không được | 500 |

**Tại sao `Failure` đứng đầu?**
Trong C#, giá trị đầu tiên của enum bằng `0`, cũng là giá trị mặc định. Nếu lỡ quên gán loại lỗi, nó sẽ là `Failure`, loại an toàn nhất, thay vì bị hiểu nhầm là "người dùng nhập sai".

**Tại sao không dùng thẳng mã HTTP (400, 409...)?**
Theo kiến trúc dự án, Domain và Application **không được biết HTTP**. Chúng chỉ nói "đây là lỗi xung đột", còn lớp Api mới quyết định trả 409. Sau này nếu có gRPC hoặc message queue gọi cùng use case, mỗi nơi tự đổi `ErrorType` theo cách của mình.

### 3.2. `record Error`: nội dung của một lỗi

```csharp
public sealed record Error(string Code, string Message, ErrorType Type)
```

| Thuộc tính | Dùng để | Ví dụ |
| --- | --- | --- |
| `Code` | Mã **cố định** để frontend/mobile nhận diện lỗi, dịch đa ngôn ngữ | `"User.EmailTaken"` |
| `Message` | Câu mô tả cho người đọc và log | `"Email đã được sử dụng."` |
| `Type` | Phân loại lỗi, để Api đổi sang mã HTTP | `ErrorType.Conflict` |

Vì sao khai báo như vậy:

- **`record`**: một dòng khai báo mà C# tự sinh constructor, các property chỉ đọc, so sánh theo giá trị và `ToString()` dễ đọc cho log.
- **So sánh theo giá trị**: hai `Error` có cùng `Code`, `Message`, `Type` thì bằng nhau (`==` trả `true`). Nhờ vậy phép so sánh `error == Error.None` mới đúng.
- **Không thể thay đổi**: tạo xong thì không ai sửa được, nên an toàn khi khai báo lỗi dùng chung dạng `static readonly`.
- **`sealed`**: không cho kế thừa. `Error` chỉ là dữ liệu, không cần class con.

**Quy ước đặt `Code`:** dùng dạng `"<Đối tượng>.<Lỗi>"`, ví dụ `User.EmailTaken`, `Auth.InvalidCredentials`, `RefreshToken.Expired`.
⚠️ Frontend dựa vào `Code`, nên **không được đổi `Code`** sau khi đã phát hành. Còn `Message` thì sửa thoải mái.

### 3.3. `Error.None`: "không có lỗi"

```csharp
public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);
```

Kết quả thành công có `Error = Error.None`.

**Tại sao không dùng `null`?** Dự án bật `Nullable`. Nếu `Error` có thể null, mọi chỗ đọc `result.Error` đều phải kiểm tra null. Dùng `None` thì `result.Error` **không bao giờ null**.
(`ErrorType.Failure` ở đây không mang ý nghĩa gì, chỉ vì record bắt buộc có đủ 3 giá trị.)

### 3.4. Các hàm tạo nhanh

```csharp
Error.Validation(code, message)
Error.Unauthorized(code, message)
Error.Forbidden(code, message)
Error.NotFound(code, message)
Error.Conflict(code, message)
Error.Failure(code, message)
```

Hai cách viết sau cho cùng kết quả, nhưng cách thứ hai ngắn hơn và không thể đặt nhầm `ErrorType`:

```csharp
new Error("User.EmailTaken", "Email đã được sử dụng.", ErrorType.Conflict);
Error.Conflict("User.EmailTaken", "Email đã được sử dụng.");
```

### 3.5. Dòng `[SuppressMessage("Naming", "CA1716", ...)]`

Analyzer CA1716 cảnh báo rằng tên `Error` trùng với từ khoá của ngôn ngữ VB.NET. Dự án bật `TreatWarningsAsErrors`, nên cảnh báo này làm build lỗi. Vì dự án chỉ dùng C#, ta tắt rule **cho riêng type `Error`**. Các file khác vẫn được kiểm tra bình thường.

---

## 4. `Result.cs` giải thích chi tiết

Có hai phiên bản:

| Kiểu | Dùng khi | Ví dụ |
| --- | --- | --- |
| `Result` | Thao tác **không trả dữ liệu** | Đăng xuất, đổi mật khẩu |
| `Result<TValue>` | Thao tác **có trả dữ liệu** | Đăng nhập trả token, đăng ký trả `userId` |

### 4.1. Các cách tạo `Result`

```csharp
Result.Success()                    // thành công, không có dữ liệu
Result.Failure(error)               // thất bại

Result.Success(userId)              // thành công, kèm dữ liệu (C# tự suy ra kiểu)
Result.Failure<Guid>(error)         // thất bại, kiểu trả về là Result<Guid>
```

Với `Result<TValue>`, có thể viết gọn hơn nữa (xem mục 4.5):

```csharp
return user.Id;              // tự hiểu là Success
return AuthErrors.User.EmailAlreadyExists; // tự hiểu là Failure
```

### 4.2. Các thuộc tính

| Thuộc tính | Ý nghĩa |
| --- | --- |
| `IsSuccess` | `true` nếu thành công |
| `IsFailure` | Ngược lại của `IsSuccess`, chỉ để đọc code cho tự nhiên |
| `Error` | Lỗi khi thất bại; là `Error.None` khi thành công (không bao giờ null) |
| `Value` | *(chỉ có ở `Result<TValue>`)* Dữ liệu khi thành công |

`IsFailure` được **tính từ** `IsSuccess` (`=> !IsSuccess`) chứ không lưu riêng, nên hai giá trị không bao giờ mâu thuẫn nhau.

### 4.3. Luật bảo vệ 1: không tạo được `Result` vô lý

Trong constructor:

```csharp
if (isSuccess != (error == Error.None))
    throw new ArgumentException(...);
```

Luật: **thành công ⇔ không có lỗi**.

| Tình huống | Hợp lệ? |
| --- | --- |
| Thành công + `Error.None` | ✅ |
| Thất bại + có lỗi | ✅ |
| Thành công + có lỗi | ❌ throw |
| Thất bại + `Error.None` | ❌ throw (thất bại mà không nói lý do) |

Thêm vào đó:

- Constructor của `Result` là **`protected`** và của `Result<TValue>` là **`internal`**, nên code bên ngoài **không thể** gọi `new Result(...)`. Chỉ còn cách dùng `Success`/`Failure`, là những hàm đã được kiểm tra.
- Chọn **`class`** thay vì `struct`, vì với struct có thể viết `default(Result)` để tạo object mà không chạy constructor, sinh ra trạng thái vô lý.

### 4.4. Luật bảo vệ 2: không đọc được `Value` khi thất bại

```csharp
public TValue Value => IsSuccess
    ? _value!
    : throw new InvalidOperationException("Cannot access the value of a failed result.");
```

Nếu **không có** luật này:

```csharp
var result = await login.Handle(cmd); // thất bại vì sai mật khẩu
var token  = result.Value;            // token = null, không ai biết
return Ok(token);                     // trả 200 OK với body rỗng, bug âm thầm
```

Có luật này thì chương trình **dừng ngay tại dòng đọc `Value`**, kèm thông báo rõ ràng, nên bug được phát hiện sớm khi dev hoặc test.

👉 **Luôn kiểm tra `IsSuccess` hoặc `IsFailure` trước khi đọc `Value`.**

### 4.5. Chuyển kiểu tự động (`implicit operator`)

```csharp
public static implicit operator Result<TValue>(TValue value) => Success(value);
public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
```

Nhờ hai dòng này, C# **tự bọc** giá trị hoặc lỗi thành `Result<TValue>`:

| Không có implicit | Có implicit |
| --- | --- |
| `return Result.Success(user.Id);` | `return user.Id;` |
| `return Result.Failure<Guid>(AuthErrors.User.EmailAlreadyExists);` | `return AuthErrors.User.EmailAlreadyExists;` |

Code use case chỉ còn lại logic nghiệp vụ, dễ đọc hơn.

### 4.6. Tại sao `Result<TValue>` kế thừa `Result`?

Để những đoạn code chỉ cần biết "thành công hay thất bại" nhận được **mọi loại** kết quả:

```csharp
void LogIfFailed(Result result) { ... }

LogIfFailed(loginResult);   // Result<TokenResponse>: vẫn truyền vào được
LogIfFailed(logoutResult);  // Result
```

---

## 5. Ví dụ đầy đủ qua các lớp

Tình huống: đăng ký tài khoản với email đã tồn tại.

**① Domain: khai báo lỗi nghiệp vụ**

```csharp
public static class AuthErrors.User
{
    public static readonly Error EmailTaken =
        Error.Conflict("User.EmailTaken", "Email đã được sử dụng.");

    public static readonly Error InvalidCredentials =
        Error.Unauthorized("Auth.InvalidCredentials", "Email hoặc mật khẩu không đúng.");
}
```

**② Application: use case trả về `Result`**

```csharp
public async Task<Result<Guid>> Handle(RegisterCommand cmd)
{
    if (await _users.EmailExistsAsync(cmd.Email))
        return AuthErrors.User.EmailAlreadyExists;

    var user = User.Create(cmd.Email, _hasher.Hash(cmd.Password));
    await _users.AddAsync(user);
    return user.Id;
}
```

**③ Api: đổi `Result` thành HTTP** (viết một lần, dùng cho mọi endpoint)

```csharp
static IResult ToProblem(Error error) => Results.Problem(
    statusCode: error.Type switch
    {
        ErrorType.Validation   => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden    => StatusCodes.Status403Forbidden,
        ErrorType.NotFound     => StatusCodes.Status404NotFound,
        ErrorType.Conflict     => StatusCodes.Status409Conflict,
        _                      => StatusCodes.Status500InternalServerError,
    },
    title: error.Code,
    detail: error.Message);

app.MapPost("/auth/register", async (RegisterCommand cmd, RegisterHandler handler) =>
{
    var result = await handler.Handle(cmd);
    return result.IsSuccess
        ? Results.Ok(new { userId = result.Value })
        : ToProblem(result.Error);
});
```

**④ Client nhận được**

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{ "title": "User.EmailTaken", "detail": "Email đã được sử dụng.", "status": 409 }
```

Mỗi lớp làm đúng phần việc của mình:

| Lớp | Biết điều gì |
| --- | --- |
| Domain | Lỗi **là gì** |
| Application | **Khi nào** xảy ra lỗi |
| Api | Trả lỗi cho client **như thế nào** |

---

## 6. Tại sao đặt ở `Common`?

- Được dùng ở **cả 3 lớp**: Domain tạo lỗi, Application trả `Result`, Api đọc `Result`.
- **Không chứa gì liên quan đến auth** (user, token, password), đúng với định nghĩa `Common` là "thật sự dùng chung".

Những gì **không** đặt ở `Common`:

| Thứ | Đặt ở |
| --- | --- |
| Lỗi cụ thể (`AuthErrors.User.EmailAlreadyExists`) | Domain |
| Entity base, domain event | Domain |
| `IPasswordHasher`, `ITokenService` | Application (interface) |
| Đổi `ErrorType` sang HTTP | Api |

---

## 7. Lưu ý và lỗi thường gặp

1. **Kiểm tra trước khi đọc `Value`.** Trình biên dịch không tự nhắc việc này.
2. **Không bọc mọi exception thành `Result.Failure`.** Lỗi DB, timeout hay bug phải để exception bay lên, nếu không sẽ che mất sự cố thật.
3. **Implicit không chạy khi `TValue` là interface.**
   ```csharp
   Result<IUser> Get() => user;                        // ❌ lỗi biên dịch
   Result<IUser> Get() => Result.Success<IUser>(user); // ✅
   ```
4. **Không dùng `Result<Error>`.** C# sẽ không biết chọn implicit operator nào và báo lỗi ambiguous.
5. **Không đổi `Code`** sau khi đã phát hành, vì frontend đang dựa vào nó.
6. **Bảo mật khi đăng nhập:** trả **cùng một lỗi** (`Auth.InvalidCredentials`) cho cả "email không tồn tại" và "sai mật khẩu". Nếu tách thành 2 lỗi, kẻ tấn công có thể dò ra email nào đã đăng ký (user enumeration).

---

## 8. Tóm tắt

| | `Error` | `Result` |
| --- | --- | --- |
| Là gì | Mô tả một lỗi | Kết quả: thành công hoặc thất bại |
| Gồm | `Code`, `Message`, `Type` | `IsSuccess`, `IsFailure`, `Error`, `Value` |
| Dùng khi | Có lỗi **đoán trước được** | Hàm có thể thất bại theo cách đoán trước được |
| Không dùng khi | Sự cố hạ tầng, bug | Như bên trái, dùng exception |
| Bảo vệ | Không sửa được, so sánh theo giá trị | Không tạo được trạng thái vô lý, không đọc được `Value` khi thất bại |
