# Ru Agent Tool Hardening Design

**Date:** 2026-09-05

## Goal

Make Ru's advertised model tools match the tools that the Broker can execute,
repair live Web search parsing, and make tool readiness auditable without
claiming experimental MCP or connector code as a shipped capability.

## Scope

- Keep `AgentToolDefinitionCatalog` as the canonical catalog for all 18 Broker
  operations.
- Project only definitions marked `ModelVisible` into model contracts and model
  capability lists.
- Derive every Broker binding's `ModelEnabled` value from the canonical
  definition rather than assigning `true` globally.
- Keep Broker run leases capable of executing the 16 built-in non-Web
  operations plus enabled Web operations. Lease execution capability is not
  the same thing as model exposure.
- Include terminal execution in the effective Agent profile. Analyzer-safe
  commands may run; deletion and unknown executables continue to require the
  existing one-time approval flow.
- Parse DuckDuckGo HTML result anchors, unwrap their `uddg` target, validate the
  final destination through `WebPolicy`, and fail explicitly when result
  anchors exist but none can be parsed.
- Add table-driven contract/binding tests covering every canonical definition.
- Correct current operational documentation. Historical plans and accepted or
  superseded ADRs remain historical records and are not rewritten.

## Non-goals

- No fallback Web API or hidden second search provider.
- No live volume mutation in automated tests.
- No production activation of MCP servers, mail, calendar, or task connectors.
  Those require a separate configuration, credential, trust, and approval
  design. Current docs will label them experimental/unwired.
- No change to public HTTP or WebSocket routes.
- No OS-level terminal sandbox in this change.

## Architecture

`AgentToolDefinitionCatalog` owns ID, version, schema, scope, and exposure.
`AgentModelToolContractCatalog` becomes a strict model-visible projection.
Broker manifests that need internal definitions read the canonical catalog
directly. `ToolBindingRegistry.ModelCapabilities` applies both model exposure
and scope filtering, so a future internal binding cannot leak through a broad
scope alone.

Web search remains a single-provider bounded HTTP adapter. Its HTML parser
recognizes result anchors instead of arbitrary absolute anchors. DuckDuckGo
redirect links are converted to their final HTTPS target before policy
validation and citation construction. An HTML response containing recognizable
result anchors but yielding no valid targets is an adapter failure, not a
successful empty search.

## Verification

1. RED/GREEN tests for DuckDuckGo protocol-relative redirect links and explicit
   parse failure.
2. RED/GREEN tests that internal definitions are absent from model contracts
   and have `ModelEnabled == false` in Broker capabilities.
3. RED/GREEN profile tests confirming terminal tools are available in Agent and
   Full while destructive/unknown commands retain approval behavior.
4. Canonical catalog-to-binding coverage for all 18 definitions with Web both
   disabled and enabled.
5. Focused Broker, Protocol, Harness, and documentation tests followed by the
   full sequential .NET and Python gates, route matrix, build, and diff check.

## Documentation policy

Current-state documents must agree that managed Git commit handling is shipped,
MCP is tested infrastructure but not production-composed, connectors are not
Agent tools, and the current public route contract is 64 HTTP plus one
WebSocket. Historical plans retain the route counts and status that were true
at their checkpoints.
