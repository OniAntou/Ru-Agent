cmake_minimum_required(VERSION 3.25)
project(RuDesktopOverlay LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Spout2 CONFIG REQUIRED)

if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/src/main.cpp")
  add_executable(RuDesktopOverlay WIN32
    src/main.cpp
    src/frame_pixels.cpp
    src/overlay_options.cpp
    src/spout_overlay.cpp
    src/window_styles.cpp)
  target_include_directories(RuDesktopOverlay PRIVATE src)
  target_link_libraries(RuDesktopOverlay PRIVATE
    Spout2::SpoutDX
    d3d11
    dxgi
    d3dcompiler
    user32
    gdi32
    shell32)
endif()

option(RU_BUILD_NATIVE_PROBES "Build native Spout probe executables" OFF)

include(CTest)
if (BUILD_TESTING)
  add_executable(overlay_options_tests
    tests/overlay_options_tests.cpp
    src/overlay_options.cpp
    src/frame_pixels.cpp
    src/window_styles.cpp)
  target_include_directories(overlay_options_tests PRIVATE src tests)
  target_link_libraries(overlay_options_tests PRIVATE user32)
  add_test(NAME overlay_options_tests COMMAND overlay_options_tests)
  add_test(NAME overlay_check_failure COMMAND overlay_options_tests --self-test-failure)
  set_tests_properties(overlay_check_failure PROPERTIES WILL_FAIL TRUE)

  if (RU_BUILD_NATIVE_PROBES)
    add_executable(spout_connection_probe
      tests/spout_connection_probe.cpp
      src/spout_overlay.cpp
      src/overlay_options.cpp
      src/frame_pixels.cpp
      src/window_styles.cpp)
    target_include_directories(spout_connection_probe PRIVATE src tests)
    target_link_libraries(spout_connection_probe PRIVATE Spout2::SpoutDX user32)
    add_test(NAME spout_connection_probe COMMAND spout_connection_probe)

    add_executable(spout_sender_probe
      tests/spout_sender_probe.cpp
      src/frame_pixels.cpp)
    target_include_directories(spout_sender_probe PRIVATE src)
    target_link_libraries(spout_sender_probe PRIVATE Spout2::SpoutLibrary)

    add_executable(spout_dx_sender_probe tests/spout_dx_sender_probe.cpp)
    target_link_libraries(spout_dx_sender_probe PRIVATE Spout2::SpoutDX)
  endif()
endif()
