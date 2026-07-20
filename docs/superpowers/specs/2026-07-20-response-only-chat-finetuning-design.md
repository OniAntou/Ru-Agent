# Response-Only Chat Fine-tuning Design

## Goal

Give Ru a better chance of producing short, natural Vietnamese-English replies
by training from the completed base checkpoint with loss applied only to assistant
tokens. The existing 3,900-example chat run remains an experiment and is not
overwritten.

## Why the previous run failed

The base model learned mostly Wikipedia-style continuation. The first chat
fine-tune used only 3,900 examples and scored both the `User:` prompt and the
assistant reply, so it could reduce loss by learning prompt formatting and
generic continuation instead of learning the desired answer. Its best validation
loss occurred at step 300, then rose while training loss continued to fall.

## Scope

This change adds a second chat corpus importer, response-only token labels, a
token-window dataset that skips windows without any answer tokens, automatic
best-checkpoint saving, and a separate fresh fine-tuning configuration. It does
not change the base checkpoint, tokenizer, model architecture, current GUI
conversation format, or the existing `chat-finetune` artifacts.

## Data sources and selection

The second corpus contains bounded, openly licensed conversation data with
source and licence recorded for every pair:

| Source | Limit | Purpose | Licence |
| --- | ---: | --- | --- |
| `5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca` | 18,000 pairs | Vietnamese and English multi-turn dialogue | Apache-2.0 |
| `hoanghai2110/vietnamese-dataset` | 1,800 pairs | Short Vietnamese daily conversation | Apache-2.0 |
| `OpenAssistant/oasst2` | 2,000 pairs | Reviewed Vietnamese-English assistant turns | Apache-2.0 |
| `HuggingFaceH4/ultrachat_200k` | 6,000 pairs | English assistant turns | MIT |

The importer extracts only adjacent user/assistant pairs; it ignores system
messages rather than training a hidden system prompt. It normalizes text,
rejects empty, oversized, code-heavy, link-heavy, and roleplay examples, removes
exact duplicate pairs globally, and fails without publishing final files if a
source cannot satisfy its quota. The existing user-reviewed `approved.jsonl`
remains separate for a later curated follow-up.

## Response-only supervision

Each example is formatted exactly as the GUI prompt:

```text
User: <prompt>
Assistant: <response>
```

Encoding writes aligned token and label binaries. Input tokens include BOS,
formatted prompt, response, and EOS. Labels use `-100` for BOS and every target
token in the prompt, then contain the response tokens and EOS. PyTorch's
cross-entropy ignore index therefore removes prompt tokens from the loss while
still allowing them to provide causal context.

`SupervisedTokenWindowDataset` reads the aligned binaries and admits only
windows containing at least one non-`-100` label. This prevents invalid all-mask
losses when a long prompt crosses a 512-token window boundary.

## Training and checkpoint policy

The new run initializes exclusively from `checkpoints/base/latest.pt` with a
fresh optimizer and scheduler. It uses a lower learning rate of `2e-5`, 1,200
steps, 16 gradient-accumulation steps, and validation/checkpoint intervals of
50 steps. All files are written under `checkpoints/chat-response-v2`.

The trainer saves `best.pt` whenever validation loss improves and preserves
every interval checkpoint plus `latest.pt`. The GUI checks V2 `best.pt`, then
V2 `latest.pt`, before the older chat checkpoint candidates and the base
checkpoint. This makes rollback explicit and leaves all earlier experiments
available.

## Verification and acceptance

Tests cover multi-turn 5CD extraction, provenance/quotas, exact label masking,
valid supervised windows, best-checkpoint updates, CLI composition, and GUI
priority. Runtime verification records deterministic completions for the same
Vietnamese and English greeting prompts from base, old chat-best, and V2
best. V2 is adopted only if it avoids an empty reply and immediate `User:`
continuation in both languages and is visibly less repetitive than the old
chat-best checkpoint. If it fails this manual quality gate, the GUI remains on
the prior checkpoint and the result is reported honestly.
