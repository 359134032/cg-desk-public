#pragma once
#include <windows.h>
bool ValidateTransitionSpeedFix() noexcept;
bool ApplyTransitionSpeedFix() noexcept;
bool RollbackTransitionSpeedFix() noexcept;
bool TransitionSpeedFixHealthy() noexcept;
LONG TransitionSpeedFixCalls() noexcept;
bool NormalizeTransitionStep(int mode,int rawStep,float acceleration,int width,int height,int* step,float* scaledAcceleration) noexcept;
#ifdef TRANSITION_SPEED_TESTING
void SetTransitionSpeedTestBase(void* base) noexcept;
void SetTransitionSpeedTestFailure(int afterWrite) noexcept;
void SetTransitionSpeedTestProtectionFailure(bool value) noexcept;
void SetTransitionSpeedTestCallbacks(int (__cdecl* exit)(int,int,float),int (__cdecl* enter)(int,int,float)) noexcept;
int InvokeTransitionSpeedTest(bool entering,int mode,int step,float acceleration) noexcept;
#endif
