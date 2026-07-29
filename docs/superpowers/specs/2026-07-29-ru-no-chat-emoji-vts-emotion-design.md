# Ru no-chat-emoji and VTS emotion design

**Date:** 2026-07-29

**Status:** Design approved for implementation planning

## Goal

Keep Ru's displayed chat text free of both Unicode emoji and ASCII emoticons.
Emotion should be expressed through the existing VTube Studio (VTS) action
channel when available, while the fallback remains natural prose.

## Active contract

The shared `ru_style.py` personality contract will state that Ru must not emit
emoji or ASCII emoticons such as `:)`, `:D`, `;)`, or `<3` in chat. Ru should
use the VTS action/metadata channel for visible emotion when appropriate and
must not explain private VTS markers in the displayed message. If VTS is
unavailable, Ru should express the feeling with ordinary words, punctuation,
and concise prose rather than replacing it with symbols.

The same rule applies to `RU_SYSTEM_PROMPT`, language-specific prompts, and the
OpenCode cloud addendum. Existing safety, language, profanity, search, and
`anh`/`em` rules remain unchanged.

## Scope and non-goals

- Modify only the shared style contract and its regression tests.
- Keep the existing VTS metadata pipeline and display stripping unchanged.
- Do not add a post-generation sanitizer that could corrupt quoted text, code,
  or user-provided content.
- Do not rebuild v6.3 unless the audit finds a prohibited symbol in its rows;
  the current authored corpus is expected to remain unchanged.

## Verification

- Prompt tests assert the no-emoji/no-emoticon rule in local and cloud prompts.
- A dataset audit scans v6.3 authored rows and generated splits for Unicode
  emoji and the supported ASCII emoticon patterns.
- The full pytest suite and whitespace diff check must pass.
- Project docs record the active display rule; historical style documents are
  not rewritten beyond a superseded note when they describe old behavior.
