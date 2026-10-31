---
status: accepted
date: 2026-10-28
---

# Explicit MCP Streamable HTTP behind the existing Broker authority

## Context

ADR 0025 introduced user-declared stdio MCP servers. Remote MCP services cannot
use that transport. Transport must not change approval semantics or silently
substitute another server after a failed tool invocation.

## Decision

Extend ADR 0025's transport choice with explicit `streamable_http`, a fixed URL
and optional bearer credential name in the same user-level configuration.
Pin the published 2025-11-25 protocol. Allow HTTPS and loopback HTTP, disable
redirects, and bound JSON/request-scoped SSE responses, events and deadlines.
The Broker owns connections, credential lookup, validation and redaction.

Keep the existing `mcp` identity and ExternalTerminal policy decision: Plan
denies active calls, Standard asks, Full Access follows its existing policy.
Server annotations and remote content grant no authority. No HTTP fallback for
stdio, automatic session recovery or replay of uncertain tool calls is allowed.
Return structured content and resource-link metadata within existing output
bounds, without fetching linked resources. Images remain omitted.

## Consequences

Remote servers using this protocol and existing bearer credentials can be used
without a local wrapper. OAuth authorization/refresh, autonomous GET streams,
prompts, sampling and resource reads remain separate work. Deterministic HTTP
handler tests establish transport behavior; they do not prove compatibility
with every remote service. See [operations](../operations/agent-tools.md#mcp-servers).
