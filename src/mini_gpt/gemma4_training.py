from __future__ import annotations

import json
import random
import subprocess
import threading
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path
from typing import Any, Literal

import torch

from ._text_utils import normalize_text

IGNORE_LABEL = -100
DEVICE = "cuda:0"
_GEMMA_LIGHT_OUTPUT_PREFIX = "gemma4-e4b-ru-light"
_RESERVED_OUTPUT_NAMES = {
    "ru-llama3.1-8b-instruct-v6",
    "ru-llama3.1-8b-instruct-v7",
    "ru-llama3.1-8b-instruct-v7.1",
}


@dataclass(frozen=True)
class GemmaMessage:
    role: Literal["system", "user", "assistant"]
    content: str

    def __post_init__(self) -> None:
        if self.role not in {"system", "user", "assistant"}:
            raise ValueError("Role must be system, user, or assistant")
        if not normalize_text(self.content):
            raise ValueError("Message content cannot be empty")


@dataclass(frozen=True)
class EncodedGemmaResponse:
    input_ids: list[int]
    labels: list[int]
    response_token_count: int


@dataclass(frozen=True)
class Gemma4TrainingConfig:
    base_model_id: str
    base_model_path: str | Path
    dataset_dir: str | Path
    adapter_dir: str | Path
    max_length: int = 512
    seed: int = 42
    max_steps: int = 64
    learning_rate: float = 2e-5
    gradient_accumulation_steps: int = 8
    lora_rank: int = 4
    lora_alpha: int = 8
    lora_dropout: float = 0.05
    target_modules: tuple[str, ...] = ("q_proj", "k_proj", "v_proj", "o_proj")
    quantization_bits: int = 4
    physical_vram_cap_mib: float | None = 6144
    physical_vram_poll_seconds: float = 0.25
    backend: str = "gemma4-qlora"
    profile_name: str = "gemma4_e4b_ru_light"

    @classmethod
    def from_json(cls, path: str | Path) -> "Gemma4TrainingConfig":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("Training config must be an object")
        for key in ("base_model_path", "dataset_dir", "adapter_dir"):
            if key in payload:
                payload[key] = Path(payload[key])
        if "target_modules" in payload:
            payload["target_modules"] = tuple(payload["target_modules"])
        return cls(**payload)

    def validate(self) -> None:
        if self.max_length <= 0:
            raise ValueError("max_length must be positive")
        if self.max_steps <= 0:
            raise ValueError("max_steps must be positive")
        if self.gradient_accumulation_steps <= 0:
            raise ValueError("gradient_accumulation_steps must be positive")
        if self.learning_rate <= 0:
            raise ValueError("learning_rate must be positive")
        if self.lora_rank <= 0 or self.lora_alpha <= 0:
            raise ValueError("LoRA rank and alpha must be positive")
        if not self.target_modules:
            raise ValueError("target_modules cannot be empty")
        if self.quantization_bits not in {4, 8}:
            raise ValueError("quantization_bits must be 4 or 8")
        if self.physical_vram_cap_mib is not None and self.physical_vram_cap_mib <= 0:
            raise ValueError("physical_vram_cap_mib must be positive")
        if self.physical_vram_poll_seconds <= 0:
            raise ValueError("physical_vram_poll_seconds must be positive")


def read_nvidia_smi_memory_mib() -> float | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.used",
                "--format=csv,noheader,nounits",
                "-i",
                "0",
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=2,
        )
    except (OSError, subprocess.SubprocessError):
        return None

    values: list[float] = []
    for line in completed.stdout.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        try:
            values.append(float(stripped))
        except ValueError:
            return None
    return values[0] if values else None


class PhysicalVramMonitor:
    def __init__(self, cap_mib: float, poll_seconds: float) -> None:
        if cap_mib <= 0:
            raise ValueError("physical VRAM cap must be positive")
        if poll_seconds <= 0:
            raise ValueError("physical VRAM poll interval must be positive")
        self.cap_mib = cap_mib
        self.poll_seconds = poll_seconds
        self._stop = threading.Event()
        self._exceeded = threading.Event()
        self._lock = threading.Lock()
        self._peak_mib: float | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._poll, daemon=True, name="gemma4-vram-monitor")
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(2.0, self.poll_seconds * 4))

    def _poll(self) -> None:
        while not self._stop.is_set():
            current = read_nvidia_smi_memory_mib()
            if current is not None:
                with self._lock:
                    self._peak_mib = max(self._peak_mib or current, current)
                if current > self.cap_mib:
                    self._exceeded.set()
            self._stop.wait(self.poll_seconds)

    @property
    def peak_mib(self) -> float | None:
        with self._lock:
            return self._peak_mib

    @property
    def exceeded(self) -> bool:
        return self._exceeded.is_set()


def validate_gemma4_adapter_dir(path: str | Path) -> Path:
    adapter_dir = Path(path)
    if adapter_dir.name in _RESERVED_OUTPUT_NAMES or "llama" in adapter_dir.name.lower():
        raise ValueError("Gemma light output cannot overwrite a reserved comparison adapter")
    if not adapter_dir.name.startswith(_GEMMA_LIGHT_OUTPUT_PREFIX):
        raise ValueError(
            "Gemma light output must use a directory beginning with gemma4-e4b-ru-light"
        )
    return adapter_dir


class GemmaResponseCollator:
    def __init__(self, pad_token_id: int) -> None:
        self.pad_token_id = pad_token_id

    def __call__(self, examples: list[EncodedGemmaResponse]) -> dict[str, torch.Tensor]:
        if not examples:
            raise ValueError("Batch cannot be empty")
        max_length = max(len(example.input_ids) for example in examples)
        input_rows: list[list[int]] = []
        attention_rows: list[list[int]] = []
        label_rows: list[list[int]] = []
        for example in examples:
            if len(example.input_ids) != len(example.labels):
                raise ValueError("Inputs and labels must have equal lengths")
            padding = max_length - len(example.input_ids)
            input_rows.append([self.pad_token_id] * padding + example.input_ids)
            attention_rows.append([0] * padding + [1] * len(example.input_ids))
            label_rows.append([IGNORE_LABEL] * padding + example.labels)
        return {
            "input_ids": torch.tensor(input_rows, dtype=torch.long),
            "attention_mask": torch.tensor(attention_rows, dtype=torch.long),
            "labels": torch.tensor(label_rows, dtype=torch.long),
        }


def _chat_template_ids(tokenizer: Any, messages: tuple[GemmaMessage, ...]) -> list[int]:
    serialized = [asdict(message) for message in messages]
    try:
        prompt_ids = tokenizer.apply_chat_template(
            serialized,
            tokenize=True,
            add_generation_prompt=True,
            enable_thinking=False,
        )
    except TypeError:
        prompt_ids = tokenizer.apply_chat_template(
            serialized,
            tokenize=True,
            add_generation_prompt=True,
        )
    if isinstance(prompt_ids, dict) or hasattr(prompt_ids, "keys"):
        prompt_ids = prompt_ids["input_ids"]
    if hasattr(prompt_ids, "tolist"):
        prompt_ids = prompt_ids.tolist()
    if prompt_ids and isinstance(prompt_ids[0], list):
        prompt_ids = prompt_ids[0]
    return [int(token_id) for token_id in prompt_ids]


def encode_gemma_response(
    tokenizer: Any,
    messages: tuple[GemmaMessage, ...] | list[GemmaMessage],
    response: str,
    max_length: int,
) -> EncodedGemmaResponse:
    if max_length <= 0:
        raise ValueError("max_length must be positive")
    normalized_messages = tuple(messages)
    if not normalized_messages or normalized_messages[-1].role != "user":
        raise ValueError("Context's last message must be a user message")
    response = normalize_text(response)
    if not response:
        raise ValueError("Response cannot be empty")

    prompt_ids = _chat_template_ids(tokenizer, normalized_messages)
    response_ids = [
        *tokenizer(response, add_special_tokens=False)["input_ids"],
        int(tokenizer.eos_token_id),
    ]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)) :]
    return EncodedGemmaResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
        response_token_count=len(response_ids),
    )


def _load_gemma_text_model(base_path: Path, quantization_bits: int):
    import torch
    from transformers import AutoConfig, AutoProcessor, BitsAndBytesConfig
    from transformers.models.gemma4.modeling_gemma4 import Gemma4ForCausalLM
    from transformers.utils import logging as transformers_logging

    transformers_logging.set_verbosity_error()
    if quantization_bits != 4:
        raise ValueError("The bounded Gemma training profile only supports 4-bit NF4")
    full_config = AutoConfig.from_pretrained(base_path, local_files_only=True)
    text_config = full_config.text_config
    quantization = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_compute_dtype=torch.float16,
        bnb_4bit_use_double_quant=True,
        llm_int8_enable_fp32_cpu_offload=True,
    )
    device_map: dict[str, str] = {
        "model.embed_tokens": DEVICE,
        "model.embed_tokens_per_layer": "cpu",
        "model.per_layer_model_projection": DEVICE,
        "model.per_layer_projection_norm": DEVICE,
        "model.norm": DEVICE,
        "lm_head": DEVICE,
    }
    device_map.update({f"model.layers.{index}": DEVICE for index in range(text_config.num_hidden_layers)})

    loaded = Gemma4ForCausalLM.from_pretrained(
        base_path,
        config=text_config,
        key_mapping={r"^model\.language_model\.": "model."},
        quantization_config=quantization,
        dtype=torch.float16,
        device_map=device_map,
        low_cpu_mem_usage=True,
        local_files_only=True,
        output_loading_info=True,
    )
    model, loading_info = loaded
    if loading_info.get("missing_keys"):
        raise RuntimeError(f"Gemma text backbone has missing weights: {loading_info['missing_keys'][:8]}")

    original_get_per_layer_inputs = model.model.get_per_layer_inputs
    per_layer_embedding = model.model.embed_tokens_per_layer
    per_layer_hook = getattr(per_layer_embedding, "_hf_hook", None)
    offloaded_per_layer_weight = None
    if per_layer_hook is not None and getattr(per_layer_hook, "offload", False):
        weights_map = getattr(per_layer_hook, "weights_map", None)
        if weights_map is not None:
            offloaded_per_layer_weight = weights_map["weight"]

    def get_per_layer_inputs_on_execution_device(input_ids, inputs_embeds):
        # The per-layer embedding table is ~5.4 GiB.  Accelerate's default CPU
        # offload hook would temporarily copy that entire frozen table to CUDA
        # during training.  Look up the frozen rows on CPU and move only the
        # small per-token result to the decoder instead.
        if offloaded_per_layer_weight is None:
            return original_get_per_layer_inputs(input_ids, inputs_embeds).to(inputs_embeds.device)
        with torch.no_grad():
            per_layer_inputs = torch.nn.functional.embedding(
                input_ids.to("cpu"),
                offloaded_per_layer_weight,
            )
        return per_layer_inputs.reshape(
            *input_ids.shape,
            text_config.num_hidden_layers,
            text_config.hidden_size_per_layer_input,
        ).to(inputs_embeds.device)

    model.model.get_per_layer_inputs = get_per_layer_inputs_on_execution_device
    model.config.use_cache = False
    tokenizer = AutoProcessor.from_pretrained(base_path, local_files_only=True).tokenizer
    tokenizer.pad_token = tokenizer.pad_token or tokenizer.eos_token
    return tokenizer, model, loading_info, device_map


def _read_encoded_rows(path: Path, tokenizer: Any, max_length: int) -> list[EncodedGemmaResponse]:
    rows: list[EncodedGemmaResponse] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        messages = tuple(GemmaMessage(message["role"], message["content"]) for message in row["messages"])
        rows.append(encode_gemma_response(tokenizer, messages, row["response"], max_length))
    if not rows:
        raise ValueError(f"Dataset split is empty: {path}")
    return rows


def _validation_loss(model: Any, validation_rows: list[EncodedGemmaResponse], collator: GemmaResponseCollator) -> float:
    model.eval()
    losses: list[float] = []
    with torch.inference_mode():
        for row in validation_rows:
            batch = {key: value.to(DEVICE) for key, value in collator([row]).items()}
            losses.append(float(model(**batch).loss))
    model.train()
    return sum(losses) / len(losses) if losses else float("inf")


def write_adapter_manifest(
    adapter_dir: str | Path,
    *,
    config: Gemma4TrainingConfig,
    dataset_sha256: str,
    best_validation_loss: float,
    max_steps: int,
    optimizer_updates: int,
    peak_vram_mb: float,
    physical_peak_mib: float | None,
    physical_cap_passed: bool | None,
) -> dict[str, object]:
    manifest = {
        "backend": config.backend,
        "profile_name": config.profile_name,
        "base_model_id": config.base_model_id,
        "base_model_path": str(Path(config.base_model_path)),
        "dataset_sha256": dataset_sha256,
        "best_validation_loss": best_validation_loss,
        "peak_vram_mb": peak_vram_mb,
        "physical_peak_mib": physical_peak_mib,
        "physical_cap_mib": config.physical_vram_cap_mib,
        "physical_cap_passed": physical_cap_passed,
        "seed": config.seed,
        "max_length": config.max_length,
        "max_steps": max_steps,
        "optimizer_updates": optimizer_updates,
        "lora_rank": config.lora_rank,
        "lora_alpha": config.lora_alpha,
        "target_modules": list(config.target_modules),
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    destination = Path(adapter_dir)
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "ru_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return manifest


def train_gemma4_adapter(
    config: Gemma4TrainingConfig,
    *,
    max_steps_override: int | None = None,
    output_dir_override: str | Path | None = None,
) -> dict[str, object]:
    config.validate()
    if not torch.cuda.is_available():
        raise RuntimeError("Gemma QLoRA training requires CUDA")

    base_path = Path(config.base_model_path)
    dataset_dir = Path(config.dataset_dir)
    adapter_dir = validate_gemma4_adapter_dir(output_dir_override or config.adapter_dir)
    if not base_path.is_dir():
        raise FileNotFoundError(f"Model path {base_path} not found")
    train_path = dataset_dir / "train.jsonl"
    validation_path = dataset_dir / "val.jsonl"
    manifest_path = dataset_dir / "manifest.json"
    if not train_path.is_file() or not validation_path.is_file() or not manifest_path.is_file():
        raise FileNotFoundError("Ru training, validation, or manifest data is missing")
    dataset_sha256 = sha256(manifest_path.read_bytes()).hexdigest()

    monitor = (
        PhysicalVramMonitor(config.physical_vram_cap_mib, config.physical_vram_poll_seconds)
        if config.physical_vram_cap_mib is not None
        else None
    )
    if monitor is not None:
        monitor.start()

    try:
        from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training

        random.seed(config.seed)
        torch.manual_seed(config.seed)
        torch.cuda.manual_seed_all(config.seed)
        tokenizer, model, loading_info, device_map = _load_gemma_text_model(
            base_path,
            config.quantization_bits,
        )
        if monitor is not None and monitor.exceeded:
            raise RuntimeError("physical VRAM cap exceeded while loading Gemma")

        train_rows = _read_encoded_rows(train_path, tokenizer, config.max_length)
        validation_rows = _read_encoded_rows(validation_path, tokenizer, config.max_length)
        model = prepare_model_for_kbit_training(
            model,
            use_gradient_checkpointing=True,
            gradient_checkpointing_kwargs={"use_reentrant": False},
        )
        model = get_peft_model(
            model,
            LoraConfig(
                r=config.lora_rank,
                lora_alpha=config.lora_alpha,
                lora_dropout=config.lora_dropout,
                target_modules=list(config.target_modules),
                bias="none",
                task_type="CAUSAL_LM",
            ),
        )
        model.print_trainable_parameters()
        collator = GemmaResponseCollator(tokenizer.pad_token_id)
        trainable_parameters = [parameter for parameter in model.parameters() if parameter.requires_grad]
        optimizer = torch.optim.AdamW(trainable_parameters, lr=config.learning_rate)
        random.Random(config.seed).shuffle(train_rows)
        limit = max_steps_override if max_steps_override is not None else config.max_steps
        if limit <= 0:
            raise ValueError("max_steps must be positive")

        model.train()
        optimizer.zero_grad(set_to_none=True)
        best_loss = float("inf")
        optimizer_updates = 0
        for step in range(limit):
            if monitor is not None and monitor.exceeded:
                raise RuntimeError("physical VRAM cap exceeded")
            batch = collator([train_rows[step % len(train_rows)]])
            batch = {key: value.to(DEVICE) for key, value in batch.items()}
            loss = model(**batch).loss / config.gradient_accumulation_steps
            loss.backward()
            if (step + 1) % config.gradient_accumulation_steps == 0 or step + 1 == limit:
                optimizer.step()
                optimizer.zero_grad(set_to_none=True)
                optimizer_updates += 1
                candidate_loss = _validation_loss(model, validation_rows, collator)
                if monitor is not None and monitor.exceeded:
                    raise RuntimeError("physical VRAM cap exceeded")
                if candidate_loss < best_loss:
                    best_loss = candidate_loss
                print(
                    f"step={step + 1}/{limit} optimizer_updates={optimizer_updates} "
                    f"train_loss={float(loss.detach()) * config.gradient_accumulation_steps:.4f} "
                    f"val_loss={candidate_loss:.4f}",
                    flush=True,
                )

        if monitor is not None and monitor.exceeded:
            raise RuntimeError("physical VRAM cap exceeded")
        adapter_dir.mkdir(parents=True, exist_ok=True)
        model.save_pretrained(adapter_dir)
        tokenizer.save_pretrained(adapter_dir)
        physical_peak_mib = monitor.peak_mib if monitor is not None else None
        physical_cap_passed = None
        if monitor is not None:
            if physical_peak_mib is None:
                raise RuntimeError("physical VRAM measurement unavailable")
            if monitor.exceeded or physical_peak_mib > config.physical_vram_cap_mib:
                raise RuntimeError("physical VRAM cap exceeded")
            physical_cap_passed = True
        return write_adapter_manifest(
            adapter_dir,
            config=config,
            dataset_sha256=dataset_sha256,
            best_validation_loss=best_loss,
            max_steps=limit,
            optimizer_updates=optimizer_updates,
            peak_vram_mb=torch.cuda.max_memory_allocated() / (1024 * 1024),
            physical_peak_mib=physical_peak_mib,
            physical_cap_passed=physical_cap_passed,
        ) | {
            "missing_key_count": len(loading_info.get("missing_keys", [])),
            "unexpected_key_count": len(loading_info.get("unexpected_keys", [])),
            "device_map": device_map,
        }
    finally:
        if monitor is not None:
            monitor.stop()
