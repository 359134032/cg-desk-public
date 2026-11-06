#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ddraw.h>
#include <stdio.h>
#include "ViewportStatus.h"

using CreateEx = HRESULT (WINAPI*)(GUID*, void**, REFIID, IUnknown*);

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { fwprintf(stderr, L"Usage: ViewportSmokeHost <absolute-proxy-dll> <absolute-system-alias-dll>\n"); return 2; }
    HMODULE real = LoadLibraryW(argv[2]);
    HMODULE proxy = LoadLibraryW(argv[1]);
    if (!real || !proxy) { printf("Load failed: %lu\n", GetLastError()); return 3; }
    auto direct = reinterpret_cast<CreateEx>(GetProcAddress(real, "DirectDrawCreateEx"));
    auto forwarded = reinterpret_cast<CreateEx>(GetProcAddress(proxy, "DirectDrawCreateEx"));
    auto query = reinterpret_cast<QueryViewportStatus>(GetProcAddress(proxy, "CgDeskViewportStatus"));
    if (!direct || !forwarded || !query || direct == forwarded) return 4;
    auto base = reinterpret_cast<const unsigned char*>(real);
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    auto exports = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    auto names = reinterpret_cast<const DWORD*>(base + exports->AddressOfNames);
    if (!exports->NumberOfNames || exports->NumberOfNames > 4096) return 5;
    DWORD checkedForwarders = 0;
    for (DWORD index = 0; index < exports->NumberOfNames; ++index) {
        const char* name = reinterpret_cast<const char*>(base + names[index]);
        if (strcmp(name, "DirectDrawCreateEx") == 0) continue;
        if (!GetProcAddress(real, name) || GetProcAddress(real, name) != GetProcAddress(proxy, name)) {
            printf("Export was not transparently forwarded: %s\n", name);
            return 5;
        }
        ++checkedForwarders;
    }
    printf("Verified all %lu ordinary export addresses against the local Windows DLL.\n", checkedForwarders);
    void* directObject = nullptr;
    void* proxyObject = nullptr;
    HRESULT expected = direct(nullptr, &directObject, IID_IDirectDraw7, nullptr);
    HRESULT actual = forwarded(nullptr, &proxyObject, IID_IDirectDraw7, nullptr);
    ViewportStatus status{};
    DWORD queried = query(&status, sizeof(status));
    printf("direct HRESULT=0x%08lX; proxy HRESULT=0x%08lX; direct output=%d; proxy output=%d\n", expected, actual, directObject != nullptr, proxyObject != nullptr);
    printf("status=%lu version=%lu calls=%ld attempts=%ld applied=%ld guard=%lu forwardingFailures=%ld\n",
        queried, status.Version, status.WrapperCalls, status.ProfileAttempts, status.Applied, status.LastGuard, status.ForwardingFailures);
    bool success = expected == actual && (directObject != nullptr) == (proxyObject != nullptr) && queried == ERROR_SUCCESS &&
        checkedForwarders + 1 == exports->NumberOfNames && query(nullptr, 0) == ERROR_INSUFFICIENT_BUFFER &&
        status.WrapperCalls == 1 && status.Applied == 0 && status.LastGuard == GuardWrongExecutable && status.ForwardingFailures == 0;
    if (directObject) reinterpret_cast<IUnknown*>(directObject)->Release();
    if (proxyObject) reinterpret_cast<IUnknown*>(proxyObject)->Release();
    FreeLibrary(proxy);
    FreeLibrary(real);
    printf("%s: isolated host forwarding and fail-closed target guard; no game was started.\n", success ? "PASS" : "FAIL");
    return success ? 0 : 6;
}
