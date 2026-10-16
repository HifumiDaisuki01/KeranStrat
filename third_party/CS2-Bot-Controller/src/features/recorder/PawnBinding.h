// Internal movement-services ownership helpers; public registration API stays in InputInjector.h.
#pragma once

namespace cs2bc::input_injector::pawn_binding {
// Resolves the owner and optionally returns the validated field pawn for this invocation.
int ServicesToSlot(void* services, void** validatedPawn = nullptr);
// Reuses the invocation-local pawn while preserving registered-pawn precedence.
void* ServicesToWeaponServices(int slot, void* services, void* validatedPawn);
// Clears all registered pawns during hook teardown.
void ClearAll();
} // namespace cs2bc::input_injector::pawn_binding
