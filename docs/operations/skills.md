# Skills

A skill is a local folder with `SKILL.md`. Ru advertises its metadata and loads
the workflow when selected. It never automatically runs package scripts or
grants permissions from `allowed-tools` or other metadata.

## Invoke and manage

- In GUI chat, send `/skill verify src/main.cs` to load the `verify` workflow with
  `src/main.cs` as arguments. `$ARGUMENTS` in the rendered workflow receives that
  text. The visible conversation keeps the concise command.
- In terminal chat, ask in an ordinary prompt, for example `Use the verify skill
  to check src/main.cs`. `/skills` and `/skill` are no longer terminal chat
  commands. Model-side loading retains the package's invocation policy.
- In the GUI, **Customize** under New Chat (or `/skills` typed in the composer)
  opens the Skills tab: a searchable list with enable/disable, automatic-selection
  and reload controls. **Use in chat** returns with `/skill <id>` in the composer.
- The GUI can register or remove a local root such as `D:\MySkills` containing
  skill folders for this workspace. This registers
  files in place; it does not copy, download, update or execute a package.

Default root precedence is workspace `skills`, `.ru/skills`, `.agents/skills`,
`RU_SKILLS_ROOT`, `~/.ru/skills`, `~/.agents/skills`, then `%LOCALAPPDATA%/Ru/skills`. Registered roots
follow those defaults. The first matching canonical ID wins. Discovery is
bounded and breadth-first: a skill may be inside grouping folders, up to six
directory levels, 256 visited directories and 4,096 enumerated entries per root.
A folder with `SKILL.md`
is a skill leaf; hidden, dependency and linked directories are skipped. The shared user
library `~/.agents/skills` is discovered automatically across workspaces, without
copying packages or registering a root. Project skills and Ru-specific user
skills take precedence over matching IDs in that shared library.

Local preferences live in `%LOCALAPPDATA%/Ru/skill-preferences.json`, scoped by
workspace. They control guidance availability, not file/terminal permissions.
Local packages can still be edited in place and refreshed from the catalog.
Diagnostic codes identify invalid YAML, unsupported fields, unreadable files,
linked paths, duplicate IDs and scan/metadata limits. A truncated catalog reports
omissions; search narrows the listing rather than silently claiming completeness.

## Explicit GitHub package installation

**Customize → Skills → Add → Install from GitHub** accepts a GitHub tree URL pinned
to a full 40-character commit, for example:

```text
https://github.com/acme/workflows/tree/0123456789abcdef0123456789abcdef01234567/skills/review
```

Choose **Install from GitHub** when there is no package with that ID in Ru's managed
user library. **Update a GitHub package** is a separate, explicit action and
only replaces an unchanged package previously installed by Ru from the same
repository and subdirectory. It may pin a newer commit. Ru fetches the fixed
`codeload.github.com` archive with redirects disabled, validates archive paths,
size, file count and `SKILL.md` frontmatter, and reports the source commit and
package SHA-256 receipt. Installation is atomic under `%LOCALAPPDATA%/Ru/skills`.
Unknown folders and packages with local edits or extra files are preserved and
refused for update. Package scripts and dependencies are never run or installed;
any later tool use follows the normal Broker policy. There is no automatic
download when a skill is discovered or selected.

## Package format

Create `skills/verify/SKILL.md`:

```markdown
---
name: verify
description: >
  Choose focused checks for a code change
  and report the verification evidence.
requires: [git, dotnet]
disable-model-invocation: false
user-invocable: true
---

Review $ARGUMENTS. Read references/checklist.md relative to this skill's
resource base, choose checks by impact, and report failures and checks not run.
```

The directory name is the callable ID; `name` is a display label. Ru accepts
lowercase alphanumeric IDs with dots, underscores and hyphens, up to 96
characters. For portable Agent Skills packages use a matching lowercase kebab
name of at most 64 characters. YAML folded/literal strings, quoted scalars and
requirement lists are parsed by the shared bounded YAML parser. Unsupported
fields produce diagnostics; they do not imply another product's behavior.

`disable-model-invocation: true` hides a workflow from automatic selection and
refuses implicit body loads, while explicit user invocation remains possible.
`user-invocable: false` prevents user invocation. Disabling a skill hides it from
model selection and refuses loading. Local automatic-selection preferences can
further restrict a package; they cannot override its explicit-only declaration.

`requires` supports `python`, `node`, `dotnet` and `git`. Availability checks
inspect the supported executable names on PATH without launching them. Missing
capabilities return named `<capability>_unavailable` errors; unknown tokens return
`unsupported_skill_requirement`. No dependency is installed automatically.
`compatibility` remains descriptive text.

## Resources, versions and continuity

The `skill` result contains `id`, `name`, `description`, `body`, `body_sha256`,
`source_path`, `resource_base` and `revision`. Resolve relative reference paths
against `resource_base`; subsequent reads or script launches use the normal
tools under Broker policy. A registered external root does not grant external
file or terminal access.

Models can use `skill` with `query` instead of `name` to search metadata omitted
from the prompt listing. The query result is bounded and discloses truncation;
exact-ID lookup can load a skill beyond the listing limit. Disabled and
explicit-only workflows are excluded from this implicit search.

Discovery reads bounded metadata, independently of the 64 KiB package body cap.
One budgeted prompt listing replaces per-skill instruction slots. The shared
cache validates discovered files and roots; refresh forces discovery. Metadata
revision is a discovery stamp, not a claim that a body hash has already been read.
`expected_revision` checks that discovery stamp. A same-length body edit with a
preserved timestamp can leave the stamp unchanged; it is not a content pin.
Use `expected_body_sha256` from an earlier load when the exact body must match.
The returned body digest identifies the exact normalized body.

Once loaded, the run uses a captured snapshot. Compact, restart and Continue
restore that snapshot, not changed disk contents. Explicitly loading again
refreshes the selected version. Up to four full workflows within a 128 KiB body
budget are retained; omitted workflows are disclosed. New chat runs do not
automatically inherit every workflow used previously.

Deterministic catalog/invocation/continuity tests establish these contracts.
The opt-in [skill benchmark suite](../../tools/coding-benchmark/README.md)
separately grades real task output; selection quality and model adherence require
matched live trials and are not established by a passing build.
