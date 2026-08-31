# Native Overlay Correctness and Pixel Cost Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make native Release tests meaningful without live Spout access, then reduce pixel work without changing output bytes.

**Architecture:** Separate pure options/style/pixel tests from opt-in Spout probes. Keep C++20 scalar behavior as the reference, fuse only the RGBA transformation, and avoid redundant waiting-status repaint without dropping received frames.

**Tech Stack:** C++20, CMake/CTest, Visual Studio Release, existing Spout dependency.

## Global Constraints

> Before executing, read the master plan's **Resume gate after concurrent implementation**.
> Several planned modules already exist in `e6fa40c`; inspect current code and
> verify missing acceptance evidence rather than recreating or replacing them.

Inherit [master constraints](2026-08-31-codebase-optimization.md). N1 is phase 1; N2 waits for phase 2. Never run existing Spout probes or the GUI as part of automated verification.

---

## N1 — Always-on Release checks, pure default tests

**Files:**

- Modify: `tools/ru_desktop_overlay/CMakeLists.txt`.
- Modify: `tools/ru_desktop_overlay/tests/overlay_options_tests.cpp`.
- Create: `tools/ru_desktop_overlay/tests/test_check.h`.
- Create: `tools/ru_desktop_overlay/tests/spout_connection_probe.cpp` only to retain the current missing-sender check as an explicit opt-in executable.
- Read: `src/frame_pixels.h`, `src/frame_pixels.cpp`, `src/overlay_options.cpp`, `src/window_styles.cpp` under the overlay directory.
- Modify if needed: `scripts/build_ru_desktop_overlay.ps1`; default must run pure CTest only.
- Docs: `docs/operations/verification.md`.

**Interfaces:** Existing pure C++ function signatures remain unchanged. New test-only always-on check:

```cpp
#pragma once
#include <cstdio>
#include <cstdlib>

#define CHECK(condition) do { \
    if (!(condition)) { \
        std::fprintf(stderr, "%s:%d: check failed: %s\n", __FILE__, __LINE__, #condition); \
        return EXIT_FAILURE; \
    } \
} while (false)
```

Use it in int-returning tests/main only; do not introduce an assert replacement into production.

- [ ] **Step 1 — Isolate live calls before enabling checks.**

Remove `spout_overlay.h`, construction/Start/Tick of the missing-sender fixture, `src/spout_overlay.cpp`, and Spout linkage from the pure test target. Preserve that check in the opt-in probe, never registered in default CTest. Put probe targets behind `RU_BUILD_NATIVE_PROBES=OFF`; do not remove the product's Spout dependency.

No downloads/bootstrap are authorized. Discover available Visual Studio CMake with the existing script logic. A missing dependency is a reported build blocker, not permission to install software.

- [ ] **Step 2 — Prove Release detects failed assertions (RED then GREEN).**

Add a pure test mode `--self-test-failure` that deliberately fails before any other work. First implement the deliberate failure using the old assert mechanism and observe that Release exits 0 incorrectly; then replace all pure assertions with CHECK and observe nonzero exit. A missing executable/build error is not this proof.

Register both normal and inverted self-check:

```cmake
add_test(NAME overlay_options_tests COMMAND overlay_options_tests)
add_test(NAME overlay_check_failure
  COMMAND overlay_options_tests --self-test-failure)
set_tests_properties(overlay_check_failure PROPERTIES WILL_FAIL TRUE)
```

Main's mode check after the fix:

```cpp
if (argc == 2 && std::string_view(argv[1]) == "--self-test-failure") {
    CHECK(false);
}
```

Include `<string_view>`, update main's parameters, preserve all existing literal option/style/pixel expectations. Run Release CTest with probes disabled. Confirm the inverted test fails if CHECK silently returns success.

- [ ] **Step 3 — Mutation gate and baseline.**

Temporarily change one expected premultiplied byte in the pure test from 50 to 51; Release CTest must fail. Restore only the deliberate edit, rerun GREEN, and report both exit codes. Keep NDEBUG in the product build; do not switch acceptance to Debug.

Record test count, config and source revision; distinguish pure checks from native device acceptance. No live window or sender test.

## N2 — Fused RGBA transform and unchanged-status suppression

**Files:**

- Modify: `tools/ru_desktop_overlay/src/frame_pixels.h`, `frame_pixels.cpp`, `spout_overlay.cpp`, `main.cpp`.
- Extend pure test file; optionally split pixel cases into `tests/frame_pixels_tests.cpp`.
- Create optional pure benchmark `tests/frame_pixels_benchmark.cpp`, target
  `frame_pixels_benchmark` behind `RU_BUILD_PIXEL_BENCHMARKS=OFF` by default.

**Interfaces:** New production function (all old functions remain until consumer scan permits removal):

```cpp
void PrepareRgbaForOverlay(unsigned char* pixels, std::size_t pixel_count);
```

Its exact output equals baseline `RemoveGreenMatteRgba -> ConvertRgbaToBgra -> PremultiplyBgra`. Alpha truncation is integer division by 255. Green matte removes a pixel only when green exceeds max(red, blue) + 24. BGRA input paths retain their current separate behavior.

- [ ] **Step 1 — RED and baseline.**

Add hand-checked literal vectors: transparent, opaque, alpha 128, green dominance exactly 24 and 25, zero pixels, and a colored semitransparent pixel:

```cpp
unsigned char pixel[] = {100, 200, 50, 128}; // green matte: 200 > 100 + 24
PrepareRgbaForOverlay(pixel, 1);
CHECK(pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 0 && pixel[3] == 0);
unsigned char colored[] = {100, 110, 50, 128};
PrepareRgbaForOverlay(colored, 1);
CHECK(colored[0] == 25 && colored[1] == 55 && colored[2] == 50 && colored[3] == 128);
```

Characterize these byte vectors against the existing three-pass path before refactoring. Implement the new signature only after those tests exist; then run the same assertions against the fused function. Temporarily mutate one channel or matte threshold to prove the tests fail, restore only that mutation, and verify GREEN. Compare fixed-seed random frames too; independent literal vectors remain required so the old implementation is not the only oracle.

Capture old transform baseline on synthetic 720p/1080p buffers; fixed seed, 5 warmups, 30 measurements, observable checksum to prevent dead-code elimination.

- [ ] **Step 2 — GREEN: one loop.**

For each RGBA pixel, load original channels/alpha, apply the unchanged matte predicate, then write premultiplied B/G/R/A once. Do not use floating-point alpha, SIMD approximations, new shaders, or change the BGRA branch. Replace only the three-call RGBA sequence in SpoutOverlay.

- [ ] **Step 3 — Avoid unchanged status repaint.**

Record waiting/error status last presented and geometry/visibility invalidations. Skip only a repeated status with unchanged presentation state. A received frame must still present even if status text matches. Resize, re-show and sender transition must invalidate suppression.

Factor this small decision into a pure helper only if needed to test it. Test changed status, unchanged status, geometry invalidation, first status after a frame, and a new frame with identical status. Do not launch a window for these tests.

- [ ] **Step 4 — Measure and document.**

Run pure Release tests and the same benchmark. Require byte-identical output and lower pixel-pass count; report timing noise honestly. Keep a change with no measured benefit only for a separately justified correctness reason. Native visual/VTS acceptance remains user-run, not silently accepted.

**Commands (discover executable paths, then execute without probes):**

```powershell
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'C++ tools unavailable; do not install automatically.' }
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ctest = Join-Path (Split-Path $cmake) 'ctest.exe'
& $cmake -S tools/ru_desktop_overlay -B tools/ru_desktop_overlay/out/build -DRU_BUILD_NATIVE_PROBES=OFF -DVCPKG_MANIFEST_INSTALL=OFF
if ($LASTEXITCODE -ne 0) { throw 'Configure failed; inspect existing toolchain, do not bootstrap.' }
& $cmake --build tools/ru_desktop_overlay/out/build --config Release --target overlay_options_tests
if ($LASTEXITCODE -ne 0) { throw 'Native test build failed.' }
& $ctest --test-dir tools/ru_desktop_overlay/out/build -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Native pure tests failed.' }
```

These commands assume the audited existing CMake cache/toolchain; for a new worktree configure with the verified existing vcpkg toolchain, never copy/delete a broad cache blindly.

If pixel tests are split into a new `frame_pixels_tests` target, build that
target before running CTest. For the opt-in synthetic benchmark after N1:

```powershell
& $cmake -S tools/ru_desktop_overlay -B tools/ru_desktop_overlay/out/build -DRU_BUILD_NATIVE_PROBES=OFF -DVCPKG_MANIFEST_INSTALL=OFF -DRU_BUILD_PIXEL_BENCHMARKS=ON
if ($LASTEXITCODE -ne 0) { throw 'Benchmark configure failed.' }
& $cmake --build tools/ru_desktop_overlay/out/build --config Release --target frame_pixels_benchmark
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
& tools/ru_desktop_overlay/out/build/Release/frame_pixels_benchmark.exe
if ($LASTEXITCODE -ne 0) { throw 'Synthetic benchmark failed.' }
```

The benchmark executable must contain only synthetic buffer transforms, not
Spout/window/device setup. Reconfigure `RU_BUILD_PIXEL_BENCHMARKS=OFF` after
recording results; do not leave an opt-in target silently enabled for releases.
