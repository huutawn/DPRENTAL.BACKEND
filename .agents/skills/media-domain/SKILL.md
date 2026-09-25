---
name: media-domain
description: Change MediaService data models, repository or object-storage adapters, PostgreSQL schema, and migrations inside the fixed Domain project.
---

# MediaService Domain and infrastructure work

Read `AGENTS.md`, the affected product document, existing Domain contracts and adapters, and their Application callers. Keep persistence and MinIO code in the existing Domain project; do not introduce a fifth production project.

## Storage boundary

- Keep media models and Application-facing contracts independent of EF Core, SQL, MinIO SDK, and HTTP types. Place concrete adapters, DbContext, mappings, and migrations in a distinct infrastructure area within Domain.
- Repository methods should express media intent, not expose arbitrary tables or queries. Map provider failures into contextual, safe errors; preserve cancellation and dispose streams and database resources.
- Enforce database integrity and concurrency at storage boundaries, while leaving authorization, visibility, lifecycle policy, and semantic validation to Application.
- Keep transaction scope explicit and short. Never retain a database transaction during MinIO calls. For operations spanning PostgreSQL and MinIO, expose enough outcome information for Application to retry or compensate safely.
- Include migration and compatibility handling for schema changes. Check existing data and old/new application coexistence before dropping or renaming persisted fields. Keep bucket objects private unless the product contract explicitly requires another policy.

## Finish the change

Test real persistence or object-storage behavior at the relevant integration boundary, including conflict, missing object, unavailable dependency, and cancellation cases. Check migration application, model mapping, and all contract callers; do not treat an in-memory repository test as proof of PostgreSQL behavior.
