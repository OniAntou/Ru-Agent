---
status: accepted
date: 2026-10-28
---

# Share skill discovery and preserve bounded workflow snapshots per run

## Context

Independent Host/Broker parsers and identities let advertised skills fail to
load. Body-based discovery budgets excluded metadata that would fit the prompt,
while ordinary tool-history compaction could remove the selected workflow.
Manual commands returned bodies without activating them in chat.

## Decision

Use one bounded YAML metadata parser in Protocol and a Broker-owned catalog/body
reader. YamlDotNet is a managed dependency, used for data parsing only; no YAML
object deserialization, package script execution or interpreter is introduced.
Canonical folder IDs stay separate from display names. Metadata-only discovery,
bounded cache validation and a budgeted prompt listing are separate from the
on-demand body limit. Checked source/resource paths and version information
accompany loads.

The discovery revision is a metadata/file-stat stamp, not a full content hash.
Exact content pinning uses the loaded `body_sha256` and optional
`expected_body_sha256`. Hashing every body at discovery would reintroduce the
I/O and allocation cost this split removes. Run restoration always uses the
captured snapshot, independently of later file stamps.

Expose a run-independent authenticated catalog and workspace-scoped local
preferences for existing-root registration and selection controls. Preferences
change guidance only. They never modify execution policy or grant paths.

Explicit chat invocation atomically stores a model-only `SkillInvocation`
payload with the concise user prompt. The same payload kind activates a skill
through the typed run-command channel. Its reserved context provenance cannot
be manufactured by ordinary tool/user text. Existing encrypted payload storage
remains canonical; no parallel workflow database is introduced.

Project validated successful skill receipts and explicit invocations before
compaction. Retain at most four complete workflows within 128 KiB of body data,
deduplicate identity/version, disclose omissions and restore missing bodies as
untrusted user context. Resume uses captured content; a fresh load explicitly
selects a newer body. The registry is run-local, not an indefinitely inherited
session instruction collection.

## Alternatives and consequences

Keeping duplicate loaders leaves cross-layer drift. Inlining all bodies or
pinning every receipt grows context with the installed library or run length.
Rereading disk after compact silently changes the selected workflow. Depending
only on a generated checkpoint makes exact workflow continuity uncertain.

The shared catalog and bounded snapshot projection add state and cache
invalidation responsibilities, covered by cross-layer and cold/warm regression
tests. Old binaries cannot interpret the new payload kind; update Host and its
bundled components together. Provider/model/tool identity and Broker execution
authority remain unchanged. There is no executable SkillManifest runtime.
