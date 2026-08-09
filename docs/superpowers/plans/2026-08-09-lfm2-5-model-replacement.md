# LFM2.5-2.6B Model Replacement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the active local Llama 3.1 GGUF runtime with LiquidAI LFM2.5-2.6B while keeping the native Hugging Face checkpoint available for later fine-tuning.

**Architecture:** Store the native checkpoint at `models/lfm2.5-2.6b-hf` and the selected Q4_K_M GGUF at `models/gguf/LFM2.5-2.6B-Q4_K_M.gguf`. The llama.cpp runtime will own one direct LFM2.5 GGUF and will no longer auto-load incompatible Llama 3.1 LoRA files. Existing Llama 3.1 adapter checkpoints remain historical training/evaluation artifacts unless explicitly removed later.

**Tech Stack:** Hugging Face Hub CLI, Transformers 5.x, llama.cpp server 10142, Python/FastAPI runtime, WinUI 3, pytest.

---

### Task 1: Define the new direct-GGUF runtime contract

**Files:**
- Modify: `src/mini_gpt/llama_cpp_runtime.py`
- Modify: `src/mini_gpt/headless_runner.py`
- Test: `tests/test_llama_cpp_runtime.py`
- Test: `tests/test_headless_runner.py`

- [x] **Step 1: Write failing tests**

  Assert that `LlamaCppRuntime.base_model` resolves to `models/gguf/LFM2.5-2.6B-Q4_K_M.gguf`, `server_command()` has no legacy `--lora`, disables model reasoning for chat, and validation only requires the executable plus the new GGUF. Assert that ownership checks use the protected chat endpoint and that `ModelRunner.load("")` selects the new GGUF and reports `LFM2.5-2.6B Q4_K_M`.

- [x] **Step 2: Run the focused tests and observe the expected failure**

  Run `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_llama_cpp_runtime.py tests/test_headless_runner.py -q`. The new contract tests must fail against the current Llama 3.1/LoRA implementation.

- [x] **Step 3: Implement the smallest runtime change**

  Change the runtime model property to the LFM2.5 GGUF, remove legacy LoRA discovery/insertion, validate only the server executable and base GGUF, disable the LFM2.5 reasoning output used by the chat path, and make `ModelRunner.load()` default the local checkpoint metadata to the new GGUF when no path is persisted. Protect process ownership with the authenticated chat endpoint and retry transient `503 Loading model` responses. Return a non-LoRA LFM2.5 status string.

- [x] **Step 4: Re-run focused tests**

  Run the same pytest command and require zero failures.

### Task 2: Download and verify both requested model variants

**Files:**
- Create: `models/lfm2.5-2.6b-hf/` (downloaded native checkpoint)
- Create: `models/gguf/LFM2.5-2.6B-Q4_K_M.gguf` (downloaded GGUF)

- [x] **Step 1: Download the native checkpoint**

  Run `D:\LLM\.venv\Scripts\hf.exe download LiquidAI/LFM2.5-2.6B --local-dir D:\LLM\models\lfm2.5-2.6b-hf`.

- [x] **Step 2: Download the selected GGUF**

  The Hub CLI stalled on the Xet transfer; the same public Hugging Face resolve URL was completed with resumable `curl` streaming into a temporary file, then promoted after exact-size and SHA-256 verification.

- [x] **Step 3: Verify files and model metadata**

  Verify the native `config.json` reports `model_type=lfm2`, the two safetensors shards and index exist, the tokenizer files exist, and the GGUF is non-empty. Load the native config/tokenizer with the project virtual environment and inspect the GGUF header with `llama-server.exe`.

### Task 3: Remove only obsolete active Llama model artefacts

**Files:**
- Delete: `models/llama3.1-8b-instruct-hf/`
- Delete: `models/llama3.1-8b-instruct-exl2/`
- Delete: `models/gguf/llama3.1-8b-instruct-q4_k_m.gguf`
- Delete: `models/gguf/ru-llama3.1-8b-instruct-v6-lora.gguf`
- Delete: `models/gguf/ru-llama3.1-8b-instruct-v6.1-lora.gguf`
- Delete: `models/gguf/ru-llama3.1-8b-instruct-v6.2-lora.gguf`

- [x] **Step 1: Reconfirm exact targets and new-model verification**

  List the six exact old targets, confirm they are inside `D:\LLM`, and confirm both new model variants are present before removal.

- [x] **Step 2: Remove the obsolete active model files**

  Use explicit `Remove-Item -LiteralPath` calls for only those six targets. Do not remove `models/omnivoice`, `models/vision`, datasets, global Hugging Face caches, or historical adapter checkpoint directories.

- [x] **Step 3: Verify deletion and remaining model inventory**

  Confirm the six old targets are absent and the new native/GGUF files remain intact.

### Task 4: Update current-facing configuration and documentation

**Files:**
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `pyproject.toml`
- Modify: `README.md`
- Modify: `TODO.md`

- [x] **Step 1: Update user-facing model names and paths**

  Replace current active-runtime wording that claims Llama 3.1/v6.2 LoRA is the default with LFM2.5-2.6B Q4_K_M, and document the native checkpoint path for future fine-tuning. Keep historical quality-gate results explicitly historical.

- [x] **Step 2: Review all project documents for stale active-model claims**

  Search `README.md`, `TODO.md`, and current-facing GUI text for the old default. Do not rewrite dated design/spec records that describe completed historical Llama experiments; mark any remaining ambiguity as historical if needed.

- [x] **Step 3: Run document/config sanity checks**

  Confirm JSON/TOML remain parseable and verify no current-facing default still points to the deleted active GGUF.

### Task 5: Run full proportionate verification and hand off

**Files:**
- Test: `tests/test_llama_cpp_runtime.py`
- Test: `tests/test_headless_runner.py`
- Test: relevant model/config smoke commands

- [x] **Step 1: Run focused Python tests**

  Run the runtime/runner tests after the new files are present.

- [x] **Step 2: Run a real llama.cpp smoke request**

  Start the owned new GGUF on a temporary loopback port, wait through transient model-loading responses, send one bounded chat request, inspect the response, then stop the owned process and verify the port is released.

- [x] **Step 3: Run the broader relevant test suite**

  Run the server/runtime tests sequentially to avoid the known 6 GiB GPU/pagefile pressure. Report environment/import failures separately from code failures.

- [x] **Step 4: Inspect git diff and final model inventory**

  Confirm only scoped source/docs/tests plus the ignored model files changed, no unrelated dirty work was overwritten, and report exact paths, sizes, verification evidence, and the preserved historical checkpoints.
