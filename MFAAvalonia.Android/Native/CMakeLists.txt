cmake_minimum_required(VERSION 3.22.1)
project(mfabridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(mfabridge SHARED mfabridge.cpp frame_store.cpp capture_preview.cpp)
target_compile_options(mfabridge PRIVATE -O3 -fvisibility=hidden -ffunction-sections -fdata-sections)
target_link_options(mfabridge PRIVATE -Wl,--gc-sections -Wl,-z,max-page-size=16384)
target_link_libraries(mfabridge log android mediandk EGL GLESv2 dl)
