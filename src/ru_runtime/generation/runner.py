import logging

logger = logging.getLogger(__name__)

BACKEND_OPENCODE = "OpenCode Zen"
BACKEND_KILO = "Kilo Gateway"
BACKEND_OLLAMA = "Ollama"
BACKEND_LMSTUDIO = "LM Studio"
BACKEND_CUSTOM = "Custom OpenAI"

OPENAI_COMPATIBLE_BACKENDS = (
    BACKEND_OPENCODE,
    BACKEND_KILO,
    BACKEND_OLLAMA,
    BACKEND_LMSTUDIO,
    BACKEND_CUSTOM,
)
ALL_BACKENDS = OPENAI_COMPATIBLE_BACKENDS

KILO_SCREEN_REACTION_MODEL = "inclusionai/ling-3.0-flash:free"
KILO_SCREEN_REACTION_FALLBACK_MODEL = "openrouter/free"
_IMMEDIATE_KILO_FALLBACK_STATUSES = ("HTTP 429", "HTTP 502", "HTTP 503", "HTTP 504")

from ru_runtime.generation.chat import ChatTurn
from ru_runtime.generation.openai import (
    OpenAIRuntime,
    OPENCODE_BASE_URL,
    KILO_GATEWAY_BASE_URL,
    OLLAMA_BASE_URL,
    LMSTUDIO_BASE_URL,
)
from ru_runtime.policy.style import (
    RU_CLOUD_SYSTEM_PROMPT,
    RU_SCREEN_REACTION_SYSTEM_PROMPT,
)
from ru_runtime.generation.orchestration import execute_reply
from ru_runtime.generation.search import perform_web_search
from ru_runtime.vts.actions import VTS_ACTION_INSTRUCTION
from ru_runtime.voice.pacing import VOICE_PACE_INSTRUCTION


class ModelRunner:
    def __init__(self) -> None:
        self.backend = BACKEND_OPENCODE
        self.openai_runtime: OpenAIRuntime | None = None
        self.kilo_reasoning_enabled = False
        self.load_error: str | None = None

    def select_backend(self, backend: str) -> None:
        if backend not in ALL_BACKENDS:
            raise ValueError(f"unsupported backend: {backend}")
        logger.info("Selecting backend: %s (previous: %s)", backend, self.backend)
        self.backend = backend

    def load(
        self,
        api_key: str = "",
        model_name: str = "deepseek-v4-flash-free",
        api_variant: str = "Default",
        kilo_reasoning_enabled: bool = False,
        base_url: str | None = None,
    ) -> str:
        self.kilo_reasoning_enabled = bool(kilo_reasoning_enabled)
        self.load_error = None
        logger.info("Loading backend=%s, model_name=%s", self.backend, model_name)
        try:
            if self.backend in OPENAI_COMPATIBLE_BACKENDS:
                if self.backend == BACKEND_OPENCODE:
                    target_url = base_url or OPENCODE_BASE_URL
                    default_model = "deepseek-v4-flash-free"
                elif self.backend == BACKEND_KILO:
                    target_url = base_url or KILO_GATEWAY_BASE_URL
                    default_model = "deepseek-ai/DeepSeek-V3"
                elif self.backend == BACKEND_OLLAMA:
                    target_url = base_url or OLLAMA_BASE_URL
                    default_model = "qwen2.5:latest"
                elif self.backend == BACKEND_LMSTUDIO:
                    target_url = base_url or LMSTUDIO_BASE_URL
                    default_model = "default"
                else:
                    target_url = base_url or "http://localhost:8000/v1"
                    default_model = "default"

                effective_model = model_name or default_model
                effective_key = api_key or ("local" if self.backend in (BACKEND_OLLAMA, BACKEND_LMSTUDIO) else "")
                self.openai_runtime = OpenAIRuntime(
                    api_key=effective_key,
                    base_url=target_url,
                    model_name=effective_model,
                    api_variant=api_variant,
                )
                logger.info("Loaded %s runtime with model: %s (%s)", self.backend, effective_model, target_url)
                return f"Ready ({self.backend} API)"
            raise ValueError(f"unsupported backend: {self.backend}")
        except Exception as error:
            self.load_error = str(error)
            raise

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
        is_cancelled=None,
        structured_tools_only=False,
    ) -> str:
        if self.backend in OPENAI_COMPATIBLE_BACKENDS and self.openai_runtime:
            kwargs = {
                "on_complete_timing": on_complete_timing,
                "is_cancelled": is_cancelled,
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
        raise RuntimeError(f"unsupported backend: {self.backend}")

    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: list[ChatTurn] | None = None,
        memory_context: str | None = None,
        mood_context: str | None = None,
        allow_web_search: bool = False,
        vts_instruction: str = "",
        *,
        on_stream_delta=None,
        on_stream_reset=None,
        on_complete_timing=None,
        is_cancelled=None,
        structured_tools_only: bool = False,
    ) -> str:
        system_prompt = RU_CLOUD_SYSTEM_PROMPT
        return execute_reply(
            self._generate,
            prompt=prompt,
            system_instruction=system_prompt + VTS_ACTION_INSTRUCTION + VOICE_PACE_INSTRUCTION + vts_instruction,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            turns_history=turns_history or (),
            memory_context=memory_context,
            mood_context=mood_context,
            allow_web_search=allow_web_search,
            web_search=perform_web_search,
            on_stream_delta=on_stream_delta,
            on_stream_reset=on_stream_reset,
            on_complete_timing=on_complete_timing,
            is_cancelled=is_cancelled,
            allow_legacy_markers=not structured_tools_only,
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
