# Analytics Dashboard API

Các route `GET /api/v1/analytics/{overview,revenue,orders,equipment,suppliers}` yêu cầu JWT của AuthService có role `adm` hoặc `adp` (hoặc tên role đầy đủ). Service token (`u=svc`) bị từ chối. `from` và `to` là ngày lịch Việt Nam, tính cả hai đầu, tối đa 366 ngày. Mọi amount của MVP là số nguyên VND.

Nguồn dữ liệu: `rental.events.v1` cho vòng đời đơn, item, thiết bị và supplier; `payment.analytics.v1` cho settlement, refund và payout. Consumer ghi projection vào PostgreSQL rồi mới commit Kafka offset. Payment event bắt buộc có header `x-dprental-proof` với audience `analytics-service`; cấu hình public keys qua `Kafka:ProofPublicKeys:{keyId}`. Không lưu raw message, thông tin giao hàng hoặc tài khoản ngân hàng.

Cấu hình cần có khi chạy: `ConnectionStrings:Postgres`, `Authentication:Jwt:{Issuer,Audience,JwksUri}`, và `Kafka:{Enabled,BootstrapServers,GroupId,ProofIssuer,ProofEnvironment,ProofPublicKeys}`. `Kafka:Enabled` mặc định `false`; chỉ bật khi broker và proof keys đã sẵn sàng. Có thể cấu hình SASL/TLS qua `Kafka:SecurityProtocol`, `Kafka:SaslMechanism`, `Kafka:SaslUsername`, `Kafka:SaslPassword` và `Kafka:SslCaLocation`.

Khi chạy local, đặt `ConnectionStrings:Postgres` trong .NET User Secrets của `AnalyticsService/src/Api/AnalyticsService.Api.csproj`, giống các service khác. `.env` chỉ được Docker Compose dùng để tạo PostgreSQL; API không tự nạp `.env`. Nếu AuthService chạy local, đặt thêm `Authentication:Jwt:JwksUri` trong User Secrets trỏ đến JWKS local của AuthService.

Sau khi cấu hình PostgreSQL, áp dụng migration bằng `dotnet ef database update --project AnalyticsService/src/Domain/AnalyticsService.Domain.csproj --startup-project AnalyticsService/src/Api/AnalyticsService.Api.csproj`. Việc replay cùng event được chống trùng theo `messageId` (Rental) hoặc khóa nghiệp vụ (Payment).

Một số KPI vẫn phụ thuộc producer: Payment cần phát `RefundRequestedFinancial` lên `payment.analytics.v1` để `pendingRefund` đầy đủ; Payment cũng cần gửi phần tiền thuê riêng trong refund để tính `GrossRentalValueRefunded`. Rental cần phát `RentalOrderRejected` và một số đường `Expired` còn thiếu. Analytics đã có consumer cho các event được nêu trong contract.

Overview tính `pendingRefund` bằng tổng nghĩa vụ `RefundRequestedFinancial` trừ tiền `RefundCompletedFinancial` theo `refundRef`, không thấp hơn 0. Giá trị chỉ đầy đủ khi Payment phát đủ event yêu cầu hoàn. `active` là số đơn còn hoạt động tại cuối ngày `to` theo giờ Việt Nam. Revenue timeline dùng ngày thanh toán cho GMV, ngày chốt settlement cho platform fee, và ngày hoàn tất cho refund/payout. Tổng tiền cọc không được đưa vào doanh thu nền tảng.
