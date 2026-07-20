# Mini-GPT GUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a simple local Tkinter interface for generating text from a selected mini-GPT checkpoint.

**Architecture:** Keep inference in a small `ModelRunner` that loads a checkpoint lazily and uses the existing tokenizer/model/generation functions. The Tkinter window owns only controls and display; generation runs in a background thread and returns to the UI through Tk's `root.after` callback so the window remains responsive.

**Tech Stack:** Python 3.11, Tkinter, existing PyTorch and tokenizer code, and `threading` from the standard library.

---

### Task 1: Implement runner and GUI

**Files:**
- Create: `D:\LLM\src\mini_gpt\gui.py`
- Modify: `D:\LLM\README.md`

- [x] Implement `ModelRunner.load(checkpoint_path, tokenizer_path, device)` with explicit CUDA validation, checkpoint configuration loading, and parameter-count status.
- [x] Implement `ModelRunner.generate(prompt, max_new_tokens, temperature, top_k)` using the existing `generate` function and cached model.
- [x] Build a Tkinter window with checkpoint/tokenizer fields, Browse button, prompt text box, max-token/temperature/top-k controls, Generate/Clear buttons, status label, and output text box.
- [x] Run generation on a worker thread, disable Generate while busy, and display errors in the status area without crashing the window.
- [x] Add the command `python -m mini_gpt.gui` to README.

### Task 2: Test and verify

**Files:**
- Create: `D:\LLM\tests\test_gui.py`

- [x] Test checkpoint-path validation and parameter parsing without creating a Tk window.
- [x] Run compileall and the full pytest suite.
- [x] Launch the GUI process briefly, verify it imports and creates a window, then close the task-owned process.
- [x] Inspect README and project documents for stale paths, run `git diff --check`, and commit the feature.

## Self-review

- Tkinter adds no third-party dependency and stays within the requested simple local GUI scope.
- The GUI does not start long training jobs; it only performs inference against a selected checkpoint.
- Background generation prevents the interface from freezing during GPU inference.
