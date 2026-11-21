---
status: accepted
date: 2026-11-19
---

# Attachments reach the model as paths; `read` extracts Office text and names the next step

## Context

Both clients decided at attach time whether the model could read a file. The terminal
refused binary files (`attachment_not_text`), any type outside its text list
(`unsupported_attachment`) and a PDF outside the workspace (`pdf_outside_workspace`). The
desktop app refused types outside its image/text/document list and extracted PDF and
Office text itself. A dropped `.zip` path in the terminal was sent as prompt text.

Other coding agents attach by path and leave the reading to the model and its tools, so
the user can drop in anything. When their read tool cannot serve a file, the model falls
back to a shell command. Ru's `read` refused Office files
([ADR 0050](0050-read-extracts-pdf-and-refuses-other-documents.md)). Its failures gave the
model nothing to act on: `invalid_utf8` reached it as "Tool arguments were rejected." and
`outside_workspace` as "Tool access was denied."

## Decision

1. **Attaching never needs a type the client understands.**
   - Small UTF-8 text (at most 8 KiB, `AttachmentStaging.InlineTextThresholdBytes`) is still inlined.
   - An image still goes to a model that reads images as an image.
   - Any other file, of any type or size, is sent as a workspace path. The prompt line is
     unchanged: `[Attached workspace file: <path> (<size>)]`.
   - The terminal treats any existing file path dropped on its own line as an attachment.
2. **Outside files are copied into the workspace.**
   - A file outside the workspace is copied by the client into
     `<workspace>\.ru\attachments\` (`AttachmentStaging`, shared by both clients).
   - The copy keeps the file's name (made safe; equal bytes reuse a copy, different bytes
     get `name (2).ext`) and is bounded at 64 MiB.
   - The folder carries its own `.gitignore` (`*`), so the copy never reaches the user's
     repository, and the user's own `.gitignore` is never edited.
   - Removing a chip, or Backspace on an empty terminal prompt, before sending deletes a
     copy no other pending attachment uses. Copies older than 14 days are pruned when
     another file is staged.
   - This is the user's own action on their workspace, made by the client. The Broker's
     path rules, presets and protected paths are unchanged: `.ru\attachments` is an
     ordinary workspace folder. Only `.ru\hooks.json` and `.ru\mcp.json` directly under
     `.ru` are protected, and a copy whose name the Broker protects anywhere (harness keys,
     databases, runtime descriptors) is renamed `attached-<name>`.
3. **A new desktop chat with no folder** asks the Host for its session-scoped scratch
   workspace before staging a non-image file, then follows the same path rules as a chat
   with a folder ([ADR 0054](0054-folderless-attachments-use-scratch-workspace.md)). If the
   Host cannot allocate it, the earlier behavior remains as a fallback: local document
   extraction, inlined text, and a refusal that says to choose a folder.
4. **`read` extracts Office Open XML and OpenDocument text.**
   - Covered formats: `.docx`/`.docm`/`.dotx`, `.xlsx`/`.xlsm`/`.xltx`,
     `.pptx`/`.pptm`/`.potx`/`.ppsx`, `.odt`, `.ods`, `.odp`.
   - A document is a sequence of parts in reading order: paragraphs, spreadsheet rows
     (each sheet's first row is prefixed with its name), or slides. `pages` selects a
     1-based part range, the same argument a PDF uses.
   - Text stops at 60,000 characters (the PDF cap). A notice names the next range.
   - The result is `kind: "document"` with `format`, `unit`, `part_count` and `pages`, and
     no `offset`. The Host projects any `read` result that has a `kind` and no `offset` as
     a bounded generic result.
   - Parsing uses the BCL only (`ZipArchive`, `XmlReader` with DTDs prohibited). XML parts
     are bounded at 16M characters, archives at 4,096 entries and files at 20 MiB. The
     ten-second parse deadline is checked between XML parts (one part's load cannot be
     interrupted, which the part bound keeps short). Nothing is rendered or executed.
   - Spreadsheet sheets follow the workbook's order and names through its relationships,
     and a sparse row keeps its columns by cell reference (at most 64 padded columns).
   - RTF reads as text. Legacy `.doc`/`.xls`/`.ppt` and iWork files still fail as
     `document_unsupported`.
5. **Every `read` failure names the next step.**
   - `binary_file_unsupported` says what the bytes look like (ZIP, gzip, 7z/RAR, legacy
     Office/OLE, Windows or ELF executable, SQLite, PDF without its extension, UTF-16
     text) and the command that opens them (`Expand-Archive`, `Format-Hex`,
     `Get-Content -Encoding Unicode`, …).
   - `invalid_utf8` names the likely encoding and how to decode it.
   - `outside_workspace` says how to attach the file.
   - `read_file` on an image, PDF or document fails as `read_file_text_only`, pointing at
     `read`.
   - Codes and exception types are unchanged; the hint travels with the exception and
     becomes the failure's summary.
   - A hint names the file only as `<path>` and carries at most an 8-byte signature.

## Consequences

- The model sees every attached file, decides how to read it, and recovers from a failed
  read by following the hint. Commands in a hint still go through terminal policy.
- The `read` description changes, so its golden description digest changes. The schema
  and the catalog version 1.0.0 are unchanged.
- Attached copies live in the workspace until pruned. A run reopened after pruning gets
  `file_not_found` for that path.
- This supersedes ADR 0050's "Office parsing in the Broker" alternative and its
  `document_unsupported` rule for Office Open XML and OpenDocument. ADR 0050's PDF behavior
  stands.

## Alternatives considered

- **Grant `read` access to a Host-owned folder outside the workspace**
  (`%LOCALAPPDATA%\Ru\attachments`): it widens the Broker's path rules for every run. A
  workspace folder needs no policy change.
- **Keep extraction in the clients**: the model would get one bounded extract with no way to
  read on, and the terminal would need its own copy of the parser.
- **Base64 for unknown files**: floods the context with bytes the model cannot use; a path
  plus a named next step is cheaper and works for every format.
