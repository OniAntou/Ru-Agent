---
status: accepted
date: 2026-11-19
---

# `read` extracts PDF text and refuses other documents by name

> Superseded in part by [ADR 0053](0053-attachments-reach-the-model-as-paths.md): `read`
> now extracts Office Open XML and OpenDocument text by part range, and every failure names
> the next step. The PDF behavior below stands.

## Context

Coding agents diverge on non-text workspace files. Claude Code's `Read` supports
text, images and PDFs (page ranges, using `pdftoppm` for long documents) and
refuses binary extensions such as `.xlsx` and `.zip` with a suggestion to use a
skill or shell command. Gemini CLI's `read_file` supports text, images, audio and
PDF. OpenCode's `read` returns text, image formats and PDF up to 20 MiB and fails
other binary files. None of them parses Office documents in the built-in read
tool; those are delegated to shell commands, skills or MCP servers.

Ru previously read only UTF-8 text and images. A PDF, Office document or binary
file failed as `invalid_utf8`, which neither described the file nor told the model
what to do. Ru already carries PdfPig in the Broker for `web_fetch` PDF text and
has no interpreter dependency, so PDF is the one binary format it can support
in-process without adding a service.

## Decision

- `read` keeps its byte-paged UTF-8 text behavior and image behavior.
- A `.pdf` file returns bounded extracted text from PdfPig. A `pages` argument
  selects a 1-based page or range (`"5"`, `"5-12"`) with at most 100 pages per
  call. Without `pages`, a document of at most ten pages is read whole; a longer
  document returns its first ten pages and a notice naming the next range.
  Extraction stops at 60,000 characters per page range; a PDF larger than 20 MiB,
  encrypted, malformed or past the ten-second parse bound fails with a named
  code and a repair hint.
- Office and OpenDocument extensions (`.doc(x)`, `.xls(x)`, `.ppt(x)`, `.odt`,
  `.ods`, `.odp`, `.rtf`, legacy Office) fail as `document_unsupported`. The hint
  points at the desktop composer, which extracts PDF/Office text for a user
  attachment, and at a shell command or installed skill for conversion.
- Other binary files (invalid UTF-8 that contains NUL bytes, or binary
  extensions) fail as `binary_file_unsupported` with the same guidance. `read`
  never dumps raw bytes or base64 into the model context as a fallback.
- The tool identity and authority do not change: the Broker still performs the
  read through `IToolGateway`, and the normal path, protected-path, hard-link and
  final-path checks run first. Only the `read` input schema gains the optional
  `pages` string and its description changes; the catalog version stays at
  1.0.0 because the change is additive and ships with its Host and Broker
  together.

## Alternatives considered

- **Provider-native document parts**: Anthropic and OpenAI accept PDFs as
  request document/file parts, but the custom OpenAI-compatible gateways Ru
  supports cannot be assumed to accept them, request bodies would grow, and
  each provider would need its own bounded projection. Local extraction keeps
  one behavior across protocols and avoids uploading file bytes beyond the
  model-visible request already required.
- **Office parsing in the Broker**: would add a second document parser and a
  larger untrusted-binary surface for formats no competitor's read tool parses.
  The desktop composer already covers the user-attachment path; the agent can
  convert a workspace document with a shell command or a skill when policy
  allows it.
- **Base64 byte mode**: makes "read anything" literal but floods model context
  with unintelligible bytes and needs paging/noise handling; the named refusal
  plus shell/skill escape hatch is the better default, matching the other
  agents.

## Limits

No OCR and no image-only PDF text. No Office/OpenDocument parsing in the agent.
No audio, archives or executable inspection. PDF page rendering is not part of
`read` (the model receives text, not page images), so a scanned PDF fails with an
explainable notice instead of invented content.
