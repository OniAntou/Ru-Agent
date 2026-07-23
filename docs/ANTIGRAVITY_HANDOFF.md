# Ru: Handoff for Antigravity

## Goal and non-negotiables

Continue the local-only work on **Ru**, a friendly Vietnamese-English conversation model. The priority is natural short conversations, coherent multi-turn context, and the default Vietnamese forms of address: Ru calls the user `anh` and refers to itself as `em`. Do not optimize for code ability or benchmarks at the expense of conversation quality.

Inference, evaluation, and any future training must stay fully local. The user does not want a cloud API. Keep voice, vision, and avatar work blocked until a text candidate passes the conversation gate; corpus, adapter, and GUI changes belong only to a candidate that passes the local bilingual gate. The current comparison set includes the Llama v6/v7/v7.1/v8 work, the Llama 3.2 base-only probe, the Gemma 4 E4B isolated text-only baseline, and the Qwen3.5 base-only probe.

## Current checked result

The current repository is `D:\LLM` on branch `main`; the working tree contains
the v6 GUI comparison path, the completed v7/v7.1 experiment source, the v8
true-continuation source and adapter, and the historical base-only evaluator
and documentation changes.

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
pass. The completed v7 and v7.1 passes below are safer corpus directions, but
both failed the conversation gate. Keep v6 as the GUI default.

## Historical v7 experiment (not adopted)

The v7 corpus and adapter are separate from v6:

- Corpus: `data\conversations\ru_v7`, 180 rows with `135/22/23` train/validation/held-out splits.
- Corpus source hash: `9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7` (removed 2026-07-23; source and metrics retained).
- Full training: 3 epochs, 26 optimizer updates, best validation loss `2.5561517639593645`, dataset-manifest hash `cffc7046c850bba6a8f564c37fcd0d47763c221fcb4a557668b98fa1799a9faf`.
- Live evaluation: `conversation_focus_v7`, 16 greedy cases, automatic `9/16`.
- Manual review: `8/16`, below the required `14/16`.
- Evaluation peak: `5,632.331 MiB`, below the current `6,144 MiB` cap. Training sampled `nvidia-smi` usage peaked around `5,845 MiB` on the `6,141 MiB` device; the manifest's `8,446.57958984375 MB` PyTorch allocator counter is retained as a diagnostic anomaly.
- Verdict: `not_adopted`; GUI remains `checkpoints\ru-llama3.1-8b-instruct-v6`.

The automatic failures were three generic-short English replies, three mixed
replies without genuine code-switching, and one mixed contextual reply that
uses `tôi` instead of `em`. Full evidence is in
The local v7 adapter and checkpoint-local evidence were removed on 2026-07-23
and sent to the Recycle Bin; source, metrics, and evaluator code remain. Any
next run should address those failures and re-instrument the allocator metric before
promotion; keep physical VRAM under the 6 GiB cap. At that checkpoint no other
base model had been downloaded; the later Llama 3.2 trial is recorded below,
while the GUI default remains unchanged.

## Latest v7.1 repair experiment (not adopted)

V7.1 combined the unchanged v7 source with 120 explicitly authored repair rows
and trained into a separate adapter:

- Corpus: `data\conversations\ru_v7_1`, 300 rows with `228/36/36` train/validation/held-out splits; language counts `vi=96`, `en=120`, `mixed=84`.
- Source hashes: base `9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`, repair `27a785ab956213d5372bee4edfa2f35d5081b307fa700d77a6abfdeb8a766bcc`, combined `b4eb7bca4841ed3bdfcab56f25ce9126869d0e7a28749f734855fafbeeb73905`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7.1` (removed 2026-07-23; source and metrics retained).
- Training: 2 epochs, 29 optimizer updates, best validation loss `2.5795224606990814`, dataset-manifest hash `b9262441633f492007cd16ed30479bf174dcc656a7109437f4f103f52d2c8582`.
- Physical training peak: `5,874 MiB`; evaluation peak: `5,850 MiB`; both passed the `6,144 MiB` cap on the `6,141 MiB` GPU. The training manifest's `8,446.57958984375 MiB` PyTorch allocator value remains diagnostic only.
- Evaluation: `conversation_focus_v7_1`, 24 greedy cases; automatic `10/24` and manual `14/24`. The original v7 slice scored `10/16` manually.
- Verdict: `not_adopted`; GUI remains `checkpoints\ru-llama3.1-8b-instruct-v6`.

The exact automatic failure IDs were recorded in the checkpoint-local
evidence before the adapter was removed on 2026-07-23 and sent to the Recycle
Bin. They are dominated by one-language collapse on mixed prompts, generic-short English
replies, and missing follow-ups. Do not promote v7.1 or describe it as better
than v6. The next permitted direction at that point was focused error analysis
and a bounded repair for mixed-language preservation and English response
depth. The later Llama 3.2 trial is a separate base-only comparison below; the
GUI default remains unchanged.

## Latest v8 true-continuation experiment (not adopted)

V8 is the first Llama continuation that loads the v6 adapter with
`PeftModel.from_pretrained(..., is_trainable=True)` instead of starting a new
LoRA adapter from the base model:

- Corpus: `data\conversations\ru_v8`, 336 rows with `262/37/37` splits.
- Corpus composition: 48 quality-filtered v6 replay rows and 288 v7.1
  correction rows; common-suite prompts were excluded and final prompts were
  kept within one split.
- Corpus manifest hash: `31d6f09e4351bbb0d8a8ffd5005c638e50c9ef5255db99a007d216e8c64296a0`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v8`; source adapter is v6.
- Training: one epoch, 17 optimizer updates, `max_length=512`, learning rate
  `1e-5`, best validation loss `2.902062905801309`.
- Training physical peak: `5,865 MiB`; the `6,144 MiB` physical cap passed.
- Common evaluation: v6 `11/24`, v8 `14/24`; original v7 slice v8 `12/16`
  automatically. V8 improved three v6 failures and introduced no automatic
  regression on a v6-passed case.

V8 remains `not_adopted`: it still has 10 automatic failures, mainly mixed
language collapse and missing follow-ups, plus one generic-short English
reply. It does not meet the zero-failure, `21/24`, and `14/16` adoption gate.
Keep the GUI on v6. Evidence is in
`eval_results\ru_llama3_1_v6_common_v7_1.json`,
`eval_results\ru_llama3_1_v8_common_v7_1.json`, and
`checkpoints\ru-llama3.1-8b-instruct-v8\ru_manifest.json`; the gate-status
record is `checkpoints\ru-llama3.1-8b-instruct-v8\manual_review_v8.json`.

The v8 training manifest reports `8,446.57958984375 MiB` from the PyTorch
allocator counter while sampled `nvidia-smi` physical usage peaked at
`5,865 MiB`; retain both values and treat the allocator value as a diagnostic
anomaly until the instrumentation is reworked.

## Latest v6.1 natural-style continuation (not adopted)

V6.1 was a bounded style-only continuation from the v6 adapter. It used a
fresh authored corpus and kept the GUI default on v6:

- Corpus: `data\conversations\ru_v6_1`, exactly 120 rows with `84/18/18`
  train/validation/held-out splits; language counts `vi=60`, `mixed=36`,
  `en=24`; 72 contextual rows; 18 identity-correction rows; 7 light-flirt
  rows.
- Corpus source hash: `7b0abe74daf3be33678791cac38d6760ef660b709e8725cd2a9ead0ce9680359`.
- Dataset manifest hash used by training: `53a9f75236121cdad38bd014e785f6db2fd191389f7be388610d05036b442321`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v6.1`, source adapter v6.
- Training: 3 epochs, 16 optimizer updates, learning rate `5e-6`, best
  validation loss `3.156519373257955`.
- Physical training peak: `5,841 MiB` against the `6,144 MiB` cap;
  `physical_cap_passed=true`. The `8,446.57958984375 MiB` allocator counter
  remains diagnostic only.

The same local evaluator used 30 case summaries plus the exact four-turn
screenshot replay, greedy `max_new_tokens=64`. V6.1 reached `13/30` case
summary passes and `11/24` on the common suite; v6 reached `12/30` and
`11/24`. V6.1 improved two case summaries and regressed one. Both v6 and v6.1
failed the first screenshot replay turn with a generic-short reply. V6.1 also
retained mixed-language collapse and missing-follow-up failures. Evaluation
physical peaks were `5,837 MiB` for v6.1 and `5,833 MiB` for v6, so the
hardware gate passed but the automatic conversation gate did not.

V6.1 is `not_adopted`; manual review was skipped after the hard automatic
failure, and no candidate GUI smoke was promoted. Evidence is in
`eval_results\ru_llama3_1_v6_style.json`,
`eval_results\ru_llama3_1_v6_1_style.json`,
`checkpoints\ru-llama3.1-8b-instruct-v6.1\ru_manifest.json`, and
`checkpoints\ru-llama3.1-8b-instruct-v6.1\manual_review_v6_1.json`. Keep the
GUI on `checkpoints\ru-llama3.1-8b-instruct-v6`.

## Latest v6.2 dataset preparation (training not run)

The v6.2 corpus is a new, dataset-only continuation candidate authored from
the v6 adapter path. It deliberately does not inherit v6.1's source corpus or
sexual prior. The build and quality gates passed on 2026-07-23:

- Dataset: `data\conversations\ru_v6_2`, exactly 2,400 rows with
  `1680/360/360` train/validation/held-out splits.
- Language counts: `vi=1200`, `mixed=720`, `en=480`.
- Mode counts: everyday/follow-up `900`, story/context `480`, emotion/support
  `360`, language-switch `240`, identity/repair `180`, humour `120`, and
  light-flirt `120`.
- Intensity counts: neutral `2040`, light `288`, adult-boundary `72`; no
  graphic sexual, violent, coercive, minor-related, or possessive assistant
  targets passed into the corpus.
- Turn bands: short `480`, medium `1200`, long `720`; all long rows retain an
  earlier context signal, and all rows have alternating history.
- Source SHA-256: `ad509760b8e87c7a6f0e11c4c1ee309a257a9766e265ee153ec170a42c450eb1`.
- Split SHA-256: train
  `f49de33139cd017535fc656857e8afbc0cae68474082b63d512a396613fe786d`,
  validation `8668d19090b8cc6eba654b90546968873e6071ee7ad4ad64fd27508bf7719e16`,
  held-out `e104780ae475dc59f481ad05327b95af970c8c50b4ce58e86cdaf9f12bce0311`.
- Builder: `scripts\build_ru_llama_v6_2_data.py`; manifest and generated JSONL
  remain local/ignored.

Current status is explicitly **dataset prepared; training not run; GUI remains
v6**. There is no v6.2 adapter, training result, evaluation result, or
adoption decision yet. Do not open or promote a v6.2 GUI until a later isolated
training run passes the conversation and physical-VRAM gates.

## Latest Qwen3.5-9B text-only baseline (not adopted)

Qwen3.5-9B was downloaded locally and evaluated without an adapter or any
training. The evaluator used the official text-only `Qwen3_5ForCausalLM`
class, disabled thinking, and loaded the language backbone in 4-bit NF4 mode.
The local checkpoint was stored at `models\qwen3.5-9b-hf`; its four
safetensors shards totaled `19,306,216,416` bytes.

The Qwen3.5 model, isolated Transformers runtime, and generated JSON results
were removed from the machine on 2026-07-23 after the comparison. The source
evaluator remains as historical code only and cannot run until those runtime
files are restored.

- Evaluation: `conversation_focus_v7_1`, 24 greedy cases.
- Automatic result: `7/24`.
- Automatic failures: 8 English-language failures caused by Vietnamese terms
  in English replies, and 9 mixed-language failures without both language
  signals.
- Physical peak: `5,812 MiB` on the `6,141 MiB` GPU; physical cap passed.
- PyTorch allocator peak: `7,477.979 MiB`; diagnostic only because it does not
  match the sampled physical reading.
- Evidence: the measured result is recorded in this handoff and `README.md`;
  the generated JSON was removed with the runtime files.
- Verdict: `not_adopted`; no Qwen3.5 corpus, adapter, training run, or GUI
  change was created.

The experiment confirms that the text-only Qwen3.5 path can fit the physical
VRAM cap, but it does not beat the current Ru path on the bilingual gate. The
GUI remains `checkpoints\ru-llama3.1-8b-instruct-v6`.

## Llama 3.2 3B Instruct base result (not adopted)

Llama 3.2 3B Instruct was downloaded as a text-only baseline candidate. The
official Meta repository is gated in this environment, so the local copy came
from the accessible `unsloth/Llama-3.2-3B-Instruct` repack of the Meta model.

- Local checkpoint: `models\llama3.2-3b-instruct-hf`.
- Safetensors: `6,425,529,048` bytes across two shards.
- Verification: `LlamaForCausalLM` config, 254 indexed tensors, and a working
  local tokenizer/chat template.
- Evaluation: `conversation_focus_v7_1`, 24 greedy cases,
  `max_new_tokens=64`, base-only with no adapter.
- Automatic result: `5/24`; all English cases failed the English-language gate
  and all mixed cases failed the mixed-language gate, while the Vietnamese
  cases passed.
- Physical VRAM peak: `2,911 MiB` on the `6,141 MiB` GPU; the `6,144 MiB`
  physical cap passed. Allocated peak was `2,233.744 MiB`.
- Evidence: `eval_results\llama3_2_3b_base_v7_1.json`.
- Verdict: `not_adopted`; no training, adapter, or GUI default change.

The checkpoint and generated JSON were removed on 2026-07-23 after the
comparison. The evaluator source remains historical and requires the model to
be downloaded again before it can run.

The result shows that this smaller base can fit comfortably in VRAM but is not
usable for Ru's current bilingual gate: it translates English into Vietnamese
and usually answers mixed prompts in Vietnamese only. Keep the current v6
adapter as the GUI default.

## Gemma 4 E4B Instruct baseline and light adapter (not adopted)

The official `google/gemma-4-E4B-it` any-to-any checkpoint was downloaded for a
local trial. Google documents Gemma 4 E4B as accepting text, image, and audio
inputs, but this project has not enabled those modalities or changed the GUI.

- Historical local checkpoint: `models\gemma-4-e4b-it-hf` (removed 2026-07-23).
- Model file: `15,992,595,884` bytes (`14.894 GiB`).
- Integrity check: `gemma4` config, `Gemma4ForConditionalGeneration`, and
  `2,130` safetensors tensors.
- Source: official Hugging Face model card.
- Status: download, header check, and isolated text-only baseline passed; the
  untouched base was not adopted.

The current project Transformers version is `4.57.6`, which does not recognize
`model_type=gemma4`. The isolated runtime under
`vendor\gemma4-transformers` uses Transformers `5.14.1` and the direct
`Gemma4ForCausalLM` text path. `scripts\evaluate_gemma4_e4b_base.py` loaded the
text backbone with no missing text weights, kept the large per-layer embedding
on CPU, and used 4-bit NF4 with all 42 text decoder layers and `lm_head` on
GPU. With `--route-system-prompt` and `max_new_tokens=64`, the full
`conversation_focus_v7_1` run reached `20/24` automatic passes after correcting
the evaluator's false positive on the natural phrase `bầu bạn`. The remaining
four failures are mixed-language preservation cases. Physical peak was
`5,901 MiB` against the `6,144 MiB` cap. The generated evaluation JSON, model
weights, isolated runtime, light adapter,
and temporary Gemma GUI were removed on 2026-07-23; the measured results are
retained in this handoff and `README.md`. Keep the Llama v6 adapter as the GUI
default.

The temporary Gemma base and light-adapter test windows were removed after
comparison on 2026-07-23. The historical evaluator source remains for review,
but it requires restoring the local checkpoint and isolated runtime before it
can run again. The active GUI remains the Llama v6 adapter.

A bounded Ru light QLoRA run was completed against the existing v7.1 corpus:

- Historical adapter: `checkpoints\gemma4-e4b-ru-light` (removed 2026-07-23).
- Profile: 64 microsteps, 8 optimizer updates, rank-4 LoRA on the four
  attention projections, 4-bit NF4.
- Training validation loss: `8.879111991988289`.
- Training peak: `5,797 MiB` allocated and `5,874 MiB` physical VRAM; cap
  passed.
- Adapter gate: `11/24` automatic passes with `13` failures; physical peak
  `4,336 MiB`.
- Generated adapter evidence and manifest were removed during cleanup; the
  measured result is retained in this handoff and `README.md`.

The adapter produced severe token/emoji repetition and malformed mixed-language
replies during spot inspection. It is `not_adopted`; do not replace v6 or change
the GUI. Before another Gemma run, diagnose this degradation and use a targeted
repair objective rather than repeating the same 64-step profile.

## Machine and stored artifacts

Last verified resources for the active Llama candidate:

- RTX 4050 Laptop GPU: `6,141 MiB` total VRAM.
- RAM: `15.7 GiB`.
- Free space on D:: about `50.41 GiB` after removing the Gemma 4 artifacts.
- Llama base: `models\llama3.1-8b-instruct-hf`.
- Llama 3.2 trial: `models\llama3.2-3b-instruct-hf` (removed after evaluation;
  result retained, not adopted).
- Gemma 4 E4B trial: removed on 2026-07-23 after comparison (routed base
  20/24; light adapter 11/24; both not adopted).
- Qwen3.5 base probe: removed on 2026-07-23 (not adopted).
- v6 adapter: `checkpoints\ru-llama3.1-8b-instruct-v6`.
- v7 adapter: removed 2026-07-23 (not adopted; source and metrics retained).
- v7.1 adapter: removed 2026-07-23 (not adopted; source and metrics retained).
- v8 adapter: `checkpoints\ru-llama3.1-8b-instruct-v8` (not adopted; GUI stays v6).
- v6.1 style adapter: `checkpoints\ru-llama3.1-8b-instruct-v6.1` (not adopted; GUI stays v6).

## Recommended next experiment

The Gemma 4 checkpoint, isolated runtime, adapters, generated evidence, and
temporary test GUIs were removed on 2026-07-23. Its historical routed
untouched-base result was `20/24`, while the light adapter fell to `11/24` and
showed repetition. Do not restore or train another Gemma adapter; focus on the
Llama path. Llama 3.2 is not a replacement: its base result was `5/24`, while
Qwen3.5's measured base result was `7/24`. V8 and v6.1 both remain comparison
adapters; v6.1 tied v6 at `11/24` on the common suite and failed the exact
screenshot replay gate. The next run should first repair mixed-language
preservation, follow-up behavior, and the first screenshot turn in a fresh
bounded corpus. Keep v6 as the GUI fallback and require zero automatic
failures, at least `21/24` natural common replies, and physical VRAM within
the cap before promotion.

## Existing code and useful commands

The persona policy is in `src\mini_gpt\ru_style.py`; the active local Llama runtime is `src\mini_gpt\llama3_1_runtime.py`; the current v6/v6.1 comparison evaluator is `scripts\evaluate_ru_llama3_1_v6_1.py`. Existing tests are under `tests\`.

From `D:\LLM`, baseline checks are:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
& .\.venv\Scripts\python.exe -m pytest --collect-only -q
```

The historical evaluator is `scripts\evaluate_ru_llama3_1_qlora.py`; the v6/v6.1
comparison evaluator is `scripts\evaluate_ru_llama3_1_v6_1.py`; the v7
focus evaluator is `scripts\evaluate_ru_llama3_1_v7.py`, the current isolated
repair evaluator is `scripts\evaluate_ru_llama3_1_v7_1.py`, the Llama 3.2
base-only evaluator is `scripts\evaluate_ru_llama3_2_base.py`, and the Qwen3.5
base-only evaluator is `scripts\evaluate_qwen35_9b_base.py`. The historical
Gemma evaluator is `scripts\evaluate_gemma4_e4b_base.py`; its model/runtime
artifacts were removed and must be restored before it can run. The runtime accepts
the GUI's `max_new_tokens`, `temperature`, and `top_k` settings; evaluation is
greedy and records its generation settings for reproducibility.

## Documentation hygiene

Before committing any completed work, update `README.md`, `TODO.md`, and the relevant design/implementation plan with only evidence actually obtained. Remove or clearly label stale instructions. Runtime models and checkpoints are ignored by Git; commit source, tests, and documentation only.
