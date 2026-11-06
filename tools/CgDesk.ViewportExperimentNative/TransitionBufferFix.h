#pragma once
#include <windows.h>
#include <stdint.h>
bool ValidateTransitionBufferFix() noexcept;
bool ApplyTransitionBufferFix() noexcept;
// Initialization failure rollback only, before the first renderer is exposed.
bool RollbackTransitionBufferFix() noexcept;
bool TransitionBufferFixHealthy() noexcept;
bool TransitionBufferFixApplied() noexcept;
#ifdef TRANSITION_BUFFER_TESTING
void SetTransitionTestBase(void* base) noexcept;
void SetTransitionTestFailure(int afterWrite) noexcept;
float* GetTransitionTestBuffer(unsigned array) noexcept;
#endif
