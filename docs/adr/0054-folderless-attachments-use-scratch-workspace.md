---
status: accepted
date: 2026-11-21
---

# Folderless chats stage attachments in their scratch workspace

## Status

Accepted, 2026-11-21. Amends [ADR 0053](0053-attachments-reach-the-model-as-paths.md)
decision 3 and extends [ADR 0041](0041-scratch-workspaces.md) allocation timing.

## Context

ADR 0053 made every attachment reach the model as a workspace path. A chat that has
chosen a project folder can therefore attach any file; a new desktop chat with no
folder could not, because the only workspace it will have — the Host-owned scratch
folder from ADR 0041 — was allocated when the first run arrived, after the composer
had already accepted or refused the attachment. The client fell back to inlining
images/text/documents and refused everything else with "choose a folder".

That refusal also depended on a client-side type list. A file the client did not
recognise, such as a normal SVG layout, was refused before the model could decide how
to read it, even though the file is UTF-8 text. The architecture already has the right
place for arbitrary files: a scratch workspace is an ordinary workspace for policy,
trust and tools, and its path is Host-owned and marker-proven.

## Decision

Before a folderless desktop chat stages a non-image attachment, the client asks the
Host for the session's scratch workspace through
`POST /sessions/{sessionId}/workspace/scratch`. The Host verifies that the session
exists, creates or reuses the marked folder under its data root, and returns
`workspace_path` plus `workspace_kind: "scratch"`. The client never constructs or
guesses that path.

The client then uses the returned path exactly as a chosen project folder:

- small UTF-8 text is still inlined, an image still goes to a model that reads images;
- every other file is copied into `<scratch>\.ru\attachments` and attached by its
  workspace-relative path;
- the first run sends the same path, and the Host classifies it as scratch from the
  ownership marker, so trust, display and deletion keep their existing rules.

Images keep the existing image path in a folderless chat: they are accepted without a
workspace, and the Host route is not called for them. If allocation fails, intake falls
back to the previous local rules and names the failure; it never sends a path the model
cannot read.

The scratch folder created for an empty draft is removed by the normal session
deletion. The `?if_empty=true` cleanup path also removes it, and the startup orphan
sweeper remains the backstop.

## Alternatives considered

- **Upload attachment bytes to a new Host staging route.** This would add a second file
  transfer contract and duplicate the staging logic the clients already share. The
  scratch workspace already exists and is designed for session-scoped files.
- **Keep refusing arbitrary files without a folder.** This is what ADR 0053 chose, and
  it is no longer necessary once scratch allocation is available before the first run.
- **Give clients a fixed Host-owned attachment folder outside every workspace.** That
  widens Broker path rules for every run (ADR 0053 rejected the same idea). The scratch
  workspace is already an ordinary, marker-owned workspace with no extra authority.
- **Treat SVG as an image.** Providers accept only png/jpeg/webp, and SVG is markup, not
  a bitmap. It is text/code here; rendering it would create a new image pipeline.

## Consequences

- The public route contract gains one ASP.NET route (57 HTTP method/path pairs).
- A folderless chat creates a small scratch folder when the user first attaches a
  non-image file, not on every New Chat.
- An abandoned draft that staged a file is cleaned through the same scratch lifecycle as
  a chat that ran; the client's staged chip still deletes its copy when removed before
  Send.
- No new filesystem authority: the run uses the same scratch workspace the Host would
  have allocated for it, and the ownership marker still decides `workspace_kind`.
