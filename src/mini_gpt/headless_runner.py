import logging
from pathlib import Path

logger = logging.getLogger(__name__)

BACKEND_TRANSFORMERS = "Transformers"
BACKEND_GGUF = "llama.cpp"
BACKEND_OPENCODE = "OpenCode Zen"
BACKEND_KILO = "Kilo Gateway"

PROJECT_ROOT = Path(__file__).parent.parent.parent

from mini_gpt.chat import ChatTurn
from mini_gpt.llama_cpp_runtime import LlamaCppRuntime
from mini_gpt.llama3_1_runtime import (
    clear_llama_generation_cache,
    dependency_status,
    generate_llama_response_hf,
    is_llama_generation_cached,
)
from mini_gpt.openai_runtime import OpenAIRuntime, OPENCODE_BASE_URL, KILO_GATEWAY_BASE_URL
from mini_gpt.ru_style import RU_SYSTEM_PROMPT, RU_CLOUD_SYSTEM_PROMPT
from mini_gpt.reply_orchestration import execute_reply
from mini_gpt.search import perform_web_search
from mini_gpt.vts_actions import VTS_ACTION_INSTRUCTION
from mini_gpt.voice_pacing import VOICE_PACE_INSTRUCTION
class ModelRunner:
    def __init__(self) -> None:
        self.checkpoint_path: Path | None = None
        self.base_model_path: Path = PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"
        self.backend = BACKEND_GGUF
        self.gguf_runtime = LlamaCppRuntime(project_root=PROJECT_ROOT)
        self.openai_runtime: OpenAIRuntime | None = None

    def select_backend(self, backend: str) -> None:
        if backend not in (BACKEND_TRANSFORMERS, BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO):
            raise ValueError(f"unsupported backend: {backend}")
        self.backend = backend

    def load(
        self,
        checkpoint_path: str | Path,
        api_key: str = "",
        model_name: str = "deepseek-v4-flash-free",
        api_variant: str = "Default",
    ) -> str:
        if self.backend == BACKEND_OPENCODE:
            self.openai_runtime = OpenAIRuntime(
                api_key,
                base_url=OPENCODE_BASE_URL,
                model_name=model_name,
                api_variant=api_variant,
            )
            return "Ready (OpenCode Zen API)"
        if self.backend == BACKEND_KILO:
            self.openai_runtime = OpenAIRuntime(
                api_key,
                base_url=KILO_GATEWAY_BASE_URL,
                model_name=model_name,
                api_variant=api_variant,
            )
            return "Ready (Kilo Gateway API)"
        if self.backend == BACKEND_GGUF:
            self.gguf_runtime.validate_assets()
            self.checkpoint_path = Path(checkpoint_path)
            return f"Ready (llama.cpp GGUF experiment) | Ru {self.checkpoint_path.name.split('-')[-1]} LoRA"
        missing_dependencies = dependency_status()
        if missing_dependencies:
            packages = ", ".join(missing_dependencies)
            raise RuntimeError(
                f"Missing Ru runtime dependencies: {packages}. "
                "Install the runtime extra before starting the model."
            )
        adapter_dir = Path(checkpoint_path)
        if not adapter_dir.is_dir():
            raise FileNotFoundError(f"adapter directory not found: {adapter_dir}")
        self.checkpoint_path = adapter_dir
        return f"Ready (Llama 3.1 8B QLoRA) | {adapter_dir.name}"

    def is_cached(self, checkpoint_path: str | Path) -> bool:
        if self.backend == BACKEND_OPENCODE:
            return True
        if self.backend == BACKEND_GGUF:
            return self.gguf_runtime.is_cached()
        return is_llama_generation_cached(self.base_model_path, checkpoint_path)

    def release_gpu(self) -> None:
        clear_llama_generation_cache()
        self.gguf_runtime.stop()

    def _generate(
        self,
        messages: tuple[tuple[str, str], ...],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_stream_delta=None,
        on_complete_timing=None,
    ) -> str:
        if self.backend in (BACKEND_OPENCODE, BACKEND_KILO) and self.openai_runtime:
            kwargs = {
                "on_complete_timing": on_complete_timing,
            }
            if on_stream_delta is not None:
                kwargs["on_delta"] = on_stream_delta
            return self.openai_runtime.generate(
                messages,
                max_new_tokens,
                temperature,
                top_k,
                **kwargs,
            )
        if self.backend == BACKEND_GGUF:
            return self.gguf_runtime.generate(
                messages, max_new_tokens, temperature, top_k, on_delta=on_stream_delta
            )
        return generate_llama_response_hf(
            str(self.base_model_path),
            messages,
            self.checkpoint_path,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            do_sample=True,
        )
    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: list[ChatTurn] | None = None,
        memory_context: str | None = None,
        allow_web_search: bool = False,
        vts_instruction: str = "",
        *,
        on_stream_delta=None,
        on_stream_reset=None,
        on_complete_timing=None,
    ) -> str:
        system_prompt = RU_CLOUD_SYSTEM_PROMPT if self.backend in (BACKEND_OPENCODE, BACKEND_KILO) else RU_SYSTEM_PROMPT
        return execute_reply(
            self._generate,
            prompt=prompt,
            system_instruction=system_prompt + VTS_ACTION_INSTRUCTION + VOICE_PACE_INSTRUCTION + vts_instruction,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            turns_history=turns_history or (),
            memory_context=memory_context,
            allow_web_search=allow_web_search,
            web_search=perform_web_search,
            on_stream_delta=on_stream_delta,
            on_stream_reset=on_stream_reset,
            on_complete_timing=on_complete_timing,
        )
