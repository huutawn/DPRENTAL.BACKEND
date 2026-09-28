# 5. Quản lý trạng thái công khai

## 5.1. User Story

Là một người dùng có quyền quản lý dữ liệu nghiệp vụ, tôi muốn công khai một Media phù hợp để Media có thể được sử dụng trong các nội dung được phép hiển thị công khai.

Là một người dùng có quyền quản lý dữ liệu nghiệp vụ, tôi muốn chuyển Media đã công khai về trạng thái riêng tư khi Media không còn được phép hiển thị công khai.

Là một Business Service, tôi muốn kiểm soát việc thay đổi trạng thái private / public của Media dựa trên nghiệp vụ, để người dùng không thể tự ý công khai tài liệu riêng tư.

## 5.2. Đầu vào

| **STT** | **Tên trường**  | **Kiểu dữ liệu** | **Bắt buộc** | **Mô tả / ràng buộc**                            |
|---------|-----------------|------------------|--------------|--------------------------------------------------|
| 1       | mediaId         | UUID/ULID        | Có           | Định danh Media cần thay đổi trạng thái hiển thị |
| 2       | visibility      | Enum             | Có           | Trạng thái mong muốn: private hoặc public        |

**Hệ thống tự xác định:**

| Thông tin           | Nguồn                  | Mô tả                                                     |
|---------------------|------------------------|-----------------------------------------------------------|
| Người dùng hiện tại | Authentication Context | Business Service dùng để kiểm tra quyền publish/unpublish |
| Service identity    | Service token          | MediaService xác thực service và scope `media.publish`   |
| status              | Media Metadata         | Chỉ Media ở trạng thái ready mới được thay đổi visibility |

**Ví dụ contract:**

```text
{
"visibility": "public"
}
```

## 5.3. Đầu ra

Chuyển sang Public thành công

Hệ thống:

- cập nhật visibility = public;

- giữ nguyên mediaId;

- không thay đổi nội dung file;

- không tạo Media mới;

- cho phép Media được truy cập theo cơ chế Public Media của hệ thống;

- hiển thị thông báo:

> “Công khai tệp thành công.”

Chuyển về Private thành công

Hệ thống:

- cập nhật visibility = private;

- giữ nguyên mediaId;

- ngừng cấp quyền truy cập public mới;

- Media tiếp tục tồn tại và có thể được truy cập theo quyền private;

- hiển thị thông báo:

> “Chuyển tệp về riêng tư thành công.”

Media không hợp lệ

Nếu Media:

- không tồn tại;

- chưa ready;

- đã rejected;

- đã deleted;

thì không thay đổi visibility.

## 5.4. Tiêu chí chấp nhận

Hệ thống hỗ trợ hai trạng thái hiển thị:

- private;

- public.

Media mới được tạo mặc định là private, trừ khi policy nghiệp vụ quy định khác.

Chỉ Media ở trạng thái ready mới được phép thay đổi visibility.

Người dùng chỉ được thay đổi visibility khi có quyền trên đối tượng nghiệp vụ liên quan.

Việc thay đổi visibility phải được thực hiện thông qua Business Service sở hữu nghiệp vụ.

Browser không được tự quyết định Media được Public chỉ bằng việc gọi trực tiếp MediaService.

MediaService phải xác thực service identity và scope `media.publish`. Business Service phải xác nhận entity đang tham chiếu mediaId trước khi yêu cầu đổi visibility.

Business Service quyết định Media được công khai theo quyền người dùng và trạng thái entity. MediaService chỉ cho phép service token có scope `media.publish` thay đổi visibility của Media `Ready`.

Chuyển private → public không được tạo mediaId mới.

Chuyển public → private không được tạo mediaId mới.

Thay đổi visibility không được thay đổi binary file.

Gửi lại yêu cầu public cho Media đã Public phải không gây lỗi ngoài ý muốn.

Gửi lại yêu cầu private cho Media đã Private phải không gây lỗi ngoài ý muốn.

Sau khi chuyển về Private, hệ thống không được cấp public access mới.

Quyền truy cập tạm thời đã cấp trước đó có thể tiếp tục tồn tại cho tới khi hết hạn nếu hệ thống chưa hỗ trợ revoke tức thời.

Media deleted không được thay đổi visibility.

## 5.5. Nguyên tắc hoạt động

### **a. Nguyên tắc mặc định**

Media được tạo với:

visibility = private

làm trạng thái an toàn mặc định.

Media không tự trở thành Public chỉ vì:

- upload thành công;

- status = ready;

- được sử dụng trong entity nghiệp vụ.

ready và public là hai khái niệm khác nhau.

status = ready

có nghĩa:

> File hợp lệ và có thể sử dụng.

visibility = public

có nghĩa:

> File được phép truy cập theo chính sách Public Media.

### **b. Nguyên tắc quyết định Public**

Business Service là thành phần quyết định **về mặt nghiệp vụ** Media có được công khai hay không.

Ví dụ với ảnh thiết bị:

Equipment Draft

→ imageMediaId = M1

→ visibility = private

Khi thiết bị được publish:

Equipment Published

→ CatalogService yêu cầu

→ M1 visibility = public

MediaService không tự quyết định:

> Equipment này đã đủ điều kiện publish chưa.

### **c. Quyết định công khai**

Business Service quyết định file được công khai theo nghiệp vụ và quan hệ entity–media. MediaService không lưu purpose.

### **d. Nguyên tắc chuyển Private → Public**

Trước khi chuyển Public, hệ thống kiểm tra:

- Media tồn tại;

- status = ready;

- Business Service đã kiểm tra quan hệ entity–media;

- Business Service đã kiểm tra quyền.

Nếu hợp lệ:

```text
visibility:
private
↓
public
```

Không:

- upload lại;

- copy binary;

- đổi MediaId.

### **e. Nguyên tắc chuyển Public → Private**

Khi nghiệp vụ không còn cho phép Media Public:

```text
visibility:
public
↓
private
```

Hệ thống:

- ngừng cấp public access mới;

- tiếp tục giữ Media;

- vẫn cho phép private access nếu người dùng có quyền.

Không cần upload lại file.

### **f. Nguyên tắc quyền truy cập đã cấp**

Việc chuyển một Media về Private không nhất thiết thu hồi ngay tất cả access URL đã được cấp trước đó.

Nếu URL hiện tại có thời gian hiệu lực:

```text
Public/Private change
↓
URL cũ
↓
có thể tiếp tục dùng đến expiresAtUtc
```

Do đó:

- access URL phải có thời gian tồn tại giới hạn;

- sau khi unpublish không cấp URL public mới.

Nếu nghiệp vụ yêu cầu revoke tức thời thì phải có cơ chế riêng và không mặc định coi là MVP.

### **g. Nguyên tắc Public không đồng nghĩa Public Storage**

visibility = public không đồng nghĩa:

Object Storage bucket = public

Metadata và URL Public ổn định vẫn che giấu:

- Bucket;

- ObjectKey;

- Provider URL nội bộ.

MVP dùng chuyển hướng tới URL GET MinIO ký ngắn hạn; URL tạm này có thể chứa Bucket và ObjectKey ngẫu nhiên như ngoại lệ được mô tả trong tài liệu 03. Bucket không được đặt Public.

Public Media được phân phối thông qua cơ chế delivery phù hợp của hệ thống.

Chi tiết CDN/storage delivery không cần đặc tả tại SRS này.

### **h. Nguyên tắc idempotency**

Nếu Media đang:

visibility = public

và nhận yêu cầu:

```text
{
"visibility": "public"
}
```

hệ thống coi request thành công mà không tạo thay đổi dư thừa.

Tương tự:

private → private

không phải lỗi.

## 5.6. Mô tả yêu cầu

### **a. Thay đổi trạng thái hiển thị**

| **STT** | **Tên**             | **Mô tả**                                 | **Control**   | **Giá trị mặc định** | **Bắt buộc**   | **Quy tắc**                                              |
|---------|---------------------|-------------------------------------------|---------------|----------------------|----------------|----------------------------------------------------------|
| 1       | Trạng thái hiển thị | Cho biết Media hiện là Public hay Private | Badge/Status  | private              | —              | Hiển thị theo trạng thái hiện tại                        |
| 2       | Công khai           | Cho phép công khai Media                  | Toggle/Button | Tắt                  | —              | Chỉ hiển thị/kích hoạt khi nghiệp vụ cho phép |
| 3       | Chuyển riêng tư     | Ngừng công khai Media                     | Toggle/Button | —                    | —              | Chỉ áp dụng với Media đang Public                        |
| 4       | Xác nhận            | Xác nhận thay đổi                         | Popup/Button  | —                    | Theo nghiệp vụ | Có thể yêu cầu xác nhận trước khi thay đổi               |
| 5       | Trạng thái Media    | Trạng thái xử lý của file                 | Status        | Tự động              | —              | Chỉ ready được thay đổi visibility                       |

### **b. Quy tắc khi Công khai**

Khi người dùng chọn **Công khai**:

1.  Business Service kiểm tra người dùng có quyền publish entity.

2.  Kiểm tra Media đang thuộc entity đó.

3.  Kiểm tra Media đang ready.

4.  Nếu hợp lệ:

    - cập nhật visibility = public;

    - thông báo thành công.

6.  Nếu không hợp lệ:

    - không thay đổi visibility;

    - hiển thị lỗi tương ứng.

### **c. Quy tắc khi Chuyển về riêng tư**

Khi người dùng chọn **Chuyển về riêng tư**:

1.  Business Service kiểm tra quyền.

2.  Xác định Media thuộc đúng entity.

3.  Yêu cầu MediaService thay đổi trạng thái.

4.  Media chuyển:

public → private

5.  Không cấp public access mới.

6.  Hiển thị thông báo thành công.

### **d. Quy tắc đồng bộ với trạng thái nghiệp vụ**

Trong các trường hợp Business Service có lifecycle riêng, visibility Media phải được điều khiển theo nghiệp vụ đó.

Ví dụ:

```text
Equipment = Draft
→ equipment-image = private
Equipment = Published
→ có thể chuyển equipment-image = public
Equipment = Unpublished
→ equipment-image có thể chuyển lại private
```

### **e. Thông báo**

| **STT** | **Trường hợp**             | **Nội dung đề xuất**                                         |
|---------|----------------------------|--------------------------------------------------------------|
| 1       | Công khai thành công       | Công khai tệp thành công.                                    |
| 2       | Chuyển riêng tư thành công | Chuyển tệp về riêng tư thành công.                           |
| 3       | Media chưa sẵn sàng        | Tệp chưa sẵn sàng để thay đổi trạng thái hiển thị.           |
| 5       | Không có quyền             | Bạn không có quyền thay đổi trạng thái hiển thị của tệp này. |
| 6       | Media không tồn tại        | Không tìm thấy tệp.                                          |
| 7       | Media đã xóa               | Tệp không còn khả dụng.                                      |
| 8       | Lỗi hệ thống               | Không thể cập nhật trạng thái tệp. Vui lòng thử lại.         |

## 5.7. Ràng buộc

- Media mặc định là private nếu không có yêu cầu nghiệp vụ khác.

- Chỉ Media ở trạng thái ready được thay đổi visibility.

- Business Service chịu trách nhiệm kiểm tra quyền publish/unpublish của người dùng.

- Browser không được gọi trực tiếp API đổi visibility bằng user token.

- Business Service phải kiểm tra quan hệ entity–media trước khi đổi visibility.

- Thay đổi visibility không tạo Media mới.

- Thay đổi visibility không thay đổi mediaId.

- Thay đổi visibility không ghi đè hoặc di chuyển binary như một phần bắt buộc của nghiệp vụ.

- Public Media không đồng nghĩa Object Storage được cấu hình public.

- Không expose Bucket, ObjectKey hoặc storage credential trong metadata hay URL Public ổn định; URL GET ký ngắn hạn theo giao thức S3 là ngoại lệ MVP.

- Chuyển Media về Private phải ngừng cấp public access mới.

- Quyền truy cập đã cấp trước khi unpublish có thể còn hiệu lực đến khi hết hạn.

- Media rejected hoặc deleted không được chuyển Public.

- MediaService không tự xác định thời điểm entity nghiệp vụ được publish/unpublish.

- Business Service phải chủ động đồng bộ visibility của Media với lifecycle nghiệp vụ.

## 5.8. Hợp đồng MVP

Business Service có service token scope `media.publish` gọi `PUT /api/v1/media/{mediaId}/visibility` với `{ "visibility": "public" }`; đổi về `private` dùng cùng endpoint. Business Service kiểm tra quan hệ entity–media trước khi gọi; MediaService kiểm tra status và scope và trả `{ "mediaId": "...", "visibility": "Public" }`. Cùng yêu cầu lặp lại là thành công.

Business Service quyết định loại file nào có thể Public. MediaService chỉ hỗ trợ JPEG, PNG, WebP và PDF trong MVP; video chưa được hỗ trợ.
