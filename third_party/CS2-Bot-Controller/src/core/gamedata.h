#pragma once
#include "core/memory_module.h"
#include <nlohmann/json.hpp>
namespace cs2bc::gamedata {
// Loads the existing native configuration and resolves non-Schema offsets.
bool Load(nlohmann::json& gd, modules::ModuleInfo& serverModule, char* error, size_t maxlen);
} // namespace cs2bc::gamedata
