#pragma once
#include <ISmmAPI.h>
#include <cstddef>

class ISchemaSystem;
extern ISchemaSystem* g_schemaSystem;

namespace cs2bc::interfaces {
// Resolves required interfaces and configures optional voice support.
bool Init(SourceMM::ISmmAPI* ismm, char* error, size_t maxlen);
// Clears consumers of the borrowed engine interfaces.
void Reset();
} // namespace cs2bc::interfaces
