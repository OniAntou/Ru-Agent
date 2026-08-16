---
status: accepted
date: 2026-08-13
---

# Store current Ru business data under LocalAppData

The canonical Windows location for settings, sessions, history, and activity
is `%LOCALAPPDATA%\Ru`. Migration from `~/.minigpt` must copy, validate, hash,
atomically activate, and prove readability before legacy data or credentials
are removed.

Executed 2026-08-17: the transactional migration ran with preflight recheck,
SHA-256 verification of all 22 records, store-level readability proofs, and
credential presence moved from service `mini_gpt` to `Ru`. The legacy
directory was retired to `.minigpt.migrated-2026-08-17` and temporary
compatibility code was removed. Evidence:
`docs/validation/plan3-task6-migration-evidence.md`.
