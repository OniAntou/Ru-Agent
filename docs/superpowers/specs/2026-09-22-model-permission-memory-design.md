# Model + Permission + Memory Implementation Design

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mo rong Ru theo huong B: Models.dev catalog 75+ provider, ladder permission plan → standard → full_access(yolo) bo trusted, setting threshold + target cho auto-compaction.

**Architecture:** Giu nguyen pipeline Host → Worker → Core va Broker policy hien co. Model them generic provider qua Models.dev probe. Permission doi preset enum + evaluator fallback. Memory them 2 setting doc tu SettingsStore xuong ContextBudgetPolicy.

**Tech Stack:** .NET 10, chat_completions protocol, SQLite AES-GCM, DPAPI, WinUI3, CLI TUI.

## Global Constraints

- Windows 10/11 64-bit, .NET SDK 10.0.
- Fail-closed moi noi: offline giu cache cu, unknown model khong probe, approval het han fail.
- Khong them subagent, MCP, hooks, plugin trong dot nay.
- Conventional Commits voi scope chi tiet, chi commit/push len nhanh dev.

---

## Part 1 — Model: Models.dev catalog

Hien tai 5 backend cung (`ProviderSelection.cs:48-64`): opencode, kilo, custom, openai, anthropic. Custom da la generic chat_completions nhung chua co catalog.

- [ ] Host fetch Models.dev catalog (75+ provider), cache local TTL 24h, offline giu cache cu, fail-closed khi khong co cache.
- [ ] Settings thay enum backend cung bang `provider_id + model_id` generic; giu lai 5 backend cu nhu alias de khong voe settings nguoi dung hien co.
- [ ] Probe model qua `chat_completions` giong custom hien co; unknown model `Supported=false`, khong tu suy URL.
- [ ] GUI picker hien provider/model tu catalog moi, van an group rong va danh dau stale nhu cu.
- [ ] Preset/mode khong doi endpoint, key, protocol — chi doi policy nhu hien tai.

Files: `RuBackend/Providers/ProviderCatalogService.cs`, `ProviderSelection.cs`, `Business/Settings/SettingsStore.cs`, `SettingsSnapshot.cs`, GUI `GroupedProviderCatalogController.cs`, docs `provider-catalog.md`.

## Part 2 — Permission: plan → standard → full_access(yolo)

Hien tai preset `standard/full_access/trusted` nhung `full_access` map ve standard (chet), `trusted` lift hep 4 ask in-workspace. Nay:

- [ ] `plan`: read-only tuyet doi. Moi mutation (write/edit/bash/web) deu ask hoac deny, khong auto.
- [ ] `standard` noi long: web_search/web_fetch khong can approval nua; terminal/file auto rong hon khi workspace sach (ke thua logic trusted cu, gop vao standard).
- [ ] `full_access(yolo)`: chi giu deny cung (protected path, invariant, schema invalid). Moi thu con lai auto, khong approval nao hien.
- [ ] Xoa preset `trusted` khoi wire enum (`BrokerPolicyContracts.cs`), GUI picker, CLI flags (`--preset`), docs. `full_access` khong con map ve standard.
- [ ] Web gate `RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL` doi mac dinh theo preset: plan bat, standard tat, yolo tat.

Files: `RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`, `RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs`, `RuAgent.Harness.Core/AgentPermissionPreset.cs`, `RuBackend/Harness/AgentV1Service.Policies.cs`, CLI `AgentCliParser.cs` + `AgentCommands.cs`, GUI `ChatView.xaml.cs` + `SettingsView.xaml.cs`, docs `agent-tools.md` + `runtime-behavior.md`.

## Part 3 — Memory: setting threshold + target

Hien tai auto-compaction chay preflight moi step, strategy Normal co dinh, nguong cung (`ContextBudgetPolicy`: trigger 0.80, target 0.62, reserve 256, safety 64). Khong co setting nao.

- [ ] Them 2 setting: `compaction_trigger_ratio` (mac dinh 0.80) + `compaction_target_ratio` (mac dinh 0.62). Validate 0 < target < trigger < 1, fail-closed ve default khi sai.
- [ ] `SettingsSnapshot` + `SettingsStore` + GUI Settings + docs. `ContextBudgetPolicy` doc tu settings, khong con hard-code.
- [ ] Auto van strategy Normal. Manual `/compact aggressive` giu nguyen. Khong them LLM-summary.

Files: `RuBackend/Business/Settings/SettingsSnapshot.cs`, `SettingsStore.cs`, `RuAgent.Harness.Core/Context/ContextBudgetPolicy.cs`, GUI Settings, docs.
