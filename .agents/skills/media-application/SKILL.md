---
name: media-application
description: Implement MediaService use cases, validation, authorization, state transitions, and orchestration in the Application project.
---

# MediaService Application work

Read `AGENTS.md` and relevant documentation. Trace each caller and the Domain contracts used by the current use case before editing. Keep one use-case path for each behavior.

## Use-case workflow

- Define the input, output, and failure contract around the requested media operation. Reuse an existing DTO or result type when it already represents that contract.
- Validate all semantic inputs at the use-case boundary, even when an Api handler already parsed them. Put the authoritative validator or rule in Application; avoid repeating it in handlers or repositories.
- Decide authorization, owner and purpose constraints, visibility, and allowed state transitions here. Use caller identity passed explicitly from the boundary. Do not trust a client-supplied owner or status merely because it is well formed.
- Coordinate repository and object-storage contracts without importing EF Core, SQL, MinIO SDK, or HTTP types. Keep remote calls outside database transactions; design retry and compensation for partial failures that the product contract permits.
- Propagate cancellation, bound retries, and make operations that may be repeated idempotent or duplicate-aware. Return specific, safe failures that Api can map consistently.

## Finish the change

Test success and invalid input, denied access, forbidden state transitions, repository/storage failure, and retry or concurrency behavior when applicable. Inspect Api callers and Domain adapters so the rule is enforced once and every path observes it.
