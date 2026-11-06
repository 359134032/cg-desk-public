#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <initializer_list>
#include "MapCacheFix.h"
#include "MapCacheProfile.h"

// This host maps a code fixture as SEC_IMAGE. Every invoked callback below is
// this test's own code. It never calls any mapped game instruction or entry.
namespace {
uintptr_t base = 0;
int checks = 0, failures = 0;
int playerX = 0, playerY = 0, mapWidth = 0, mapHeight = 0;
int boundsCalls = 0, scanCalls = 0;
bool testReentry = false, testException = false;
bool otherThreadScanRejected = false, otherThreadBoundsRejected = false, concurrentClearPassed = false;
constexpr DWORD TestException = 0xe0c06451;
constexpr uintptr_t fields[] = {0xaa87ac,0xaa87b0,0xaa87b4,0xaa87b8,0xaa87bc,0xaa87c0};
struct Rect { short x0, y0, x1, y1, width, height; };
uintptr_t Address(uintptr_t original) { return base + original - 0x400000; }
void Check(bool condition, const char* message) {
    ++checks;
    if (!condition) { ++failures; if (failures < 12) printf("FAIL: %s\n", message); }
}
int Min(int a, int b) { return a < b ? a : b; }
int Max(int a, int b) { return a > b ? a : b; }
Rect Read() {
    Rect value{};
    for (unsigned i = 0; i < 6; ++i) (&value.x0)[i] = *reinterpret_cast<short*>(Address(fields[i]));
    return value;
}
Rect Expected(bool expanded) {
    Rect value{};
    value.x0 = static_cast<short>(Max(0, playerX - (expanded ? 36 : 30)));
    value.y0 = static_cast<short>(Max(0, playerY - (expanded ? 28 : 20)));
    value.x1 = static_cast<short>(Min(mapWidth, playerX + (expanded ? 28 : 21)));
    value.y1 = static_cast<short>(Min(mapHeight, playerY + (expanded ? 36 : 31)));
    value.width = value.x1 - value.x0;
    value.height = value.y1 - value.y0;
    return value;
}
bool Same(const Rect& a, const Rect& b) { return memcmp(&a, &b, sizeof(a)) == 0; }
unsigned short Cell(int x, int y) { return static_cast<unsigned short>((x * 317 + y * 997 + 1) & 0xffff); }
void __cdecl OriginalBounds() {
    ++boundsCalls;
    Rect value = Expected(false);
    for (unsigned i = 0; i < 6; ++i) *reinterpret_cast<short*>(Address(fields[i])) = (&value.x0)[i];
}
int __cdecl OriginalScan(int direction) {
    ++scanCalls;
    const Rect before = Read(), expected = Expected(false);
    Check(Same(before, expected), "original scanner sees exact legacy six fields");
    const auto* scratch = static_cast<const unsigned short*>(GetMapCacheTestScratch());
    bool equal = true;
    for (int y = 0; y < expected.height; ++y) for (int x = 0; x < expected.width; ++x)
        if (scratch[y * expected.width + x] != Cell(expected.x0 + x, expected.y0 + y)) equal = false;
    Check(equal, "scratch equals original world rectangle cell-for-cell");
    if (before.width >= 51 && before.height >= 51) {
        // All cells reachable by the four original interior reads, including
        // both inclusive extremes of their unchanged 23-cell scan.
        bool interiorEqual = true;
        for (int n = 0; n < 23; ++n) {
            const int indices[] = {(n + 9) * before.width + 19, 9 * before.width + 19 + n,
                                   (n + 9) * before.width + 41, 31 * before.width + 19 + n};
            for (int index : indices) {
                const int x = index % before.width, y = index / before.width;
                if (scratch[index] != Cell(before.x0 + x, before.y0 + y)) interiorEqual = false;
            }
        }
        Check(interiorEqual, "all original four scan lanes preserve world cells");
    }
    if (testReentry) {
        Check(InvokeMapCacheTestScan(direction) == 0, "reentrant scan rejected");
        InvokeMapCacheTestBounds();
        Check(Same(Read(), before), "reentrant bounds cannot overwrite temporary legacy fields");
    }
    if (testException) RaiseException(TestException, 0, 0, nullptr);
    return direction + 100;
}
void* __cdecl OriginalClear(void* destination, int fill, size_t count) { return memset(destination, fill, count); }
bool InvokeExceptionalScan() {
    __try { InvokeMapCacheTestScan(3); }
    __except (GetExceptionCode() == TestException ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { return true; }
    return false;
}
DWORD WINAPI OtherThread(void*) {
    otherThreadScanRejected = InvokeMapCacheTestScan(2) == 0;
    const int count = boundsCalls;
    InvokeMapCacheTestBounds();
    otherThreadBoundsRejected = boundsCalls == count;
    return 0;
}
DWORD WINAPI ClearThread(void*) {
    auto* buffer = static_cast<BYTE*>(GetMapCacheTestBuffer(3));
    concurrentClearPassed = true;
    for (int i = 0; i < 256; ++i) {
        InvokeMapCacheTestClear(buffer, 0xa5, 5202);
        if (buffer[0] != 0xa5 || buffer[8191] != 0xa5) concurrentClearPassed = false;
        InvokeMapCacheTestClear(buffer, 0, 17);
        if (buffer[16] != 0 || buffer[17] != 0xa5 || buffer[8191] != 0xa5) concurrentClearPassed = false;
    }
    return 0;
}
void FillLayer() {
    const Rect now = Read();
    auto* values = static_cast<unsigned short*>(GetMapCacheTestBuffer(2));
    for (int y = 0; y < now.height; ++y) for (int x = 0; x < now.width; ++x)
        values[y * now.width + x] = Cell(now.x0 + x, now.y0 + y);
}
bool ProtectionsMatch(const DWORD* expected) {
    for (unsigned i = 0; i < ARRAYSIZE(MapCachePatchSpecs); ++i) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<void*>(Address(MapCachePatchSpecs[i].operand)), &info, sizeof(info)) != sizeof(info) ||
            info.Protect != expected[i]) return false;
    }
    return true;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    HANDLE file = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 3;
    HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY | SEC_IMAGE, 0, 0, nullptr);
    if (!mapping) { CloseHandle(file); return 4; }
    void* view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (!view) { CloseHandle(mapping); CloseHandle(file); return 5; }
    base = reinterpret_cast<uintptr_t>(view);
    SetMapCacheTestBase(view);
    SetMapCacheTestCallbacks(&OriginalBounds, &OriginalScan, &OriginalClear);
    DWORD protections[ARRAYSIZE(MapCachePatchSpecs)]{};
    for (unsigned i = 0; i < ARRAYSIZE(MapCachePatchSpecs); ++i) {
        MEMORY_BASIC_INFORMATION info{};
        VirtualQuery(reinterpret_cast<void*>(Address(MapCachePatchSpecs[i].operand)), &info, sizeof(info));
        protections[i] = info.Protect;
    }
    Check(ValidateMapCacheFix(), "full original profile validates");
    for (int failAt : {1, 30, 60}) {
        SetMapCacheTestFailureAfterWrite(failAt);
        Check(!ApplyMapCacheFix(), "partial apply failure injected");
        Check(ValidateMapCacheFix(), "partial apply restores every code guard");
        Check(ProtectionsMatch(protections), "partial apply restores every page protection");
        Check(GetMapCacheTestBuffer(0) == nullptr, "partial apply frees unexposed cache");
    }
    SetMapCacheTestFailureAfterWrite(-1);
    Check(ApplyMapCacheFix(), "normal apply succeeds");
    Check(GetMapCacheDiagnostics().PatchCount == 60, "exactly sixty operands changed");
    Check(ProtectionsMatch(protections), "apply restores every page protection");
    Check(ApplyMapCacheFix(), "apply idempotent");
    for (const auto& spec : MapCachePatchSpecs) {
        const DWORD actual = *reinterpret_cast<DWORD*>(Address(spec.operand));
        if (spec.kind == 0) Check(actual == reinterpret_cast<uintptr_t>(GetMapCacheTestBuffer(spec.layer)), "base operand points to expanded layer");
        if (spec.kind == 1) Check(actual == reinterpret_cast<uintptr_t>(GetMapCacheTestScratch()) + spec.offset, "interior operand points to legacy scratch offset");
    }
    for (unsigned i = 0; i < 4; ++i) {
        auto* buffer = static_cast<BYTE*>(GetMapCacheTestBuffer(i));
        memset(buffer, 0xa5, 8192);
        Check(InvokeMapCacheTestClear(buffer, 0, 5202) == buffer, "clear returns original destination");
        bool zero = true;
        for (int j = 0; j < 8192; ++j) if (buffer[j]) zero = false;
        Check(zero, "all expanded bytes cleared");
        MEMORY_BASIC_INFORMATION guard{};
        VirtualQuery(buffer + 8192, &guard, sizeof(guard));
        Check(guard.State == MEM_RESERVE && guard.Protect == 0, "expanded cache ends at reserved guard page");
        memset(buffer, 0xa5, 8192);
        InvokeMapCacheTestClear(buffer, 0, 17);
        Check(buffer[16] == 0 && buffer[17] == 0xa5 && buffer[8191] == 0xa5, "other clear lengths unchanged");
    }
    BYTE unrelated[5204];
    memset(unrelated, 0xa5, sizeof(unrelated));
    InvokeMapCacheTestClear(unrelated, 0, 5202);
    Check(unrelated[0] == 0 && unrelated[5201] == 0 && unrelated[5202] == 0xa5 && unrelated[5203] == 0xa5,
        "49dac0-like stack array retains exact original clear length");
    int scenarios = 0;
    for (int width : {1, 20, 50, 51, 58, 64, 80, 200, 2000}) for (int height : {1, 50, 51, 64, 200, 2000}) {
        mapWidth = width; mapHeight = height;
        *reinterpret_cast<int*>(Address(0xaa87a4)) = width;
        *reinterpret_cast<int*>(Address(0xaa87a8)) = height;
        const int xs[] = {0, width / 2, width - 1, 20, 30, 35, width - 21, width - 28};
        const int ys[] = {0, height / 2, height - 1, 20, 27, 30, height - 31, height - 36};
        for (int x : xs) for (int y : ys) {
            if (x < 0 || x >= width || y < 0 || y >= height) continue;
            playerX = x; playerY = y;
            const int calls = boundsCalls;
            InvokeMapCacheTestBounds();
            Check(boundsCalls == calls + 1, "original bounds called exactly once");
            Check(Same(Read(), Expected(true)), "expanded bounds exactly match direct unclipped formula");
            FillLayer();
            const Rect expanded = Read();
            for (int direction = 0; direction < 8; ++direction) {
                const int scans = scanCalls;
                Check(InvokeMapCacheTestScan(direction) == direction + 100, "scan return preserved");
                Check(scanCalls == scans + 1, "original scan called exactly once");
                Check(Same(Read(), expanded), "scan restores all six expanded fields");
            }
            ++scenarios;
        }
    }
    testReentry = true;
    const Rect before = Read();
    InvokeMapCacheTestScan(1);
    Check(Same(Read(), before), "reentry leaves expanded bounds restored");
    testReentry = false;
    testException = true;
    Check(InvokeExceptionalScan(), "original scan SEH propagates to original outer handler");
    Check(Same(Read(), before), "finally restores all six fields after original exception");
    testException = false;
    HANDLE thread = CreateThread(nullptr, 0, &OtherThread, nullptr, 0, nullptr);
    Check(thread != nullptr, "independent thread test starts");
    if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
    Check(otherThreadScanRejected, "scan on other thread rejected");
    Check(otherThreadBoundsRejected, "bounds on other thread rejected before original call");
    HANDLE clearThread = CreateThread(nullptr, 0, &ClearThread, nullptr, 0, nullptr);
    Check(clearThread != nullptr, "concurrent clear test starts");
    bool mainClearPassed = true;
    auto* concurrentBuffer = static_cast<BYTE*>(GetMapCacheTestBuffer(0));
    for (int i = 0; i < 256; ++i) {
        InvokeMapCacheTestClear(concurrentBuffer, 0x3b, 5202);
        if (concurrentBuffer[0] != 0x3b || concurrentBuffer[8191] != 0x3b) mainClearPassed = false;
    }
    if (clearThread) { WaitForSingleObject(clearThread, INFINITE); CloseHandle(clearThread); }
    Check(mainClearPassed && concurrentClearPassed, "clear arguments remain isolated across threads");
    Check(!RollbackMapCacheFix(), "rollback after gameplay use is refused");
    ResetMapCacheTestRuntimeUse();
    Check(RollbackMapCacheFix(), "initialization-only rollback succeeds");
    Check(ValidateMapCacheFix(), "rollback restores complete original routines");
    Check(ProtectionsMatch(protections), "rollback restores every page protection");
    DWORD oldProtection = 0, ignored = 0;
    BYTE* first = reinterpret_cast<BYTE*>(Address(0x49ab80));
    Check(VirtualProtect(first, 1, PAGE_EXECUTE_READWRITE, &oldProtection) != FALSE, "test mutation protection");
    const BYTE original = *first;
    *first ^= 1;
    VirtualProtect(first, 1, oldProtection, &ignored);
    Check(!ValidateMapCacheFix() && !ApplyMapCacheFix(), "wrong version rejected before writes");
    Check(GetMapCacheTestBuffer(0) == nullptr, "wrong version allocates no buffers");
    VirtualProtect(first, 1, PAGE_EXECUTE_READWRITE, &oldProtection);
    *first = original;
    VirtualProtect(first, 1, oldProtection, &ignored);
    Check(ValidateMapCacheFix(), "test mutation restored");
    UnmapViewOfFile(view); CloseHandle(mapping); CloseHandle(file);
    Check(!ValidateMapCacheFix(), "unmapped image rejected");
    printf("Map-cache fixture scenarios=%d checks=%d failures=%d; no game instruction executed.\n", scenarios, checks, failures);
    return failures ? 1 : 0;
}
