#pragma once
#include <windows.h>
#include <stdint.h>
// Center native800 geometry and trim initial phase2 actor positions only.
// Shared entry/standing targets, map speed and game ticks stay intact.
bool ValidateBattleLayoutFix() noexcept;
bool ApplyBattleLayoutFix() noexcept;
bool RollbackBattleLayoutFix() noexcept;
bool BattleLayoutFixHealthy() noexcept;
LONG BattleLayoutFixCalls() noexcept;
void CenterBattleLayout(int* coordinates,unsigned rows,int width,int height) noexcept;
bool TrimBattleInitialPosition(float* x,float* y,int targetX,int targetY,int width,int height) noexcept;
#ifdef BATTLE_LAYOUT_TESTING
void SetBattleLayoutTestBase(void* base) noexcept;
void SetBattleLayoutTestFailure(int afterWrite) noexcept;
void SetBattleLayoutTestTail(void (__cdecl* tail)()) noexcept;
void InvokeBattleLayoutTestTail() noexcept;
void SetBattleEntryTestUpdate(int (__cdecl* update)()) noexcept;
int InvokeBattleEntryTestUpdate() noexcept;
#endif
