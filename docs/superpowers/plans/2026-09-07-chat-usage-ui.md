# Chat usage UI

## Goal

Show provider-reported token usage on completed assistant messages in the WinUI chat while preserving the distinction between an exact zero and an unavailable field.

## Tasks

- [x] Add a small UI usage model/parser for the durable `model.usage_recorded` payload.
- [x] Deliver parsed usage from `AgentRunController` to `ChatView` without exposing raw payload content.
- [x] Render an unobtrusive usage footer on assistant bubbles and preserve it across history reload.
- [x] Add focused model, controller, and source-contract tests; run the WinUI test project and inspect related docs.
