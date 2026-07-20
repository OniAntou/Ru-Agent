# Bilingual Mini-GPT Design

## Goal

Build a small general-purpose Vietnamese-English language model from random initialization so the project demonstrates the complete LLM pipeline: text preparation, tokenization, decoder-only Transformer training, checkpoint recovery, evaluation, and text generation.

The model is intended for learning and experimentation, not for ChatGPT-level reasoning or production use.

## Constraints and success criteria

The project runs on the user's Windows laptop with an NVIDIA RTX 4050 Laptop GPU and approximately 6 GB VRAM, 16 GB system RAM, and project data stored on `D:\LLM`. The initial model must fit a 6 GB GPU through a micro-batch of one, mixed precision, gradient accumulation, and memory-conscious defaults.

The first milestone is successful end-to-end operation:

1. A reproducible dataset preparation command produces train and validation shards from openly usable Vietnamese and English text.
2. The tokenizer encodes and decodes Vietnamese diacritics and English text without losing characters.
3. A single GPU training step completes, including forward pass, backward pass, optimizer update, and loss logging.
4. A training run can save a checkpoint and resume with model, optimizer, scheduler, step, and random-state information restored.
5. Validation loss is measured separately for Vietnamese and English held-out samples.
6. The trained checkpoint generates short text for prompts in both languages.

## Selected approach

The project uses a decoder-only Transformer trained with causal next-token prediction. The model starts with random weights; no pretrained model weights are used.

The initial configuration targets approximately 25–35 million parameters:

- 8 Transformer blocks
- model width 512
- 8 attention heads
- feed-forward width 2,048
- context length 512 tokens
- vocabulary size 16,384
- tied input and output embeddings
- pre-normalization with LayerNorm
- GELU feed-forward activation

The configuration must be centralized so a smaller fallback configuration can be selected without code changes if the GPU runs out of memory.

## Data and language balance

The corpus is general rather than domain-specific and contains both Vietnamese and English. Only public-domain, openly licensed, or user-provided data with permission may be included. The first version must not silently scrape or bundle copyrighted books, private documents, credentials, or application data.

Data preparation has four explicit stages:

1. Read supported UTF-8 text sources from a configured raw-data directory.
2. Normalize line endings and Unicode while preserving Vietnamese diacritics.
3. Remove empty content, obvious boilerplate, and exact duplicate documents.
4. Split by document into training and validation sets so near-identical text does not cross the split.

The language mixture is configured rather than assumed. The default target is approximately 50% Vietnamese and 50% English by token count, with a report showing actual counts. If the available corpus is imbalanced, the report must expose that fact instead of duplicating data invisibly.

## Tokenizer

The tokenizer is a byte-level BPE trained on the prepared bilingual corpus with a 16,384-token vocabulary. It must preserve UTF-8 Vietnamese characters through encode/decode round trips and expose special tokens for beginning/end of document and padding where required by batching.

Tokenizer training is a separate, repeatable command. The resulting tokenizer files are versioned as project artifacts and referenced by the training configuration. Training and generation must refuse to continue when the tokenizer artifact is missing or incompatible with the configured vocabulary size.

## Training pipeline

The training loop reads token shards from disk instead of loading the complete corpus into RAM. Each batch is a contiguous sequence of token windows with the target shifted by one token.

Memory-conscious defaults are:

- micro-batch size 1
- sequence length 512
- gradient accumulation to reach an effective batch size without increasing VRAM use
- automatic mixed precision when supported by the GPU
- gradient clipping
- AdamW optimizer
- warm-up followed by cosine learning-rate decay
- periodic validation and checkpointing to `D:\LLM\checkpoints`

The command must print device information, parameter count, effective batch size, current step, training loss, validation loss, learning rate, tokens processed, and peak allocated GPU memory. Checkpoints must be written atomically so an interrupted write does not replace the last known-good checkpoint.

## Project structure

```text
D:\LLM\
├── docs\superpowers\specs\
├── docs\superpowers\plans\
├── src\mini_gpt\
│   ├── config.py          # validated model and training configuration
│   ├── tokenizer.py       # tokenizer training and load/save interface
│   ├── data.py            # corpus preparation, sharding, and batches
│   ├── model.py           # Transformer blocks and language-model head
│   ├── checkpointing.py   # atomic save/load and resume state
│   ├── train.py           # training entry point
│   ├── evaluate.py        # bilingual validation and generation checks
│   └── cli.py             # stable command-line entry points
├── tests\
├── data\raw\
├── data\processed\
├── checkpoints\
├── pyproject.toml
└── README.md
```

The code is split by responsibility: model code does not own file preparation, data loading does not own optimization, and checkpointing does not own training policy.

## Evaluation

Automated tests cover configuration validation, tokenizer round trips, causal target shifting, tensor shapes, checkpoint round trips, and deterministic resume behavior under a fixed seed.

Runtime smoke checks cover CPU forward pass, GPU forward/backward pass when CUDA is available, one short training run, checkpoint resume, and bilingual generation. The project reports when a check is skipped because the required GPU runtime is unavailable.

The first quality signal is a validation-loss decrease against the randomly initialized baseline. Generation samples are qualitative evidence only; they must be recorded with the checkpoint step and prompt language.

## Risks and boundaries

- Six GB of VRAM limits batch size and model size; an OOM must trigger a documented smaller configuration rather than silent CPU fallback.
- Sixteen GB of RAM requires streaming or memory-mapped data; the full corpus must not be loaded into memory.
- The 49 GB free space on D: is enough for the first experiment, but raw data, processed shards, and checkpoints must be size-checked before long runs.
- General-purpose capability is limited mainly by corpus size and quality, not only parameter count.
- The project will not include web serving, chat UI, model quantization, distributed training, or private-data ingestion in the first milestone.
