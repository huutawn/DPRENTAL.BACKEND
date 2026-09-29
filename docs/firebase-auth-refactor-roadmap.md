# Roadmap Refactor Google Login Sang Firebase Auth

## Muc Tieu

Refactor flow dang nhap Google hien tai tu viec AuthService tu xu ly OAuth2 callback/ticket sang flow moi dung Firebase Authentication lam identity provider.

Kien truc sau refactor:

```text
Firebase Auth = xu ly Google sign-in va cap Firebase ID token
AuthService = verify Firebase ID token, dong bo user noi bo, phat accessToken/refreshToken
Next BFF = nhan token noi bo va set HttpOnly cookie tren domain FE
FE = goi Firebase SDK, goi BFF, render auth state
```

## Nhanh Lam Viec

Backend branch:

```bash
git checkout auth/develop
git pull origin auth/develop
git checkout -b auth/feature/DPR-136-refactor-google-login-firebase
```

## Flow Moi

```text
1. User bam Login with Google tren FE.
2. FE dung Firebase SDK de login Google.
3. Firebase tra ve Firebase user va ID token.
4. FE gui ID token ve Next BFF.
5. Next BFF goi AuthService POST /api/v1/auth/firebase/login.
6. AuthService verify ID token bang Firebase Admin SDK.
7. AuthService find/create/link local user.
8. AuthService tao accessToken va refreshToken noi bo.
9. Next BFF set HttpOnly cookies cho FE domain.
10. FE redirect ve app, goi /users/me de lay profile, roles, permissions, organizations.
```

## API Backend Moi

Endpoint:

```http
POST /api/v1/auth/firebase/login
```

Request:

```json
{
  "idToken": "firebase-id-token"
}
```

Response thanh cong:

```json
{
  "accessToken": "...",
  "refreshToken": "..."
}
```

Ghi chu:

- Response token de Next BFF set HttpOnly cookie tren domain FE.
- Khong de FE tu luu token trong localStorage/sessionStorage.
- Neu sau nay BE muon set cookie domain BE thi van khong thay the duoc cookie FE-domain cho SSR khac domain.

## Backend Roadmap

### Phase 1 - Firebase Config

Viec can lam:

- Them package Firebase Admin SDK cho .NET.
- Them config Firebase vao AuthService.
- Ho tro doc credentials an toan tu environment/secret manager.
- Validate config luc startup neu endpoint Firebase login duoc bat.

Config de xuat:

```text
Auth:Firebase:Enabled=true
Auth:Firebase:ProjectId=...
Auth:Firebase:CredentialsJson=...
```

Khong nen commit service account JSON vao repo.

### Phase 2 - Firebase Token Verification Service

Tao service:

```csharp
IFirebaseIdentityService
```

Tra ve model noi bo:

```csharp
FirebaseIdentity(
    string Uid,
    string ProviderSubject,
    string? SignInProvider,
    string Email,
    bool EmailVerified,
    string? DisplayName,
    string? PhotoUrl
)
```

Service can:

- Nhan Firebase ID token.
- Verify token bang Firebase Admin SDK.
- Dam bao token thuoc dung Firebase project.
- Dam bao UID hop le.
- Dam bao email ton tai.
- Dam bao email da verified.
- Map loi verify thanh error ro rang.

Loi de xuat:

```text
Auth.FirebaseTokenMissing
Auth.FirebaseTokenInvalid
Auth.FirebaseEmailMissing
Auth.FirebaseEmailNotVerified
```

### Phase 3 - Local User Link/Create

Sau khi verify token thanh cong:

1. Tim `ExternalLogin` theo:

```text
Provider = Google
ProviderSubject = Firebase claim firebase.identities["google.com"][0], fallback Firebase UID
```

2. Neu da co link:

- Lay user noi bo.
- Neu user inactive thi reject.
- Tao session/token noi bo.

3. Neu chua co link:

- Tim user theo email normalized.
- Neu user ton tai:
  - Neu inactive thi reject.
- Link Google provider subject vao user do.
- Neu user chua ton tai:
  - Tao user moi.
  - FullName lay tu `displayName`, fallback email.
  - Email lay tu Firebase verified email.
  - HasPassword = false.
  - Gan role default `customer`.
  - Tao ExternalLogin provider `Google`.

4. Chong duplicate/race condition:

- Unique index tren provider + subject van can duoc ton trong.
- Neu race khi add external login, reload lai external login theo provider subject.
- Khong tao duplicate user theo cung email.

### Phase 4 - Tao Session/Token Noi Bo

Tai su dung flow hien tai:

```text
CreateSessionAsync
Issue accessToken
Issue refreshToken
```

Response:

```json
{
  "accessToken": "...",
  "refreshToken": "..."
}
```

Audit log de xuat:

```text
Firebase_Login
Firebase_Link
Firebase_Register
```

### Phase 5 - Endpoint Controller

Them endpoint:

```http
POST /api/v1/auth/firebase/login
```

Controller chi nen:

- Validate body co idToken.
- Goi service.
- Set Cache-Control: no-store.
- Tra token pair.

Trang thai hien tai:

- Da them endpoint `POST /api/v1/auth/firebase/login`.
- Controller chi set `Cache-Control: no-store`, goi `IFirebaseLoginService`, va map loi/response.
- `FirebaseLoginService` verify Firebase ID token, chi chap nhan `sign_in_provider = google.com`.
- `FirebaseLoginService` tao/link local user qua `ExternalLoginService`.
- `FirebaseLoginService` tao accessToken/refreshToken noi bo qua `AuthSessionService`.
- Response tra JSON token pair cho Next BFF set HttpOnly cookie tren FE domain.

### Phase 6 - Deprecate Google OAuth Flow Cu

Danh dau deprecated cac endpoint cu:

```http
GET /api/v1/auth/google/login
GET /api/v1/auth/google/callback
POST /api/v1/auth/google/exchange
```

Chua xoa ngay trong phase dau.

Sau khi FE migrate va test on dinh moi can xoa:

- GoogleAuthOptions.
- GoogleAuthenticationExtensions.
- Google callback resolver.
- Google login ticket flow.
- Bang GoogleLoginTickets neu khong con dung.

## Frontend Roadmap

### Phase 1 - Setup Firebase SDK

Them Firebase web config:

```text
NEXT_PUBLIC_FIREBASE_API_KEY
NEXT_PUBLIC_FIREBASE_AUTH_DOMAIN
NEXT_PUBLIC_FIREBASE_PROJECT_ID
NEXT_PUBLIC_FIREBASE_APP_ID
```

Khoi tao Firebase client chi o browser/client component.

### Phase 2 - Firebase Google Login

FE dung Firebase SDK:

```ts
signInWithPopup(auth, new GoogleAuthProvider())
```

Sau khi login thanh cong:

```ts
const idToken = await user.getIdToken()
```

Khong gui email/displayName tu FE lam source of truth. Backend phai lay cac gia tri nay tu ID token da verify.

### Phase 3 - Next BFF Login Endpoint

Them Next route:

```http
POST /api/auth/firebase/login
```

Request tu FE:

```json
{
  "idToken": "firebase-id-token"
}
```

BFF goi AuthService:

```http
POST /api/v1/auth/firebase/login
```

Neu thanh cong, BFF set:

```text
accessToken HttpOnly cookie
refreshToken HttpOnly cookie
```

Cookie settings:

- HttpOnly.
- Secure tren production.
- SameSite phu hop voi domain hien tai.
- Path `/`.

### Phase 4 - Redirect Va Load Current User

Sau khi cookie da set:

```http
GET /api/v1/auth/users/me
```

FE cap nhat auth state:

- user profile
- roles
- permissions
- organizations
- isProfileCompleted

Neu user moi chua hoan thien profile, redirect sang complete profile flow.

### Phase 5 - Remove Google Ticket Flow Cu

Sau khi flow Firebase on dinh:

- Bo nut/call `/api/v1/auth/google/login`.
- Bo route callback xu ly `ticket`.
- Bo call `/api/v1/auth/google/exchange`.
- Bo UI state/error lien quan Google ticket cu.

## Validation Va Security

Backend bat buoc:

- Khong tin email FE gui len.
- Khong tin displayName FE gui len.
- Khong accept ID token rong.
- Verify token bang Firebase Admin SDK.
- Check email verified.
- Check user active.
- Khong tao duplicate user khi email da ton tai.
- Khong luu Firebase ID token vao DB.
- Khong log raw ID token.

Frontend bat buoc:

- Khong luu accessToken/refreshToken trong localStorage.
- Chi gui Firebase ID token ve BFF qua HTTPS.
- Sau login xong, de BFF set HttpOnly cookie.

## Test Backend Can Co

Unit tests:

- Missing idToken -> 400.
- Invalid Firebase token -> 401.
- Email missing -> 400/401 tuy error mapping.
- Email not verified -> 403/401 tuy policy.
- Existing Firebase UID -> login thanh cong.
- Existing email but chua link Firebase -> link thanh cong.
- New Firebase user -> tao user + default role customer.
- Inactive user -> login fail.
- Race condition khi link external login -> khong duplicate user.

Integration tests:

- `POST /api/v1/auth/firebase/login` tra token pair.
- Token pair goi duoc `/api/v1/auth/users/me`.
- User moi co `HasPassword = false`.
- User moi co role default `customer`.

## Test Frontend Can Co

- Bam Login with Google goi Firebase popup.
- Login thanh cong lay duoc Firebase ID token.
- BFF goi AuthService va set cookies.
- SSR doc duoc cookie sau login.
- `/users/me` load thanh cong sau redirect.
- Loi Firebase popup bi cancel hien message dung.
- Loi backend invalid token/inactive account hien message dung.

## Acceptance Criteria

- Login Google moi di qua Firebase Auth, khong di qua backend Google callback.
- AuthService verify Firebase ID token thanh cong.
- AuthService tao/link user noi bo dung.
- AuthService van phat accessToken/refreshToken noi bo nhu login thuong.
- Next BFF set HttpOnly cookie cho FE domain.
- SSR call API duoc sau Firebase login.
- User moi co role `customer`.
- User inactive khong login duoc.
- `/api/v1/auth/users/me` tra dung profile sau login.
- Flow Google OAuth cu duoc danh dau deprecated va khong con duoc FE su dung.

## Thu Tu Lam Khuyen Nghi

```text
1. BE tao Firebase config + verify service.
2. BE tao service login bang Firebase identity.
3. BE tao POST /api/v1/auth/firebase/login.
4. BE viet unit/integration tests.
5. FE setup Firebase SDK.
6. FE tao Next BFF /api/auth/firebase/login.
7. FE doi nut Google login sang Firebase flow.
8. FE test SSR cookie va /users/me.
9. BE/FE deprecate flow Google ticket cu.
10. Sau khi production on dinh moi xoa code Google OAuth cu.
```

## Cau Hoi Can Chot Truoc Khi Code

- Firebase project dev/staging/prod dung project rieng hay chung?
- Backend se doc Firebase credentials bang file path hay JSON env?
- Email chua verified se reject hay cho login?
- Provider trong ExternalLogin se dat la `Firebase` hay `GoogleFirebase`?
- Khi user da co password account cung email, co auto-link Firebase khong?
- Co can migrate external login Google cu sang Firebase UID khong?
- Co giu endpoint Google OAuth cu trong bao lau?
