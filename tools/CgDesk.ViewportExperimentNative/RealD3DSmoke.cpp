#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ddraw.h>
#include <d3d.h>
#include <stdio.h>
#include "D3DSeamFix.h"

static bool Result(const char* stage, HRESULT result) {
    printf("%s: 0x%08lX\n", stage, result);
    fflush(stdout);
    return SUCCEEDED(result);
}

int wmain(int argc, wchar_t** argv) {
    if ((argc != 2 && argc != 4) || (wcscmp(argv[1], L"baseline") && wcscmp(argv[1], L"hooked"))) return 2;
    const int width = argc == 4 ? _wtoi(argv[2]) : 320;
    const int height = argc == 4 ? _wtoi(argv[3]) : 240;
    if (width < 320 || width > 1920 || height < 240 || height > 1200) return 2;
    const bool hook = !wcscmp(argv[1], L"hooked");
    printf("Real Windows DirectDraw7 smoke: %s; no game process or game image used\n", hook ? "hooked" : "baseline");
    fflush(stdout);
    printf("Render target and viewport: %d x %d\n", width, height);
    wchar_t library[MAX_PATH];
    const UINT length = GetSystemDirectoryW(library, ARRAYSIZE(library));
    if (!length || length >= ARRAYSIZE(library) - 11) return 3;
    wcscat_s(library, L"\\ddraw.dll");
    HMODULE module = LoadLibraryW(library);
    if (!module) return 3;
    using Create = HRESULT (WINAPI*)(GUID*, void**, REFIID, IUnknown*);
    auto create = reinterpret_cast<Create>(GetProcAddress(module, "DirectDrawCreateEx"));
    if (!create) return 3;
    HWND window = CreateWindowExW(0, L"STATIC", L"CgDesk isolated graphics test", WS_OVERLAPPEDWINDOW,
        0, 0, 320, 240, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return 4;
    IDirectDraw7* draw = nullptr;
    if (!Result("DirectDrawCreateEx", create(nullptr, reinterpret_cast<void**>(&draw), IID_IDirectDraw7, nullptr))) return 5;
    if (hook) {
        printf("Install seam hooks: %d\n", InstallD3DSeamFix(reinterpret_cast<void**>(&draw)));
        fflush(stdout);
    }
    if (!Result("SetCooperativeLevel", draw->SetCooperativeLevel(window, DDSCL_NORMAL))) return 6;
    DDCAPS caps{}; caps.dwSize = sizeof(caps);
    if (!Result("GetCaps", draw->GetCaps(&caps, nullptr))) return 7;
    DDSURFACEDESC2 display{}; display.dwSize = sizeof(display);
    if (!Result("GetDisplayMode", draw->GetDisplayMode(&display))) return 8;
    IUnknown* identity = nullptr;
    if (!Result("QI IUnknown", draw->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&identity)))) return 9;
    identity->Release();
    IDirect3D7* d3d = nullptr;
    if (!Result("QI Direct3D7", draw->QueryInterface(IID_IDirect3D7, reinterpret_cast<void**>(&d3d)))) return 10;
    DDSURFACEDESC2 primaryDesc{}; primaryDesc.dwSize = sizeof(primaryDesc);
    primaryDesc.dwFlags = DDSD_CAPS; primaryDesc.ddsCaps.dwCaps = DDSCAPS_PRIMARYSURFACE;
    IDirectDrawSurface7* primary = nullptr;
    if (!Result("Create primary surface", draw->CreateSurface(&primaryDesc, &primary, nullptr))) return 11;
    DDSURFACEDESC2 desc{}; desc.dwSize = sizeof(desc);
    desc.dwFlags = DDSD_CAPS | DDSD_WIDTH | DDSD_HEIGHT;
    desc.dwWidth = static_cast<DWORD>(width); desc.dwHeight = static_cast<DWORD>(height);
    desc.ddsCaps.dwCaps = DDSCAPS_OFFSCREENPLAIN | DDSCAPS_3DDEVICE | DDSCAPS_SYSTEMMEMORY;
    IDirectDrawSurface7* surface = nullptr;
    if (!Result("CreateSurface", draw->CreateSurface(&desc, &surface, nullptr))) return 11;
    IDirect3DDevice7* device = nullptr;
    if (!Result("CreateDevice RGB", d3d->CreateDevice(IID_IDirect3DRGBDevice, surface, &device))) return 12;
    D3DDEVICEDESC7 deviceCaps{};
    if (!Result("Device GetCaps", device->GetCaps(&deviceCaps))) return 13;
    IDirect3D7* returnedD3D = nullptr;
    if (!Result("Device GetDirect3D", device->GetDirect3D(&returnedD3D))) return 13;
    IUnknown* firstIdentity = nullptr;
    IUnknown* secondIdentity = nullptr;
    if (!Result("D3D identity", d3d->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&firstIdentity))) ||
        !Result("Returned D3D identity", returnedD3D->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&secondIdentity))) ||
        firstIdentity != secondIdentity) return 13;
    firstIdentity->Release(); secondIdentity->Release(); returnedD3D->Release();
    D3DVIEWPORT7 viewport{0, 0, static_cast<DWORD>(width), static_cast<DWORD>(height), 0, 1};
    if (!Result("SetViewport", device->SetViewport(&viewport))) return 14;
    if (!Result("Clear", device->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1, 0))) return 15;
    if (!Result("BeginScene", device->BeginScene())) return 16;
    SeamVertex vertices[] = {
        {0,0,0,1,0xffffffff,0,0}, {100,0,0,1,0xffffffff,1,0},
        {100,100,0,1,0xffffffff,1,1}, {0,100,0,1,0xffffffff,0,1}
    };
    if (!Result("DrawPrimitive (foreign caller)", device->DrawPrimitive(D3DPT_TRIANGLEFAN, 0x144, vertices, 4, 0))) return 17;
    if (!Result("EndScene", device->EndScene())) return 18;
    printf("Release device\n"); fflush(stdout); device->Release();
    printf("Release surface\n"); fflush(stdout); surface->Release();
    printf("Release primary\n"); fflush(stdout); primary->Release();
    printf("Release D3D7\n"); fflush(stdout); d3d->Release();
    printf("Release DDraw7\n"); fflush(stdout); draw->Release();
    DestroyWindow(window);
    FreeLibrary(module);
    const auto status = GetD3DSeamFixStatus();
    printf("PASS real graphics initialization: draw=%ld d3d=%ld device=%ld failures=%ld calls=%ld\n",
        static_cast<long>(status.installedDraw), static_cast<long>(status.installedD3D),
        static_cast<long>(status.installedDevice), static_cast<long>(status.installFailures), static_cast<long>(status.drawCalls));
    return 0;
}
