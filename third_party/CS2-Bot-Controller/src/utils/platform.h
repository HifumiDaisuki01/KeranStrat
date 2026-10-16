// Cross-platform self-module path

#pragma once

#include <string>

namespace cs2bc {
// Absolute path of this shared library on disk; empty on failure
std::string SelfModulePath();
} // namespace cs2bc
