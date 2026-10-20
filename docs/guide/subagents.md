# Subagents

For work that splits into independent parts, or needs a long investigation, the main agent can hand a
task to a **subagent** and carry on. You do not start subagents yourself; you ask the main agent, and it
decides whether delegating helps.

## Two kinds

| Kind | For | Needs |
| --- | --- | --- |
| `review` | Read-only research, web searches, analysis and code review | Nothing: it works in any folder |
| `coding` | Tasks that change files | A project that is a Git repository root with at least one commit |

A coding subagent works separately from your project. Its result reaches your project only when the
main agent applies it through guarded file edits, and applying never commits or pushes. If Git is missing, Ru explains
the prerequisite and changes nothing; it will not initialize a repository for you.

## Ask for one

Say what you want delegated:

> Use a review subagent to check `parser.cs` for boundary errors, then summarize what it found.

The main agent starts the child, waits for it, reads its result and reports back. Stopping the main run
stops its children.

## Watch them and read the results

- **Terminal UI:** each child gets a line when it starts, finishes, fails or stops, and a strip of chips
  at the right of the header's folder line shows what is running. `/subagents` lists the children with their model and usage,
  and `/subagents <agent-id> result` prints a result.
- **Desktop app:** a **Subagents** panel in the side pane shows the task tree, each child's status and
  model, and its result. You can page through or export a result.

You cannot send messages to a child, stop one or apply its work yourself; ask the main agent to do any of
those.

## Which model a subagent uses

A subagent uses the same provider and model as the main agent. To use another one, name it explicitly in
your own message, for example "use Claude Opus for the review subagent". If that model is not available
from a provider you are signed in to, Ru tells you and does not substitute another.

## Limits

Subagents cannot start subagents of their own. How the delegation, approvals and results work in detail
is in [Subagents](../operations/subagents.md) under Reference.
