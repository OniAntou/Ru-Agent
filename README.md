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
- Adapter: `checkpoints\ru-llama3.1-8b-instruct-v7`
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

The saved evidence is
`checkpoints\ru-llama3.1-8b-instruct-v7\conversation_focus_v7.json` and
`checkpoints\ru-llama3.1-8b-instruct-v7\manual_review_v7.json`. The GUI
continues to use `checkpoints\ru-llama3.1-8b-instruct-v6`. The next model
iteration must improve English conversational length, enforce genuine mixed
turn-taking, preserve `anh`/`em` in mixed contexts, and keep measured physical
VRAM under the current 6 GiB cap before any adapter promotion.

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
