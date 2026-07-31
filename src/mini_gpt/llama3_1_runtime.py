from __future__ import annotations

import json
import logging
import threading
from collections import OrderedDict
from collections.abc import Callable
from importlib.util import find_spec
from pathlib import Path

from ._text_utils import normalize_text

logger = logging.getLogger(__name__)

_GENERATION_CACHE: OrderedDict[tuple[str, str | None], tuple[object, object]] = OrderedDict()
_CACHE_LOCK = threading.Lock()
_LOAD_LOCK = threading.Lock()
_MODEL_LOCKS: dict[tuple[str, str | None], threading.Lock] = {}
_IN_USE_COUNTS: dict[tuple[str, str | None], int] = {}
_MAX_CACHE_SIZE = 2
_MAX_EVICTIONS_PER_LOAD = 2
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

    if not torch.cuda.is_available():
        logger.warning(
            "CUDA is not available; falling back to a CPU model (4-bit quantization is skipped)"
        )
        model = AutoModelForCausalLM.from_pretrained(
            base_path,
            torch_dtype=torch.float32,
            device_map="cpu",
            attn_implementation="sdpa",
        )
    else:
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


def _release_cuda_memory() -> None:
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def clear_llama_generation_cache() -> None:
    with _CACHE_LOCK:
        for cache_key in list(_GENERATION_CACHE):
            if _IN_USE_COUNTS.get(cache_key, 0) > 0:
                continue
            _GENERATION_CACHE.pop(cache_key, None)
            _MODEL_LOCKS.pop(cache_key, None)
    _release_cuda_memory()


def _evict_cache_if_needed() -> None:
    # Must be called while holding _CACHE_LOCK.
    evicted = 0
    scanned = 0
    while (
        len(_GENERATION_CACHE) >= _MAX_CACHE_SIZE
        and evicted < _MAX_EVICTIONS_PER_LOAD
        and scanned < len(_GENERATION_CACHE)
    ):
        oldest_key = next(iter(_GENERATION_CACHE))
        scanned += 1
        if _IN_USE_COUNTS.get(oldest_key, 0) > 0:
            _GENERATION_CACHE.move_to_end(oldest_key)
            continue
        _GENERATION_CACHE.pop(oldest_key, None)
        _MODEL_LOCKS.pop(oldest_key, None)
        evicted += 1


def _generation_cache_key(base_path: str | Path, adapter_dir: str | Path | None) -> tuple[str, str | None]:
    adapter_key = str(Path(adapter_dir).resolve()) if adapter_dir is not None else None
    return str(base_path), adapter_key


def is_llama_generation_cached(base_path: str | Path, adapter_dir: str | Path | None) -> bool:
    """Return cache residency without importing model packages or allocating GPU memory."""
    with _CACHE_LOCK:
        return _generation_cache_key(base_path, adapter_dir) in _GENERATION_CACHE


def _get_or_load_model(
    cache_key: tuple[str, str | None], base_path: str, adapter_dir: str | Path | None
) -> tuple[object, object]:
    # Fast path: a resident entry is returned without touching the load lock.
    with _CACHE_LOCK:
        entry = _GENERATION_CACHE.get(cache_key)
        if entry is not None:
            _GENERATION_CACHE.move_to_end(cache_key)
            _IN_USE_COUNTS[cache_key] = _IN_USE_COUNTS.get(cache_key, 0) + 1
            return entry
    # Model loading allocates gigabytes and must never run while holding
    # _CACHE_LOCK, otherwise every other thread (including GUI status polls)
    # blocks for seconds. Double-checked locking guarantees a single load per
    # key; the load lock intentionally serializes concurrent loads of
    # different keys to bound peak GPU memory.
    with _LOAD_LOCK:
        with _CACHE_LOCK:
            entry = _GENERATION_CACHE.get(cache_key)
            if entry is not None:
                _GENERATION_CACHE.move_to_end(cache_key)
                _IN_USE_COUNTS[cache_key] = _IN_USE_COUNTS.get(cache_key, 0) + 1
                return entry
            _evict_cache_if_needed()
            loaded = _load_llama_for_generation(base_path, adapter_dir)
            _GENERATION_CACHE[cache_key] = loaded
            _MODEL_LOCKS[cache_key] = threading.Lock()
            _IN_USE_COUNTS[cache_key] = 1
    _release_cuda_memory()
    return loaded


def _release_model(cache_key: tuple[str, str | None]) -> None:
    with _CACHE_LOCK:
        remaining = _IN_USE_COUNTS.get(cache_key, 1) - 1
        if remaining > 0:
            _IN_USE_COUNTS[cache_key] = remaining
        else:
            _IN_USE_COUNTS.pop(cache_key, None)


def _is_cuda_oom(error: Exception, torch: object) -> bool:
    try:
        torch_oom = torch.OutOfMemoryError
    except AttributeError:
        torch_oom = torch.cuda.OutOfMemoryError
    return isinstance(error, (torch.cuda.OutOfMemoryError, torch_oom))


def _generate_with_oom_retry(model: object, generation_kwargs: dict, max_new_tokens: int, torch: object) -> object:
    try:
        with torch.no_grad():
            return model.generate(**generation_kwargs)
    except Exception as error:
        if not _is_cuda_oom(error, torch):
            raise
        logger.warning("CUDA out of memory during generation; retrying with max_new_tokens halved")
        torch.cuda.empty_cache()
        retry_kwargs = dict(generation_kwargs)
        retry_kwargs["max_new_tokens"] = max(1, max_new_tokens // 2)
        try:
            with torch.no_grad():
                return model.generate(**retry_kwargs)
        except Exception as retry_error:
            if _is_cuda_oom(retry_error, torch):
                raise RuntimeError(
                    "CUDA out of memory during generation even after reducing max_new_tokens; "
                    "free GPU memory and try again."
                ) from retry_error
            raise


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
    tokenizer, model = _get_or_load_model(cache_key, base_path, adapter_dir)
    try:
        inputs = tokenizer.apply_chat_template(
            serialized,
            tokenize=True,
            add_generation_prompt=True,
            return_tensors="pt",
            return_dict=True
        )

        input_ids = inputs["input_ids"]
        attention_mask = inputs["attention_mask"]

        import torch
        device = "cuda" if torch.cuda.is_available() else "cpu"
        input_ids = input_ids.to(device)
        attention_mask = attention_mask.to(device)

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

        if torch.cuda.is_available():
            generation_kwargs["cache_implementation"] = "quantized"

        model_lock = _MODEL_LOCKS[cache_key]
        with model_lock:
            try:
                generated = _generate_with_oom_retry(model, generation_kwargs, max_new_tokens, torch)
            except (TypeError, ValueError, NotImplementedError) as error:
                if "cache_implementation" not in generation_kwargs:
                    raise
                logger.warning(
                    "quantized KV cache is unsupported by this transformers version; "
                    "falling back to the default cache: %s",
                    error,
                )
                generation_kwargs.pop("cache_implementation")
                generated = _generate_with_oom_retry(model, generation_kwargs, max_new_tokens, torch)
    finally:
        _release_model(cache_key)

    return normalize_text(tokenizer.decode(generated[0][input_ids.shape[-1] :], skip_special_tokens=True))
