# Provider isolation and lifecycle review remediation

**Goal:** Fix the five findings approved by the user after review of `4521785` and its dirty working tree.

**Architecture:** Provider bootstrap reads exactly the canonical account for the selected provider, never an alias or another provider. GUI/CLI are clients of a shared Public Host; disposing a client detaches rather than terminating the Host. Startup cancellation prevents post-close work. Policy error responses contain stable safe messages, not raw exceptions.

**Constraints:** Preserve unrelated dirty changes. No credential migration or live cloud/device calls. Test doubles use synthetic credentials and temporary data only. Run heavyweight suites sequentially. The initial repair excluded publishing and Git handoff; the user subsequently authorized GUI rebuild, commit and push.

- [x] Add failing provider-isolation tests against real protected bootstrap, remove both credential fallbacks, and verify canonical/missing/local-key cases.
- [x] Add client lifetime regression tests; remove GUI-owned process termination and job ownership from PublicHostStarter. Keep Host-owned shutdown and Worker/Broker ownership unchanged.
- [x] Test a small startup-lifetime helper with held asynchronous work; wire cancellation/checkpoints into MainWindow and voice-status startup requests. Close cancels before awaiting teardown.
- [x] Inject policy/preset failures into real backend endpoints; assert safe bounded error responses and preserved cancellation. Replace raw exception messages.
- [x] Correct dependency inventory, ADR and TODO acceptance claims. Keep unmeasured performance and live acceptance open.
- [x] Run focused RED/GREEN checks, then Python, all .NET test projects, GUI compile, lint/typecheck/dependency/route gates and diff check. Record actual results in verification docs.

Review gate: compare final changes with these five requirements; do not treat passing generic suites as proof of credential isolation or live GUI acceptance.

Completed 2026-09-01: Python 405, .NET 2,100 across 13 projects, GUI Release/x64
compile with zero warnings/errors; lint, typecheck, dependency and public-route
checks passed. See [verification](../../operations/verification.md) for scope
and evidence. That checkpoint did not commit/push or publish the GUI.

The authorized release follow-up found that `build.bat` copied XAML from the
wrong intermediate directory and ignored copy errors. Clean/publish/copy now
agree on x64, and missing/copy-failed XAML stops the build. Four new regression
cases failed before the fix, then passed; the full Python suite passed 409.
See the release follow-up in verification for publish and Git handoff scope.
