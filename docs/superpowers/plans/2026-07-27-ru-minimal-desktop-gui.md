# Ru Minimal Desktop GUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Replace the settings-heavy Ru Tkinter screen with a calm conversation-first desktop UI and make local response latency visible and bounded without weakening the Llama/Gwen 6 GiB VRAM safety lifecycle.

**Architecture:** Keep \`RuApp\` as the Tkinter controller and retain its worker threads, conversation store, memory/search opt-ins, and Gwen endpoint. Extract testable presentation/state helpers from \`gui.py\`; the view uses ttk styles and tagged \`tk.Text\` turns, while a modal settings window owns infrequently changed model and privacy controls. Add a small generation timing result and a shared GPU-transition lock so text inference and voice synthesis cannot compete for the GPU.

**Tech Stack:** Python 3.13, Tkinter/ttk, pygame, SpeechRecognition, PyTorch CUDA, local Llama 3.1 QLoRA, local Gwen TTS, pytest.

**Implementation status (2026-07-27):** The conversation-first layout,
settings dialog, keyboard submit, 128-token default, cache-aware status, and
Llama/Gwen GPU transition lock are implemented. The focused GUI/runtime suite
passes with 23 tests. The full project suite currently has 79 passing tests and
two pre-existing Gwen service collection failures because the project `.venv`
does not install Flask; Flask is isolated in `gwen_env`, where the service runs.
This implementation does not mask or bypass those failures.

**Stability follow-up (2026-07-27):** Windows Event Log captured an
`APPCRASH` in `torch_cpu.dll` (`0xc0000005`) from the GUI process while
`torch.compile` was enabled. The runtime now stays eager and a short real v6
text-only generation completed without that native crash. The transcript also
renders each completed turn in chronological order: user message, then Ru
reply.

**Latency follow-up (2026-07-27):** The default is now 128 tokens to keep
ordinary dialogue and its speech short. FlashAttention was evaluated in
`gwen_env` (Python 3.11, Torch 2.7.1+cu128, RTX 4050) but no compatible Windows
wheel exists and the machine has no CUDA Toolkit (`nvcc`) for a source build.
Gwen therefore remains on the supported SDPA path; no dependency was forced
into the voice environment.

---

## File map

- \`src/mini_gpt/gui_presentation.py\`: palette, display labels, elapsed-time formatting, and transcript tag constants with no Tk root or model imports.
- \`src/mini_gpt/gui.py\`: Tkinter layout, settings dialog, keyboard handling, phase/status updates, guarded GPU worker transitions, and use of the presentation helpers.
- \`tests/test_gui_presentation.py\`: pure formatting and phase-label coverage.
- \`tests/test_gui.py\`: keyboard submit, generation timing/state, and existing provider/model checks without loading model weights or opening a window.
- \`README.md\`: concise user-facing description of the conversation-first UI, settings location, and why voice can still impose a model reload.

### Task 1: Define and test presentation/state helpers

**Files:**

- Create: \`src/mini_gpt/gui_presentation.py\`
- Create: \`tests/test_gui_presentation.py\`

- [ ] **Step 1: Write failing pure-helper tests**

~~~python
from mini_gpt.gui_presentation import format_elapsed, format_generation_status


def test_format_elapsed_is_compact_and_stable():
    assert format_elapsed(0.04) == "under 0.1 s"
    assert format_elapsed(1.24) == "1.2 s"
    assert format_elapsed(61.0) == "1 min 1 s"


def test_generation_status_describes_cache_and_elapsed_time():
    assert format_generation_status(cache_hit=True, seconds=2.6) == "Ru replied in 2.6 s"
    assert format_generation_status(cache_hit=False, seconds=12.2) == "Ru replied in 12.2 s (model loaded)"
~~~

- [ ] **Step 2: Run the new tests to prove they fail**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui_presentation.py -q\`

Expected: FAIL because \`mini_gpt.gui_presentation\` does not exist.

- [ ] **Step 3: Add the minimal, Tk-independent presentation module**

~~~python
from __future__ import annotations

PALETTE = {
    "background": "#FAF8FC",
    "surface": "#FFFFFF",
    "ink": "#2E2933",
    "muted": "#6E6575",
    "accent": "#765E98",
    "ru_bubble": "#F0E9F8",
    "user_bubble": "#E9F1F0",
}


def format_elapsed(seconds: float) -> str:
    if seconds < 0.1:
        return "under 0.1 s"
    if seconds < 60:
        return f"{seconds:.1f} s"
    whole_seconds = int(round(seconds))
    return f"{whole_seconds // 60} min {whole_seconds % 60} s"


def format_generation_status(*, cache_hit: bool, seconds: float) -> str:
    suffix = "" if cache_hit else " (model loaded)"
    return f"Ru replied in {format_elapsed(seconds)}{suffix}"
~~~

- [ ] **Step 4: Run helper tests**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui_presentation.py -q\`

Expected: \`2 passed\`.

- [ ] **Step 5: Commit the isolated presentation contract**

~~~powershell
git add src/mini_gpt/gui_presentation.py tests/test_gui_presentation.py
git commit -m "feat: add Ru GUI presentation helpers"
~~~

### Task 2: Build the conversation-first layout and settings dialog

**Files:**

- Modify: \`src/mini_gpt/gui.py:1-290\`
- Modify: \`tests/test_gui.py\`

- [ ] **Step 1: Add focused GUI-controller tests without creating \`tk.Tk\`**

~~~python
def test_submit_from_keyboard_starts_generation_and_consumes_event():
    app = object.__new__(gui.RuApp)
    calls = []
    app._start_generation = lambda: calls.append("generate")
    assert app._submit_from_keyboard(object()) == "break"
    assert calls == ["generate"]


def test_default_generation_limit_is_short_conversation_length():
    assert gui.DEFAULT_MAX_NEW_TOKENS == "128"
~~~

- [ ] **Step 2: Run those tests to prove they fail**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py -q\`

Expected: FAIL because \`_submit_from_keyboard\` and \`DEFAULT_MAX_NEW_TOKENS\` do not yet exist.

- [ ] **Step 3: Introduce visual constants and replace the top-level layout**

~~~python
DEFAULT_MAX_NEW_TOKENS = "128"


def _submit_from_keyboard(self, _event: object) -> str:
    self._start_generation()
    return "break"
~~~

Configure the root with \`PALETTE["background"]\`, Segoe UI ttk fonts, and a single \`header\`, \`conversation\`, \`composer\`, \`secondary_actions\`, and \`status\` grid. The header contains \`Ru\`, \`Local Llama\`, the status variable, and a \`Settings\` button. Bind \`<Control-Return>\` on \`prompt_text\` to \`_submit_from_keyboard\`; do not bind bare Return. Replace the broken emoji labels with the literal labels \`Voice input\` and \`Listening...\`.

Use \`tk.Text\` tags named \`ru_label\`, \`ru_bubble\`, \`user_label\`, and \`user_bubble\` in \`_append_turn\`. Each turn inserts a left-aligned \`Ru\` label and a soft Ru background, then a right-aligned \`You\` label and user background; the disabled transcript remains scrollable and is set back to \`state="disabled"\` after insertion. \`_new_chat\` restores a single empty-state message instead of silently leaving a blank document.

- [ ] **Step 4: Move settings into one modal dialog**

~~~python
def _open_settings(self) -> None:
    dialog = tk.Toplevel(self.root)
    dialog.title("Ru settings")
    dialog.transient(self.root)
    dialog.resizable(False, False)
    dialog.grab_set()
    # Reuse checkpoint, sampler, memory, and search StringVar/BooleanVar
    # instances. Keep browse and clear-memory commands unchanged.
~~~

The dialog is the only place that renders adapter selection, sampler values, memory/search opt-ins, and \`Clear saved memory\`. Retain all existing paths, values, parsing, and the clear-memory confirmation; no setting is deleted.

- [ ] **Step 5: Run GUI and helper tests**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py tests\\test_gui_presentation.py -q\`

Expected: all GUI tests pass without CUDA, a microphone, or a Tk display.

- [ ] **Step 6: Commit the presentation change**

~~~powershell
git add src/mini_gpt/gui.py tests/test_gui.py
git commit -m "feat: redesign Ru conversation screen"
~~~

### Task 3: Serialize GPU transitions and report actual timing

**Files:**

- Modify: \`src/mini_gpt/gui.py:1-565\`
- Modify: \`src/mini_gpt/llama3_1_runtime.py:11-90\`
- Modify: \`tests/test_gui.py\`
- Modify: \`tests/test_llama3_1_runtime.py\`

- [ ] **Step 1: Write tests for cache-aware timing and cache querying**

~~~python
def test_generation_status_describes_a_warm_response():
    assert gui.format_generation_status(cache_hit=True, seconds=1.2) == "Ru replied in 1.2 s"


def test_cached_query_uses_the_same_resolved_adapter_key(monkeypatch, tmp_path):
    adapter = tmp_path / "adapter"
    adapter.mkdir()
    key = ("base", str(adapter.resolve()))
    monkeypatch.setattr(runtime, "_GENERATION_CACHE", {key: (object(), object())})
    assert runtime.is_llama_generation_cached("base", adapter)
~~~

- [ ] **Step 2: Run tests to prove the current cache-query contract fails**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py tests\\test_llama3_1_runtime.py -q\`

Expected: FAIL because \`is_llama_generation_cached\` does not yet exist.

- [ ] **Step 3: Add cache-aware elapsed measurement around production calls**

~~~python
from time import perf_counter

self.gpu_transition_lock = threading.Lock()

def _generate_worker(...):
    self.root.after(0, lambda: self.status_var.set("Ru is replying..."))
    with self.gpu_transition_lock:
        started = perf_counter()
        cache_hit = self.runner.is_cached(checkpoint)
        status = self.runner.load(checkpoint)
        result = self.runner.generate_reply(...)
        elapsed = perf_counter() - started
    self.root.after(0, lambda: self._generation_finished(
        result, cache_hit, elapsed, prompt, remember_conversations
    ))
~~~

Add \`ModelRunner.is_cached(checkpoint)\` as a non-loading wrapper around public \`is_llama_generation_cached(base_path, adapter_dir)\` in \`llama3_1_runtime.py\`. The runtime helper uses the same resolved adapter key currently used by generation and never allocates CUDA memory. Keep \`load()\` as dependency/path validation only. Do not pre-load model weights from the UI.

\`_generation_finished\` uses \`format_generation_status(cache_hit=..., seconds=...)\` after adding a turn. The optional speech worker may supersede this with \`Preparing voice...\`, as it does today.

- [ ] **Step 4: Guard the Gwen transition with the same lock**

~~~python
def _play_tts(self, text: str) -> None:
    clean_text = clean_tts_text(text)
    if not clean_text:
        return
    self.root.after(0, lambda: self.status_var.set("Preparing voice..."))
    with self.gpu_transition_lock:
        clear_llama_generation_cache()
        audio_file = request_tts_audio(clean_text)
    self.root.after(0, lambda: self.status_var.set("Ru is speaking"))
    # Retain existing pygame playback and temporary WAV cleanup.
~~~

The lock prevents Llama loading/generation and Gwen synthesis from sharing the 6 GiB GPU simultaneously. It does not cover WAV playback, so typing and later Llama work are not blocked by the audio duration.

- [ ] **Step 5: Run focused tests**

Run: \`& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py tests\\test_llama3_1_runtime.py -q\`

Expected: all pass; additions verify cache-query keying and deterministic status formatting without model loading.

- [ ] **Step 6: Commit timing and GPU serialization**

~~~powershell
git add src/mini_gpt/gui.py src/mini_gpt/llama3_1_runtime.py tests/test_gui.py tests/test_llama3_1_runtime.py
git commit -m "feat: report Ru response timing safely"
~~~

### Task 4: Verify the desktop experience and update active documentation

**Files:**

- Modify: \`README.md:24-42\`
- Review: \`docs/superpowers/specs/2026-07-27-ru-minimal-desktop-gui-design.md\`

- [ ] **Step 1: Update only the active GUI section**

~~~markdown
The default screen is conversation-first. Open **Settings** to change the adapter, sampler, saved-memory option, or online-search permission. Send with the button or `Ctrl+Enter`; Enter adds a line. The status line reports whether the Llama model was already resident and how long the completed local reply took.
~~~

Keep the existing Gwen VRAM explanation, local privacy language, and v6 fallback policy unchanged.

- [ ] **Step 2: Run the full automated suite and documentation consistency checks**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q`

Run: `rg -n "Model Settings|Max tokens|Temperature|Top-k|Remember conversations|Allow online search" README.md docs src\mini_gpt\gui.py`

Expected: tests pass; only the settings dialog/source documentation contains the old technical-control terms.

- [ ] **Step 3: Manually smoke-check without loading a model**

Run: `& D:\LLM\.venv\Scripts\python.exe -c "import tkinter as tk; from mini_gpt.gui import RuApp; root=tk.Tk(); app=RuApp(root); root.update_idletasks(); root.destroy()"`

Expected: exit code 0. This verifies layout construction only; it must not load Llama, use microphone input, call Gwen, or start web search.

- [ ] **Step 4: Inspect change scope and commit**

Run: `git diff --check; git status --short`

Expected: no whitespace errors; no unrelated pre-existing worktree changes is staged.

~~~powershell
git add README.md src/mini_gpt/gui.py src/mini_gpt/gui_presentation.py src/mini_gpt/llama3_1_runtime.py tests/test_gui.py tests/test_gui_presentation.py tests/test_llama3_1_runtime.py
git commit -m "docs: describe Ru desktop chat controls"
~~~

## Self-review

- Spec coverage: Task 2 covers the calm chat-first screen, hidden technical controls, secondary actions, keyboard action, accessible labels, and future replaceable stage. Task 3 covers measured status, worker-thread updates, and safe Llama/Gwen GPU turnover. Task 4 preserves and refreshes user documentation.
- Placeholder scan: every task names source files, tests, commands, and an expected result. The only runtime-specific action is a non-inference Tkinter construction check.
- Type consistency: `format_generation_status(cache_hit, seconds)`, `ModelRunner.is_cached(checkpoint)`, and `_generation_finished(result, cache_hit, elapsed, prompt, remember_conversations)` are introduced once and used consistently.
