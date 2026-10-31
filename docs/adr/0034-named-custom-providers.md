---
status: proposed
date: 2026-10-28
---

# Named custom providers

## Context

Ru talks to one self-hosted or relay endpoint through the single "Custom OpenAI"
slot: one URL, one optional key (`custom_api_key`), one model, Chat Completions
only. A review of that slot on 2026-10-28 found:

- **One slot.** A user with Ollama at home and a vLLM box at work, or a relay
  speaking the Anthropic protocol, has to keep retyping the slot.
- **Catalog from `GET {url}/models` only.** A server without that route offers no
  model at all, and one malformed entry (`id` missing or over 256 characters)
  fails the whole list as `catalog_malformed`.
- **Context window.** A run pins the catalog's `context_length` and otherwise the
  32,768-token fallback. Ollama and LM Studio publish no length, and Ollama's
  default context is far smaller than 32K and truncates silently, so the
  compaction budget is wrong without any error.
- **Plain-HTTP keys.** Any `http` URL is accepted, so a saved key travels
  unencrypted to whatever host the URL names.
- **No feedback while editing.** The URL saves on every pause in typing, is not
  normalised (`…/v1/chat/completions` pasted from a guide breaks both routes), and
  nothing tests it.
- **Tool calling.** Every request declares tools. A model or server that cannot
  call tools fails with the server's raw 400.

The invariants that bind any change:

- **No silent substitution.** Provider, model, protocol and tool identities stay
  pinned for a run. Failures are bounded, named codes.
- **Durable pins.** A run's Provider Selection is pinned in SQLite before the first
  model request and rebuilt from the same account after restart. No key migration
  or provider switching is performed (providers.md).
- **Secrets.** Keys live in Windows Credential Manager and never in settings,
  logs, `GET /settings` or pins.
- **Fail closed** on invalid or ambiguous configuration.

## Decision

Users can add any number of **named custom providers** next to the catalog
providers. Each is one OpenAI- or Anthropic-compatible endpoint described by the
user.

### Definition

`custom_providers` in settings is an array of non-secret definitions (at most 32):

| Field | Rule |
|---|---|
| `id` | `^[a-z][a-z0-9-]{0,31}$`, unique. Names the provider in requests and its credential. Immutable after creation. |
| `display_name` | 1–64 characters, no control characters. What pickers and Settings show. |
| `base_url` | Absolute `http`/`https`, no user info, query or fragment. Normalised on save (below). |
| `protocol` | `chat_completions` or `anthropic_messages`. |
| `models` | At most 200 entries of `{ id, context_window? }`: id 1–256 characters without control characters, unique; `context_window` 1,024–10,000,000 tokens when present. |

A save with any invalid entry is rejected as a whole (`invalid_settings_payload`),
never partly applied.

URL normalisation trims whitespace and trailing slashes, strips a trailing
`/chat/completions`, `/completions`, `/messages` or `/models` (in that order, once),
and appends `/v1` when the remaining path is empty. The saved value is what Settings
shows afterwards, so the user sees the change.

### Identity

- Backend and alias are `custom:<id>`. The prefix keeps a user id such as
  `anthropic` from ever matching first-party branches (fixed endpoints, auth
  headers, hosted web search eligibility).
- The credential account is `custom_provider_<id>_key`. It cannot collide with
  `<models.dev-id>_api_key`, `custom_api_key`, `openai_api_key` or
  `anthropic_api_key`.
- Endpoints: Chat Completions posts to `{base}/chat/completions`; Anthropic Messages
  posts to `{base}/messages` with `x-api-key` and `anthropic-version: 2023-06-01`.

### Catalog

The provider's catalog is its configured model list, read from settings on every
request. It involves no HTTP request, no cache and no Models.dev metadata. Each
model is supported, has reasoning state `unknown` (requests carry no effort) and
carries the user's `context_window` as its context length. An empty list is a
valid, empty catalog: the picker offers nothing until a model is added.

**Discovery** is separate and never changes settings. `GET /models?backend=custom:<id>&discover=true`
makes the Host send `GET {base}/models` with the stored key, using the protocol's
auth headers. The response is parsed tolerantly: entries without a usable `id` are
skipped, `context_length`, `max_model_len` or `top_provider.context_length` become
the context length, and the result returns as an ordinary catalog snapshot. The
editor offers those ids for adding; it is also the connection test. Discovery
failures use the existing catalog codes (`catalog_unauthorized`,
`catalog_http_error`, `catalog_timeout`, `catalog_unavailable`,
`catalog_malformed`), plus `provider_insecure_endpoint` (below).

### Pins

A new run pins backend, alias, endpoint, protocol, model and account, as for every
provider. The context window comes from the model's `context_window` with source
`user_setting`, and is otherwise unknown (the 32K fallback). Edits apply to new
runs only. On rebuild, a deleted provider fails as `provider_selection_invalid` and
a model removed from the list fails as `model_unavailable`. Neither falls back.

Custom providers are never eligible for provider-hosted web search (ADR 0033).
Their catalog declares no image input, so image input follows the same model-name
check as any provider without a declaration. Native tool calling stays the only calling mode. A Chat
Completions error that says the model or server does not support tools becomes
`model_tools_unsupported`, so the user can choose another model. Ru does not retry
without tools.

### Transport rule

A key is sent only over `https`, or over `http` to a loopback host (`localhost`,
`*.localhost`, `127.0.0.0/8`, `::1`). Otherwise the request fails as
`provider_insecure_endpoint` before anything leaves the machine. The rule is
checked when a key is saved, at discovery, and when a run's snapshot is built or
rebuilt. It also applies to the legacy Custom OpenAI slot. Keyless `http` to any
host stays allowed, for LAN servers without authentication.

### Legacy slot

The existing Custom OpenAI backend keeps its settings, account and pins untouched
and remains editable while configured. New endpoints are added as named custom
providers. No key or setting is migrated.

## Consequences

- Any number of local or relay endpoints, each with its own name, key and
  protocol. A model works even when the server publishes no `/models`.
- Context windows for local servers can be set exactly, and Ollama's small
  defaults no longer go unnoticed.
- Keys can no longer leak over plain HTTP to another host. A user running a keyed
  server on the LAN over HTTP must remove the key or use HTTPS. This is a
  deliberate, visible break.
- Reasoning effort is unavailable for custom providers, and image input rests on
  the model-name check, until a later decision lets definitions declare both.
- The TUI's provider picker does not list named custom providers yet. Runs it
  starts with one already selected in settings work, because selection is
  Host-owned.

## Alternatives considered

- **Keep one slot and add a name.** It fixes nothing about relays, multiple servers
  or the Anthropic protocol.
- **Live `GET /models` as the runtime catalog.** It keeps every failure mode listed
  in Context. Discovery keeps the convenience without making runs depend on it.
- **Bare `<id>` as backend and alias.** User ids would share a namespace with
  first-party aliases that many branches test by name.
- **Migrating the legacy slot into the list.** It would move a stored key between
  accounts, which providers.md rules out, and change existing pins.
