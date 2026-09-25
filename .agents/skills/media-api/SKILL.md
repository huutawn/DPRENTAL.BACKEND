---
name: media-api
description: Change MediaService HTTP routes, client contracts, middleware, or API composition while keeping use-case decisions in Application.
---

# MediaService API work

Read the repository `AGENTS.md` and the numbered product document for the affected capability. Inspect the existing route, request and response types, its Application call, authorization setup, error mapping, and endpoint tests before editing.

## Boundary

- Keep handlers thin: parse transport input, obtain the authenticated identity, call one Application use case, and map its result to an HTTP response.
- Authentication setup and HTTP-specific malformed-input checks belong here. Application performs semantic validation, ownership checks, and authorization so non-HTTP callers cannot bypass them.
- Keep client DTOs and their validators in Application as the existing four-project design requires. Do not create transport copies unless the wire contract genuinely differs; document and test any mapping.
- Centralize status and error mapping. Do not expose storage keys, MinIO endpoints, credentials, exception details, or private metadata in responses.
- Wire Domain adapters in the Api composition root. Keep adapter behavior and queries out of controllers and middleware.

## Finish the change

Test the observable status, body, and authorization behavior for success, malformed input, denied access, and dependency failure where relevant. Check that no new route duplicates an existing use case and that the published contract and product document agree.
