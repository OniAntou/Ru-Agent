# Ru V7.1 Conversation-Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, train, and evaluate an isolated Ru v7.1 adapter that repairs v7's short English replies, lost mixed-language turn-taking, and `anh`/`em` role drift without changing v6, v7, or the GUI default.

**Architecture:** Keep the existing v7 rows as an immutable base and combine them with a new, explicitly authored 120-row repair source. Build a separate deterministic 300-row corpus, train into a new v7.1 adapter with a lower learning rate, and evaluate the original 16-case gate plus eight repair regressions. Add a physical `nvidia-smi` monitor to the shared Llama trainer behind an opt-in config cap so the v7.1 run can stop before exceeding 6,144 MiB while preserving the existing allocator metric as diagnostic data.

**Tech Stack:** Python 3.11, JSONL, pytest, PyTorch CUDA, Transformers, PEFT/QLoRA, PowerShell, the local Llama 3.1 runtime, and Markdown project documentation.

---

## Scope and protected surfaces

Do not modify `data\conversations\ru_v7`,
`checkpoints\ru-llama3.1-8b-instruct-v7`,
`checkpoints\ru-llama3.1-8b-instruct-v6`, or the GUI's v6 default. Do not
download a new base model. Do not add automatic ingestion of GUI chat data.
Keep adult-only, consent, minors, violence, coercion, and harmful-instruction
boundaries in the repair validator and in the existing persona contract.

The worktree already contains unrelated dirty v6/runtime files. Every commit
must stage only the paths named in that task.

### Task 1: Add isolated v7.1 training configuration and physical-cap seams

**Files:**
- Modify: `D:\LLM\src\mini_gpt\llama3_1_training.py`
- Create: `D:\LLM\configs\ru_llama3_1_8b_v7_1.json`
- Create: `D:\LLM\scripts\train_ru_llama3_1_v7_1.py`
- Create: `D:\LLM\tests\test_llama3_1_v7_1_config.py`
- Create: `D:\LLM\tests\test_llama3_1_training_guards.py`

- [x] **Step 1: Write failing config and output-isolation tests.**

Add tests that load the new JSON and exercise the pure
`validate_v7_1_adapter_dir(path)` guard exported from
`mini_gpt.llama3_1_training`:

```python
def test_v7_1_config_is_separate_and_uses_conservative_training_values():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    assert config["dataset_dir"].endswith("data/conversations/ru_v7_1")
    assert config["adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v7.1")
    assert config["adapter_dir"] not in {
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
    }
    assert config["epochs"] == 2
    assert config["learning_rate"] == 3e-5
    assert config["max_length"] == 384
    assert config["gradient_accumulation_steps"] == 16
    assert config["physical_vram_cap_mib"] == 6144
    assert config["profile_name"] == "ru_llama3_1_v7_1"


@pytest.mark.parametrize("path", [
    "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
    "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
])
def test_v7_1_output_guard_rejects_comparison_adapters(path):
    with pytest.raises(ValueError, match="v7.1 output"):
        validate_v7_1_adapter_dir(path)
```

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_llama3_1_v7_1_config.py D:\LLM\tests\test_llama3_1_training_guards.py -q
```

Historical TDD checkpoint: this initially failed before the v7.1 config, guard,
and physical monitor seam existed; the focused tests now pass.

- [x] **Step 2: Add the v7.1 config and a dedicated training entry point.**

Create `configs\ru_llama3_1_8b_v7_1.json` with these exact values:

```json
{
  "base_model_id": "NousResearch/Meta-Llama-3.1-8B-Instruct",
  "base_model_path": "D:/LLM/models/llama3.1-8b-instruct-hf",
  "dataset_dir": "D:/LLM/data/conversations/ru_v7_1",
  "adapter_dir": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7.1",
  "max_length": 384,
  "seed": 42,
  "epochs": 2,
  "learning_rate": 3e-5,
  "batch_size": 1,
  "gradient_accumulation_steps": 16,
  "evaluation_steps": 4,
  "save_steps": 4,
  "logging_steps": 1,
  "lora_rank": 8,
  "lora_alpha": 16,
  "lora_dropout": 0.05,
  "target_modules": ["q_proj", "k_proj", "v_proj", "o_proj"],
  "backend": "llama3.1-qlora",
  "profile_name": "ru_llama3_1_v7_1",
  "physical_vram_cap_mib": 6144,
  "physical_vram_poll_seconds": 0.5
}
```

Create `scripts\train_ru_llama3_1_v7_1.py` with a parser defaulting to this
config, the existing `--max-steps` override, and an explicit check that the
resolved adapter directory is exactly the v7.1 path and is not either
comparison path. The script calls the existing
`train_ru_adapter_llama(LlamaTrainingConfig.from_json(args.config),
args.max_steps)` function and prints the returned manifest as UTF-8 JSON.

- [x] **Step 3: Add an opt-in physical VRAM monitor and manifest fields.**

Extend `LlamaTrainingConfig` with optional fields
`physical_vram_cap_mib: float | None = None` and
`physical_vram_poll_seconds: float = 0.5`, so existing v6/v7 JSON remains
backward-compatible. Add these seams to the same module:

```python
def read_nvidia_smi_memory_mib() -> float | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.used",
                "--format=csv,noheader,nounits",
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=2,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    values = [float(line.strip()) for line in completed.stdout.splitlines() if line.strip()]
    return values[0] if values else None


class PhysicalVramMonitor:
    def __init__(self, cap_mib: float, poll_seconds: float) -> None:
        self.cap_mib = cap_mib
        self.poll_seconds = poll_seconds
        self._stop = threading.Event()
        self._exceeded = threading.Event()
        self._peak_mib: float | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        self._thread = threading.Thread(target=self._poll, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2)

    def _poll(self) -> None:
        while not self._stop.is_set():
            current = read_nvidia_smi_memory_mib()
            if current is not None:
                self._peak_mib = max(self._peak_mib or current, current)
                if current > self.cap_mib:
                    self._exceeded.set()
            self._stop.wait(self.poll_seconds)

    @property
    def peak_mib(self) -> float | None:
        return self._peak_mib

    @property
    def exceeded(self) -> bool:
        return self._exceeded.is_set()
```

`read_nvidia_smi_memory_mib()` runs
`nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits`, parses the
first numeric value as MiB, and returns `None` on a missing executable,
timeout, or malformed output. The monitor polls in a daemon thread, retains
the largest observed value, sets an event when the cap is exceeded, and always
stops in a `finally` block. The training loop checks that event once per step
and raises `RuntimeError("physical VRAM cap exceeded")` before continuing.

Extend `write_adapter_manifest()` and its call site with
`physical_peak_mib`, `physical_cap_mib`, and `physical_cap_passed`. Keep the
existing `peak_vram_mb` field as the PyTorch allocator metric. When the cap is
unset, the monitor is disabled and the new manifest fields are `None`.

- [x] **Step 4: Test the monitor and run focused tests.**

Use monkeypatched `subprocess.run` and a short poll interval to test numeric
parsing, malformed output, peak retention, and cap detection without CUDA.
Test that a config with no cap leaves the old training API behavior unchanged.
Run the two focused test files again and expect PASS.

- [x] **Step 5: Commit the isolated training seam.**

```powershell
git add -- src/mini_gpt/llama3_1_training.py configs/ru_llama3_1_8b_v7_1.json scripts/train_ru_llama3_1_v7_1.py tests/test_llama3_1_v7_1_config.py tests/test_llama3_1_training_guards.py
git commit -m "feat: add isolated Ru v7.1 training guard"
```

### Task 2: Author and validate the failure-driven repair corpus

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_v7_1_dialogues.py`
- Create: `D:\LLM\src\mini_gpt\ru_v7_1_corpus.py`
- Create: `D:\LLM\scripts\build_ru_llama_v7_1_data.py`
- Create: `D:\LLM\tests\test_ru_v7_1_dialogues.py`
- Create: `D:\LLM\tests\test_ru_v7_1_corpus.py`
- Create: `D:\LLM\tests\test_build_ru_llama_v7_1_data.py`

- [x] **Step 1: Write failing repair-source shape tests.**

Require `v7_1_repair_records()` to return exactly 120 rows with language
counts `vi=24`, `en=48`, `mixed=48`, mode counts
`answer_first=40`, `natural_follow_up=32`, `mixed_code_switch=24`,
`role_voice=12`, `light_flirt_context=12`, no duplicate final prompts, at
least 12 contextual histories, no more than 12 light-flirt rows, and project
provenance `teacher-curated-project-owned` / `project-owned`.

Add explicit regression examples to the test's expected prompt set for the
seven known failures, including `I feel a little tired today.`,
`I just got home.`, `I am having coffee right now.`,
`Ru, hôm nay I feel better.`, `Today was hơi stressful.`,
`Anh đang having coffee với em.`, and `I am home now, but my head is still busy.`

Historical TDD checkpoint: these tests initially failed before the module
existed; the focused source tests now pass.

- [x] **Step 2: Author the 120 repair rows as explicit UTF-8 dialogue records.**

Use the existing `_record()`/`_entry()` pattern, but keep the repair module's
metadata independent of the v7 constants. The first rows must directly repair
the observed failures, for example:

```python
_entry(
    "en", "answer_first", "I feel a little tired today.",
    "Then let yourself slow down for a bit; you have already carried enough today. "
    "Would a quiet break or a little company feel better right now?",
),
_entry(
    "en", "answer_first", "I just got home.",
    "Welcome home. Put down the day for a minute and let your shoulders relax. "
    "Do you want to tell me what kind of day it was?",
),
_entry(
    "en", "answer_first", "I am having coffee right now.",
    "That sounds like a good pause. Is it helping you wake up, or are you enjoying "
    "the quiet more than the caffeine?",
),
_entry(
    "mixed", "mixed_code_switch", "Ru, hôm nay I feel better.",
    "Nghe anh đỡ mệt là em vui rồi. What helped your mood turn around today?",
    flirt_level="light",
),
_entry(
    "mixed", "mixed_code_switch", "Today was hơi stressful.",
    "Nghe một ngày như vậy mệt thật. What part of it is still sitting in your head, anh?",
),
_entry(
    "mixed", "mixed_code_switch", "Anh đang having coffee với em.",
    "Em thích khoảnh khắc này đó. Is the coffee strong enough, or should I keep you company a little longer?",
    flirt_level="light",
),
_entry(
    "mixed", "role_voice", "I am home now, but my head is still busy.",
    "Anh đã về đến nhà rồi thì mình cho đầu óc hạ nhịp một chút nhé. Would you rather talk it out or let em distract anh with something gentle?",
),
```

Fill the remaining rows with concrete, non-repetitive English and mixed
turns. Do not generate rows from mechanical template substitution: each row
must contribute a distinct user fact, answer, or follow-up. Keep all language
and flirt counts exact, keep the repair set mostly ordinary, and include
contextual histories where the assistant responds to the newest user fact.

Use this exact mode-by-language inventory for all 120 explicit rows:

| mode | Vietnamese | English | mixed | total |
| --- | ---: | ---: | ---: | ---: |
| `answer_first` | 10 | 18 | 12 | 40 |
| `natural_follow_up` | 8 | 12 | 12 | 32 |
| `mixed_code_switch` | 0 | 8 | 16 | 24 |
| `role_voice` | 4 | 6 | 2 | 12 |
| `light_flirt_context` | 2 | 4 | 6 | 12 |
| **total** | **24** | **48** | **48** | **120** |

- [x] **Step 3: Implement a fail-closed combined corpus builder.**

`ru_v7_1_corpus.py` must import the unchanged `v7_dialogue_records()` and the
new repair records, normalize both sources, validate roles/provenance/length,
reject the existing v7 safety and self-focus patterns, duplicate final prompts
within either source, and exact duplicate prompt/response pairs across the
combined source, then build exactly 300 combined rows. Permit only the three
known cross-source repair prompt overlaps when their responses differ, and
record the overlap count. Preserve each row's `source` and add manifest fields
`base_source_sha256`, `repair_source_sha256`, and `combined_source_sha256`.
Write `train.jsonl`, `val.jsonl`,
`held_out.jsonl`, and `manifest.json` to `data\conversations\ru_v7_1` with
split counts `228/36/36` and seed `42`.

Use `.partial` files followed by `os.replace`, and remove all partial files on
any exception. The builder must never write to `data\conversations\ru_v7`.

- [x] **Step 4: Add the v7.1 builder CLI and test the corpus.**

`build_ru_llama_v7_1_data.py` must default to
`data\conversations\ru_v7_1`, accept `--seed` and `--audit`, and use a
temporary directory for audit mode. Tests must cover forbidden response
patterns, duplicate prompts, deterministic output for seed 42, exact combined
counts, manifest source hashes, and absence of partial files after a forced
failure.

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_1_dialogues.py D:\LLM\tests\test_ru_v7_1_corpus.py D:\LLM\tests\test_build_ru_llama_v7_1_data.py -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\build_ru_llama_v7_1_data.py --audit
```

Expected audit result: `total_examples=300`, split counts `228/36/36`,
language counts `vi=96`, `en=120`, `mixed=84`, and non-empty source hashes.

- [x] **Step 5: Commit the corpus and builder.**

```powershell
git add -- src/mini_gpt/ru_v7_1_dialogues.py src/mini_gpt/ru_v7_1_corpus.py scripts/build_ru_llama_v7_1_data.py tests/test_ru_v7_1_dialogues.py tests/test_ru_v7_1_corpus.py tests/test_build_ru_llama_v7_1_data.py
git commit -m "feat: author Ru v7.1 repair corpus"
```

### Task 3: Add the 24-case v7.1 evaluator and manual-decision seam

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_v7_1_evaluation.py`
- Create: `D:\LLM\scripts\evaluate_ru_llama3_1_v7_1.py`
- Create: `D:\LLM\scripts\review_ru_llama3_1_v7_1.py`
- Create: `D:\LLM\tests\test_ru_v7_1_evaluation.py`
- Create: `D:\LLM\tests\test_evaluate_ru_llama3_1_v7_1.py`

- [x] **Step 1: Define the eight repair cases and gate tests.**

Reuse `ConversationCase` and `assess_conversation_focus_response` from
`mini_gpt.ru_qwen_evaluation`, but keep the eight new cases in the new module.
The suite must contain two additional English continuations, four mixed
code-switch cases, and two contextual latest-fact cases. Every case ends in a
user message and has `focus_expected=True`; mixed cases also require a
follow-up question.

Add `record_v7_1_decision()` that accepts the 24 automatic assessments, 24
manual review records, physical peak, and cap. It returns `passed` only when
all automatic cases pass, all reviews are present exactly once, at least 14 of
the original 16 and at least 21 of 24 reviews satisfy
`natural and relevant and expected_language and voice and role_consistent`,
no `severe_failure` flag is true, and physical peak is at most 6,144 MiB.
Without manual reviews it must return `not_reviewed` rather than `passed`.

- [x] **Step 2: Run gate tests before implementing the seam.**

Test full 24-case coverage, duplicate/missing manual IDs, failure when an
automatic assessment fails, the original-16 threshold, the overall-21/24
threshold, severe-failure rejection, physical-cap rejection, and the
`not_reviewed` result when reviews are absent. Historical TDD checkpoint: the
tests initially failed before the module existed; the evaluator tests now pass.

- [x] **Step 3: Implement the evaluator CLI.**

`evaluate_ru_llama3_1_v7_1.py` must default to the local Llama base, adapter
`checkpoints\ru-llama3.1-8b-instruct-v7.1`, and output
`checkpoints\ru-llama3.1-8b-instruct-v7.1\conversation_focus_v7_1.json`.
Run the 16 existing cases followed by the eight repair cases with
`do_sample=False` and `max_new_tokens=64`. Store case IDs, language, prompt,
all messages, response, failures, and the PyTorch allocator peak. Sample the
physical device peak with the same `nvidia-smi` parser used by training, and
write `verdict=not_reviewed` until the review command is run. Return exit code
1 for any automatic failure.

- [x] **Step 4: Implement the manual-review command.**

`review_ru_llama3_1_v7_1.py` accepts an automatic evaluation JSON and a
review JSON containing exactly these fields per case:
`case_id`, `natural`, `relevant`, `expected_language`, `voice`,
`role_consistent`, `severe_failure`, and `notes`. It validates every case ID,
calls `record_v7_1_decision()`, and writes
`manual_review_v7_1.json` next to the evaluation artifact using a partial file
and atomic replace. The agent must inspect every saved response before
authoring this review; no score may be inferred from the automatic check.

- [x] **Step 5: Run evaluator tests and commit the evaluation seam.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_1_evaluation.py D:\LLM\tests\test_evaluate_ru_llama3_1_v7_1.py -q
git add -- src/mini_gpt/ru_v7_1_evaluation.py scripts/evaluate_ru_llama3_1_v7_1.py scripts/review_ru_llama3_1_v7_1.py tests/test_ru_v7_1_evaluation.py tests/test_evaluate_ru_llama3_1_v7_1.py
git commit -m "feat: add Ru v7.1 conversation repair gate"
```

### Task 4: Build, train, and evaluate v7.1 locally

**Files/artifacts:**
- Create ignored: `D:\LLM\data\conversations\ru_v7_1\*`
- Create ignored: `D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\*`
- Create ignored: `D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\conversation_focus_v7_1.json`

- [x] **Step 1: Rebuild the deterministic v7.1 corpus.**

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\build_ru_llama_v7_1_data.py --output-dir D:\LLM\data\conversations\ru_v7_1 --seed 42
Get-Content D:\LLM\data\conversations\ru_v7_1\manifest.json | ConvertFrom-Json | Select-Object total_examples,split_counts,language_counts,base_source_sha256,repair_source_sha256,combined_source_sha256
```

Confirm `300` rows, `228/36/36` splits, and that the v7 manifest hash and
files are byte-for-byte unchanged after the build.

Observed: `300` rows with `228/36/36` splits, language counts `vi=96`,
`en=120`, `mixed=84`, and source hashes base
`9778d25b4a45c90bcee11ceed7364c76e7489292a03a417af9efdc53fe206da5`, repair
`27a785ab956213d5372bee4edfa2f35d5081b307fa700d77a6abfdeb8a766bcc`, and
combined `b4eb7bca4841ed3bdfcab56f25ce9126869d0e7a28749f734855fafbeeb73905`.
The v7 source remained unchanged.

- [x] **Step 2: Verify the machine and release the GPU before training.**

Close the temporary v7 GUI process used for manual testing, then run:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" | Select-Object ProcessId,CommandLine
nvidia-smi --query-gpu=index,name,memory.used,memory.total --format=csv,noheader,nounits
```

Do not start training while another Llama process owns the GPU. Stop and
report instead of changing quantization or downloading another model if the
base path is missing or free VRAM is insufficient.

Observed: the local base was present, the RTX 4050 Laptop GPU reported
`6,141 MiB` total, and no competing Llama process owned the GPU before the
run.

- [x] **Step 3: Run the full v7.1 training command.**

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\train_ru_llama3_1_v7_1.py --config D:\LLM\configs\ru_llama3_1_8b_v7_1.json
```

Capture the JSON manifest. Confirm the output contains
`adapter_config.json`, `adapter_model.safetensors`, `ru_manifest.json`, the
v7.1 profile name, the combined dataset manifest hash, optimizer update count,
the allocator peak, the physical peak, `physical_cap_mib=6144`, and
`physical_cap_passed=true`. A CUDA OOM or physical peak above the cap stops the
run and leaves v6/v7 untouched.

Observed manifest: 2 epochs, 29 optimizer updates, best validation loss
`2.5795224606990814`, dataset-manifest hash
`b9262441633f492007cd16ed30479bf174dcc656a7109437f4f103f52d2c8582`,
allocator peak `8,446.57958984375 MiB` (diagnostic), physical peak `5,874 MiB`,
physical cap `6,144 MiB`, and `physical_cap_passed=true`.

- [x] **Step 4: Run the deterministic 24-case evaluator.**

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\evaluate_ru_llama3_1_v7_1.py --base-model D:\LLM\models\llama3.1-8b-instruct-hf --adapter-dir D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1 --output D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\conversation_focus_v7_1.json
```

Record all automatic failures and both VRAM metrics. Do not run manual review
if the evaluator failed to save all 24 replies; do not change the GUI default
based on this result.

Observed: all 24 replies were saved. Automatic evaluation passed `10/24`,
with physical allocator peak `5,632.331 MiB`, physical peak `5,850 MiB`, and
the physical cap passing. The automatic failure IDs are recorded in the
evaluation and manual-review artifacts; the dominant categories are
generic-short English, mixed-language collapse, and missing follow-ups.

- [x] **Step 5: Manually review every saved reply and record the decision.**

Read all 24 responses from the saved evaluation artifact. Create a review JSON
with a short evidence-based note for each case, then run:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\review_ru_llama3_1_v7_1.py --evaluation D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\conversation_focus_v7_1.json --reviews D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\manual_inputs_v7_1.json --output D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\manual_review_v7_1.json
```

The resulting verdict is `passed` only when every spec threshold is met.
Otherwise record `not_adopted`, keep v6 as the GUI default, and document the
exact remaining failure categories.

Observed: manual review scored `14/24` overall and `10/16` on the original v7
slice. The final verdict is `not_adopted` because of automatic disqualifiers,
the overall manual threshold, and the original-v7 manual threshold. The GUI
default remains v6.

### Task 5: Update evidence-backed documents and finish verification

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\TODO.md`
- Modify: `D:\LLM\docs\ANTIGRAVITY_HANDOFF.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-22-ru-v7-1-conversation-repair-implementation.md`
- Create ignored: `D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7.1\manual_review_v7_1.json`

- [x] **Step 1: Record only measured v7.1 evidence.**

Add the exact corpus counts/hash, training loss and updates, automatic/manual
counts, allocator and physical VRAM measurements, and final verdict. If the
gate fails, explicitly state `not_adopted` and that the GUI remains on v6. If
the gate passes, document the separate promotion decision but do not change
the GUI in this plan unless the evaluation artifact proves every gate.

Recorded in `README.md`, `TODO.md`, and `docs/ANTIGRAVITY_HANDOFF.md`; no GUI
promotion was made.

- [x] **Step 2: Check documentation hygiene.**

Search all project Markdown for stale claims that v7.1 is adopted, stale
paths, or commands that overwrite v7. Run:

```powershell
rg -n "v7\.1|ru_v7_1|ru-llama3\.1-8b-instruct-v7\.1|GUI default|not_adopted" D:\LLM\README.md D:\LLM\TODO.md D:\LLM\docs
```

Retain historical v7 documents, but mark their measured result as v7 and
`not_adopted`; remove no user-authored document solely because it is old.

Observed: the historical v7 result remains labeled separately, and the new
v7.1 paths, failure state, and v6 GUI default are documented without changing
older experiment evidence.

- [x] **Step 3: Run the complete verification set.**

```powershell
Set-Location D:\LLM
& .\.venv\Scripts\python.exe -m pytest -q
& .\.venv\Scripts\python.exe -m compileall -q src scripts tests
git diff --check
git status --short
```

Expected: all tests pass, compileall exits 0, `git diff --check` emits no
errors, v6/v7 files remain unchanged, and no unrelated dirty files are
staged. Commit only the v7.1 source/tests/config/docs that belong to this
implementation.

- [x] **Step 4: Commit the evidence handoff.**

```powershell
git add -- README.md TODO.md docs/ANTIGRAVITY_HANDOFF.md docs/superpowers/plans/2026-07-22-ru-v7-1-conversation-repair-implementation.md
git commit -m "docs: record Ru v7.1 evaluation handoff"
```

Do not report v7.1 as better than v6 unless the saved artifact satisfies every
automatic, manual, persona, and physical-cap condition. If it fails, leave a
concise next-iteration handoff with exact failure IDs and the next permitted
repair direction.
