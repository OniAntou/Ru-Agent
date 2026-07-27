# Ru v6 GGUF Runtime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use inline execution task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Add a reproducible llama.cpp CUDA GGUF experiment for Ru v6 while preserving the current Transformers GUI backend as the default fallback.

**Architecture:** Convert the local Llama base to one Q4_K_M GGUF and the v6 PEFT adapter to a separate LoRA GGUF. A focused Python runtime owns a local llama-server.exe process and its OpenAI-compatible chat request; ModelRunner chooses that runtime only when the user explicitly selects the experimental backend. Tests use fake processes and HTTP transport only.

**Tech Stack:** Python 3.13 standard library, Tkinter, pytest, local llama.cpp Windows CUDA binary, local Llama 3.1 8B HF base, PEFT v6 adapter.

**Implementation status (2026-07-27):** Completed. The verified local build uses llama.cpp `b10142`, Q4_K_M base plus a separate Ru v6 LoRA GGUF. Automated tests and a two-prompt local benchmark passed; GGUF became the GUI default after the user's manual Vietnamese conversation-quality approval. Transformers remains a selectable fallback and its HF base remains required for training.

---

## File map

- scripts/build_ru_v6_gguf.py: validates disk and paths, then converts the HF base and v6 adapter with local llama.cpp tools.
- src/mini_gpt/llama_cpp_runtime.py: launches/stops the local server and posts OpenAI-compatible chat completion requests.
- src/mini_gpt/gui.py: exposes a deliberately opt-in GGUF backend and releases whichever local backend owns the GPU before Gwen speech.
- tests/test_build_ru_v6_gguf.py: validates conversion commands and the disk guard without invoking a converter.
- tests/test_llama_cpp_runtime.py: verifies command construction, health/error handling, and chat-response parsing using fakes.
- tests/test_gui.py: verifies backend selection keeps Transformers as the default and routes through GGUF only when selected.
- README.md: documents installation, conversion, experimental GUI selection, and the quality gate.

### Task 1: Add conversion guard and commands

**Files:**

- Create: scripts/build_ru_v6_gguf.py
- Create: tests/test_build_ru_v6_gguf.py

- [ ] **Step 1: Write failing conversion-path tests.**

~~~python
import pytest

from build_ru_v6_gguf import build_commands, ensure_free_space


def test_build_commands_keep_base_and_adapter_separate(tmp_path):
    commands = build_commands(tmp_path, "python.exe")
    text = "\n".join(" ".join(command) for command in commands)
    assert "convert_hf_to_gguf.py" in text
    assert "convert_lora_to_gguf.py" in text
    assert "llama-quantize.exe" in text
    assert "--lora" not in text


def test_ensure_free_space_rejects_a_small_volume(tmp_path):
    with pytest.raises(RuntimeError, match="24 GiB"):
        ensure_free_space(tmp_path, free_bytes=1)
~~~

- [ ] **Step 2: Run test to verify it fails.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_build_ru_v6_gguf.py -q

Expected: FAIL because build_ru_v6_gguf does not exist.

- [ ] **Step 3: Implement a no-merge conversion command builder.**

~~~python
MIN_FREE_BYTES = 24 * 1024**3


def ensure_free_space(root: Path, *, free_bytes: int | None = None) -> None:
    available = free_bytes if free_bytes is not None else shutil.disk_usage(root).free
    if available < MIN_FREE_BYTES:
        raise RuntimeError(
            f"GGUF conversion needs at least 24 GiB free; found {available / 1024**3:.2f} GiB"
        )


def build_commands(root: Path, python_executable: str) -> tuple[tuple[str, ...], ...]:
    source = root / "tools" / "llama.cpp"
    base = root / "models" / "llama3.1-8b-instruct-hf"
    adapter = root / "checkpoints" / "ru-llama3.1-8b-instruct-v6"
    temporary = root / ".tmp" / "ru-v6-gguf-base-f16.gguf"
    base_output = root / "models" / "gguf" / "llama3.1-8b-instruct-q4_k_m.gguf"
    adapter_output = root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6-lora.gguf"
    return (
        (python_executable, str(source / "convert_hf_to_gguf.py"), str(base), "--outtype", "f16", "--outfile", str(temporary)),
        (str(source / "llama-quantize.exe"), str(temporary), str(base_output), "Q4_K_M"),
        (python_executable, str(source / "convert_lora_to_gguf.py"), "--base", str(base), "--outtype", "f16", "--outfile", str(adapter_output), str(adapter)),
    )
~~~

The CLI validates the source checkout, converter binaries, base, adapter, and 24 GiB free space before creating .tmp. It runs commands sequentially, verifies non-empty base and adapter outputs, then removes only the intermediate base F16 GGUF.

- [ ] **Step 4: Run focused tests.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_build_ru_v6_gguf.py -q

Expected: PASS.

- [ ] **Step 5: Commit the isolated conversion guard.**

~~~powershell
git add scripts/build_ru_v6_gguf.py tests/test_build_ru_v6_gguf.py
git commit -m "feat: add Ru v6 GGUF conversion guard"
~~~

### Task 2: Add local llama.cpp server client

**Files:**

- Create: src/mini_gpt/llama_cpp_runtime.py
- Create: tests/test_llama_cpp_runtime.py

- [ ] **Step 1: Write failing runtime tests.**

~~~python
from mini_gpt.llama_cpp_runtime import LlamaCppRuntime


def test_server_command_loads_the_base_and_v6_lora(tmp_path):
    runtime = LlamaCppRuntime(project_root=tmp_path)
    command = runtime.server_command()
    assert "--model" in command and "--lora" in command
    assert command[command.index("--ctx-size") + 1] == "2048"
    assert command[command.index("--n-gpu-layers") + 1] == "all"


def test_chat_completion_parses_openai_response(tmp_path):
    runtime = LlamaCppRuntime(
        project_root=tmp_path,
        request=lambda *_args, **_kwargs: {"choices": [{"message": {"content": "Chào anh"}}]},
    )
    assert runtime.generate((("system", "s"), ("user", "u")), 32, 0.7, 20) == "Chào anh"
~~~

- [ ] **Step 2: Run test to verify it fails.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_llama_cpp_runtime.py -q

Expected: FAIL because mini_gpt.llama_cpp_runtime does not exist.

- [ ] **Step 3: Implement process ownership and HTTP parsing.**

~~~python
class LlamaCppRuntime:
    def server_command(self) -> tuple[str, ...]:
        return (
            str(self.server_executable), "--model", str(self.base_model), "--lora", str(self.lora_adapter),
            "--host", "127.0.0.1", "--port", str(self.port), "--ctx-size", "2048",
            "--n-gpu-layers", "all", "--parallel", "1",
        )

    def generate(self, messages, max_new_tokens, temperature, top_k) -> str:
        self.ensure_started()
        body = {
            "messages": [{"role": role, "content": text} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
            "top_k": top_k,
            "stream": False,
        }
        response = self._request("/v1/chat/completions", body, timeout=120)
        content = response.get("choices", [{}])[0].get("message", {}).get("content")
        if not isinstance(content, str) or not content.strip():
            raise RuntimeError("llama.cpp returned an empty chat completion")
        return normalize_text(content)
~~~

ensure_started validates the executable and both GGUF paths, starts a hidden subprocess only when no owned healthy server exists, polls /health with a bounded timeout, and includes captured stderr in the failure. stop terminates only the owned process.

- [ ] **Step 4: Run focused tests.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_llama_cpp_runtime.py -q

Expected: PASS.

- [ ] **Step 5: Commit the isolated runtime client.**

~~~powershell
git add src/mini_gpt/llama_cpp_runtime.py tests/test_llama_cpp_runtime.py
git commit -m "feat: add local llama.cpp runtime client"
~~~

### Task 3: Make GGUF an explicit GUI experiment

**Files:**

- Modify: src/mini_gpt/gui.py
- Modify: tests/test_gui.py

- [ ] **Step 1: Add failing backend-selection tests.**

~~~python
def test_model_runner_defaults_to_transformers_backend():
    assert ModelRunner().backend == "transformers"


def test_model_runner_uses_gguf_only_when_selected(monkeypatch):
    runner = ModelRunner()
    runner.backend = "gguf"
    runner.gguf_runtime = type("Runtime", (), {"generate": lambda *_args: "GGUF reply"})()
    assert runner.generate_reply("hi", 32, 0.7, 20) == "GGUF reply"
~~~

- [ ] **Step 2: Run test to verify it fails.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q

Expected: FAIL because ModelRunner.backend does not exist.

- [ ] **Step 3: Add a backend selector without changing the default.**

~~~python
BACKEND_TRANSFORMERS = "transformers"
BACKEND_GGUF = "gguf"


class ModelRunner:
    def __init__(self) -> None:
        self.backend = BACKEND_TRANSFORMERS
        self.gguf_runtime = LlamaCppRuntime(project_root=PROJECT_ROOT)

    def release_gpu(self) -> None:
        clear_llama_generation_cache()
        self.gguf_runtime.stop()
~~~

The settings dialog uses a readonly combobox labelled Backend with GGUF (default) and Transformers (fallback). _start_generation passes its selected backend into ModelRunner; is_cached and generate_reply dispatch to the selected runtime. The existing search retry continues to use the same selected backend. _play_tts calls self.runner.release_gpu under the existing transition lock before Gwen.

- [ ] **Step 4: Run focused GUI/runtime tests.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_llama_cpp_runtime.py tests\test_llama3_1_runtime.py -q

Expected: PASS.

- [ ] **Step 5: Commit the opt-in GUI path.**

~~~powershell
git add src/mini_gpt/gui.py tests/test_gui.py
git commit -m "feat: expose Ru GGUF runtime experiment"
~~~

### Task 4: Install official tools, convert, and measure

**Files:**

- Create: scripts/benchmark_ru_v6_gguf.py
- Modify: README.md

- [ ] **Step 1: Fetch and validate the official Windows CUDA release and source checkout.**

Run:

~~~powershell
git clone --depth 1 https://github.com/ggml-org/llama.cpp.git tools\llama.cpp
Invoke-WebRequest https://github.com/ggml-org/llama.cpp/releases/download/b10142/cudart-llama-bin-win-cuda-12.4-x64.zip -OutFile tools\llama.cpp-bin.zip
if ((Get-FileHash tools\llama.cpp-bin.zip -Algorithm SHA256).Hash -ne '8C79A9B226DE4B3CACFD1F83D24F962D0773BE79F1E7B75C6AF4DED7E32AE1D6') { throw 'llama.cpp release checksum mismatch' }
Invoke-WebRequest https://github.com/ggml-org/llama.cpp/releases/download/b10142/llama-b10142-bin-win-cuda-12.4-x64.zip -OutFile tools\llama.cpp-bin-core.zip
if ((Get-FileHash tools\llama.cpp-bin-core.zip -Algorithm SHA256).Hash -ne 'B0D9E893F130AB7A7267AAA21D8DD16C95CF5CE7C55E9A1048CC5AB030C2D126') { throw 'llama.cpp binary checksum mismatch' }
Expand-Archive tools\llama.cpp-bin.zip tools\llama.cpp-bin
Expand-Archive tools\llama.cpp-bin-core.zip tools\llama.cpp-bin
~~~

Expected: tools\llama.cpp\convert_hf_to_gguf.py, convert_lora_to_gguf.py, llama-server.exe, and llama-quantize.exe exist before conversion.

- [ ] **Step 2: Run the guarded converter.**

Run: & D:\LLM\.venv\Scripts\python.exe scripts\build_ru_v6_gguf.py

Expected: non-empty models\gguf\llama3.1-8b-instruct-q4_k_m.gguf and models\gguf\ru-llama3.1-8b-instruct-v6-lora.gguf; the HF base and v6 adapter remain present.

- [ ] **Step 3: Add a repeatable benchmark.**

~~~python
def main() -> int:
    runtime = LlamaCppRuntime(project_root=PROJECT_ROOT)
    prompts = ("Chào anh, em là Ru.", "Anh đang mệt một chút.")
    for prompt in prompts:
        started = perf_counter()
        reply = runtime.generate((("system", RU_SYSTEM_PROMPT), ("user", prompt)), 128, 0.7, 20)
        print(json.dumps({"prompt": prompt, "seconds": round(perf_counter() - started, 2), "reply": reply}, ensure_ascii=False))
    runtime.stop()
    return 0
~~~

- [ ] **Step 4: Record actual hardware evidence and update README.**

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe scripts\benchmark_ru_v6_gguf.py
nvidia-smi
~~~

Document the install command, optional GUI backend selection, measured cold/warm timings, and the fact that GGUF remains experimental until manual bilingual review passes.

- [ ] **Step 5: Run focused verification and commit docs/benchmark.**

Run: & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_build_ru_v6_gguf.py tests\test_llama_cpp_runtime.py tests\test_gui.py tests\test_llama3_1_runtime.py -q

Expected: PASS; no test opens model weights or requires a running server.

~~~powershell
git add scripts/benchmark_ru_v6_gguf.py README.md docs/superpowers/plans/2026-07-27-ru-v6-gguf-runtime.md
git commit -m "docs: document Ru v6 GGUF experiment"
~~~
