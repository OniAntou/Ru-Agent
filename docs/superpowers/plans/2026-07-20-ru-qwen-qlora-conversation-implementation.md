# Ru Qwen QLoRA Conversation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run Ru locally on `Qwen/Qwen2.5-0.5B-Instruct` with a 4-bit QLoRA adapter and a Tkinter chat path that favors natural Vietnamese-English conversation.

**Architecture:** Keep the educational Mini-GPT path untouched and add a separate lazy-loaded Qwen runtime. A response-only Qwen data packer uses the same chat template as inference, a QLoRA trainer saves only local adapter artifacts, and the GUI selects V3 only when a valid adapter manifest exists.

**Tech Stack:** Python 3.11, PyTorch CUDA, Transformers, PEFT, bitsandbytes, Accelerate, Hugging Face Hub, existing Tkinter GUI, pytest.

---

## File structure

| Path | Responsibility |
| --- | --- |
| `pyproject.toml` | Optional Qwen runtime dependency group; legacy Mini-GPT dependencies remain unchanged. |
| `src/mini_gpt/qwen_runtime.py` | Lazy dependency checks, Qwen 4-bit loading, chat-template rendering, local reply generation. |
| `src/mini_gpt/qwen_data.py` | Conversation schema validation, quality filtering, Qwen-template response-only labels, padding collator. |
| `src/mini_gpt/qwen_training.py` | QLoRA config validation, trainer construction, adapter manifest generation. |
| `scripts/qwen_preflight.py` | Explicit CUDA/bitsandbytes preflight with no model download. |
| `scripts/download_ru_qwen_base.py` | Download and verify the immutable Qwen base under `D:\LLM\models`. |
| `scripts/build_ru_qwen_v3_data.py` | Build V3 train/validation JSONL plus manifest from the approved local conversation sources. |
| `scripts/train_ru_qwen_v3.py` | Train, resume, and report the local LoRA adapter. |
| `configs/ru_qwen_v3.json` | Exact 0.5B QLoRA settings, data paths, and model identity. |
| `src/mini_gpt/gui.py` | Qwen runner and safe V3 manifest preference while preserving Mini-GPT behavior. |
| `tests/test_qwen_runtime.py` | Unit tests that do not require optional Qwen packages or model weights. |
| `tests/test_qwen_data.py` | Response-only template-label and filter tests using a fake tokenizer. |
| `tests/test_qwen_training.py` | Config/manifest and lazy-runtime error tests without a GPU. |
| `tests/test_gui.py` | V3 manifest priority and runner routing tests. |
| `README.md`, `TODO.md`, V2/V3 plan docs | Exact commands, known V2 failure, V3 acceptance result, and current roadmap. |

### Task 1: Record the completed-but-unsuccessful V2 experiment

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\TODO.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-response-only-chat-finetuning-plan.md`
- Modify: `D:\LLM\docs\superpowers\specs\2026-07-20-response-only-chat-finetuning-design.md`

- [ ] **Step 1: Add the exact V2 outcome to the README**

Add a short status block with the confirmed artifacts and result:

```markdown
### V2 result (not adopted)

`chat-response-v2` trained for 1,200 CUDA steps from `checkpoints/base/latest.pt`.
It produced a validation-best adapter at step 1,150 with assistant-token loss
`3.3194339752197264`, but manual Vietnamese-English greeting tests were still
repetitive or nonsensical. The checkpoint is retained for comparison and is
not evidence that Ru meets the natural-conversation goal.
```

- [ ] **Step 2: Mark implementation facts without marking natural conversation complete**

Update `TODO.md` so the V2 data/response-only mechanics are completed, while the
top-level natural conversation goal remains unchecked. Add this explicit note:

```markdown
- [x] Build and run the V2 response-only fine-tune pipeline (quality gate failed; do not use as the final Ru backend).
- [ ] Make Ru consistently natural in short Vietnamese-English conversations.
```

Update the V2 plan checkboxes for importer, labels, CLI, automatic best
checkpoint, data creation, and CUDA run to `[x]`. Under its final quality-gate
step, record `FAILED: V2 best still produced repetitive/nonsensical greeting
responses; roadmap remains pending.` Replace every obsolete
`data/conversations/response-v2` command with
`data/conversations/response_v2_processed`.

- [ ] **Step 3: Verify documentation contains the actual paths and outcome**

Run:

```powershell
rg -n "response-v2|response_v2_processed|3.3194339752197264|not adopted|quality gate failed" README.md TODO.md docs\superpowers\plans\2026-07-20-response-only-chat-finetuning-plan.md docs\superpowers\specs\2026-07-20-response-only-chat-finetuning-design.md
```

Expected: no `response-v2` path remains; the current underscore path and failed
quality gate are present.

- [ ] **Step 4: Commit the V2 record separately**

```powershell
git add README.md TODO.md docs/superpowers/plans/2026-07-20-response-only-chat-finetuning-plan.md docs/superpowers/specs/2026-07-20-response-only-chat-finetuning-design.md
git commit -m "docs: record response-only chat outcome"
```

### Task 2: Add optional Qwen dependencies and a no-download preflight

**Files:**
- Modify: `D:\LLM\pyproject.toml`
- Create: `D:\LLM\src\mini_gpt\qwen_runtime.py`
- Create: `D:\LLM\scripts\qwen_preflight.py`
- Create: `D:\LLM\tests\test_qwen_runtime.py`

- [ ] **Step 1: Write the failing dependency-status test**

```python
from mini_gpt.qwen_runtime import qwen_dependency_status, require_qwen_dependencies


def test_qwen_dependency_status_reports_missing_packages_without_importing_them():
    installed = {"transformers": True, "accelerate": False, "peft": True, "bitsandbytes": False}

    assert qwen_dependency_status(lambda name: installed[name]) == {
        "transformers": True,
        "accelerate": False,
        "peft": True,
        "bitsandbytes": False,
    }
    with pytest.raises(RuntimeError, match="accelerate, bitsandbytes"):
        require_qwen_dependencies(lambda name: installed[name])
```

- [ ] **Step 2: Run the test to verify the runtime module is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
```

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.qwen_runtime'`.

- [ ] **Step 3: Add optional dependencies and lazy dependency checks**

Add this group to `pyproject.toml`:

```toml
[project.optional-dependencies]
dev = ["pytest>=8"]
qwen = [
    "accelerate>=1.0,<2",
    "bitsandbytes>=0.48,<1",
    "peft>=0.14,<1",
    "safetensors>=0.4",
    "transformers>=4.37,<5"
]
```

Create `qwen_runtime.py` with an import-free status surface:

```python
from __future__ import annotations

from collections.abc import Callable
from importlib.util import find_spec

QWEN_MODEL_ID = "Qwen/Qwen2.5-0.5B-Instruct"
REQUIRED_QWEN_PACKAGES = ("transformers", "accelerate", "peft", "bitsandbytes")


def qwen_dependency_status(resolver: Callable[[str], object | None] = find_spec) -> dict[str, bool]:
    return {name: resolver(name) is not None for name in REQUIRED_QWEN_PACKAGES}


def require_qwen_dependencies(resolver: Callable[[str], object | None] = find_spec) -> None:
    missing = [name for name, present in qwen_dependency_status(resolver).items() if not present]
    if missing:
        raise RuntimeError("install the Qwen runtime extra: " + ", ".join(missing))
```

Create `qwen_preflight.py` so it imports the optional packages only after
`require_qwen_dependencies()`, requires `torch.cuda.is_available()`, reports
GPU name/VRAM, creates a `BitsAndBytesConfig(load_in_4bit=True,
bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True)`, and exits zero
without calling `from_pretrained`.

- [ ] **Step 4: Run unit tests before changing the environment**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
```

Expected: PASS without requiring Transformers, PEFT, or model files.

- [ ] **Step 5: Install the isolated optional runtime and run preflight**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pip install -e ".[qwen,dev]"
& .\.venv\Scripts\python.exe scripts\qwen_preflight.py
```

Expected: the second command reports CUDA, RTX 4050 VRAM, and an NF4 config.
Stop V3 before a model download if it reports a missing package, CUDA failure,
or bitsandbytes import failure.

- [ ] **Step 6: Commit the optional runtime surface**

```powershell
git add pyproject.toml src/mini_gpt/qwen_runtime.py scripts/qwen_preflight.py tests/test_qwen_runtime.py
git commit -m "feat: add Qwen runtime preflight"
```

### Task 3: Define Qwen-template response-only training examples

**Files:**
- Create: `D:\LLM\src\mini_gpt\qwen_data.py`
- Create: `D:\LLM\tests\test_qwen_data.py`

- [ ] **Step 1: Write failing template-label and padding tests**

Use a fake tokenizer whose chat template returns stable numeric prefixes:

```python
def test_encode_qwen_response_masks_chat_prefix_and_scores_response_end_marker():
    tokenizer = FakeQwenTokenizer(prompt_ids=[10, 11, 12], response_ids=[21, 22], eos_token_id=99)

    encoded = encode_qwen_response(
        tokenizer,
        messages=(QwenMessage("system", "You are Ru."), QwenMessage("user", "Xin chào")),
        response="Chào anh!",
        max_length=16,
    )

    assert encoded.input_ids == [10, 11, 12, 21, 22, 99]
    assert encoded.labels == [-100, -100, -100, 21, 22, 99]


def test_collator_left_pads_inputs_and_keeps_label_padding_ignored():
    batch = QwenResponseCollator(pad_token_id=0)([
        EncodedQwenResponse([10, 21, 99], [-100, 21, 99]),
        EncodedQwenResponse([10, 11, 22, 99], [-100, -100, 22, 99]),
    ])

    assert batch["input_ids"].tolist() == [[0, 10, 21, 99], [10, 11, 22, 99]]
    assert batch["labels"].tolist() == [[-100, -100, 21, 99], [-100, -100, 22, 99]]
```

- [ ] **Step 2: Run the data tests to verify the module is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_data.py -q
```

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.qwen_data'`.

- [ ] **Step 3: Implement the schema, encoder, and collator**

Define these focused types and functions in `qwen_data.py`:

```python
IGNORE_LABEL = -100

@dataclass(frozen=True)
class QwenMessage:
    role: Literal["system", "user", "assistant"]
    content: str

@dataclass(frozen=True)
class EncodedQwenResponse:
    input_ids: list[int]
    labels: list[int]

def encode_qwen_response(tokenizer: Any, messages: tuple[QwenMessage, ...], response: str, max_length: int) -> EncodedQwenResponse:
    prompt_ids = tokenizer.apply_chat_template(
        [asdict(message) for message in messages], tokenize=True, add_generation_prompt=True
    )
    response_ids = tokenizer(response, add_special_tokens=False)["input_ids"] + [tokenizer.eos_token_id]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)):]
    return EncodedQwenResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
    )
```

Validate roles, non-empty normalized content, a final user message before the
target response, a positive `max_length`, and a defined tokenizer EOS ID.
Implement `QwenResponseCollator` to left-pad `input_ids` with the tokenizer
pad ID, set left padded attention entries to zero, and pad `labels` with
`IGNORE_LABEL`.

- [ ] **Step 4: Run the data tests**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_data.py -q
```

Expected: PASS; no optional Qwen dependency or model download is needed.

- [ ] **Step 5: Commit the template-aligned data core**

```powershell
git add src/mini_gpt/qwen_data.py tests/test_qwen_data.py
git commit -m "feat: add Qwen response-only data encoding"
```

### Task 4: Build a short, licensed Ru V3 corpus from local sources

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_qwen_corpus.py`
- Create: `D:\LLM\scripts\build_ru_qwen_v3_data.py`
- Create: `D:\LLM\tests\test_ru_qwen_corpus.py`
- Create at runtime: `D:\LLM\data\conversations\ru_v3\train.jsonl`
- Create at runtime: `D:\LLM\data\conversations\ru_v3\val.jsonl`
- Create at runtime: `D:\LLM\data\conversations\ru_v3\manifest.json`

- [ ] **Step 1: Write failing tests for casual-pair selection and atomic output**

```python
def test_selects_short_everyday_pair_and_rejects_instruction_lists():
    assert casual_rejection_reason("Hôm nay anh hơi mệt.", "Em nghe đây. Anh muốn kể thêm không?") is None
    assert casual_rejection_reason("Explain sorting", "1. First do this\n2. Then do that") == "list_heavy"
    assert casual_rejection_reason("hello", "As an AI language model, I cannot...") == "ai_disclaimer"


def test_build_corpus_writes_only_complete_train_val_and_manifest(tmp_path):
    report = build_ru_qwen_corpus(source_path, tmp_path, validation_ratio=0.25, seed=7)

    assert report["total_examples"] == 4
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert not list(tmp_path.glob("*.partial"))
```

- [ ] **Step 2: Run the corpus tests to verify the module is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py -q
```

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.ru_qwen_corpus'`.

- [ ] **Step 3: Implement V3 filtering and deterministic splits**

Read single-turn records from the already downloaded
`data/conversations/response_v2_seed.jsonl` and stream the Apache-2.0
`5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca` source for contextual examples.
For 5CD, omit its system turns, retain up to two completed preceding
user/assistant turns as `messages`, and train only the final adjacent
`human -> gpt` response. Accept records from
`hoanghai2110/vietnamese-dataset`, `HuggingFaceH4/ultrachat_200k`, and 5CD only
when all conditions are true:

```python
MAX_PROMPT_CHARACTERS = 180
MAX_RESPONSE_CHARACTERS = 260
REJECTED_RESPONSE_PATTERNS = (
    r"(?im)^\s*(\d+\.|[-*])\s+",
    r"(?i)\b(as an ai language model|i cannot access)\b",
    r"(?i)(với tư cách là|là một mô hình ngôn ngữ ai)",
)
REJECTED_PROMPT_PATTERNS = (
    r"(?i)^(write|explain|list|create|generate|viết|giải thích|liệt kê|tạo)\b",
)
```

Reuse `quality_rejection_reason` before applying these V3-specific checks.
Convert every accepted record to exactly this JSONL schema:

```json
{"messages":[{"role":"system","content":"You are Ru, a friendly bilingual local companion. Reply naturally and concisely in the user's language."},{"role":"user","content":"Xin chào"}],"response":"Chào anh, hôm nay anh thế nào?","language":"vi","source":"hoanghai2110/vietnamese-dataset","license":"Apache-2.0"}
```

Globally deduplicate `(messages[-1].content, response)`, use `Random(seed)` for
a 90/10 train/validation split, write `.partial` files first, and atomically
publish `train.jsonl`, `val.jsonl`, and `manifest.json` only when at least 500
unique records survive, including at least 125 contextual examples with a
preceding assistant turn and at least one Vietnamese and English example. The
manifest must contain input SHA-256, 5CD dataset revision, source counts,
contextual count, language counts, rejections, train/validation counts,
filtering constants, seed, and licences.

- [ ] **Step 4: Run corpus tests and the actual local corpus build**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py -q
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --input data\conversations\response_v2_seed.jsonl --output-dir data\conversations\ru_v3
```

Expected: tests PASS; the script reports at least 500 unique records, at least
125 contextual examples, both languages, source/licence counts, and no partial
files. Stop before QLoRA training if any threshold is absent.

- [ ] **Step 5: Inspect a bounded human-readable sample**

Run:

```powershell
Get-Content data\conversations\ru_v3\train.jsonl -TotalCount 12
```

Expected: short everyday replies, no numbered instructions, no links, no
template tags, and no obvious repetition. If two or more of the 12 lines fail
this check, tighten the rejection patterns and rerun Task 4 before training.

- [ ] **Step 6: Commit the corpus builder, not generated data**

```powershell
git add src/mini_gpt/ru_qwen_corpus.py scripts/build_ru_qwen_v3_data.py tests/test_ru_qwen_corpus.py
git commit -m "feat: build curated Ru Qwen chat corpus"
```

### Task 5: Download and verify the immutable local Qwen base

**Files:**
- Create: `D:\LLM\scripts\download_ru_qwen_base.py`
- Modify: `D:\LLM\tests\test_qwen_runtime.py`
- Create at runtime: `D:\LLM\models\qwen2.5-0.5b-instruct\*`

- [ ] **Step 1: Write a failing local-model verification test**

```python
def test_verify_local_qwen_base_rejects_the_wrong_model_type(tmp_path):
    (tmp_path / "config.json").write_text('{"model_type":"gpt2"}', encoding="utf-8")

    with pytest.raises(ValueError, match="qwen2"):
        verify_local_qwen_base(tmp_path)
```

- [ ] **Step 2: Run the focused test to verify the verifier is missing**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py::test_verify_local_qwen_base_rejects_the_wrong_model_type -q
```

Expected: FAIL because `verify_local_qwen_base` does not yet exist.

- [ ] **Step 3: Implement download and verification**

Add this verifier to `qwen_runtime.py`:

```python
def verify_local_qwen_base(path: str | Path) -> dict[str, object]:
    config_path = Path(path) / "config.json"
    if not config_path.is_file():
        raise FileNotFoundError(f"Qwen config not found: {config_path}")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if config.get("model_type") != "qwen2":
        raise ValueError("local base model_type must be qwen2")
    if not any(Path(path).glob("*.safetensors")):
        raise FileNotFoundError("local Qwen safetensors weights not found")
    return config
```

Implement `download_ru_qwen_base.py` with `huggingface_hub.snapshot_download`,
the fixed repo ID `QWEN_MODEL_ID`, `local_dir`, and `local_dir_use_symlinks=False`.
It must call `verify_local_qwen_base` after download and emit model ID, local
path, model type, and total safetensors bytes. It must not delete or replace an
already verified local base.

- [ ] **Step 4: Install download support, fetch once, and verify the result**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pip install --upgrade huggingface_hub
& .\.venv\Scripts\python.exe scripts\download_ru_qwen_base.py --output-dir D:\LLM\models\qwen2.5-0.5b-instruct
& .\.venv\Scripts\python.exe -c "from mini_gpt.qwen_runtime import verify_local_qwen_base; print(verify_local_qwen_base(r'D:\LLM\models\qwen2.5-0.5b-instruct')['model_type'])"
```

Expected: `qwen2`. Stop if less than 5 GB remains on D: after download or the
base fails verification.

- [ ] **Step 5: Commit only the downloader and tests**

```powershell
git add src/mini_gpt/qwen_runtime.py scripts/download_ru_qwen_base.py tests/test_qwen_runtime.py
git commit -m "feat: add local Qwen base downloader"
```

### Task 6: Train and retain a validation-best Ru adapter

**Files:**
- Create: `D:\LLM\src\mini_gpt\qwen_training.py`
- Create: `D:\LLM\scripts\train_ru_qwen_v3.py`
- Create: `D:\LLM\configs\ru_qwen_v3.json`
- Create: `D:\LLM\tests\test_qwen_training.py`
- Create at runtime: `D:\LLM\checkpoints\ru-qwen-v3\adapter\*`

- [ ] **Step 1: Write failing configuration and manifest tests**

```python
def test_qwen_training_config_rejects_a_model_other_than_the_approved_base():
    with pytest.raises(ValueError, match="Qwen/Qwen2.5-0.5B-Instruct"):
        QwenTrainingConfig(base_model_id="other/model")


def test_adapter_manifest_records_reproducible_identity(tmp_path):
    manifest = write_adapter_manifest(
        tmp_path,
        base_model_id="Qwen/Qwen2.5-0.5B-Instruct",
        base_model_path="D:/LLM/models/qwen2.5-0.5b-instruct",
        dataset_sha256="abc",
        best_validation_loss=1.25,
        peak_vram_mb=1200.0,
    )

    assert manifest["backend"] == "qwen-qlora-v3"
    assert json.loads((tmp_path / "ru_manifest.json").read_text(encoding="utf-8")) == manifest
```

- [ ] **Step 2: Run the test to verify the training module is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_training.py -q
```

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.qwen_training'`.

- [ ] **Step 3: Create exact QLoRA config and pure training metadata functions**

Create `configs/ru_qwen_v3.json`:

```json
{
  "base_model_id": "Qwen/Qwen2.5-0.5B-Instruct",
  "base_model_path": "models/qwen2.5-0.5b-instruct",
  "dataset_dir": "data/conversations/ru_v3",
  "adapter_dir": "checkpoints/ru-qwen-v3/adapter",
  "max_length": 512,
  "seed": 42,
  "epochs": 2,
  "learning_rate": 0.0001,
  "batch_size": 1,
  "gradient_accumulation_steps": 16,
  "evaluation_steps": 50,
  "save_steps": 50,
  "logging_steps": 10,
  "lora_rank": 16,
  "lora_alpha": 32,
  "lora_dropout": 0.05,
  "target_modules": ["q_proj", "k_proj", "v_proj", "o_proj"]
}
```

Implement `QwenTrainingConfig.from_json`, strict validation for every positive
numeric field, the fixed model ID, the four target modules, and `epochs <= 3`.
Implement `write_adapter_manifest` with `backend`, `base_model_id`,
`base_model_path`, `dataset_sha256`, `best_validation_loss`, `peak_vram_mb`,
`seed`, `max_length`, and UTC timestamp.

- [ ] **Step 4: Implement the lazy QLoRA trainer**

In `train_ru_adapter(config)`, call `require_qwen_dependencies`, verify the
local base and corpus manifest, then lazily import Transformers/PEFT. Load the
base with:

```python
quantization = BitsAndBytesConfig(
    load_in_4bit=True,
    bnb_4bit_quant_type="nf4",
    bnb_4bit_use_double_quant=True,
    bnb_4bit_compute_dtype=torch.float16,
)
model = AutoModelForCausalLM.from_pretrained(
    config.base_model_path,
    quantization_config=quantization,
    torch_dtype=torch.float16,
)
model = prepare_model_for_kbit_training(model)
model = get_peft_model(model, LoraConfig(
    r=config.lora_rank,
    lora_alpha=config.lora_alpha,
    lora_dropout=config.lora_dropout,
    target_modules=list(config.target_modules),
    bias="none",
    task_type="CAUSAL_LM",
))
```

Use `Trainer` with the Task 3 collator, `eval_strategy="steps"`,
`save_strategy="steps"`, `load_best_model_at_end=True`, and
`metric_for_best_model="eval_loss"`. After training, save the selected adapter
to `adapter_dir`, write `ru_manifest.json`, and return it. The trainer must
raise if CUDA is unavailable, the train/validation JSONL is missing, or there
is no measured `eval_loss`.

Create `train_ru_qwen_v3.py` with `--config`, `--resume-from-checkpoint`, and
`--max-steps`. When supplied, `--max-steps` must be positive and is passed to
`TrainingArguments.max_steps`, enabling a bounded CUDA smoke run without
changing the committed configuration. It prints only the manifest JSON on
completion.

- [ ] **Step 5: Run pure tests, then a bounded CUDA adapter smoke run**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_training.py tests\test_qwen_data.py -q
& .\.venv\Scripts\python.exe scripts\train_ru_qwen_v3.py --config configs\ru_qwen_v3.json --max-steps 2
```

Expected: tests PASS; CUDA run creates `adapter\adapter_config.json`, adapter
weights, and `adapter\ru_manifest.json`. Stop the run and report if peak VRAM
exceeds 5,500 MiB, CUDA OOM occurs, or an adapter manifest is absent.

- [ ] **Step 6: Run the configured two-epoch adapter training after the smoke passes**

Run:

```powershell
& .\.venv\Scripts\python.exe scripts\train_ru_qwen_v3.py --config configs\ru_qwen_v3.json
```

Expected: the final manifest identifies a validation-best adapter and the
training log contains at least one measured validation loss. If validation loss
is absent, adapter files are incomplete, or peak VRAM exceeds 5,500 MiB, retain
the smoke artifact for diagnosis and do not make V3 the GUI default.

- [ ] **Step 7: Commit trainer source and configuration, not model artifacts**

```powershell
git add src/mini_gpt/qwen_training.py scripts/train_ru_qwen_v3.py configs/ru_qwen_v3.json tests/test_qwen_training.py
git commit -m "feat: train Ru Qwen QLoRA adapter"
```

### Task 7: Add a Qwen-backed Ru GUI runner without breaking Mini-GPT

**Files:**
- Modify: `D:\LLM\src\mini_gpt\qwen_runtime.py`
- Modify: `D:\LLM\src\mini_gpt\gui.py`
- Modify: `D:\LLM\tests\test_gui.py`
- Modify: `D:\LLM\tests\test_qwen_runtime.py`

- [ ] **Step 1: Write the failing V3 priority and routing tests**

```python
def test_default_paths_prefer_verified_ru_qwen_manifest(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    manifest = tmp_path / "checkpoints" / "ru-qwen-v3" / "adapter" / "ru_manifest.json"
    manifest.parent.mkdir(parents=True)
    manifest.write_text('{"backend":"qwen-qlora-v3"}', encoding="utf-8")
    (tmp_path / "checkpoints" / "chat-response-v2").mkdir(parents=True)
    (tmp_path / "checkpoints" / "chat-response-v2" / "best.pt").touch()

    checkpoint, _ = gui.default_paths()

    assert checkpoint == manifest


def test_model_runner_routes_a_qwen_manifest_to_qwen_backend(monkeypatch, tmp_path):
    manifest = tmp_path / "ru_manifest.json"
    manifest.write_text('{"backend":"qwen-qlora-v3"}', encoding="utf-8")
    monkeypatch.setattr(gui, "QwenRunner", FakeQwenRunner)

    runner = gui.ModelRunner()
    assert runner.load(manifest, tmp_path / "tokenizer.json") == "Ready | qwen"
```

- [ ] **Step 2: Run GUI tests to verify V3 is not yet supported**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q
```

Expected: FAIL because `default_paths()` does not consider the V3 manifest and
`QwenRunner` does not exist.

- [ ] **Step 3: Implement QwenRunner with the shared chat contract**

Add `QwenRunner` in `qwen_runtime.py` with this public interface:

```python
class QwenRunner:
    def load(self, manifest_path: str | Path, device_name: str = "auto") -> str: ...
    def generate_reply(
        self,
        turns: list[ChatTurn],
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
    ) -> str: ...
```

`load` validates `backend == "qwen-qlora-v3"`, verifies the base path from the
manifest, loads the adapter with `PeftModel.from_pretrained`, and caches the
loaded model/tokenizer. `generate_reply` creates this exact system message
before saved turns and the new user turn:

```text
You are Ru, a friendly bilingual local companion. Reply naturally and concisely in the user's language.
```

It must use `tokenizer.apply_chat_template(..., add_generation_prompt=True)`,
generate `max_new_tokens`, slice off prompt IDs, decode only new IDs, and reject
an empty result or a result beginning with `<|im_`.

Refactor `ModelRunner` into a small delegator: `.pt` paths keep the existing
MiniGPT load/generation behavior; `ru_manifest.json` paths delegate to
`QwenRunner`. Refactor `MiniGPTApp._start_generation` and `_generate_worker`
to pass `self.session_turns` plus the original prompt to
`ModelRunner.generate_chat_reply`; call existing `format_chat_prompt` only in
the MiniGPT branch. Update the browse dialog to allow `.pt` and
`ru_manifest.json` files.

- [ ] **Step 4: Run GUI/runtime regression tests**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_qwen_runtime.py tests\test_chat.py -q
```

Expected: PASS; all existing MiniGPT GUI defaults remain valid when V3 is absent.

- [ ] **Step 5: Start a visible Ru GUI smoke test only after V3 adapter exists**

Run:

```powershell
Start-Process -FilePath "D:\LLM\.venv\Scripts\python.exe" -ArgumentList "-m mini_gpt.gui" -WorkingDirectory "D:\LLM"
```

Expected: the checkpoint field defaults to
`checkpoints\ru-qwen-v3\adapter\ru_manifest.json`; send one Vietnamese and one
English greeting, then verify both results appear as Ru responses in the
transcript. Do not keep a hidden process running after the visual check.

- [ ] **Step 6: Commit GUI routing**

```powershell
git add src/mini_gpt/qwen_runtime.py src/mini_gpt/gui.py tests/test_gui.py tests/test_qwen_runtime.py
git commit -m "feat: run Ru with local Qwen adapter"
```

### Task 8: Run the conversation gate and update project documentation

**Files:**
- Create: `D:\LLM\scripts\evaluate_ru_qwen_v3.py`
- Create at runtime: `D:\LLM\checkpoints\ru-qwen-v3\conversation_evaluation.json`
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\TODO.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-ru-qwen-qlora-conversation-implementation.md`

- [ ] **Step 1: Write the failing fixed-suite test**

```python
def test_conversation_suite_contains_twelve_bilingual_cases():
    prompts = default_conversation_suite()

    assert len(prompts) == 12
    assert {case.language for case in prompts} == {"vi", "en"}
    assert any(case.has_history for case in prompts)
```

- [ ] **Step 2: Run the test to verify the evaluator is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py::test_conversation_suite_contains_twelve_bilingual_cases -q
```

Expected: FAIL because the fixed suite is not defined.

- [ ] **Step 3: Implement deterministic base-versus-Ru evaluation**

Create a `ConversationCase` dataclass and `default_conversation_suite()` with
exactly these 12 cases: Vietnamese greeting, Vietnamese thanks, Vietnamese
feeling tired, Vietnamese follow-up after gaming, Vietnamese language switch,
Vietnamese simple daily question, and the English equivalents. Run each case
once through the base-only runner and once through the adapter runner using
seed 42, `max_new_tokens=80`, `temperature=0.7`, and `top_k=20`.

Write `conversation_evaluation.json` with each prompt, both outputs, language,
history flag, checkpoint paths, generation settings, and these automated flags:

```python
def response_flags(text: str) -> dict[str, bool]:
    clean = text.strip()
    words = clean.casefold().split()
    repeated_adjacent = any(words[index] == words[index + 1] == words[index + 2] for index in range(len(words) - 2))
    return {
        "non_empty": bool(clean),
        "no_template_leak": not clean.startswith("<|im_") and "<|im_start|>" not in clean,
        "no_user_label": not clean.startswith("User:"),
        "no_immediate_repetition": not repeated_adjacent,
    }
```

- [ ] **Step 4: Run tests and the real conversation suite**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_v3.py --config configs\ru_qwen_v3.json
```

Expected: all 12 adapter outputs have every automated flag true. Manually rate
each adapter output natural/not-natural against its prompt; pass only when at
least 10 are natural and no language-following failure appears.

- [ ] **Step 5: Update docs based only on the observed verdict**

On a pass, add the verified local Qwen install/train/run commands to README and
mark `Make Ru consistently natural...` complete in TODO. On a fail, retain that
TODO unchecked and add the exact failed case names plus the evaluation JSON path
to README. In either outcome, mark completed plan checkboxes and state that V1
and V2 remain preserved experiments.

- [ ] **Step 6: Run final regression and inspect document freshness**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
rg -n "response-v2|chat-response-v2|ru-qwen-v3|Qwen/Qwen2.5-0.5B-Instruct|V2 result" README.md TODO.md docs\superpowers\plans\2026-07-20-ru-qwen-qlora-conversation-implementation.md
git status --short
```

Expected: all tests PASS; documentation contains current V2/V3 paths and
outcomes; generated data/model artifacts remain ignored; only intended source,
test, config, script, and documentation changes are staged.

- [ ] **Step 7: Commit implementation result and documentation**

```powershell
git add scripts/evaluate_ru_qwen_v3.py README.md TODO.md docs/superpowers/plans/2026-07-20-ru-qwen-qlora-conversation-implementation.md tests/test_qwen_runtime.py
git commit -m "docs: evaluate Ru Qwen conversation quality"
```

## Plan self-review

- **Spec coverage:** Task 1 records V2 accurately. Task 2 verifies the Windows
  QLoRA runtime. Tasks 3–4 build short, licensed, template-aligned response
  supervision including contextual multi-turn examples. Task 5 retains an
  immutable local Qwen base. Task 6 runs a CUDA smoke check before training only
  a validation-best LoRA adapter and records reproducibility metadata. Task 7
  makes V3 the GUI default only when valid. Task 8 uses the specified 12-prompt
  manual gate and documents either verdict.
- **Placeholder scan:** No open-ended implementation steps, unnamed files, or
  deferred validation actions remain. Every code change has an exact path,
  test, command, expected result, and commit boundary.
- **Type consistency:** `QwenMessage`, `EncodedQwenResponse`,
  `QwenResponseCollator`, `QwenTrainingConfig`, `QwenRunner`, and
  `verify_local_qwen_base` are introduced before any later task uses them. The
  V3 adapter manifest name is consistently `ru_manifest.json`.
