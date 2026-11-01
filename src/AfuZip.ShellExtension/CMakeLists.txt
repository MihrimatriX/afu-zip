# AFU Zip Explorer eklentisi (IExplorerCommand). MSVC ile x64 ve ARM64 için derlenir:
#   cmake -S . -B build -A x64 -DAFUZIP_PUBLISHER="CN=..."   &&   cmake --build build --config Release
# Explorer bu DLL'i kendi sürecine yükler; VC++ çalışma zamanı kurulu olmayabilir, bu yüzden CRT statik bağlanır.
cmake_minimum_required(VERSION 3.21)
project(AfuZipShellExtension LANGUAGES CXX)

# Sparse paketin kimliği: DLL'in gömülü bildirimindeki msix öğesi paketle birebir aynı olmalı.
set(AFUZIP_PUBLISHER "CN=AFU Zip" CACHE STRING "Sparse paketi imzalayan sertifikanın konusu (Publisher)")
set(AFUZIP_PACKAGE_NAME "AfuZip.ContextMenu" CACHE STRING "Sparse paket adı")
configure_file(identity.manifest.in ${CMAKE_CURRENT_BINARY_DIR}/identity.manifest @ONLY)

add_library(AfuZip.ShellExtension SHARED
    ShellExtension.cpp
    Naming.h
    exports.def
    ${CMAKE_CURRENT_BINARY_DIR}/identity.manifest)

set_target_properties(AfuZip.ShellExtension PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(AfuZip.ShellExtension PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(AfuZip.ShellExtension PRIVATE /utf-8 /W4 /permissive- /guard:cf)
target_link_options(AfuZip.ShellExtension PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
target_link_libraries(AfuZip.ShellExtension PRIVATE ole32 shlwapi shell32 uuid)
