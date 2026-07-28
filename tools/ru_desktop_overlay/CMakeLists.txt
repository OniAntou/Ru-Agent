cmake_minimum_required(VERSION 3.25)
project(RuDesktopOverlay LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Spout2 CONFIG REQUIRED)

add_executable(RuDesktopOverlay WIN32 src/main.cpp)
target_link_libraries(RuDesktopOverlay PRIVATE
  Spout2::SpoutLibrary
  d3d11
  dxgi
  d3dcompiler
  user32
  gdi32)
