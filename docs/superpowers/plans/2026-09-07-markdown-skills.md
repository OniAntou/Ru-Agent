# Markdown Skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans (recommended for inline work). Steps use checkbox (`- [ ]`) syntax.

**Goal:** Load common external `SKILL.md` packages as bounded, untrusted Agent guidance while preserving existing declarative workflow skills and Broker authority.

**Architecture:** Add a typed origin to prompt instruction blocks, a focused Markdown front-matter/body parser, and a deterministic discovery adapter. `RuInstructionLoader` composes root instructions and discovered Markdown skills; the prompt composer renders external skills under an explicit untrusted boundary. Markdown skills never become `SkillManifest` workflows and never modify Broker capabilities, scopes, or approvals.

**Tech Stack:** .NET 10, C#, xUnit, existing Harness system-prompt contracts, Windows final-path containment, UTF-8 byte caps, ASP.NET Core host wiring.

---

## File map

- Create `backend-dotnet/RuBackend/Harness/Prompting/MarkdownSkillLoader.cs`: parse bounded `SKILL.md` files, front matter, derived metadata, and deterministic discovery diagnostics.
- Modify `backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptContext.cs`: add `PromptInstructionOrigin` and preserve the default origin for existing callers.
- Modify `backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptComposer.cs`: render external skills with the untrusted boundary and descriptive metadata.
- Modify `backend-dotnet/RuBackend/Harness/Prompting/RuInstructionLoader.cs`: compose fixed root instructions with Markdown skill blocks from workspace, `.ru`, and user-data roots.
- Create `backend-dotnet/RuBackend.Tests/Harness/MarkdownSkillLoaderTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Harness/RuInstructionLoaderTests.cs` and `RuPromptContextProviderTests.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/SystemPromptContractTests.cs` and `SystemPromptComposerTests.cs`.
- Modify `docs/architecture.md`, `docs/operations/agent-tools.md`, `docs/operations/runtime-behavior.md`, and `CONTEXT.md` where their current wording omits Markdown skills or implies that prose grants authority.

## Global constraints

- Keep `SkillPackageLoader` and `SkillManifest` behavior unchanged for existing executable workflows.
- Do not add a network downloader, package manager, provider fallback, model fallback, or permission mechanism.
- Broker remains the only authority for path canonicalization, tool policy, approval, and execution.
- Discovery is deterministic and best-effort; one bad skill cannot stop prompt construction.
- Preserve unrelated dirty work and stage only files owned by this plan.
- Run heavyweight test projects sequentially.

### Task 1: Add typed instruction origins to the prompt contract

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptContext.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptComposer.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/SystemPromptContractTests.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/SystemPromptComposerTests.cs`

- [ ] **Step 1: Add RED contract tests**

Add tests that construct a `PromptInstructionBlock` with `PromptInstructionOrigin.ExternalSkill` and assert the composed prompt contains the exact external boundary, the stable `skill:` source ID, the display name, and the body. Keep an existing construction without the new argument and assert it remains the root/workspace origin.

Run:

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SystemPromptContractTests|FullyQualifiedName~SystemPromptComposerTests"
```

Expected: compilation fails because the origin type and constructor argument do not yet exist.

- [ ] **Step 2: Implement the minimal typed origin**

Add:

```csharp
public enum PromptInstructionOrigin
{
    Workspace,
    ExternalSkill,
}
```

Add an `Origin` property to `PromptInstructionBlock` with a default of `Workspace`, and serialize the origin beside each instruction block in `SystemPromptContext.SerializeUtf8()`. Update the composer so `ExternalSkill` uses the untrusted boundary and renders `name`/`description` metadata without changing any capability list. Keep source IDs and content digests bounded by the existing `PromptText` rules; `SystemPromptReceipt` remains digest-based and does not need a new authority field.

- [ ] **Step 3: Run focused GREEN tests and the Core suite**

Run the focused command from Task 1 Step 1, then run:

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore
```

Expected: all focused and complete Core tests pass with no skipped tests.

- [ ] **Step 4: Commit the prompt contract seam**

```powershell
git add -- backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptContext.cs backend-dotnet/RuAgent.Harness.Core/Prompting/SystemPromptComposer.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/SystemPromptContractTests.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/SystemPromptComposerTests.cs
git commit -m "feat(skills): mark external prompt instructions"
```

### Task 2: Parse and discover Markdown skills

**Files:**
- Create: `backend-dotnet/RuBackend/Harness/Prompting/MarkdownSkillLoader.cs`
- Create: `backend-dotnet/RuBackend.Tests/Harness/MarkdownSkillLoaderTests.cs`

- [ ] **Step 1: Write RED parser and discovery tests**

Cover these exact cases:

```csharp
[Fact]
public void Parses_front_matter_and_body_without_treating_allowed_tools_as_authority() { }

[Fact]
public void Accepts_markdown_without_front_matter_and_derives_metadata() { }

[Fact]
public void Ignores_unknown_front_matter_and_malformed_metadata_lines() { }

[Fact]
public void Discovers_sorted_skill_directories_and_reports_duplicate_by_root_precedence() { }

[Theory]
[InlineData("invalid_encoding")]
[InlineData("contains_nul")]
[InlineData("oversize")]
[InlineData("reparse_point")]
public void Skips_invalid_skill_without_stopping_other_skills(string expectedReason) { }
```

The tests must also create `references`, `assets`, and `scripts` files and assert discovery preserves/ignores them without executing or rejecting the package. Assert stable IDs are safe prompt identifiers, descriptions are bounded, and the returned content digest matches the body.

Run:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MarkdownSkillLoaderTests
```

Expected: compilation fails because the loader and result types do not exist.

- [ ] **Step 2: Implement bounded front-matter parsing**

Implement a focused loader with these public shapes:

```csharp
public sealed record MarkdownSkill(
    string Id,
    string Name,
    string Description,
    string Body,
    string ContentSha256,
    string SourcePath);

public sealed record MarkdownSkillSkip(string SourcePath, string Reason);

public sealed record MarkdownSkillLoadResult(
    IReadOnlyList<MarkdownSkill> Skills,
    IReadOnlyList<MarkdownSkillSkip> Skips);

public sealed class MarkdownSkillLoader
{
    public MarkdownSkillLoadResult Load(IReadOnlyList<string> roots);
}
```

Use strict UTF-8, normalization, NUL rejection, final-path containment, and
64 KiB per file / 256 KiB combined caps. Read only direct child directories
and a case-insensitive `SKILL.md`/`skill.md`. Treat a missing or malformed
front-matter fence as ordinary Markdown; parse only bounded scalar metadata and
the `allowed-tools` list, ignore unknown keys, and never turn metadata into
tool policy. Derive a safe lower-case ID from `name` or the folder name and
return it to the caller, which prefixes it as `skill:<id>`.

- [ ] **Step 3: Implement deterministic root precedence and skips**

Sort roots and child directories with ordinal comparison after applying the
documented precedence order from the caller. Reject reparse-point package
directories and files. Keep the first duplicate ID and append a bounded
`duplicate` skip for lower-precedence copies. Allow ancillary files but do not
open them.

- [ ] **Step 4: Run GREEN loader tests and commit**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MarkdownSkillLoaderTests
git diff --check
git add -- backend-dotnet/RuBackend/Harness/Prompting/MarkdownSkillLoader.cs backend-dotnet/RuBackend.Tests/Harness/MarkdownSkillLoaderTests.cs
git commit -m "feat(skills): load external markdown skill packages"
```

### Task 3: Integrate Markdown skills into fresh system prompts

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/Prompting/RuInstructionLoader.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/RuInstructionLoaderTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs`

- [ ] **Step 1: Add RED integration tests**

Create workspace fixtures containing `skills/alpha/SKILL.md`, `.ru/skills/beta/SKILL.md`, and a user-data root fixture. Assert `RuInstructionLoader.Load` returns root instructions first, then `skill:alpha`, `skill:beta`, and the user skill in documented precedence/order. Assert duplicate IDs select the first root and appear in skips. Assert every Markdown block has `ExternalSkill` origin and the existing root blocks retain `Workspace` origin.

Add a prompt-provider test that changes a `SKILL.md` between two `BuildAsync` calls and asserts the content digest changes and the second context sees the new body.

- [ ] **Step 2: Integrate the focused loader**

Extend `RuInstructionLoader` with a private `MarkdownSkillLoader`, derive the three roots from the canonical workspace and `Environment.SpecialFolder.LocalApplicationData`, and append Markdown blocks only after fixed root instruction blocks. Convert each result into a `PromptInstructionBlock` with `skill:<id>`, name/description metadata in its display content, the body, digest, and `ExternalSkill` origin. Keep skip records bounded and preserve existing missing-root behavior.

- [ ] **Step 3: Verify prompt cap and stable receipt behavior**

Use the existing `SystemPromptContext` cap rather than adding a second cap. When the combined cap or 16-block limit is reached, keep earlier deterministic blocks and record `oversize` skips for the rest. Confirm `SystemPromptReceipt.ValidateAgainst` still validates block order and digest for both origins. The receipt remains content-bound; origin is carried by prompt-context serialization and does not alter the receipt block-digest shape.

- [ ] **Step 4: Run GREEN integration tests and complete Backend tests**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~RuInstructionLoaderTests|FullyQualifiedName~RuPromptContextProviderTests"
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
```

Run the two commands sequentially and require 0 failures and 0 skips.

- [ ] **Step 5: Commit runtime integration**

```powershell
git add -- backend-dotnet/RuBackend/Harness/Prompting/RuInstructionLoader.cs backend-dotnet/RuBackend.Tests/Harness/RuInstructionLoaderTests.cs backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs
git commit -m "feat(skills): inject discovered markdown guidance"
```

### Task 4: Document the two skill formats and authority boundary

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `CONTEXT.md`

- [ ] **Step 1: Add documentation RED checks**

Search the active documents for the existing claims that skills require only
`skill.json`, that all Markdown is authority, or that `allowed-tools` grants
permissions. Record the exact sections to update; do not delete current
declarative workflow guidance.

- [ ] **Step 2: Document installation and behavior**

Document the `SKILL.md` example, supported roots, automatic reload on a new
run/restart, the distinction between prompt guidance and `SkillManifest`
execution, ancillary files, and the fact that Broker policy remains decisive.
Add the `Markdown Skill` term to `CONTEXT.md` with its “untrusted guidance,
not permission” invariant.

- [ ] **Step 3: Validate docs and commit**

```powershell
rg -n "SKILL\.md|markdown skill|SkillManifest|allowed-tools|skill\.json" CONTEXT.md docs/architecture.md docs/operations/agent-tools.md docs/operations/runtime-behavior.md
git diff --check
git add -- CONTEXT.md docs/architecture.md docs/operations/agent-tools.md docs/operations/runtime-behavior.md
git commit -m "docs(skills): document markdown skill installation"
```

### Task 5: Full verification and GUI-ready build

**Files:**
- No production changes expected unless verification exposes a regression.
- Update `docs/operations/verification.md` only with measured results.

- [ ] **Step 1: Run heavyweight suites sequentially**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --no-restore
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj --no-restore
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --no-restore
```

Run each command only after the previous one completes. Require 0 failures and
0 skips and separately run the focused Markdown skill tests again if any suite
uses a different test filter.

- [ ] **Step 2: Build the GUI and native overlay**

```powershell
cmd /c build.bat
```

Require all 13 build stages, `RuApp.exe` in the publish directory, and native
overlay tests passing. Do not claim live provider acceptance from a build alone.

- [ ] **Step 3: Record final state**

Run `git status --short --branch`, `git diff --check`, and inspect active docs
for stale contradictory skill instructions. Update verification notes only
with observed test/build counts, then commit those notes separately if changed.
