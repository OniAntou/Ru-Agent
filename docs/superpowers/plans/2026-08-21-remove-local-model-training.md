# Remove local model/training and harden the harness

## Scope

- Remove LFM2.5 HF/GGUF generation, llama.cpp sources/binaries, QLoRA tooling,
  evaluator/V5 suite, configs, tests and local evaluator artifacts.
- Keep OmniVoice, memory, conversation data and OpenAI-compatible adapters.
- Preserve one-way migration of stale local-backend settings to the cloud
  default.
- Make the ASP.NET Core harness fail closed and run its deterministic gate in
  CI.

## Steps

1. Delete tracked local-model/training/evaluator code, configurations and tests;
   remove ignored local assets only at their exact approved paths.
2. Refactor the Python runner and worker settings boundary to remove GGUF
   loading, checkpoint state and local GPU-generation branches.
3. Remove local-model dependencies and stale setup/UI/documentation claims;
   retain OmniVoice and memory dependencies.
4. Upgrade the compatibility harness: method-aware safe probing, expected
   status checks, WAV validation, and CI invocation with deterministic worker.
5. Run focused tests, full Python/.NET/WinUI gates, route/build checks, then
   scan documents and references for stale local-model/training claims.
