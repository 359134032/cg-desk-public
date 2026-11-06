#pragma once
#include <stdint.h>
// Explicitly enabled local diagnostics; no process access or game-state writes.
void StartGraphicsDiagnostics(const wchar_t* path) noexcept;
void ObserveGraphicsDraw(int32_t result) noexcept;
bool GraphicsDiagnosticsHealthy() noexcept;
#ifdef GRAPHICS_DIAGNOSTICS_TESTING
void* GraphicsLockObserverAddress() noexcept;
#endif
