#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ddraw.h>
#include <intrin.h>
#include <stdint.h>
#include <wchar.h>
#include <string.h>
#include "ViewportStatus.h"
#include "ProfileSignatures.h"
#include "D3DSeamFix.h"
#include "MapCacheFix.h"
#include "GraphicsDiagnostics.h"
#include "TransitionBufferFix.h"
#include "BattleLayoutFix.h"
#include "TransitionSpeedFix.h"

#pragma intrinsic(_ReturnAddress)
static_assert(sizeof(void*) == 4, "The experiment must be built for x86.");
extern "C" __declspec(dllexport) const char CgDeskViewportBuildMarker[] = "CgDeskViewportLaunch-v1";

namespace {
LONG Width = 0, Height = 0;
wchar_t diagnosticPath[32768]{};
bool ReadRequestedSize() {
    HMODULE self = nullptr;
    wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&ReadRequestedSize), &self)) return false;
    DWORD count = GetModuleFileNameW(self, path, ARRAYSIZE(path));
    if (!count || count >= ARRAYSIZE(path)) return false;
    wchar_t* leaf = wcsrchr(path, L'\\');
    if (!leaf || static_cast<SIZE_T>(leaf - path) + ARRAYSIZE(L"\\cgdesk_viewport.ini") > ARRAYSIZE(path)) return false;
    wcscpy_s(leaf, ARRAYSIZE(path) - static_cast<SIZE_T>(leaf - path), L"\\cgdesk_viewport.ini");
    Width = static_cast<LONG>(GetPrivateProfileIntW(L"Viewport", L"Width", 0, path));
    Height = static_cast<LONG>(GetPrivateProfileIntW(L"Viewport", L"Height", 0, path));
    GetPrivateProfileStringW(L"Viewport",L"DiagnosticPath",L"",diagnosticPath,ARRAYSIZE(diagnosticPath),path);
    return Width >= 640 && Height >= 480 && Width <= 1600 && Height <= 1200 &&
        3 * Width == 4 * Height;
}
bool NeedsExpandedCache() { return Width > 1152 || Height > 864; }
constexpr uintptr_t GameBase = 0x400000;
constexpr uintptr_t DrawContext = 0x73d4f4;
constexpr uintptr_t ScreenWidth = 0x5e0198, ScreenHeight = 0x5e019c;
constexpr uintptr_t WindowWidth = 0xa527e0, WindowHeight = 0xa527e4;
constexpr uintptr_t GameWindow = 0xa526ac, Windowed = 0xa526b0;
constexpr uintptr_t RefreshDerived = 0x5080e0;
volatile LONG applyState = 0; // 0 untouched, 1 checking/applying, 2 applied, 3 failed after writes.
ViewportStatus status = [] { ViewportStatus value{}; value.Size = sizeof(value); value.Version = 4;
    value.LastForwardResult = S_OK; return value; }();
PVOID volatile realCreateEx = nullptr;

bool Accessible(uintptr_t address, SIZE_T length, bool writable) {
    if (!address || !length || address > UINTPTR_MAX - length) return false;
    uintptr_t end = address + length;
    while (address < end) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<void*>(address), &info, sizeof(info)) != sizeof(info)) return false;
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        DWORD protection = info.Protect & 0xff;
        if (writable) {
            if (protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
                protection != PAGE_EXECUTE_READWRITE && protection != PAGE_EXECUTE_WRITECOPY) return false;
        } else if (protection != PAGE_READONLY && protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
                   protection != PAGE_EXECUTE_READ && protection != PAGE_EXECUTE_READWRITE && protection != PAGE_EXECUTE_WRITECOPY) return false;
        uintptr_t next = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        if (next <= address) return false;
        address = next;
    }
    return true;
}

bool TargetExecutable() {
    wchar_t name[32768];
    DWORD count = GetModuleFileNameW(nullptr, name, ARRAYSIZE(name));
    if (!count || count >= ARRAYSIZE(name)) return false;
    const wchar_t* leaf = wcsrchr(name, L'\\');
    return _wcsicmp(leaf ? leaf + 1 : name, L"cg_se_3000.exe") == 0;
}

struct Snapshot {
    uintptr_t addresses[16];
    DWORD values[16];
    HWND window;
    RECT originalWindow;
    bool captured;
    bool writesStarted;
    bool windowAttempted;
};

bool Rollback(Snapshot* snapshot) {
    InterlockedExchange(&status.RollbackAttempted, 1);
    bool restored = RollbackTransitionSpeedFix();
    if(!RollbackBattleLayoutFix())restored=false;
    if(!RollbackTransitionBufferFix())restored=false;
    if(!RollbackMapCacheFix())restored=false;
    __try {
        for (unsigned i = 0; i < ARRAYSIZE(snapshot->addresses); ++i) {
            if (!Accessible(snapshot->addresses[i], sizeof(DWORD), true)) { restored = false; continue; }
            *reinterpret_cast<volatile DWORD*>(snapshot->addresses[i]) = snapshot->values[i];
        }
        if (snapshot->windowAttempted && IsWindow(snapshot->window)) {
            if (!SetWindowPos(snapshot->window, nullptr, 0, 0,
                snapshot->originalWindow.right - snapshot->originalWindow.left,
                snapshot->originalWindow.bottom - snapshot->originalWindow.top,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE)) restored = false;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status.LastException = GetExceptionCode();
        restored = false;
    }
    InterlockedExchange(&status.RollbackSucceeded, restored ? 1 : 0);
    return restored;
}

void TryApplyProfile(void* returnAddress, void** output) {
    LONG prior = InterlockedCompareExchange(&applyState, 1, 0);
    if (prior) {
        status.LastGuard = prior == 2 ? GuardAlreadyApplied : GuardPreviousFailure;
        return;
    }
    InterlockedIncrement(&status.ProfileAttempts);
    Snapshot snapshot{};
    DWORD guard = GuardNone;
    uintptr_t context = 0;
    __try {
        if (!TargetExecutable()) { guard = GuardWrongExecutable; __leave; }
        if (!ReadRequestedSize()) { guard = GuardUnsupportedDimensions; __leave; }
        status.RequestedWidth = Width;
        status.RequestedHeight = Height;
        if (reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)) != GameBase) { guard = GuardWrongBase; __leave; }
        uintptr_t caller = reinterpret_cast<uintptr_t>(returnAddress);
        status.Caller = static_cast<DWORD>(caller);
        if (caller < 0x481100 || caller > 0x481ea3) { guard = GuardWrongCaller; __leave; }
        for (unsigned i = 0; i < ARRAYSIZE(ProfileSignatures); ++i) {
            const auto& signature = ProfileSignatures[i];
            if (!Accessible(signature.address, sizeof(signature.bytes), false) ||
                memcmp(reinterpret_cast<void*>(signature.address), signature.bytes, sizeof(signature.bytes))) {
                guard = GuardSignatureMismatch;
                __leave;
            }
        }
        if (NeedsExpandedCache() && !ValidateMapCacheFix()) { guard = GuardMapCache; __leave; }
        if (Height > 1023 && !ValidateTransitionBufferFix()) { guard = GuardTransitionBuffers; __leave; }
        if (Width > 800 && !ValidateBattleLayoutFix()) { guard = GuardBattleLayout; __leave; }
        if (Width > 800 && !ValidateTransitionSpeedFix()) { guard = GuardTransitionSpeed; __leave; }
        if (!Accessible(Windowed, sizeof(DWORD), false) || *reinterpret_cast<DWORD*>(Windowed) != 1) { guard = GuardNotWindowed; __leave; }
        if (!Accessible(GameWindow, sizeof(HWND), false)) { guard = GuardInvalidWindow; __leave; }
        snapshot.window = *reinterpret_cast<HWND*>(GameWindow);
        if (!snapshot.window || !IsWindow(snapshot.window)) { guard = GuardInvalidWindow; __leave; }
        DWORD windowProcess = 0;
        DWORD windowThread = GetWindowThreadProcessId(snapshot.window, &windowProcess);
        if (windowProcess != GetCurrentProcessId() || windowThread != GetCurrentThreadId() ||
            (GetWindowLongPtrW(snapshot.window, GWL_STYLE) & WS_CHILD)) { guard = GuardInvalidWindow; __leave; }
        if (!Accessible(DrawContext, sizeof(uintptr_t), false)) { guard = GuardInvalidContext; __leave; }
        context = *reinterpret_cast<uintptr_t*>(DrawContext);
        status.Context = static_cast<DWORD>(context);
        if (!Accessible(context, 0x9c, true)) { guard = GuardInvalidContext; __leave; }
        if (reinterpret_cast<uintptr_t>(output) != context + 0x04) { guard = GuardWrongOutput; __leave; }
        if (*reinterpret_cast<uintptr_t*>(context + 0x04) || *reinterpret_cast<uintptr_t*>(context + 0x0c)) { guard = GuardAlreadyInitialized; __leave; }
        if (!Accessible(ScreenWidth, sizeof(DWORD), true) || !Accessible(ScreenHeight, sizeof(DWORD), true)) { guard = GuardUnwritableData; __leave; }
        DWORD originalWidth = *reinterpret_cast<DWORD*>(ScreenWidth), originalHeight = *reinterpret_cast<DWORD*>(ScreenHeight);
        status.OriginalWidth = originalWidth;
        status.OriginalHeight = originalHeight;
        if (!((originalWidth == 640 && originalHeight == 480) || (originalWidth == 800 && originalHeight == 600)) ||
            *reinterpret_cast<DWORD*>(context + 0x94) != originalWidth || *reinterpret_cast<DWORD*>(context + 0x98) != originalHeight) {
            guard = GuardUnsupportedDimensions;
            __leave;
        }
        // Six explicit inputs plus the ten fields written by the validated native refresh function.
        const uintptr_t fields[] = { ScreenWidth, ScreenHeight, WindowWidth, WindowHeight, context + 0x94, context + 0x98,
            0xd6f4e4, 0xd6f4e0, 0x854d34, 0x5d8218, 0x854d38, 0x5d821c, 0xcd3a10, 0xcd3a44, 0x5e7bfc, 0x5e6e4c };
        static_assert(ARRAYSIZE(fields) == ARRAYSIZE(snapshot.addresses));
        for (unsigned i = 0; i < ARRAYSIZE(fields); ++i) {
            if (!Accessible(fields[i], sizeof(DWORD), true)) { guard = GuardUnwritableData; __leave; }
            snapshot.addresses[i] = fields[i];
            snapshot.values[i] = *reinterpret_cast<DWORD*>(fields[i]);
        }
        RECT desired{ 0, 0, Width, Height };
        SetLastError(0);
        LONG_PTR style = GetWindowLongPtrW(snapshot.window, GWL_STYLE);
        if (!style && GetLastError()) { guard = GuardWindowGeometry; __leave; }
        SetLastError(0);
        LONG_PTR exStyle = GetWindowLongPtrW(snapshot.window, GWL_EXSTYLE);
        if (!exStyle && GetLastError()) { guard = GuardWindowGeometry; __leave; }
        if (!GetWindowRect(snapshot.window, &snapshot.originalWindow) ||
            !AdjustWindowRectEx(&desired, static_cast<DWORD>(style), GetMenu(snapshot.window) != nullptr, static_cast<DWORD>(exStyle))) {
            guard = GuardWindowGeometry;
            __leave;
        }
        snapshot.captured = true;
        snapshot.writesStarted = true;
        *reinterpret_cast<volatile DWORD*>(ScreenWidth) = Width;
        *reinterpret_cast<volatile DWORD*>(ScreenHeight) = Height;
        *reinterpret_cast<volatile DWORD*>(WindowWidth) = Width;
        *reinterpret_cast<volatile DWORD*>(WindowHeight) = Height;
        *reinterpret_cast<volatile DWORD*>(context + 0x94) = Width;
        *reinterpret_cast<volatile DWORD*>(context + 0x98) = Height;
        reinterpret_cast<void(__cdecl*)()>(RefreshDerived)();
        snapshot.windowAttempted = true;
        if (!SetWindowPos(snapshot.window, nullptr, 0, 0, desired.right - desired.left, desired.bottom - desired.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE)) {
            status.LastWin32Error = GetLastError();
            guard = GuardWindowGeometry;
            __leave;
        }
        RECT client{};
        if (!GetClientRect(snapshot.window, &client) || client.right != Width || client.bottom != Height) { guard = GuardWindowGeometry; __leave; }
        if (Height > 1023 && !ApplyTransitionBufferFix()) { guard = GuardTransitionBuffers; __leave; }
        if (Width > 800 && !ApplyBattleLayoutFix()) { guard = GuardBattleLayout; __leave; }
        if (Width > 800 && !ApplyTransitionSpeedFix()) { guard = GuardTransitionSpeed; __leave; }
        if (NeedsExpandedCache() && !ApplyMapCacheFix()) { guard = GuardMapCache; __leave; }
        InterlockedExchange(&status.Applied, 1);
        InterlockedExchange(&applyState, 2);
        if(diagnosticPath[0])StartGraphicsDiagnostics(diagnosticPath);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status.LastException = GetExceptionCode();
        guard = GuardException;
    }
    status.LastGuard = guard;
    if (status.Applied) return;
    if (snapshot.captured && snapshot.writesStarted) {
        Rollback(&snapshot);
        InterlockedExchange(&applyState, 3);
    } else {
        InterlockedExchange(&applyState, 0);
    }
}

using RealCreateEx = HRESULT (WINAPI*)(GUID*, void**, REFIID, IUnknown*);
RealCreateEx ResolveReal() {
    auto existing = reinterpret_cast<RealCreateEx>(InterlockedCompareExchangePointer(&realCreateEx, nullptr, nullptr));
    if (existing) return existing;
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&ResolveReal), &self)) return nullptr;
    wchar_t path[32768];
    DWORD count = GetModuleFileNameW(self, path, ARRAYSIZE(path));
    if (!count || count >= ARRAYSIZE(path)) return nullptr;
    wchar_t* leaf = wcsrchr(path, L'\\');
    if (!leaf || static_cast<SIZE_T>(leaf - path) + ARRAYSIZE(L"\\cgdesk_viewport_sys.dll") > ARRAYSIZE(path)) return nullptr;
    wcscpy_s(leaf, ARRAYSIZE(path) - static_cast<SIZE_T>(leaf - path), L"\\cgdesk_viewport_sys.dll");
    HMODULE system = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!system || system == self) return nullptr;
    auto resolved = reinterpret_cast<RealCreateEx>(GetProcAddress(system, "DirectDrawCreateEx"));
    if (!resolved) { FreeLibrary(system); return nullptr; }
    auto earlier = InterlockedCompareExchangePointer(&realCreateEx, reinterpret_cast<void*>(resolved), nullptr);
    if (earlier) { FreeLibrary(system); return reinterpret_cast<RealCreateEx>(earlier); }
    // Keep the one successful library reference while the forwarding DLL remains loaded.
    return resolved;
}
}

extern "C" HRESULT WINAPI CgDeskDirectDrawCreateEx(GUID* guid, void** output, REFIID iid, IUnknown* outer) {
    DWORD lastError = GetLastError();
    InterlockedIncrement(&status.WrapperCalls);
    RealCreateEx real = ResolveReal();
    if (!real) {
        InterlockedIncrement(&status.ForwardingFailures);
        status.LastWin32Error = GetLastError();
        status.LastForwardResult = E_FAIL;
        return E_FAIL;
    }
    TryApplyProfile(_ReturnAddress(), output);
    if (!GraphicsDiagnosticsHealthy() || !TransitionBufferFixHealthy() || !BattleLayoutFixHealthy() || !TransitionSpeedFixHealthy() || !GetMapCacheDiagnostics().ProtectionRestored ||
        (status.RollbackAttempted && !status.RollbackSucceeded)) {
        status.LastForwardResult = E_FAIL;
        return E_FAIL;
    }
    SetLastError(lastError);
    HRESULT result = real(guid, output, iid, outer);
    if (SUCCEEDED(result) && status.Applied && output && *output) InstallD3DSeamFix(output);
    status.LastForwardResult = result;
    return result;
}

extern "C" DWORD WINAPI CgDeskViewportStatus(ViewportStatus* output, DWORD size) {
    if (!output || size < sizeof(ViewportStatus)) return ERROR_INSUFFICIENT_BUFFER;
    __try {
        ViewportStatus current = status;
        current.SeamFix = GetD3DSeamFixStatus();
        current.MapCache = GetMapCacheDiagnostics();
        *output = current;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return ERROR_NOACCESS; }
    return ERROR_SUCCESS;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
