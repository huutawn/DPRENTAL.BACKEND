# ADR-0005: Service Authentication, Internal Contact Endpoint, and Gateway Boundary

## Status
Accepted

## Context
NotificationService interacts with external clients (Frontend via API Gateway) and internal services (`AuthService` for recipient contact resolution).
Key architectural constraints in DPRental:
1. `AuthService` issues asymmetric RSA JWTs. Public keys are exposed via JWKS (`/.well-known/jwks.json`).
2. Gateway (`Ocelot`) acts as the public reverse proxy. It inspects paths, blocks internal service routes, extracts identities, and proxies WebSockets.
3. Notification REST APIs and SignalR Hub are personal to individual human users. A user must only see their own notifications.
4. Internal APIs (such as user contact resolution) must never be accessible from the public internet.
5. In DPRental, service tokens are identified by specific claims (`u: "svc"`), whereas human tokens identify individuals via `sub` (User GUID).

## Decision
1. **Human JWT vs Service Principal Distinction**:
   - **Human Tokens**: Contain claim `sub = <UserGuid>`. Used by browser/mobile clients for inbox access (`/api/v1/notifications/*`) and SignalR Hub (`/api/v1/notifications/hubs/inbox`).
   - **Service Tokens**: Contain claim `u = "svc"` or use `X-Internal-Api-Key` headers for inter-service communication.
   - **Access Control Rule**: Human endpoints (`/api/v1/notifications/**`) strictly reject service tokens (`u = "svc"`). Service tokens CANNOT read or manipulate personal user notification boxes. No administrative bypass is permitted on personal inbox queries.

2. **Auth Minimal Contact Resolution Endpoint**:
   - NotificationService MUST NOT query the `auth.users` PostgreSQL table directly.
   - `AuthService` will provide an authenticated internal batch contact endpoint:
     `POST /api/v1/auth/internal/users/notification-contacts:batch`
   - Request payload:
     ```json
     {
       "userIds": ["550e8400-e29b-41d4-a716-446655440000"]
     }
     ```
   - Maximum batch size: 100 User IDs per request.
   - Response payload:
     ```json
     {
       "contacts": [
         {
           "userId": "550e8400-e29b-41d4-a716-446655440000",
           "email": "customer@example.com",
           "isActive": true,
           "isEmailVerified": true
         }
       ]
     }
     ```
   - **Privacy Invariant**: The endpoint returns ONLY `userId`, `email`, `isActive`, and `isEmailVerified`. It NEVER returns passwords, password hashes, national ID (CCCD), phone numbers, or tokens.
   - **Eligibility Invariant**: Email delivery is suppressed if `isActive == false` or `isEmailVerified == false` (or if email verification is mandatory per security configuration).

3. **Gateway Boundary & Exclusion of Internal Routes**:
   - `Gateway` (`ServiceOnlyRouteBlockingMiddleware`) strictly blocks any route containing `/internal/` with HTTP 403 Forbidden.
   - The route `/api/v1/auth/internal/users/notification-contacts:batch` is NOT registered in Gateway's Ocelot configuration and is blocked if attempted through Gateway.
   - NotificationService and AuthService communicate directly over the internal Docker/Kubernetes container network, authenticated via `X-Internal-Api-Key` header matching `InternalAuthentication:ApiKey`.

4. **SignalR Realtime Hub Security**:
   - Endpoint: `/api/v1/notifications/hubs/inbox`.
   - Token extraction follows DPRental convention: Authorization header takes precedence. If missing on WebSocket upgrade/negotiate, fallback to HTTP-only cookie `accessToken`.
   - **Query-String Tokens Strictly Prohibited**: Token passing via `?access_token=` query string is rejected.
   - On connection, user is automatically joined only to their personal SignalR group `user:{sub}`. Client-side group join requests are rejected.

## Consequences
### Positive
- Cross-service separation of concerns preserved (Auth owns user data; Notification owns messages).
- Zero exposure of internal contact resolution APIs to public internet.
- IDOR immune: inbox queries strictly bind to `JWT.sub`.

### Trade-offs / Risks
- Requires AuthService workspace update to implement `POST /api/v1/auth/internal/users/notification-contacts:batch`. (Tracked in implementation dependencies).
