# MediaService engineering rules

These instructions apply to implementation, refactoring, debugging, and review in this repository. The six numbered documents in `.agents/` describe product behavior. Read the documents relevant to a change before designing it; do not silently invent a conflicting contract. Keep the relevant document, code, and tests consistent in the same PR.

## Fixed solution architecture

The **only production projects** are:

1. `MediaService/src/Common/MediaService.Common.csproj`
2. `MediaService/src/Domain/MediaService.Domain.csproj`
3. `MediaService/src/Application/MediaService.Application.csproj`
4. `MediaService/src/Api/MediaService.Api.csproj`

Do not add, rename, split, or replace a production project. Test projects under `MediaService/tests/` are allowed and do not count toward the four-project limit. Organize new code by media capability inside the existing projects. Do not create a second service, repository, endpoint, DTO, validator, or execution pipeline for behavior an existing component owns.

The dependency direction is `Api -> Application -> Domain -> Common`. `Api` may also reference `Domain` **only in the composition root** to register persistence and object-storage implementations. `Domain` may reference `Common`. No lower layer may reference a higher layer, and no circular project or namespace dependencies are allowed. Keep package versions in `Directory.Packages.props`; add only dependencies needed by a current use case.

| Project | Owns | Must not own |
| --- | --- | --- |
| `Api` | HTTP routing, authentication integration, transport parsing, response/status mapping, middleware, configuration and DI composition | Use-case decisions, media state transitions, authorization policy, duplicate business validation |
| `Application` | Use cases, request DTOs and validators, authorization and ownership decisions, state transitions, orchestration, transaction boundaries | HTTP types, SQL/EF queries, MinIO SDK calls, storage credentials |
| `Domain` | Media data models, repository and storage contracts, PostgreSQL/EF Core and MinIO implementations, migrations, storage error translation | HTTP behavior, client DTOs, authorization or business-policy decisions |
| `Common` | Stable primitives genuinely needed across projects | Media use cases, generic helper dumping grounds, infrastructure adapters |

Domain models and contracts must stay free of HTTP, SQL, and MinIO types even though infrastructure implementations live in the same `Domain` project. Put infrastructure code in a clearly named area of that project. Application depends on the contracts, not concrete adapters. The Api composition root wires adapters to contracts; handlers call Application use cases. A transport or configuration validity check in Api does not replace Application validation of use-case input and business rules. Database constraints provide a final integrity guard, not a second business policy.

## Change ownership and collaboration

- Before changing behavior, inspect the current implementation, its callers, dependencies, tests, and the relevant numbered product document. Use `codebase-memory-mcp` graph tools when available (`search_graph`, `trace_path`, `get_code_snippet`); if unavailable or incomplete, use targeted `rg` and file reads. Do not infer ownership from filenames alone.
- State the capability and files owned by each contributor before parallel work. Coordinate changes to shared files such as `Program.cs`, project files, package versions, migrations, and common contracts. Do not overwrite or revert another contributor's edits; re-read shared files before applying your change.
- Keep each PR tied to one coherent responsibility. A cross-layer PR is appropriate when one use case requires it, but unrelated features, cleanup, and infrastructure changes belong in separate PRs. Integrate all affected layers and callers for the chosen responsibility in that PR.
- Modify or replace an existing implementation before adding another. Remove obsolete code, adapters, configuration, tests, and documentation after replacement. Temporary coexistence requires a documented reason, owner, removal condition, and tests. Avoid `V2`, `New`, and `Legacy` names used to hide parallel paths.
- Keep interfaces small and owned by their consumer. Do not add ceremonial abstractions or placeholder business types. Prefer capability-focused folders and reasonably sized files over global `Services`, `Helpers`, or `Utils` dumping grounds.

## Use-case and boundary rules

- Application is the authoritative place for semantic input validation, permission checks, ownership checks, media state rules, idempotency decisions, and orchestration. Validate every entry to a use case, including calls that do not originate from HTTP. Api may reject malformed HTTP and map Application outcomes to HTTP; it must not repeat business rules.
- Treat the six product documents as behavior specifications. For upload, metadata, access, replacement, visibility, and deletion, implement only the behavior the current task requests. Resolve a material contradiction with the requester before changing the contract.
- Application decides when a transaction is needed; Domain implements persistence and storage operations. Never hold a database transaction open across a slow MinIO or other remote call. Make retryable operations idempotent or detect duplicates; specify cancellation, timeout, and concurrency behavior.
- Domain repositories express media intent and enforce storage integrity. They do not decide who may view, publish, replace, or delete media. Do not let another service mutate MediaService tables or objects through an undocumented path.
- Propagate `CancellationToken` through database, object-storage, and other blocking operations. Dispose connections, streams, responses, and clients correctly. Return contextual errors without exposing credentials or sensitive payloads.
- Validate external configuration at startup. Keep secrets out of source control, logs, traces, URLs, and command-line arguments when a safer channel exists. Do not silently fall back to insecure or stale behavior.

## Evidence required for a PR

- Cover changed behavior with tests at the owning layer. Include invalid input and failure cases; include authorization, concurrency, idempotency, and integration tests when those boundaries are affected. Do not mock the behavior under test or weaken valid assertions to pass.
- Run targeted tests plus solution build, formatting/static checks, and broader regression checks when shared code changes. If a command cannot run, report the exact reason and do not claim it passed.
- Review the full diff for one authoritative path, all callers updated, no references to removed symbols, no accidental secrets or generated output, correct architecture, and synchronized docs.
- A PR description must state the behavior changed, existing code changed or removed, architectural decision, tests and commands actually run, unrun checks with reasons, and remaining risks. Do not describe planned work as implemented.

## Task-specific skills

Read the relevant repository skill before changing that area. Skills explain the workflow; this file remains the authority for mandatory boundaries.

- HTTP routes or client contracts: [media-api](.agents/skills/media-api/SKILL.md)
- Use cases, validation, authorization, or state rules: [media-application](.agents/skills/media-application/SKILL.md)
- PostgreSQL, MinIO, repositories, or migrations: [media-domain](.agents/skills/media-domain/SKILL.md)
- Cross-layer review or PR handoff: [media-pr-review](.agents/skills/media-pr-review/SKILL.md)
