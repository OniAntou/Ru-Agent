# Bilingual Chat Fine-tuning Design

## Goal

Provide a separate, reproducible fine-tuning path that teaches the completed base model to continue Vietnamese-English conversations naturally from user-approved chat examples. Fine-tuning must not modify the base checkpoint, tokenizer, processed Wikipedia corpus, or the base-training process currently in progress.

## Decision

Use the base checkpoint as **model-weight initialization only**. A chat fine-tuning run starts a fresh optimizer, scheduler, random seed, and local step counter. This avoids the existing resume mechanism's global-step/scheduler state and preserves an independently reproducible conversational-training phase.

## Data flow

1. The GUI appends explicitly approved examples to `D:\LLM\data\conversations\approved.jsonl`.
2. A new preparation command validates each JSONL record, rejects blank prompt/response pairs, deduplicates exact pairs, deterministically splits the examples into train/validation sets, and writes documents formatted as:

   ```text
   User: Xin chào
   Assistant: Chào anh, hôm nay anh thế nào?
   ```

3. The existing tokenizer encodes those documents to `D:\LLM\data\conversations\processed\train.bin` and `val.bin`.
4. The training command initializes the architecture from `D:\LLM\checkpoints\base\latest.pt` and writes all fine-tuning checkpoints to `D:\LLM\checkpoints\chat-finetune`.

The initial implementation uses ordinary causal-language-model loss over the complete formatted turn. It does not mask user tokens; response-only supervised loss is a later improvement when enough curated examples exist to justify additional complexity.

## CLI and configuration

Add a `prepare-chat` CLI command with input JSONL path, output directory, and validation ratio. Add an optional `--init-checkpoint` argument to `train`; it loads only compatible model weights and rejects combining it with `--resume`.

Add `configs\chat_finetune.json` with the same 34M-parameter model shape as `base.json`, lower learning rate (`5e-5`), fresh warmup (`100` steps), gradient accumulation (`16`), validation/checkpoint interval (`100`), and `1,000` local fine-tuning steps. It is a starting configuration, not a claim that the current small approved dataset is sufficient.

## Safety and usability

- `--resume` continues an interrupted run including optimizer/scheduler state; `--init-checkpoint` starts a new phase from model weights only. They are mutually exclusive.
- The chat preprocessor never edits `approved.jsonl`; it creates derived artifacts under the ignored `data\conversations\processed` directory.
- A minimum of two unique approved examples is required to form a train/validation split. A clear error explains that more reviewed conversations are needed.
- The GUI retains its existing checkpoint picker. Once fine-tuning produces `checkpoints\chat-finetune\latest.pt`, the GUI prefers it by default while still allowing the user to select the base checkpoint explicitly for rollback.

## Verification

- Unit-test JSONL validation, Unicode Vietnamese preservation, exact-pair deduplication, deterministic split, and output format.
- Unit-test weight-only initialization: model weights match the source checkpoint while local training starts at step zero and writes a separate checkpoint.
- Unit-test CLI argument validation for the mutually exclusive initialization/resume options.
- Run the entire pytest suite and a CPU smoke run using a temporary mini checkpoint/data set.
- Do not launch fine-tuning automatically: it requires enough approved examples and a completed base checkpoint chosen by the user.
