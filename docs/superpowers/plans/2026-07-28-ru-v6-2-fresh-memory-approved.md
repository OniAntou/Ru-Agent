# v6.2 fresh corpus: memory and approved merge plan

> Execute inline in the current `D:\LLM` checkout. The user explicitly
> authorized merging the current memory and approved sources into the new
> candidate. Do not start training during this plan.

## Constraints

- Preserve the historical `data/conversations/ru_v6_2`; write only to the new
  `ru_v6_2_fresh` candidate.
- Keep the authored core and its deterministic validation/held-out split
  unchanged when imports are added.
- Imports are train-only, project-owned, deduplicated, repaired from the known
  mojibake, and rejected when unsafe, explicit, meta, malformed, or noisy.
- Every dropped source row has a stable reason in the manifest; rejected text
  must not be written to the candidate.
- Training remains bounded by the approved 480-step / 9,600-second config and
  is a separate future task.
- The current `ru_style.py` personality is authoritative: use its
  language-specific system prompt, preserve natural bilingual switching, end
  targets with `Nya~`, and keep VTS markers out of text.

## Task 1 — Write importer tests first (red)

Add `tests/test_ru_v6_2_fresh_import.py` with temporary approved JSONL and
memory JSON fixtures covering:

1. mojibake repair and `User:`/`Assistant:` parsing;
2. one accepted approved row and one accepted memory row with provenance;
3. explicit, AI-meta, URL/list, malformed, empty, duplicate-prompt, and
   duplicate-pair rejections with reasons;
4. train-only append where validation and held-out rows are byte-for-byte
   unchanged;
5. manifest source hashes and accepted/rejected counts.

Run the focused test file and confirm it fails because the importer does not
exist yet.

## Task 2 — Implement the safe importer

Create `src/mini_gpt/ru_v6_2_fresh_import.py` exposing:

- `load_approved_rows(path) -> list[SourceTurn]`;
- `load_memory_rows(path) -> list[SourceTurn]`;
- `select_import_rows(approved_path, memory_path) -> ImportSelection`;
- `append_imports_to_train(dataset_dir, selection) -> dict[str, object]`.

Use small immutable dataclasses for source turns and selection/report data.
Repair mojibake only when the round-trip improves known marker characters;
otherwise preserve the original Unicode. Normalize with the repository text
utility, derive `vi`/`en`/`mixed` conservatively, and build the existing Llama
message schema. Put source id/index in metadata only where the corpus schema
allows it; do not expose control metadata in displayed text.

Run the focused tests until green, then run the existing approved-import and
memory tests to catch regressions.

## Task 3 — Build the new authored core and merge imports

Implement the fresh scene/corpus modules and CLI named in the approved design.
Build the 1,200-row authored core, deterministic train/val/held-out splits,
then append only accepted imports to train. The CLI must be idempotent: running
it twice produces the same rows, hashes, and rejection report.

## Task 4 — Audit and verify

Run fresh corpus tests, importer tests, and the CLI audit. Inspect a
deterministic stratified sample of at least 60 authored rows plus every
accepted import. Confirm no source sentence from `roleplay_chat.md` was copied,
no rejected source text is present, and all import provenance/hash counts match
the manifest. Check project docs (`README.md`, `TODO.md`, handoff) for stale
v6.2 statements and update only if the new candidate status requires it.

## Task 5 — Handoff

Report authored/imported counts, rejection reasons, split sizes, safety gate
status, and exact next training command. Do not claim v6.2 adoption or start a
three-hour training run in this dataset task.
