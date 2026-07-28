# Ru Spout2 Desktop Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local Windows overlay that receives VTube Studio's transparent `VTubeStudioSpout` output and displays it above ordinary desktop applications.

**Architecture:** A C++20/Win32 executable owns a native SpoutLibrary receiver, reads BGRA frames with alpha, and displays them through a borderless layered window. The options and extended-window-style functions are pure, separately testable modules; the receiver layer alone owns the Spout/frame-buffer lifecycle and handles sender reconnection.

**Tech Stack:** C++20, Win32 layered windows, SpoutLibrary through vcpkg, CMake/CTest, Visual Studio C++ Desktop workload.

---

## File structure

- `tools/ru_desktop_overlay/vcpkg.json`: native Spout2 dependency manifest.
- `tools/ru_desktop_overlay/CMakeLists.txt`: CMake build and CTest targets.
- `tools/ru_desktop_overlay/src/overlay_options.h/.cpp`: command-line parsing.
- `tools/ru_desktop_overlay/src/window_styles.h/.cpp`: topmost/click-through style calculation.
- `tools/ru_desktop_overlay/src/spout_overlay.h/.cpp`: SpoutLibrary frame-buffer lifetime and reconnect state.
- `tools/ru_desktop_overlay/src/main.cpp`: native window, controls, and message loop.
- `tools/ru_desktop_overlay/tests/overlay_options_tests.cpp`: no-GPU unit tests.
- `scripts/build_ru_desktop_overlay.ps1`: explicit build prerequisite/build wrapper.
- `scripts/start_ru_desktop_overlay.ps1`: safe launch wrapper.
- `README.md`: verified user setup and recovery instructions.

### Task 1: Create a build shell that never installs prerequisites implicitly

**Files:**
- Create: `tools/ru_desktop_overlay/vcpkg.json`
- Create: `tools/ru_desktop_overlay/CMakeLists.txt`
- Create: `scripts/build_ru_desktop_overlay.ps1`

- [ ] **Step 1: Check the C++ workload without changing the machine**

Run:

```powershell
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
```

Expected: one Visual Studio path. If it is empty, stop and ask the user to install **Desktop development with C++**. Do not invoke the Visual Studio installer automatically.

- [ ] **Step 2: Add the vcpkg manifest**

Create `tools/ru_desktop_overlay/vcpkg.json`:

```json
{
  "name": "ru-desktop-overlay",
  "version-string": "0.1.0",
  "dependencies": ["spout2"]
}
```

- [ ] **Step 3: Add the first CMake target**

Create `tools/ru_desktop_overlay/CMakeLists.txt`:

```cmake
cmake_minimum_required(VERSION 3.25)
project(RuDesktopOverlay LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
find_package(Spout2 CONFIG REQUIRED)
add_executable(RuDesktopOverlay WIN32 src/main.cpp)
target_link_libraries(RuDesktopOverlay PRIVATE Spout2::SpoutLibrary d3d11 dxgi d3dcompiler user32 gdi32)
```

- [ ] **Step 4: Add an explicit build wrapper**

Create `scripts/build_ru_desktop_overlay.ps1`:

```powershell
[CmdletBinding()]
param([switch]$BootstrapVcpkg)
$ErrorActionPreference = 'Stop'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Visual Studio Desktop development with C++ is required. Install it manually, then rerun this script.' }
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $cmake)) { throw 'The Visual Studio CMake component is required. Add it in Visual Studio Installer, then rerun this script.' }
if (-not $env:VCPKG_ROOT) {
  if (-not $BootstrapVcpkg) { throw 'Set VCPKG_ROOT or rerun with -BootstrapVcpkg to clone vcpkg into .tmp.' }
  $vcpkgRoot = Join-Path $PSScriptRoot '..\.tmp\vcpkg'
  if (-not (Test-Path $vcpkgRoot)) { git clone https://github.com/microsoft/vcpkg $vcpkgRoot }
  & (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat')
  $env:VCPKG_ROOT = (Resolve-Path $vcpkgRoot)
}
$source = Join-Path $PSScriptRoot '..\tools\ru_desktop_overlay'
$build = Join-Path $source 'out\build'
$toolchain = Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake'
& $cmake -S $source -B $build -DCMAKE_TOOLCHAIN_FILE=$toolchain
& $cmake --build $build --config Release
```

- [ ] **Step 5: Verify the initial failure surface**

Run `./scripts/build_ru_desktop_overlay.ps1`.

Expected: before the workload exists, the explicit workload error; after it exists but before `main.cpp` exists, CMake reports the missing source file.

- [ ] **Step 6: Commit**

```powershell
git add tools/ru_desktop_overlay/vcpkg.json tools/ru_desktop_overlay/CMakeLists.txt scripts/build_ru_desktop_overlay.ps1
git commit -m "build: scaffold Ru Spout overlay"
```

### Task 2: Add pure option and window-style tests

**Files:**
- Create: `tools/ru_desktop_overlay/src/overlay_options.h`
- Create: `tools/ru_desktop_overlay/src/overlay_options.cpp`
- Create: `tools/ru_desktop_overlay/src/window_styles.h`
- Create: `tools/ru_desktop_overlay/src/window_styles.cpp`
- Create: `tools/ru_desktop_overlay/tests/overlay_options_tests.cpp`
- Modify: `tools/ru_desktop_overlay/CMakeLists.txt`

- [ ] **Step 1: Write the failing tests**

Create `tools/ru_desktop_overlay/tests/overlay_options_tests.cpp`:

```cpp
#include "overlay_options.h"
#include "window_styles.h"
#include <cassert>
int main() {
  const char* defaults[] = {"RuDesktopOverlay"};
  const auto normal = ParseOptions(1, defaults);
  assert(normal.sender_name == "VTubeStudioSpout" && !normal.click_through);
  const char* custom[] = {"RuDesktopOverlay", "--sender", "RuPreview", "--click-through"};
  const auto selected = ParseOptions(4, custom);
  assert(selected.sender_name == "RuPreview" && selected.click_through);
  assert((OverlayExStyle(false) & WS_EX_TRANSPARENT) == 0);
  assert((OverlayExStyle(true) & WS_EX_TRANSPARENT) != 0);
  return 0;
}
```

Append to `CMakeLists.txt`:

```cmake
include(CTest)
add_executable(overlay_options_tests tests/overlay_options_tests.cpp src/overlay_options.cpp src/window_styles.cpp)
target_include_directories(overlay_options_tests PRIVATE src)
target_link_libraries(overlay_options_tests PRIVATE user32)
add_test(NAME overlay_options_tests COMMAND overlay_options_tests)
```

- [ ] **Step 2: Confirm the test fails**

Run `cmake --build .\tools\ru_desktop_overlay\out\build --config Release --target overlay_options_tests`.

Expected: compilation fails because the headers/functions are absent.

- [ ] **Step 3: Add minimal implementations**

Create `src/overlay_options.h` and `src/overlay_options.cpp`:

```cpp
// overlay_options.h
#pragma once
#include <string>
struct OverlayOptions { std::string sender_name{"VTubeStudioSpout"}; bool click_through{false}; };
OverlayOptions ParseOptions(int argc, const char* const argv[]);

// overlay_options.cpp
#include "overlay_options.h"
#include <stdexcept>
OverlayOptions ParseOptions(int argc, const char* const argv[]) {
  OverlayOptions result;
  for (int i = 1; i < argc; ++i) {
    const std::string arg{argv[i]};
    if (arg == "--click-through") result.click_through = true;
    else if (arg == "--sender" && i + 1 < argc) result.sender_name = argv[++i];
    else throw std::invalid_argument("Usage: RuDesktopOverlay [--sender NAME] [--click-through]");
  }
  return result;
}
```

Create `src/window_styles.h` and `src/window_styles.cpp`:

```cpp
// window_styles.h
#pragma once
#include <windows.h>
LONG_PTR OverlayExStyle(bool click_through);

// window_styles.cpp
#include "window_styles.h"
LONG_PTR OverlayExStyle(bool click_through) {
  LONG_PTR value = WS_EX_LAYERED | WS_EX_TOOLWINDOW;
  if (click_through) value |= WS_EX_TRANSPARENT;
  return value;
}
```

- [ ] **Step 4: Run the unit tests and commit**

Run:

```powershell
cmake --build .\tools\ru_desktop_overlay\out\build --config Release --target overlay_options_tests
ctest --test-dir .\tools\ru_desktop_overlay\out\build -C Release --output-on-failure
```

Expected: `100% tests passed`.

```powershell
git add tools/ru_desktop_overlay/src/overlay_options.* tools/ru_desktop_overlay/src/window_styles.* tools/ru_desktop_overlay/tests/overlay_options_tests.cpp tools/ru_desktop_overlay/CMakeLists.txt
git commit -m "feat: add Ru overlay controls"
```

### Task 3: Implement the Spout2 frame receive and alpha presentation lifecycle

**Files:**
- Create: `tools/ru_desktop_overlay/src/spout_overlay.h`
- Create: `tools/ru_desktop_overlay/src/spout_overlay.cpp`
- Modify: `tools/ru_desktop_overlay/CMakeLists.txt`

- [ ] **Step 1: Define the receiver interface**

Create `src/spout_overlay.h`:

```cpp
#pragma once
#include <string>
class SpoutOverlay {
 public:
  explicit SpoutOverlay(std::string sender_name);
  bool Start(); bool Tick(); bool IsConnected() const;
  const std::string& Status() const; void Stop();
  void Render(HWND window); void RenderStatus(HWND window);
 private:
  std::string sender_name_, status_{"Waiting for VTubeStudioSpout"};
  class Impl; Impl* impl_{nullptr};
};
```

- [ ] **Step 2: Implement Spout ownership and reconnect state**

Create `src/spout_overlay.cpp` around the official SpoutLibrary receiver functions:

```cpp
#include "spout_overlay.h"
#include <SpoutLibrary/SpoutLibrary.h>
class SpoutOverlay::Impl {
 public:
  SPOUTHANDLE receiver{GetSpout()};
  std::vector<unsigned char> pixels;
  ~Impl() { if (receiver) { receiver->ReleaseReceiver(); } }
};
SpoutOverlay::SpoutOverlay(std::string name) : sender_name_(std::move(name)) {}
bool SpoutOverlay::Start() {
  impl_ = new Impl();
  if (!impl_->receiver) { status_ = "SpoutLibrary unavailable"; return false; }
  impl_->receiver->SetReceiverName(sender_name_.c_str());
  return true;
}
bool SpoutOverlay::Tick() {
  if (impl_->receiver->IsUpdated()) ResizeForSender();
  if (!impl_->receiver->ReceiveImage(impl_->pixels.data(), GL_BGRA, true)) { status_ = "Waiting for " + sender_name_; return false; }
  status_ = "Connected: " + std::string(impl_->receiver->GetSenderName());
  return true;
}
bool SpoutOverlay::IsConnected() const { return impl_ && impl_->receiver->IsConnected(); }
const std::string& SpoutOverlay::Status() const { return status_; }
void SpoutOverlay::Stop() { delete impl_; impl_ = nullptr; }
```

In `Render`, premultiply each received BGRA pixel then call `UpdateLayeredWindow` with `AC_SRC_ALPHA`. Reallocate the buffer and DIB section when `IsUpdated()` changes sender dimensions. Do not color-key or modify the VTube Studio window:

```cpp
BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
UpdateLayeredWindow(window, screen_dc, &position, &size, memory_dc,
                    &source, 0, &blend, ULW_ALPHA);
```

- [ ] **Step 3: Compile the receiver**

Append `target_sources(RuDesktopOverlay PRIVATE src/spout_overlay.cpp)` and `target_include_directories(RuDesktopOverlay PRIVATE src)` to CMake, then run `./scripts/build_ru_desktop_overlay.ps1 -BootstrapVcpkg`.

Expected: vcpkg resolves Spout2 and Release build emits `RuDesktopOverlay.exe`. If the port exports a different CMake target name, inspect the installed `Spout2Config.cmake` and change only that target spelling.

- [ ] **Step 4: Commit**

```powershell
git add tools/ru_desktop_overlay/src/spout_overlay.* tools/ru_desktop_overlay/CMakeLists.txt
git commit -m "feat: receive VTube Studio Spout output"
```

### Task 4: Add the transparent, safe desktop window

**Files:**
- Create: `tools/ru_desktop_overlay/src/main.cpp`
- Modify: `tools/ru_desktop_overlay/src/spout_overlay.h`
- Modify: `tools/ru_desktop_overlay/src/spout_overlay.cpp`

- [ ] **Step 1: Create the window without click-through by default**

In `main.cpp`, create a `WS_POPUP` window using `OverlayExStyle(options.click_through)` and place it with:

```cpp
SetWindowPos(hwnd, HWND_TOPMOST, 200, 200, 640, 360, SWP_NOACTIVATE | SWP_SHOWWINDOW);
```

Use the `WM_NCHITTEST` handler below so mouse input is only passed through after explicit opt-in:

```cpp
case WM_NCHITTEST:
  if (state.click_through) return HTTRANSPARENT;
  return DefWindowProc(hwnd, message, wparam, lparam);
```

- [ ] **Step 2: Add recovery controls and the render timer**

Support `C` to toggle click-through, `T` to toggle topmost, and `Esc` to close while the window owns focus. At 60 Hz, preserve receiver availability as a recoverable state:

```cpp
case WM_TIMER:
  if (state.receiver.Tick()) state.receiver.Render(hwnd);
  else state.receiver.RenderStatus(hwnd);
  return 0;
case WM_DESTROY:
  state.receiver.Stop();
  PostQuitMessage(0);
  return 0;
```

Show status and controls only when disconnected or when the pointer is over the overlay; alpha content otherwise remains unobstructed.

- [ ] **Step 3: Build and recovery-test without VTube Studio**

Run:

```powershell
.\scripts\build_ru_desktop_overlay.ps1
& .\tools\ru_desktop_overlay\out\build\Release\RuDesktopOverlay.exe
```

Expected: movable topmost window says `Waiting for VTubeStudioSpout`, closes with `Esc`, and never changes the VTube Studio process.

- [ ] **Step 4: Commit**

```powershell
git add tools/ru_desktop_overlay/src/main.cpp tools/ru_desktop_overlay/src/spout_overlay.*
git commit -m "feat: add transparent Ru desktop overlay"
```

### Task 5: Document, launch, and live-verify

**Files:**
- Create: `scripts/start_ru_desktop_overlay.ps1`
- Modify: `README.md`

- [ ] **Step 1: Add a safe launch wrapper**

Create `scripts/start_ru_desktop_overlay.ps1`:

```powershell
[CmdletBinding()]
param([switch]$ClickThrough, [string]$Sender = 'VTubeStudioSpout')
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\tools\ru_desktop_overlay\out\build\Release\RuDesktopOverlay.exe'
if (-not (Test-Path $exe)) { throw 'RuDesktopOverlay.exe is missing. Run scripts\build_ru_desktop_overlay.ps1 first.' }
$arguments = @('--sender', $Sender)
if ($ClickThrough) { $arguments += '--click-through' }
Start-Process -FilePath $exe -ArgumentList $arguments
```

- [ ] **Step 2: Document verified commands only**

Add `## Desktop overlay (Spout2)` to the README VTube Studio section: enable VTS Spout2 plus transparency in capture, build once, launch without `-ClickThrough`, visually approve alpha over a normal desktop app, then relaunch with `-ClickThrough`. State clearly that closing the overlay does not close Ru or VTube Studio.

- [ ] **Step 3: Live-test the transparent output**

With VTube Studio's Spout2 sender enabled, run `./scripts/start_ru_desktop_overlay.ps1`.

Expected: `Connected: VTubeStudioSpout`; no green background above a normal desktop window; a Ru spoken reply visibly lip-syncs, and a hotkey plus `LLM_Sway` remain visible.

- [ ] **Step 4: Test click-through and recovery after visual approval**

Run `./scripts/start_ru_desktop_overlay.ps1 -ClickThrough`.

Expected: model stays visible and pointer input reaches the app behind it. Close the overlay and confirm VTube Studio and Ru continue running.

- [ ] **Step 5: Final checks and commit**

Run:

```powershell
ctest --test-dir .\tools\ru_desktop_overlay\out\build -C Release --output-on-failure
git diff --check
git status --short
```

Expected: all overlay tests pass and only intended files are staged.

```powershell
git add scripts/start_ru_desktop_overlay.ps1 README.md
git commit -m "docs: add Ru desktop overlay setup"
```
