from __future__ import annotations

from pathlib import Path
from typing import Iterable

from tokenizers import Tokenizer, decoders, models, pre_tokenizers, trainers

SPECIAL_TOKENS = ["<pad>", "<bos>", "<eos>", "<unk>"]


def train_tokenizer(text_files: Iterable[str | Path], output_path: str | Path, vocab_size: int = 16_384) -> int:
    files = [str(Path(path)) for path in text_files]
    if not files:
        raise ValueError("at least one text file is required to train the tokenizer")
    if vocab_size < len(SPECIAL_TOKENS) + 256:
        raise ValueError("vocab_size must leave room for byte alphabet and special tokens")

    tokenizer = Tokenizer(models.BPE(unk_token="<unk>"))
    tokenizer.pre_tokenizer = pre_tokenizers.ByteLevel(add_prefix_space=False)
    tokenizer.decoder = decoders.ByteLevel()
    trainer = trainers.BpeTrainer(
        vocab_size=vocab_size,
        special_tokens=SPECIAL_TOKENS,
        initial_alphabet=pre_tokenizers.ByteLevel.alphabet(),
        show_progress=False,
    )
    tokenizer.train(files, trainer)
    destination = Path(output_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    tokenizer.save(str(destination))
    return tokenizer.get_vocab_size()


def load_tokenizer(path: str | Path) -> Tokenizer:
    tokenizer_path = Path(path)
    if not tokenizer_path.is_file():
        raise FileNotFoundError(f"tokenizer artifact not found: {tokenizer_path}")
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    tokenizer.post_processor = None
    tokenizer.decoder = decoders.ByteLevel()
    return tokenizer


def encode_text(tokenizer: Tokenizer, text: str) -> list[int]:
    return tokenizer.encode(text).ids


def decode_tokens(tokenizer: Tokenizer, token_ids: list[int]) -> str:
    return tokenizer.decode(token_ids, skip_special_tokens=False)
