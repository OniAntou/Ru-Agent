# Agent tools and truthful execution

This document is the operating contract for Ru's in-process tool authority,
session policy and headless Agent surfaces, updated 2026-10-31.
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

This page owns the operating permission matrix; the executable source is the
[Protocol policy catalog](../../backend-dotnet/RuAgent.Protocol/Harness/AgentPolicyProfileContracts.cs).
Other guides link here instead of maintaining a separate preset matrix.

This checkpoint includes declarative Skills, bounded headless terminal execution
(`powershell` and `run_process`), session rewind, managed Git commit workflow,
and dynamic model context compaction. MCP servers the user
declares in `~/.ru/mcp.json` are reached through one `mcp` tool (see
[MCP servers](#mcp-servers)); connector libraries are not advertised Agent tools.

## Coding profiles

New runs default to `coding_v2` (27 definitions). `patch_v2` (26) and
`coding_code_v1` (29) are also supported, and
must be selected explicitly for a new run; changing the Host setting does not
alter an existing run. Set
`RU_AGENT_TOOL_PROFILE=coding_v2|patch_v2|coding_code_v1` in a dedicated Host
process to choose the profile for new runs. Persisted provider selections must
include a valid profile pin: a missing pin fails as `tool_profile_required`, and
an explicit retired or unknown profile fails as `tool_profile_invalid`. Run
status reports the actual `tool_profile`. A profile changes tool presentation,
never permissions.

Use `write_file` to create, `edit_file` with an `edits` array to modify an existing
file atomically, or `apply_patch` in the patch profile for unified diffs. Keep
replacements exact and disjoint. Patch paths must remain inside the run workspace;
all targets are checked, and patch approval is one-time. For Windows command text
use `powershell` with `script`; for literal argv use `run_process`. Read retained
output with `terminal_read` and stop a background task with `terminal_stop`.
These operations retain terminal lifetime/output/process-tree bounds.

Prefer `read_file` with 1-based `start_line` and `line_count`; continue at
`next_line` (also updated when model context shortens the page). Use `read` for
images, files above the 4 MiB line snapshot cap, and exceptionally long lines.
Do not reinterpret line cursors as byte offsets. Both surfaces preserve Unicode.

### Programmatic coding calls (experimental)

`coding_code_v1` adds `run_code` and unified patches to the explicit coding
surface. Supply a synchronous JavaScript function body in `code`, for example:

```javascript
const page = tools.read_file({path: 'src/example.cs', start_line: 1, line_count: 100});
return {status: page.status, preview: page.output.content};
```

The SDK exposes only the coding tools listed in `run_code`'s schema description.
Each returns `{status, output, error_code}`. Use normal JavaScript loops and
filtering, then `return` the selected JSON. There is no async/Promise contract,
CLR access, module loader, direct filesystem/network API, clock or randomness.
Dynamic compilation (`eval`/`Function`) and generator syntax are unavailable;
async syntax is rejected before evaluation, including unawaited functions.
Image reads require a direct `read` call. Oversized nested output stops with
`program_tool_output_limit`; inspect the tool directly instead of rerunning
expensive commands to recover output.

`tools.parallel([{tool: 'read_file', arguments: {path: 'a'}}, …])` runs up to
eight read-only calls (`read`, `read_file`, `list`, `grep`, `glob`) concurrently
and returns results in input order. It remains a synchronous SDK call; mutations
and terminal operations cannot enter a batch. The batch shares the normal call
budget and reserves all members atomically before dispatch.

Limits: 16 KiB source, 32 total calls, 256 KiB selected result or batch result, 2 MiB trace,
100,000 statements, recursion depth 64, 32 MiB interpreter allocation and 250 ms
computation per replay. The regex timeout is 50 ms. `timeout_ms` defaults to
30 seconds and cannot exceed 120 seconds; at most two programs run per authority.
These constraints do not confine Windows terminal processes at the OS level.

Every nested call uses the current run's normal Broker policy. An operation that
requires approval stops with `program_requires_direct_call`; propose the exact
standalone operation from `blocked_call` to use the normal approval UI. Its
arguments are omitted by name above 16 KiB. Program code approvals are
one-time and cannot become remembered path/argv rules.

Prepared intent is stored before the dispatch marker and a receipt afterwards.
A replay uses stored, redacted receipts. Prepared-only calls can resume; a dispatch
marker without a receipt blocks repetition, as do changed source/policy/SDK pins.
Indexed batch receipts preserve original positions, including partial failures.
Interrupted effects can require reconciliation and workspace
inspection. Nested calls count against the shared 256-tool-call budget window.
An unresolved program trace blocks verified completion; Continue does not clear
the journal or authorize repeating it. If the effects cannot be established,
report `[BLOCKED]` and start a fresh run after inspecting the workspace.
The root's displayed model-tool counter still counts model-visible calls;
the shared family pool also counts nested calls. Continue opens a new window.

The full trace remains encrypted audit/completion evidence. Provider context
always receives selected results without the trace, including when the response
would fit. Filtering cannot hide failed required checks, and a nested structured
source edit requires a later declared check (ADR 0039). See [ADR 0028](../adr/0028-bounded-programmatic-coding-tools.md).

## Incomplete outcomes and model context

A final reply beginning `[BLOCKED]` reports an incomplete/partial outcome. The
Host strips that marker from durable assistant history and records a failed run
with the original tool error (or `model_reported_blocker`), so an honest blocker
report does not repeatedly consume recovery turns or masquerade as verified
success. Ordinary unsupported success claims after failed tools still receive
bounded recovery feedback. Approvals and protected paths are unchanged.

Native requests carry full tool descriptions, schemas and argument examples.
Legacy text selection receives these in the system prompt and has no native
function declarations. Native history holds up to 1,024 messages / 8 MiB, with
128 Ki-character result pages; encoded prompt context has an 8 MiB cap and
128 KiB references fitted collectively. Broker tool output now has a 4 MiB ceiling; adapter-specific result, time and network bounds still apply. These are memory ceilings; exact
provider-token preflight and compaction decide how much fits the selected model.
Existing instruction files omitted for invalid encoding, size or containment
produce a safe source-ID/reason notice. Workspace guidance cannot override the
current user's explicit instructions or the system execution contract.

## Current capability matrix

Coding tools: `read`, `read_file`, `list`, `help`, `grep`, `glob`, `git_status`,
`write_file`, `edit_file`, `file_delete`, `file_restore`, `apply_patch`,
`powershell`, `run_process`, `terminal_read`, `terminal_stop`,
`web_search` and `web_fetch`. Profile and availability filters apply below.

The Protocol catalog contains 29 definitions; the Broker has 28 executable
bindings because `ask_user` is intercepted by the Host. Run-pinned inventories
contain 27 definitions in `coding_v2`, 26 in `patch_v2`, and 29 in `coding_code_v1`,
before availability/role filtering. There is no selectable
legacy profile. The model-facing file mutation and terminal tools are
`write_file`, `edit_file`, `file_delete`, `file_restore`, `powershell`,
`run_process`, `terminal_read` and `terminal_stop`; `apply_patch` is available
in `patch_v2` and `coding_code_v1`. Internal adapter reuse does not expose a
retired tool identity. Persisted deny/ask matchers for old tool families remain
effective for policy preservation, but do not restore those tools as callable
identities. Profiles do not widen authority. `read` takes `path` with
optional `offset`/`limit` (source bytes, default 1 MiB, maximum 4 MiB) and reads
one file. A `.png`, `.jpg`, `.jpeg` or `.webp` file is returned as an image
when the run's model accepts image input (see [Image reads](#image-reads)).
Returned JSON is fitted into 4 MiB, including escaping and metadata;
`offset + bytes_read` continues a truncated Broker page. The Host adjusts
`next_offset` to the visible page when model context clips it.
`list` takes `path` with optional
`max_entries` (1–512); `help` takes `query` (1–256) with optional `max_results`
(1–5) and searches the five bundled offline documents; `todo_write` replaces the
session task list (at most 64 rows of `content` plus a `status` of `pending`,
`in_progress` or `completed`) and touches neither disk nor network. For a coding
request with several explicit requirements, record each one as its own row before
editing, and mark it `completed` only after a check that exercises it passes. A failed
or unparseable call keeps the previous list.
`glob` takes `pattern`
(required) plus optional `path` and `max_results` (1–128, default 128) and returns
stable sorted relative paths without content access; `grep` takes `query`
(1–2048 chars, required) plus optional `path`, `file_glob`, `case_sensitive`
(default false), `is_regex` (default false), `max_results` (1–128, default
128) and `offset` (0–50000, default 0) for paging the ordinal results,
and returns bounded match locations with previews up to 512 characters.
`run_process` takes an executable, an `arguments` array and an optional working
directory; `powershell` takes a script. Both use the same Broker terminal
policy and bounded process lifecycle. `terminal_read` pages retained output and
`terminal_stop` stops a running task.

`git_status` takes no arguments and reports the run workspace's Git state from one
fixed read-only `git status --porcelain=v2 --branch` invocation: `branch` (null when
detached), `detached`, `head_oid`, `upstream`, `ahead`/`behind` (null without an
upstream), and `staged`, `unstaged`, `untracked` and `unmerged` lists. Staged and
unstaged rows carry the porcelain code and a rename's `original_path`; conflicts
stay in `unmerged` instead of appearing as ordinary staged work. Paths are
workspace-relative: when the workspace is a subdirectory of a larger repository,
only that subtree is reported and nothing outside it appears. The status entry cap
is 2000 (`truncated: true` marks an incomplete list), `unparsed_lines` counts lines
the shared parser could not interpret, and `clean: true` carries a `notice`. A
workspace outside Git answers `is_repository: false` with a notice rather than
failing the call. The tool never diffs, stages, commits, fetches or runs repository
hooks; use `run_process` for those reads or a managed Git action.

Every running terminal task reserves a per-session slot when launched, including
foreground tasks that may later move to background. Promotion keeps that slot;
it does not admit another process. `RU_MAX_BACKGROUND_TERMINAL_TASKS` sets this
Broker capacity at initialization (default 8, valid 1–64). Invalid explicit values
fail initialization. When full, a new launch returns
`background_task_limit_reached`; existing tasks keep running. Lifetime, output,
spool quotas and approval rules still apply, and model arguments cannot raise
the configured capacity.

A Python verification invocation has no special case. A failed `python -c` compile or a `python -m pytest`
run that exits 5 is a failed check if it was declared `purpose=verification`. It stays outstanding until the
same identity passes. A different invocation does not clear it. An undeclared failure is a diagnostic only.
Exact command identities and failed receipts are retained, and no argument or authorization changes.

`web_search` takes `query` (1–512 chars) plus optional `max_results` (1–10,
default 10); `web_fetch` takes `url` plus optional `offset` (0–4194304,
default 0) and `limit` (1–32000, default 32000); `skill` supports catalog query
or loading a canonical ID with arguments and a revision check. Its exact body,
resource base and digest remain untrusted guidance; see [skills](skills.md).
`ask_user` takes
`question` (1–2048) plus optional `options` (at most 8 strings of 512 chars) and
is Host-intercepted before gateway dispatch: it never reaches the Broker, parks
the run in `WaitingUser` with a durable `input.requested` event, and resumes when
the client answers through `POST /agent/v1/runs/{runId}/inputs/{inputId}` with
the answer as that call's durable tool result. Web tools are standalone with their own
`network.search`/`network.fetch` scopes and are automatic in the canonical
`plan`, `standard`, and `full_access` profiles. `web_approval_required` remains
available only when an explicit ordered rule asks; profile fallback does not
synthesize it.
Google HTML (`https://www.google.com/search`) is the primary search engine and
is raced against a fixed set of free fallback engines (DuckDuckGo HTML, Bing,
Mojeek); the first engine with usable results answers, so a slow or blocked
primary no longer holds the call hostage. Every engine request still passes
redirect and private-host validation, so a fallback never widens the network
boundary the call was granted under. When every engine fails, or when anchors
were present but none resolved to a usable target, the call fails closed as
`web_search_parse_failed` or the earliest engine's bounded error rather than
returning an empty answer. Known engines require their result markup; CAPTCHA
and navigation anchors cannot win the race. Bing links are decoded to the
public destination and titles/snippets come from the result block. This does
not guarantee semantic relevance or availability: free engines may still
block requests or return weak results. Known HTML engines now discard obvious
off-query candidates with no meaningful query-term overlap (accent-insensitive),
and honor explicit `site:` restrictions before accepting a winner. All discarded
candidates produce `web_search_irrelevant_results`; the race continues to other
engines. This is a lexical sanity check, not a factual accuracy score.
For a configured structured search service, set
`RU_AGENT_BROKER_WEB_SEARCH_MODE=configured_json` and an explicit public
`RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT`. Existing query parameters (for example
`format=json`) are retained and `q` is appended. The endpoint must return JSON,
canonically `{ "results": [{ "title": "...", "url": "https://...", "snippet": "..." }] }`;
`content` is also accepted for snippets. This mode uses only that endpoint,
without an HTML-engine race or silent fallback. Missing endpoints/unknown modes
disable Web at startup. HTML responses fail as `web_search_json_required`,
invalid JSON as `web_json_invalid`, and unusable/empty results remain named
failures. Redirects, public-result validation and response bounds still apply.
The default `html_race` mode retains the fixed engine set above.

HTTP failures surface as `web_http_<status>` (for example 403, 404, 429) and
connection failures as `web_network_error`, rather than generic `adapter_error`.
Use another source or report unavailable evidence; do not bypass an access error
by terminal scraping. Once a relevant source answers the question, finish.
Each web failure code carries its own next step in the result summary: a
search with no readable or relevant results suggests one retry with fewer
keywords or fetching a known authoritative URL, 401/403 says the site refuses
automated clients, 404 suggests checking the address, 429 a different source,
and an unsupported content type points to an HTML version of the document.

Web downloads default to 4 MiB, with the existing configurable 4 MiB ceiling.
Compressed responses are decoded before that bound is counted. `web_fetch`
identifies itself as `RuAgent/<version> (+https://github.com/OniAntou/Ru-Agent)`
and sends `Accept` and `Accept-Language`; without them ordinary sites such as
Wikipedia refused the request. It never presents a browser user agent, so a site
that turns away automated clients still fails with its HTTP status.
Fetch parses HTML locally through AngleSharp without loading resources or
executing scripts, prefers main/article content, removes navigation/scripts,
and preserves paragraphs, preformatted code and HTTP links. Each destination
is written as a link once; anchors into the same page, repeated URLs and
footnote markers keep their text only, since URLs took about half of an
article's extracted text. Markdown, JSON
and source text preserve newlines and indentation. HTTP charset and BOM are
honored; invalid encodings fail as `web_encoding_invalid`. Returned content
is explicitly untrusted and limited to 32000 extracted UTF-16 characters per
page, separate from the download bound. Output includes `offset`, `next_offset`
and `truncated`; pass `next_offset` as the next request's `offset`. Each page
fetches the source again, so a changed page can shift offsets. Offsets beyond
the extracted document fail as `web_offset_out_of_range`. Recognized challenge
forms fail as `web_page_blocked`; empty JavaScript-only shells fail as
`web_content_empty`. A PDF (`application/pdf`) is parsed with PdfPig, which
renders and executes nothing: up to 500 pages are read in content order, and the
text pages like any other document. Scanned or malformed PDFs fail as
`web_pdf_unreadable`, password-protected ones as `web_pdf_encrypted`.
Authentication, JavaScript execution and other binary formats are not provided
by these tools (`web_content_type_not_supported`).
Model exposure in this table applies only to profiles containing the named ID.
Modern file and terminal tools use the existing policy families and Broker
authority. Retained rules matching old tool-family names can still deny or ask;
they do not make a retired public identity executable.

| Capability family | Model exposure | Broker behavior |
| --- | --- | --- |
| `glob`, `grep`, `list` | always advertised | `plan`: trusted-workspace read auto, external ask; `standard`: trusted/session-granted read auto, external ask; `full_access`: external structured read auto, protected paths allowed |
| `read` | always advertised | same canonical read matrix; `read` carries no `op` discriminator. An image read also needs the run's pinned model to accept image input, otherwise `image_unsupported_by_model` |
| `git_status` | always advertised (`workspace.read`) | one fixed read-only `git status --porcelain=v2 --branch` run in the run workspace, bounded by the shared 2000-entry status cap and a workspace-subtree pathspec; no path or revision argument. Automatic in `plan`, `standard` and `full_access`; a workspace outside Git answers `is_repository: false`. It never diffs, stages, commits or fetches |
| `write_file`, `edit_file`, `file_delete`, `file_restore` | present in supported coding profiles | `plan`: mutation deny; `standard`: trusted-workspace mutation auto, external structured ask; `full_access`: external structured mutation auto, protected paths allowed; preimage mismatch returns `workspace_changed` |
| `apply_patch` | `patch_v2`, `coding_code_v1` | workspace-scoped unified patches; all targets are checked under the same policy, approval is one-time, and patch rollback preserves intervening edits |
| `powershell`, `run_process` | present in supported coding profiles | `plan`: deny; `standard`: workspace cwd executable/shell auto, external cwd asks; explicit ask/deny and long foreground rules apply; `full_access`: arbitrary current-Windows-user terminal auto. Children run as the current user without an OS filesystem/network sandbox. Obsolete sandbox arguments are rejected. Output, timeout, cancellation and process-tree bounds apply. |
| `terminal_read`, `terminal_stop` | present in supported coding profiles | read retained terminal output or stop a running task by its opaque execution ID; session ownership and existing approval rules apply |
| `web_search`, `web_fetch` | advertised when managed Web is enabled | automatic in canonical `plan`/`standard`/`full_access`, bounded, sanitized, explicitly untrusted; an explicit ask rule may still return `web_approval_required`; the primary engine races the fixed free fallbacks and the first usable result answers, every engine's failure is bounded rather than silently empty, and redirect targets are revalidated |
| `help` | always advertised | offline, workspace-independent, approval-free search over five embedded documents; never reads workspace files |
| `skill` (workspace SKILL.md body) | always advertised (`system.read`) | loads one name + description-listed body as untrusted prompt guidance; grants nothing |
| `ask_user` | always advertised (`user.input`) | Host-intercepted: the Broker never receives the call. Parks the run in `WaitingUser` with a durable `input.requested` event; the client answers through `POST /agent/v1/runs/{runId}/inputs/{inputId}`, a replayed or foreign id fails as `input_stale`, and the answer completes the parked call durably before the run resumes |
| `mcp` (`op=list_servers`, `list_tools`, `call`) | advertised while `~/.ru/mcp.json` declares an enabled server; never to subagents | `list_servers` reads the user's file and is automatic; `list_tools` and `call` start or call a server and take the `ExternalTerminal` decision: `plan` deny, `standard` ask (`mcp_call_requires_approval`), `full_access` automatic; "Always allow" remembers one server and op/tool (`call` with its tool name, or `list_tools`), never a whole server or another op; the card names the server and tool. See [MCP servers](#mcp-servers) |
| `computer` (`action=list`, `request_access`, `focus`, `screenshot`, `click`, `move`, `drag`, `scroll`, `type`, `key`) | advertised only to a root run that pinned the computer-use opt-in with image input; never to subagents or `run_code` | every action is a mutation, so `plan` denies all of them; `request_access` takes the `ExternalTerminal` decision: `standard` ask (`computer_access_requires_approval`), `full_access` automatic; other actions are allowed by policy and checked against the session's app grants when they run. Protected apps are refused in every preset (`computer_app_protected`). See [Computer use](#computer-use) |
| connector libraries | not advertised | experimental/unwired |

Large structured tool results are projected into bounded prompt references while
their durable Broker receipts stay unchanged. When a reference carries
`has_more: true`, continue the same source using
`next_offset`: `read` cursors count UTF-8 bytes, `grep` cursors count
matches from the original query offset (an omitted `offset` means zero),
`web_fetch` cursors count UTF-16 characters in extracted content (excluding
the untrusted wrapper/truncation marker), and `terminal_read` cursors count UTF-16 characters within the same
`execution_id`/spool. The projection names its visible unit and retains source,
receipt, and spool metadata; it does not claim older output exchanges remain in
the bounded model history. Missing call context fails closed if clipping needs
a cursor. Paging does not authorize a new operation or bypass Broker approval.

### File creation versus editing

Create a new file with `write_file` and its `path` and `content` fields.
`edit_file` never creates files: it requires an existing target and an `edits`
array whose replacements are exact, non-overlapping and resolved against the
original file. Missing-target errors direct the caller to `write_file`;
permissions and overwrite checks remain in force.

### File discovery output

Both
`glob` and `grep file_glob` share pattern semantics: `**/` matches zero or more
directories, including root files; `*` and `?` do not cross a path separator,
and filename-only patterns also match nested filenames. Literal pattern text
is escaped rather than replaced through wildcard placeholders.
Glob traversal observes the tool deadline and caller cancellation while
enumerating, including after its worker has started; an expired deadline
returns `process_timed_out` rather than a successful partial result.

Grep previews include the match instead of always showing the line's prefix.
`column` is the absolute 1-based line column; `preview_column` names the first
column of the preview. Results retain ordinal path/line ordering. Grep scans
UTF-8 files without treating a leading BOM as content or a line column, so
first-line anchored regexes behave the same with and without a BOM. It scans
ordered batches with bounded concurrency for the first page. Nonzero offsets skip
matches sequentially without retaining skipped match objects; collection is bounded
by the page size plus one lookahead, stopping once the page is known. Discovery has
no aggregate byte cutoff; per-file, candidate, directory and time bounds still apply. Output
includes `offset`, `next_offset`, `scan_incomplete`, `discovered_files` and
`scanned_files` (the files actually searched). Files over the 1 MiB search limit
increment `excluded_by_size` and `skipped` and set `scan_incomplete=true`, including
an explicit file target. This does not imply a next page; use bounded `read` or
another appropriate approved tool for those files. A final exact-size page has no
next cursor. `truncated` can also indicate a scan/time bound or regex timeout;
`scan_incomplete=true` means coverage is incomplete, not proof that another
page exists. Narrow the path/query in that case. Each page re-scans files;
changing files or hitting discovery bounds can change the ordered result set.
The 50000-offset ceiling can prevent continuation even when more matches are
known; narrow the query/path rather than exceeding it. If prompt projection
clips the returned page, use the adjusted `next_offset` in the model-visible page.

Each candidate's opened file handle must still match its previously authorized
canonical path before content is read; a junction swap is skipped instead of
being treated as an ordinary change to searchable content.

### Text paging boundaries

File `read` keeps byte offsets and never returns a non-EOF page with zero
visible bytes. If `limit` cannot hold the first complete UTF-8 character,
`read_limit_too_small` asks for a larger window (at least 4 bytes can hold any
Unicode character). An offset inside a UTF-8 character fails as
`invalid_utf8_offset`; use the returned byte count or projection cursor.
Malformed UTF-8, including an incomplete final character at EOF, fails as
`invalid_utf8` instead of producing a page that cannot advance.

### Image reads

[ADR 0026](../adr/0026-read-returns-workspace-images.md) lets `read` show the
model a workspace image, such as a UI screenshot. A file is an image only when
its extension is `.png`, `.jpg`, `.jpeg` or `.webp` and its leading bytes are a
PNG, JPEG or WebP header; the media type comes from the bytes. Any other
extension keeps the UTF-8 text path. The same path, protected-path, hard-link and
final-path checks run first.

| Condition | Result |
| --- | --- |
| Image, at most 4 MiB and 8000 px a side, model accepts image input | Success. JSON carries `path`, `media_type`, `total_bytes`, `width`, `height`, `sha256` and `duration_ms`; the image travels beside it to the model. |
| The run's pinned model does not accept image input | `image_unsupported_by_model`; no bytes are returned |
| Larger than 4 MiB | `image_too_large` (the Broker does not resize) |
| Wider or taller than 8000 px | `image_dimensions_too_large` |
| Image extension, but not a readable PNG, JPEG or WebP header | `image_format_invalid` |
| Non-zero `offset` | `image_offset_unsupported`; `limit` is ignored for images |

Whether the model accepts image input is decided once, when the run's provider
selection is pinned, by the same rule run creation uses for attachments: the
catalog's Models.dev declaration, then the model-name fallback. The Host sends
the decision to the Broker on the `tool.execute` envelope, never in the model's
arguments. The image is stored as a model-only encrypted `ToolImage` payload;
only the newest four tool images of a run are sent with a request, and an older
result says `[image omitted from this request; read the file or take the screenshot again to view it]`.
A later run in the same session sees the metadata, not the image.

Web pages keep UTF-16 offsets but shorten a page by one character when its end
would split a surrogate pair. Their `next_offset` advances by the actual body
length. An offset inside a pair fails as `web_offset_invalid_boundary`; a
limit of 1 at an emoji fails as `web_limit_too_small` (retry with at least 2).
Follow returned cursors rather than adding the requested limit to the offset.

### Retained terminal output and background tasks

`terminal_read` pages retained output in the same session, using `execution_id`
from a launch receipt or failure `spool_id` with an `offset`/`limit` window.
Unknown or foreign-session IDs fail closed. Output beyond the 256K-character
in-memory window spills into the session spool (1 GiB UTF-8 bytes per call,
2 GiB per session); `spool_path` and `spool_truncated` describe retention.
`background=true` returns an execution ID and `state=running` immediately;
`terminal_read` also reports live state/exit code, and `terminal_stop` stops it.
Background launches skip the foreground-window approval gate, but retain all
other policy checks and a bounded lifetime (30-minute default, 2-hour ceiling).
A foreground command exceeding its approved window moves to the background
with `moved_to_background` and the same execution ID/lifetime.

### Session folder grants and terminal approval

Terminal launch resolves the executable or shell runtime before registering a
foreground or background task. Resolution errors such as `executable_not_found`
retains its code instead of becoming
`process_start_failed`; an unresolved background call cannot report a task as
started. Windows children retain a native process handle before resume, so an
immediate exit still yields its actual exit code.

Failed terminal starts, nonzero exits and timeouts retain `exit_code` when
available and already-redacted stdout/stderr head/tail excerpts, each bounded
to 2,048 characters, in the private model-visible receipt. Failure status and
code remain unchanged. `terminal_read` retrieves retained output for the same
session; public SSE summaries do not expose these diagnostic excerpts.

On Windows, `powershell` takes a PowerShell script, while `run_process` takes a
separate executable and an `arguments` array (an omitted array means no
arguments). The explicit forms avoid relying on Unix shell commands or
implicit shell parsing.

Canonical `plan` denies terminal and mutation. Profile version 2 makes Standard
launch-directory based: both `run_process` and `powershell` are automatic with a canonical cwd
inside the trusted workspace. External launch directories ask; explicit ask/deny
rules and long foreground approval still apply. Argument literals, inline code,
package installation, executable install location and network metadata are not
approval classifiers. PATH is sanitized and the real executable/argv/cwd are bound
to the single-use authorization token. Standard and Full Access run as the Windows
user without OS filesystem/network confinement. Full Access also permits external
launch directories. Hard timeout, output cap, cancellation, redaction and process
cleanup remain. API keys stay in credential storage. See
[ADR 0017](../adr/0017-workspace-terminal-policy.md).

An explicit absolute structured-file path outside the trusted root asks for
`external_directory_required` under `standard`. Any approval of that request, including a one-time
allow, records a session-scoped `CanonicalDirectoryIdentity` for structured descendants, because the
Host sends `allow_session_directory` for any outside-folder approval. Approving with `allow_always` additionally saves the nearest existing
folder of the approved path as a durable project-scope path pattern (`<folder>\**`) that covers the same
structured tools and survives restarts and later sessions of the same workspace identity. A drive,
volume or share root is never saved; its approval is only a one-time token with no folder grant. The evaluator honors a matched allow rule for external
targets (`external_directory_rule_allow`); deny and ask rules keep precedence,
the plan read-only gate still applies, and no grant ever authorizes terminal
intent. Full Access instead selects an ephemeral nearest external
ancestor for that structured call and does not persist a folder grant.
Structured protected paths (harness key/database files, runtime descriptors and Ru hook and MCP configuration files,
plus user-added `RU_PROTECTED_PATHS` entries) are denied in `plan` and
`standard` and allowed in `full_access`, and canonical, symlink, junction, and
hard-link checks stay active. An ambiguous
mutation pauses for inspection until explicit Continue.

`apply_patch` is advertised only by `patch_v2` and `coding_code_v1`. Internal
adapter names do not create additional public model capabilities.

## Model-first selection and native batches

The Host advertises the same canonical tool definitions to the prompt and the model step.
On a provider-native path, one model generation may emit optional narration and
one or more ordered tool calls. On the compatibility text-selection path, the
model emits exactly one JSON object with `tool_id`, `tool_version`, and
`arguments`, without selection prose. The text path also accepts the existing
strict wrapper forms; malformed markup remains ordinary text and never executes.

The runtime evaluates a complete batch before executing any call. If any
non-denied call asks, prepared authorization is discarded and the whole retained
batch pauses for one explicit decision. Resolution re-evaluates the exact durable
proposals under the current lease; the model is not asked to reissue an approved
call. An `ask_user` call parks the same way without a Broker decision: the asked
call is completed by the answer route, and the durable rehydrator resumes the
remaining retained proposals without a new model proposal. A denial may return
a repair hint. An exact repeat or short failed-call cycle is suppressed before
duplicate dispatch; up to two `[REPETITION GUARD]` notes request changed arguments,
then `repeated_tool_call` parks the run for explicit Continue. Suppressed calls
remain audit-only and do not create unmatched native-history tool calls. The
failed receipt remains visible behind advice, so prose alone cannot clear it. An empty or whitespace-only
final step is answered up to three times with a bounded `[HARNESS RECOVERY]` note before the run fails as
`empty_model_reply`. A runner-rejected tool call or selection
(`model_tool_call_invalid`, `model_tool_call_unavailable`, `capability_unavailable`,
`invalid_selection`) is
likewise answered up to four times before failing with the original runner code.
A `Length` finish is not a completed turn: the entire step, including partial
tool calls, is suppressed before any execution and gets up to three recovery notes
requesting smaller output, then fails as `model_output_truncated`. A
`ContentFilter` or `Error` finish fails immediately as `model_content_filtered`
or `model_reply_error`. All recovery kinds share a cumulative 24-note run cap;
provider-transient failures retain their existing retry/Continue behavior, and
prompt failures never take the note path.

The shared budget remains 256 model-initiated tool calls per window; there is no
new low per-step cap. A root run opens up to eight windows by itself before it
parks for an explicit Continue, so one long prompt does not pause at the first
128 steps; internal children keep their own bound. Read-only calls may execute
concurrently in chunks of at most eight while receipts retain model order.
Mutations and terminal calls are sequential and act as barriers; an unknown
mutation outcome stops later work until reconciliation. A model cannot claim
success before durable receipts.

Use the `help` tool when the user asks how Ru's own features, architecture,
configuration, tools, providers, or operating boundaries work. It returns
bounded excerpts from the five current bundled documents (`ru-overview`,
`architecture`, `runtime`, `agent-tools`, and `provider-catalog`). Use runtime
context or an advertised terminal tool for live machine, process, device, and
workspace state. There is no second docs alias or fallback tool; the only
model-visible bundled-help contract is `help` on tool
`1.0.0`.

## One execution path

Agent text and voice turns share the same execution ownership chain:

```text
WinUI authenticated Agent composer
        -> ASP.NET Core Public Host and session grant
        -> in-process typed model step (ModelStepService)
        -> in-process execution authority policy and canonical binding (BrokerExecutionRuntime)
        -> bounded adapter plus readback verification (terminal OS confinement retired)
        -> typed receipt back to the Host
        -> visible reply
```

The Public Host keeps one active execution grant. It reuses a valid matching
session, and closes the previous grant before opening a different Agent
session. Close or open failure stops before adapter execution; the authority
continues to reject concurrent owners with `session_conflict`.

The desktop session switch also waits for the previous Harness run's
cancellation commit before creating a run for the replacement session. A
workspace-trust run that was abandoned past its ten-minute trust window is
cancelled (startup recovery and the running host's drive loop, safe code
`workspace_trust_expired`), so an interrupted prompt cannot keep the
session's active-run ownership indefinitely. The drive loop covers the long-lived
desktop host, where no restart would otherwise arrive to notice the expired
challenge. The same rule covers approvals: a run
still parked in `WaitingApproval` past the 30-minute approval lifetime fails as
`approval_expired` (startup recovery and the running host's drive loop), because
no client can resolve an older approval — the endpoint refuses it as
`approval_stale`. The gated tool call is pre-dispatch, so failing that run cannot
roll back a side effect. Different sessions always run in the same workspace
without an exclusive workspace lock. The compatibility `409 workspace_busy`
(or the session-scoped variant) for a second run in the same session
names the run that owns that session in `blocking_run_id`/`blocking_run_status`,
so a client can point the operator at the exact run to cancel
(`ru agent cancel <run-id>`), attach to (`ru agent attach <run-id>`), or — when
`blocking_run_status` is `waiting_user` or `reconciliation_required` — resume
with `ru agent continue <run-id>`.

While a run is actively driven, a dedicated heartbeat renews its workspace
membership independently of model and tool step duration. A failed renewal
cancels the active step and fails closed with `workspace_membership_expired`;
slow provider responses alone do not let a healthy membership expire.

Agent turns continue through normal streamed generation. Unrelated
conversation is never forced through a tool-selection path.

The Public Host derives `model_tools` from canonical model exposure and runtime
availability, while the execution session owns executable scopes and publishes the actual
resulting grant as `agent.session`. Managed startup enables the Host web
configuration and the default headless model loop advertises both read-only
Web bindings through the in-process gateway. Workspace `SKILL.md` roots are
prompt guidance and grant nothing by themselves. WinUI renders a received grant;
it does not infer workspace, terminal, Web, connector, or skill access.

A model step may return one or more native calls, and the Host preserves their
provider IDs and order. The Host sends only bounded enabled capabilities, exact
tool versions, and typed arguments; narration is display text, never execution.
The scheduler allows at most eight overlapping read-only executions, keeps
mutation/terminal calls sequential, and appends receipts in model order. A
visible success reply is legal only after matching durable success receipts.
Timeout, cancellation, policy denial, malformed context, or adapter failure
terminates or reconciles the action with a deterministic receipt.

If the model produces ordinary assistant prose immediately following a committed
tool batch that contains any failed or unverified receipt, the coordinator suppresses
the prose from the conversation outbox, retains the assistant step internally for audit
as an `InternalOnly` payload, and answers with a bounded durable `[HARNESS RECOVERY]`
note in the model context naming the failed receipt code, so the model can redo the
call, use a different tool, or report the blocker. The note states the reason from
the failed receipt and requires the next reply to be a repair tool call or a final
`[BLOCKED]` report; a plain final reply cannot complete the run. The unverified
case is bounded to 4 notes and every recovery case shares a global cap of 24 notes
per run; once the
bound is spent the run fails with the receipt error code (falling back to `tool_failed`)
exactly as before. An unverified success claim is never displayed to the user as a
completed answer. A subsequent different tool call remains eligible for normal execution
to allow recovery. A first `policy_denied` is the exception: the run stays alive so
the model can read the failed receipt plus repair hint and adjust; only a repeated
identical deny terminates the run.

A final prose reply after an unverified source edit is held once per run. A
successful or unknown-effect `apply_patch` or `agent_apply` call, or a `write`,
`edit`, `write_file` or `edit_file` call to a non-documentation path, sets the
condition until a launched `purpose=verification` check runs. The harness retracts
that reply and asks for a verification build or test; this is advice, not a
failure, and a later final reply is not held again.

Two more one-time holds can follow it, in this order, on a non-empty final reply
that the earlier checks did not take. An `open_todos` hold retracts the reply when the
latest successful `todo_write` list still has items that are not `completed`; its note
quotes up to eight of them and asks for each to be finished and checked, marked
`completed`, or removed with a stated reason. A `completion_review` hold retracts the
reply when the run edited source, no check is awaiting verification, the source-edit
hold was never issued, and the list is not fully completed; its note asks the model to
re-read the request and the changed code, confirm each explicit requirement is met and
exercised by a check, fix gaps and rerun the affected `purpose=verification` check, then
name any unchecked requirement. Neither hold fails the run or certifies verification,
and a fully completed list skips the review.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next Agent turn, and removed on session replacement, disconnect, or deletion.
It is never written to conversation history, reviewed
examples, or public payload metadata. This lets Ru answer an immediate “why?” from
the verified reason without asking the model to reconstruct or invent the
previous action state.

Legacy action-marker prose is stripped from visible output and never translated
into desktop side effects; structured selection is the only action channel.

## File and terminal boundaries

File and terminal bindings use the canonical matrix above. `plan` terminal
authority is denied. `standard` permits executable and shell launches from a canonical workspace cwd
and asks for external cwd or explicit ask rules. `full_access` may run
arbitrary terminal commands as the current Windows user against any path and may open
sockets, including outside the workspace; terminal execution has no OS
filesystem/network confinement. Repository
cleanliness is irrelevant. Full Access still has hard timeout, output cap,
cancellation, redaction, and process-tree cleanup. The structured protected
paths (harness key/database files, runtime descriptors and Ru hook and MCP configuration files) are denied in `plan`
and `standard` and allowed in `full_access`, but they never re-impose terminal
confinement.

Background terminal tasks are session-scoped and owned by the execution authority: at most
`TerminalLimits.MaxBackgroundTasksPerSession` run at once, a task that reaches
its lifetime is killed (`expired`), `terminal_stop` stops it (`killed`), and a
host restart drops every task (a stale id fails closed). The kill path kills
the process tree by parentage first and then the Job Object, so a descendant
created before the job assignment landed cannot survive. The model context
carries one bounded `background_tasks` reference per step naming the tasks that
appeared or changed state since the previous step, so a task that finished
while the model was working is noticed without polling; the GUI's compact
right-hand card lists the same session tasks, and its details panel stops one
through the `/tasks` command.

The managed Git commit workflow retains temporary-index isolation, path rollback
attribution, and refusal when the real index already contains staged user
changes. A zero exit is `succeeded`; non-zero exit and start failure are
`failed`; timeout is `timed_out`. Standard structured-file tools use the trusted
root, a session-scoped canonical folder grant, or a remembered project folder
pattern; Full Access uses an ephemeral
per-call external root. Every structured mutation binds a Broker-captured
preimage and fails with `workspace_changed` on mismatch without writing.
Existing targets are held against competing writes and rename-over; hash
validation and update or delete use that same handle. Updates retain at most
32 MiB of original bytes for ordinary failure recovery (`file_too_large` above
that bound), write in place and flush. This does not provide crash-atomic
replacement. Missing-target creation remains staged with no overwrite.
Patch rollback restores only an applied target whose current hash still
matches that patch's result; it preserves intervening editor changes.
`file_delete` keeps its bounded recovery cap and opaque recovery ID.
Terminal children receive redirected stdin that is closed immediately; TTY/editor
requests cannot receive interactive input. Literal argv is not rejected by a
global flag list; noninteractive operation and timeout/cancellation remain.

### Skill guidance (`SKILL.md`)

The shared metadata catalog advertises canonical IDs and descriptions, and the
`skill` tool loads a workflow on demand. `/skill <id> [arguments]` explicitly
activates a workflow in a chat turn; `/skills` and the GUI composer picker work
before a run exists. The picker and `/skills` management actions support local
root registration, enable/disable, automatic-selection controls and refresh.
See [skill setup, requirements, versioning and diagnostics](skills.md).

Skill bodies are untrusted guidance. `allowed-tools` is descriptive metadata
only: it never grants a tool, scope, path, network permission, approval or Broker
capability. Relative references resolve against the returned `resource_base`,
then use normal file/terminal tools under their existing policy. Nothing in the
package executes automatically.

An optional `~/.ru/AGENTS.md` carries user-wide preferences and is read before the
workspace files. `AGENTS.md`, `CONTEXT.md`, `.ru/AGENTS.md`, and `.ru/CONTEXT.md`
are the fixed instruction candidates below the run root. Their final-path,
UTF-8, per-file and combined instruction checks remain separate from catalog
metadata and skill-body budgets. These files also grant no execution authority.

## Typed command channel

`POST /agent/v1/runs/{runId}/commands` accepts only the bounded commands
`/compact normal|aggressive`, `/help`, `/skills`, `/skill <id> [arguments]`, and
`/tasks [kill <execution_id>]`.
The shared `AgentActionRouter` rejects unknown or malformed slash commands with
stable `command_unknown`/`command_invalid` codes, without creating a prompt,
history entry, or outbox message. `/compact` reuses the durable compaction
contract; `/help` is static; `/skills` and `/skill` are session-scoped; `/tasks`
lists the session's background tasks and stops one on request. The CLI
adapter uses the same parser and renders only bounded result metadata.

## Web tool gates

For the managed Agent path, Web tools use the configured Google HTML search
engine, `https://www.google.com/search`, raced against fixed free fallbacks
(DuckDuckGo HTML, Bing, Mojeek); the first engine with usable results answers,
so one blocked or slow engine no longer delays the answer. The remaining
in-flight engine requests are cancelled once a winner is known, and every
request -- winner or loser -- still passes redirect and
private-host validation. Canonical
`plan`, `standard`, and
`full_access` all allow Web; an explicit ordered ask rule may still return the
legacy `web_approval_required` reason. Redirects are validated hop-by-hop and
private/reserved targets are rejected. Responses retain byte, UTF-8,
content-type, sanitizer, and citation caps, and all returned content stays
explicitly untrusted. These are bounded Web-tool controls: web tools run
Host-side. Terminal children also run as the current user without OS-level
filesystem/network confinement (ADR-0015).

### Provider web search

A root run on first-party Anthropic (`api.anthropic.com`) or OpenAI
(`api.openai.com`) with an eligible model searches through the provider instead
([ADR 0033](../adr/0033-provider-hosted-web-search.md)). The decision is pinned
when the run's selection is pinned (`_ru_provider_web_search_v1`, removed before
any option reaches the provider) and used only when the Broker would allow
`web_search` without an approval under the run's policy; a rule that asks for or
denies `web_search` keeps the Broker tool. Such a run advertises no Broker
`web_search`: Anthropic requests declare `web_search_20250305` with `max_uses: 5`,
OpenAI requests declare `{"type": "web_search"}`. `web_fetch` stays a Broker tool.
The provider runs the query, outside the Broker and `WebPolicy`, and bills it on
the user's account. Answers that cite results end with a `Sources:` list.
Anthropic `pause_turn` responses are continued in the same step at most three
times (`provider_search_paused` after that), and a provider refusal of the tool
fails as `provider_web_search_unavailable`. Tool steps replay their search
blocks to later requests that declare the tool; any other request receives none.
Set `provider_web_search_enabled` to `false` in settings to keep the Broker
search for new runs.

## Agent activity shown in WinUI

Agent is the only supported generation path. The desktop composer uses the
known project root and exposes one bounded preset selector (`Plan` /
`Standard` / `Full Access`) plus the model picker; there is no Chat mode switch
and no workspace editor. The `Full Access` choice needs one explicit
confirmation per session, is stored server-side with a monotonic version, and
flows into runs;
a version conflict surfaces as `preset_mismatch` / `preset_version_conflict`.
The sidebar
keeps only primary conversation surfaces; Log, Diagnostics, Policy, and
Audit remain reachable from the `Công cụ` flyout. Agent prompts are
created through the authenticated `/agent/v1/*` API and observed over
SSE. The inline surface never displays raw commands, arguments, stdout,
or private payload content.

## Agent permission UX

Every Agent run is session-scoped. The chat composer offers canonical `Plan`,
`Standard` (default), and opt-in `Full Access`; the in-process execution
authority remains the only authority. The selection is stored per session: opening
a session loads its stored preset into the composer, so a `plan` session opened
later still shows `Plan` and the next send keeps it. Plan is read-only: workspace
read/Web auto, terminal/mutation denied outright, external structured access
asks. Standard auto-allows workspace mutations and workspace executable/shell launches;
external structured targets and external terminal cwd ask. Terminal/git children
in both execution presets run as the current Windows user without OS
filesystem/network confinement. Full
Access is the skip-all mode: external structured files, arbitrary terminal, and
structured protected paths (harness key/database files, runtime descriptors and Ru hook and MCP configuration files)
are automatic, while hard-invalid requests remain denied.

Workspace trust is not a preset. It is explicit once per canonical identity:
the GUI requires a guarded Approve action, while CLI `run`/`chat` require
`--trust-workspace`. A moved or recreated identity asks again. Full Access
requires its GUI confirmation or CLI `--confirm-full-access`. Host and UI only
display the server-selected pending approval kind with one bounded message per
reason code:

- `external_directory_required` — Ru cần dùng thư mục ngoài project
  trong đoạn chat này, cho đến khi ứng dụng đóng. The approval opens a
  session-scoped directory grant in the Broker; the same session
  re-uses the grant on subsequent calls, and new sessions ask again.
- `terminal_external_cwd_requires_approval` — Lệnh sẽ chạy ở thư mục ngoài
  workspace. Approval authorizes this launch; choosing always additionally
  remembers the bounded command pattern (resolved executable plus at most two
  leading non-flag argv tokens) with the exact launch directory — không phải
  một folder grant cho terminal.
- `rule_asks` — Rule của người dùng yêu cầu duyệt lời gọi này.
- Older delete/unknown/network command-classification reason codes remain
  readable in stored activity; profile v2 no longer emits them as fallback asks.
- `terminal_protected_path_requires_approval` — Lệnh terminal này trỏ vào một
  **file tự bảo vệ của harness** (`agent_harness*`, `harness_key*`, runtime
  descriptor) hoặc một path user tự thêm qua `RU_PROTECTED_PATHS`. Terminal vốn
  **không** bị protected-path confinement — `full_access` vẫn chạy như user
  hiện tại, đúng như `README.md` đã ghi — nên reason này **không** nâng quyền
  lên deny; nó chỉ để người duyệt thấy đúng câu hỏi thay vì một thư mục "ngoài"
  trung tính. Approval này **không thể nhớ vĩnh viễn**: GUI và CLI đều không hiện
  lựa chọn `allow_always` cho reason này, và Host cũng từ chối lưu rule (trả
  `rule_saved=false`, degrade thành `allow_once`), nên một lần duyệt không biến thành
  rule dự án; đúng như mọi allow khác, intent chính xác chỉ được nhớ cho session
  (mất khi đóng session hoặc restart Host).
- `terminal_timeout_requires_approval` — the standard-window gate: a terminal
  call above 15 minutes asks under `standard`, is automatic under `full_access`
  (the skip-all mode), and is denied under `plan` with the rest of terminal; a
  timeout above the protocol hard maximum is denied in every preset.
- `web_approval_required` — compatibility reason emitted only when an explicit
  ordered rule asks for Web. Canonical `plan`, `standard`, and `full_access`
  otherwise allow Web automatically; the returned authorization is
  single-use and exact-intent bound.

Standard auto-allows executable and shell launches from a workspace cwd; external
cwd and explicit ask rules still prompt regardless of repository cleanliness. Both presets launch terminal children as
the current Windows user without OS filesystem/network confinement. Full Access
removes those profile asks. The CLI exposes the same model through `agent approve` /
`agent deny`; `agent run` and `agent chat` accept `--preset
plan|standard|full_access`, `--trust-workspace`, and
`--confirm-full-access`. `--json` is accepted only with explicit trust and
Full Access confirmation.

The transcript activity feed is connection-local and transient. Runtime maps the
Core projection to an `agent.activity` SSE frame whose inner JSON contains only
the typed fields `activity_id`, `kind`, `state`, optional safe `file_name`
(basename/line range only), `line_start`/`line_end`, `symbol`, `count`, `added`, `removed`,
`duration_ms`, bounded `safe_code`, a bounded `tool_display` and `detail`, and for a
terminal row the process `exit_code` its receipt reported. It does not advance the durable event
cursor, append to the Harness event store, or enter session history.

The feed projects fourteen kinds: `thinking`, `explore`, `search`, `analyze`, `edit`,
`terminal`, `compaction`, `retry`, `todo`, `web` (`web_search` query or `web_fetch`
host), `mcp` ("server › tool"), `computer` (the action, never typed text), `skill`
(a loaded skill's name or a catalog search), `program` (`run_code`, never its source),
and a generic `status` fallback for unknown tools. `tool_display` tells apart
listing from globbing (`List`, `Glob`), tool docs (`Docs`) and terminal follow-ups
(`Output` for `terminal_read`, `Stop` for `terminal_stop`). `compaction`
reports the bounded number of covered context entries and the elapsed time of a
context compaction (the summary text never crosses the sink boundary); `retry`
reports a bounded tool transport retry attempt (any access kind, at most once
per call). Status rows can carry a bounded plain-text `detail` (for example the
inspection-gate pause with its Continue guidance). Completed tool kinds
carry a bounded `duration_ms` measured from the scheduled execution.

Assistant text uses a separate typed `assistant.delta` transient frame. The
client validates its bounded UTF-8 text and step/execution identities, then
delivers it on the UI dispatcher with `(run, model_step, transient_sequence)`
deduplication. Raw transient JSON and tool-output deltas are never exposed to
the UI or durable audit.

Each desktop row reads as one line: a glyph for the kind of work, a verb in the
row's tense ("Running", "Ran"), then the subject it acted on (file, command, query,
host, tool) and a mono tail of numbers (line range, result count, duration, line
delta), with an `exit N` badge for a non-zero terminal exit. A running verb sweeps;
failed rows turn danger-coloured and waiting rows warning-coloured. Thinking items may show only their elapsed
duration after completion. File edits and analysis show a basename and optional
line range; search and exploration show bounded symbols or counts. The temporary
user prompt and activity items use separate collections, so durable assistant
history remains unchanged when the feed is cleared.

The desktop surface renders every kind. The TUI prints the newer tool kinds
(`web`, `mcp`, `computer`, `skill`, `program`) as tool rows. It renders `thinking` through the
live status row and `compaction`, `retry` and non-`frames_dropped` `status` kinds
as transient status lines; tool kinds are settled by the durable tool events,
which remain authoritative when transient frames are dropped. Because the
durable `safe_summary` for a tool event is a bounded safe code rather than a
name, the TUI keeps the activity captured for each tool and prints one row per
tool when the durable completion arrives (`verb`, basename, line range and
`duration_ms` when the activity was delivered); a dropped frame degrades to a
code-only row instead of duplicating the event. Completed tool kinds
additionally expose their bounded `duration_ms` on the desktop item.

Before an Agent final reply is persisted, the reply policy strips private
markers. The resulting
visible text is the only assistant payload sent to the authenticated client,
model context, and conversation outbox. Assistant
activity is a post-commit effect; failures are bounded observations and do not
turn a committed run into a failed run. User activity is projected from its
outbox intent with the same preview-sanitized `agent` shape.

The feed is shown only after the first valid activity frame and is cleared on
terminal completion, cancellation, failure, disconnect, session replacement,
or reset. The inline surface still owns bounded status, workspace trust,
approval, Stop, and Continue controls. Malformed, unknown, stale, or other
content-bearing transient frames are ignored. Raw commands, arguments, stdout,
or private payload content never reach the feed, durable audit, conversation
history, or public payload metadata.
The desktop entry point also creates a real 32-character session automatically
for the first Agent prompt when the empty conversation surface has no active
session; users do not need to send a preliminary message.

Agent requests use an opaque `correlation_id`, durable run identity, and SSE
cursor. The UI presents one active run, ignores stale or uncorrelated frames,
and reconnects by replaying durable events without duplicating history. A
disconnect marks an ambiguous request as a bounded retryable error; it never
silently replays a request that might already have committed. An approved run
rehydrates and re-evaluates its durable retained batch without a new model
proposal or duplicate proposal event; the model is never asked to reissue an
approved call. An ambiguous mutation outcome pauses with a waiting status
detail, and explicit `Continue` clears the inspection gate with a
`gate_cleared_by_user` audit event in the same commit.

The short-intent resolver may answer only an exact, high-confidence greeting
or acknowledgement locally. Corrections, filesystem/path, search, network,
current-fact, and uncertain text continue through the full model context; the
resolver does not select Agent capabilities. A stale Agent session is recovered
by creating a replacement session and retrying at most once before a run exists.

## Approval, privacy, and diagnostics

External-folder and dangerous-terminal requests use a short-lived approval bound to
session, request, tool version, canonical path/executable/argv/cwd/operation/
network, snapshot version, and intent hash with 30-min expiry. Consumption is
first-writer-wins `approval_stale` (including `PendingApprovalStore` and Host
`HostApprovalLedger`). A first deny keeps the run alive with a repair hint so
the model can adjust; only a repeated identical deny terminates the current
run with `approval_denied`,
so the model cannot immediately request the same permission again. Changed
fields and expired or moved-workspace replays return `approval_stale`. Before an allow commits, the
Host asks the Broker whether it still holds the approval pending; if not, the reply is `approval_stale`
(409) and nothing is committed. The
Broker reply carries the matcher preview plus one `authorization_token`
(60s); a durable project rule is produced only for the explicit
`allow_always` decision, and it carries the bounded pattern that decision
remembered, as the Broker builds it ([Remembered Approval](../architecture/broker.md#remembered-approval)):
a terminal run call stores the resolved executable plus at most two leading non-flag argv tokens with
the exact launch directory (flag-first and shell-mode calls stay exact), an external-directory ask on a
file stores the nearest existing folder as a path pattern (a drive or share root stores nothing), and
`rule_asks` / `terminal_protected_path_requires_approval` never become a reusable rule.

Per tool, "Always allow" and session memory remember: for file tools, the canonical path, and for an
outside folder, the nearest existing folder as a durable pattern; for `run_process` and `powershell`,
the resolved executable, argv and canonical cwd (a `powershell` script exactly); for MCP `call` or
`list_tools`, the server and tool (a remembered listing never covers a call); for computer use, the exact
app list, so a request that names other apps is not covered.
`apply_patch`, `file_restore`, `run_code`, terminal read and stop, subagent controls,
web and skill calls are never remembered, and `allow_always` on them is refused with
`invalid_approval_command`. Rules saved by earlier builds for MCP or computer use carry
no identity and no longer authorize, so those calls ask again. An allow of a rememberable call additionally remembers the approved
intent in
the Host's in-process session ledger (the durable pattern when one was saved,
the exact intent otherwise), and the runtime loop materializes those
entries as run-scope allow rules when it builds the next policy snapshot for
the session, so a later matching call — including later in the same run —
evaluates as allowed without another prompt. The ledger never widens the
approval: protected-path denials (where the preset carries them) still apply,
the Broker still decides every
call, external-directory approvals keep the session directory grant (plus the
durable rule) and write no entry, each
entry expires after 30 minutes under a per-session cap, and the memory dies
with the session or a host restart. Ordering is
`consume one-time approval under snapshot N
→ execute already-authorized call under N → persist final receipt → build/bind
N+1 → invalidate other pending N decisions`; a host restart before dispatch
invalidates the ephemeral decision and causes safe re-evaluation, never
adapter invocation. File tools follow the same ordering with `tool.execution_requested`
persisted before dispatch and `FileRecoveryStore` for
recoverable deletes. The deterministic Broker approval probe uses
`write_file` (and `file_delete`/`file_restore`) only inside a fresh temporary
workspace, approves once, expects the replay to be denied as `approval_stale`,
closes the private session, and removes the workspace. Production WinUI
approval-card behavior remains covered independently by its UI contract tests.

Public Agent events and diagnostics may contain correlation ID, tool
ID/version, terminal status, stable error code, duration, verification state,
the fixed endpoint role, and an ephemeral endpoint hash. They must not contain
arguments, output values, prompts, replies, paths, scripts, stdout/stderr,
credentials, device names, or native exception text.

See [Verification](verification.md) for the separate contract, adapter, and
explicit opt-in native evidence classes. Deterministic evidence does not prove
that an arbitrary terminal command changed a live external application or
device.

## MCP servers

[ADR 0025](../adr/0025-mcp-servers-through-the-broker.md) and
[ADR 0035](../adr/0035-mcp-streamable-http.md) define this surface. Servers are
declared only in the user's own `%USERPROFILE%\.ru\mcp.json`; a workspace file is never read,
and the file is a structured protected path, so the agent cannot change it in `plan` or
`standard`:

```json
{"servers":{"github":{"command":"npx.cmd","args":["-y","@modelcontextprotocol/server-github"],
  "env":{"GITHUB_PERSONAL_ACCESS_TOKEN":{"credential":"ru-mcp-github"}},
  "description":"GitHub issues and pull requests","timeout_seconds":60}}}
```

Names are `[a-z][a-z0-9_-]` up to 32 characters; `cwd`, when given, must be absolute;
`"enabled": false` hides a server; at most 16 servers are read from a file of at most 64 KB.
An `env` value is literal text or `{"credential": "<name>"}`, a Windows Credential Manager
generic entry (for example `cmdkey /generic:ru-mcp-github /user:mcp /pass:<token>`) read
only when the server starts. Resolved credential values are redacted from every result and
error the tool returns.

For stdio, the Broker execution runtime starts a server on its first `list_tools` or `call`, at most one
instance per Host, inside a kill-on-close job object, as the current Windows user with the
credential-stripped agent environment plus its declared variables. A server whose
declaration changed or that exited is restarted; all servers stop with the Host. Startup is
bounded to 20 seconds and each request to the server's `timeout_seconds` (default 60,
at most 600). `arguments` is a JSON object encoded as a string (at most 64 KB) and follows
the schema `list_tools` reports. Text, structured content and resource-link metadata
are returned within the shared 256 KB bound and marked `untrusted`; images and
other unsupported content are counted, not returned. Failures are named:
`mcp_server_unknown`, `mcp_server_unavailable` (with the server's last stderr, redacted),
`mcp_credential_missing`, `mcp_arguments_invalid`, `mcp_timeout`, `mcp_error` and
`mcp_unavailable`. Ru does not confine what a server does once it runs.

An explicit remote declaration instead uses:

```json
{"servers":{"remote":{"transport":"streamable_http","url":"https://mcp.example.com/mcp",
  "bearer_credential":"ru-mcp-remote","timeout_seconds":60}}}
```

This transport pins MCP **2025-11-25**, supports JSON POST responses and
request-scoped SSE, and retains the same `ExternalTerminal` approval decision.
Only HTTPS or loopback HTTP is accepted; redirects and transport fallback are
disabled. Session and protocol headers are validated. Authentication failure is
`mcp_auth_required`, an expired session is `mcp_session_expired`; neither replays a
tool call. Bearer credentials use the existing user credential store. OAuth login
and refresh, autonomous GET streams, prompts, resource reads and sampling are
not implemented. Resource links are metadata, not automatically fetched content.

## Computer use

[ADR 0031](../adr/0031-computer-use-through-the-broker.md) defines this surface. It is off by
default: turn it on in **Customize → Plugins → Computer use**, or with `/computer on` in the
terminal UI. The setting is read when a new run pins its provider, and only a model that
accepts images gets the tool, so a run never changes mid-way.

- `list` returns monitors (primary first) and apps with visible windows, each marked
  `granted`, `not_granted`, `full_access` or `protected`; window titles appear only for apps
  with access.
- `request_access` takes 1 to 8 executable names (`notepad.exe`; a bare name gains `.exe`)
  in canonical order. In `standard` it asks once; approving records an **app grant** for the
  rest of the Agent session. "Always allow" saves a project rule for exactly that app list, so a later request that names other apps is not covered by it.
- `focus` takes one `app` and brings its topmost window to the front, restoring it when
  minimized. In `standard` the app needs a grant. Ru activates the window without sending
  any input to the window it replaces. Use it when another window covers the app.
- `screenshot` returns a PNG of one monitor, at most 1,280 px on the long edge, through the
  [image read](#image-reads) path, so only a model that accepts images receives it and only
  the newest four images reach a request. In `standard` every pixel outside a granted app's
  visible windows is black; the window list is read before and after the capture and a pixel
  stays only when both allow it.
- `click`, `move`, `drag` and `scroll` take `x` and `y` in pixels of that monitor's latest
  screenshot. `type` sends up to 2,000 characters and `key` a chord such as `ctrl+s` to the
  foreground window. Before each input event the Broker resolves the target window again.

UWP apps such as Calculator are named by their own executable, not by
`ApplicationFrameHost.exe`, which can never be granted. The Windows 11 Start menu belongs to
`StartMenuExperienceHost.exe`, so typing into it after `win` needs that app's access too.

Named failures: `computer_app_not_granted`, `computer_app_protected` (Ru, terminal hosts,
`cmd`/`powershell`/`pwsh`, sign-in and elevation UI, and any process that hosts a running Ru
CLI, such as an editor's terminal, in every preset), `computer_target_changed` (the window
under the pointer or in front changed during a click or while typing),
`computer_target_elevated`, `computer_key_not_allowed` (the Windows key without
`explorer.exe` access), `computer_screenshot_required`, `computer_display_changed`,
`computer_point_outside_screenshot`, `computer_app_not_running` and `computer_focus_refused`
(focus found no window, or Windows kept another window in front), `computer_busy` (another run holds the desktop until it
ends or is idle for two minutes), `computer_desktop_unavailable` (locked or secure desktop),
`computer_stop_unavailable` (the stop hotkey could not be registered),
`computer_stopped_by_user`, `computer_root_run_required` (a subagent or program) and
`image_unsupported_by_model`.

**Ctrl+Alt+Backspace** (or **Ctrl+Alt+Pause** on keyboards that have it) stops computer use at any time: it cancels the action in flight and
cancels the controlling run. Observation actions report no side effects and may repeat;
input actions are not structured source edits and do not change verification (ADR 0039). Ru does not confine
what a granted application does with the input.

## Hooks

`~/.ru/hooks.json` and `<workspace>/.ru/hooks.json` can bind shell commands to tool calls:

```json
{"hooks":{"PreToolUse":[{"matcher":"powershell|run_process|edit_file","command":"scripts\\check.cmd","timeout_seconds":30}],
          "PostToolUse":[{"matcher":"write|edit","command":"scripts\\after-edit.cmd"}]}}
```

`matcher` is a case-insensitive regular expression over the whole tool id (omit or `*` for all tools); `timeout_seconds` is 1-120 (default 30). The Host decorates the tool gateway: a **PreToolUse** hook runs before policy evaluation, and exit code `2` rejects the call with the code `hook_blocked` and its stderr (first 200 characters) as the model-visible hint, so no approval is requested. Any other non-zero exit, a timeout or a start failure never blocks. A **PostToolUse** hook runs after the receipt and cannot change it.

Two run-level events use the same file. **UserPromptSubmit** runs when a run is created, before anything of it is written; exit code `2` refuses the prompt with HTTP 422 `prompt_blocked_by_hook` and the hook's stderr (first 200 characters) as the message, so no run exists. Its stdin carries `event`, `session_id`, `workspace`, `workspace_trusted` and `prompt` (null with `prompt_omitted: true` above 32 KB). Until the workspace is trusted only the user's own `~/.ru/hooks.json` prompt hooks run, and they start in the user's profile folder so `cmd.exe` cannot resolve a bare program name against workspace files. **Stop** runs once when a run finishes, without being waited for, with `event`, `run_id`, `session_id`, `workspace`, `workspace_trusted`, `status` and `terminal_error_code`; it cannot change the run. Trust is checked when the hook fires: a run that finished because trust was declined or expired, or whose trust cannot be read, runs only the user's own Stop hooks, from the profile folder. Both ignore `matcher`.

The hook runs through `cmd.exe` in the workspace root with the same credential-stripped environment as agent terminal children plus `RU_HOOK_EVENT`; it receives one JSON object on stdin (`event`, `tool_id`, `tool_call_id`, `run_id`, `workspace`, `arguments` unless larger than 32 KB, and for PostToolUse `outcome` and `code`). Files over 64 KB, links and malformed JSON are ignored; at most 32 hooks per event are read. A workspace `hooks.json` is workspace-supplied code that runs as your Windows user once tools are evaluated, so open only workspaces you trust. Hooks are loaded when a run's gateway is created, apply to subagents, and are not a security boundary. Because a hook's command runs outside terminal policy, `.ru\hooks.json` (in the workspace or the user profile) is a structured protected path: the model's `write_file`/`edit_file` are denied in `plan` and `standard`. A terminal command in `standard` can still write the file; that residual is the terminal launch authority `standard` already grants.

### Declaring terminal checks

Set `purpose` on the terminal tools (`powershell`, `run_process`) to declare intent:

- `purpose=verification` marks a required build or test. Its exit zero verifies the task.
- `purpose=expected_failure` with an exact nonzero signed 32-bit `expected_exit_code` marks a
  deliberately failing test. Only that observed exit satisfies it. A zero exit, a denial, a timeout,
  a cancellation or an unknown outcome never does.
- `purpose=inspection` marks read-only diagnostics. It is not enforced confinement.
- The default `command` is for everything else.

Verification is advisory ([ADR 0039](../adr/0039-advisory-verification.md)). It records evidence and
holds final replies, but it never grants or denies execution:

- A failed, pending or result-missing declared check is outstanding. Only the same canonical check
  passing later retires it. Unrelated reads, other passing checks and summaries do not.
- A final reply with an outstanding check is held with a recovery note up to three times per run.
  After that the run completes, and its summary verification state is `unverified`. A `[BLOCKED]`
  reply still ends the run unsuccessfully.
- An expected-exit mismatch is an outstanding failed check under the same rule. Report the observed
  exit. Do not rerun a deliberately failing command to force the expected code, and a different
  invocation does not clear the original check.
- A `verified` summary needs a declared check that ran after the last structured source edit and no
  outstanding check. Structured source edits are `write`, `edit`, `write_file`, `edit_file`,
  `apply_patch` and `agent_apply` outside documentation paths. Terminal commands, computer input,
  deletes and restores are not source edits.
- Commands do not invalidate earlier checks. A background check that is still running is pending:
  read its terminal output until it exits, and do not start a duplicate.
- `supersedes` is accepted and ignored. It is not checked for `purpose=verification`, and it retires
  nothing. Its schema bounds (1 to 16 entries) still apply.
- A declared check rejected before launch (a malformed call, a policy denial or an unresolvable
  executable) observed nothing and creates no obligation. Its failed receipt still counts as a tool
  failure.

Clean up temporary scripts before final checks. Canonical identity is the resolved executable, the
exact argv and the canonical working directory, so cwd aliases resolve the same check. Authenticated
receipts identify the actual resolved invocation. Authority-derived metadata grants no execution
permission. See [receipt-backed verification](../architecture/harness.md#receipt-backed-verification).

The model-facing text describes this behavior. The `purpose` description says that a command is not a
declared check and that a failed or pending required check stays outstanding until the same check
passes. The `supersedes` description says that it is deprecated, ignored and retires nothing.
Verification emits no repair families or repair hints, and it never emits `verification_repair_identity`
or `verification_invocation_invalid`. Receipts that still carry those fields parse, and the fields are
ignored.

### Evidence retention and model continuation

Verification retains at most 256 required checks independently from 64 recent
diagnostic checks. Passed negative tests are diagnostics; unresolved declared
checks are never evicted to admit optional failures. Recent expected-failure
receipt identities are retained for the latest bounded tool batch without
turning successful negative tests into a tracking-limit failure. Exhausting
required evidence still reports `verification_tracking_limit`.

The model receives one effective continuation per pageable result:
`next_offset` with `has_more`, or `next_line` with `has_more` for line reads.
The Host adjusts it when fitting context and preserves provenance; original
Broker receipt bytes are unchanged. A truncated incomplete scan with no source
cursor has no invented continuation: narrow the query. Offset units remain
UTF-8 bytes for read, UTF-16 characters for web/terminal output, and match indices
for grep. Raw SDK results retain their native receipt metadata.

## Registering a model-visible tool

A model-visible tool is one row in `RuAgent.Protocol/Tools/AgentToolDescriptorTable.cs`
([ADR 0038](../adr/0038-tool-descriptor-table.md)). Its Protocol definition, Broker manifest,
trusted adapter, session scopes and access class derive from that row. To add a tool:

1. Add one row at its position in model order. Its ordinal equals its index, and it carries the
   current definition version, primary and extra scopes, risk, approval requirement, adapter,
   workspace requirement, access class, optional output bound, description and input schema builder.
   The access class is a literal on the row. Only computer observation actions are refined, in
   `ToolAccessClassification`.
2. Write the input schema as a strict object (`additionalProperties: false`). Any example must
   validate against it. The catalog rejects a non-strict schema or a failing example at construction.
3. Bind an executor in the Broker execution runtime. `ToolCatalog` rejects an executor without a row
   and a bound row without an executor.
4. Teach `PolicyContextFactory` the tool's policy identity: its operation class, its `op` value and
   whether it is target-free. Its switch throws `malformed_call` for an id it does not know, which
   denies every call before execution.
5. For a new risk or adapter value, add an explicit arm in `ToolDescriptorMapping`. Unmapped values
   throw. The trusted adapter allowlist follows the `AgentToolAdapter` enum automatically.
6. Add deliberate membership where the tool should be advertised: a coding profile in
   `AgentToolProfiles.Includes`, a subagent role in `SubagentRoleTools`, or a program tool in
   `ProgramToolContract.SupportedTools`. No tool is advertised by default.
7. When a schema digest, security column or definition changes, update the golden locks
   (`AgentToolGoldenLockTests`, `ToolManifestGoldenLockTests`) and the counts on this page and in
   [broker.md](../architecture/broker.md).
