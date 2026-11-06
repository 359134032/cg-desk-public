#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <limits.h>
#include <string.h>
#include "MapCacheFix.h"
#include "MapCacheProfile.h"

static_assert(sizeof(void*) == 4, "Map cache profile is x86-only.");
namespace {
constexpr uintptr_t PreferredBase = 0x400000;
constexpr SIZE_T LegacyBytes = 51 * 51 * 2, ExpandedBytes = 64 * 64 * 2;
constexpr uintptr_t BoundsAddresses[] = {0xaa87ac,0xaa87b0,0xaa87b4,0xaa87b8,0xaa87bc,0xaa87c0};
struct Bounds { short x0, y0, x1, y1, width, height; };
static_assert(sizeof(Bounds) == 12, "Six packed legacy short fields.");
struct Page { uintptr_t address; DWORD original; bool changed; };
struct Patch { uintptr_t address; DWORD original, replacement; };
MapCacheDiagnostics diagnostics{0,0,0,1,0,0,0,0,0};
uintptr_t imageBase = PreferredBase;
void* layers[4]{};
void* scratch = nullptr;
Bounds legacy{}, expanded{};
bool boundsKnown = false;
volatile LONG active = 0, scanDepth = 0;
Patch patches[ARRAYSIZE(MapCachePatchSpecs)]{};
Page pages[ARRAYSIZE(MapCachePatchSpecs)]{};
unsigned pageCount = 0;
SIZE_T pageSize = 0;
using BoundsFunction = void (__cdecl*)();
using ScanFunction = int (__cdecl*)(int);
using ClearFunction = void* (__cdecl*)(void*, int, size_t);
BoundsFunction originalBounds = reinterpret_cast<BoundsFunction>(0x4a0540);
ScanFunction originalScan = reinterpret_cast<ScanFunction>(0x49ab80);
ClearFunction originalClear = reinterpret_cast<ClearFunction>(0x57e7c0);
#ifdef MAP_CACHE_TESTING
int failAfterWrite = -1;
#endif

uintptr_t Address(uintptr_t value) noexcept { return imageBase + value - PreferredBase; }
bool Readable(uintptr_t address, SIZE_T length, bool requireImage = true) noexcept {
    if (!length || address > UINTPTR_MAX - length) return false;
    const uintptr_t end = address + length;
    while (address < end) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<void*>(address), &info, sizeof(info)) != sizeof(info) ||
            info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            (requireImage && (info.Type != MEM_IMAGE || reinterpret_cast<uintptr_t>(info.AllocationBase) != imageBase))) return false;
        const uintptr_t next = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        if (next <= address) return false;
        address = next;
    }
    return true;
}
Bounds ReadBounds() noexcept {
    Bounds value{};
    short* values = &value.x0;
    for (unsigned i = 0; i < ARRAYSIZE(BoundsAddresses); ++i)
        values[i] = *reinterpret_cast<volatile short*>(Address(BoundsAddresses[i]));
    return value;
}
void WriteBounds(const Bounds& value) noexcept {
    const short* values = &value.x0;
    for (unsigned i = 0; i < ARRAYSIZE(BoundsAddresses); ++i)
        *reinterpret_cast<volatile short*>(Address(BoundsAddresses[i])) = values[i];
}
bool ValidBounds(const Bounds& value, int maximum) noexcept {
    return value.x0 >= 0 && value.y0 >= 0 && value.x1 >= value.x0 && value.y1 >= value.y0 &&
        value.width == value.x1 - value.x0 && value.height == value.y1 - value.y0 &&
        value.width <= maximum && value.height <= maximum;
}
bool Owner() noexcept {
    if (GetCurrentThreadId() == diagnostics.OwnerThread && !scanDepth) return true;
    InterlockedIncrement(&diagnostics.ThreadOrReentryRejected);
    diagnostics.LastError = ERROR_INVALID_THREAD_ID;
    return false;
}

// All ten direct calls are redirected here. The original makes exactly its
// original four protected-coordinate reads, then we extend its clipped bounds.
void __cdecl UpdateBoundsHook() noexcept {
    if (!Owner()) return;
    InterlockedExchange(&diagnostics.RuntimeUsed, 1);
    InterlockedIncrement(&diagnostics.BoundsCalls);
    originalBounds();
    const Bounds old = ReadBounds();
    boundsKnown = false;
    if (!ValidBounds(old, 51)) { diagnostics.LastError = ERROR_INVALID_DATA; return; }
    const int mapWidth = *reinterpret_cast<volatile int*>(Address(0xaa87a4));
    const int mapHeight = *reinterpret_cast<volatile int*>(Address(0xaa87a8));
    if (mapWidth < old.x1 || mapHeight < old.y1 || mapWidth > SHRT_MAX || mapHeight > SHRT_MAX) {
        diagnostics.LastError = ERROR_INVALID_DATA;
        return;
    }
    Bounds next = old;
    next.x0 = static_cast<short>(old.x0 < 6 ? 0 : old.x0 - 6);
    next.y0 = static_cast<short>(old.y0 < 8 ? 0 : old.y0 - 8);
    next.x1 = static_cast<short>(mapWidth < old.x1 + 7 ? mapWidth : old.x1 + 7);
    next.y1 = static_cast<short>(mapHeight < old.y1 + 5 ? mapHeight : old.y1 + 5);
    next.width = next.x1 - next.x0;
    next.height = next.y1 - next.y0;
    if (!ValidBounds(next, 64)) { diagnostics.LastError = ERROR_INVALID_DATA; return; }
    legacy = old;
    expanded = next;
    WriteBounds(expanded);
    boundsKnown = true;
}

// The three call sites retain the original scan, getter count, two-rectangle
// limit and request geometry. Its four literal reads use only legacy scratch.
// The analyzed scan/callees contain no normal message pump or game callback.
int __cdecl LegacyScanHook(int direction) noexcept {
    if (!Owner() || !boundsKnown) return 0;
    const Bounds before = ReadBounds();
    if (memcmp(&before, &expanded, sizeof(before)) || !ValidBounds(legacy, 51) || !ValidBounds(expanded, 64)) {
        diagnostics.LastError = ERROR_INVALID_DATA;
        return 0;
    }
    const int x = legacy.x0 - expanded.x0, y = legacy.y0 - expanded.y0;
    if (x < 0 || y < 0 || x + legacy.width > expanded.width || y + legacy.height > expanded.height) {
        diagnostics.LastError = ERROR_INVALID_DATA;
        return 0;
    }
    InterlockedExchange(&diagnostics.RuntimeUsed, 1);
    InterlockedIncrement(&diagnostics.LegacyScanCalls);
    // A clipped original cache exits before any of its four reads. Copying is
    // still well-defined with the original dynamic row stride.
    memset(scratch, 0, LegacyBytes);
    for (int row = 0; row < legacy.height; ++row) {
        const auto* source = static_cast<const BYTE*>(layers[2]) + ((row + y) * expanded.width + x) * 2;
        auto* destination = static_cast<BYTE*>(scratch) + row * legacy.width * 2;
        memcpy(destination, source, legacy.width * 2);
    }
    int result = 0;
    InterlockedExchange(&scanDepth, 1);
    __try {
        WriteBounds(legacy);
        result = originalScan(direction);
    } __finally {
        WriteBounds(before);
        InterlockedExchange(&scanDepth, 0);
    }
    return result;
}

// Pointer and original length discriminate our buffers. No return-address,
// shared argument scratch or thread-local rewrite state is used. In particular
// 49dac0's original stack buffers keep their original 5202-byte clear.
void* __cdecl CacheClearHook(void* destination, int fill, size_t count) noexcept {
    if (count == LegacyBytes) {
        for (void* layer : layers) {
            if (destination == layer) {
                count = ExpandedBytes;
                InterlockedExchange(&diagnostics.RuntimeUsed, 1);
                break;
            }
        }
    }
    return originalClear(destination, fill, count);
}

bool CheckOriginal(uintptr_t start, const BYTE* bytes, SIZE_T count) noexcept {
    return Readable(Address(start), count) && memcmp(reinterpret_cast<void*>(Address(start)), bytes, count) == 0;
}
void FreeBuffers() noexcept {
    for (void*& layer : layers) { if (layer) VirtualFree(layer, 0, MEM_RELEASE); layer = nullptr; }
    if (scratch) VirtualFree(scratch, 0, MEM_RELEASE);
    scratch = nullptr;
}
bool AllocateBuffers() noexcept {
    // Commit exactly two 4KiB pages per expanded cache, followed by a reserved
    // inaccessible page. The independent legacy scratch has the same guard.
    if (pageSize != 4096) { diagnostics.LastError = ERROR_NOT_SUPPORTED; return false; }
    for (unsigned i = 0; i < 5; ++i) {
        void* region = VirtualAlloc(nullptr, ExpandedBytes + pageSize, MEM_RESERVE, PAGE_NOACCESS);
        if (!region) { diagnostics.LastError = GetLastError(); FreeBuffers(); return false; }
        if (i < 4) layers[i] = region; else scratch = region;
        if (!VirtualAlloc(region, ExpandedBytes, MEM_COMMIT, PAGE_READWRITE)) {
            diagnostics.LastError = GetLastError(); FreeBuffers(); return false;
        }
    }
    return true;
}
bool PreparePatches() noexcept {
    pageCount = 0;
    for (unsigned i = 0; i < ARRAYSIZE(MapCachePatchSpecs); ++i) {
        const auto& spec = MapCachePatchSpecs[i];
        auto& patch = patches[i];
        patch.address = Address(spec.operand);
        patch.original = *reinterpret_cast<const DWORD*>(patch.address);
        uintptr_t replacement = 0;
        switch (spec.kind) {
        case 0: replacement = reinterpret_cast<uintptr_t>(layers[spec.layer]); break;
        case 1: replacement = reinterpret_cast<uintptr_t>(scratch) + spec.offset; break;
        case 2: replacement = reinterpret_cast<uintptr_t>(&CacheClearHook) - (patch.address + 4); break;
        case 3: replacement = reinterpret_cast<uintptr_t>(&UpdateBoundsHook) - (patch.address + 4); break;
        case 4: replacement = reinterpret_cast<uintptr_t>(&LegacyScanHook) - (patch.address + 4); break;
        default: return false;
        }
        patch.replacement = static_cast<DWORD>(replacement);
        // Every four-byte operand fits in one page in this exact profile.
        const uintptr_t page = patch.address & ~(static_cast<uintptr_t>(pageSize) - 1);
        if ((patch.address + 3) / pageSize != page / pageSize) return false;
        bool present = false;
        for (unsigned n = 0; n < pageCount; ++n) if (pages[n].address == page) present = true;
        if (!present) {
            MEMORY_BASIC_INFORMATION info{};
            if (VirtualQuery(reinterpret_cast<void*>(page), &info, sizeof(info)) != sizeof(info)) return false;
            pages[pageCount++] = {page, info.Protect, false};
        }
    }
    return true;
}
bool RestoreProtections() noexcept {
    bool restored = true;
    for (unsigned i = 0; i < pageCount; ++i) if (pages[i].changed) {
        DWORD ignored = 0;
        if (VirtualProtect(reinterpret_cast<void*>(pages[i].address), pageSize, pages[i].original, &ignored)) pages[i].changed = false;
        else { diagnostics.LastError = GetLastError(); restored = false; }
    }
    diagnostics.ProtectionRestored = restored ? 1 : 0;
    return restored;
}
bool MakeWritable() noexcept {
    for (unsigned i = 0; i < pageCount; ++i) {
        DWORD previous = 0;
        if (!VirtualProtect(reinterpret_cast<void*>(pages[i].address), pageSize, PAGE_EXECUTE_READWRITE, &previous)) {
            diagnostics.LastError = GetLastError();
            return false;
        }
        pages[i].changed = true;
    }
    return true;
}
bool RestoreOriginalCode() noexcept {
    if (!MakeWritable()) { RestoreProtections(); return false; }
    bool copied = false;
    __try {
        for (const auto& patch : patches) memcpy(reinterpret_cast<void*>(patch.address), &patch.original, 4);
        copied = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { diagnostics.LastError = ERROR_NOACCESS; }
    const bool flushed = FlushInstructionCache(GetCurrentProcess(), nullptr, 0) != FALSE;
    const bool protectedAgain = RestoreProtections();
    return copied && flushed && protectedAgain;
}
}

bool ValidateMapCacheFix() noexcept {
    if (diagnostics.Applied) {
        __try {
            for (const auto& patch : patches)
                if (!Readable(patch.address, 4) || *reinterpret_cast<const DWORD*>(patch.address) != patch.replacement) {
                    diagnostics.LastError = ERROR_REVISION_MISMATCH;
                    return false;
                }
        } __except (EXCEPTION_EXECUTE_HANDLER) { diagnostics.LastError = ERROR_NOACCESS; return false; }
        return true;
    }
    __try {
        for (const auto& spec : MapCachePatchSpecs) {
            if (!CheckOriginal(spec.guard, spec.bytes, sizeof(spec.bytes))) {
                diagnostics.LastError = ERROR_REVISION_MISMATCH;
                return false;
            }
        }
        if (!CheckOriginal(MapCacheLegacyScanAddress, MapCacheLegacyScanBytes, sizeof(MapCacheLegacyScanBytes)) ||
            !CheckOriginal(MapCacheLegacyBoundsAddress, MapCacheLegacyBoundsBytes, sizeof(MapCacheLegacyBoundsBytes)) ||
            !CheckOriginal(MapCacheGameClearAddress, MapCacheGameClearBytes, sizeof(MapCacheGameClearBytes))) {
            diagnostics.LastError = ERROR_REVISION_MISMATCH;
            return false;
        }
        for (const auto& dependency : MapCacheDependencies) {
            if (!CheckOriginal(dependency.address, dependency.bytes, dependency.count)) {
                diagnostics.LastError = ERROR_REVISION_MISMATCH;
                return false;
            }
        }
        for (uintptr_t address : BoundsAddresses) if (!Readable(Address(address), sizeof(short))) return false;
        if (!Readable(Address(0xaa87a4), 8)) return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) { diagnostics.LastError = ERROR_NOACCESS; return false; }
    return true;
}

bool ApplyMapCacheFix() noexcept {
    if (diagnostics.Applied) return ValidateMapCacheFix();
    if (InterlockedCompareExchange(&active, 1, 0)) { diagnostics.LastError = ERROR_BUSY; return false; }
    if (!ValidateMapCacheFix()) { InterlockedExchange(&active, 0); return false; }
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    pageSize = system.dwPageSize;
    diagnostics.OwnerThread = GetCurrentThreadId();
    diagnostics.RuntimeUsed = 0;
    if (!AllocateBuffers() || !PreparePatches()) { FreeBuffers(); InterlockedExchange(&active, 0); return false; }
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&ApplyMapCacheFix), &module)) {
        diagnostics.LastError = GetLastError(); FreeBuffers(); InterlockedExchange(&active, 0); return false;
    }
    bool wrote = false, succeeded = false;
    if (MakeWritable()) {
        __try {
#ifdef MAP_CACHE_TESTING
            unsigned count = 0;
#endif
            for (const auto& patch : patches) {
                memcpy(reinterpret_cast<void*>(patch.address), &patch.replacement, 4);
                wrote = true;
#ifdef MAP_CACHE_TESTING
                ++count;
                if (failAfterWrite >= 0 && count >= static_cast<unsigned>(failAfterWrite)) __leave;
#endif
            }
            succeeded = FlushInstructionCache(GetCurrentProcess(), nullptr, 0) != FALSE;
        } __except (EXCEPTION_EXECUTE_HANDLER) { diagnostics.LastError = ERROR_NOACCESS; }
    }
    if (!RestoreProtections()) succeeded = false;
    if (!succeeded) {
        const bool rolledBack = !wrote || RestoreOriginalCode();
        // If an unexpected cache consumer ran during initialization, keep the
        // allocations resident even after restoring code. The caller must stop
        // this launch when RollbackMapCacheFix reports that fatal state.
        if (rolledBack && diagnostics.ProtectionRestored && !diagnostics.RuntimeUsed) { FreeBuffers(); InterlockedExchange(&active, 0); }
        else { diagnostics.LastError = ERROR_CAN_NOT_COMPLETE; InterlockedExchange(&active, 3); }
        return false;
    }
    boundsKnown = false;
    diagnostics.PatchCount = ARRAYSIZE(MapCachePatchSpecs);
    diagnostics.LastError = ERROR_SUCCESS;
    InterlockedExchange(&diagnostics.Applied, 1);
    InterlockedExchange(&active, 2);
    return true;
}

bool RollbackMapCacheFix() noexcept {
    if (!diagnostics.Applied) return active != 3;
    if (diagnostics.RuntimeUsed || GetCurrentThreadId() != diagnostics.OwnerThread) { diagnostics.LastError = ERROR_BUSY; return false; }
    if (!ValidateMapCacheFix()) return false;
    if (!RestoreOriginalCode()) { InterlockedExchange(&active, 3); return false; }
    InterlockedExchange(&diagnostics.Applied, 0);
    diagnostics.PatchCount = 0;
    diagnostics.LastError = ERROR_SUCCESS;
    boundsKnown = false;
    FreeBuffers();
    InterlockedExchange(&active, 0);
    return true;
}
MapCacheDiagnostics GetMapCacheDiagnostics() noexcept { return diagnostics; }

#ifdef MAP_CACHE_TESTING
void SetMapCacheTestBase(void* base) noexcept { imageBase = reinterpret_cast<uintptr_t>(base); }
void SetMapCacheTestCallbacks(BoundsFunction bounds, ScanFunction scan, ClearFunction clear) noexcept {
    originalBounds = bounds; originalScan = scan; originalClear = clear;
}
void SetMapCacheTestFailureAfterWrite(int count) noexcept { failAfterWrite = count; }
void ResetMapCacheTestRuntimeUse() noexcept { diagnostics.RuntimeUsed = 0; }
void InvokeMapCacheTestBounds() noexcept { UpdateBoundsHook(); }
int InvokeMapCacheTestScan(int direction) noexcept { return LegacyScanHook(direction); }
void* InvokeMapCacheTestClear(void* destination, int fill, size_t count) noexcept { return CacheClearHook(destination, fill, count); }
void* GetMapCacheTestBuffer(unsigned layer) noexcept { return layer < 4 ? layers[layer] : nullptr; }
void* GetMapCacheTestScratch() noexcept { return scratch; }
#endif
