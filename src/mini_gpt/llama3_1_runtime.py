from __future__ import annotations

import json
import threading
from collections.abc import Callable
from importlib.util import find_spec
from pathlib import Path

from ._text_utils import normalize_text

_GENERATION_CACHE: dict[tuple[str, str | None], tuple[object, object]] = {}
_CACHE_LOCK = threading.Lock()
_MAX_CACHE_SIZE = 2
_RUNTIME_PACKAGES = ("accelerate", "bitsandbytes", "peft", "transformers")


def dependency_status() -> tuple[str, ...]:
    return tuple(name for name in _RUNTIME_PACKAGES if find_spec(name) is None)


def prepare_llama_for_generation(model: object) -> object:
    """Keep inference eager for the most stable local GUI path."""
    model.eval()
    # Do not enable torch.compile here. On this Python 3.13 / CUDA 13 setup it
    # has crashed the GUI process inside torch_cpu.dll, outside Python's error
    # handling. Stable eager inference is preferable to a fragile optimization.
    return model


def _load_llama_for_generation(base_path: str, adapter_dir: str | Path | None):
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    tokenizer = AutoTokenizer.from_pretrained(base_path)
    
    quantization = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True,
        bnb_4bit_compute_dtype=torch.float16,
    )
    
    model = AutoModelForCausalLM.from_pretrained(
        base_path,
        quantization_config=quantization,
        torch_dtype=torch.float16,
        device_map="cuda",
        attn_implementation="sdpa",
    )
    
    if adapter_dir is not None:
        adapter_path = Path(adapter_dir)
        if not adapter_path.is_dir():
            raise FileNotFoundError(f"Adapter directory not found: {adapter_path}")
        from peft import PeftModel
        model = PeftModel.from_pretrained(model, adapter_path)
        
    return tokenizer, prepare_llama_for_generation(model)

def clear_llama_generation_cache() -> None:
    with _CACHE_LOCK:
        _GENERATION_CACHE.clear()
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def _evict_cache_if_needed() -> None:
    # Must be called while holding _CACHE_LOCK.
    while len(_GENERATION_CACHE) >= _MAX_CACHE_SIZE:
        oldest_key = next(iter(_GENERATION_CACHE))
        del _GENERATION_CACHE[oldest_key]
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def _generation_cache_key(base_path: str | Path, adapter_dir: str | Path | None) -> tuple[str, str | None]:
    adapter_key = str(Path(adapter_dir).resolve()) if adapter_dir is not None else None
    return str(base_path), adapter_key


def is_llama_generation_cached(base_path: str | Path, adapter_dir: str | Path | None) -> bool:
    """Return cache residency without importing model packages or allocating GPU memory."""
    with _CACHE_LOCK:
        return _generation_cache_key(base_path, adapter_dir) in _GENERATION_CACHE

def generate_llama_response_hf(
    base_path: str,
    messages: tuple[tuple[str, str], ...],
    adapter_dir: str | Path | None = None,
    *,
    max_new_tokens: int = 1024,
    temperature: float = 0.7,
    top_k: int = 20,
    do_sample: bool = False,
) -> str:
    if not messages or messages[-1][0] != "user":
        raise ValueError("messages must end with a user turn")
    if max_new_tokens <= 0:
        raise ValueError("max new tokens must be positive")
    if temperature <= 0:
        raise ValueError("temperature must be positive")
    if top_k < 0:
        raise ValueError("top k cannot be negative")
        
    serialized = [{"role": role, "content": normalize_text(content)} for role, content in messages]
    
    cache_key = _generation_cache_key(base_path, adapter_dir)
    
    with _CACHE_LOCK:
        if cache_key not in _GENERATION_CACHE:
            _evict_cache_if_needed()
            _GENERATION_CACHE[cache_key] = _load_llama_for_generation(base_path, adapter_dir)
        
        tokenizer, model = _GENERATION_CACHE[cache_key]
    
    inputs = tokenizer.apply_chat_template(
        serialized, 
        tokenize=True, 
        add_generation_prompt=True, 
        return_tensors="pt",
        return_dict=True
    )
    
    input_ids = inputs["input_ids"].to("cuda")
    attention_mask = inputs["attention_mask"].to("cuda")
    
    generation_kwargs = {
        "input_ids": input_ids,
        "attention_mask": attention_mask,
        "do_sample": do_sample,
        "max_new_tokens": max_new_tokens,
        "repetition_penalty": 1.15,
    }
    if do_sample:
        generation_kwargs["temperature"] = temperature
    if do_sample and top_k > 0:
        generation_kwargs["top_k"] = top_k

    import torch
    with torch.no_grad():
        generated = model.generate(**generation_kwargs)
    
    return normalize_text(tokenizer.decode(generated[0][input_ids.shape[-1] :], skip_special_tokens=True))
