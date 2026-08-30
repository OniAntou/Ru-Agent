# Typed Agent Commands and Web Search/Fetch

**Status:** Historical implementation record. Typed commands and bounded web
adapters shipped; the later default-web wiring is recorded in
`2026-08-31-enable-agent-web-tools-design.md`. Current behavior is in
`docs/operations/runtime-behavior.md` and `docs/operations/agent-tools.md`.
**Date:** 2026-08-29
**Scope:** A small typed command router plus bounded web search/fetch tools.
Subagents and LSP are explicitly removed from this iteration.

## Outcome

The Host recognizes a small, versioned set of Agent commands before dispatching
ordinary user text to the model. Commands produce structured results, do not
silently become model history, and reuse the same Broker/session policy as
normal Agent actions. The Broker exposes the existing safe web adapters as
first-class tools when web configuration and session grants permit them.

## Goals and non-goals

Goals:

- one parser/router shared by the GUI and CLI adapters;
- deterministic grammar and stable error codes;
- `/compact`, `/help`, `/skills`, and `/skill <id> <json>` in v1;
- `web.search` and `web.fetch` tool manifests/executors with bounded,
  sanitized, explicitly untrusted results;
- safe defaults: no web capability is advertised without configuration and a
  matching session grant.

Non-goals:

- no subagent, LSP, extension, plugin, or MCP command;
- no arbitrary shell or HTTP request command;
- no command-specific bypass of approval, scope, or context-compaction rules;
- no OS-level network isolation. `WebPolicy` remains request validation, not a
  sandbox.

## Typed command contract

The shared core exposes an `AgentActionRouter` and immutable values similar to:

```
AgentCommandKind = Compact | Help | Skills | Skill
AgentCommand = { Kind, Strategy?, SkillId?, JsonInput? }
AgentCommandResult = { Status, Code, Data, RunVersion? }
```

Parsing is whitespace-tolerant but bounded. A command is recognized only when
the first non-whitespace character is `/` and the command name is one of the
versioned names below. Malformed recognized commands return a structured
`command_invalid` result and are not sent to the model. Unknown slash commands
return `command_unknown` with help text; ordinary text that does not begin
with `/` remains an ordinary user prompt. A future escape syntax can be added
without changing the typed contract.

### v1 commands

- `/compact [normal|aggressive]`: invokes the run compaction operation, returns
  safe estimates/strategy/version, and never inserts the command or summary
  text into conversation history.
- `/help`: returns static command names and argument descriptions.
- `/skills`: returns only skills visible in the current authenticated session
  and capability profile.
- `/skill <id> <json-object>`: validates the ID and JSON object, then invokes
  the granted declarative Skill through the Broker. Tool approval and scope
  rules still apply.

The router never directly executes a tool. It returns a typed action for the
Host/runtime layer, which calls the existing coordinator or Broker service and
maps the result to the client activity stream. Command activity contains only
bounded metadata and is excluded from model context unless a future explicit
policy opts in.

The CLI and WinUI adapters use the same parser and result codes. They may
render different controls, but neither duplicates command grammar or policy.

## Web tool contracts

Add `web.search` and `web.fetch` to the protocol-level tool contract catalog
and Broker `ToolCatalog`, using the existing `WebSearchAdapter`,
`WebFetchAdapter`, `WebPolicy`, `WebContentSanitizer`, and
`SourceCitation` types.

`web.search` accepts a bounded query and returns at most ten citations.
`web.fetch` accepts one absolute HTTP(S) URI and returns one citation plus
bounded sanitized text. Both adapters enforce response byte/character caps,
supported content types, cancellation, and stable invalid-input errors.

Web options are explicit runtime configuration. The search endpoint is absent
by default; without it, `web.search` is not registered or advertised.
`web.fetch` is advertised only when web is enabled and the session has
`web.fetch`; search additionally requires `web.search` and a configured
search endpoint. Configuration must not put credentials in prompts, capability
JSON, events, or receipts.

`WebPolicy` validates the initial URI and every redirect target (or disables
automatic redirects and follows only validated targets). It rejects invalid
schemes, loopback/private/link-local/reserved addresses, and DNS resolutions
that map to denied ranges. Tests must cover redirects, DNS rebinding seams,
oversized responses, unsupported content types, cancellation, and sanitizer
boundaries.

Search/fetch are read-only external operations. They do not grant workspace or
terminal scopes. If a deployment elects to require approval for network reads,
the normal approval binding is used; the tool must never bypass it merely
because the request came from a command or Skill.

## Prompt and trust boundary

The result contract preserves citations and wraps fetched/search text with an
explicit untrusted-web marker. The prompt composer may present this data as
reference material, but system policy and tool-selection instructions remain
authoritative. HTML/script/control-marker stripping occurs before model/client
serialization, and raw response bodies are never persisted as diagnostics.

This design does not claim that URL validation is an OS sandbox. The separate
future sandbox project remains responsible for process/filesystem/network
isolation against hostile approved code.

## Tests

Focused tests cover:

1. parser success, malformed/unknown commands, bounds, and no-history routing;
2. `/compact` delegation and conflict/error mapping;
3. `/skills` grant filtering and `/skill` input/approval propagation;
4. web manifest schemas, configuration-gated capability advertisement, scope
   checks, search result limits, fetch sanitization, content/byte limits,
   redirect/private-address rejection, cancellation, and stable receipts; and
5. GUI/CLI contract tests proving both adapters use the same parser/result
   codes.

The route matrix, deterministic Broker tests, and runtime capability snapshot
are updated together so documentation cannot claim web support when the
endpoint is disabled.

## Documentation impact

Update current command/capability/tool/operations documents and README sections
to list the four typed commands, configuration-gated web search/fetch, bounded
untrusted results, and the explicit absence of subagent/LSP/extension/plugin/
MCP support. Keep historical comparison/design documents unchanged except for
links or status labels when required by the documentation policy.
