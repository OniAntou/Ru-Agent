---
status: accepted
date: 2026-11-06
---

# User plugins: bundled skills, instructions, hooks and MCP servers

## Status

Accepted by the owner on 2026-11-06 (user-level only; no plugin slash commands).

## Context

The owner wants users to create, share and install plugins: one folder that bundles
the extension points Ru already has. Today each lives in its own fixed place:

- Skills: `SkillRoots.For/ForUser` (project, user and `%LOCALAPPDATA%\Ru\skills` roots),
  GitHub packages under ADR 0036.
- Instructions: `RuInstructionLoader` (`~/.ru/AGENTS.md`, then workspace files).
- Slash commands: `CustomPromptCommands`, expanded by the CLI only.
- Hooks: `HookConfiguration.Load` over `~/.ru/hooks.json` and, once trusted,
  `<workspace>/.ru/hooks.json`.
- MCP servers: `McpConfiguration`, the user's `~/.ru/mcp.json` only (ADR 0025).

Several documents state that plugins are not loaded or discoverable
(`runtime-behavior.md`, `harness.md`, `coding-agent-capabilities.md`, `CONTEXT.md`).
Hooks and MCP servers run programs as the user, so a plugin is executable content
and needs the boundaries the owner chose: full contents, with consent.

## Decision

1. **A plugin is a folder with `ru-plugin.json`.** The manifest is strict (unknown
   fields rejected): `id` (`^[a-z][a-z0-9-]{0,23}$`, so `<id>-<server>` still fits the
   MCP name limit), `name`, `description`, `version`. Optional parts are found by
   fixed names: `skills/`, `AGENTS.md`, `hooks.json`, `mcp.json`, each with the same
   format and limits as today. Plugins carry no slash commands: reusable prompts are
   skills, which the model loads on demand.

2. **User-level only.** Plugins live in `~/.ru/plugins/<id>` (local and created) and
   `%LOCALAPPDATA%\Ru\plugins\<id>` (managed GitHub installs). There are no project
   plugins: they would reopen workspace-declared MCP servers (ADR 0025) and
   trust-gated workspace hooks. A plugin is off until the user enables it, and the
   enabled set is stored on the Host side, never inside the plugin folder.

3. **Each part joins its existing loader at the lowest precedence:**
   - Skills: enabled plugins' `skills/` are appended after the user roots in
     `SkillRoots.ForUser`. At most 8 enabled plugins may carry skills (the catalog
     caps roots at 16). A plugin skill shadowed by a user or project skill is reported
     in Customize, not dropped silently.
   - Instructions: a new `PromptInstructionOrigin.Plugin`, source id `plugin:<id>`, read
     after `~/.ru/AGENTS.md` and before workspace files; within the existing 64/256 KiB
     limits and block cap.
   - Hooks: plugin hooks are user-level sources, behaving exactly like
     `~/.ru/hooks.json` at all three `HookConfiguration.Load` call sites.
   - MCP: servers merge into `McpServerHost` as `<id>-<server>`. A name the user's own
     file already declares wins; the plugin's server is refused with a diagnostic.
     Per-call approval (ExternalTerminal) is unchanged.

4. **Consent for executable parts.** The executable surface of a plugin is the
   canonical list of hook events, matchers, commands and timeouts, plus each MCP
   server's transport, command, args, cwd, url and environment variable names (never
   values). Enabling a plugin with an executable surface shows that list and records
   its SHA-256. Every load recomputes the digest; on a mismatch the hooks and MCP
   servers of that plugin are suspended (fail closed) and Customize shows "changed,
   approve again". Skills, commands and instructions need only enable, like today's
   files. Instructions and skills remain guidance and never grant permission.

5. **Protection.** Both plugin roots join `ProtectedPathCatalog` (version bump), so the
   agent's structured file tools cannot edit installed plugins in `plan` and
   `standard`; `full_access` stays literally full. The digest is the backstop for
   anything that changes the files outside those tools; the terminal is not confined,
   the same boundary as today.

6. **Creating a plugin.**
   - Customize → Plugins → **New plugin** scaffolds `~/.ru/plugins/<id>` from a short
     form (id, name, description, parts) and opens the folder.
   - A built-in `create-plugin` skill lets the user describe a plugin in chat. Because
     installed roots are protected, the skill writes a draft folder inside the
     workspace (a non-dot folder); the user imports it with **Add plugin from folder**,
     which copies it into `~/.ru/plugins` and goes through enable and consent. A
     plugin the agent wrote is never enabled automatically.

7. **Installing from GitHub.** A `PluginPackageInstaller` reuses ADR 0036's mechanics:
   tree URLs pinned to a full commit, codeload with redirects disabled, size and file
   limits, an exclusive lock, staging and rollback, and per-file hashes in a managed
   manifest. It never runs scripts or installs dependencies. An update that changes the
   executable-surface digest asks for consent again. New routes join
   `public-route-contract.json`.

8. **Lifecycle.** Enabling, disabling, consent and updates apply to new runs; run-local
   skill snapshots (ADR 0032) are unchanged. Disabling a plugin stops its MCP servers.

## Alternatives and consequences

- Project plugins were rejected for v1 (decision 2).
- Safe-only plugins (no hooks or MCP) were the owner's alternative; they chose full
  contents with consent.
- Plugin slash commands were dropped: the owner wants the model, not a composer
  shortcut, to use a plugin's prompts, which skills already provide. `.ru/commands`
  stays a CLI feature.
- Skill ids stay un-namespaced to keep `/skill <id>` and existing preferences
  working; shadowing is reported instead.
- This amends "plugins are not loaded" in `runtime-behavior.md`, `harness.md`,
  `coding-agent-capabilities.md`, `CONTEXT.md` and the CLI README, and extends
  ADR 0025 (a second, user-level MCP source behind consent) and ADR 0036 (a second
  managed package kind).

## Implementation phases

Each phase is built, tested and committed on its own.

1. Manifest and loader; user plugins with skills and instructions; the plugin list
   with enable/disable in Customize → Plugins (Computer use stays); New plugin
   scaffolding.
2. Hooks and MCP with the consent digest and path protection (policy change: full
   matrix).
3. GitHub install and update (new routes: full matrix and route guards).
4. `create-plugin` skill and Add plugin from folder.
