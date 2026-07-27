from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path


MIN_FREE_BYTES = 24 * 1024**3
PROJECT_ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class ConversionPaths:
    root: Path
    source_dir: Path
    binaries_dir: Path
    base_model: Path
    adapter_dir: Path
    temporary_base: Path
    base_output: Path
    adapter_output: Path


def conversion_paths(root: Path) -> ConversionPaths:
    return ConversionPaths(
        root=root,
        source_dir=root / "tools" / "llama.cpp",
        binaries_dir=root / "tools" / "llama.cpp-bin",
        base_model=root / "models" / "llama3.1-8b-instruct-hf",
        adapter_dir=root / "checkpoints" / "ru-llama3.1-8b-instruct-v6",
        temporary_base=root / ".tmp" / "ru-v6-gguf-base-f16.gguf",
        base_output=root / "models" / "gguf" / "llama3.1-8b-instruct-q4_k_m.gguf",
        adapter_output=root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6-lora.gguf",
    )


def ensure_free_space(root: Path, *, free_bytes: int | None = None) -> None:
    available = free_bytes if free_bytes is not None else shutil.disk_usage(root).free
    if available < MIN_FREE_BYTES:
        raise RuntimeError(
            "GGUF conversion needs at least 24 GiB free; "
            f"found {available / 1024**3:.2f} GiB."
        )


def build_commands(root: Path, python_executable: str) -> tuple[tuple[str, ...], ...]:
    paths = conversion_paths(root)
    return (
        (
            python_executable,
            str(paths.source_dir / "convert_hf_to_gguf.py"),
            str(paths.base_model),
            "--outtype",
            "f16",
            "--outfile",
            str(paths.temporary_base),
        ),
        (
            str(paths.binaries_dir / "llama-quantize.exe"),
            str(paths.temporary_base),
            str(paths.base_output),
            "Q4_K_M",
        ),
        (
            python_executable,
            str(paths.source_dir / "convert_lora_to_gguf.py"),
            "--base",
            str(paths.base_model),
            "--outtype",
            "f16",
            "--outfile",
            str(paths.adapter_output),
            str(paths.adapter_dir),
        ),
    )


def _require_directory(path: Path, label: str) -> None:
    if not path.is_dir():
        raise FileNotFoundError(f"{label} directory not found: {path}")


def _require_file(path: Path, label: str) -> None:
    if not path.is_file():
        raise FileNotFoundError(f"{label} file not found: {path}")


def validate_paths(paths: ConversionPaths) -> None:
    _require_directory(paths.base_model, "base model")
    _require_directory(paths.adapter_dir, "v6 adapter")
    _require_file(paths.source_dir / "convert_hf_to_gguf.py", "llama.cpp base converter")
    _require_file(paths.source_dir / "convert_lora_to_gguf.py", "llama.cpp LoRA converter")
    _require_file(paths.binaries_dir / "llama-quantize.exe", "llama.cpp quantizer")


def _verify_output(path: Path, label: str) -> None:
    if not path.is_file() or path.stat().st_size == 0:
        raise RuntimeError(f"{label} GGUF was not created: {path}")


@contextmanager
def conversion_lock(root: Path):
    lock_path = root / ".tmp" / "ru-v6-gguf-conversion.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    acquired = False
    try:
        with lock_path.open("x", encoding="utf-8") as lock_file:
            acquired = True
            lock_file.write(str(os.getpid()))
            yield
    except FileExistsError as error:
        raise RuntimeError(
            f"Another Ru v6 GGUF conversion is already running or left a lock: {lock_path}"
        ) from error
    finally:
        if acquired:
            lock_path.unlink(missing_ok=True)


def convert(root: Path, *, python_executable: str = sys.executable) -> ConversionPaths:
    paths = conversion_paths(root)
    with conversion_lock(root):
        ensure_free_space(root)
        validate_paths(paths)
        if paths.base_output.exists() or paths.adapter_output.exists():
            raise FileExistsError(
                "Refusing to overwrite an existing GGUF candidate. "
                f"Remove or rename {paths.base_output.parent} only after reviewing it."
            )

        paths.temporary_base.parent.mkdir(parents=True, exist_ok=True)
        paths.base_output.parent.mkdir(parents=True, exist_ok=True)
        try:
            for command in build_commands(root, python_executable):
                print("Running:", subprocess.list2cmdline(command), flush=True)
                subprocess.run(command, cwd=root, check=True)
            _verify_output(paths.base_output, "base")
            _verify_output(paths.adapter_output, "adapter")
        except BaseException:
            raise
        else:
            paths.temporary_base.unlink(missing_ok=True)
    return paths


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Ru v6 base and LoRA GGUF files with llama.cpp")
    parser.add_argument("--dry-run", action="store_true", help="validate setup and print conversion commands")
    args = parser.parse_args()

    paths = conversion_paths(PROJECT_ROOT)
    ensure_free_space(PROJECT_ROOT)
    validate_paths(paths)
    if args.dry_run:
        for command in build_commands(PROJECT_ROOT, sys.executable):
            print(subprocess.list2cmdline(command))
        return 0

    result = convert(PROJECT_ROOT)
    print(f"Created base GGUF: {result.base_output}")
    print(f"Created LoRA GGUF: {result.adapter_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
