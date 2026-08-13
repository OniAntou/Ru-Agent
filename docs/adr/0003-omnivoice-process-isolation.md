---
status: accepted
date: 2026-08-13
---

# Isolate OmniVoice behind a framed process

OmniVoice remains in `omnivoice_env`, but its owned child process communicates
through bounded stdin/stdout control and binary frames instead of Flask on port
5052. This preserves dependency isolation while removing an unnecessary web
seam and network listener.
