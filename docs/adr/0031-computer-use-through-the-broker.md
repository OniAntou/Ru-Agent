---
status: accepted
date: 2026-10-27
---

# Computer use runs through the Broker with per-app session grants

## Context

The owner wants Ru to operate desktop applications: look at the screen, move the
pointer, click and type, the way computer-use agents do. That gives the model the
user's whole interactive session, so the design starts from the existing
invariants:

- **One execution authority.** Screen capture and synthetic input are side
  effects and run only in `BrokerExecutionRuntime` behind `IToolGateway`
  ([ADR 0009](0009-broker-execution-authority.md),
  [ADR 0014](0014-in-process-execution-and-terminal-sandbox.md)).
- **No self-approval.** Approval prompts live in Ru's own windows: the desktop
  app and the terminal that hosts the TUI. Input that reaches them would let the
  model approve its own requests.
- **No silent substitution.** A screenshot is an image. Only a pinned model that
  accepts images may receive one ([ADR 0026](0026-read-returns-workspace-images.md)).
- **Privacy.** A screenshot of the whole desktop sends every visible window, such
  as mail or a password manager, to the provider.
- **Parallel sessions.** Sessions run in parallel
  ([ADR 0020](0020-always-parallel-workspace-sessions.md)), but there is one
  pointer and one keyboard.

## Decision

1. **Opt-in, pinned per run.** A Host setting, `computer_use_enabled`, is off by
   default. When a new run's provider selection is pinned with the setting on and
   with image input accepted, the selection carries the `_ru_computer_use_v1`
   marker. The marker is removed before any request option reaches a provider.
   Runs pinned without it never see the tool, and a setting change applies to
   new runs only. With the setting on and a model without image input, the tool
   is not advertised and the run's selection records no marker.
2. **One `computer` tool.** `RuAgent.Protocol` gains one definition, `computer`,
   with an `action` of `list`, `request_access`, `focus`, `screenshot`, `click`,
   `move`, `drag`, `scroll`, `type` or `key`. `focus` (amended 2026-10-27) brings a
   granted app's topmost window to the front, so the model is not stuck when
   another window covers the app; it shares the foreground thread's input state
   for that one call instead of sending input to the window it replaces. It belongs to every coding profile but is
   advertised only for a run that carries the marker. Subagents and `run_code`
   programs never receive it.
3. **Policy.** Every action is classified as a mutation, so `plan` denies all of
   them, including `list` and `screenshot`. `request_access` names applications
   by executable file name and is evaluated as the `ExternalTerminal` subject:
   `standard` asks, `full_access` allows. The approval binds the exact app list.
   Executing an approved `request_access` records a **session app grant** in the
   Broker session. Every other action is allowed by policy and checked against
   the grants when it executes. "Always allow" saves an ordinary project rule for
   the exact app list, so a later `request_access` for those apps does not ask;
   the model still has to request access in each session.
4. **Grant enforcement at execution.** Immediately before each input event the
   Broker resolves the target: the root window under the pointer for clicks,
   moves and scrolls (both ends of a drag), and the foreground window for typing
   and keys. In `standard` the target process must hold a session grant
   (`computer_app_not_granted`). The target is checked again before every click
   of a multi-click and every typed character, and the action stops with
   `computer_target_changed` when another window took its place. The Windows key
   is refused unless Explorer is granted (`computer_key_not_allowed`). A UWP
   window is attributed to its app's own executable, never to
   `ApplicationFrameHost`.
5. **Protected applications are never targets.** Ru itself (`RuApp`, `RuBackend`,
   `RuAgent.Cli`, `ru`), terminal hosts (`WindowsTerminal`, `OpenConsole`,
   `conhost`), and credential and elevation UI (`consent`, `CredentialUIBroker`,
   `LogonUI`), and every process that hosts a running Ru CLI (for example an
   editor's integrated terminal) cannot be requested or targeted in any preset,
   including
   `full_access` (`computer_app_protected`). This is a deliberate exception to
   "full_access is literally full": without it, any explicit ask rule could be
   clicked through. Elevated windows are refused (`computer_target_elevated`)
   because Windows drops input to them without reporting an error.
6. **Screenshots show granted windows only.** In `standard` the Broker captures a
   monitor, then blacks out every pixel that is not the visible part of a granted
   top-level window, using the z-order, DWM frame bounds, and skipping cloaked
   and minimized windows. The window list is read before and after the capture,
   and a pixel stays only when both lists allow it. `full_access` returns the
   unmasked monitor. Images are
   PNG, at most 1,280 pixels on the long edge, and travel through the ADR 0026
   `ToolImage` path, so only the newest four reach a request. `list` returns
   monitors and applications; window titles appear only for granted apps.
7. **All monitors, screenshot-relative coordinates.** `screenshot` takes a
   monitor index. Pointer actions name the monitor and use pixel coordinates in
   that monitor's last screenshot. An action without a screenshot of its monitor
   fails (`computer_screenshot_required`), and so does an action after the
   monitor's geometry changed (`computer_display_changed`).
8. **One controller.** One run at a time controls the desktop. Another run's
   action fails with `computer_busy` until the controlling run ends or stays idle
   for two minutes. Every action is a tool-batch barrier. Input receipts report
   possible side effects, so earlier verification becomes stale, as after any
   terminal command. Observation actions (`list`, `screenshot`) are exempt from
   the repeated-tool-call guard.
9. **Emergency stop.** While computer use is enabled, the Broker registers the
   global hotkey **Ctrl+Alt+Backspace** (or **Ctrl+Alt+Pause** on keyboards that have it). Pressing it cancels the action in flight and
   asks the Host to cancel the controlling run. Ctrl+Alt+Backspace is the
   fallback for keyboards without a Pause key (amended 2026-10-27). If Ctrl+Alt+Backspace
   cannot be registered, every action fails with `computer_stop_unavailable`. A locked or
   secure desktop fails with `computer_desktop_unavailable`.

## Alternatives and consequences

- Asking on every click is safe but unusable for multi-step tasks; the owner
  chose per-app grants.
- An unmasked screenshot in `standard` would make the grant meaningless for
  privacy, so masking costs some context: the model sees black where it has no
  grant and must request access to see more.
- UI Automation trees would give exact element names, but they are a second,
  much larger surface. Pixels and synthetic input match the providers'
  computer-use training and keep one bounded adapter.

Synthetic input runs with the user's rights. A granted application can do
anything that application can do, and Ru does not confine it. The grant, the
protected-app list and the stop hotkey are the boundary. The owner accepted this
decision on 2026-10-27.
