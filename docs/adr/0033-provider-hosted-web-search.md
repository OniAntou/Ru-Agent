---
status: proposed
date: 2026-10-28
---

# Provider-hosted web search for first-party Anthropic and OpenAI runs

## Context

Models struggle to research with Ru. `web_search` reads the HTML result pages of
free engines (Google, DuckDuckGo, Bing, Mojeek) and those engines refuse automated
clients. Run through Ru's own adapter on 2026-10-28, eight ordinary queries per
engine succeeded 0/8 on Google, 1/8 on DuckDuckGo, 4/8 on Bing and 0/8 on Mojeek
(403). Making the scraper look more like a browser would be working around those
engines' bot defences, which Ru does not do. A keyed search API needs an account
the user does not otherwise have.

Claude Code and Codex search without an extra key because the search runs at the
model provider: Anthropic's Messages API offers a server-side `web_search` tool,
and OpenAI's Responses API a built-in `web_search` tool. The provider runs the
query, gives the results to the model inside the same response and bills the
searches to the account the user already pays for. Ru already has adapters for
both protocols against their first-party endpoints.

The invariants this has to respect:

- **One execution authority** ([ADR 0009](0009-broker-execution-authority.md)).
  File, terminal and network tools execute in the Broker behind `IToolGateway`,
  and every Broker web request passes `WebPolicy`.
- **No silent substitution.** Provider, model, protocol and tool identities stay
  pinned for a run.
- **Policy is the user's.** Web tools are automatic in `plan`, `standard` and
  `full_access`, but an explicit ordered rule may ask for or deny them.
- **Durable, replayable history.** Native tool steps replay their provider
  content blocks through `ProviderContinuation`, bounded to 128 KiB and 32 items.

## Decision

1. **Eligibility is pinned with the provider selection.** When a new run pins
   its selection, it records the `_ru_provider_web_search_v1` marker when all of
   the following hold. The marker is removed before any option reaches a provider.
   Runs pinned without it, including every run pinned before this change, keep
   the Broker's `web_search`.
   - The selection is first-party Anthropic (`anthropic_messages` at
     `api.anthropic.com`) or first-party OpenAI (`openai_responses` at
     `api.openai.com`), with native tool calling.
   - The model passes Ru's own conservative name rule. The provider catalog Ru
     reads does not record the capability, and a model the provider rejects would
     fail the run, so the rule stays narrow: Claude models of generation 4 and
     later (`claude-<family>-4…`), and OpenAI `gpt-5` and later, `gpt-4.1` and
     `gpt-4o`, without nano, audio, realtime, transcription, speech, image or
     search variants.
   - For OpenAI, the pinned reasoning effort is not `minimal`; OpenAI does not run
     search for gpt-5 at that effort.
   - The Host setting `provider_web_search_enabled` is not `false` (the default
     is on). A change applies to new runs only.

   The pinned snapshot removes the marker before the provider option validator
   sees it, like the image-input and computer-use markers.

2. **The run's policy decides whether the marker is used.** Provider search is
   used only for a root run whose tool profile advertises `web_search`, and only
   when the Broker's own evaluator, given the run's bound policy snapshot, would
   allow a `web_search` call without an approval. The Host asks through the
   gateway (`AllowsWithoutApproval`), which evaluates exactly as a real proposal
   does but issues no token and records no pending approval, so the policy logic
   stays in the Broker. A `web_search` policy context carries no path, executable,
   argv or working directory, so one evaluation answers for every query.
   Otherwise the run keeps the Broker tool and its approvals. A server-side search
   cannot stop for an approval, so a rule that asks for one turns provider search
   off rather than being skipped. That one decision feeds both the advertised
   tool list and the request flag the adapters read, so the two can never disagree.

3. **One search tool per run.** When provider search is used, the Broker's
   `web_search` is not advertised. Both providers name their tool `web_search`,
   and two tools with one name would leave the model guessing. The request
   declares the provider's tool instead:
   - Anthropic: `{"type": "web_search_20250305", "name": "web_search", "max_uses": 5}`.
     The basic version calls search directly, without the code-execution step of
     the dynamic-filtering versions, and is eligible for zero data retention.
   - OpenAI: `{"type": "web_search"}`.

   `web_fetch` stays a Broker tool in every run, so reading a page the model
   found still passes `WebPolicy` and runs on the user's machine.

4. **The search runs outside the Broker.** This is the second documented web
   exception, next to the HTML engine race. The provider's servers send the
   query and read the results; nothing runs on the user's machine and no request
   reaches the local network. The query and results travel inside the model
   request and response that the run already sends to that provider, and the
   provider bills each search on the user's account. `max_uses` bounds Anthropic
   searches per step. Search results stay untrusted model input, exactly as Broker
   web results are.

5. **Streams accept the search content.**
   - Anthropic streams `server_tool_use` blocks (input through
     `input_json_delta`), `web_search_tool_result` blocks and `citations_delta`
     events on text blocks. A search error arrives as a result block and is left
     to the model. A `pause_turn` stop is continued inside the same model step:
     the adapter sends the paused content back as an assistant message with the
     same tools, at most three times, and then fails as `provider_search_paused`.
     A cited answer arrives as many short text blocks, so the stream allows up to
     256 blocks; only a continuation keeps the tighter bound (decision 6).
   - OpenAI streams `web_search_call` items, whose events are already ignored as
     unknown names; the completed response carries the items and the message's
     `url_citation` annotations.

6. **Replay keeps what the provider requires and drops what it cannot use.**
   - A tool step's continuation keeps its `server_tool_use` and
     `web_search_tool_result` blocks, and text blocks keep their citations,
     unchanged, because Anthropic rejects modified `encrypted_content`.
     OpenAI continuations keep their `web_search_call` items in order, because a
     reasoning item must keep the item that followed it.
   - A `server_tool_use` without its result in the same response is dropped.
     Anthropic defers such a search when it is mixed with Ru's tool calls and runs
     it on the next request; dropping it means that one search does not run, and
     the model can search again.
   - A continuation that would exceed its bounds keeps only text and tool calls,
     dropping the search blocks and citations together, instead of failing the run.
   - A request without provider search, such as a later run on another model,
     drops every search block, citation and `web_search_call` item from replayed
     history.
   - A final answer is stored as text, as before, so search results do not cross
     into later steps after the answer or into later runs. The answer keeps what
     the model wrote about its sources.

7. **Sources are shown.** Both providers require citations to be visible when
   their output is shown to users. An answer that cites web results ends with a
   deduplicated `Sources:` list of Markdown links, built from Anthropic's
   `web_search_result_location` and OpenAI's `url_citation` entries. Because the
   list is part of the answer text, it also survives into later runs.

8. **Failures are named.** A 400 whose error message names web search, for a
   request that declared the tool, fails as `provider_web_search_unavailable`
   (for example when an administrator turned search off), with copy that points
   at the setting.

9. **Visibility.** This version adds no activity row for a provider search; the
   model's narration and the Sources list name what it used. A feed row for
   provider searches is a follow-up.

## Verification status

The request shapes, stream events, `pause_turn` handling and replay rules follow
the providers' documentation as of 2026-10-28 and are covered by recorded-stream
tests. Three replay rules are not documented by the providers and stay unverified
until a real run confirms them; this ADR stays `proposed` until then:

- that OpenAI accepts `web_search_call` items as input with `store=false`;
- that Anthropic accepts history from which an unanswered `server_tool_use` was
  dropped;
- that Anthropic accepts history from which all search blocks and citations were
  dropped together.

## Consequences

- Runs on first-party Claude and OpenAI models search through their provider and
  no longer depend on HTML scraping. Other providers, custom endpoints and
  gateways keep the Broker's `web_search`.
- Each search costs money on the user's provider account, and a step may run up
  to five on Anthropic. The setting turns it off for new runs.
- The search request and its results leave the Broker's audit path. The run's
  history keeps the provider's blocks for tool steps, but the activity feed does
  not show them yet.
- A model the eligibility rule includes but the provider rejects fails the run
  with the provider's error rather than falling back silently. The rule is kept
  narrow for that reason, and the setting is the escape hatch.
- History projection gains provider block types and must drop them for any
  request that does not declare provider search.

## Alternatives considered

- **Make the scraper look like a browser.** Rejected: it works around the
  engines' bot defences, and it would stay fragile.
- **A keyed search API (Brave, Tavily, Google Programmable Search).** Works for
  every provider but needs an account and a key the user does not have. It can
  still be added later as a Broker backend.
- **Exa's hosted MCP endpoint, as OpenCode does.** Reported to work without a key
  only through OpenCode's own provider and to be rate limited; not something Ru
  can rely on.
- **Keep both tools and let the model choose.** Rejected: both are named
  `web_search`, and the scraped one is the one that fails.
