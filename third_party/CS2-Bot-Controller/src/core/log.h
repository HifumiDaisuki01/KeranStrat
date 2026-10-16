#pragma once

#include <cstddef>

namespace cs2bc::log {
enum class Level
{
    Debug,
    Info,
    Warn,
    Error
};

// Opens the console and file sinks before plugin initialization.
bool Init(const char* baseDir, char* error, size_t maxlen);
// Flushes and releases the plugin logger after hook cleanup.
void Close();
// Formats existing printf-style diagnostics for the shared logger.
void Write(Level level, const char* format, ...);
} // namespace cs2bc::log

#define BC_LOG_DEBUG(...) ::cs2bc::log::Write(::cs2bc::log::Level::Debug, __VA_ARGS__)
#define BC_LOG_INFO(...)  ::cs2bc::log::Write(::cs2bc::log::Level::Info, __VA_ARGS__)
#define BC_LOG_WARN(...)  ::cs2bc::log::Write(::cs2bc::log::Level::Warn, __VA_ARGS__)
#define BC_LOG_ERROR(...) ::cs2bc::log::Write(::cs2bc::log::Level::Error, __VA_ARGS__)
