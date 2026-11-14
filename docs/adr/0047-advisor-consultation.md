---
status: proposed
date: 2026-11-14
---

# Advisor: the main model consults a second pinned model on any provider

## Status

Proposed. Scope chosen by the owner on 2026-11-14: the main model decides when to
consult, with one Host recommendation before finishing (decision 4), the advisor
works with every signed-in provider, and Ru adds no advisor-specific limit. Phases 2 and 3 are implemented: selection and pin in the
TUI and the desktop app, and the `advisor` tool with user-named model and effort.
Usage reporting (phase 4) is not. A live run on 2026-11-14 confirmed the
consultation end to end (main model `longcat-2.5-free` and advisor
`deepseek-v4.1-flash`, both on OpenCode Go); accepting this ADR is the owner's call.

## Context

Coding agents commonly let a cheaper or faster executor model ask a stronger model
for guidance mid-run. Anthropic offers this as a server-side tool
(`advisor_20260301`, beta), but only for first-party Anthropic models, with a fixed
pairing table, and recent advisor models return encrypted advice that Ru could
replay but not show. The owner wants the same behaviour on every provider Ru
supports, so the consultation has to run in Ru.

Existing decisions this touches:

- **No silent substitution** (AGENTS.md). Provider, model, protocol and tool
  identity stay pinned for a run.
- **One execution authority** ([ADR 0009](0009-broker-execution-authority.md)).
  Side-effecting tools execute in the Broker; the Host intercepts `ask_user` and
  never dispatches it.
- **No implicit transcript forks for children**
  ([ADR 0022](0022-delegated-subagent-runs.md)). Subagents get a brief, not the
  parent transcript.
- **A pinned profile must not silently gain tools** (`AgentToolProfiles`). Opt-in
  tools such as `computer` are advertised only for runs that pinned the opt-in
  ([ADR 0031](0031-computer-use-through-the-broker.md)).
- **Context Checkpoint** already makes a tool-less, bounded model call from the
  Host through `IContextCheckpointSummarizer`. It is the precedent for a model call
  that is not a run step.

## Decision

1. **The user selects the advisor; a run pins it.** `/advisor` in the TUI and in
   the desktop composer (which opens the model picker; `/advisor off` clears it) save the Host settings
   `advisor_backend` and `advisor_model` as a pair; two nulls clear them, and a half
   is rejected.
   The advisor may be on a different provider from the main model, and may be the
   same model. When a new root run pins its provider selection, the Host resolves
   the advisor into a secret-free selection (backend, alias, protocol, base URI,
   model, context window, maximum output) and records it in the
   `_ru_advisor_v1` request-option marker. The marker is removed before any option
   reaches a provider. A malformed marker fails closed as
   `provider_selection_invalid`. An advisor that cannot be resolved when the run is
   pinned (signed out, catalog unavailable, model not listed) is recorded as
   unavailable; the run starts on its own selection and a consultation reports
   `advisor_unavailable` (decision 9). With no advisor selected, a new root run
   pins `{"selected":false}`, so it can still consult a model the user names
   (decision 2a). Runs pinned without any marker, including every run pinned
   before this change, never see the tool. A settings change applies to new runs
   only. The advisor's reasoning effort is the one saved for that model with
   `/reasoning`, or the model's default; it is pinned with the advisor. Credentials
   are read from existing credential storage at call time and never stored in the
   marker.

2. **The tool is Host-intercepted and read-only.** A new Protocol tool definition
   `advisor` takes two optional arguments, `model` and `effort` (decision 2a);
   called without them it consults the pinned advisor. Its descriptor row uses
   `AgentToolAdapter.HostIntercepted`, so it has no Broker manifest entry and no
   binding, and the Broker manifest digest does not change. The tool is listed in
   the common profile inventory but advertised only when the run pinned the marker
   and the run is a root run, the same gate `computer` uses.

2a. **The user can name the advisor model and effort in the request** (owner's
   decision, 2026-11-14). When the user's prompt asks for a particular advisor
   model or effort, the main model passes it as `model` or `effort`. The Host
   accepts a value only when it occurs in the run's original authenticated user
   prompt, the rule [ADR 0022](0022-delegated-subagent-runs.md) uses for a child
   model: model output, tool output, workspace instructions and skills cannot opt
   in. A requested model resolves only to an exact catalog ID (case and
   punctuation tolerant, as for children) of a configured provider; no match is
   `advisor_model_unavailable`, several are `advisor_model_ambiguous`, and an
   unreadable catalog is `advisor_catalog_unavailable`, with no substitution. A
   requested effort must be one the advisor model publishes, else
   `advisor_effort_unsupported`. The request is resolved again from the user's own
   words at each consultation; the name cannot change within a run because the
   prompt cannot. Without these arguments the pinned advisor and its saved effort
   apply.

3. **Children never consult.** Children inherit the parent's exact pinned
   selection (ADR 0022), so the marker travels with it. The projection hides the
   tool from any run with a child role, and a child call is denied with
   `advisor_subagent_denied`, mirroring `subagent_ask_user_denied`.

4. **The model decides when to consult; Ru recommends once before finishing.** The
   tool description tells the model to consult before committing to an approach,
   when stuck, and before declaring the task done, and to weigh the advice rather
   than obey it. Ru never makes a consultation on the model's behalf and never
   requires one before completion.

   Amended by the owner on 2026-11-14 after live evidence that a weak executor
   never consults unprompted: the Host recommends, it does not force. A final reply
   that would complete a root run is held once (completion policy R10,
   `advisor_recommended`) when the user selected an advisor that resolved at pin
   time, the step advertised `advisor`, and the latest tool step was not an
   `advisor` call (answered or failed). The note recommends consulting before
   finishing and says plainly that repeating the final reply completes the run.
   The hold is once per run, like the other one-time completion nudges; children,
   runs without tool work and runs whose user selected no advisor are never held.
   A first design in which the Host itself sent the proposed final reply to the
   advisor and held it on a `REVISE` verdict was rejected by the owner the same
   day because it forced the consultation.

5. **The call completes inline, not as a pause.** Unlike `ask_user`, an advisor call
   does not park the run. Harness.Core hands the call to a Host service behind a new
   interface next to `IContextCheckpointSummarizer`. That service makes one
   tool-less request to the pinned advisor, and the result becomes an ordinary
   native tool result with a durable encrypted receipt.

6. **`advisor` must be alone in its batch.** Sibling results would be missing from
   what the advisor sees. A batch that mixes `advisor` with another call is denied
   with `advisor_must_be_alone`, as `agent_wait` already is
   (`subagent_wait_must_be_alone`).

7. **What the advisor sees.** The request carries a Ru-authored advisor directive
   and the run's active model context as quoted transcript: the system prompt,
   workspace instructions, user messages, assistant text, tool calls and Tool Output
   Projections, including Context Checkpoint summaries in place of compacted
   history. It is rendered as quoted context in the advisor's protocol, in the
   Session Run Handoff manner, so the advisor reviews the work instead of
   continuing it. Excluded:
   - Provider Continuations and every provider-native block (reasoning, encrypted
     content, provider search blocks). They are bound to their own provider and
     run.
   - Images. A placeholder line names each omitted image; image forwarding is a
     follow-up.
   - The `advisor` tool itself. The advisor gets no tools, so it can neither act nor
     consult recursively.

8. **No advisor-specific limits.** Ru sets no cap on calls per run, no output cap
   below the advisor model's own maximum output, and no transcript budget of its
   own. The bounds that remain are the ones Ru cannot remove:
   - **The advisor model's context window.** When the transcript does not fit, the
     oldest complete exchanges are dropped first. The system prompt, workspace
     instructions, the latest user request and the newest exchange are protected.
     The result states that history was shortened. If even the protected part does
     not fit, the call returns `advisor_context_too_large`.
   - **The 1,000,000-character prompt bound** of every model request; the transcript
     is shortened the same way when it exceeds it.
   - **Run Stop and cancellation** cancel an advisor call in flight.
   - **The provider request timeout and the bounded transient retries** of
     `ProviderRetryPolicy` apply as for model steps.
   - **The descriptor's `MaxOutputBytes`** and the Tool Output Projection bound how
     much of the advice enters the main model's context; the full advice stays in
     the receipt.

9. **Failures are named, and the run continues.** A failed consultation returns a
   tool result with a named code instead of failing the run, so the main model
   decides what to do next. Codes: `advisor_unavailable` (provider removed or
   signed out since the pin), `advisor_provider_failed` (after bounded transient
   retries), `advisor_refused` (the provider declined), `advisor_context_too_large`,
   `advisor_incomplete` (stopped before a complete answer), `advisor_interrupted`,
   `advisor_not_selected` (no advisor pinned and none named by the user) and the
   codes of decision 2a. There is no retry on another model and no fallback to the
   main model.

10. **Interrupted calls are not replayed.** An advisor call has no side effects, but
    each call costs money. The Host never re-sends a consultation: a run restarted
    in the middle of one goes through ordinary run recovery, which does not
    re-execute a step's tool calls. An advisor call found in a resumed batch (after
    an approval or input wait) returns `advisor_interrupted`, and the main model may
    call again once; the repeated-call guard treats that code like an
    infrastructure failure that never ran.

11. **Advice is untrusted model input.** It grants no permission, approves nothing,
    cannot create Session Grants or approval tokens, and is never treated as
    evidence of verification. A run's verification label still comes only from
    declared checks ([ADR 0039](0039-advisory-verification.md)).

12. **Cost is visible.** Advisor token usage is recorded per call under the
    advisor's identity and shown separately from the main model in run usage and
    `/status`.

## Invariants

- Exactly one execution authority. The advisor call is a model request made by the
  Host, like a Context Checkpoint; it executes no tool and reaches no Broker
  adapter.
- Both identities stay pinned for the run: the main selection and the advisor
  selection. Neither substitutes for the other.
- **Affected invariant: data leaving the machine.** Today a run's transcript goes
  only to the main provider. With an advisor pinned, the advisor's provider also
  receives it, including file contents, terminal output and MCP output the run has
  read, and that provider may be a custom endpoint. Selecting an advisor with
  `/advisor` is the user's consent to this flow for new runs. Only a provider the
  user configured can be pinned: one with a stored key, or a custom endpoint the
  user added that needs none. A provider that needs a key and has none pins as
  unavailable. Provider-native blocks and images do not
  cross (decision 7). Credentials are already redacted from diagnostics; tool
  output is sent as the main provider already receives it.
- Workspace instructions, skills, tool output and model output cannot enable the
  advisor, select its model or effort, or change the pin. The main model only
  relays a model or effort that the user's own prompt names (decision 2a).

## Alternatives

- **Anthropic's server-side advisor tool only.** Smallest change and no new data
  flow, but first-party Anthropic only, with fixed pairings and unreadable advice
  on recent advisor models. It can be added later as an optimization for runs
  whose main and advisor selections are both first-party Anthropic and form a valid
  pair, under the ADR 0033 pattern.
- **An oracle subagent that receives a brief.** Reuses the read-only review role
  and avoids forwarding the transcript, but the main model must write a good brief,
  and ADR 0022 only allows a different child model when the user names it in the
  root prompt. The owner chose transcript forwarding.
- **Planner/executor modes (Aider architect mode, Cline Plan/Act).** A fixed
  per-phase split rather than consultation on demand; Ru's `plan` preset already
  covers planning.
- **Fixed caps on calls, output and transcript size.** Rejected by the owner.
  Decision 8 keeps only the bounds the provider or run lifecycle imposes.

## Consequences

- Each consultation costs a full transcript's input tokens on the advisor's
  account, and with no call cap a model that consults often can be expensive. The
  usage line (decision 12) is how the user notices.
- The Protocol tool catalog gains one row, and Protocol-definition and descriptor
  checks change ([ADR 0038](0038-tool-descriptor-table.md)). The Broker manifest
  does not.
- The settings payload gains `advisor_backend` and `advisor_model`; the public
  route list does not change.
- AGENTS.md gains the advisor in its "No silent substitution" and data-flow
  wording when the implementation lands, not before.

## Implementation notes

- An advisor call does not reserve a model step: it is answered inside the tool
  step that called it, so it counts once as a tool call, like any other.
- The transcript is rendered from the same portable history the model runner
  builds for chat-completions requests (`BuildConversation`), as Markdown
  sections inside a `<transcript>` block; the Session Run Handoff format was not
  reused because it is bounded for a different purpose. When the request does
  not fit, the first two entries (normally the instructions and the task) and the newest
  eight are kept and the rest are dropped oldest first, found by binary search.
- The repeated-tool-call guard treats `advisor` like any other call: the same
  call repeated with no change in between gets the usual repair note, except
  that one retry after `advisor_interrupted` is allowed.
- Shortening keeps the user's latest request wherever it sits, so a continued
  conversation cannot lose the current task.
- The advisor's system prompt is Ru's base agent prompt, composed by the model
  runner from a prompt context with the run's environment under the advisor's
  model name and no capabilities or instruction blocks; the agent's own
  instructions are quoted in the transcript, and the advisor role is set by the
  directive in the request text. A dedicated advisor system prompt would need
  the model runner to accept one and is a possible follow-up.
- The transcript quotes everything the agent's system prompt told it except Ru's
  base contract, which is already the advisor request's own system prompt: the
  agent's model and preset, each tool's one-line purpose, the user, workspace
  and plugin instructions and the runtime references. Runtime snapshots stay in
  the transcript. The directive asks for prioritized, complete advice with no
  length cap; accuracy comes before token savings (owner, 2026-11-14).
- A consultation is recorded as a `model.usage_recorded` event with safe code
  `advisor_usage` and the advisor's model name. Usage readers bill it on that
  model's rates and never read it as the run's context fill.
- A user-named model goes through the child-model resolver (ADR 0022), so the
  same evidence rule and catalog match apply, and its codes are mapped to the
  advisor's.

## Open points

- `context-management.md` mentions shared recovery and model-call budgets. Ru
  adds no advisor budget (decision 8); whether a future run-level model-call
  budget should count consultations is left to that budget's own decision.
- Live evidence, 2026-11-14: told nothing about the advisor, `longcat-2.5-free`
  never called it, so "the model decides" alone gave a weak executor almost no
  benefit. The one-time recommendation (decision 4, as amended) is the owner's
  answer. A live run the same night (same models, unprompted pricing fix) held
  the final reply once with `advisor_recommended` after the fix; the next main
  model step then failed with `provider_rate_limited` on the free main model, so
  whether a weak model follows the recommendation is still to be measured.
- Whether the owner's target ordering holds (weak < weak + advisor < strong <
  strong + advisor) is unmeasured; it needs a four-arm benchmark cohort.

## Implementation phases

Each phase is built, tested and committed on its own.

1. This ADR and the glossary entry.
2. The setting, the `_ru_advisor_v1` pin, `/advisor [off | <model> | <provider> <model>]`
   in the TUI (with the `--json` `interactive_required` refusal for the bare form)
   and the desktop selection (first a Settings card, moved to the composer's `/advisor`
   command on 2026-11-14). No tool yet. Implemented.
3. The tool definition and descriptor row, projection gating, Host interception,
   batch rule, transcript rendering, the advisor call service, receipts, named
   failures and restart handling, with allowed and denied tests. Implemented,
   together with decision 2a.
4. Usage reporting in `/status` and the desktop usage view, AGENTS.md, CONTEXT.md,
   operations docs, the CLI README and CHANGELOG.
