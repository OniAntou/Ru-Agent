---
status: accepted
date: 2026-11-01
---

# Scratch workspaces for chats without a project

## Decision

A chat that has not chosen a project folder gets a Ru-owned **scratch workspace**
under the Ru data root: `<data root>/workspaces/<session id>`. The Host creates
it when `POST /agent/v1/runs` arrives with an empty `workspace_path`, writes a
`.ru-scratch.json` marker naming the session, and returns the canonical path and
`workspace_kind: "scratch"` in the run response.

A scratch workspace is an ordinary workspace for trust, policy, tools and
skills. Its only differences are origin, display and lifecycle:

- The desktop client approves its trust request silently. Ru created the
  folder; asking the user to trust Ru's own scratch space is noise. The
  decision still goes through the normal explicit trust endpoint.
- The workspace sidebar shows every scratch session in one **Scratch** group,
  not one group per session id.
- It is deleted with its session: the session-deletion coordinator removes the
  folder after the session record, and a startup sweeper reclaims folders whose
  session no longer exists.
- Deleting a workspace group deletes its sessions through the same
  single-session path. The workspace folder on disk is never deleted; a project
  folder belongs to the user.

Deletion is marker-gated. A folder without a marker is not Ru's: it is left in
place, and a marker naming another session is refused, so a user folder whose
name happens to match a session id is safe.

## Context

The desktop app previously derived a new chat's workspace from the application
directory's parent. With the product checked out at `D:\LLM`, that made every
new chat run in `D:\` — the file tools' authority boundary — so the whole drive
was treated as the workspace and read/write access inside it needed no approval.
The drive root also has no project identity: skills, project memory, AGENTS.md
discovery and coding subagents all assume a project root, and `glob`/`grep`
truncate on a drive.

Other agents avoid this by tying the workspace to the process working directory.
A GUI launched from the Start menu has no project directory, so it needs an
explicit no-project state instead of a guessed one.

## Consequences

- `workspace_path` may be empty. The Host owns the scratch layout; clients never
  construct the path themselves.
- `workspace_path` and `workspace_kind` are additive response fields, and
  `workspace_kind` is additive on the session listing.
- Scratch folders live in the Ru data root, so a read-only install works and
  the product's own repository is never polluted.
- Scratch deletion is best-effort: a locked file reports `ArtifactsPending`,
  which the client surfaces as a retry, and the sweeper finishes later. The
  marker is removed last so an interrupted delete can be retried.
- A scratch folder is ordinary policy territory: no extra grants are implied by
  its origin.
