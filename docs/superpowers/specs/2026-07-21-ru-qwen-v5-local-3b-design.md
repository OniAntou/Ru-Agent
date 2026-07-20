# Ru V5 Local 3B Baseline Design

## Purpose

The V4 Qwen 1.5B candidate is not a usable Ru backend. Its 32-case evaluation
still contained wrong Vietnamese address forms, role reversals, unnatural
language changes, and generic responses after one QLoRA epoch. More epochs on
that candidate would reinforce those failures rather than make Ru natural.

Ru must run entirely on the user's local machine. V5 therefore tests a stronger
local foundation before writing or training another style adapter. Natural,
friendly Vietnamese-English conversation remains the only adoption target.

## Options considered

1. Train V4 for more epochs. Rejected: V4 failed both automatic and manual
   conversation gates, so this has no evidence of improving the real goal.
2. Use a local 7B-or-larger model. Deferred: its training memory margin on the
   6 GB RTX 4050 is too uncertain for the first next step.
3. Test local `Qwen/Qwen2.5-3B-Instruct` in 4-bit NF4 mode. Selected: it is a
   meaningfully stronger instruction base while remaining a bounded local
   experiment with a mandatory measured VRAM smoke check.

## V5 phases

### Phase 1: Base-only decision gate

**Result (2026-07-21): not adopted.** The local 3B safetensors download
completed at `6,171,926,992` bytes and the valid 32-case evaluation peaked at
`2,034.184 MiB`, below the 5,500 MiB hardware stop gate. The base nevertheless
triggered four automatic Vietnamese voice failures by addressing the user as
`bạn` or referring to Ru as `tôi`. Manual review was skipped as required by the
hard automatic gate. No V5 corpus, adapter, training run, or GUI change exists.

V5 first adds a separate registered base profile, downloader, config, and
runtime paths:

| Artifact | V5 path/value |
| --- | --- |
| Model ID | `Qwen/Qwen2.5-3B-Instruct` |
| Local base | `D:\LLM\models\qwen2.5-3b-instruct` |
| Candidate data | `D:\LLM\data\conversations\ru_v5` |
| Candidate adapter | `D:\LLM\checkpoints\ru-qwen-v5\adapter` |
| Evaluation | `D:\LLM\checkpoints\ru-qwen-v5\base_evaluation.json` |

The base loads in 4-bit NF4 with the existing deterministic generation settings.
It runs the fixed 32-case bilingual suite before any V5 corpus, adapter, or GUI
change exists. The base passes this phase only when it has no automatic
disqualifier and a manual review finds at least 24 of 32 replies natural,
relevant, in the expected language, and consistent with Ru's Vietnamese
`em`/`anh` voice. A failed base is documented as not adopted and stops V5 before
training.

The 3B download and generation run must leave at least 5 GB free on D. A
two-step QLoRA smoke run is authorized only after the base passes; it stops at
CUDA OOM or measured peak VRAM above 5,500 MiB. Existing V2, V3, and V4 assets
are never overwritten or deleted.

### Phase 2: Conditional style adapter

Only a passing base moves to Phase 2. The V5 source has 240 literal,
individually reviewed project-owned dialogue records: 110 Vietnamese, 100
English, and 30 light code-switching examples. At least 100 examples include a
real two-to-four-turn context. They cover greetings, everyday updates, stories,
feelings, small humour, gentle corrections, and endings that do not force a
question.

The source does not generate dialogue text from templates, scrape web data, or
include private chats. Any future user conversations require explicit export and
approval before they can become candidate material. Each Vietnamese assistant
reply defaults to `em` for Ru and `anh` for the user.

V5 trains only a low-rate QLoRA style adapter for one bounded pass. It compares
the adapter against the already-recorded 3B base on the exact same 32 cases.
V5 is adopted only when every automatic check passes, manual review reaches at
least 28 natural replies, and the adapter is no worse than the base on the
remaining replies. The GUI remains on its existing backend until that gate
passes in a separate runtime smoke test.

## Boundaries

V5 stays fully local. It does not add a cloud API, voice, vision, avatars,
autonomous learning from private chat, or a GUI backend switch. It does not use
validation loss as a conversation-quality verdict.
