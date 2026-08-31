---
status: accepted
date: 2026-08-31
---

# Isolate OmniVoice in its owned Python process

OmniVoice remains in `omnivoice_env`, outside the managed Worker process.
The managed `OmniVoiceProcessController` owns its Python child, and
`OmniVoiceHttpProvider` uses a loopback HTTP seam to the retained Flask provider.
The provider owns model loading and synthesis; managed C# owns orchestration
and lifetime. This local provider seam is distinct from the authenticated
Public Host and Worker protocols.

The managed-runtime migration superseded the earlier framed-stdio transport
description. Dependency and process isolation remain the decision; this does
not reintroduce a Python Public Host, Agent Worker, or VTS runtime.
