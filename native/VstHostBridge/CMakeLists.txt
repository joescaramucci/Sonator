cmake_minimum_required(VERSION 3.20)
project(VstHostBridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# --------------------------------------------------------------------------
# Uses only the MIT-licensed "pluginterfaces" module of the Steinberg VST3
# SDK (vendored under thirdparty/pluginterfaces, see its LICENSE.txt) for
# the abstract IPluginFactory/IComponent/IEditController declarations. No
# GPL/commercial-licensed "public.sdk" helper code is required or used.
# --------------------------------------------------------------------------

add_library(VstHostBridge SHARED
    src/VstHostBridge.cpp
    include/VstHostBridge.h
)

target_include_directories(VstHostBridge PRIVATE include thirdparty)

# Optional ASIO backend. Point this at the Steinberg ASIO SDK folder (the one that
# contains common/asio.h). The SDK is not part of this repository. Leave it empty
# to build without ASIO support.
set(SONATOR_ASIO_SDK_DIR "" CACHE PATH "Steinberg ASIO SDK folder containing common/asio.h (optional)")
if (SONATOR_ASIO_SDK_DIR AND EXISTS "${SONATOR_ASIO_SDK_DIR}/common/asio.h")
    target_include_directories(VstHostBridge PRIVATE "${SONATOR_ASIO_SDK_DIR}/common")
    target_compile_definitions(VstHostBridge PRIVATE SONATOR_WITH_ASIO=1)
    message(STATUS "VstHostBridge: ASIO backend enabled (${SONATOR_ASIO_SDK_DIR})")
else()
    target_compile_definitions(VstHostBridge PRIVATE SONATOR_WITH_ASIO=0)
    message(STATUS "VstHostBridge: ASIO backend disabled (set SONATOR_ASIO_SDK_DIR to enable)")
endif()

# The .NET side P/Invokes "VstHostBridge.dll" (no "lib" prefix). MSVC already
# omits the prefix by default, but MinGW/GCC add "lib" unless told not to.
set_target_properties(VstHostBridge PROPERTIES PREFIX "")

if (MSVC)
    # /utf-8: source and runtime strings are UTF-8, matching the UTF-8 error text the managed side reads.
    target_compile_options(VstHostBridge PRIVATE /W4 /utf-8)
elseif (MINGW)
    target_link_options(VstHostBridge PRIVATE -static -static-libgcc -static-libstdc++)
endif()

# WASAPI (real-time audio output) + threading support.
target_link_libraries(VstHostBridge PRIVATE ole32 uuid winmm)

# The Sonator .NET app P/Invokes "VstHostBridge.dll" and expects to find it
# next to Sonator.exe (bin/Debug/net8.0-windows or bin/Release/net8.0-windows).
# Until a build step copies it automatically, copy it there manually, e.g.:
#   copy build\Debug\VstHostBridge.dll ..\..\bin\Debug\net8.0-windows\
