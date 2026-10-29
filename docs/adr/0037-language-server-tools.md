---
status: accepted
date: 2026-10-29
---

# Broker-owned language-server tools

## Status

Accepted with the `coding_semantic_v1` tool profile.

## Context

Coding runs need syntax and project-aware diagnostics, symbol outlines, go-to-definition and references. Reusing installed language servers gives C# and TypeScript project semantics without downloading executables or adding another execution authority. A configured server is still an arbitrary process running as the current Windows user, so its launch must use Broker policy.

## Decision

Expose one `code_intelligence` v1 tool with `diagnostics`, `symbols`, `definition` and `references` operations. It is present only in the opt-in `coding_semantic_v1` profile; existing profiles retain their exact inventories and pins. The Host projects the tool only when the user's profile contains a supported server configuration.

Only `%USERPROFILE%\\.ru\\lsp.json` is read. Each entry names a supported language, executable and literal argument vector. Ru never reads a workspace server config, invokes package managers, downloads a server or runs a workspace setup script. The Broker resolves the requested document under its canonical workspace and starts the configured executable with that workspace as cwd. The call is classified as an external-process launch: plan denies it, standard asks, and full_access follows the existing external-terminal policy. Approval happens before process creation.

The Broker owns one process per configured language for its workspace. LSP uses JSON-RPC over stdio with ASCII `Content-Length` framing and UTF-8 JSON bodies, a 4 MiB frame cap, 120-second request deadline, cancellation notification and process-tree cleanup. User content is synchronized using monotonically increasing LSP document versions. Diagnostics without a matching current version and responses after the document content hash changes are rejected. Results are limited to 128 entries and 512 KiB and disclose truncation. Returned document locations must use file URIs inside the canonical workspace; they are rewritten to workspace-relative paths.

The initial language set is C# and TypeScript. Supported operations are only those in the tool schema. Missing configuration, unsupported language, launch failure, protocol failure, stale diagnostics and external locations return bounded named failures; no fallback server is selected.

## Consequences

- The tool requires process-launch approval even though its language operations are read-oriented.
- Users install and update language servers themselves and configure their executable paths in their user profile.
- Servers receive the same Windows user rights as other Broker-launched tools. Ru bounds the protocol and process lifetime but does not claim an OS filesystem or network sandbox.
- LSP results and diagnostics are untrusted tool output and do not grant execution authority.

## Verification

The Broker tests cover configuration parsing, framing limits, fragmented and consecutive frames, policy decisions and current-document identity. Installed-server smoke coverage depends on a configured local C# or TypeScript server and is reported separately.
