# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

cmake_minimum_required(VERSION 3.25)
project(rtaime_media_io LANGUAGES CXX)

set(AJA_NTV2_ROOT "$ENV{RTAIME_AJA_NTV2_ROOT}" CACHE PATH "Path to aja-video/libajantv2 release checkout")
set(RTAIME_AJA_SDK_REVISION "$ENV{RTAIME_AJA_SDK_REVISION}" CACHE STRING "Qualified libajantv2 tag or commit")
if(NOT AJA_NTV2_ROOT OR NOT EXISTS "${AJA_NTV2_ROOT}/CMakeLists.txt")
	message(FATAL_ERROR "AJA_NTV2_ROOT must point to an aja-video/libajantv2 release checkout.")
endif()
if(NOT RTAIME_AJA_SDK_REVISION)
	set(RTAIME_AJA_SDK_REVISION "release-unverified")
endif()

set(AJANTV2_BUILD_OPENSOURCE ON CACHE BOOL "" FORCE)
set(AJANTV2_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_DEMOS ON CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_DRIVER ON CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_TESTS ON CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_TOOLS ON CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_PLUGIN_LOAD ON CACHE BOOL "" FORCE)
set(AJANTV2_DISABLE_RDMA ON CACHE BOOL "" FORCE)
set(AJA_INSTALL_HEADERS OFF CACHE BOOL "" FORCE)
set(AJA_INSTALL_SOURCES OFF CACHE BOOL "" FORCE)
set(AJA_INSTALL_CMAKE OFF CACHE BOOL "" FORCE)
set(AJA_INSTALL_MISC OFF CACHE BOOL "" FORCE)
add_subdirectory("${AJA_NTV2_ROOT}" "${CMAKE_BINARY_DIR}/libajantv2" EXCLUDE_FROM_ALL)

add_library(rtaime_media_io SHARED
	rtaime_media_io_aja.cpp
	../MediaIo/rtaime_media_io_abi.h)

target_compile_features(rtaime_media_io PRIVATE cxx_std_20)
target_compile_definitions(rtaime_media_io PRIVATE RTAIME_AJA_SDK_REVISION="${RTAIME_AJA_SDK_REVISION}")
target_include_directories(rtaime_media_io PRIVATE
	${CMAKE_CURRENT_SOURCE_DIR}/../MediaIo
	${AJA_NTV2_ROOT}
	${AJA_NTV2_ROOT}/ajantv2/includes)
target_link_libraries(rtaime_media_io PRIVATE ajantv2)

if(MSVC)
	target_compile_options(rtaime_media_io PRIVATE /W4 /permissive-)
	set_target_properties(rtaime_media_io PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
else()
	target_compile_options(rtaime_media_io PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(rtaime_media_io PROPERTIES
	OUTPUT_NAME "rtaime_media_io"
	POSITION_INDEPENDENT_CODE ON)
