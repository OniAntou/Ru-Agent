cmake_minimum_required(VERSION 3.25)
project(RuDesktopOverlay LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Spout2 CONFIG REQUIRED)

if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/src/main.cpp")
  add_executable(RuDesktopOverlay WIN32 src/main.cpp)
  target_link_libraries(RuDesktopOverlay PRIVATE
    Spout2::SpoutLibrary
    d3d11
    dxgi
    d3dcompiler
    user32
    gdi32)
endif()

include(CTest)
if (BUILD_TESTING)
  add_executable(overlay_options_tests
    tests/overlay_options_tests.cpp
    src/overlay_options.cpp
    src/window_styles.cpp)
  target_include_directories(overlay_options_tests PRIVATE src)
  target_link_libraries(overlay_options_tests PRIVATE user32)
  add_test(NAME overlay_options_tests COMMAND overlay_options_tests)
endif()
