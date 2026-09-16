---
status: accepted
date: 2026-09-16
---

# Product logic stays out of WinUI code-behind

An architecture review of the desktop client applied the deletion test to the
small `*Policy` modules extracted from `ChatView.xaml.cs` and `App.xaml.cs` and
concluded that several of them are shallow: `ChatHistoryReloadPolicy` (27 lines,
one member) and `AppUnhandledExceptionPolicy` (28 lines, one member) have an
interface nearly as wide as their implementation, and each has exactly one
production caller. The deletion test therefore recommends inlining them back
into the caller.

We decided to keep them out of the code-behind anyway.

`gui-winui3.Tests` cannot load WinUI: `RuApp` is a WinUI 3 executable whose
`App.xaml.cs`, views, and controls all touch the XAML runtime, so the test
project has never been able to instantiate `ChatView` or `App`. The existing
test suite reaches product decisions only because those decisions live in
modules that do not reference WinUI. Inlining a decision into
`ChatView.xaml.cs` line 2864 or `App.xaml.cs` line 21 would not move a
complexity from one place to another — it would remove it from the reach of the
test suite entirely, leaving hand-testing as the only verification.

The testability constraint therefore outranks interface width for this
codebase, and the modules stay.

## Consequences

- A single-caller pure module is still worth extracting when the caller is UI
  code. The relevant measure is not "how many callers", but "can it be reached
  by a test". `ReloadActivitiesPolicy` and `TerminalFailureCardPolicy` are the
  same shape and stay for the same reason.
- Interface width is a poor proxy for shallowness here. `AgentApprovalPresentation`
  is 41 lines with one member yet encodes a non-obvious precedence rule (the
  controller's own proposal wins; the activity feed is only a fallback for a run
  reattached mid-flight). Extraction is justified by what the module knows, not
  by how many lines it hides.
- The duplication this leaves is real and is accepted: callers still re-check
  some of what a policy already decided, because the caller's use of the answer
  is not the same as the policy's question. `ReloadSessionHistoryAsync` checks
  `IsNullOrWhiteSpace(streamedReply)` and `LooksLikeToolSelection` in addition to
  `ChatHistoryReloadPolicy.ShouldPreserveStreamedReply`, and the merge of those
  two decisions is not yet stated in one place.
- A future review should not re-propose inlining these modules without first
  establishing that `gui-winui3.Tests` can reach the caller.
