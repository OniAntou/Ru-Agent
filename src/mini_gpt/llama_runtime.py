from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from ._text_utils import normalize_text

@dataclass(frozen=True)
class LlamaBaseProfile:
    model_id: str
    local_path: str
    label: str

VISTRAL_7B_CHAT = LlamaBaseProfile(
    model_id="QuantFactory/Vistral-7B-Chat-GGUF",
    local_path="models/vistral-7b-chat-gguf/Vistral-7B-Chat.Q4_K_M.gguf",
    label="Ru Vistral 7B Chat GGUF",
)

LLAMA_3_1_8B_INSTRUCT = LlamaBaseProfile(
    model_id="bartowski/Meta-Llama-3.1-8B-Instruct-GGUF",
    local_path="models/llama3.1-8b-instruct-gguf/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
    label="Ru Llama-3.1 8B Instruct GGUF",
)

LLAMA_BASE_PROFILES = {
    profile.model_id: profile for profile in (VISTRAL_7B_CHAT, LLAMA_3_1_8B_INSTRUCT)
}
_LLAMA_CACHE: dict[str, object] = {}

def llama_base_profile(model_id: str) -> LlamaBaseProfile:
    try:
        return LLAMA_BASE_PROFILES[model_id]
    except KeyError as error:
        raise ValueError(f"model_id must be a registered Llama base, got {model_id!r}") from error

def _load_llama(profile: LlamaBaseProfile):
    from llama_cpp import Llama
    
    path = Path(profile.local_path)
    if not path.is_file():
        raise FileNotFoundError(f"GGUF model not found at {path}")
    
    model = Llama(
        model_path=str(path),
        n_gpu_layers=-1,  # Full GPU offload
        n_ctx=4096,       # Reasonable context size
        verbose=False,
    )
    return model

def clear_llama_generation_cache() -> None:
    _LLAMA_CACHE.clear()

def generate_llama_response(
    profile: LlamaBaseProfile,
    messages: tuple[tuple[str, str], ...],
) -> str:
    if llama_base_profile(profile.model_id) != profile:
        raise ValueError("profile must be a registered Llama base")
    if not messages or messages[-1][0] != "user":
        raise ValueError("Messages must end with a user turn")
        
    serialized = [{"role": role, "content": normalize_text(content)} for role, content in messages]
    
    cache_key = profile.model_id
    if cache_key not in _LLAMA_CACHE:
        _LLAMA_CACHE[cache_key] = _load_llama(profile)
        
    model = _LLAMA_CACHE[cache_key]
    
    response = model.create_chat_completion(
        messages=serialized,
        max_tokens=96,
        temperature=0.0,
        repeat_penalty=1.05,
    )
    
    return normalize_text(response["choices"][0]["message"]["content"])
