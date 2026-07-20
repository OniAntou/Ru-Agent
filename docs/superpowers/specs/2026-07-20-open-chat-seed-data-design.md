# Open Conversation Seed Data Design

## Goal

Improve Ru's natural, friendly Vietnamese-English conversation without changing
the completed base-language checkpoint or pretending that external data was
manually approved by the user.

## Scope

This change adds a repeatable importer for a bounded, openly licensed seed
corpus and documents the fine-tuning flow. It does not change Ru's personality
prompt, tokenizer, base pre-training corpus, GUI layout, or model architecture.

## Data sources

The importer will collect a deterministic bounded sample from these sources:

| Source | Intended contribution | Licence |
| --- | --- | --- |
| `HuggingFaceH4/ultrachat_200k` | English user/assistant exchanges | MIT |
| `OpenAssistant/oasst2` | Vietnamese and English assistant exchanges | Apache-2.0 |
| `hoanghai2110/vietnamese-dataset` | Short Vietnamese daily conversation | Apache-2.0 |

The default limits favour Vietnamese examples while retaining enough English to
preserve bilingual conversation. Every output row retains its source identifier,
licence, and language alongside the prompt and response.

## Architecture and data flow

`scripts/download_open_chat_data.py` will be the explicit entry point. It uses
the installed Hugging Face `datasets` dependency in streaming mode, so it does
not fetch the complete source datasets. Source-specific extractors convert
records into a small common record shape:

```json
{"prompt":"...","response":"...","language":"vi","source":"...","license":"..."}
```

The importer validates text, normalizes whitespace, and removes exact duplicate
pairs. It filters examples that are too short, too long for the 512-token model
context, primarily code, roleplay, or dominated by links/markup. It writes:

- `data/conversations/open_seed.jsonl` — normalized examples with provenance;
- `data/conversations/open_seed_manifest.json` — requested limits, kept/rejected
  counts, per-source counts, and source licences.

The existing `prepare-chat` command remains unchanged and reads the seed JSONL
because it already consumes the `prompt` and `response` fields while safely
ignoring provenance fields. The user-owned `approved.jsonl` remains separate.

## Fine-tuning and runtime selection

The documented workflow prepares and encodes `open_seed.jsonl`, then starts a
fresh optimizer from `checkpoints/base/latest.pt` using
`configs/chat_finetune.json`. It saves only into
`checkpoints/chat-finetune`. Once complete, the GUI will prefer that checkpoint
over the base checkpoint, while the base remains available as a rollback.

## Error handling

The importer fails clearly when a source schema changes, an expected text field
is absent, a requested limit cannot be reached, or the destination cannot be
written. Partial output is written to temporary files and promoted only after a
successful run. The manifest captures enough information to reproduce the data
selection.

## Testing and verification

Unit tests will cover source-record extraction, language and quality filters,
deduplication, deterministic limits, and manifest content without downloading
datasets. Integration verification will run the importer with small limits,
prepare/encode the result, start the chat fine-tuning run, and compare a short
Vietnamese and English prompt before and after fine-tuning. Project
documentation will be updated with the commands and provenance note.
