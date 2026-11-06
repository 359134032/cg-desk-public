#pragma once
#include <windows.h>
#include <stdint.h>

// This module is initialization-only. Rollback is refused once any cache hook
// has been used, because old and expanded map layouts must not be mixed.
struct MapCacheDiagnostics {
    LONG Applied;
    DWORD LastError;
    LONG PatchCount;
    LONG ProtectionRestored;
    LONG RuntimeUsed;
    LONG BoundsCalls;
    LONG LegacyScanCalls;
    LONG ThreadOrReentryRejected;
    DWORD OwnerThread;
};

bool ValidateMapCacheFix() noexcept;
bool ApplyMapCacheFix() noexcept;
bool RollbackMapCacheFix() noexcept;
MapCacheDiagnostics GetMapCacheDiagnostics() noexcept;

#ifdef MAP_CACHE_TESTING
void SetMapCacheTestBase(void* base) noexcept;
void SetMapCacheTestCallbacks(void (__cdecl* bounds)(), int (__cdecl* scan)(int),
    void* (__cdecl* clear)(void*, int, size_t)) noexcept;
void SetMapCacheTestFailureAfterWrite(int count) noexcept;
void ResetMapCacheTestRuntimeUse() noexcept;
void InvokeMapCacheTestBounds() noexcept;
int InvokeMapCacheTestScan(int direction) noexcept;
void* InvokeMapCacheTestClear(void* destination, int fill, size_t count) noexcept;
void* GetMapCacheTestBuffer(unsigned layer) noexcept;
void* GetMapCacheTestScratch() noexcept;
#endif
