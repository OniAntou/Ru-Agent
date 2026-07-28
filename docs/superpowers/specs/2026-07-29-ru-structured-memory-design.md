# Ru Structured Memory Design

**Date:** 2026-07-29
**Status:** Approved for implementation planning

## Goal

Give Ru an automatic, local long-term memory that reliably retains both the
user's stable preferences and relevant project context. The design must replace
raw transcript retrieval as the default memory experience without interrupting
chat with confirmation dialogs.

## Decisions

- Memory is automatic by default. Ru extracts only clear, useful facts after a
  completed turn; it does not interrupt the conversation to ask for approval.
- The source of truth is a local SQLite database. Semantic search is a
  retrieval aid, not the authoritative store.
- The system distinguishes profile, preference, project, decision, and
  session-summary memory. Raw conversation pairs remain a migration/fallback
  artifact, not a prompt input by default.
- Ru must not automatically save sensitive data: credentials, API keys,
  passwords, precise addresses, financial/health data, or excessively private
  content. Existing VTube Studio control metadata remains excluded from chat,
  TTS, history, approved examples, and memory.
- The UI is intentionally low-friction: automatic behavior first, with an
  optional compact Settings panel for inspection, search, pinning, editing,
  deletion, and clearing all memory.

## Data model

The initial implementation uses SQLite from Python's standard library. A
`memories` record includes:

- stable id and normalized content;
- `kind`: `profile`, `preference`, `project`, `decision`, or `session_summary`;
- optional `scope` (for example, a project path/name);
- source turn id and evidence excerpt;
- confidence, importance, creation/update/access timestamps, and optional
  expiry timestamp;
- `active`, `superseded`, or `deleted` lifecycle status; and
- `pinned` flag.

SQLite FTS provides lexical lookup. The existing FAISS-based semantic index is
rebuilt from active records and stores only the memory id mapping plus
embeddings. It never becomes the sole copy of a fact.

## Write flow

After a clean assistant response completes:

1. Sanitize both sides of the turn, including removal of VTS/action metadata.
2. Apply a rule-first extractor for explicit preferences, identity/addressing,
   project paths, durable project decisions, corrections, and stated follow-up
   commitments.
3. Reject empty, ambiguous, transient, duplicated, unsafe, or sensitive
   candidates.
4. Normalize the candidate and compare it with active memory in its kind and
   scope. A clear correction supersedes the earlier fact; a duplicate updates
   timestamps rather than adding a new row.
5. Commit SQLite first, then update the semantic index. If indexing fails, the
   committed record remains searchable by FTS and is queued for rebuild.

The first version deliberately avoids a second LLM inference after every turn.
That prevents extra GPU contention and response latency. A model-assisted
extractor is a future measured enhancement, not part of this scope.

Session summaries are generated only at a bounded turn/character threshold or
when starting a new session. They capture open work, decisions, and unresolved
questions in a short scoped record; newer summaries supersede older summaries
for the same session/project.

## Retrieval flow

For each user prompt:

1. Detect likely scope and intent from explicit paths/names and prompt terms.
2. Retrieve candidates with FTS plus semantic similarity from active records.
3. Rank by relevance, exact scope match, importance, recency, confidence, and
   pinning; penalize expired records and contradictions.
4. Select a small, deduplicated context (target: three to five concise facts
   within a fixed token budget) and attach provenance internally.
5. Add only this context to the model instruction. Never inject raw saved
   transcripts by default.

If no candidate clears the relevance threshold, no memory context is supplied.

## Lifecycle, privacy, and recovery

Preferences and profile facts are long-lived. Project memories and session
summaries receive a default expiry and decay in ranking; pinned records never
expire automatically. Explicit corrections supersede rather than erase prior
records, allowing a user to inspect and restore history later. User deletion
removes the record from SQLite and the semantic index.

When the database or index is unavailable, Ru must keep chatting without
memory for that turn, surface a concise status message, and preserve no partial
write. An index mismatch is recoverable by rebuilding from active SQLite rows.
Legacy `memory_texts.json` data is imported only through a conservative,
deduplicated migration that labels it as legacy conversation memory; invalid or
sensitive blocks are skipped and reported.

## User interface

Settings retains the existing remember-memory toggle and clear-all action. A
small Memory panel adds record count, search, kind/scope display, pin, edit,
individual delete, and rebuild status. None of these controls are required for
ordinary use and none cause confirmation prompts during normal automatic
saving. Clear-all retains confirmation because it is destructive.

## Verification

Tests must cover:

- explicit preference and project extraction;
- rejection of sensitive, ambiguous, VTS, and duplicate candidates;
- correction/supersession and expiry behavior;
- scoped hybrid retrieval, ranking, relevance threshold, and fixed context
  budget;
- clean deletion from SQLite and semantic index;
- FTS-only operation and recoverable index rebuild;
- legacy import without treating raw transcript blocks as default prompt
  context; and
- end-to-end GUI behavior with automatic memory on/off and a real clear action.

Acceptance uses a small authored memory evaluation set: Ru must recall a
relevant preference and project decision, prefer a corrected fact, avoid
unrelated or expired memory, and never retain the prohibited sensitive/control
content.

## Non-goals

- Cloud synchronization, multi-device memory, or a hosted database.
- Automatic model training from memory or approved examples.
- A model-assisted extractor in the initial release.
- Replacing the current session conversation history; structured memory
  complements it.
