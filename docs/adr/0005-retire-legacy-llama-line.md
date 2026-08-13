---
status: accepted
date: 2026-08-13
---

# Retire the legacy Llama line

Ru's supported local model is LFM2.5. Llama 3.1/v6.x-only source and local
assets are removed after a path/reference/protection manifest because retaining
an unsupported training line obscures the active runtime and produces
misleading operations documentation.

Plan 2 completed the approved tracked and local retirement. One dirty
training-compatibility file remains protected until Plan 3 migrates its active
LFM2.5 token-ending behavior; current llama.cpp runtime code is not legacy.
