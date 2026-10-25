# Coding-agent capability baseline

This is the current capability and acceptance map for Ru. The 2026-10-04 hardening delivery balanced Harness and TUI development; it does not establish benchmark or overall product parity. Updated 2026-10-11: terminal OS confinement was retired under ADR-0015; terminal children run as the current Windows user, while Job Object bounds, ordinary approvals, and secret redaction remain. The [post-refactor Nemotron rerun](../history/verification/2026-10-10.md#post-refactor-live-coding-rerun-2026-10-10) resolved 0/9 custom tasks under `standard`; its model/tool-use and approval failures do not establish general coding quality.

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
| Read/edit/run/verify loop | Harness Core, run-pinned Protocol coding profiles, Broker adapters; incomplete-finish rejection and concrete tool repair hints | Before the finish-reason fixes, the nine-task live rerun resolved none. The post-fix small C# diagnostic stopped at provider rate limiting before tool use; improvement remains unmeasured. See [Verification](../history/verification/2026-10-10.md#provider-finish-reason-and-tool-feedback-fixes-2026-10-10) |
| Multi-tool steps and bounded repair | Tool Batch, durable Recovery Notes, failed period-2/3 cycle guard; real Host/Broker edit/verify and guard scenarios | Real-provider effectiveness of repair advice |
| Streaming and durable replay | HTTP/SSE, transcript reconciliation, retained run discovery, explicit `/follow` and typed `Continue` | Native rendering and transport interruption acceptance |
| Approval, trust and presets | Broker authority, single-use tokens, Workspace Trust; pending approval replay and fresh run-version checks | Native approval keyboard acceptance; another client may resolve first |
| Provider/model selection | Catalog, GUI/CLI pickers; secret-free SQLite pin; optional run-detail metadata used by retained-run display | Unknown legacy selections suppress usage estimates; real-provider acceptance remains separate |
| Context retention | Current-run prompt protection across sequence domains; compaction replay by committed run version | Real-provider long-task quality |
| Session import/export and branching | Accurate persisted/dropped counts, bounded cached listing, `/new`, searchable `/sessions` and explicit `/resume` | Listing is partial when any scan/read/result budget is exhausted |
| Attachments | Shared GUI picker/paste normalization; CLI image/text/code intake and immutable queued submission ownership; Office attachments are unsupported | Native clipboard, image quality and file-picker acceptance; document processing depends on installed tools/optional skills |
| Project-file context | GUI file browsing; compact TUI uses dropped paths or Ctrl+V for image/text/code context | Native picker acceptance; no LSP or automatic repository index |
| Prompt queue and retry | Immutable session/workspace/provider intent, attachment ownership and stable idempotency key; queue pauses on failure, `/retry` preserves order, repair commands remain available | Settings preflight detects drift but is not an atomic settings lock |
| TUI command discovery | Core chat/session/model/attachment/recovery commands, help, updates, completion and Ctrl+P palette; context meter retained; no separate inspection/file-browser/branch/export reports | Native terminal key routing and resize acceptance |
| TUI transcript | Bounded visible history, scroll during streaming, unread-output hint, Ctrl+Home/End, collapsed tool/diff expansion | Native terminal rendering and Unicode/resize acceptance |
| Automation/integration | Noninteractive run, JSON-RPC bridge, TypeScript SDK | Real distribution/resource acceptance |
| Background terminal tasks | Session-scoped Broker registry with a per-session cap and lifetime; foreground commands auto-move to the background at their timeout; live `read_output` paging; `mode=kill`; `/tasks` command, the compact right-hand card, a tabbed details panel (Changes, Tasks) with a title-bar visibility toggle, and a bounded `background_tasks` prompt reference | Live-provider long-task behavior and native card/panel acceptance |
| Interactive terminal (GUI) | ConPTY plus vendored xterm.js in WebView2, PowerShell in the session workspace; a user-only surface the Agent can neither see nor call, killed with the app or a workspace change; the WebView2 runtime ships with Windows 11 | Native terminal rendering, resize and Unicode acceptance |
| Custom slash commands | `CustomPromptCommands`: `.ru/commands/*.md` templates with `$ARGUMENTS`, expanded to an ordinary prompt | TUI only; not in completion/palette; no GUI surface |
| Hooks | `HookToolGateway`: `PreToolUse` (exit 2 blocks) and `PostToolUse`; `RunLifecycleHooks`: `UserPromptSubmit` (exit 2 refuses the prompt before a run exists) and `Stop` (observes a finished run), all from `hooks.json` | Workspace hooks run as the user once the workspace is trusted; the agent cannot write `.ru\hooks.json` in Plan or Standard |
| External instructions | Workspace AGENTS and lazy Markdown skills | Instructions remain untrusted and never confer execution authority |
| Workflow evaluation | Eleven hashed scenario oracles; isolated scripted CLI/Host/Broker tool-cycle runner | Scripted wiring is not model quality; eleven cases remain unmeasured or unmet; this workflow evaluator has no live quality run |
| Coding benchmark | Six custom Terminal-style tasks and three SWE-style issue repositories across C#, Python and TypeScript; external graders, reference self-tests, fresh Git baselines and opt-in native RPC trials | Compact public corpus; dated Step 5 pilot and post-refactor Nemotron rerun recorded under different conditions; official dataset scores and container parity remain unmeasured |

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
workflow evaluator above still measures scripted integration. Oracle self-tests
and scripted workflows do not measure model quality; native live trials provide
condition-specific evidence with pinned runtime, model and policy. Candidate
grader correctness and unattended run completion must be reported separately.

Session listing queries indexed SQLite summaries across all stored sessions and returns the newest 500, ordered by UTC update time and then session ID. More rows set `sessions_truncated`; there is no filesystem scan, byte-budget or summary-cache deadline. The one-time legacy import validates transcript files separately.

Project-file discovery inspects at most 5,000 entries for two seconds and presents at most 50 matches. Selection is explicit; pending context is capped at eight attachments and 32 MiB of source bytes. Prompt submissions retain their own copies, with eight queued entries and a 32 MiB aggregate estimate of retained content. A refused submission preserves the draft and attachments. A failed submission holds the queue until `/retry`; configuration/help commands remain available while it is paused.

Transcript retention is capped at 2,000 rows, 200,000 Unicode scalars and 16,000 UTF-16 units per line, including collapsed content. The palette caps query length and matches. Approval recovery reads at most 2,048 durable events with a four-second deadline, then checks the fresh run version before an explicit decision. None of these UI bounds enlarge the Broker's authority.

## Remaining independent projects

MCP servers are supported through the `mcp` tool ([ADR 0025](../adr/0025-mcp-servers-through-the-broker.md)): stdio servers from the user's own `~/.ru/mcp.json`, run by the Broker, with `ExternalTerminal` approval. LSP and executable plugin integration remain distinct architectural projects. They require explicit execution boundaries, dependency and authority designs; a UI affordance or allowlist is not proof that those capabilities exist. Desktop/CLI installers and explicit updates are documented in [distribution and updates](distribution-and-updates.md). Terminal OS sandboxing has been retired by [ADR 0015](../adr/0015-retire-terminal-os-sandbox.md).
