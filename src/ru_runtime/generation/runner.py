import logging
from pathlib import Path

logger = logging.getLogger(__name__)

BACKEND_GGUF = "llama.cpp"
BACKEND_OPENCODE = "OpenCode Zen"
BACKEND_KILO = "Kilo Gateway"
KILO_SCREEN_REACTION_MODEL = "inclusionai/ling-3.0-flash:free"
KILO_SCREEN_REACTION_FALLBACK_MODEL = "openrouter/free"
_IMMEDIATE_KILO_FALLBACK_STATUSES = ("HTTP 429", "HTTP 502", "HTTP 503", "HTTP 504")

PROJECT_ROOT = Path(__file__).resolve().parents[3]

from ru_runtime.generation.chat import ChatTurn
from ru_runtime.models.llama_cpp import LlamaCppRuntime
from ru_runtime.generation.openai import OpenAIRuntime, OPENCODE_BASE_URL, KILO_GATEWAY_BASE_URL
from ru_runtime.policy.style import (
    RU_CLOUD_SYSTEM_PROMPT,
    RU_SCREEN_REACTION_SYSTEM_PROMPT,
    RU_SYSTEM_PROMPT,
)
from ru_runtime.generation.orchestration import execute_reply
from ru_runtime.generation.search import perform_web_search
from ru_runtime.vts.actions import VTS_ACTION_INSTRUCTION
from ru_runtime.voice.pacing import VOICE_PACE_INSTRUCTION
class ModelRunner:
    def __init__(self) -> None:
        self.checkpoint_path: Path | None = None
        self.backend = BACKEND_GGUF
        default_adapter = PROJECT_ROOT / "models" / "gguf" / "ru-lfm2.5-2.6b-v1-lora.gguf"
        self.gguf_runtime = LlamaCppRuntime(
            project_root=PROJECT_ROOT,
            adapter=default_adapter if default_adapter.is_file() else None,
        )
        self.openai_runtime: OpenAIRuntime | None = None
        self.kilo_reasoning_enabled = False

    def select_backend(self, backend: str) -> None:
        if backend not in (BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO):
            raise ValueError(f"unsupported backend: {backend}")
        logger.info("Selecting backend: %s (previous: %s)", backend, self.backend)
        self.backend = backend

    def load(
        self,
        checkpoint_path: str | Path,
        api_key: str = "",
        model_name: str = "deepseek-v4-flash-free",
        api_variant: str = "Default",
        kilo_reasoning_enabled: bool = False,
    ) -> str:
        self.kilo_reasoning_enabled = bool(kilo_reasoning_enabled)
        logger.info("Loading backend=%s, checkpoint_path=%s, model_name=%s", self.backend, checkpoint_path, model_name)
        if self.backend in (BACKEND_OPENCODE, BACKEND_KILO):
            self.gguf_runtime.stop()
            base_url = OPENCODE_BASE_URL if self.backend == BACKEND_OPENCODE else KILO_GATEWAY_BASE_URL
            self.openai_runtime = OpenAIRuntime(
                api_key,
                base_url=base_url,
                model_name=model_name,
                api_variant=api_variant,
            )
            logger.info("Loaded %s runtime with model: %s", self.backend, model_name)
            return f"Ready ({self.backend} API)"
        if self.backend == BACKEND_GGUF:
            logger.info("Loading GGUF runtime with checkpoint: %s", checkpoint_path)
            self.gguf_runtime.validate_assets()
            base_model_path = self.gguf_runtime.base_model.resolve()
            if str(checkpoint_path).strip():
                resolved = Path(checkpoint_path).resolve()
                allowed = (PROJECT_ROOT / "checkpoints", PROJECT_ROOT / "models")
                if not any(
                    resolved.is_relative_to(allowed_dir.resolve())
                    for allowed_dir in allowed
                ):
                    raise ValueError(
                        f"checkpoint_path must be inside project checkpoints/ or models/, got: {checkpoint_path}"
                    )
                if resolved != base_model_path:
                    logger.info(
                        "Ignoring legacy GGUF checkpoint selector %s; using the fixed LFM2.5 asset %s",
                        resolved,
                        base_model_path,
                    )
            self.checkpoint_path = base_model_path
            logger.info("GGUF runtime ready with checkpoint: %s", self.checkpoint_path)
            label = "LFM2.5-2.6B Q4_K_M"
            if self.gguf_runtime.adapter is not None:
                label += " + v1 LoRA"
            return f"Ready (llama.cpp GGUF) | {label}"
        raise ValueError(f"unsupported backend: {self.backend}")

    def is_cached(self, checkpoint_path: str | Path) -> bool:
        if self.backend == BACKEND_OPENCODE:
            return True
        if self.backend == BACKEND_GGUF:
            return self.gguf_runtime.is_cached()
        return False

    def release_gpu(self) -> None:
        self.gguf_runtime.stop()

    def _generate(
        self,
        messages: tuple[tuple[str, str], ...],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_stream_delta=None,
        on_stream_reset=None,
        on_complete_timing=None,
    ) -> str:
        if self.backend in (BACKEND_OPENCODE, BACKEND_KILO) and self.openai_runtime:
            kwargs = {
                "on_complete_timing": on_complete_timing,
            }
            if self.backend == BACKEND_KILO and not self.kilo_reasoning_enabled:
                kwargs["reasoning_effort_override"] = "none"
            if on_stream_delta is not None:
                kwargs["on_delta"] = on_stream_delta
            if on_stream_reset is not None:
                kwargs["on_reset"] = on_stream_reset
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
        raise RuntimeError(f"unsupported backend: {self.backend}")
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

    def generate_screen_reaction(
        self,
        observation_prompt: str,
        *,
        vts_instruction: str,
        is_cancelled,
        on_complete_timing=None,
    ) -> str:
        if (
            self.backend not in (BACKEND_OPENCODE, BACKEND_KILO)
            or self.openai_runtime is None
        ):
            raise RuntimeError(
                "Screen reactions require an active OpenCode or Kilo cloud backend."
            )
        system = (
            RU_SCREEN_REACTION_SYSTEM_PROMPT
            + vts_instruction
        )
        model_override = (
            KILO_SCREEN_REACTION_MODEL
            if self.backend == BACKEND_KILO
            and self.openai_runtime.model_name == "kilo-auto/free"
            else None
        )
        reasoning_effort_override = "none" if model_override is not None else None
        messages = (("system", system), ("user", observation_prompt))
        kwargs = {
            "on_complete_timing": on_complete_timing,
            "timeout_seconds": 4.0,
            "max_retries": 0,
            "is_cancelled": is_cancelled,
            "model_override": model_override,
            "reasoning_effort_override": reasoning_effort_override,
        }
        try:
            return self.openai_runtime.generate(messages, 256, 0.85, 40, **kwargs)
        except RuntimeError as error:
            if model_override is None or not any(
                status in str(error) for status in _IMMEDIATE_KILO_FALLBACK_STATUSES
            ):
                raise
            kwargs["model_override"] = KILO_SCREEN_REACTION_FALLBACK_MODEL
            return self.openai_runtime.generate(messages, 256, 0.85, 40, **kwargs)
