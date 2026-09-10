# Skills

Bundled declarative `SkillManifest` examples (`skill.json` + `instructions.md`).
They are not auto-loaded from the repo root; a skill executes only when its
validated ID is included in the live session grant (`/skills`, `/skill`).

- `research.and_note` — runnable: all step `tool_id` values (`read`,
  `write`, `web_search`, `web_fetch`) are advertised bindings. Step `name` values
  (`search`, `fetch`, `save_note`) are local aliases only.
- `personal.create-calendar-event` — example only: requires `calendar.*`,
  which remains experimental/unwired (see `docs/operations/agent-tools.md`
  capability table), so it fails closed at validation and never executes.

Markdown `SKILL.md` prompt guidance under workspace skill roots is a separate
format and never becomes an executable `SkillManifest`.
