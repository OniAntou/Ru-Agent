# Reliability repair implementation plan

**Goal:** Repair the verified Settings, process-isolation, timeout, UI-contract,
and stale-documentation defects in the current Ru checkout.

**Status:** Implemented and locally verified on 2026-08-09.

## Task 1: Settings rollback and llama.cpp process isolation

**Files:**
- Modify: `src/mini_gpt/routers/settings.py`
- Modify: `src/mini_gpt/llama_cpp_runtime.py`
- Test: `tests/test_server_api.py`
- Test: `tests/test_llama_cpp_runtime.py`

1. Add a failing API test showing that a backend-load failure preserves the
   previous saved backend/model configuration.
2. Make the Settings route construct a candidate runtime configuration, load
   with staged backend credentials, persist runtime state and credentials only
   after success, retain unrelated preferences from the same request when
   loading fails, and restore the full prior runner state.
3. Add a failing concurrent-request test proving that one backend transition
   cannot enter `load` while an earlier transition is still running, then
   serialize the full runtime transition.
4. Add a failing runtime test showing that an occupied local port aborts launch
   without spawning or killing a process.
5. Replace stale-server termination with loopback-port collision detection and
   use an authenticated `/v1/chat/completions` startup probe that also verifies
   rejection of a wrong bearer. `/v1/models` is public in this llama-server
   build, so it cannot prove ownership; fail fast if a foreign listener wins the
   post-check spawn race or accepts arbitrary protected requests.
6. Run the focused Python tests for both modules.

## Task 2: Bounded background work after timeout

**Files:**
- Modify: `src/mini_gpt/voice_stream.py`
- Modify: `src/mini_gpt/search.py`
- Test: `tests/test_voice_stream.py`
- Test: `tests/test_search.py`

1. Add a failing VoiceStream test proving its model lock stays held until a
   timed-out thread exits.
2. Shield worker tasks, track actual thread completion, and defer cleanup
   until that completion callback runs.
3. Replace executor replacement with bounded in-flight search admission and
   retain a shutting-down executor until its active workers finish.
4. Add a failing search test proving repeated timeouts use no more than the
   configured worker capacity and recover after workers finish.
5. Run the focused Python tests.

## Task 3: Serialize structured-memory access with clearing

**Files:**
- Modify: `src/mini_gpt/backend_core.py`
- Test: `tests/test_backend_core.py`

1. Add a failing concurrency test showing that clear waits for an in-flight
   core memory write.
2. Use the core store lock across write and retrieval operations; keep the
   one-time recovery path inside that same lock.
3. Run the backend-core regression tests.

## Task 4: Chat contract and documentation

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Test: `gui-winui3.Tests/`
- Modify: `README.md`
- Modify: relevant OmniVoice specifications and plans under `docs/`

1. Add a failing source-contract test for emoji in ChatView output.
2. Remove the hard-coded emoji from user-visible ChatView errors.
3. Expand the contract test to both ChatView XAML and code-behind, with explicit
   coverage for emoji presentation and composition ranges.
4. Mark rejected DSP documents historical and describe the current native-speed
   path without claiming unperformed listening validation.
5. Run GUI tests, source scans, full regressions, and the release build.
