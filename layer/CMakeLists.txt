cmake_minimum_required(VERSION 3.20)
project(OrderUpVR_Layer LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Vulkan REQUIRED) # headers only; the layer never links the loader

add_library(VkLayer_OrderUpVR SHARED src/layer.cpp)
target_include_directories(VkLayer_OrderUpVR PRIVATE ${Vulkan_INCLUDE_DIRS})
target_compile_definitions(VkLayer_OrderUpVR PRIVATE VK_USE_PLATFORM_WIN32_KHR NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
if(MSVC)
    target_compile_options(VkLayer_OrderUpVR PRIVATE /W4 /permissive-)
endif()

# Put the DLL and its manifest side by side so VK_ADD_LAYER_PATH can point at one folder.
set(OUT_DIR ${CMAKE_SOURCE_DIR}/../bin)
set_target_properties(VkLayer_OrderUpVR PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${OUT_DIR}
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${OUT_DIR}
    RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO ${OUT_DIR})
add_custom_command(TARGET VkLayer_OrderUpVR POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_SOURCE_DIR}/VkLayer_OrderUpVR.json ${OUT_DIR}/VkLayer_OrderUpVR.json)
