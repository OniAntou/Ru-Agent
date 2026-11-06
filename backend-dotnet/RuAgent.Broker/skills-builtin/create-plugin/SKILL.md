---
name: create-plugin
description: Turn a plugin the user describes into a draft Ru plugin folder (ru-plugin.json, instructions, skills, hooks, MCP servers) in the workspace, ready for Customize → Plugins → Add plugin → Add from folder.
---

# Create a Ru plugin

A Ru plugin is a folder with `ru-plugin.json` and any of `AGENTS.md`, `skills/`, `hooks.json`
and `mcp.json`. Installed plugins live in `~\.ru\plugins`, which your tools cannot write. You
write a **draft** in the workspace; the user imports it, turns it on and approves any commands.

## Steps

1. Ask only what you need: what the plugin should do, and whether it needs to run programs
   (hooks or MCP servers). Prefer instructions and skills; add commands only when asked.
2. Pick an id: lowercase letters, digits and dashes, starting with a letter, at most 24
   characters, no trailing dash (for example `team-rules`).
3. Create the draft folder `ru-plugin-drafts\<id>` at the workspace root. Never use a folder
   whose name starts with a dot, and never write under `~\.ru`.
4. Write `ru-plugin.json`. Only these fields are allowed, and `id` must equal the folder name:

   ```json
   {
     "id": "team-rules",
     "name": "Team rules",
     "description": "Our review checklist and commit style",
     "version": "0.1.0"
   }
   ```

5. Add the parts the user asked for:
   - `AGENTS.md`: instructions Ru reads in every run while the plugin is on. Keep it short and
     specific.
   - `skills\<skill-id>\SKILL.md`: a reusable workflow, loaded on demand. Start with front
     matter holding `name` (the skill id) and a one-line `description` that says when to use it.
     Prefix skill ids with the plugin id so they do not collide.
   - `hooks.json`: commands run before or after tool calls, same format as `~\.ru\hooks.json`:
     `{"hooks":{"PreToolUse":[{"matcher":"bash","command":"%RU_PLUGIN_ROOT%\\scripts\\check.cmd","timeout_seconds":10}]}}`.
     Events are `PreToolUse`, `PostToolUse`, `UserPromptSubmit` and `Stop`. `RU_PLUGIN_ROOT`
     is the installed plugin folder, so put scripts in the plugin and refer to them through it.
   - `mcp.json`: MCP servers, same format as `~\.ru\mcp.json`:
     `{"servers":{"docs":{"command":"node","args":["server.js"],"description":"Team docs"}}}`.
     Servers start in the plugin folder and appear as `<id>-<server>`. Never put a secret in
     the file; reference a stored credential with `{"credential":"<name>"}` in `env`.
6. Keep the draft small: no `.git`, no dependencies or build output, at most 256 files.
7. Tell the user the draft path and what each part does, listing every command a hook or
   server would run. Then tell them to open **Customize → Plugins → Add plugin → Add from
   folder…**, paste the draft folder's full path, and turn the plugin on. Ru shows the
   commands and asks before any of them run.

## Rules

- Never enable, install or approve a plugin yourself, and never claim it is installed.
- Do not write secrets, tokens or personal data into any plugin file.
- If the user later wants a change, edit the draft and ask them to remove the installed copy
  and add the folder again, or edit `~\.ru\plugins\<id>` themselves.
