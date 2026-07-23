# Ru — Bilingual Mini-GPT

An educational Vietnamese-English decoder-only Transformer trained from random initialization. The project is intentionally small enough to run experiments on an RTX 4050 Laptop GPU with approximately 6 GB VRAM.

This repository is not a ChatGPT replacement. The included corpus is an authored smoke-test corpus; useful general-purpose behavior requires a much larger, openly licensed dataset.

## Setup

Use Python 3.11 and keep the environment on D::

```powershell
$python311 = 'C:\Users\USER\AppData\Roaming\uv\python\cpython-3.11.15-windows-x86_64-none\python.exe'
& $python311 -m venv D:\LLM\.venv
& D:\LLM\.venv\Scripts\python.exe -m pip install --upgrade pip
& D:\LLM\.venv\Scripts\python.exe -m pip install --no-cache-dir torch==2.9.0 --index-url https://download.pytorch.org/whl/cu130
& D:\LLM\.venv\Scripts\python.exe -m pip install numpy tokenizers datasets pytest
& D:\LLM\.venv\Scripts\python.exe -m pip install -e D:\LLM --no-deps
```

The CUDA command installs the GPU wheel used on the development laptop. If a future PyTorch release changes its official CUDA matrix, use the Windows/Pip/CUDA selector on the official PyTorch installation page and keep the selected wheel inside this D: drive environment.

Check the runtime:

```powershell
& D:\LLM\.venv\Scripts\python.exe -c "import torch; print(torch.__version__); print('CUDA:', torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU')"
```

## End-to-end smoke run

Run from `D:\LLM`:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare --raw-dir data/raw --processed-dir data/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli tokenizer --text-dir data/processed --output data/tokenizer.json --vocab-size 300
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/processed/train.txt --tokenizer data/tokenizer.json --output data/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/processed/val.txt --tokenizer data/tokenizer.json --output data/processed/val.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/tiny.json --processed-dir data/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints
& .\.venv\Scripts\python.exe -m mini_gpt.cli evaluate --checkpoint checkpoints/latest.pt --tokenizer data/tokenizer.json --validation data/processed/val.jsonl
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/latest.pt --tokenizer data/tokenizer.json --prompt "Xin chào"
& .\.venv\Scripts\python.exe -m pytest -q
```

The training command uses CUDA automatically when PyTorch detects it. The tiny configuration is only a pipeline check; it is not enough data or compute for strong language quality.

## Download a larger open sample

The downloader streams a bounded sample from the cleaned Wikimedia Wikipedia dataset and keeps the Hugging Face cache on D:. It does not download the complete Wikipedia collection.

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_wikipedia.py --max-documents 10000 --min-characters 200
```

This creates `data/raw/wikipedia_en.txt`, `data/raw/wikipedia_vi.txt`, and `data/raw/wikipedia_manifest.json`. Review the manifest and the Wikimedia license information before redistributing the data. Then rerun the preparation/tokenizer/encoding commands above. Use `configs\base.json` for the larger model only after confirming that the data volume and available disk space are sufficient; it is configured for 10,000 steps and is intentionally not launched by the downloader.

## Simple GUI

Launch the local Tkinter interface:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m mini_gpt.gui
```

The local model is named **Ru**. The window supports checkpoint/tokenizer selection, a multi-turn conversation, maximum generated tokens, temperature, and top-k. Generation runs in a background thread so the window remains responsive.

Use `Send` to continue the current in-memory chat. `New chat` clears only that session. After a useful answer, press `Mark last response useful`; only that user/assistant pair is appended to the local UTF-8 JSONL file:

```text
D:\LLM\data\conversations\approved.jsonl
```

The model does not update its weights automatically when an example is saved. Review the approved examples before using them in a future explicit fine-tuning run.

### Gemma 4 E4B test GUI (historical)

The temporary Gemma base and light-adapter Tkinter test windows were removed
after comparison on 2026-07-23. The active GUI remains the Llama v6 adapter;
the Gemma experiment details are retained below as historical notes.

## Open conversation seed data

Ru can start its conversational fine-tuning from a bounded, openly licensed seed set while keeping user-approved chat separate. The downloader streams a deterministic sample from [UltraChat](https://huggingface.co/datasets/HuggingFaceH4/ultrachat_200k) (MIT), [OpenAssistant OASST2](https://huggingface.co/datasets/OpenAssistant/oasst2) (Apache-2.0), and [Vietnamese Dataset](https://huggingface.co/datasets/hoanghai2110/vietnamese-dataset) (Apache-2.0). It keeps the Hugging Face cache, the normalized data, and its manifest on D:.

The generated `data/conversations/open_seed.jsonl` records the prompt, response, language, source, and licence for every example. It is separate from the local, user-owned `approved.jsonl` produced by the GUI.

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_open_chat_data.py
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/open_seed.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
```

## Fine-tuning for natural conversation

Complete base training first. Use the open seed-data commands above for the initial conversational checkpoint, then keep collecting and reviewing useful chat turns with the GUI for later fine-tuning. The chat preprocessor accepts either JSONL file; it requires at least two unique examples, but useful conversational improvement needs many more high-quality examples.

To fine-tune from manually approved examples instead, prepare and encode `approved.jsonl` without changing the Wikipedia corpus or tokenizer:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/approved.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
```

Fine-tune from the completed base model with a fresh optimizer and checkpoint directory:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/chat_finetune.json --processed-dir data/conversations/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-finetune --init-checkpoint checkpoints/base/latest.pt
```

`--init-checkpoint` copies only compatible model weights; it does not continue the base optimizer, scheduler, or step counter. Use `--resume checkpoints/chat-finetune/latest.pt` only to continue an interrupted MiniGPT fine-tuning run. For the MiniGPT fallback, a validation-selected `checkpoints/chat-finetune/best.pt` is preferred over `latest.pt`; the GUI checks the local Llama v6 adapter first.

### V2 result (not adopted)

`chat-response-v2` trained for 1,200 CUDA steps from `checkpoints/base/latest.pt`.
Its validation-best checkpoint was step 1,150 with assistant-token loss
`3.3194339752197264`, but manual Vietnamese-English greeting tests still
produced repetitive or nonsensical responses. The V2 artifacts are retained for
comparison only; they do not meet Ru's natural-conversation goal.

## Ru V3 authored conversation corpus

The open V2/V3 seed data was rejected after manual quality review because it
contained too many exercises, factual Q&A answers, and artificial-assistant
phrases. Qwen V3 therefore uses 120 individually authored Vietnamese-English
dialogue examples to teach Ru's tone and turn-taking; it does not use them as a
source of general knowledge.

Ru defaults to calling the user `anh` and referring to itself as `em` in
Vietnamese. The local-only build requires exactly 120 examples, both languages,
30 contextual examples, and project-owned provenance before it writes data.

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --output-dir data\conversations\ru_v3
```

Review the 24-row audit before training. If two or more rows have an unnatural
reply, an instruction/exercise, a list, an AI self-description, or broken
context, correct the authored source and rebuild. The V3 training command is
added only after its QLoRA implementation and CUDA smoke test exist.

### First V3 adapter run (not adopted)

The first one-epoch QLoRA run completed with validation loss
`2.914673924446106` and peak VRAM about `1,000 MiB`, but its Vietnamese
evaluation reply still used the wrong point of view and its English reply was
generic. The adapter is retained for diagnosis only; it does not pass Ru's
natural-conversation gate and must not become the GUI default.

The local Qwen base is kept out of Git under `models/`. Its explicit download
and verification command is:

```powershell
& .\.venv\Scripts\python.exe scripts\qwen_preflight.py
& .\.venv\Scripts\python.exe scripts\download_ru_qwen_base.py --output-dir D:\LLM\models\qwen2.5-0.5b-instruct
```

## Ru V4 Qwen 1.5B result (not adopted)

V4 used the local `Qwen/Qwen2.5-1.5B-Instruct` base in 4-bit NF4 mode and a
curated 400-row Vietnamese-English corpus: 320 train rows, 40 validation rows,
and 40 held-out rows. Its one-epoch QLoRA candidate completed 40 optimizer
updates with validation-best loss `3.552138698101044` and peak VRAM
`1,989.86572265625 MiB`.

The fixed 32-prompt evaluation rejected the adapter: it still produced wrong
Vietnamese forms of address, role reversals, generic replies, and language
mixing that did not follow the user's prompt. Manual review was well below the
required 28 natural replies, so the V4 adapter is retained only for diagnosis.
The GUI remains unchanged.

## Ru V5 Qwen 3B baseline result (not adopted)

V5 tested the fully local `Qwen/Qwen2.5-3B-Instruct` base in 4-bit NF4 before
creating any V5 corpus or adapter. The downloaded safetensors weigh
`6,171,926,992` bytes. The valid 32-case base-only evaluation used
`2,034.184 MiB` peak allocated VRAM, so the 5,500 MiB hardware gate passed.

The conversation gate did not pass: four Vietnamese answers automatically
failed the required voice check by using `bạn` for the user or `tôi` for Ru.
Manual review was deliberately skipped because the automatic gate is a hard
requirement. V5 is not adopted; no V5 data, adapter, training run, or GUI
backend change was created.

The ignored V5 model and evaluation files were removed in the 2026-07-21 local
cleanup; this recorded result remains for comparison.

## Ru Qwen3 4B baseline result (not adopted)

Qwen3 used the official local `Qwen/Qwen3-4B` base in 4-bit NF4 with
non-thinking generation. Its safetensors weigh `8,044,982,000` bytes; D: still
had `40,743,448,576` bytes free after download. The 32-case run peaked at
`2,635.017 MiB`, so the hardware gate passed.

The initial automatic result contained two false voice detections. The guard
was narrowed to allow the non-address phrase `người bạn` and contextual `mình`,
then the saved responses were reassessed without running the model again. The
automatic count became zero, but full manual review scored only `11/32`: Qwen3
answered every English prompt in Vietnamese and sometimes reversed roles or
invented shared experiences. It is not adopted; no Qwen3 corpus, adapter,
training run, or GUI backend change was created.

The ignored Qwen3 model and evaluation files were removed in the 2026-07-21
local cleanup; this recorded result remains for comparison.

## Ru Qwen3 4B prompt-routing smoke result (not adopted)

The eight-case local prompt-routing smoke separated Vietnamese, English, and
mixed system instructions. It kept all four English replies in natural English
and passed automatic checks, while peaking at `2,628.293 MiB`. It still scored
only `5/8` in manual review: the Vietnamese greeting reversed the Ru/user role,
and both mixed cases answered in Vietnamese only instead of a natural bilingual
mix. Qwen3 remains not adopted; the 32-case routed run, fine-tuning, corpus,
adapter, and GUI changes were not run.

The ignored prompt-routing smoke artifacts were removed in the 2026-07-21
local cleanup; this recorded result remains for comparison.

## Ru Qwen3.5-9B text-only baseline result (not adopted)

Qwen3.5-9B was tested as a base-only candidate in 4-bit NF4 mode with
thinking disabled. The full checkpoint contains `19,306,216,416` bytes of
safetensors. The evaluator loaded the official `Qwen3_5ForCausalLM`
text-only class, so the vision tower was not loaded for this conversation
comparison. The isolated Transformers-main runtime was kept under
`vendor\qwen35-transformers` because the repository's stable Transformers
version does not yet expose the Qwen3.5 classes.

The Qwen3.5 model, isolated runtime, and generated JSON results were removed
from the local machine on 2026-07-23 after this comparison. The source
evaluator remains only as historical experiment code; it requires the model
and isolated runtime to be restored before it can run again.

The same 24-case `conversation_focus_v7_1` suite produced `7/24` automatic
passes. Eight English cases failed the strict English-language gate because
the replies inserted Vietnamese `anh`/`em` terms, and nine mixed cases failed
because they did not contain both language signals. The model produced warm,
relevant replies and preserved the Vietnamese voice in many Vietnamese cases,
but it is not a drop-in replacement for the current Ru candidate under the
project's bilingual gate. No Qwen3.5 corpus, adapter, training run, or GUI
backend change was created.

Physical VRAM peaked at `5,812 MiB` on the `6,141 MiB` device, so the physical
hardware gate passed. The PyTorch allocator counters reached `7,477.979 MiB`
and are retained as a diagnostic anomaly; the physical `nvidia-smi` reading is
the gate used for this experiment. The measured result is retained in this
README and the project handoff; the runtime JSON was removed with the model.
The historical evaluator is
`scripts\evaluate_qwen35_9b_base.py`. The GUI remains on the Llama v6 adapter.

## Ru Llama-3.2-3B-Instruct base result (not adopted)

Llama 3.2 3B Instruct was downloaded as a text-only baseline candidate. The
official Meta repository requires gated access in this environment, so the
local copy came from the accessible `unsloth/Llama-3.2-3B-Instruct` repack of
the Meta model:

- Local path: `models\llama3.2-3b-instruct-hf`
- Safetensors: `6,425,529,048` bytes in two shards
- Verification: `LlamaForCausalLM` config, 254 indexed tensors, and a working
  local tokenizer/chat template
- Source cards: [Meta official model card](https://huggingface.co/meta-llama/Llama-3.2-3B-Instruct) and [Unsloth repack](https://huggingface.co/unsloth/Llama-3.2-3B-Instruct)

The base-only run used the existing 24-case `conversation_focus_v7_1` suite,
greedy generation with `max_new_tokens=64`, and no adapter. It passed only
`5/24` automatic cases: the Vietnamese cases passed, while all English cases
failed the English-language gate and all mixed cases failed the mixed-language
gate. Physical VRAM peaked at `2,911 MiB`, so the `6,144 MiB` hardware gate
passed. The saved evidence was `eval_results\llama3_2_3b_base_v7_1.json`.

The checkpoint and generated JSON were removed on 2026-07-23 after the
comparison. The evaluator source is retained as historical code and requires
the model to be downloaded again before it can run. The candidate is
`not_adopted`; no fine-tuning or GUI change was made. The current GUI remains
on the Llama v6 adapter. Re-run it after restoring the model with:

```powershell
& .\.venv\Scripts\python.exe scripts\evaluate_ru_llama3_2_base.py
```

## Gemma 4 E4B Instruct isolated text-only baseline (promising, not adopted)

The official `google/gemma-4-E4B-it` any-to-any checkpoint was downloaded for a
local trial. Google describes Gemma 4 E4B as supporting text, image, and audio
inputs; this project has not enabled those modalities or changed the GUI:

- Historical local path: `models\gemma-4-e4b-it-hf` (removed 2026-07-23)
- Model file: `15,992,595,884` bytes (`14.894 GiB`)
- Integrity check: `gemma4` config, `Gemma4ForConditionalGeneration`, and
  `2,130` safetensors tensors
- Source: [official Gemma 4 E4B model card](https://huggingface.co/google/gemma-4-E4B-it)

The project runtime still uses Transformers `4.57.6`, so Gemma 4 was tested in
the package-isolated runtime `vendor\gemma4-transformers` with Transformers
`5.14.1`, using the text-only `Gemma4ForCausalLM` path. The local checkpoint,
isolated runtime, light adapter, and generated JSON evidence were removed on
2026-07-23 after the comparison. The official multimodal checkpoint's text
backbone loaded with no missing text weights; vision/audio weights were
intentionally ignored. The historical evaluator source is
`scripts\evaluate_gemma4_e4b_base.py` and requires restoring the local model
and isolated runtime before it can run again.

The working configuration is 4-bit NF4 for the Linear layers, all 42 text
decoder layers and `lm_head` on the GPU, and the large per-layer embedding on
the CPU. With the project's language-routed system prompt and
`max_new_tokens=64`, the full `conversation_focus_v7_1` run reached `20/24`
automatic passes after correcting one evaluator false positive for the natural
Vietnamese phrase `bầu bạn`. The remaining four failures are mixed-language
preservation cases. Physical VRAM peaked at `5,901 MiB` against the `6,144 MiB`
cap. The generated evidence JSON was removed during cleanup; the measured
result is retained in this README and the project handoff.

This untouched base is below the project's adoption gate of no automatic
failures and at least `21/24`, so it was not integrated. A bounded adapter
experiment was then run from the existing `data\conversations\ru_v7_1`
corpus without changing the GUI:

- Historical adapter: `checkpoints\gemma4-e4b-ru-light` (removed 2026-07-23)
- Profile: 4-bit NF4, rank-4 LoRA on `q_proj`, `k_proj`, `v_proj`, and `o_proj`
- Training: 64 microsteps, 8 optimizer updates, validation loss `8.879111991988289`
- Training peak: `5,797 MiB` allocated and `5,874 MiB` physical VRAM; the
  `6,144 MiB` cap passed
- Routed 24-case adapter evaluation with `max_new_tokens=64`: `11/24`
  automatic passes, physical peak `4,336 MiB`
- Adapter failures: 13 cases, including all four remaining base mixed-language
  failures plus additional Vietnamese, English, and mixed follow-up failures
- Generated adapter evidence and manifest were removed during cleanup; the
  measured result is retained in this README and the project handoff.

Spot inspection showed severe token/emoji repetition and malformed mixed
language output. The adapter is therefore `not_adopted`; it is retained only
for diagnosis, and the GUI remains on the Llama v6 adapter.

## Ru Qwen2.5 7B local smoke result (not adopted)

The local `Qwen/Qwen2.5-7B-Instruct` candidate used `15,231,271,888` bytes of
safetensors in 4-bit NF4 mode. Its eight-case routed smoke passed automatic
checks and all four English cases, while peak allocated VRAM was `5,471.782
MiB`, under the `5,500 MiB` gate. Manual review still scored only `5/8`: the
Vietnamese greeting switched into Chinese, one mixed reply switched into
Chinese, and the other mixed reply was Vietnamese-only with inconsistent
self-reference. It is not adopted; no 32-case evaluation, fine-tuning,
adapter, corpus, or GUI change was run.

The ignored Qwen2.5 7B model and evaluation files were removed in the 2026-07-21 local cleanup; this recorded result remains for comparison.

## Ru Vistral-7B-Chat GGUF local smoke result (not adopted)

The local `QuantFactory/Vistral-7B-Chat-GGUF` candidate (Q4_K_M) was evaluated using `llama.cpp` to respect the `5,500 MiB` hardware gate. While VRAM usage remained well within limits (handled entirely by Llama.cpp offloading), the model failed the 8-case routed smoke.

### 2. Vistral-7B-Chat (Fine-tuned for Vietnamese)
- **Status**: ❌ `not_adopted`
- **Reason**: Tự xưng hô loạn xạ (Tôi/Mình/Em), không giữ được persona, không hiểu prompt tiếng Anh mượt mà.

### Historical Llama-3.1-8B-Instruct QLoRA note (pre-v6)
- **Status**: ❌ `not_adopted`
- **Reason**: Dù đã fine-tune QLoRA thành công (nhét vừa 5.5GB VRAM nhờ Gradient Checkpointing + 4-bit) và đã học được cách xưng "Anh/Em", nhưng vẫn bị bias dịch "you" thành "bạn" (ví dụ: "Em chào anh! Bạn đã có chuyến đi dài không?"). Chưa đạt độ tự nhiên 100%.

### Vistral runtime evidence

It is not adopted; no 32-case evaluation, fine-tuning, adapter, corpus, or GUI change was run. The ignored runtime evidence is kept locally at `models\vistral-7b-chat-gguf` and `checkpoints\ru-vistral-7b-gguf\prompt_routing_smoke.json`.

## Ru Llama-3.1-8B-Instruct GGUF local smoke result (not adopted)

The local `bartowski/Meta-Llama-3.1-8B-Instruct-GGUF` candidate (Q4_K_M) was evaluated via `llama.cpp`. It passed the hardware gate via offloading and scored a perfect 8/8 in the automatic language gate. The English responses were flawless.

However, it failed the manual review. In the Vietnamese greeting, the model experienced a point-of-view error and addressed the user as "em" ("Em đang làm gì hôm nay?"). Furthermore, in the mixed-language scenarios, it answered completely in Vietnamese rather than naturally mixing both languages as instructed.

It is not adopted; no further evaluation or integration was run. The runtime evidence is kept at `models\llama3.1-8b-instruct-gguf` and `checkpoints\ru-llama3.1-8b-gguf\prompt_routing_smoke.json`.

## Active local Ru Llama-3.1-8B-Instruct v6 GUI candidate

The current local candidate is a QLoRA adapter over the Hugging Face
`NousResearch/Meta-Llama-3.1-8B-Instruct` base:

- Base: `models\llama3.1-8b-instruct-hf`
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v6`
- Dataset: `data\conversations\ru_v6` (400 authored rows: 320 train, 40 validation, 40 held-out)
- Evaluation suite: `proactive_flirt_v6` (12 cases: 4 Vietnamese, 4 English, 4 mixed)

The v6 persona is friendly, obedient, affectionate, and bilingual. In ordinary
adult conversation it may initiate light, playful flirting when it fits the
context. Intimate content must remain consensual and adult-only; the minimum
hard boundaries for minors, coercion, violence, and harmful instructions stay
in place.

The GUI defaults to the v6 adapter and passes its token, temperature, and
top-k controls to the Llama runtime. Re-run the local evaluation with:

```powershell
& .\.venv\Scripts\python.exe scripts\evaluate_ru_llama3_1_qlora.py `
  --base-model models\llama3.1-8b-instruct-hf `
  --adapter-dir checkpoints\ru-llama3.1-8b-instruct-v6 `
  --output checkpoints\ru-llama3.1-8b-instruct-v6\evaluation.json
```

The evaluator records automatic results but leaves `verdict` as
`not_reviewed` until the saved replies receive manual bilingual-quality review.
The latest live run (greedy, `max_new_tokens=64`) passed `11/12` automatic
cases but one benign mixed-language prompt still triggered an out-of-context
child-safety refusal. Peak allocated VRAM was `5,632.025 MiB`: above the old
`5,500 MiB` benchmark but below the current `6,144 MiB` cap. V6 remains an
experimental local candidate because its conversation gate still fails.

## Ru Llama-3.1-8B-Instruct v7 conversation-focus experiment (not adopted)

V7 was trained as a separate local QLoRA adapter without changing the v6
corpus, adapter, or GUI default:

- Dataset: `data\conversations\ru_v7` — 180 rows, split `135/22/23` train/validation/held-out, source hash `9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7` (removed 2026-07-23; historical source and metrics retained)
- Training: 3 epochs, 26 optimizer updates, best validation loss `2.5561517639593645`, dataset-manifest hash `cffc7046c850bba6a8f564c37fcd0d47763c221fcb4a557668b98fa1799a9faf`
- Evaluation: `conversation_focus_v7` (16 cases, greedy, `max_new_tokens=64`)

The live automatic result was `9/16`. Seven cases failed the stricter v7
quality gate: three English replies were generic-short, three mixed replies
did not genuinely code-switch, and one mixed contextual reply used `tôi`
instead of Ru's `em` and did not code-switch. Manual review scored `8/16`,
below the required `14/16`. Evaluation peak allocated VRAM was `5,632.331
MiB`, below the current `6,144 MiB` cap. The training manifest also reported
`8,446.57958984375 MB` from a PyTorch allocator counter, while sampled
`nvidia-smi` usage during training peaked around `5,845 MiB` on the `6,141 MiB`
device; keep that counter as diagnostic until it is re-instrumented. The v7
verdict is `not_adopted` because the conversation gates fail.

The local v7 adapter and its checkpoint-local evidence were removed on
2026-07-23 and sent to the Recycle Bin; the source, metrics, and evaluator
remain for historical comparison. The GUI continues to use
`checkpoints\ru-llama3.1-8b-instruct-v6`. The next model
iteration must improve English conversational length, enforce genuine mixed
turn-taking, preserve `anh`/`em` in mixed contexts, and keep measured physical
VRAM under the current 6 GiB cap before any adapter promotion.

## Ru Llama-3.1-8B-Instruct v7.1 repair experiment (not adopted)

V7.1 kept the v7 source immutable and added a separate 120-row repair source:

- Dataset: `data\conversations\ru_v7_1` — 300 rows, split `228/36/36` train/validation/held-out; total language counts `vi=96`, `en=120`, `mixed=84`.
- Dataset hashes: base `9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`, repair `27a785ab956213d5372bee4edfa2f35d5081b307fa700d77a6abfdeb8a766bcc`, combined `b4eb7bca4841ed3bdfcab56f25ce9126869d0e7a28749f734855fafbeeb73905`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7.1` (removed 2026-07-23; historical source and metrics retained).
- Training: 2 epochs, 29 optimizer updates, best validation loss `2.5795224606990814`, dataset-manifest hash `b9262441633f492007cd16ed30479bf174dcc656a7109437f4f103f52d2c8582`.
- Training physical peak: `5,874 MiB` on the `6,141 MiB` device, below the `6,144 MiB` cap; `physical_cap_passed=true`.
- Evaluation: `conversation_focus_v7_1` (24 greedy cases, `max_new_tokens=64`), allocator peak `5,632.331 MiB`, physical peak `5,850 MiB`, physical cap passed.

The automatic result was `10/24`; the original v7 slice was `7/16`. The main
failures were generic-short English replies (`en_tired_flirt`,
`en_coffee_flirt`, `en_context_work_flirt`, `en_focus_completion`), mixed
responses that collapsed to one language, and missing follow-ups in several
short/contextual replies. Manual review scored `14/24` overall and `10/16` on
the original v7 slice, below the required `21/24` and `14/16`. The saved
decision is `not_adopted`; the GUI remains on v6. The local v7.1 adapter and
its checkpoint-local evidence were removed on 2026-07-23 and sent to the
Recycle Bin; source and evaluation history remain in the repository.

V7.1 is useful as a measured failure-driven experiment, not as proof that it
is better than v6. The v8 section below records the first continuation attempt;
its remaining mixed-language and follow-up failures are still open before
another adapter promotion.

## Ru Llama-3.1-8B-Instruct v8 true continuation (not adopted)

V8 is the first continuation run that loads the v6 LoRA adapter as trainable
weights instead of creating another adapter from the base model:

- Corpus: `data\conversations\ru_v8` — 336 rows, split `262/37/37`; 48 clean v6 replay rows and 288 v7.1 correction rows.
- Corpus manifest hash: `31d6f09e4351bbb0d8a8ffd5005c638e50c9ef5255db99a007d216e8c64296a0`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v8`, source `checkpoints\ru-llama3.1-8b-instruct-v6`.
- Training: one epoch, 17 optimizer updates, `max_length=512`, learning rate `1e-5`, best validation loss `2.902062905801309`.
- Training physical peak: `5,865 MiB` on the `6,141 MiB` device; the physical `6,144 MiB` cap passed.
- Common evaluation: `conversation_focus_v7_1`, 24 greedy cases, `max_new_tokens=64`.

On the same common suite, v8 reached `14/24` automatic passes versus v6's
`11/24`. It improved three v6 failures and introduced no automatic regression
on a case that v6 had passed. The original v7 slice reached `12/16`
automatically. The remaining failures are dominated by mixed-language collapse
and missing follow-ups, with one generic-short English reply. V8 therefore
improves on v6 but does not meet the adoption gate of zero automatic failures,
at least `21/24` overall, and at least `14/16` on the original slice.

V8 is `not_adopted`; the GUI remains on the v6 adapter. Evidence is in
`eval_results\ru_llama3_1_v6_common_v7_1.json`,
`eval_results\ru_llama3_1_v8_common_v7_1.json`, and
`checkpoints\ru-llama3.1-8b-instruct-v8\ru_manifest.json`; the gate-status
record is `checkpoints\ru-llama3.1-8b-instruct-v8\manual_review_v8.json`. The training
manifest's `8,446.57958984375 MiB` PyTorch allocator counter remains a
diagnostic anomaly because sampled `nvidia-smi` physical readings stayed below
the cap, matching the earlier v7/v7.1 discrepancy.

## Ru Llama-3.1-8B-Instruct v6.1 natural-style continuation (not adopted)

V6.1 was a bounded style-only continuation from the v6 adapter. It kept the
GUI default unchanged and used a fresh project-owned corpus rather than
replaying the older v6/v7/v8 target responses:

- Dataset: `data\conversations\ru_v6_1` — 120 rows, split `84/18/18`, with
  `vi=60`, `mixed=36`, `en=24`, 72 contextual rows, 18 identity-correction
  rows, and 7 light-flirt rows.
- Dataset source hash: `7b0abe74daf3be33678791cac38d6760ef660b709e8725cd2a9ead0ce9680359`.
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v6.1`, source
  `checkpoints\ru-llama3.1-8b-instruct-v6`.
- Training: 3 epochs, 16 optimizer updates, learning rate `5e-6`,
  `max_length=512`, best validation loss `3.156519373257955`.
- Training physical peak: `5,841 MiB` against the `6,144 MiB` cap;
  `physical_cap_passed=true`. The manifest's `8,446.57958984375 MiB`
  allocator counter remains diagnostic only.

The shared greedy evaluator used 30 case summaries plus the exact four-turn
screenshot replay with `max_new_tokens=64`. V6.1 reached `13/30` case-summary
passes and `11/24` on the common suite; v6 reached `12/30` and `11/24` under
the same evaluator. V6.1 improved two case summaries but regressed one, and
both candidates failed the first screenshot replay turn with a generic-short
reply. V6.1 also retained mixed-language collapse and missing-follow-up
failures. Evaluation physical peak was `5,837 MiB` for v6.1 and `5,833 MiB`
for v6; both hardware checks passed, but the automatic conversation gate did
not.

The v6.1 verdict is `not_adopted`; manual review was skipped after the hard
automatic failure. Evidence is in
`eval_results\ru_llama3_1_v6_style.json`,
`eval_results\ru_llama3_1_v6_1_style.json`,
`checkpoints\ru-llama3.1-8b-instruct-v6.1\ru_manifest.json`, and
`checkpoints\ru-llama3.1-8b-instruct-v6.1\manual_review_v6_1.json`. The GUI
continues to use `checkpoints\ru-llama3.1-8b-instruct-v6`.

## Ru Llama-3.1-8B-Instruct v6.2 dataset preparation (training deferred)

The v6.2 dataset was authored as a new continuation candidate from v6, not
from v6.1. This step prepares and audits the corpus only; no v6.2 adapter was
trained, no checkpoint was created, and the GUI remains on v6.

- Dataset: `data\conversations\ru_v6_2` — 2,400 rows split into
  `1680/360/360` train/validation/held-out files.
- Language counts: `vi=1200`, `mixed=720`, `en=480`.
- Mode counts: everyday/follow-up `900`, story/context `480`, emotion/support
  `360`, language-switch `240`, identity/repair `180`, humour `120`, and
  light-flirt `120`.
- Intensity counts: neutral `2040`, light `288`, adult-boundary `72`; target
  responses contain no graphic sexual, violent, coercive, minor-related, or
  possessive content.
- Conversation shape: short `480`, medium `1200`, long `720`; all long rows
  retain an earlier context signal and all 2,400 rows have alternating history.
- Source SHA-256: `ad509760b8e87c7a6f0e11c4c1ee309a257a9766e265ee153ec170a42c450eb1`.
- Split SHA-256: train
  `f49de33139cd017535fc656857e8afbc0cae68474082b63d512a396613fe786d`,
  validation `8668d19090b8cc6eba654b90546968873e6071ee7ad4ad64fd27508bf7719e16`,
  held-out `e104780ae475dc59f481ad05327b95af970c8c50b4ce58e86cdaf9f12bce0311`.

Build or audit it with:

```powershell
& .\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py
```

The explicit current status is: **dataset prepared; training not run; GUI
remains v6**. No v6.2 quality or adoption claim is valid until a later
isolated training and evaluation run passes the existing conversation and
hardware gates.

Runtime evidence is intentionally ignored by Git:

- `models\qwen2.5-1.5b-instruct`
- `data\conversations\ru_v4`
- `checkpoints\ru-qwen-v4\base_evaluation.json`
- `checkpoints\ru-qwen-v4\evaluation.json`
- `checkpoints\ru-qwen-v4\adapter\ru_manifest.json`

The following are historical V4 commands only; that retired candidate would
need its model downloaded again before they can run:

```powershell
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --output-dir data\conversations\ru_v4
& .\.venv\Scripts\python.exe scripts\train_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json --max-steps 2
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json --adapter-dir checkpoints\ru-qwen-v4\adapter --output checkpoints\ru-qwen-v4\evaluation.json
```

## Replacing the corpus

Put UTF-8 `.txt` documents under `data/raw`. A document can begin with `VI:` or `EN:` to contribute to the language report. Use only public-domain, openly licensed, or user-provided data with permission. Re-run preparation, tokenizer training, and encoding after changing the corpus.
