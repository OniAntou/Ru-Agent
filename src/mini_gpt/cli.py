from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import torch

from .chat_finetune_data import prepare_chat_corpus
from .checkpointing import load_checkpoint
from .config import ModelConfig, TrainingConfig
from .data import TokenWindowDataset, encode_split, prepare_corpus
from .evaluate import evaluate_language_losses, generate
from .model import MiniGPT
from .tokenizer import load_tokenizer, train_tokenizer
from .train import run_training


PROJECT_ROOT = Path(__file__).resolve().parents[2]


def _path(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else PROJECT_ROOT / path


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Train a bilingual mini-GPT from scratch")
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare = subparsers.add_parser("prepare", help="clean and split UTF-8 text files")
    prepare.add_argument("--raw-dir", default="data/raw")
    prepare.add_argument("--processed-dir", default="data/processed")
    prepare.add_argument("--validation-ratio", type=float, default=0.1)

    prepare_chat = subparsers.add_parser("prepare-chat", help="prepare approved chat examples for fine-tuning")
    prepare_chat.add_argument("--input", default="data/conversations/approved.jsonl")
    prepare_chat.add_argument("--output-dir", default="data/conversations/processed")
    prepare_chat.add_argument("--validation-ratio", type=float, default=0.1)

    tokenizer = subparsers.add_parser("tokenizer", help="train a byte-level BPE tokenizer")
    tokenizer.add_argument("--text-dir", default="data/processed")
    tokenizer.add_argument("--output", default="data/tokenizer.json")
    tokenizer.add_argument("--vocab-size", type=int, default=16_384)

    encode = subparsers.add_parser("encode", help="encode a prepared text split")
    encode.add_argument("--text", required=True)
    encode.add_argument("--tokenizer", default="data/tokenizer.json")
    encode.add_argument("--output", required=True)

    train = subparsers.add_parser("train", help="train or resume a model")
    train.add_argument("--config", required=True)
    train.add_argument("--processed-dir", default="data/processed")
    train.add_argument("--tokenizer", default="data/tokenizer.json")
    train.add_argument("--checkpoint-dir", default="checkpoints")
    train_start = train.add_mutually_exclusive_group()
    train_start.add_argument("--resume")
    train_start.add_argument("--init-checkpoint")

    generate_parser = subparsers.add_parser("generate", help="generate text from a checkpoint")
    generate_parser.add_argument("--checkpoint", default="checkpoints/latest.pt")
    generate_parser.add_argument("--tokenizer", default="data/tokenizer.json")
    generate_parser.add_argument("--prompt", required=True)
    generate_parser.add_argument("--max-new-tokens", type=int, default=50)
    generate_parser.add_argument("--temperature", type=float, default=0.8)
    generate_parser.add_argument("--top-k", type=int, default=50)
    generate_parser.add_argument("--device", default="auto", choices=["auto", "cpu", "cuda"])

    evaluate = subparsers.add_parser("evaluate", help="measure validation loss by language")
    evaluate.add_argument("--checkpoint", default="checkpoints/latest.pt")
    evaluate.add_argument("--tokenizer", default="data/tokenizer.json")
    evaluate.add_argument("--validation", default="data/processed/val.jsonl")
    evaluate.add_argument("--device", default="auto", choices=["auto", "cpu", "cuda"])
    return parser


def _load_config(path: Path, tokenizer_vocab_size: int) -> tuple[ModelConfig, TrainingConfig]:
    values = json.loads(path.read_text(encoding="utf-8"))
    model_values = dict(values["model"])
    model_config = ModelConfig.from_dict(model_values, vocab_size=tokenizer_vocab_size)
    training_config = TrainingConfig.from_dict(values["training"])
    return model_config, training_config


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = _build_parser().parse_args(argv)
    if args.command == "prepare":
        report = prepare_corpus(_path(args.raw_dir), _path(args.processed_dir), args.validation_ratio)
        print(json.dumps(report.__dict__, ensure_ascii=False, indent=2))
        return 0
    if args.command == "prepare-chat":
        report = prepare_chat_corpus(_path(args.input), _path(args.output_dir), args.validation_ratio)
        print(json.dumps(report.__dict__, ensure_ascii=False, indent=2))
        return 0
    if args.command == "tokenizer":
        text_files = sorted(_path(args.text_dir).glob("*.txt"))
        actual_size = train_tokenizer(text_files, _path(args.output), args.vocab_size)
        print(f"saved tokenizer with vocab_size={actual_size}")
        return 0
    if args.command == "encode":
        count = encode_split(_path(args.text), _path(args.tokenizer), _path(args.output))
        print(f"saved {count} tokens to {_path(args.output)}")
        return 0
    if args.command == "train":
        tokenizer = load_tokenizer(_path(args.tokenizer))
        model_config, training_config = _load_config(_path(args.config), tokenizer.get_vocab_size())
        processed = _path(args.processed_dir)
        train_dataset = TokenWindowDataset(processed / "train.bin", model_config.context_length)
        validation_dataset = TokenWindowDataset(processed / "val.bin", model_config.context_length)
        result = run_training(
            model_config,
            training_config,
            train_dataset,
            validation_dataset,
            _path(args.checkpoint_dir),
            _path(args.resume) if args.resume else None,
            _path(args.init_checkpoint) if args.init_checkpoint else None,
        )
        print(json.dumps(result, indent=2))
        return 0
    if args.command == "generate":
        tokenizer = load_tokenizer(_path(args.tokenizer))
        selected_device = "cuda" if args.device == "auto" and torch.cuda.is_available() else "cpu" if args.device == "auto" else args.device
        device = torch.device(selected_device)
        if device.type == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested but is unavailable")
        try:
            payload = torch.load(_path(args.checkpoint), map_location=device, weights_only=False)
        except TypeError:
            payload = torch.load(_path(args.checkpoint), map_location=device)
        model_config = ModelConfig.from_dict(payload["model_config"])
        model = MiniGPT(model_config).to(device)
        load_checkpoint(_path(args.checkpoint), model, device=device, restore_rng=False)
        print(generate(model, tokenizer, args.prompt, device, args.max_new_tokens, args.temperature, args.top_k))
        return 0
    if args.command == "evaluate":
        tokenizer = load_tokenizer(_path(args.tokenizer))
        selected_device = "cuda" if args.device == "auto" and torch.cuda.is_available() else "cpu" if args.device == "auto" else args.device
        device = torch.device(selected_device)
        if device.type == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested but is unavailable")
        try:
            payload = torch.load(_path(args.checkpoint), map_location=device, weights_only=False)
        except TypeError:
            payload = torch.load(_path(args.checkpoint), map_location=device)
        model = MiniGPT(ModelConfig.from_dict(payload["model_config"])).to(device)
        load_checkpoint(_path(args.checkpoint), model, device=device, restore_rng=False)
        print(json.dumps(evaluate_language_losses(model, tokenizer, _path(args.validation), device), ensure_ascii=False, indent=2))
        return 0
    raise RuntimeError(f"unsupported command: {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())
