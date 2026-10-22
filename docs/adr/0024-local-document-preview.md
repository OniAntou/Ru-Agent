---
status: accepted
date: 2026-10-22
---

# Local, read-only document preview in the desktop panel

## Context

Files previously expanded folders but did nothing when a file was clicked.
The owner requested actual DOCX/PPTX rendering in the side panel and approved
using installed Windows/Office preview handlers, with explicit missing-renderer
feedback and no document upload service.

## Decision

The GUI owns a read-only Preview tab reached by an explicit file click. Office
documents use registered Windows preview handlers activated out of process.
PDF, images, text and Markdown use native local renderers. No Host route or
model-visible tool is added; existing execution authority and approvals do not
change. File reads are limited to the selected workspace and checked against
size, reparse and final-handle-path rules. Read handles prevent source mutation
while a handler is attached.

COM work lives on a pumping STA; opening times out from the UI after 15 seconds.
An owned borderless host has an independent DWM surface: PowerPoint rendered
in a standalone native host but not as a child of WinUI's no-redirection HWND.
It tracks only the panel's content rectangle, has no taskbar entry, and is not
activated by positioning or refresh. The GUI hides this HWND for other tabs,
other foreground applications and modal/approval surfaces, and
releases the handler and read handle on selection/session/workspace changes.
Windows owns its registered renderer's process hosting. Ru neither bundles
Office nor terminates unrelated Office/preview-host processes. A stalled COM
call can defer worker cleanup; this feature does not promise process-tree,
filesystem or network confinement for installed third-party preview handlers.

## Alternatives and consequences

- Cloud Office viewers require disclosure/upload of workspace documents and
  were rejected for the local preview flow.
- A text extractor or document thumbnail would not satisfy actual page/slide
  rendering. Parsing OOXML into a new custom layout engine would also lose
  fidelity for charts, tables, themes and embedded images.
- Converting through Office editing automation or bundling LibreOffice adds
  process ownership, installation and conversion lifecycle beyond this feature.

Preview fidelity and availability depend on the user's installed handler;
unsupported formats and missing/failing renderers produce named feedback.
The default .NET product does not acquire a Python or Office dependency for
chat, model execution or its other native preview formats.
