from __future__ import annotations

import json
import hashlib
import importlib.metadata
import os
import random
import shutil
import subprocess
import tempfile
import threading
import time
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal
import torch

from ._text_utils import normalize_text

IGNORE_LABEL = -100

@dataclass(frozen=True)
class LlamaMessage:
    role: Literal["system", "user", "assistant"]
    content: str

    def __post_init__(self) -> None:
        if self.role not in {"system", "user", "assistant"}:
            raise ValueError("Role must be system, user, or assistant")
        if not normalize_text(self.content):
            raise ValueError("Message content cannot be empty")

@dataclass(frozen=True)
class EncodedLlamaResponse:
    input_ids: list[int]
    labels: list[int]


def read_nvidia_smi_memory_mib() -> float | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.used,memory.total",
                "--format=csv,noheader,nounits",
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
        fields = [field.strip() for field in stripped.split(",")]
        if len(fields) < 2:
            continue
        try:
            used = float(fields[0])
            total = float(fields[1])
        except ValueError:
            continue
        if used > total:
            continue
        values.append(used)
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
        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return
            # A previous aborted run must not poison the next one.
            self._stop.clear()
            self._exceeded.clear()
            self._peak_mib = None
            self._thread = threading.Thread(target=self._poll, daemon=True, name="ru-vram-monitor")
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


_CHECKPOINT_ROOT = (Path(__file__).resolve().parents[2] / "checkpoints").resolve()
_V6_1_OUTPUT_NAMES = frozenset(
    {
        "ru-llama3.1-8b-instruct-v6.1",
        "ru-llama3.1-8b-instruct-v6.1-smoke",
    }
)
_V6_1_SOURCE_NAME = "ru-llama3.1-8b-instruct-v6"


def validate_v6_1_adapter_dir(path: str | Path) -> Path:
    adapter_dir = Path(path)
    if adapter_dir.name not in _V6_1_OUTPUT_NAMES:
        raise ValueError(
            "v6.1 output must use ru-llama3.1-8b-instruct-v6.1 or "
            "ru-llama3.1-8b-instruct-v6.1-smoke"
        )
    if adapter_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError(f"v6.1 output must be directly inside the {_CHECKPOINT_ROOT} directory")
    return adapter_dir


def validate_v6_1_source_adapter_dir(path: str | Path) -> Path:
    source_dir = Path(path)
    if source_dir.name != _V6_1_SOURCE_NAME or source_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError(f"v6.1 source adapter must be the v6 adapter inside {_CHECKPOINT_ROOT}")
    return source_dir

class LlamaResponseCollator:
    def __init__(self, pad_token_id: int) -> None:
        self.pad_token_id = pad_token_id

    def __call__(self, examples: list[EncodedLlamaResponse]) -> dict[str, torch.Tensor]:
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

def encode_llama_response(
    tokenizer: Any,
    messages: tuple[LlamaMessage, ...],
    response: str,
    max_length: int,
) -> EncodedLlamaResponse:
    if max_length <= 0:
        raise ValueError("max_length must be positive")
    if not messages or messages[-1].role != "user":
        raise ValueError("Context must end with a user message")
    response = normalize_text(response)
    if not response:
        raise ValueError("Response cannot be empty")

    prompt_ids = tokenizer.apply_chat_template(
        [asdict(message) for message in messages],
        tokenize=True,
        add_generation_prompt=True,
    )
    if hasattr(prompt_ids, "__contains__") and "input_ids" in prompt_ids:
        prompt_ids = prompt_ids["input_ids"]
    elif hasattr(prompt_ids, "ids"):
        prompt_ids = prompt_ids.ids
    
    # Llama 3 specific: response ends with <|eot_id|>
    eot_token_id = tokenizer.convert_tokens_to_ids("<|eot_id|>")
    if eot_token_id is None or eot_token_id == tokenizer.unk_token_id:
        eot_token_id = tokenizer.eos_token_id
        
    response_ids = [*tokenizer(response, add_special_tokens=False)["input_ids"], eot_token_id]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)) :]
    return EncodedLlamaResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
    )

@dataclass(frozen=True)
class LlamaTrainingConfig:
    base_model_id: str
    base_model_path: str
    source_adapter_dir: str | None = None
    dataset_dir: str = "data/conversations/ru_v3"
    adapter_dir: str = "checkpoints/ru-llama3.1-8b-instruct"
    max_length: int = 512
    seed: int = 42
    epochs: int = 1
    max_steps: int | None = None
    learning_rate: float = 5e-5
    batch_size: int = 1
    gradient_accumulation_steps: int = 8
    evaluation_steps: int = 4
    validation_max_examples: int | None = None
    save_steps: int = 4
    logging_steps: int = 1
    lora_rank: int = 8
    lora_alpha: int = 16
    lora_dropout: float = 0.05
    target_modules: tuple[str, ...] = ("q_proj", "k_proj", "v_proj", "o_proj")
    backend: str = "llama3.1-qlora"
    profile_name: str = "ru_llama3_1"
    physical_vram_cap_mib: float | None = None
    physical_vram_poll_seconds: float = 0.5
    wall_clock_seconds: float | None = None
    training_status: Literal["not_started", "approved"] = "not_started"

    @classmethod
    def from_json(cls, path: str | Path) -> "LlamaTrainingConfig":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("Training config must be an object")
        if "target_modules" in payload:
            payload["target_modules"] = tuple(payload["target_modules"])
        return cls(**payload)


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def training_dataset_fingerprint(dataset_dir: str | Path) -> dict[str, object]:
    directory = Path(dataset_dir)
    names = ("train.jsonl", "val.jsonl", "manifest.json")
    files = {}
    for name in names:
        path = directory / name
        if not path.is_file():
            raise FileNotFoundError(f"Training dataset input is missing: {path}")
        files[name] = _sha256_file(path)
    canonical = json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return {"files": files, "aggregate_sha256": hashlib.sha256(canonical).hexdigest()}


def directory_fingerprint(directory: str | Path) -> str:
    root = Path(directory)
    if not root.is_dir():
        raise FileNotFoundError(f"Fingerprint directory is missing: {root}")
    digest = hashlib.sha256()
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest.update(path.relative_to(root).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(_sha256_file(path).encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def set_training_seed(seed: int) -> None:
    import numpy as np

    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def resolve_training_step_limit(
    config: LlamaTrainingConfig,
    *,
    train_row_count: int,
    cli_max_steps: int | None = None,
) -> int:
    if train_row_count <= 0:
        raise ValueError("training data cannot be empty")
    limit = cli_max_steps if cli_max_steps is not None else config.max_steps
    if limit is None:
        limit = train_row_count * config.epochs
    if limit <= 0:
        raise ValueError("max_steps must be positive")
    return limit


def select_validation_rows(
    rows: list[EncodedLlamaResponse],
    *,
    max_examples: int | None,
    seed: int,
) -> list[EncodedLlamaResponse]:
    if max_examples is None or max_examples >= len(rows):
        return rows
    if max_examples <= 0:
        raise ValueError("validation_max_examples must be positive")
    selected_indexes = sorted(random.Random(seed).sample(range(len(rows)), max_examples))
    return [rows[index] for index in selected_indexes]

def write_adapter_manifest(
    adapter_dir: str | Path,
    *,
    backend: str,
    profile_name: str,
    base_model_id: str,
    base_model_path: str,
    source_adapter_dir: str | None = None,
    dataset_sha256: str,
    dataset_files_sha256: dict[str, str],
    training_config_sha256: str,
    base_model_sha256: str,
    source_adapter_sha256: str | None,
    best_validation_loss: float,
    peak_vram_mb: float,
    seed: int,
    max_length: int,
    optimizer_updates: int = 0,
    physical_peak_mib: float | None = None,
    physical_cap_mib: float | None = None,
    physical_cap_passed: bool | None = None,
    training_steps: int | None = None,
    validation_examples: int | None = None,
    elapsed_seconds: float | None = None,
    training_status: str = "completed",
    wall_clock_seconds: float | None = None,
    aborted_reason: str | None = None,
) -> dict[str, object]:
    destination = Path(adapter_dir)
    project_root = Path(__file__).resolve().parents[2]
    try:
        git_commit = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_root,
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        git_commit = None
    packages = {}
    for package in ("torch", "transformers", "peft", "bitsandbytes", "numpy"):
        try:
            packages[package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            packages[package] = None
    manifest = {
        "adapter_dir": str(destination),
        "backend": backend,
        "profile_name": profile_name,
        "base_model_id": base_model_id,
        "base_model_path": base_model_path,
        "source_adapter_dir": source_adapter_dir,
        "dataset_sha256": dataset_sha256,
        "dataset_files_sha256": dataset_files_sha256,
        "training_config_sha256": training_config_sha256,
        "base_model_sha256": base_model_sha256,
        "source_adapter_sha256": source_adapter_sha256,
        "best_validation_loss": best_validation_loss,
        "peak_vram_mb": peak_vram_mb,
        "physical_peak_mib": physical_peak_mib,
        "physical_cap_mib": physical_cap_mib,
        "physical_cap_passed": physical_cap_passed,
        "seed": seed,
        "max_length": max_length,
        "optimizer_updates": optimizer_updates,
        "training_steps": training_steps,
        "validation_examples": validation_examples,
        "elapsed_seconds": elapsed_seconds,
        "training_status": training_status,
        "wall_clock_seconds": wall_clock_seconds,
        "aborted_reason": aborted_reason,
        "git_commit": git_commit,
        "package_versions": packages,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    destination.mkdir(parents=True, exist_ok=True)
    manifest_path = destination / "ru_manifest.json"
    temp_path = manifest_path.with_suffix(".json.tmp")
    temp_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    os.replace(temp_path, manifest_path)
    return manifest


def _is_cuda_oom(error: Exception) -> bool:
    try:
        torch_oom = torch.OutOfMemoryError
    except AttributeError:
        torch_oom = torch.cuda.OutOfMemoryError
    return isinstance(error, (torch.cuda.OutOfMemoryError, torch_oom))


def _save_adapter_atomically(model: object, destination: Path) -> None:
    # save_pretrained() writes in place; a crash mid-write leaves a corrupt
    # adapter that later fails to load. Save into a staging directory and
    # swap it in with a rename so the destination is always complete.
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".adapter-staging-", dir=destination.parent))
    try:
        model.save_pretrained(staging)
        backup = destination.with_name(destination.name + ".prev")
        if destination.exists():
            if backup.exists():
                shutil.rmtree(backup, ignore_errors=True)
            destination.rename(backup)
        staging.rename(destination)
        if backup.exists():
            shutil.rmtree(backup, ignore_errors=True)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise

def train_ru_adapter_llama(config: LlamaTrainingConfig, max_steps: int | None = None) -> dict[str, object]:
    if config.training_status != "approved":
        raise RuntimeError(
            "Training config is not approved. Set training_status to 'approved' only after the manual quality gate."
        )
    import torch
    from peft import LoraConfig, PeftModel, get_peft_model, prepare_model_for_kbit_training
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig, modeling_utils

    if not torch.cuda.is_available():
        raise RuntimeError("QLoRA training requires CUDA")
    set_training_seed(config.seed)
    project_root = Path(__file__).resolve().parents[2]

    def resolve_project_path(value: str) -> Path:
        path = Path(value)
        return path if path.is_absolute() else project_root / path

    base_path = resolve_project_path(config.base_model_path)
    if not base_path.is_dir():
        raise FileNotFoundError(f"Model path {base_path} not found")
        
    dataset_dir = resolve_project_path(config.dataset_dir)
    train_path, validation_path, manifest_path = dataset_dir / "train.jsonl", dataset_dir / "val.jsonl", dataset_dir / "manifest.json"
    if not train_path.is_file() or not validation_path.is_file() or not manifest_path.is_file():
        raise FileNotFoundError("Ru training, validation, or manifest data is missing")
    dataset_fingerprint = training_dataset_fingerprint(dataset_dir)
    dataset_sha256 = str(dataset_fingerprint["aggregate_sha256"])
    config_sha256 = hashlib.sha256(
        json.dumps(asdict(config), sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    base_model_sha256 = directory_fingerprint(base_path)
    
    tokenizer = AutoTokenizer.from_pretrained(base_path)
    if tokenizer.pad_token_id is None:
        pad_id = tokenizer.convert_tokens_to_ids("<|finetune_right_pad_id|>")
        tokenizer.pad_token_id = pad_id if pad_id != tokenizer.unk_token_id else tokenizer.eos_token_id
        
    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    # Transformers 5 warms CUDA's allocator with a multi-gigabyte temporary
    # buffer before loading. That preallocation OOMs on Ru's 6 GiB GPU even
    # though the 4-bit model itself fits, so omit only that load-time shortcut.
    allocator_warmup = modeling_utils.caching_allocator_warmup
    modeling_utils.caching_allocator_warmup = lambda *args, **kwargs: None
    try:
        model = AutoModelForCausalLM.from_pretrained(
            base_path,
            quantization_config=quantization,
            torch_dtype=torch.float16,
            device_map="cuda",
        )
    finally:
        modeling_utils.caching_allocator_warmup = allocator_warmup
    
    model.config.use_cache = False
    
    # CRITICAL: Enable gradient checkpointing to save VRAM
    model.gradient_checkpointing_enable()
    
    model = prepare_model_for_kbit_training(model)
    if config.source_adapter_dir is not None:
        source_adapter = resolve_project_path(config.source_adapter_dir)
        if not source_adapter.is_dir():
            raise FileNotFoundError(f"Source adapter {source_adapter} not found")
        model = PeftModel.from_pretrained(model, source_adapter, is_trainable=True)
    else:
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

    def encode(path: Path):
        rows: list[EncodedLlamaResponse] = []
        for line_number, line in enumerate(
            path.read_text(encoding="utf-8").splitlines(), start=1
        ):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"Invalid JSON in {path} at line {line_number}: {error}") from error
            rows.append(
                encode_llama_response(
                    tokenizer,
                    tuple(
                        LlamaMessage(message["role"], message["content"])
                        for message in row["messages"]
                    ),
                    row["response"],
                    config.max_length,
                )
            )
        return rows

    train_rows, validation_rows = encode(train_path), encode(validation_path)
    collator = LlamaResponseCollator(tokenizer.pad_token_id)
    optimizer = torch.optim.AdamW((parameter for parameter in model.parameters() if parameter.requires_grad), lr=config.learning_rate)
    random.Random(config.seed).shuffle(train_rows)
    
    limit = resolve_training_step_limit(
        config,
        train_row_count=len(train_rows),
        cli_max_steps=max_steps,
    )
    if config.gradient_accumulation_steps <= 0:
        raise ValueError("gradient_accumulation_steps must be positive")
    if config.evaluation_steps <= 0:
        raise ValueError("evaluation_steps must be positive")
    if config.logging_steps <= 0:
        raise ValueError("logging_steps must be positive")
    validation_rows = select_validation_rows(
        validation_rows,
        max_examples=config.validation_max_examples,
        seed=config.seed,
    )
    if not validation_rows:
        raise ValueError("validation data cannot be empty")
    print(
        "[train] "
        f"steps={limit} train_rows={len(train_rows)} "
        f"validation_rows={len(validation_rows)}"
    )
        
    def validation_loss() -> float:
        model.eval()
        losses = []
        with torch.inference_mode():
            for row in validation_rows:
                batch = {key: value.to("cuda") for key, value in collator([row]).items()}
                losses.append(float(model(**batch).loss))
        model.train()
        return sum(losses) / len(losses) if losses else float('inf')

    adapter_dir = resolve_project_path(config.adapter_dir)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    best_loss = float("inf")
    optimizer_updates = 0
    training_started = time.monotonic()
    timed_out = False
    executed_steps = 0
    aborted_reason: str | None = None
    monitor = (
        PhysicalVramMonitor(config.physical_vram_cap_mib, config.physical_vram_poll_seconds)
        if config.physical_vram_cap_mib is not None
        else None
    )
    if monitor is not None:
        monitor.start()

    try:
        for step in range(limit):
            # Poll the VRAM monitor periodically instead of only before the
            # first step so an overshoot aborts mid-run.
            if step % 5 == 0 and monitor is not None and monitor.exceeded:
                aborted_reason = f"physical VRAM cap exceeded (peak_mib={monitor.peak_mib})"
                break
            if config.wall_clock_seconds is not None and time.monotonic() - training_started >= config.wall_clock_seconds:
                timed_out = True
                break
            try:
                batch = collator([train_rows[step % len(train_rows)]])
                executed_steps += 1
                batch = {key: value.to("cuda") for key, value in batch.items()}
                loss = model(**batch).loss / config.gradient_accumulation_steps
                loss.backward()
            except Exception as error:
                if not _is_cuda_oom(error):
                    raise
                aborted_reason = f"CUDA out of memory during step {step + 1}: {error}"
                optimizer.zero_grad(set_to_none=True)
                del model
                torch.cuda.empty_cache()
                break
            if (step + 1) % config.gradient_accumulation_steps == 0 or step + 1 == limit:
                optimizer.step()
                optimizer.zero_grad(set_to_none=True)
                optimizer_updates += 1
                if optimizer_updates % config.logging_steps == 0:
                    elapsed = time.monotonic() - training_started
                    print(
                        "[train] "
                        f"step={step + 1}/{limit} update={optimizer_updates} "
                        f"loss={float(loss) * config.gradient_accumulation_steps:.4f} "
                        f"elapsed_s={elapsed:.1f}"
                    )
                if optimizer_updates % config.evaluation_steps == 0 or step + 1 == limit:
                    try:
                        candidate_loss = validation_loss()
                    except Exception as error:
                        if not _is_cuda_oom(error):
                            raise
                        aborted_reason = f"CUDA out of memory during validation: {error}"
                        optimizer.zero_grad(set_to_none=True)
                        del model
                        torch.cuda.empty_cache()
                        break
                    print(
                        "[validation] "
                        f"update={optimizer_updates} rows={len(validation_rows)} "
                        f"loss={candidate_loss:.4f}"
                    )
                    if monitor is not None and monitor.exceeded:
                        aborted_reason = f"physical VRAM cap exceeded (peak_mib={monitor.peak_mib})"
                        break
                    if candidate_loss < best_loss:
                        best_loss = candidate_loss
                        _save_adapter_atomically(model, adapter_dir)
    finally:
        if monitor is not None:
            monitor.stop()

    physical_peak_mib = monitor.peak_mib if monitor is not None else None
    physical_cap_passed = None
    if monitor is not None:
        if physical_peak_mib is None:
            # The allocator's reserved pool can exceed the physical device
            # capacity on some CUDA/driver combinations.  When nvidia-smi
            # produced no trustworthy sample, use actual tensor allocation as
            # the conservative fallback instead of the virtual reserved pool.
            physical_peak_mib = torch.cuda.max_memory_allocated() / (1024 * 1024)
        if aborted_reason is None and monitor.exceeded:
            aborted_reason = f"physical VRAM cap exceeded (peak_mib={physical_peak_mib})"
        if aborted_reason is None and physical_peak_mib > config.physical_vram_cap_mib:
            aborted_reason = f"physical VRAM cap exceeded (peak_mib={physical_peak_mib})"
        if aborted_reason is None:
            physical_cap_passed = True

    peak_vram_mb = torch.cuda.max_memory_allocated() / (1024 * 1024)
    elapsed_seconds = time.monotonic() - training_started
    # Write the manifest even for aborted runs so the checkpoint directory is
    # loadable and the failure reason is recorded for the next run.
    manifest = write_adapter_manifest(
        adapter_dir,
        backend=config.backend,
        profile_name=config.profile_name,
        base_model_id=config.base_model_id,
        base_model_path=str(base_path),
        source_adapter_dir=config.source_adapter_dir,
        dataset_sha256=dataset_sha256,
        dataset_files_sha256=dict(dataset_fingerprint["files"]),
        training_config_sha256=config_sha256,
        base_model_sha256=base_model_sha256,
        source_adapter_sha256=(
            directory_fingerprint(resolve_project_path(config.source_adapter_dir))
            if config.source_adapter_dir is not None
            else None
        ),
        best_validation_loss=best_loss,
        peak_vram_mb=peak_vram_mb,
        physical_peak_mib=physical_peak_mib,
        physical_cap_mib=config.physical_vram_cap_mib,
        physical_cap_passed=physical_cap_passed,
        seed=config.seed,
        max_length=config.max_length,
        optimizer_updates=optimizer_updates,
        training_steps=optimizer_updates,
        validation_examples=len(validation_rows),
        elapsed_seconds=elapsed_seconds,
        training_status="incomplete" if aborted_reason else ("timed_out" if timed_out else "completed"),
        wall_clock_seconds=config.wall_clock_seconds,
        aborted_reason=aborted_reason,
    )
    if aborted_reason is not None:
        raise RuntimeError(aborted_reason)
    return manifest
