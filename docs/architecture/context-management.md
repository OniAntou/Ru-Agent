# Context management

The Public Host builds model context from encrypted durable history. Context is
evidence, not execution authority: summaries, workspace instructions, receipts
and snapshots cannot grant paths, tools, approval or a different provider/model.

## Structured checkpoints

Provider-backed compaction uses one auxiliary request to the run's pinned
provider/model. It reuses protocol-specific history projection and opaque
continuations. Its final user message requests eight ordered sections: request
and intent, decisions and constraints, files and changes, results and failures,
pending work, current work, next step, and critical context. Previous checkpoints
must be merged with newer facts rather than copied indefinitely.

The summary must be nonempty, contain every section in order with bullet content,
contain no NUL, and fit 8,000 UTF-16 characters. There is no silent truncation.
Both checkpoint and deterministic summaries are wrapped by `ContextCompactionFrame`
in a `[context_compaction … untrusted=true]` frame; frame markers inside the body
are neutralized so summarized history cannot close or reopen its own frame.
The auxiliary request has a 60-second deadline and an output cap of at most
4,096 tokens (never above the pinned cap). Exact request preflight still applies.
It uses the model's declared lowest reasoning effort when available for this
auxiliary request; ordinary task requests retain their selected effort.
Empty, malformed, truncated, filtered or tool-producing responses return named
`checkpoint_*` failures; no returned tool is dispatched. Production does not
substitute deterministic excerpts after a failed provider summary. Test/scripted
runners without the summarizer interface retain deterministic compaction.

Before dispatch, a durable `checkpoint_started` note reserves one model call in
the run's budget and, when configured, its family budget. No tool budget is
reserved. Reported usage is stored through `ModelUsageRecorded` even when the
returned checkpoint text later fails validation. Prompt-composition receipts retain private block digests
and never expose raw instruction or prompt text. A crash after admission keeps
the spent reservation; it cannot make a dispatched call free. A failed summary
does not replace original history. An automatic malformed/permanent checkpoint
failure stops the run with its named error. Transient provider failures, operator-repairable account limits and the
checkpoint deadline park a root run for explicit Continue; internal children
fail instead of requesting user input. Neither path loops automatically.
Budget exhaustion follows ordinary run policy: root runs wait for explicit
Continue, while internal child runs fail at their bound; no extra call dispatches.

A successful summary must shrink the selected span. It is encrypted and
committed with covered ranges and an input digest through the existing
compaction transaction. Concurrent run changes cause a commit conflict rather
than landing stale text. Full receipts remain available in durable storage.
Automatic compaction targets a recent tail priced at 16% of available prompt
tokens, extending protection to whole call/result groups while leaving at least
one eligible old unit available to compact. System/base context,
the latest user request, unresolved calls and the latest Host instruction-scope
snapshot are protected. The fully prepared request is measured again after
compaction; fixed prompt overhead can still leave a request too large.

## Tool excerpts

During context assembly, unstructured text results over 8,192 characters receive
a stable model-only excerpt: the first 4,096 and last 1,024 characters, with a
visible middle-omission notice and Unicode-safe boundaries. Original receipt
bytes and call/result identities remain intact. Valid JSON objects/arrays are left to the
existing structured output projector, preserving paging and cursor semantics.
This stable projection happens before request budgeting or checkpoint generation;
it does not rewrite previously admitted history merely because a result aged.

## Provider usage anchors

The Generation Kernel may increase a heuristic input estimate using observed
input usage plus an estimate of newly appended wire-history bytes. Cache buckets
are included once according to the adapter's explicit inclusive/exclusive flag.
Missing input counts, unknown accounting or a mismatched reported model cannot
seed an anchor. Estimates never fall below the full current heuristic.

Anchors are isolated by run and require matching protocol/endpoint, exact
non-history request envelope and an unchanged wire-message prefix. A rewrite,
compaction, instruction/schema/model change or expired anchor invalidates it.
Only native text requests with history-owned prompts participate; images and
text-selection use the existing estimator. The in-memory cache is bounded to
256 scopes and a 30-minute access validity window; it stores digests and counts,
not prompts or credentials. It is an estimate, not exact tokenization or a
billing ledger, and does not survive Host restart.

## Nested workspace instructions

The fixed root/user instruction sources remain system-prompt blocks. Successful
structured `read`, `write` and `edit` receipts discover nested `AGENTS.md` along
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
after restart, including subagent continuation. They preserve discovery for image,
text-selection and oversized-runtime requests that do not admit native text cache
snapshots. The prompt provider selects its latest 16 in-root hints and rechecks
instruction files on each build. This metadata grants no authority and undergoes
the same containment and protected-path checks as newly observed scopes.
Sibling file reads reuse one directory hint; root-only file reads add no nested
baseline, avoiding redundant per-file context snapshots.

## Prompt ownership and verification

`RuAgent.Protocol/AgentToolPromptGuidance.cs` owns capability-specific workflow
hints next to tool definitions. The Core composer emits only hints for advertised
tools; advertisement never authorizes execution. Native schemas remain separate
from the short purpose list, and text selection retains full declarations.

Behavioral regressions cover checkpoint validation/admission/usage, protocol
projection, cancellation, budget exhaustion, scope refresh/removal/containment,
prefix invalidation and cache-bucket accounting. Deterministic tests prove the
pipeline, not that a live model faithfully summarizes every task. Evaluate
long-session recall, repeated compaction, rereads, completion and latency with
matched real-provider tasks before claiming a quality or speed improvement.
