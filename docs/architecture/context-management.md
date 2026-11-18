# Context management

The Public Host builds model context from encrypted durable history. Context is
evidence, not execution authority: summaries, workspace instructions, receipts
and snapshots cannot grant paths, tools, approval or a different provider/model.

## Structured checkpoints

Provider-backed compaction normally uses one auxiliary request plus one optional
format repair to the run's pinned provider/model. It reuses protocol-specific
history projection and opaque
continuations. Its final user message requests eight ordered sections: request
and intent, decisions and constraints, files and changes, results and failures,
pending work, current work, next step, and critical context. Previous checkpoints
must be merged with newer facts rather than copied indefinitely.

The summary must be nonempty, contain every section in order with bullet content,
contain no NUL, and fit 8,000 UTF-16 characters. There is no silent truncation.
Both checkpoint and deterministic summaries are wrapped by `ContextCompactionFrame`
in a `[context_compaction … untrusted=true]` frame; frame markers inside the body
are neutralized so summarized history cannot close or reopen its own frame.
The auxiliary prompt keeps only pinned tool definitions referenced by selected
native call/result history, omitting unrelated schemas and duplicate tool-result
references. Only current nested instruction references may remain beside the source.
The auxiliary request has a 60-second deadline and an output cap of at most
4,096 tokens (never above the pinned cap). Exact request preflight still applies.
It uses the model's declared lowest reasoning effort when available for this
auxiliary request; ordinary task requests retain their selected effort.
Empty, malformed, truncated, filtered or tool-producing responses return named
`checkpoint_*` failures; no returned tool is dispatched. A malformed format gets
one repair per source span using the original covered history and at most 4,000 characters of
the rejected candidate as untrusted data. Input-window rejection can select a
smaller complete source span: at most three
spans and four admitted calls total per compaction, including format repairs,
counted by admission rather than by the window counter. An automatic budget
window therefore does not extend the allowance.
An unchanged or irreducible span is not retried. Other failures are not
format-retried. Production does not
substitute deterministic excerpts after a failed provider summary. Test/scripted
runners without the summarizer interface retain deterministic compaction.

Before each dispatch, including repair, a durable `checkpoint_started` note reserves one model call in
the run's budget and, when configured, its family budget. No tool budget is
reserved. Reported usage is stored through `ModelUsageRecorded` even when the
returned checkpoint text later fails validation. Prompt-composition receipts retain private block digests
and never expose raw instruction or prompt text. A crash after admission keeps
the spent reservation; it cannot make a dispatched call free. A failed summary
does not replace original history. The checkpoint completion cap is up to 32,768 tokens,
bounded by the run's pinned output reserve and a quarter of the model's context window
(so the summarizer always leaves the rest of the window for the conversation it replays).
Reasoning tokens draw from the same completion budget, and a summary cut off by the old
4,096 cap used to fail the compaction as `checkpoint_incomplete`. A truncated
response is retried once with a shorter directive; only a second truncation is a
permanent `checkpoint_incomplete`. An exhausted format repair or permanent checkpoint
failure stops the run with its named error. Transient provider failures, operator-repairable account limits and the
checkpoint deadline also fail the run, root or child, with their code; there is no
Continue gate (ADR 0048) and nothing loops automatically. A run that cannot fit its
context after compaction fails as `context_limit_reached`. Budget exhaustion follows
ordinary run policy: root and child runs open automatic windows, and a reservation
that cannot be granted fails the run; no extra call dispatches without an admitted
reservation.

A successful summary must shrink the selected span. It is encrypted and
committed with covered ranges and an input digest through the existing
compaction transaction. Concurrent run changes cause a commit conflict rather
than landing stale text. Full receipts remain available in durable storage.
Automatic compaction targets a recent tail priced at 16% of available prompt
tokens, extending protection to whole call/result groups while leaving at least
one eligible old unit available to compact. The target is the whole prepared
request: system instructions and tool declarations are counted as fixed prompt
overhead the planner cannot rewrite, so it does not keep covering history a
target it can never reach. System instructions, non-dialogue
base context,
the latest actual user request, unresolved calls and the latest Host instruction-scope
snapshot are protected. Complete historical `base:turn:*` user/assistant exchanges
may be summarized; encrypted originals remain unchanged. Selection and replay use
snapshot order because base and run-local sequence numbers may overlap. The
fully prepared request is measured again after
compaction; fixed prompt overhead can still leave a request too large.

## Bounded admission and overflow recovery

Planning prices units once and constructs one final plan, capped at 256 distinct
source ranges before checkpoint dispatch. Provider-backed selection targets a
source span around half the available prompt budget; an indivisible larger unit
still faces exact request preflight. Automatic preflight may commit at most 16
shrinking compactions while the request would not fit, reassembling and
remeasuring after each. Once the request fits, the loop stops: a fitting request
gets at most one proactive pass, and that pass is skipped when fixed prompt
overhead alone exceeds the compaction target. A checkpoint failure in that
proactive pass is optional and leaves the run working, because the request already
fits; only a compaction required to make the request fit can fail the run with
its code. Unavailable or nonshrinking plans stop the loop; irreducible input
fails by name.

Recognized HTTP/SSE provider context errors carry `context_window_exceeded`.
Invocation retracts partial text, the compaction owner reduces context and a new
budgeted step prepares again with the same provider/model/protocol/tool pin.
At most two durable overflow recovery notes are admitted per run, also subject
to shared recovery and model-call budgets. This is not a transport retry of the
same oversized request. Rejected tool proposals never execute.

## Active context projection

A source-local cache retains active base/run entries, the latest call association,
bounded directory hints and verification evidence. Warm loads seek new context
rows and newer compactions in one read transaction; they do not read all covered
ciphertexts. Images are queried only for active receipts; current Work State is
refreshed separately. Restart, eviction or invalidation rebuilds from originals.

Verification receipts retain the recorded tool-request time and identity. Context loads project declared checks from original receipts only. They do not compare checks with the workspace change journal, so there is no journal high-water lookup and no workspace-wide invalidation for verification (ADR 0039). The journal itself stays owned by workspace coordination ([ADR 0008](../adr/0008-shared-workspace-coordination.md)).

Successful structured file-tool receipts trigger a path-specific observation
before the tool returns to orchestration. This shares a persistence gate with
watcher callbacks, which re-read the path against the latest acknowledged hash.
A delayed notification for the same content cannot become a new post-check
mutation. This observes results after Broker execution; it adds no execution
adapter or permission decision to the Host and does not rescan the entire
workspace on every step.

Repair-family metadata and `supersedes` retirement are no longer part of verification (ADR 0039). The `supersedes` argument has been removed from the launch schemas; old receipts that carry it are ignored. A failed or pending check stays outstanding until the same identity passes. Original receipts remain in durable history, and the projection keeps the latest result for each identity.

The cache admits at most eight runs, 32 MiB estimated weight per run and 64 MiB
total, with two-minute access validity. Native Responses public-message metadata
reserves its full envelope bound in that estimate, including annotations beside
small visible text, without traversing its JSON on each warm load. Native-heavy
contexts may therefore bypass the cache conservatively. Every load checks key identity, base count,
run version/sequence and mutation revision. Schema v31 triggers advance the
revision when existing payload/event/compaction rows are changed or deleted,
including updates outside the write set. Invalidated projections rebuild with
existing audience/authentication checks; parsed-row fingerprints remain checked
when decoding rows. No plaintext cache or duplicate original log is persisted.

## Tool excerpts

During context assembly, unstructured text results over 8,192 characters receive
a stable model-only excerpt: the first 4,096 and last 1,024 characters, with a
visible middle-omission notice and Unicode-safe boundaries. Original receipt
bytes and call/result identities remain intact. Valid JSON objects/arrays are left to the
existing structured output projector, preserving paging and cursor semantics; the only
change to them is that top-level 64-hex `sha256`, `before_sha256` and `after_sha256`
values are dropped (the model never passes them back, and session work state reads
them from the stored receipt).
This stable projection happens before request budgeting or checkpoint generation;
it does not rewrite previously admitted history merely because a result aged.

For unstructured text only, up to six diagnostic-like lines from the omitted
middle supplement the unchanged head/tail excerpt. Selection scans at most
1 MiB of characters and retains at most 300 characters per line with Unicode-safe
boundaries. It is explicitly labelled heuristic and incomplete; the original
receipt and existing output cursors remain the source for full diagnostics.
Structured JSON and small output are unchanged. These samples are context,
not independent verification evidence.

Outstanding-check guidance lists each outstanding declared check with its provider call ID, authority-recorded working directory, code and next action: read a pending background check's terminal execution until it exits, repair and rerun a failed check, or report the observed exit of an expected-exit mismatch without forcing the expected code. Inspecting logs does not satisfy a check. The text is bounded and never establishes equivalence between tests.

## Provider usage anchors

The Generation Kernel may increase a heuristic input estimate using observed
input usage plus an estimate of newly appended wire-history bytes. Cache buckets
are included once according to the adapter's explicit inclusive/exclusive flag.
Missing input counts, unknown accounting or a mismatched reported model cannot
seed an anchor. Estimates never fall below the full current heuristic.

Anchors are isolated by run and require matching protocol/endpoint, exact
non-history request envelope and an unchanged wire-message prefix. A rewrite,
compaction, instruction/schema/model change or expired anchor invalidates it.
Only text requests with history-owned prompts participate; image requests use
the existing estimator. The in-memory cache is bounded to
256 scopes and a 30-minute access validity window; it stores digests and counts,
not prompts or credentials. It is an estimate, not exact tokenization or a
billing ledger, and does not survive Host restart.

Beside the per-run anchor, each protocol/endpoint/model keeps one calibration: the
ratio of reported input to the bytes/4 heuristic of the same request, learned only from
requests of at least 4,096 heuristic tokens and clamped to 1.0–2.0. When no valid anchor
exists (a new run, a compaction, an image, a rewritten prefix or an expired anchor) the
heuristic is scaled by that ratio, and the anchored path scales only the appended delta.
It never lowers an estimate. Without it a tokenizer that bytes/4 undercounts (DeepSeek
by about 25% on a long coding run) pushed the 80% compaction trigger to about the full
window, so the provider rejected the request before Ru compacted.

### What a context meter shows

The desktop and terminal meters share `RequestContextReading` and change only the figure they
display; admission and compaction keep the estimate above. The reading is the provider's prompt
count for the run's latest request. Between reports:

- A step's `agent.usage_estimate` carries the request's heuristic breakdown. The meter multiplies
  its total by the route's last provider-to-heuristic ratio (from the previous report, bounded to
  0.5–2.0, forgotten when the protocol or model changes) instead of showing the raw estimate,
  which overstated a model that counts fewer tokens than bytes/4 by about 12% and fell back at
  every report. On a recorded run the converted figure was within 3% of the next report.
- A finished reply is not in the reading that produced it, so its visible text is added as an
  estimated tail until the next request is measured. Output tokens are not used: on recorded
  runs most of them were reasoning that the next request did not replay.
- The estimate frame also carries `compaction_trigger_tokens`, the normal preflight trigger for
  that budget in the estimate's units; the meter converts it with the same ratio and names where
  the run compacts. The history-window fill can compact earlier.
- Before a session's first request, the fixed prompt (system prompt and tool definitions) last
  seen for the same model stands in, marked as an estimate. A model not seen in this process
  shows the visible text only.
- A run without a published window shows no percentage. Its estimate's prompt budget is derived
  from an assumed default window, so dividing by it would invent a capacity.

Advisor consultations (`advisor_usage`) and compaction checkpoints (`checkpoint_usage`) record
usage for their own separate requests. Both are billed with the run; neither is a context
reading, live or when a session is reopened.

The token trigger is not the only one. The runner fits history into a fixed window of
4,096 messages and 32 MiB by dropping the oldest of it, and on a large-context model the
token budget may never ask for compaction before that window fills. Preflight therefore
also compacts when the prepared request's history window is three quarters full
(`HistoryWindowFill`), aiming at half the current estimate and keeping a smaller recent
tail, so a long run's start reaches a checkpoint instead of being dropped unsummarized.
The fill measures whichever history the request sends (a native Anthropic or Responses
history carries the replayed reasoning). Because the request still fits, this compaction
is optional: a checkpoint that fails, times out or hits a transient provider error leaves
the run working with its current history instead of parking or failing it.

## Nested workspace instructions

The fixed root/user instruction sources remain system-prompt blocks. Successful
structured `read`/`read_file`, `write_file` and `edit_file` receipts discover nested `AGENTS.md` along
the touched file's directory chain. Failed calls, shell text and arbitrary
model-selected instructions do not discover scopes. Lexical and resolved final
paths must remain within the trusted workspace root. Discovery is bounded to
16 active directory hints within a 16 KiB runtime metadata budget (4 KiB per encoded source
path before base64), 16 directory levels and eight existing nested instruction files; ordinary
instruction byte/count limits still apply. Catalog-protected and multiply-linked
nested instruction files are skipped during automatic configuration intake;
this does not change the Broker's explicit file-tool permission rules.

Empty ancestor directories do not consume instruction slots. If more than eight
files are found, the deepest scopes are selected and an omission notice is emitted.
Selected broader instructions precede more specific
ones, and each applies only within its directory. Current user instructions and
system execution rules take precedence. Nested guidance is a complete current
runtime baseline rather than an edit to the system prefix. Whole files are omitted with a notice
when the reference budget cannot fit them; rules are not silently cut in half.
Changes and removals are re-read before later requests. Bounded encoded scope
metadata in Host-authored snapshots remains a fallback for context sources without
receipt metadata; marker-like tool, workflow and instruction text is escaped.
The SQLite context source derives up to 128 recent directory hints from paired
successful file receipts before applying compactions. These private hints travel
outside canonical model history and are reconstructed from encrypted originals
after restart, including subagent continuation. They preserve discovery for image
and oversized-runtime requests that do not admit native text cache snapshots. The prompt provider selects its latest 16 in-root hints and rechecks
instruction files on each build. This metadata grants no authority and undergoes
the same containment and protected-path checks as newly observed scopes.
Sibling file reads reuse one directory hint; root-only file reads add no nested
baseline, avoiding redundant per-file context snapshots.

New root runs capture private hints from eligible visible source runs. Recent
paired successful receipts take priority; if fewer than 16 hints are found, a
bounded scan examines at most 64 older assistant steps and 128 receipt lookups
per run. IDs match within that step's sequence interval. Host-authored
`base:session-scope-hints` metadata is capped at 16 KiB and excluded from canonical
model history. Quoted markers cannot create private hints. Normal containment and
instruction refresh checks apply before use; see [handoff](harness.md#session-run-handoff).

## Prompt ownership and verification

An optional [project memory index](../operations/project-memory.md) supplies
bounded workspace continuity across conversations. It is refreshed runtime data,
separate from run-local checkpoints, instruction authority and verification.

Capability-specific workflow guidance lives in the Protocol tool descriptions and
reaches the model through native declarations; the Core composer lists only each
advertised tool's purpose. Advertisement never authorizes execution.

Behavioral regressions cover checkpoint validation/admission/usage, protocol
projection, cancellation, budget exhaustion, scope refresh/removal/containment,
prefix invalidation and cache-bucket accounting. Deterministic tests prove the
pipeline, not that a live model faithfully summarizes every task. Evaluate
long-session recall, repeated compaction, rereads, completion and latency with
matched real-provider tasks before claiming a quality or speed improvement.
