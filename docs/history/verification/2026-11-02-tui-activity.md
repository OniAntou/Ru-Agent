---
status: historical
date: 2026-11-02
---

# Compact TUI activity and conversation flow

Historical evidence at the working tree based on `922335d4`. Current behavior is
documented in [TUI architecture](../../architecture/tui.md#activity-and-conversation-flow)
and the [CLI controls](../../../backend-dotnet/RuAgent.Cli/README.md).

The user approved compact activity with details on demand. Native rows now show
observed action verbs, aligned targets, tool-specific units and available exit
codes. Each identified action owns an inspector containing bounded safe context,
outcome, metrics, failure code and retained diff. Late metadata refreshes the same
row and inspector without consuming another action or changing its expansion.
Normal and late rows retain semantic paint. Native group summaries describe the
work before the following prose; line/JSON output ownership remains unchanged.

Live presentation distinguishes model waiting, reported thinking, streaming,
compaction and tool/request retries. A withdrawn response returns to model waiting.
Retry attempt counts are never rendered as result counts. Failed/unknown operations
do not claim successful edits. A later tool inspector removes the stale thinking
shortcut from the turn footer. Target columns remain stable as common actions
settle; narrow rows prioritize failure and exit evidence.

## Verification

- New behavioral tests first failed for missing metadata/inspectors, phase copy,
  target alignment and native expansion (`tui-activity-red.log`,
  `tui-activity-native-red.log` and the `*-edges-red.log` / alignment logs).
- An intermediate focused run had 252 passes and one failure: choosing generic
  activity success metadata had hidden the durable `exit_1` failure reason.
  Meaningful failure codes now take priority while the durable fallback and its
  existing assertion remain (`tui-activity-focused-1.log`). The next focused run
  passed 257 tests (`tui-activity-focused-2.log`).
- The first full CLI run had **2,068 passes and one failure**: the long-paragraph
  test still matched the old `2 tools` summary. Its expected summary was updated
  to `2 searched`; paragraph text, prefix retention and summary ordering assertions
  were preserved (`tui-activity-cli-full.log`).
- An intermediate complete CLI suite passed **2,073 tests**
  (`tui-activity-cli-final.log`). After adding and fixing the native late-resize
  regression below, the final full CLI build/suite passed **2,074 tests, zero
  failures/skips** (`tui-activity-cli-final-2.log`). It ran under `Global\ru-test-matrix-run`, with a
  120-second hang collector and no automatic retries. The external runner invoked
  `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --no-restore`.
- Windows ConPTY passed **6/6 cases**: control, BMP, emoji, fragmented input,
  navigation and typing. `scripts/test_terminal_input.ps1` built its Release
  probe successfully and ran serially under the same matrix lock
  (`tui-activity-conpty/results.json`, `tui-activity-conpty.log`).
- A follow-up native resize regression exposed the legacy row updater appending
  a shortcut after typed spans had already been fitted to a narrower width
  (`tui-activity-late-resize-red.log`). Structured updates now preserve their
  fitted text/spans exactly; the legacy text importer retains its existing
  shortcut behavior. The inspector remains accessible after the update.
- Documentation and source-encoding guards passed **8 tests**
  (`tui-activity-docs.log`); the final documentation-only checkpoint uses
  `tui-activity-docs-final.log`. Current-tree whitespace checks also passed.

Native cell buffers were captured at 40, 80 and 120 columns in seven configurations
covering live, completed, failed and expanded presentation. `tui-activity-visual/`
contains JSON buffers, text and PNG renders. The capture fixture initially assumed
full-window transcript width and used a fixed resize delay; it was corrected to
honor the four-cell inset and wait for ANSI startup/viewport agreement. Product
layout and driver behavior were not changed for the probe. PNGs use the native
cells' text/colors with illustrative events and fonts rendered outside the repo;
they are not screenshots or timing measurements of the user's Windows Terminal.

All evidence/probe files live outside the repository in the session's review
artifact directory. No provider call, user-session read, desktop automation,
permission/protocol change, installed-runtime replacement, commit or push was
performed for this UI task. The full Host/Broker/GUI matrices were not rerun for
this client-only change. Physical terminal/IME and live-provider acceptance remain
separate from deterministic native rendering and input evidence.
