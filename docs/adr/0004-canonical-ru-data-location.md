---
status: accepted
date: 2026-08-13
---

# Store current Ru business data under LocalAppData

The canonical Windows location for settings, sessions, history, and activity
is `%LOCALAPPDATA%\Ru`. Migration from `~/.minigpt` must copy, validate, hash,
atomically activate, and prove readability before legacy data or credentials
are removed.
