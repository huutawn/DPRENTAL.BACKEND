# DPR-83 — Nhật ký làm việc

## Phạm vi chính thức (theo ticket)

Mục tiêu: AuthService trở thành nguồn sự thật cho user, organization, membership, role, permission và service identity. 6 khối:

| Khối | Yêu cầu ticket | Trạng thái sau phiên này |
| --- | --- | --- |
| Organization & Membership | `Organization`; user thuộc organization; quản lý membership User↔Organization; loại Platform/Supplier/Renter | Entity + migration xong (`Organization`, `OrganizationMembership`). **Chưa có** endpoint quản lý (mời, đổi role trong org, rời org) |
| RBAC | `Permission`, `RolePermission`; Role map tập permission; assign/revoke; seed tối thiểu | **Xong đầy đủ**: entity, migration, endpoint assign/revoke, seed role `admin`/`customer` + 2 permission |
| Authorization | Policy enforce theo permission/role; không hard-code role trong Controller | **Xong** — 1 `AuthorizationPolicy` cho mỗi permission trong `Permissions.All`, gắn `[Authorize(Policy = ...)]` lên endpoint |
| JWT Identity Contract | Chuẩn hóa claim: `sub`, organization context, role, permission/scope, issuer, audience, expiration | **Xong** — thêm claim `perm`, `org`, `token_use`, `scope`; audience giờ theo từng request (không cố định) |
| Service Identity | Phân biệt user vs machine identity; không dùng header tự khai | **Xong** — endpoint `POST /api/v1/auth/service-token`, xác thực bằng client secret hash, không dùng header tự khai |
| Persistence + Migration | Schema/migration cho Organization, Membership, Permission, RolePermission | **Xong** cho toàn bộ entity trên, cộng 1 migration seed data |

**Còn thiếu để coi DPR-83 xong hẳn:** endpoint quản lý Organization Membership (mời user vào org, đổi role trong org, rời org) — Organization/Membership mới có persistence, chưa có nghiệp vụ nào gọi tới.

## Sự cố mất code và cách xử lý (đầu phiên)

**Chuyện gì xảy ra:** Ở một lượt làm việc trước trong cùng phiên, đã tạo xong entity cho RBAC (`Permission`, `RolePermission`, `ServicePrincipal`, `ServicePrincipalScope`) và Organization/Membership (`Organization`, `OrganizationMembership`), cộng migration cho cả hai, đã áp thành công vào Postgres local. Nhưng **chưa từng `git commit`** — toàn bộ nằm ở working tree.

Giữa chừng, nhánh `auth/feature/DPR-83-rbac-service-identity` bị `git reset --hard` về điểm trước khi nhánh tách ra, rồi `pull` lại `auth/develop` mới nhất (bằng chứng: `git reflog` có dòng `reset: moving to ef030db`). Việc này xóa sạch mọi thay đổi working tree chưa commit.

**Vì sao không mất trắng:** Postgres (chạy Docker) là tiến trình độc lập, không bị git đụng tới — schema của các entity đã mất vẫn còn nguyên trong volume Docker dù không còn migration `.cs` tương ứng.

**Cách khôi phục:**
1. Soi `git log`/`git reflog`/`git status` để xác nhận thứ gì còn, thứ gì mất; phát hiện nhánh đã fast-forward theo `auth/develop`, có nhiều thay đổi mới từ team (login xong, đổi sang HttpOnly cookie, thêm field khóa tài khoản, có `AuditLog.cs` scaffold dở của bạn Nguyen Bao — chưa wire vào `AuthDbContext`, không đụng tới).
2. Viết lại 4 entity RBAC/Service Identity từ nội dung đã biết trước đó.
3. Với `Organization`/`OrganizationMembership` — soi trực tiếp schema thật trong Postgres (`\d auth."Organizations"`, `\d auth."OrganizationMemberships"`) để dựng lại entity khớp 100% với DB, thay vì đoán lại.
4. Migration mới sinh ra có ID khác migration cũ đã ghi trong `__EFMigrationsHistory` → áp bị lỗi "relation already exists". Giải quyết bằng cách xóa hẳn volume Postgres local (`docker compose down -v`) rồi tạo lại + `database update` từ đầu — an toàn vì chỉ có dữ liệu test.
5. **Commit ngay** sau mỗi bước có ý nghĩa, không gom việc lớn rồi mới commit — để không lặp lại sự cố.

**Sự cố phụ trong lúc dựng lại:** khi build lần đầu sau khi restore `Organization.cs`, phát hiện 2 file rác `Domain/Enum/OrganizationType.cs` và `MembershipStatus.cs` với nội dung vỡ (`namespace Dptental.Auth`, cú pháp không hợp lệ) — nhiều khả năng do Visual Studio (đang mở solution) tự chạy refactor "move type to file" giữa chừng rồi bị ngắt. Đã xóa 2 file rác đó; enum vẫn định nghĩa inline trong entity gốc như thiết kế ban đầu, không mất gì.

## Việc đã làm — theo từng commit

### `350225d` — Dựng lại entity persistence
- `Permission.cs`, `RolePermission.cs` (RBAC), `ServicePrincipal.cs`, `ServicePrincipalScope.cs` (Service Identity), `Organization.cs`, `OrganizationMembership.cs` (Organization & Membership)
- Wire 6 `DbSet` mới vào `AuthDbContext.cs`; navigation `RolePermissions` trên `Role.cs`, `OrganizationMemberships` trên `User.cs`
- 2 migration: `AddRbacAndServiceIdentity`, `AddOrganizationAndMembershipSupport`
- Xử lý lỗi analyzer CA1711 (hậu tố "Permission" bị .NET coi là CAS permission cũ) và CA1861 (mảng const trong migration tool tự sinh) bằng `[SuppressMessage]`, theo pattern có sẵn trong `Common/Error.cs`
- **Không commit** `docker-compose.yml` (đổi cổng Postgres 5432→5433) — workaround riêng cho máy này (có Postgres 18 cài sẵn chiếm cổng 5432), commit sẽ phá cấu hình chung của team

### `0cba5ad` — Claim `perm` và `org` trong JWT
- `AuthSessionPrincipal`/`AccessTokenRequest` thêm `Permissions` (gộp từ `UserRole` toàn cục và `OrganizationMembership` đang active) và `OrganizationIds` (chỉ membership active)
- `AccessTokenRequest` thêm `Audience`/`TokenUse`/`Scopes` tùy chọn — để dùng chung cho cả user token lẫn service token thay vì viết 2 issuer riêng
- `RsaAccessTokenIssuer` phát thêm claim `perm`, `org`, `token_use`, `scope`; audience giờ lấy từ request nếu có, không thì dùng mặc định
- `EfAuthSessionRepository` sửa cả 2 nơi tạo principal (login, refresh) để include đủ `RolePermissions`/`OrganizationMemberships`

### `665e1f4` — Authorization, assign/revoke, service-token
- `Permissions.cs`: hằng số permission (`roles.assign`, `roles.revoke`)
- `AddJwtBearerSecurity`: đăng ký 1 `AuthorizationPolicy` cho mỗi permission trong `Permissions.All` — thêm permission mới tự có policy, không cần nhớ đăng ký riêng
- `IRoleRepository`/`EfRoleRepository` + `RoleAssignmentService`: assign/revoke có 2 lớp chặn — actor không tự thao tác lên chính mình, actor không thể gán/thu role mang permission mà actor không có (chặn tự nâng quyền)
- `RolesController`: `POST /api/v1/roles/assign`, `/revoke`, actor id lấy từ `User.Identity.Name` (đã map sẵn thành claim `sub`)
- `IServicePrincipalRepository`/`EfServicePrincipalRepository` + `ServiceIdentityService`: xác thực client secret bằng cách hash lại rồi so `FixedTimeEquals` — tái dùng `RefreshTokenUtility.ComputeHash` có sẵn thay vì viết hàm hash riêng; sai client_id/sai secret/principal bị khóa đều trả cùng 1 lỗi để không lộ lý do
- `POST /api/v1/auth/service-token` trên `AuthController`: audience theo từng request, `token_use=service`, có `scope`, không có refresh token (service tự xin lại bằng secret khi hết hạn)

### `86570c4` — Seed data + verify end-to-end
- Migration tay `SeedRolesAndPermissions`: role `admin`/`customer`, permission `roles.assign`/`roles.revoke`, gán cả hai cho `admin`. Không dùng `OnModelCreating().HasData()` vì codebase chưa có `OnModelCreating` override nào và seed data này còn có thể đổi — migration tay dễ sửa/gỡ hơn là bám vào model snapshot vĩnh viễn
- **Đã test thật trên instance đang chạy** (không chỉ build/test xanh): đăng ký user → gán role `admin` thẳng trong Postgres → login → xác nhận token thật sự mang `perm: ["roles.assign","roles.revoke"]` → gọi `/api/v1/roles/assign`: thành công (204), tự gán bị chặn (403 `Role.CannotSelfAssign`), thiếu cookie (401), actor chỉ có role `customer` (không có `perm`) bị chặn ngay ở tầng policy ASP.NET Core trước khi vào code (403). Cũng test `/api/v1/auth/service-token` với 1 `ServicePrincipal` seed tay: đúng secret+scope → token hợp lệ; sai secret → 401; đúng secret nhưng scope chưa cấp → 403 `InvalidScope` (khác `InvalidClient`)

## Việc cần làm tiếp (ngoài phạm vi phiên này)

- Endpoint quản lý Organization Membership: mời user vào organization, đổi role trong phạm vi organization, rời/loại khỏi organization
- Quyết định 3 câu hỏi mở còn treo: role mặc định khi đăng ký có phải `customer`? Danh sách service/scope đầu tiên (Rental, Inventory...) ai định nghĩa? Đã chốt path `service-token` là `/api/v1/auth/service-token`
- Cập nhật `docs/README.md` chính thức của service: phần claim JWT mới (`perm`, `org`, `token_use`, `scope`), cách cấp `ServicePrincipal`/secret cho service khác, cách seed role/permission cho môi trường khác local
