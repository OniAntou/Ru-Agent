---
status: accepted
date: 2026-10-22
---

# MCP servers run through the Broker behind one `mcp` tool

> Transport extension: [ADR 0035](0035-mcp-streamable-http.md) adds explicit
> Streamable HTTP and bounded structured/resource-link results. The original
> stdio decision below remains applicable to local server processes.

## Context

Power users coming from Claude Code and Codex expect to attach Model Context
Protocol (MCP) servers. Ru has a tested stdio MCP client and tool bridge, but
only in `RuAgent.Broker.Tests`; [tool policy](../operations/agent-tools.md)
lists MCP as unwired until a production trust and credential design exists.

Four existing constraints shape that design:

- **One execution authority.** File, terminal and network effects run through
  `IToolGateway` against the single `BrokerExecutionRuntime`
  ([ADR 0009](0009-broker-execution-authority.md),
  [ADR 0014](0014-in-process-execution-and-terminal-sandbox.md)). An MCP server
  is an arbitrary local program, so starting it and calling it are side effects.
- **Fixed tool catalog.** Tool definitions belong to `RuAgent.Protocol` and the
  Broker manifest digests must agree. The catalog already holds its maximum of
  18 definitions (13 built-in plus 5 `agent_*`), so per-server tools cannot be
  added to it one by one.
- **Workspace content grants nothing.** A configuration file inside the
  workspace can be written by the model in `standard`. `<workspace>/.ru/hooks.json`
  already shows the risk: a hook runs a command outside terminal policy.
- **Credentials stay in Windows Credential Manager** and out of logs.

## Decision

1. **Configuration is user-level only.** Servers are declared in
   `%USERPROFILE%\.ru\mcp.json` (name, executable, arguments, working directory,
   environment names, enabled flag, call timeout). A workspace file is never
   read. The file joins the protected path catalog, so structured tools cannot
   change it in `plan` or `standard`. Secret environment values are references to
   Credential Manager entries, resolved only when the server starts and never
   written to the file, receipts or diagnostics.
2. **Transport is stdio.** Remote (HTTP/SSE) MCP servers, OAuth, and the
   resources, prompts and sampling features are out of scope for this decision.
3. **The Broker owns server processes.** The execution runtime starts a server
   lazily on its first call, at most one instance per server per Host, as the
   current Windows user with the sanitized agent environment plus its declared
   variables. Startup, each call and output size are bounded; the process tree is
   killed on timeout, Host shutdown or idle exit. A failing server produces a
   named code (`mcp_server_unavailable`, `mcp_tool_unknown`, `mcp_timeout`,
   `mcp_error`, `mcp_output_too_large`) and never fails over to another server.
4. **The model sees one `mcp` tool.** `RuAgent.Protocol` gains a single
   definition, `mcp`, with `{server, tool, arguments}`; the catalog maximum
   becomes 19 and the manifest digest changes once. The tool is advertised only
   when at least one enabled server is configured. Like skills, the prompt lists
   each enabled server's tools by name and bounded description; the Broker
   validates `arguments` against the server's declared input schema before the
   call. Server output is bounded and marked untrusted, like web content.
5. **Policy asks by default.** An MCP call is evaluated as the
   `ExternalTerminal` policy subject: like a terminal launch outside the
   workspace, it runs a user-chosen program with the user's rights. The canonical
   profile therefore needs no new subject or version: `plan` denies every MCP
   call, `standard` asks for each call, and an approval card names the server
   and tool (amended 2026-10-23: the arguments are bound exactly into the
   single-use approval but are not shown on the card).
   "Always allow" saves an ordinary policy rule for that server and tool; the
   rule's argv carries the op first (`list_tools`, or `call` and the tool), so a
   remembered `list_tools` never covers a call, and server and tool names are
   restricted to characters that are never rule wildcards.
   `full_access` runs MCP calls without asking. Tool annotations reported by a
   server (for example a read-only hint) are untrusted and never grant
   authority; explicit deny and ask rules still apply in every preset.

## Alternatives and consequences

- Registering every server tool as a native provider tool would give providers
  exact schemas, but it makes the catalog dynamic per Host, breaks the fixed
  manifest digest, and lets a server's tool names collide with built-in tools.
- Reading `.ru/mcp.json` from the workspace would match some other agents, but a
  model in `standard` could then add a server and run any program the next time
  the tool list loads.
- Running servers from the Host outside the Broker would recreate the parallel
  execution path that ADR 0009 removed.

The single tool costs some schema precision: providers see `arguments` as an
object, and argument errors come back from the Broker instead of the provider.
Each server runs with the user's full rights, so an approval in `standard` and
the user-level file are the trust boundary; Ru does not confine what a server
does once it runs. The owner accepted this decision on 2026-10-22.
