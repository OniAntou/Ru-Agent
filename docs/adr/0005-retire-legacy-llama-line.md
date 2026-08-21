---
status: accepted
date: 2026-08-13
---

# Retire the legacy Llama line

Ru's conversational generation is cloud-first. Llama 3.1/v6.x and the later
LFM2.5 local-generation/training line are removed after a path/reference/
protection manifest because retaining unsupported local assets obscures the
active runtime and produces misleading operations documentation.

The tracked local runtime, training tooling, evaluator, configs, and generated
local assets were removed on 2026-08-21. A narrow settings migration remains so
old persisted `llama.cpp`/GGUF values fall back to the cloud default; it does
not load or retain a local model.
