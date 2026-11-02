# NotificationService — REST & Realtime API Contracts Specification (v1)

## 1. Overview & Protocol Architecture

This document defines the official API specification for `NotificationService` (v1). It specifies:
- In-App Inbox REST endpoints for notifications and user preferences.
- SignalR Realtime Hub for inbox invalidation.
- Inter-service internal contact resolution API required from `AuthService`.
- Security constraints, authentication tokens, pagination snapshot semantics, and error handling.

---

## 2. Security & Authentication Architecture

### 2.1. Human JWT vs. Service JWT Distinction
- **Human JWT (`sub` = User GUID)**:
  - Emitted by `AuthService` upon human user login.
  - Required for all personal inbox endpoints (`/api/v1/notifications/**`) and the SignalR Hub (`/api/v1/notifications/hubs/inbox`).
  - Strict IDOR Protection: NotificationService extracts `userId = Guid.Parse(jwt.Claims["sub"])` and filters all queries and mutations to match `recipient_user_id == userId`. No user can query or modify another user's notifications.
- **Service JWT (`u: "svc"` or `token_use = svc`)**:
  - Emitted by `AuthService` for machine-to-machine integration.
  - **STRICTLY PROHIBITED** from calling human inbox endpoints or connecting to the inbox SignalR Hub.
  - If a token containing claim `u: "svc"` attempts to access inbox APIs, NotificationService immediately returns `HTTP 403 Forbidden` (`ServiceTokensForbidden`).

### 2.2. Token Transmission Rules
- **REST APIs**: Transmitted via HTTP header `Authorization: Bearer <token>`.
- **SignalR Hub**: Transmitted via HTTP header `Authorization: Bearer <token>` during the HTTP negotiate and WebSocket upgrade requests, or via HTTP-only cookie `accessToken`.
- **PROHIBITION OF QUERY PARAMETERS**: Passing access tokens via query string (`?access_token=...`) is **STRICTLY FORBIDDEN**. Any request containing access tokens in the URL is rejected to prevent exposure in reverse proxy access logs, browser history, and diagnostic dumps.

### 2.3. Gateway Boundary & Internal Endpoint Exclusion
- `Gateway` (`ServiceOnlyRouteBlockingMiddleware`) inspects incoming requests and blocks any route containing `/internal/` with `HTTP 403 Forbidden`.
- Internal service-to-service endpoints (`POST /api/v1/auth/internal/users/notification-contacts:batch`) are NOT routed through Gateway and are authenticated directly over the internal network using mutual API key validation (`X-Internal-Api-Key`).

---

## 3. In-App Inbox REST Endpoints

### 3.1. `GET /api/v1/notifications` — List User Notifications

Retrieves a paginated list of notifications for the authenticated user with stable ordering and optional status/category filters.

- **Method**: `GET`
- **Path**: `/api/v1/notifications`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Query Parameters**:

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `page` | Integer | No | `1` | Page number (1-based, minimum 1). |
| `pageSize` | Integer | No | `20` | Number of items per page (minimum 1, maximum 100). |
| `status` | String | No | `"all"` | Filter status: `"all"` or `"unread"`. |
| `category` | String | No | `null` | Filter by category: `"ORDER"`, `"PAYMENT"`, `"HANDOVER"`, `"RETURN"`. |

- **Pagination Semantics**:
  - Database query uses strict deterministic ordering: `ORDER BY created_at DESC, id DESC`.
  - This is a deterministic database ordering, not a cross-request transactional snapshot. New notifications created between requests can cause items to shift pages.
  - Executed using `.AsNoTracking()` with `.Select()` projection.
  - Total item count and total pages are calculated in a single round-trip or parallel aggregate count.
- **Response 200 OK**:
```json
{
  "items": [
    {
      "id": "e4b5c6d7-e8f9-4a0b-1c2d-3e4f5a6b7c8d",
      "notificationType": "ORDER_CREATED",
      "category": "ORDER",
      "severity": "ACTION_REQUIRED",
      "title": "Đơn thuê #RNT-20261102-0001 đã được tạo",
      "body": "Đơn thuê RNT-20261102-0001 đang chờ thanh toán 1,500,000 VND trước hạn 10:30 02/11/2026.",
      "resourceType": "RentalOrder",
      "resourceId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
      "deepLinkUrl": "/renter/orders/a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
      "isActionable": true,
      "actionUrl": "/checkout/payment?orderId=a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
      "isRead": false,
      "readAt": null,
      "occurredAt": "2026-11-02T03:00:00Z",
      "createdAt": "2026-11-02T03:00:01Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1,
  "hasPrevious": false,
  "hasNext": false
}
```

- **Error Responses**:
  - `400 Bad Request`: `InvalidPaginationParameters` (`page < 1` or `pageSize > 100`) or `InvalidCategory`.
  - `401 Unauthorized`: Missing or expired Bearer token.
  - `403 Forbidden`: Token is a service token (`u: "svc"`).

---

### 3.2. `GET /api/v1/notifications/unread-count` — Get Unread Count

Returns the exact count of unread notifications for the authenticated user.

- **Method**: `GET`
- **Path**: `/api/v1/notifications/unread-count`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Query Optimization**: Evaluates directly via `SELECT COUNT(1) FROM notification.notifications WHERE recipient_user_id = @UserId AND read_at IS NULL` using index `ix_notifications_recipient_unread`.

- **Response 200 OK**:
```json
{
  "unreadCount": 3
}
```

---

### 3.3. `PATCH /api/v1/notifications/{id}/read` — Mark Single Notification As Read

Marks an unread notification belonging to the authenticated user as read.

- **Method**: `PATCH`
- **Path**: `/api/v1/notifications/{id}/read`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Idempotency**: If the notification is already marked read, returns `200 OK` with the existing `readAt` timestamp.
- **Resource Ownership Invariant**: If `{id}` does not exist or belongs to another user, returns `404 Not Found` (preventing IDOR reconnaissance).

- **Response 200 OK**:
```json
{
  "id": "e4b5c6d7-e8f9-4a0b-1c2d-3e4f5a6b7c8d",
  "isRead": true,
  "readAt": "2026-11-02T03:10:00Z"
}
```

- **Error Responses**:
  - `400 Bad Request`: `{id}` is not a valid GUID.
  - `404 Not Found`: Notification not found or not owned by caller.

---

### 3.4. `PATCH /api/v1/notifications/read-all` — Mark All As Read With Server Cutoff

Marks all unread notifications as read up to the server's authoritative cutoff timestamp.

- **Method**: `PATCH`
- **Path**: `/api/v1/notifications/read-all`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Request Body**: None / Empty JSON object `{}`. The client cannot supply a cutoff timestamp. The server determines `cutoffUtc` via its authoritative UTC clock (`TimeProvider.GetUtcNow()`).

- **Concurrency & Cutoff Semantics**:
  - Updates only notifications satisfying:
    `recipient_user_id = @UserId AND read_at IS NULL AND created_at <= @CutoffUtc`.
  - Notifications arriving after `@CutoffUtc` remain unread.

- **Response 200 OK**:
```json
{
  "markedCount": 3,
  "cutoffUtc": "2026-11-02T03:15:00Z",
  "readAt": "2026-11-02T03:15:00Z"
}
```
---

### 3.5. `GET /api/v1/notifications/preferences` — Get User Preferences

Returns the user's notification delivery preferences for email across all categories.

- **Method**: `GET`
- **Path**: `/api/v1/notifications/preferences`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Default Policy**: If no database record exists for a category, the system applies safe defaults (`emailEnabled: true` for all categories).

- **Response 200 OK**:
```json
{
  "preferences": [
    {
      "category": "ORDER",
      "emailEnabled": true
    },
    {
      "category": "PAYMENT",
      "emailEnabled": true
    },
    {
      "category": "HANDOVER",
      "emailEnabled": true
    },
    {
      "category": "RETURN",
      "emailEnabled": true
    }
  ]
}
```

---

### 3.6. `PUT /api/v1/notifications/preferences` — Update User Preferences

Updates email delivery preferences for one or more categories.

- **Method**: `PUT`
- **Path**: `/api/v1/notifications/preferences`
- **Authentication**: Bearer Human JWT (`sub = <UserGuid>`)
- **Request Body**:
```json
{
  "preferences": [
    {
      "category": "ORDER",
      "emailEnabled": true
    },
    {
      "category": "PAYMENT",
      "emailEnabled": false
    }
  ]
}
```

- **Validation Rules**:
  - `category` must be one of: `"ORDER"`, `"PAYMENT"`, `"HANDOVER"`, `"RETURN"`.
  - Duplicate categories within the same request are rejected.

- **Response 200 OK**:
```json
{
  "updatedCount": 2,
  "updatedAt": "2026-11-02T03:20:00Z"
}
```

---

## 4. SignalR Realtime Hub Specification

### 4.1. Hub Overview & Transport
- **Path**: `/api/v1/notifications/hubs/inbox`
- **Protocol**: SignalR JSON Hub Protocol over WebSocket (fallback to Long Polling).
- **Authentication**: Bearer token via `Authorization` header or HTTP-only cookie `accessToken`. Service tokens (`u: "svc"`) rejected with `403 Forbidden`.
- **Query String Tokens**: Strictly prohibited (`?access_token=` rejected).

### 4.2. Connection Lifecycle & Group Security
1. Client establishes connection with valid Human JWT.
2. Server validates token and extracts `userId = Guid.Parse(jwt.Claims["sub"])`.
3. Server automatically joins the connection to private group `user:{userId}`.
4. Client has NO ability to join or leave arbitrary groups. Any invocation from client to join other groups is rejected.

### 4.3. Realtime Invalidation Event
The server emits an invalidation event when a notification transaction commits:

- **Method Name**: `NotificationInboxChanged`
- **Payload Schema**:
```json
{
  "notificationId": "e4b5c6d7-e8f9-4a0b-1c2d-3e4f5a6b7c8d",
  "changeType": "CREATED",
  "category": "ORDER",
  "occurredAt": "2026-11-02T03:00:01Z"
}
```

- **Client Consumption Semantics**:
  - `NotificationInboxChanged` is an **invalidation signal**, NOT a full data push.
  - Realtime only requires refetch; clients MUST NOT increment badge counts directly from the event.
  - Upon receipt, client triggers `GET /api/v1/notifications/unread-count` to refresh unread badges.
  - If the user is currently viewing the notifications drop-down or inbox page, client invalidates its local cache and fetches the top page from `GET /api/v1/notifications`.
---

## 5. Internal Service APIs (`AuthService` Prerequisite)

NotificationService requires contact resolution from `AuthService` for transactional email delivery.

### 5.1. `POST /api/v1/auth/internal/users/notification-contacts:batch`

Resolves verified email addresses and active status for a batch of User GUIDs.

- **Method**: `POST`
- **Path**: `/api/v1/auth/internal/users/notification-contacts:batch`
- **Caller**: `NotificationService` (EmailDeliveryWorker)
- **Target**: `AuthService`
- **Authentication**: `X-Internal-Api-Key: <InternalAuthentication:ApiKey>`
- **Gateway Enforcement**: Blocked by Gateway (`403 Forbidden` if invoked via public Gateway).
- **Request Body**:
```json
{
  "userIds": [
    "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
    "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b"
  ]
}
```
- **Constraints**: Minimum 1 ID, maximum 100 IDs per batch.

- **Response 200 OK**:
```json
{
  "contacts": [
    {
      "userId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
      "email": "renter@example.com",
      "isActive": true,
      "isEmailVerified": true
    },
    {
      "userId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b",
      "email": "supplier@example.com",
      "isActive": true,
      "isEmailVerified": true
    }
  ]
}
```

- **Privacy & Eligibility Invariants**:
  - Endpoint returns ONLY `userId`, `email`, `isActive`, and `isEmailVerified`.
  - Passwords, hashes, phone numbers, addresses, and identity documents (CCCD) are NEVER returned.
  - If `isActive == false` or `isEmailVerified == false`, NotificationService email worker marks the delivery job as `Suppressed` and does not send email.

---

## 6. ProblemDetails & Standard Error Catalog

All API error responses adhere to RFC 7807 / RFC 9110 `ProblemDetails` format:

```json
{
  "type": "https://errors.dprental.vn/notifications/INVALID_PAGINATION",
  "title": "Invalid Pagination Parameters",
  "status": 400,
  "detail": "Page must be greater than or equal to 1, and PageSize must not exceed 100.",
  "instance": "/api/v1/notifications?page=0&pageSize=200",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

### Error Catalog Table:

| Error Code | HTTP Status | Detail Description |
|---|---|---|
| `UNAUTHORIZED` | 401 | Missing, malformed, or expired Bearer JWT. |
| `SERVICE_TOKEN_FORBIDDEN` | 403 | Service tokens (`u: "svc"`) cannot access human inbox APIs. |
| `FORBIDDEN` | 403 | Caller does not have permission to perform this action. |
| `NOTIFICATION_NOT_FOUND` | 404 | Notification ID does not exist or is not owned by the caller. |
| `INVALID_PAGINATION` | 400 | `page < 1` or `pageSize < 1` or `pageSize > 100`. |
| `INVALID_CATEGORY` | 400 | Category is not one of: `ORDER`, `PAYMENT`, `HANDOVER`, `RETURN`. |
| `INVALID_CUTOFF_TIMESTAMP` | 400 | `cutoffUtc` is invalid or refers to a future timestamp. |
| `INTERNAL_API_BLOCKED` | 403 | Endpoint is internal service-only and cannot be accessed via Gateway. |
