# Native OpenAI and Anthropic providers

**Status:** Approved design; implementation not started.

## Goal

Add first-party OpenAI and Anthropic Claude providers to Ru. Each provider uses
its native API, its own Windows Credential Manager account, the existing
Host-owned model catalog, and the existing per-run protected provider snapshot.
Only models verified as suitable for conversational Agent runs are selectable.

This change must not add provider, credential, model, endpoint, protocol, or
reasoning fallback. It must not restore generation-parameter controls removed
from the GUI.

## Decisions

- OpenAI uses the Responses API at `https://api.openai.com/v1/responses`.
- Anthropic uses the Messages API at `https://api.anthropic.com/v1/messages`.
- OpenAI and Anthropic have fixed first-party endpoints. They do not reuse the
  Custom OpenAI endpoint.
- Secrets use canonical Credential Manager accounts `openai_api_key` and
  `anthropic_api_key`. Empty GUI secret fields retain the existing value, as
  they do for the current providers.
- The provider bootstrap carries an explicit bounded protocol value. The Worker
  selects an adapter from that value and rejects unknown or mismatched values.
  It never infers protocol from a URL or silently substitutes an adapter.
- OpenAI, Anthropic, and the existing OpenAI-compatible transport remain small,
  separate `IModelProvider` implementations.
- Ru's existing prompt-based Agent/tool selection contract remains authoritative.
  This change does not enable provider-hosted tools or native function calling.

## Settings and GUI

Add `OpenAI (Official API)` and `Anthropic Claude (Official API)` to the provider
selector. Selecting either provider shows only its matching API-key row. The
shared model selector, refresh behavior, stale/error state, and reasoning-effort
selector continue to use `ProviderCatalogController`.

Persist separate `openai_model` and `anthropic_model` values. Public settings
return only `has_openai_api_key` and `has_anthropic_api_key`; secret values are
always empty and never serialized to disk settings, logs, diagnostics, catalog
responses, or GUI state.

OpenAI and Anthropic count as cloud providers for the existing explicit cloud
memory consent behavior. Switching provider saves only the selected provider and
fields supplied by the GUI. It does not copy, alias, migrate, delete, or probe
another provider's credential.

## Catalog and capability registry

Catalog discovery remains Host-owned, bounded, cached for five minutes,
throttled for thirty seconds, single-flight, and keyed by provider endpoint plus
credential revision.

- OpenAI calls `GET https://api.openai.com/v1/models` with its Bearer key.
- Anthropic calls `GET https://api.anthropic.com/v1/models` with its `x-api-key`
  and the required `anthropic-version` header.
- Redirects remain disabled. Response timeout, byte, item, depth, and string
  limits remain enforced.

Neither model-list endpoint exposes enough machine-readable information to
prove conversational protocol, Agent suitability, reasoning levels, and output
limits for every returned ID. Ru therefore intersects the live account catalog
with a versioned first-party capability registry. Registry entries use exact
provider-published model IDs and aliases; broad name-prefix inference is not
allowed. Each entry contains protocol, selectable status, supported reasoning
options, and, for Anthropic, the documented maximum output capability.

The initial registry contains the generally available first-party text models
documented by each provider at implementation time. Tests pin the chosen entries
and their source date. A model returned by the live API but absent from the
registry is retained internally as unsupported and omitted from the GUI's
selectable list. It is never called to discover its capability. Updating support
for a newly released model is a small registry-and-test change, while ordinary
availability changes continue to appear through automatic catalog refresh.

Reasoning options come only from the registry entry for the exact selected
model. `Provider default` omits every reasoning override. A stale, unknown, or
invalid explicit option blocks dispatch instead of downgrading to a default.

## OpenAI Responses adapter

The OpenAI adapter builds one request from the canonical `ModelRequest`:

- `model` is the run-pinned model.
- `instructions` contains the composed system prompt.
- `input` contains the bounded prior user/assistant turns followed by the current
  user prompt.
- `stream` is `true` and `store` is `false`.
- A verified explicit effort is serialized as `reasoning: { effort: ... }`.
  Provider default omits `reasoning`.
- Private Worker settings such as `model_tools` never cross the provider boundary.

The parser accepts the documented Responses SSE lifecycle, emits visible text
only from output-text delta events, completes only on the documented terminal
event, ignores documented non-text progress events, and rejects malformed,
oversized, contradictory, or incomplete streams. Reasoning content and provider
error bodies never enter transcript text.

Authentication uses only `Authorization: Bearer <OpenAI key>`. The adapter does
not send Anthropic or OpenAI-compatible compatibility headers.

## Anthropic Messages adapter

The Anthropic adapter sends:

- `model`, the separate top-level `system` prompt, alternating bounded
  user/assistant `messages`, and the current user prompt;
- `stream: true`;
- `max_tokens` equal to the smaller of the model's documented output capability
  and Ru's internal 8,192-token safety ceiling;
- for a verified explicit effort, the model-specific native configuration from
  the registry, including adaptive thinking and `output_config.effort` where
  documented. Provider default omits these overrides.

The 8,192 ceiling is an internal transport safeguard, not a user setting. It
does not restore Max New Tokens in the GUI. A registry entry whose documented
limit is missing or invalid is not selectable for Anthropic.

Authentication uses only `x-api-key`, the pinned `anthropic-version` value, and
JSON content headers. The parser handles the documented Messages SSE lifecycle,
emits only `text_delta`, ignores ping and non-visible thinking/signature deltas,
maps the final stop reason to Ru's bounded finish reasons, and rejects malformed,
oversized, contradictory, or incomplete streams. Thinking text is never exposed
as an assistant answer, log field, or diagnostic payload.

## Provider selection and protected transport

`ProviderSelection` resolves a single immutable descriptor containing backend,
alias, protocol, fixed base URI, selected model, and canonical credential
account. `AgentProviderSnapshotFactory` validates that the selected model and
reasoning choice are supported by the same fresh catalog identity, reads exactly
that credential account, and writes the single-use DPAPI bootstrap.

The first step pins provider, protocol, endpoint, model, credential, and request
options for the run. Later settings changes affect only a new run. Credential
revision mismatches, missing credentials for first-party providers, unsupported
models, and invalid reasoning choices fail before generation.

The Worker creates the adapter selected by the pinned protocol for each step.
OpenAI-compatible providers continue to use `/chat/completions`; OpenAI uses
`/responses`; Anthropic uses `/messages`. No failure may trigger a call through a
different adapter or endpoint.

## Error, privacy, and cancellation behavior

Both adapters map authentication, authorization, rate limiting, provider
overload/server failure, context limits, timeout, cancellation, malformed data,
and response-size violations to stable `ModelProviderException` codes. Raw
provider response bodies and exception messages stay private. Cancellation is
forwarded to the one active HTTP request and never starts a replacement request.

Retries, if retained by an existing same-provider transport policy, must use the
identical pinned provider, protocol, endpoint, model, credential, and options.
This feature adds no automatic retry and no cross-provider fallback.

OpenAI requests set `store: false`. No prompt, response, API key, account identity,
provider body, or model-list body is added to Ru logs or automated evidence.

## Testing and acceptance

Implementation follows RED/GREEN cycles with controlled HTTP handlers and no
live credentials:

1. Settings and credential-isolation tests cover canonical account reads/writes,
   redacted public settings, provider switching, rollback, and no aliases.
2. Catalog tests cover required authentication headers, exact capability
   intersection, unsupported new IDs, reasoning choices, cache invalidation,
   bounds, rate limits, cancellation, stale snapshots, and no generation probes.
3. OpenAI adapter tests cover exact request JSON, `store: false`, reasoning
   omission/serialization, text streaming, terminal/error events, cancellation,
   status mapping, malformed UTF-8/JSON, and byte/line limits.
4. Anthropic adapter tests cover required headers, system/message mapping,
   internal `max_tokens`, adaptive effort omission/serialization, text-only
   projection, thinking suppression, stop reasons, stream errors, cancellation,
   and bounds.
5. Worker and protected-bootstrap tests prove exact protocol dispatch and fail
   closed behavior for unknown/mismatched protocols.
6. GUI tests cover provider labels, key-row visibility, redacted placeholders,
   model filtering, refresh, reasoning state, partial saves, and no retired
   generation controls.
7. Verification runs focused tests first, then the backend suites sequentially,
   the GUI suite, Release builds, documentation checks, and `git diff --check`.

Automated tests do not prove real account access, billing behavior, provider
quality, or live model availability. A live catalog smoke requires the user's
key but does not generate text. Any live generation smoke requires separate
explicit approval because it can spend provider credits. GUI publishing,
launching, and interactive acceptance remain separate evidence.

## Out of scope

- Provider-native hosted tools, function calling, web search, files, MCP, or
  computer use.
- OpenAI Chat Completions as fallback for Responses.
- Anthropic's OpenAI compatibility layer, Bedrock, Vertex AI, or third-party
  Claude gateways.
- Editable first-party endpoints, organization/project selectors, prompt
  caching controls, service tiers, batch/realtime APIs, or provider-side state.
- Returning temperature, Top-K, or Max New Tokens controls to the GUI.
- Automatic capability guessing or live trial requests for unknown models.

## Provider references

- [OpenAI Responses streaming](https://platform.openai.com/docs/api-reference/responses-streaming/response/refusal?lang=python)
- [OpenAI models API](https://platform.openai.com/docs/api-reference/models/object?lang=curl)
- [OpenAI model catalog](https://platform.openai.com/docs/models)
- [Anthropic streaming Messages](https://platform.claude.com/docs/en/build-with-claude/streaming)
- [Anthropic Models API](https://platform.claude.com/docs/en/api/models/list)
- [Anthropic reasoning guidance](https://docs.anthropic.com/en/docs/build-with-claude/prompt-engineering/prompt-templates-and-variables)
