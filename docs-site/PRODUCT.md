# Product

<!-- impeccable:product-schema 1 -->

Scope: the Ru Agent documentation site (`docs-site/`). Product facts below come from the repository
(`README.md`, `CONTEXT.md`, `docs/`) and from the answers confirmed for this surface.

## Platform

web

## Users

Windows developers who are weighing or starting to use Ru: they want to install it, connect their own
model provider, run it from the desktop app or the terminal, and understand what it is allowed to do
on their machine. Contributors and integrators (RPC, TypeScript SDK, architecture, ADRs) are a second,
deeper audience reached through the same site; the confirmed primary reader is the user, not the
contributor.

## Product Purpose

Ru is a local-first coding agent for Windows. The user describes a change; Ru reads the project, edits
files, runs commands and keeps the work visible in the conversation. The documentation site must get a
visitor from "what is this" to a running Preview install quickly, then support day-to-day use and,
further back, the architecture and decisions behind it.

## Positioning

A native Windows coding agent (WinUI 3 desktop app plus a full-screen terminal UI) that uses the
visitor's own model provider and API keys, with no Ru account, and whose permission model, tool policy
and run history are inspectable. Orchestration runs locally.

## Operating Context

Visitors read on a desktop browser, often with a terminal open beside it, copying install and `ru`
commands. They arrive from the GitHub repository. Documents are English Markdown that lives in the
repository (README, `docs/`, package READMEs) and is rebuilt on every push to `dev`.

## Capabilities and Constraints

- Platform: Windows x64 only. Desktop and CLI ship as separate per-user packages; Preview is available,
  no Stable release is published yet, and the initial packages are unsigned.
- Permission presets: `plan` (read-only exploration), `standard`, `full_access`. Workspace trust and
  explicit approvals are part of the workflow. Instructions and skills guide the agent; they never grant
  execution authority.
- Terminal commands run as the user's Windows account without an OS filesystem or network sandbox.
- Local-first does not mean offline inference or a bundled model; web search/fetch make network requests
  when used. API keys are stored in Windows Credential Manager.
- Licence: MIT.
- Surfaces: native desktop app, terminal UI (`ru`, `ru agent chat`), stdio RPC bridge with a TypeScript
  SDK, custom slash commands and hooks, `SKILL.md` workflows, subagents.

## Brand Commitments

- Name: Ru Agent. The pixel-style wordmark `docs/assets/wordmark-clean.svg` is the existing identity asset.
- Tone, confirmed for this surface: calm, technical and honest. Ru is a Preview; the site states limits
  (no sandbox, unsigned packages) plainly and does not oversell.
- Not binding (evidence only): the TUI palette is steel-blue with violet accents and warm neutrals; the
  desktop app is dark-first.

## Evidence on Hand

- `README.md` (positioning, install, quick start, permissions), `docs/` (about 50 pages), 23 ADRs.
- Real terminal output from Ru runs (for example the status panel and subagent lines) can be quoted.
- Screenshots of the desktop app exist only in local test artifacts and have not been cleared for
  publication; none may be assumed available. No customers, testimonials, benchmarks or download counts
  exist to cite.

## Product Principles

1. Say what Ru can and cannot do, including the uncomfortable limits, before the visitor finds out the
   hard way.
2. The visitor's route to a working install is shorter than any other path through the site.
3. Show the work: real commands, real output, real policy names, not paraphrase.
4. Reference depth stays one click away from the first screen but never crowds it.

## Accessibility & Inclusion

Dense technical reading in long sessions, light and dark system preferences, keyboard navigation and
copyable code. No product-specific standard beyond WCAG AA contrast and visible focus.
