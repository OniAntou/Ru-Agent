from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from importlib.util import find_spec
from pathlib import Path

from .data import normalize_text


@dataclass(frozen=True)
class QwenBaseProfile:
    model_id: str
    local_path: str
    label: str


QWEN_V3_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-0.5B-Instruct",
    local_path="models/qwen2.5-0.5b-instruct",
    label="Ru V3 Qwen 0.5B",
)
QWEN_V4_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-1.5B-Instruct",
    local_path="models/qwen2.5-1.5b-instruct",
    label="Ru V4 Qwen 1.5B",
)
QWEN_V5_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-3B-Instruct",
    local_path="models/qwen2.5-3b-instruct",
    label="Ru V5 Qwen 3B",
)
QWEN_MODEL_ID = QWEN_V3_BASE.model_id
QWEN_BASE_PROFILES = {profile.model_id: profile for profile in (QWEN_V3_BASE, QWEN_V4_BASE, QWEN_V5_BASE)}
REQUIRED_QWEN_PACKAGES = ("transformers", "accelerate", "peft", "bitsandbytes")
_GENERATION_CACHE: dict[tuple[str, str | None], tuple[object, object]] = {}


def qwen_base_profile(model_id: str) -> QwenBaseProfile:
    try:
        return QWEN_BASE_PROFILES[model_id]
    except KeyError as error:
        raise ValueError(f"model_id must be a registered Ru Qwen base, got {model_id!r}") from error


def _load_qwen_for_generation(profile: QwenBaseProfile, adapter_dir: str | Path | None):
    require_qwen_dependencies()
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    base_path = Path(profile.local_path)
    verify_local_qwen_base(base_path)
    tokenizer = AutoTokenizer.from_pretrained(base_path)
    tokenizer.pad_token = tokenizer.pad_token or tokenizer.eos_token
    quantization = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True,
        bnb_4bit_compute_dtype=torch.float16,
    )
    model = AutoModelForCausalLM.from_pretrained(
        base_path,
        quantization_config=quantization,
        dtype=torch.float16,
        device_map="cuda",
    )
    if adapter_dir is not None:
        adapter_path = Path(adapter_dir)
        if not adapter_path.is_dir():
            raise FileNotFoundError(f"Ru adapter directory not found: {adapter_path}")
        from peft import PeftModel

        model = PeftModel.from_pretrained(model, adapter_path)
    model.eval()
    return tokenizer, model


def clear_qwen_generation_cache() -> None:
    _GENERATION_CACHE.clear()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def generate_qwen_response(
    profile: QwenBaseProfile,
    messages: tuple[tuple[str, str], ...],
    adapter_dir: str | Path | None = None,
) -> str:
    if qwen_base_profile(profile.model_id) != profile:
        raise ValueError("profile must be a registered Ru Qwen base")
    if not messages or messages[-1][0] != "user":
        raise ValueError("Qwen generation messages must end with a user turn")
    serialized = [{"role": role, "content": normalize_text(content)} for role, content in messages]
    if any(message["role"] not in {"system", "user", "assistant"} or not message["content"] for message in serialized):
        raise ValueError("Qwen generation messages must use non-empty supported roles")
    adapter_key = str(Path(adapter_dir).resolve()) if adapter_dir is not None else None
    cache_key = (profile.model_id, adapter_key)
    if cache_key not in _GENERATION_CACHE:
        _GENERATION_CACHE[cache_key] = _load_qwen_for_generation(profile, adapter_dir)
    tokenizer, model = _GENERATION_CACHE[cache_key]
    input_ids = tokenizer.apply_chat_template(
        serialized,
        tokenize=True,
        add_generation_prompt=True,
        return_tensors="pt",
    ).to("cuda")
    attention_mask = input_ids.new_ones(input_ids.shape)
    generated = model.generate(
        input_ids=input_ids,
        attention_mask=attention_mask,
        do_sample=False,
        max_new_tokens=96,
        repetition_penalty=1.05,
    )
    return normalize_text(tokenizer.decode(generated[0][input_ids.shape[-1] :], skip_special_tokens=True))


def qwen_dependency_status(
    resolver: Callable[[str], object | None] = find_spec,
) -> dict[str, bool]:
    return {name: resolver(name) is not None for name in REQUIRED_QWEN_PACKAGES}


def require_qwen_dependencies(
    resolver: Callable[[str], object | None] = find_spec,
) -> None:
    missing = [name for name, present in qwen_dependency_status(resolver).items() if not present]
    if missing:
        raise RuntimeError("install the Qwen runtime extra: " + ", ".join(missing))


def verify_local_qwen_base(path: str | Path) -> dict[str, object]:
    model_path = Path(path)
    config_path = model_path / "config.json"
    if not config_path.is_file():
        raise FileNotFoundError(f"Qwen config not found: {config_path}")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if not isinstance(config, dict) or config.get("model_type") != "qwen2":
        raise ValueError("local base model_type must be qwen2")
    if not any(model_path.glob("*.safetensors")):
        raise FileNotFoundError("local Qwen safetensors weights not found")
    return config
