# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

Native Windows desktop app (WinUI 3, .NET 10), not web. The schema's values do not
cover it; design work follows Windows desktop conventions (keyboard, Fluent controls,
window chrome, Credential Manager) rather than browser or mobile ones.

## Users

Power users coming from Claude Code, Codex and similar coding agents who want a
native Windows agent. They already know the agent loop (tools, approvals, runs,
context, models) and work in it all day beside their IDE and terminal. They value
information density, keyboard control, fine-grained control and seeing exactly what
the agent is doing over hand-holding.

## Product Purpose

Ru is a coding agent built for Windows: the user picks a model provider, brings their
own API key, describes a change, and Ru reads the project, edits files, runs commands
and keeps the work visible in the conversation. Success is that a power user can
delegate real work, follow and steer it, and trust what it did.

## Positioning

Native Windows (WinUI 3 desktop app plus a full-screen terminal UI) with bring-your-own
provider and key, no Ru account, orchestration and storage local to the machine, and an
explicit, scoped permission model (Plan / Standard / Full Access, single-use approvals,
workspace trust).

## Operating Context

- Used beside an IDE and terminal for long sessions across several workspaces.
- Sessions, runs, approvals, questions, background tasks and subagents are first-class.
- Prompts and tool results go to the provider the user selected; web search/fetch make
  network requests when used. Local-first does not mean offline inference.

## Capabilities and Constraints

- Desktop app: chat surface with activity feed, approval and question cards, composer
  with attachments and turn queue, session sidebar grouped by workspace, side panel with
  changes, subagents, tasks and integrated terminal, model/reasoning picker, Settings
  (providers, keys, theme, permission preset, updates), session usage.
- Execution authority stays in the Host/Broker; the GUI never widens permissions.
  Full Access always needs explicit confirmation. Fail closed.
- Architecture: CommunityToolkit.Mvvm view models under `gui-winui3/ViewModels`; views
  keep rendering, animation and dialogs. Behavioral tests in `gui-winui3.Tests`.
- API keys live in Windows Credential Manager and are never shown in diagnostics.

## Brand Commitments

- Keep the current dark tone (user decision, 2026-10-21): refine within the existing
  dark visual language rather than replacing it.
- Name and wordmark: "ru agent" (`docs/assets/wordmark-clean.svg`, `wordmark.svg`).

## Evidence on Hand

- Public README and docs under `docs/`; Preview release 0.1.1-preview.1.
- No customer testimonials, usage metrics or benchmarks of real-provider speed exist;
  do not invent them.

## Product Principles

1. Show the work: every action the agent takes is visible, attributable and inspectable.
2. Control stays with the user: permissions, approvals and stops are always one clear
   step away, and nothing elevates silently.
3. Density with calm: a power user's screen carries a lot of state without noise.
4. Keyboard first, mouse welcome.
5. Native to Windows, not a web page in a window.
