cmake_minimum_required(VERSION 3.28)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
  project(simplevoicechat_obs VERSION 1.0.0 LANGUAGES CXX)
endif()

if(NOT TARGET OBS::libobs)
  find_package(libobs CONFIG REQUIRED)
endif()

if(NOT TARGET OBS::frontend-api)
  find_package(obs-frontend-api CONFIG REQUIRED)
endif()

add_library(simplevoicechat_obs MODULE simplevoicechat_obs.cpp)
target_compile_features(simplevoicechat_obs PRIVATE cxx_std_20)
target_link_libraries(simplevoicechat_obs PRIVATE OBS::libobs OBS::frontend-api)
target_compile_definitions(simplevoicechat_obs PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(simplevoicechat_obs PROPERTIES PREFIX "")

if(COMMAND set_target_properties_obs)
  set_target_properties_obs(simplevoicechat_obs PROPERTIES FOLDER plugins PREFIX "")
elseif(WIN32)
  install(TARGETS simplevoicechat_obs RUNTIME DESTINATION obs-plugins/64bit LIBRARY DESTINATION obs-plugins/64bit)
endif()
