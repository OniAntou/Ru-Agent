# Ru Qwen3 4B Local Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Download and evaluate the Qwen3-4B base locally for Ru without creating any dataset, adapter, training run, or GUI change.

**Architecture:** Extend the existing Qwen runtime with one registered Qwen3 profile, accepted qwen3 model validation, and a non-thinking chat-template flag. Reuse the current fixed 32-case evaluator and decision recorder so Qwen3 produces one isolated, directly comparable local evidence artifact.

**Tech Stack:** Python 3.11, PyTorch, Transformers 4.57, BitsAndBytes 4-bit NF4, Hugging Face Hub, pytest, Windows PowerShell.

---

## Scope guardrails

- [ ] Only Qwen/Qwen3-4B is in scope. Do not download or mention Gemma runtime artifacts.
- [ ] Use official model weights under models/qwen3-4b and run entirely locally.
- [ ] Do not create a Qwen3 dataset, adapter, training script, training config, or GUI setting.
- [ ] Stop on CUDA OOM, peak allocated VRAM above 5,500 MiB, less than 5 GB free on D:, or any automatic response disqualifier.
- [ ] Only perform manual review if every automatic response check passes.

## Task 1: Register Qwen3 as a non-thinking local Qwen profile

**Files:**

- Modify: src/mini_gpt/qwen_runtime.py
- Modify: tests/test_qwen_runtime.py

- [ ] **Step 1: Write the failing profile and validation tests.**

Add a test named test_qwen3_profile_is_registered_with_non_thinking_generation. It must assert:

~~~python
assert QWEN3_4B_BASE.model_id == "Qwen/Qwen3-4B"
assert QWEN3_4B_BASE.local_path == "models/qwen3-4b"
assert QWEN3_4B_BASE.label == "Ru Qwen3 4B"
assert QWEN3_4B_BASE.disable_thinking is True
assert qwen_base_profile(QWEN3_4B_BASE.model_id) == QWEN3_4B
~~~

Add a local verification test with config.json containing model_type qwen3 and a dummy safetensors file. It must pass. Retain the existing gpt2 rejection test.

- [ ] **Step 2: Run the focused tests and verify RED.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
~~~

Expected: import of QWEN3_4B_BASE fails and qwen3 is rejected by local verification.

- [ ] **Step 3: Implement the profile and model type support.**

Extend QwenBaseProfile without changing existing profiles:

~~~python
@dataclass(frozen=True)
class QwenBaseProfile:
    model_id: str
    local_path: str
    label: str
    disable_thinking: bool = False
~~~

Add:

~~~python
QWEN3_4B_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen3-4B",
    local_path="models/qwen3-4b",
    label="Ru Qwen3 4B",
    disable_thinking=True,
)
~~~

Include it in QWEN_BASE_PROFILES. Replace the qwen2-only check with a named supported set:

~~~python
SUPPORTED_QWEN_MODEL_TYPES = frozenset({"qwen2", "qwen3"})
...
if not isinstance(config, dict) or config.get("model_type") not in SUPPORTED_QWEN_MODEL_TYPES:
    raise ValueError("local base model_type must be qwen2 or qwen3")
~~~

- [ ] **Step 4: Add the Qwen3-only template behavior.**

Build chat-template kwargs before calling apply_chat_template:

~~~python
template_kwargs = {
    "tokenize": True,
    "add_generation_prompt": True,
    "return_tensors": "pt",
}
if profile.disable_thinking:
    template_kwargs["enable_thinking"] = False
input_ids = tokenizer.apply_chat_template(serialized, **template_kwargs).to("cuda")
~~~

Extend the fake-tokenizer tests to assert Qwen V4 keeps the old kwargs and Qwen3 receives enable_thinking=False. Keep the explicit attention mask test.

- [ ] **Step 5: Verify and commit.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
git add src/mini_gpt/qwen_runtime.py tests/test_qwen_runtime.py
git diff --cached --check
git commit -m "feat: register Ru Qwen3 4B base"
~~~

Expected: all Qwen runtime tests pass.

## Task 2: Add a fixed Qwen3 downloader and base-only config

**Files:**

- Create: scripts/download_ru_qwen3_4b_base.py
- Create: configs/ru_qwen3_4b.json
- Modify: tests/test_qwen_runtime.py
- Modify: tests/test_evaluate_ru_qwen_script.py

- [ ] **Step 1: Write failing downloader/config tests.**

Add a subprocess help test for scripts/download_ru_qwen3_4b_base.py. It must return zero and print:

~~~text
Download and verify Ru's Qwen3 4B base
~~~

Add a config-loading test that reads configs/ru_qwen3_4b.json through load_evaluation_config and asserts:

~~~python
assert config["profile_name"] == "ru_qwen3_4b"
assert config["base_model_id"] == "Qwen/Qwen3-4B"
assert config["base_model_path"] == "models/qwen3-4b"
assert config["phase"] == "base_only"
assert "dataset_dir" not in config
assert "adapter_dir" not in config
~~~

- [ ] **Step 2: Run focused tests and verify RED.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py tests\test_evaluate_ru_qwen_script.py -q
~~~

Expected: Qwen3 downloader/config paths do not exist.

- [ ] **Step 3: Implement the fixed downloader.**

Create the downloader with project-root resolution, no output-directory override, and verification through QWEN3_4B_BASE:

~~~python
from mini_gpt.qwen_runtime import QWEN3_4B_BASE, verify_local_qwen_base

def report() -> dict[str, object]:
    output_dir = PROJECT_ROOT / QWEN3_4B_BASE.local_path
    config = verify_local_qwen_base(output_dir)
    return {
        "model_id": QWEN3_4B_BASE.model_id,
        "local_path": str(output_dir.resolve()),
        "model_type": config["model_type"],
        "safetensors_bytes": sum(path.stat().st_size for path in output_dir.glob("*.safetensors")),
    }
~~~

On missing or invalid local files, call snapshot_download with QWEN3_4B_BASE.model_id and that exact output directory, then report verified results.

- [ ] **Step 4: Add the base-only config.**

Create configs/ru_qwen3_4b.json:

~~~json
{
  "profile_name": "ru_qwen3_4b",
  "base_model_id": "Qwen/Qwen3-4B",
  "base_model_path": "models/qwen3-4b",
  "phase": "base_only"
}
~~~

- [ ] **Step 5: Verify and commit.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py tests\test_evaluate_ru_qwen_script.py -q
git add scripts/download_ru_qwen3_4b_base.py configs/ru_qwen3_4b.json tests/test_qwen_runtime.py tests/test_evaluate_ru_qwen_script.py
git diff --cached --check
git commit -m "feat: add Ru Qwen3 base acquisition"
~~~

Expected: focused tests pass without loading model weights.

## Task 3: Preflight, download, and measure Qwen3 base only

**Runtime artifacts:**

- Create: models/qwen3-4b (ignored)
- Create: checkpoints/ru-qwen3-4b/base_evaluation.json (ignored)

- [ ] **Step 1: Verify local hardware and repository state.**

~~~powershell
Get-PSDrive -Name D
& .\.venv\Scripts\python.exe -c "import torch; print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'no CUDA')"
git status --short
~~~

Stop before download when CUDA is unavailable, unexpected overlapping edits exist, or expected model storage would leave less than 5 GB free.

- [ ] **Step 2: Download and verify the official Qwen3 snapshot.**

~~~powershell
& .\.venv\Scripts\python.exe scripts\download_ru_qwen3_4b_base.py
Get-PSDrive -Name D
Get-ChildItem models\qwen3-4b -File | Select-Object Name,Length
& .\.venv\Scripts\python.exe -c "from pathlib import Path; p=Path('models/qwen3-4b'); print(sum(x.stat().st_size for x in p.glob('*.safetensors')))"
~~~

Expected: verified qwen3 config, tokenizer, non-zero safetensors bytes, and at least 5 GB free on D:.

- [ ] **Step 3: Run one valid adapter-free evaluation.**

~~~powershell
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_v4.py --config configs\ru_qwen3_4b.json --output checkpoints\ru-qwen3-4b\base_evaluation.json
~~~

The evaluation must use non-thinking generation, no adapter argument, the fixed 32 cases, and the reset peak-memory counter. Capture peak_allocated_mib from the saved JSON.

- [ ] **Step 4: Apply the decision gate.**

If CUDA OOM occurs, peak_allocated_mib is above 5,500, or the base automatic gate fails, run:

~~~powershell
& .\.venv\Scripts\python.exe scripts\record_ru_qwen_base_review.py --evaluation checkpoints\ru-qwen3-4b\base_evaluation.json --automatic-only
~~~

Do not create manual review data, corpus, adapter, or training command.

Only when the automatic gate passes, write exactly 32 manual review objects to checkpoints/ru-qwen3-4b/base_review.json and run:

~~~powershell
& .\.venv\Scripts\python.exe scripts\record_ru_qwen_base_review.py --evaluation checkpoints\ru-qwen3-4b\base_evaluation.json --review checkpoints\ru-qwen3-4b\base_review.json
~~~

Adopt only when the persisted decision says passed with manual_natural_count at least 24.

- [ ] **Step 5: Run the full test suite.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest -q
~~~

Expected: all tests pass. Keep models and evaluation artifacts ignored.

## Task 4: Document measured evidence and close the gate

**Files:**

- Modify: README.md
- Modify: TODO.md
- Modify: docs/superpowers/specs/2026-07-21-ru-qwen3-4b-baseline-design.md
- Modify: docs/superpowers/plans/2026-07-21-ru-qwen3-4b-baseline-implementation.md

- [ ] **Step 1: Write only observed Qwen3 evidence.**

Record the verified weight bytes, D: free space after download, measured peak MiB, automatic failure count, manual score when it exists, and final verdict. State explicitly that no fine-tuning, corpus, adapter, or GUI backend change occurred on a failed baseline.

- [ ] **Step 2: Review project documents for stale Gemma or planned-result text.**

~~~powershell
rg -n "Gemma|Qwen3|ru_qwen3_4b|not adopted|passed|planned" README.md TODO.md docs
git diff --check
~~~

Delete only a document that is wholly superseded. Keep V5 documents as historical evidence.

- [ ] **Step 3: Commit the observed result.**

~~~powershell
git add README.md TODO.md docs/superpowers/specs/2026-07-21-ru-qwen3-4b-baseline-design.md docs/superpowers/plans/2026-07-21-ru-qwen3-4b-baseline-implementation.md
git diff --cached --check
git commit -m "docs: record Ru Qwen3 baseline result"
~~~

