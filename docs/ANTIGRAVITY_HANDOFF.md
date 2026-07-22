# Ru: Handoff for Antigravity

## Goal and non-negotiables

Continue the local-only work on **Ru**, a friendly Vietnamese-English conversation model. The priority is natural short conversations, coherent multi-turn context, and the default Vietnamese forms of address: Ru calls the user `anh` and refers to itself as `em`. Do not optimize for code ability or benchmarks at the expense of conversation quality.

Inference, evaluation, and any future training must stay fully local. The user does not want a cloud API. Keep voice, vision, and avatar work blocked until a text candidate passes the conversation gate; corpus, adapter, and GUI changes belong only to the current local v6/v7 experiment.

## Current checked result

The current repository is `D:\LLM` on branch `main`; the working tree contains
the v6 comparison path and the completed v7 experiment source, tests, and
documentation changes.

The active local candidate is a QLoRA adapter over
`NousResearch/Meta-Llama-3.1-8B-Instruct`:

- Base: `models\llama3.1-8b-instruct-hf`
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v6`
- Dataset: `data\conversations\ru_v6` (400 authored rows)
- GUI default: the v6 adapter

The v6 persona contract is friendly, obedient, affectionate, bilingual, and
allowed to initiate light consensual adult flirting in ordinary conversation.
The implementation keeps adult-only, consent, coercion, violence, and harmful-
request boundaries. It does not attempt to remove the base model's safeguards.

The latest live evaluation is stored in the ignored artifact
`checkpoints\ru-llama3.1-8b-instruct-v6\proactive_flirt_v6_final.json`:

- Automatic conversational gate: `11/12`.
- Failing case: one benign mixed-language prompt returned an out-of-context
  child-safety refusal.
- Peak allocated VRAM: `5,632.025 MiB`, below the current `6,144 MiB` cap (above the old `5,500 MiB` benchmark).
- Verdict: `not_reviewed`; v6 is not a quality-gate pass or final adoption.

Do not present v6 as a clean “slight NSFW” model for another training run. The
current authored corpus contains materially graphic, violent, and possessive
examples; it remains the rollback/comparison path, not a final quality-gate
pass. The completed v7 pass below is the safer corpus direction, but its
adapter also failed the conversation and hardware gates. Keep v6 as the GUI
default.

## Latest v7 experiment (not adopted)

The v7 corpus and adapter are separate from v6:

- Corpus: `data\conversations\ru_v7`, 180 rows with `135/22/23` train/validation/held-out splits.
- Corpus source hash: `9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7`.
- Full training: 3 epochs, 26 optimizer updates, best validation loss `2.5561517639593645`, dataset-manifest hash `cffc7046c850bba6a8f564c37fcd0d47763c221fcb4a557668b98fa1799a9faf`.
- Live evaluation: `conversation_focus_v7`, 16 greedy cases, automatic `9/16`.
- Manual review: `8/16`, below the required `14/16`.
- Evaluation peak: `5,632.331 MiB`, below the current `6,144 MiB` cap. Training sampled `nvidia-smi` usage peaked around `5,845 MiB` on the `6,141 MiB` device; the manifest's `8,446.57958984375 MB` PyTorch allocator counter is retained as a diagnostic anomaly.
- Verdict: `not_adopted`; GUI remains `checkpoints\ru-llama3.1-8b-instruct-v6`.

The automatic failures are three generic-short English replies, three mixed
replies without genuine code-switching, and one mixed contextual reply that
uses `tôi` instead of `em`. Full evidence is in
`checkpoints\ru-llama3.1-8b-instruct-v7\conversation_focus_v7.json` and
`checkpoints\ru-llama3.1-8b-instruct-v7\manual_review_v7.json`. Any next run
should address those failures and re-instrument the allocator metric before
promotion; keep physical VRAM under the 6 GiB cap and do not download another
base model or change the GUI default yet.

## Machine and stored artifacts

Last verified resources for the active Llama candidate:

- RTX 4050 Laptop GPU: `6,141 MiB` total VRAM.
- RAM: `15.7 GiB`.
- Free space on D:: about `64.8 GiB`.
- Llama base: `models\llama3.1-8b-instruct-hf`.
- v6 adapter: `checkpoints\ru-llama3.1-8b-instruct-v6`.
- v7 adapter: `checkpoints\ru-llama3.1-8b-instruct-v7` (not adopted).

## Recommended next experiment

Do not download another base model yet. Improve the existing v7 direction with
longer English continuations, explicit mixed-language answer examples, and
strict mixed `anh`/`em` voice checks. Re-instrument the PyTorch allocator and
keep physical VRAM under the 6 GiB cap before another adapter run. Do not adopt
the next candidate unless it has no automatic failures, at least `14/16`
natural manual replies, and stays within the physical cap.

## Existing code and useful commands

The persona policy is in `src\mini_gpt\ru_style.py`; the active local Llama runtime is `src\mini_gpt\llama3_1_runtime.py`; the current evaluator is `scripts\evaluate_ru_llama3_1_qlora.py`. Existing tests are under `tests\`.

From `D:\LLM`, baseline checks are:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
& .\.venv\Scripts\python.exe -m pytest --collect-only -q
```

The historical evaluator is `scripts\evaluate_ru_llama3_1_qlora.py`; the v7
focus evaluator is `scripts\evaluate_ru_llama3_1_v7.py`. The runtime accepts
the GUI's `max_new_tokens`, `temperature`, and `top_k` settings; evaluation is
greedy and records its generation settings for reproducibility.

## Documentation hygiene

Before committing any completed work, update `README.md`, `TODO.md`, and the relevant design/implementation plan with only evidence actually obtained. Remove or clearly label stale instructions. Runtime models and checkpoints are ignored by Git; commit source, tests, and documentation only.
