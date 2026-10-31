---
status: retired
date: 2026-10-30
---

# Retired language-server integration

## Decision

The user requested removal of Ru's language-server integration on 2026-10-30.
The Broker process host, LSP transport, tool definition, semantic profile and
associated policy/configuration paths are removed. Ru no longer reads `.ru/lsp.json`.
Existing user files are not deleted. Persisted retired profile selections fail
explicitly instead of selecting a different profile.

Use the existing file, search and terminal tools for code navigation and CLI
build, lint, typecheck and test feedback. The former implementation and its
verification evidence remain in Git history and dated verification records.
