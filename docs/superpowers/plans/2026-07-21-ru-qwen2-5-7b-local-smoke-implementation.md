# Ru Qwen2.5 7B Local Smoke Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Download and evaluate one local Qwen2.5-7B-Instruct candidate against Ru's eight-case bilingual conversation smoke gate.

**Architecture:** Register an isolated Qwen2.5 7B profile/config and reuse the existing NF4 CUDA runtime. Generalize the prompt-routing smoke script with an explicit hardware decision that writes evidence on an OOM or VRAM-limit rejection; only a complete hardware pass reaches the existing manual review recorder.

**Tech Stack:** Python 3.11, pytest, PyTorch CUDA, Transformers, bitsandbytes, huggingface_hub, Qwen2.5-7B-Instruct.

---

### Task 1: Register and acquire the isolated 7B candidate

**Files:**

- Modify: `src/mini_gpt/qwen_runtime.py`
- Create: `configs/ru_qwen_v6_7b.json`
- Create: `scripts/download_ru_qwen_v6_7b_base.py`
- Modify: `tests/test_qwen_runtime.py`
- Modify: `tests/test_evaluate_ru_qwen_script.py`

- [ ] **Step 1: Write failing registration and config tests.**

Add these tests:

```python
def test_v6_profile_is_the_registered_local_qwen_7b_base():
    from mini_gpt.qwen_runtime import QWEN_V6_BASE, qwen_base_profile

    assert QWEN_V6_BASE.model_id == "Qwen/Qwen2.5-7B-Instruct"
    assert QWEN_V6_BASE.local_path == "models/qwen2.5-7b-instruct"
    assert QWEN_V6_BASE.label == "Ru V6 Qwen 7B"
    assert QWEN_V6_BASE.disable_thinking is False
    assert qwen_base_profile(QWEN_V6_BASE.model_id) == QWEN_V6_BASE
```

Add a config-load test asserting `profile_name == "ru_qwen_v6_7b"`,
`base_model_id == "Qwen/Qwen2.5-7B-Instruct"`,
`base_model_path == "models/qwen2.5-7b-instruct"`, and `phase ==
"base_only"`. Add a downloader help test requiring `Download and verify Ru's
V6 Qwen 7B base`.

- [ ] **Step 2: Confirm RED.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py tests\test_evaluate_ru_qwen_script.py -q
```

Expected: imports/config/script are absent.

- [ ] **Step 3: Add the profile, config, and downloader.**

Add this profile alongside V3/V4/V5 and include it in `QWEN_BASE_PROFILES`:

```python
QWEN_V6_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-7B-Instruct",
    local_path="models/qwen2.5-7b-instruct",
    label="Ru V6 Qwen 7B",
)
```

Create `configs/ru_qwen_v6_7b.json`:

```json
{
  "profile_name": "ru_qwen_v6_7b",
  "base_model_id": "Qwen/Qwen2.5-7B-Instruct",
  "base_model_path": "models/qwen2.5-7b-instruct",
  "phase": "base_only"
}
```

Create the downloader by importing `QWEN_V6_BASE` and `verify_local_qwen_base`.
`report()` must return `model_id`, resolved `local_path`, `model_type`, and the
sum of top-level `*.safetensors` bytes. `main()` attempts `report()`, calls
`huggingface_hub.snapshot_download(repo_id=QWEN_V6_BASE.model_id,
local_dir=str(PROJECT_ROOT / QWEN_V6_BASE.local_path))` only when verification
raises `FileNotFoundError` or `ValueError`, then prints the verified JSON.

- [ ] **Step 4: Confirm GREEN and commit.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py tests\test_evaluate_ru_qwen_script.py -q
git add src\mini_gpt\qwen_runtime.py configs\ru_qwen_v6_7b.json scripts\download_ru_qwen_v6_7b_base.py tests\test_qwen_runtime.py tests\test_evaluate_ru_qwen_script.py
git diff --cached --check
git commit -m "feat: register Ru Qwen 7B candidate"
```

### Task 2: Make the routed smoke record a hardware rejection

**Files:**

- Modify: `scripts/evaluate_ru_qwen_prompt_routing.py`
- Modify: `tests/test_evaluate_ru_qwen_script.py`

- [ ] **Step 1: Write failing pure hardware-gate tests.**

Import `hardware_gate` from the smoke script and add:

```python
assert hardware_gate(5500.0, 5500.0) == {
    "passed": True,
    "peak_allocated_mib": 5500.0,
    "peak_limit_mib": 5500.0,
}
assert hardware_gate(5500.001, 5500.0)["passed"] is False
```

Add parser tests that assert `--max-peak-mib` defaults to `5500.0` and help
still contains `Prompt-routing smoke`.

- [ ] **Step 2: Confirm RED.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_evaluate_ru_qwen_script.py -q
```

Expected: `hardware_gate` and `--max-peak-mib` are absent.

- [ ] **Step 3: Add the hardware-gate function and bounded smoke flow.**

Add:

```python
def hardware_gate(peak_allocated_mib: float | None, peak_limit_mib: float) -> dict[str, float | bool | None]:
    return {
        "passed": peak_allocated_mib is not None and peak_allocated_mib <= peak_limit_mib,
        "peak_allocated_mib": peak_allocated_mib,
        "peak_limit_mib": peak_limit_mib,
    }
```

Add `--max-peak-mib` as a positive float with default `5500.0`. After every
generated response, obtain `bytes_to_mib(torch.cuda.max_memory_allocated())`.
If it exceeds the limit, stop generating further cases, clear the model cache,
write the partial artifact with `hardware_gate`, `verdict: "not_adopted"`, and
`decision: {"phase": "hardware", "failure_reasons": ["peak_vram_exceeded"]}`;
return exit code `1`.

Catch `torch.OutOfMemoryError` around generation, clear the cache, write any
completed case rows plus `hardware_gate.passed: false`, `verdict:
"not_adopted"`, and `decision: {"phase": "hardware", "failure_reasons":
["out_of_memory"]}`; return exit code `1`. Do not load with CPU/RAM offload
or retry at another quantization. A complete smoke below the limit writes
`hardware_gate.passed: true` and preserves the existing `verdict:
"not_reviewed"` path for manual review.

- [ ] **Step 4: Confirm GREEN and commit.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_evaluate_ru_qwen_script.py -q
git add scripts\evaluate_ru_qwen_prompt_routing.py tests\test_evaluate_ru_qwen_script.py
git diff --cached --check
git commit -m "feat: gate Ru smoke by GPU memory"
```

### Task 3: Download, evaluate, review, and document Qwen2.5 7B

**Files:**

- Create at runtime: `models/qwen2.5-7b-instruct/`
- Create at runtime: `checkpoints/ru-qwen-v6-7b/prompt_routing_smoke.json`
- Create at runtime: `checkpoints/ru-qwen-v6-7b/prompt_routing_smoke_review.json`
- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/superpowers/specs/2026-07-21-ru-qwen2-5-7b-local-smoke-design.md`
- Modify: `docs/superpowers/plans/2026-07-21-ru-qwen2-5-7b-local-smoke-implementation.md`

- [ ] **Step 1: Verify available disk and download one model.**

Run:

```powershell
Get-PSDrive D | Select-Object Name,Free
& .\.venv\Scripts\python.exe scripts\download_ru_qwen_v6_7b_base.py
```

Require at least 20 GiB free before download. Verify the printed `model_id` is
`Qwen/Qwen2.5-7B-Instruct`, `model_type` is `qwen2`, and the reported
`local_path` is `D:\LLM\models\qwen2.5-7b-instruct`. Do not download any
other model in this task.

- [ ] **Step 2: Run the eight-case hardware and conversation smoke.**

Run:

```powershell
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_prompt_routing.py --config configs\ru_qwen_v6_7b.json --output checkpoints\ru-qwen-v6-7b\prompt_routing_smoke.json --max-peak-mib 5500
```

If the command returns `1`, inspect the persisted hardware decision and stop:
do not manually review, offload to RAM, fine-tune, download another model, or
run the 32-case suite.

- [ ] **Step 3: Perform the complete manual review after hardware passes.**

Write exactly eight review objects to
`checkpoints/ru-qwen-v6-7b/prompt_routing_smoke_review.json`, each with
`case_id`, `natural`, `relevant`, `expected_language`, `voice`,
`role_consistent`, and `notes`. Mark English `expected_language` false for any
Vietnamese word or address. Mark mixed `expected_language` false for a
Vietnamese-only or English-only reply. Mark `role_consistent` false if Ru claims
the user's experience as its own or swaps speakers. Then run:

```powershell
& .\.venv\Scripts\python.exe scripts\record_ru_prompt_routing_smoke_review.py --evaluation checkpoints\ru-qwen-v6-7b\prompt_routing_smoke.json --review checkpoints\ru-qwen-v6-7b\prompt_routing_smoke_review.json
```

- [ ] **Step 4: Enforce the decision boundary.**

For a `passed` decision, document that only a new approved plan may run the
full 32-case routed evaluation. For `not_adopted`, document the exact hardware
or conversation failures and stop: no training, adapter, corpus, GUI, full
suite, or fallback-model action. Ask the user whether to retain a failed 7B
model before deleting it.

- [ ] **Step 5: Run full regression, update documentation, and commit.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
rg -n "Qwen2.5 7B|Qwen3|not adopted|passed|hardware" README.md TODO.md docs
git diff --check
```

Update README, TODO, and the spec with only actual disk, model, VRAM, manual
score, verdict, and actions not run. Mark this plan's completed steps. Commit
source and runtime-result documentation separately:

```powershell
git add README.md TODO.md docs\superpowers\specs\2026-07-21-ru-qwen2-5-7b-local-smoke-design.md docs\superpowers\plans\2026-07-21-ru-qwen2-5-7b-local-smoke-implementation.md
git diff --cached --check
git commit -m "docs: record Ru Qwen 7B smoke result"
```
