# Ru Qwen QLoRA Conversation Design

> **Data-strategy update (2026-07-21):** The external-source V3 corpus did not
> pass its manual quality audit. The project-authored corpus design in
> `2026-07-21-ru-authored-dialogue-corpus-design.md` supersedes this document's
> source-selection details; the Qwen runtime, QLoRA, GUI, and acceptance design
> remain unchanged.

**Date:** 2026-07-20
**Status:** Approved design; implementation has not started

## Goal

Replace Ru's runtime language-model backend with a locally stored, pretrained
Vietnamese-English conversational model so that short everyday exchanges are
natural, coherent, and non-repetitive. Ru remains a local Windows application
whose data, model cache, adapters, checkpoints, and GUI stay under `D:\LLM`.

The selected foundation model is `Qwen/Qwen2.5-0.5B-Instruct`. Its official
model card identifies it as a 0.49B instruction-tuned causal model with
Vietnamese and English support. The existing 34M Mini-GPT experiments remain
intact as educational artifacts and fallbacks; no checkpoint is overwritten.

## Why this replaces another Mini-GPT fine-tune

The V2 response-only experiment lowered validation loss but failed the manual
conversation gate: Vietnamese and English greetings remained nonsensical or
repetitive. Its data was dominated by long instruction answers, and the 34M
Wikipedia-pretrained base did not contain enough conversational language
competence to recover through SFT alone. Adding more of the same data or steps
would optimize the wrong proxy.

## Architecture

```text
Local Qwen 0.5B base (4-bit, frozen)
        +
Ru LoRA adapter (trainable, local)
        |
 Qwen chat template + bounded conversation history
        |
 Tkinter Ru GUI / local generation
```

### Local artifacts

| Artifact | Location | Ownership and behavior |
| --- | --- | --- |
| Qwen base files | `D:\LLM\models\qwen2.5-0.5b-instruct` | Download once from the official model repository; immutable after verification. |
| Ru adapter | `D:\LLM\checkpoints\ru-qwen-v3\adapter` | LoRA adapter, tokenizer references, and a manifest identifying the exact base revision. |
| V3 data | `D:\LLM\data\conversations\ru_v3` | Source manifest, train/validation JSONL, and quality report. |
| Training output | `D:\LLM\checkpoints\ru-qwen-v3` | Adapter checkpoints, trainer state, logs, and best-adapter pointer only. |

`checkpoints/base`, `checkpoints/chat-finetune`, and
`checkpoints/chat-response-v2` are preserved and never used as V3 initial
weights.

### Dependency and memory strategy

The project will add compatible local runtime dependencies for
`transformers`, `accelerate`, `peft`, `bitsandbytes`, and `safetensors`.
Before downloading model weights, a smoke check must confirm that the installed
bitsandbytes build can load a 4-bit NF4 layer on this Windows NVIDIA system.

QLoRA loads the Qwen base in 4-bit NF4 with double quantization. The base stays
frozen; LoRA parameters are attached to Qwen attention projections
`q_proj`, `k_proj`, `v_proj`, and `o_proj`. The initial adapter configuration
is rank 16, alpha 32, dropout 0.05, no bias, and causal-LM task type. Training
uses batch size 1, bounded 512-token samples, gradient accumulation, fp16/bf16
only after the hardware smoke test, and no CPU-offloaded training path. If the
smoke test cannot keep a safe VRAM margin, implementation stops before a long
training run rather than silently swapping to a larger model or degrading to
CPU training.

## Conversation data

V3 does not train directly on the unfiltered 27,797-pair V2 corpus. It creates
a separate response-only dataset with short, ordinary dialogue as the target:

| Share | Intended behavior | Constraints |
| --- | --- | --- |
| 45% | greetings, daily check-ins, gratitude, small talk, emotion support | 1–4 natural sentences; no lists or disclaimers. |
| 25% | contextual follow-ups over two to four turns | Assistant must refer to the immediately relevant earlier turn. |
| 20% | bilingual English/Vietnamese conversations and light code-switching | Answer in the user's language unless asked to switch. |
| 10% | helpful everyday questions | Concise answer first; no specialist, code, or lengthy instruction content. |

Every pair is normalized and must pass these exclusions: links, code blocks,
markup, role-play instructions, AI-disclaimer phrases, list-heavy answers,
duplicate prompt/response pairs, prompt or response longer than 420 characters,
and responses shorter than two characters. The importer records provenance and
licence for any external source. Project-authored examples are explicitly
labelled `source: "ru_project_seed"` and `license: "project-owned"`; they are
reviewed as plain conversational text before use.

Training messages use Qwen's own `apply_chat_template` rather than the old
`User:`/`Assistant:` text convention. Labels mask every system/user/template
token and score only assistant content plus its end marker. This makes training
and GUI inference use the identical dialogue format.

## Training and selection

The implementation runs a Qwen-base conversation baseline before training,
then trains the adapter for up to three epochs with validation at fixed
intervals. It saves an adapter checkpoint only when validation assistant-token
loss improves, retains a `best` pointer, and records seed, package versions,
base model ID/revision, dataset manifest checksum, peak VRAM, and validation
loss in the adapter manifest.

The V3 GUI candidate is selected only when this manifest and the adapter files
exist. Otherwise the GUI's existing Mini-GPT fallback order remains available.

## GUI behavior

`ModelRunner` gains a separate Qwen-backed implementation rather than changing
the Mini-GPT runner's checkpoint format. The Qwen runner:

1. loads the local base and Ru adapter in 4-bit mode;
2. sends a short fixed system instruction: Ru is a friendly bilingual local
   companion who answers naturally and concisely in the user's language, and
   defaults to `em` for itself and `anh` for the user in Vietnamese;
3. serializes the bounded saved turns using Qwen's chat template;
4. generates only the newly produced assistant tokens; and
5. releases no conversation content outside the local process.

The GUI keeps the name Ru, conversation-store flow, manual useful-response
button, and explicit model-path controls. Voice, vision, avatar control, web
tools, and autonomous learning are not part of V3.

## Verification and acceptance

Verification has four layers:

1. **Runtime:** package/import, 4-bit load, base-model local generation, and
   adapter-load smoke tests on CUDA.
2. **Data:** JSONL schema, deterministic split, response-only label alignment,
   source manifest, and filtering tests.
3. **Training:** a short CPU-free GPU smoke run writes a loadable adapter;
   full training records a genuine validation-best adapter without modifying
   the base model.
4. **Conversation:** compare Qwen base and Ru adapter with a fixed 12-prompt
   Vietnamese-English suite covering greeting, feelings, thanks, short
   follow-up, language switch, and simple daily questions.

V3 is accepted only if each selected Ru response is non-empty, has no Qwen
template/user-label leakage, has no immediate repeated phrase, follows the
latest turn's language, and is visibly natural in at least 10 of the 12 manual
checks. A lower validation loss alone does not pass V3.

## Non-goals and failure policy

V3 does not claim that a 0.5B model matches large hosted assistants, nor does
it add voice, vision, avatar control, or specialist reasoning. If QLoRA is
unsupported or the adapter fails the conversation gate, preserve the artifacts,
leave V2 marked unsuccessful, and report the evidence before proposing a
different base size or a different data strategy.
