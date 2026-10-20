---
name: media-pr-review
description: Review or prepare a MediaService PR for architecture, ownership, behavior, tests, documentation, and safe integration of concurrent changes.
---

# MediaService PR review

Read `AGENTS.md`, the changed documentation, and the relevant layer skills. Review the actual final diff and test output, not only the PR summary.

## Review sequence

1. Identify the PR's one coherent responsibility, changed behavior, inbound callers, outbound dependencies, and other contributors' ownership of shared files.
2. Confirm the solution still has exactly the four named production projects. Test projects are allowed. Check project and namespace references for the permitted direction and the Api composition-root exception.
3. Trace each behavior from Api to Application to Domain. Confirm Application owns validation, authorization, and state decisions; Api maps HTTP; Domain owns storage mechanics. Search for duplicate routes, validators, adapters, and obsolete paths.
4. Compare the changed behavior with the relevant documentation and tests. Confirm callers, migration compatibility, configuration, and docs are updated together.
5. Inspect tests and command results for failure paths, invalid input, authorization, concurrency, idempotency, and integration boundaries relevant to the change. Do not count skipped or unrun checks as passing.
6. Check the diff for secrets, generated files, debug output, unexplained dependencies, unsafe fallbacks, and accidental overwrites of concurrent work.

Report concrete findings with paths and impact. If no issue is found, state the checks performed and the remaining evidence gaps. A review result is not permission to merge or deploy.
