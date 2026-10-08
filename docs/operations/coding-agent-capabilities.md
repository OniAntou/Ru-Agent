# Coding-agent capability baseline

This is the current capability and acceptance map for Ru. The 2026-10-04 hardening delivery balanced Harness and TUI development; it does not establish benchmark or overall product parity. Updated 2026-10-08: background terminal tasks are implemented, submission and durable argument ownership have been consolidated, and custom multilingual Terminal/SWE-style benchmarks are available. Live-provider task quality remains unmeasured.

## Reference workflows

The baseline was checked against official sources on 2026-10-04:

- [Codex CLI](https://learn.chatgpt.com/docs/codex/cli): an interactive explore/edit/tool loop, visible commands and changes, saved-session resume and reusable skills.
- [Claude Code interactive mode](https://code.claude.com/docs/en/interactive-mode): multiline input, queued prompts, interruption and keyboard-driven control of conversation state.
- [OpenCode TUI](https://opencode.ai/v2/docs/cli/tui/): discoverable commands, session/model controls, project-file context and transcript interaction.
- [Pi usage](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/usage.md): a small composable core with session continuity, prompt/context entry and explicit extension boundaries.
- [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness): composable agent workflows and extension ownership.
- [Antigravity artifact review](https://www.antigravity.google/docs/cli/artifacts/): inspectable output and changes before subsequent decisions.

These products differ in architecture and scope. Ru must keep its native .NET runtime and Broker execution boundary; copying a provider fallback, interpreter or unrestricted plugin architecture would violate Ru's contract.

## Implemented workflows

| Workflow | Current source evidence | Acceptance gap |
| --- | --- | --- |
| Read/edit/run/verify loop | Harness Core, thirteen Protocol tool definitions, Broker adapters | Real-provider task-quality baseline has not been measured |
| Multi-tool steps and bounded repair | Tool Batch, durable Recovery Notes, failed period-2/3 cycle guard; real Host/Broker edit/verify and guard scenarios | Real-provider effectiveness of repair advice |
| Streaming and durable replay | HTTP/SSE, transcript reconciliation, retained run discovery and explicit `/follow`/`/continue` | Native rendering and transport interruption acceptance |
| Approval, trust and presets | Broker authority, single-use tokens, Workspace Trust; pending approval replay and fresh run-version checks | Native approval keyboard acceptance; another client may resolve first |
| Provider/model selection | Catalog, GUI/CLI pickers; secret-free SQLite pin; optional run-detail metadata used by retained-run display | Unknown legacy selections suppress usage estimates; real-provider acceptance remains separate |
| Context retention | Current-run prompt protection across sequence domains; compaction replay by committed run version | Real-provider long-task quality |
| Session import/export and branching | Accurate persisted/dropped counts, bounded cached listing, `/new`, searchable `/sessions` and explicit `/resume` | Listing is partial when any scan/read/result budget is exhausted |
| Attachments | Shared GUI picker/paste normalization; CLI image/text/office intake and immutable queued submission ownership | Native clipboard, image quality and file-picker acceptance |
| Project-file context | Explicit `/files` selection; bounded traversal, protected-path/reparse/hard-link checks and content read from the validated handle | Native picker acceptance; no LSP or automatic repository index |
| Prompt queue and retry | Immutable session/workspace/provider intent, attachment ownership and stable idempotency key; queue pauses on failure, `/retry` preserves order, repair commands remain available | Settings preflight detects drift but is not an atomic settings lock |
| TUI command discovery | Shared slash vocabulary, completion and Ctrl+P searchable palette | Native terminal key routing and resize acceptance |
| TUI transcript | Bounded visible history, scroll during streaming, unread-output hint, Ctrl+Home/End, collapsed tool/diff expansion | Native terminal rendering and Unicode/resize acceptance |
| Automation/integration | Noninteractive run, JSON-RPC bridge, TypeScript SDK | Real distribution/resource acceptance |
| Background terminal tasks | Session-scoped Broker registry with a per-session cap and lifetime; foreground commands auto-move to the background at their timeout; live `read_output` paging; `mode=kill`; `/tasks` command, composer tasks flyout and a bounded `background_tasks` prompt reference | Live-provider long-task behavior and native flyout acceptance |
| External instructions | Workspace AGENTS and lazy Markdown skills | Instructions remain untrusted and never confer execution authority |
| Workflow evaluation | Eleven hashed scenario oracles; isolated scripted CLI/Host/Broker tool-cycle runner | Scripted wiring is not model quality; eleven cases remain unmeasured or unmet; live-provider results are not available |
| Coding benchmark | Six custom Terminal-style tasks and three SWE-style issue repositories across C#, Python and TypeScript; external graders, reference self-tests, fresh Git baselines and opt-in native RPC trials | Compact public corpus; real-provider Ru quality, official dataset scores and container parity remain unmeasured |

## Acceptance boundary

The hardening delivery repaired the confirmed review defects and added CLI workflows on existing authenticated endpoints and the same dispatcher in Terminal.Gui and sequential mode. Regression coverage includes Git helper isolation, coherent file content/hash, provider pinning across recreation, compaction ordering, creation cancellation, attachment admission, import accounting and failed-tool cycles. Independent review also covered session ownership, migration rollback, recovery expiry, bounded listing and retained approval recovery.

Transcript navigation retains a bounded history, allows reading earlier output during streaming and marks new output while detached from the tail. Command/session controls preserve drafts on refusal. Project-file context uses bounded traversal and explicit selection with protected-path, reparse and hard-link checks. Validation results are recorded in [Verification](verification.md).

Deterministic tests prove wiring, ordering, limits and cancellation. Native Windows Terminal tests prove actual keys, resize, clipboard and visual behavior. Real-provider coding tasks prove model/harness task quality; their model, version, prompt, workspace, outcome and cost must be recorded independently.

The baseline runner is [`scripts/evaluate_agent_workflows.ps1`](../../scripts/evaluate_agent_workflows.ps1), with the eleven bounded oracles in [`tests/fixtures/model-working-environment-scenarios.json`](../../tests/fixtures/model-working-environment-scenarios.json). Its default `-ValidateOnly` mode checks oracle completeness, path bounds and workspace hashes without starting the Host or reading provider settings. `-RunScripted` drives a registered case through the real CLI with a temporary data root and workspace. Scripted results prove integration wiring only; quality, token usage, and cost remain unknown unless measured with a pinned live provider/model/protocol.

## Operating bounds

The [coding benchmark](../../tools/coding-benchmark/README.md) provides separate
source-task and issue-to-patch suites. Its oracle self-test verifies starter
failures and reference passes, including SWE regression/preservation groups.
Native grading executes trusted code and is not an OS sandbox. The existing
workflow evaluator above still measures scripted integration; neither result
should be reported as a live-model coding score.

Session listing inspects at most 2,000 metadata entries, parses at most 1,000 documents and returns at most 500 summaries. Uncached reads share a 32 MiB budget and a cooperative five-second deadline; cached summaries expire after one minute. Exhausted scan/read/result budgets set `sessions_truncated`. Ordering is newest first within the inspected set, not a guarantee of the globally newest sessions when the metadata cap is reached.

Project-file discovery inspects at most 5,000 entries for two seconds and presents at most 50 matches. Selection is explicit; pending context is capped at eight attachments and 32 MiB of source bytes. Prompt submissions retain their own copies, with eight queued entries and a 32 MiB aggregate estimate of retained content. A refused submission preserves the draft and attachments. A failed submission holds the queue until `/retry`; configuration/help commands remain available while it is paused.

Transcript retention is capped at 2,000 rows, 200,000 Unicode scalars and 16,000 UTF-16 units per line, including collapsed content. The palette caps query length and matches. Approval recovery reads at most 2,048 durable events with a four-second deadline, then checks the fresh run version before an explicit decision. None of these UI bounds enlarge the Broker's authority.

## Remaining independent projects

OS-enforced process-tree/filesystem/network sandboxing, built-in subagent orchestration, LSP, executable plugin/MCP integration and installers remain distinct architectural projects. They require explicit execution boundaries, dependency and authority designs; a UI affordance or allowlist is not proof that those capabilities exist.
