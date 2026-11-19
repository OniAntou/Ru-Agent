---
status: accepted
date: 2026-11-19
---

# A provider route table for Models.dev providers

Ru reached every Models.dev provider over chat completions at `{api}/chat/completions` and
read only the entry's `api` root. Models.dev also names the AI SDK package that serves each
provider (`npm`), and that package fixes the wire protocol. Two failures followed:

- Providers published with `@ai-sdk/anthropic` speak Anthropic Messages. MiniMax (four
  entries), Subconscious, FreeModel, Thinking Machines and Rayline were called at
  `…/chat/completions`; MiniMax answers 404 there and 401 at `…/messages`.
- Twenty-five providers carry no `api` because their own SDK knows the URL. xAI, Groq,
  Mistral, Google, Cerebras, Together, DeepInfra, Cohere and Venice were never offered,
  although each serves an OpenAI-compatible root.

## Decision

**The protocol comes from the entry's package, through one route table.** When the catalog
is read, `ProviderRoutes` resolves each entry to a protocol and an endpoint root:
`@ai-sdk/anthropic` gives `anthropic_messages`; every other package Ru can reach with a key
keeps `chat_completions`, which is what each such entry was already served over. A table
entry can supply a root Models.dev leaves out, override the protocol, or mark a provider
whose model list must come from Models.dev. The table holds the nine roots above, each probed
without a credential on 2026-11-19. Perplexity is left out: none of its models call tools,
and every Ru run needs them. Bedrock, Vertex, Azure, watsonx, SAP and GitLab need
credentials other than an API key and stay out. Models.dev's own `openai` and `anthropic`
entries also stay out: their credential accounts are Ru's first-party ones.

**The protocol is pinned like the rest of the selection.** A run's Models.dev pin may now
carry `anthropic_messages`; the snapshot rebuilds from the pinned protocol, so a later
change in the catalog never switches a running selection. Provider-hosted search (ADR 0033)
still requires the first-party Anthropic or OpenAI alias, so an Anthropic-compatible gateway
never receives it.

**An Anthropic-compatible gateway lists its models from Models.dev.** Their list routes and
auth vary by vendor, so Ru does not call them. The list carries Models.dev's context window,
output limit and prices, but no reasoning-effort options: those become chat-completions
fields. The same applies to a table route marked for it (Google) and to the existing
Cloudflare override, which now also receives the published windows. A reasoning effort saved
for such a model while its route still offered levels runs at the model's default instead of
failing the run as `reasoning_unsupported`; a model that publishes levels but not the saved
one still fails.

**A key for such a provider is checked on the generation route.** Its list never carries
the key, so the key check posts an empty body (`{}`) to the generation URL, which nothing
can run. When the same URL refuses the anonymous body with 401/403, a keyed 401 refuses the
key and a keyed 400/404/405/415/422 accepts it; any other answer saves it unchecked. xAI
answers a wrong key with 400 on its list, so its key is checked at `/v1/api-key` like
OpenRouter's and Cloudflare's.

## Consequences

- Nine more providers appear in both clients, and Anthropic-compatible gateways are called
  at `{api}/messages`. Request shape is covered by tests and live probes with made-up keys;
  no run with a real key has been made yet.
- xAI also answers a wrong key with 400 during a run, so its stored key status is never
  recorded as `rejected`.
- No credential, settings or pin format changes; existing pins keep `chat_completions`.
- A provider whose package changes in Models.dev is reached with the new protocol on the
  next new run only.
- Not covered: providers that need a cloud identity, Google's native Gemini protocol (its
  OpenAI-compatible root is used), and OpenAI Responses for third-party gateways.
