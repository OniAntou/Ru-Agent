# Ru Qwen3 4B Local Baseline Design

## Purpose

The Qwen2.5-3B V5 base fit the RTX 4050 but failed Ru's mandatory Vietnamese anh/em voice gate. Ru then tested only Qwen/Qwen3-4B as a stronger local conversational base.

Natural, friendly Vietnamese-English conversation remains the adoption target. Model size and benchmark scores do not substitute for this quality gate.

## Decision

Qwen3-4B ran locally in 4-bit NF4 using the fixed 32-case bilingual suite. The experiment remained base-only: it created no corpus, adapter, training configuration, GUI backend change, or user-chat training path.

The Qwen3 chat template must explicitly disable thinking mode so the test measures concise conversational replies, not visible reasoning text.

## Runtime boundaries

| Item | Value |
| --- | --- |
| Model | Qwen/Qwen3-4B |
| Local directory | D:\LLM\models\qwen3-4b |
| Evaluation output | D:\LLM\checkpoints\ru-qwen3-4b\base_evaluation.json |
| Execution | One candidate on GPU at a time |
| Quantization | 4-bit NF4 |
| Generation | Deterministic, maximum 96 new tokens, non-thinking chat template |

The existing Qwen runtime and evaluation system are extended without changing existing V3, V4, or V5 profiles. Qwen3 validation must accept model type qwen3 while retaining the qwen2 validation required by older profiles.

## Base gate

**Result (2026-07-21): not adopted.** The official safetensors download was
`8,044,982,000` bytes and left `40,743,448,576` bytes free on D:. The valid
non-thinking 32-case run used `2,635.017 MiB` peak allocated VRAM. A narrow
automatic-voice guard correction was applied and the already saved outputs were
reassessed without a second model generation; automatic disqualifiers were then
zero. Full manual review scored only `11/32`, mainly because every English
prompt received Vietnamese text plus several role reversals and invented shared
experiences. No Qwen3 corpus, adapter, training run, or GUI change exists.

Before download, verify CUDA availability, D: free space, and a clean non-overlapping working tree. The download must leave at least 5 GB free on D:.

The base is not adopted immediately when any of these occurs:

1. CUDA OOM.
2. Peak allocated VRAM above 5,500 MiB.
3. Any automatic response disqualifier.

Only a candidate with zero automatic disqualifiers receives a manual review of all 32 cases. It passes only with at least 24 replies that are natural, relevant, language-appropriate, and use em for Ru and anh for the user when Vietnamese address is needed.

A failed base stops the experiment: no fine-tuning, corpus, adapter, or GUI change. A passing base requires a separately approved fine-tuning design.

## Verification and documentation

Persist the model id, local verification details, peak allocated MiB, all prompts/responses, automatic failures, manual review when applicable, and final verdict. Run the full pytest suite after the experiment.

Update README, TODO, and this design with observed evidence only. Do not claim improvement until the saved evaluation proves it.

## Out of scope

Gemma, vision, speech, avatars, cloud APIs, CPU/RAM offload, datasets, and training are out of scope for this experiment.
