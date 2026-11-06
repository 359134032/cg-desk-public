#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ddraw.h>
#include <d3d.h>
#include <stdio.h>
#include <string.h>
#include "D3DSeamFix.h"

namespace {
int checks = 0, failures = 0;
void Check(bool passed, const char* name) {
    ++checks;
    if (!passed) { ++failures; printf("FAIL %s\n", name); }
}

void MakeQuad(int x, int y, int width, int height, double scale, int textureWidth,
    int textureHeight, SeamVertex* output, double verticalScale = 0) {
    if (verticalScale == 0) verticalScale = scale;
    float left = static_cast<float>(static_cast<int>(x * scale));
    float top = static_cast<float>(static_cast<int>(y * verticalScale));
    float right = left + static_cast<int>(width * scale);
    float bottom = top + static_cast<int>(height * verticalScale);
    float u0 = 1.05f / textureWidth, u1 = static_cast<float>(width + 1) / textureWidth;
    float v0 = 1.05f / textureHeight, v1 = static_cast<float>(height + 1) / textureHeight;
    output[0] = {left, top, 0, 1, 0xffffffff, u0, v0};
    output[1] = {right, top, 0, 1, 0xffffffff, u1, v0};
    output[2] = {right, bottom, 0, 1, 0xffffffff, u1, v1};
    output[3] = {left, bottom, 0, 1, 0xffffffff, u0, v1};
}

void RejectUnchanged(SeamVertex* quad, const SeamGeometryContext& context, const char* name,
    unsigned primitive = 6, uint32_t fvf = 0x144, unsigned count = 4) {
    SeamVertex output[4], unchanged[4];
    memset(output, 0xa5, sizeof(output));
    memcpy(unchanged, output, sizeof(output));
    bool changed = CorrectD3DSeamQuad(primitive, fvf, quad, count, context, output);
    Check(!changed && memcmp(output, unchanged, sizeof(output)) == 0, name);
}

// Small in-process COM fixtures: no DirectDraw creation or game process access.
struct FakeObject { void** table; LONG refs; };
void* drawTable[30]{}, *d3dTable[8]{}, *deviceTable[49]{};
FakeObject draw{drawTable, 1}, d3d{d3dTable, 1}, device{deviceTable, 1};
using QueryFn = HRESULT (WINAPI*)(void*, REFIID, void**);
using ReleaseFn = ULONG (WINAPI*)(void*);
using AddRefFn = ULONG (WINAPI*)(void*);
using CreateFn = HRESULT (WINAPI*)(void*, REFCLSID, IDirectDrawSurface7*, IDirect3DDevice7**);
using DrawFn = HRESULT (WINAPI*)(void*, D3DPRIMITIVETYPE, DWORD, void*, DWORD, DWORD);
ULONG WINAPI FakeAddRef(void* object) { return static_cast<ULONG>(++static_cast<FakeObject*>(object)->refs); }
ULONG WINAPI FakeRelease(void* object) { return static_cast<ULONG>(--static_cast<FakeObject*>(object)->refs); }
HRESULT WINAPI FakeQuery(void* object, REFIID iid, void** output) {
    if (IsEqualGUID(iid, IID_IDirect3D7)) *output = &d3d;
    else if (IsEqualGUID(iid, IID_IDirect3DDevice7)) *output = &device;
    else if (IsEqualGUID(iid, IID_IDirectDraw7)) *output = &draw;
    else if (IsEqualGUID(iid, IID_IUnknown)) *output = object == &device ? &device : &draw;
    else { *output = nullptr; return E_NOINTERFACE; }
    FakeAddRef(*output);
    return S_OK;
}
HRESULT WINAPI FakeCreate(void*, REFCLSID, IDirectDrawSurface7*, IDirect3DDevice7** output) {
    *output = reinterpret_cast<IDirect3DDevice7*>(&device);
    FakeAddRef(&device);
    return S_OK;
}
void* lastVertices = nullptr;
bool originalThis = false;
HRESULT WINAPI FakeDraw(void* object, D3DPRIMITIVETYPE, DWORD, void* vertices, DWORD, DWORD) {
    originalThis = object == &device;
    lastVertices = vertices;
    return S_FALSE;
}

void TestCOM() {
    void** tables[] = {drawTable, d3dTable, deviceTable};
    for (void** table : tables) {
        table[0] = reinterpret_cast<void*>(&FakeQuery);
        table[1] = reinterpret_cast<void*>(&FakeAddRef);
        table[2] = reinterpret_cast<void*>(&FakeRelease);
    }
    d3dTable[4] = reinterpret_cast<void*>(&FakeCreate);
    deviceTable[25] = reinterpret_cast<void*>(&FakeDraw);
    void* wrapped = &draw;
    Check(InstallD3DSeamFix(&wrapped), "install DDraw7 fixture");
    auto iface = reinterpret_cast<IDirectDraw7*>(wrapped);
    Check(wrapped != &draw && draw.table == drawTable, "facade leaves original DDraw7 vptr unchanged");
    LONG addRefs = draw.refs;
    Check(iface->AddRef() == static_cast<ULONG>(addRefs + 1), "AddRef thunk forwards original this");
    iface->Release();
    void* identity = nullptr;
    Check(iface->QueryInterface(IID_IUnknown, &identity) == S_OK && identity != &draw,
        "IUnknown returns canonical facade");
    auto unknown = reinterpret_cast<IUnknown*>(identity);
    LONG refs = draw.refs;
    Check(unknown->Release() == static_cast<ULONG>(refs - 1), "Release result forwarded");
    void* result = nullptr;
    Check(iface->QueryInterface(IID_IDirect3D7, &result) == S_OK && result != &d3d &&
        d3d.table == d3dTable, "QI wraps D3D7 and leaves its original vptr unchanged");
    auto d3dIface = reinterpret_cast<IDirect3D7*>(result);
    void* d3dIdentity = nullptr;
    Check(d3dIface->QueryInterface(IID_IUnknown, &d3dIdentity) == S_OK && d3dIdentity == identity,
        "aggregate DDraw7 and D3D7 have same IUnknown identity");
    reinterpret_cast<IUnknown*>(d3dIdentity)->Release();
    void* roundTrip = nullptr;
    Check(unknown->QueryInterface(IID_IDirectDraw7, &roundTrip) == S_OK && roundTrip == iface,
        "IUnknown QI round trip returns original facade");
    reinterpret_cast<IDirectDraw7*>(roundTrip)->Release();
    IDirect3DDevice7* created = nullptr;
    Check(d3dIface->CreateDevice(IID_IDirect3DHALDevice, nullptr, &created) == S_OK &&
        created != reinterpret_cast<IDirect3DDevice7*>(&device) && device.table == deviceTable,
        "CreateDevice wraps Device7 and leaves original vptr unchanged");
    SeamVertex quad[4]{};
    Check(created->DrawPrimitive(D3DPT_TRIANGLEFAN, 0x144, quad, 4, 0) == S_FALSE && originalThis &&
        lastVertices == quad && GetD3DSeamFixStatus().callerMatches == 0, "foreign caller forwards original this, vertices and result");
    int32_t before = GetD3DSeamFixStatus().retainedRecords;
    Check(InstallD3DSeamFix(&wrapped) && GetD3DSeamFixStatus().retainedRecords == before, "repeat install idempotent");
    draw.refs = 1;
    Check(iface->Release() == 0, "zero Release forwarded");
    Check(draw.table == drawTable && d3d.table == d3dTable && device.table == deviceTable,
        "all original system vptrs remain unchanged after release");
    // Simulate fresh COM allocation at a previously released address.
    draw.table = drawTable; draw.refs = 1;
    void* reused = &draw;
    Check(InstallD3DSeamFix(&reused) && reused == wrapped && GetD3DSeamFixStatus().retainedRecords == before &&
        reinterpret_cast<IDirectDraw7*>(reused)->AddRef() == 2,
        "reused original address uses same facade and forwards to current original");
}
}

int main() {
    unsigned changed = 0;
    const SeamGeometryContext context{1024, 768, 66, 50};
    for (int y = 0; y < 480; y += 48) {
        for (int x = 0; x < 640; x += 64) {
            SeamVertex quad[4], corrected[4];
            MakeQuad(x, y, 64, 48, 1.6, 66, 50, quad);
            bool result = CorrectD3DSeamQuad(6, 0x144, quad, 4, context, corrected);
            if (result) ++changed;
            const SeamVertex* out = result ? corrected : quad;
            Check(out[0].x == static_cast<int>(x * 1.6) && out[0].y == static_cast<int>(y * 1.6) &&
                out[1].x == static_cast<int>((x + 64) * 1.6) && out[3].y == static_cast<int>((y + 48) * 1.6),
                "1024 shared tile boundaries have no gaps");
            Check(out[1].x - quad[1].x >= 0 && out[1].x - quad[1].x <= 1 &&
                out[3].y - quad[3].y >= 0 && out[3].y - quad[3].y <= 1,
                "1024 correction bounded to one pixel");
            for (unsigned i = 0; i < 4; ++i) Check(out[i].u == quad[i].u && out[i].v == quad[i].v &&
                out[i].color == quad[i].color && out[i].z == quad[i].z && out[i].rhw == quad[i].rhw,
                "non-position vertex data unchanged");
            MakeQuad(x, y, 64, 48, 1.25, 66, 50, quad);
            RejectUnchanged(quad, {800, 600, 66, 50}, "native800 grid needs no change");
        }
    }
    Check(changed > 0, "fractional grid exercised corrections");
    const int modes[][2] = {{640,480},{800,600},{1024,768},{1152,864},{1280,960},{1400,1050},{1440,1080},{1600,1200}};
    for (const auto& mode : modes) {
        for (int y = 0; y < 480; y += 48) for (int x = 0; x < 640; x += 64) {
            SeamVertex source[4], fixed[4];
            const double sx = mode[0] / 640.0, sy = mode[1] / 480.0;
            MakeQuad(x, y, 64, 48, sx, 66, 50, source, sy);
            bool repaired = CorrectD3DSeamQuad(6, 0x144, source, 4, {mode[0],mode[1],66,50}, fixed);
            const auto* out = repaired ? fixed : source;
            Check(out[1].x == static_cast<int>((x + 64) * sx) && out[3].y == static_cast<int>((y + 48) * sy),
                "all adapted aspect ratios retain shared UI tile edges");
        }
    }
    SeamVertex quad[4];
    MakeQuad(128, 96, 64, 48, 3, 66, 50, quad, 2.25);
    RejectUnchanged(quad, {1920, 1080, 66, 50}, "withdrawn 1080p mode remains blocked after real map-coverage failure");
    MakeQuad(128, 96, 64, 48, 1, 66, 50, quad);
    RejectUnchanged(quad, context, "world1:1 sprite unchanged");
    MakeQuad(-64, 96, 64, 48, 1.6, 66, 50, quad);
    RejectUnchanged(quad, context, "negative origin unchanged");
    MakeQuad(128, 96, 64, 48, 1.6, 66, 50, quad);
    for (unsigned i = 0; i < 4; ++i) quad[i].u = 1 - quad[i].u;
    RejectUnchanged(quad, context, "mirrored UV unchanged");
    MakeQuad(128, 96, 64, 48, 1.6, 66, 50, quad);
    quad[2].x += 2;
    RejectUnchanged(quad, context, "nonrectangular quad unchanged");
    MakeQuad(128, 96, 64, 48, 1.6, 66, 50, quad);
    RejectUnchanged(quad, context, "wrong FVF unchanged", 6, 0x1c4);
    RejectUnchanged(quad, context, "wrong primitive unchanged", 5);
    RejectUnchanged(quad, context, "wrong count unchanged", 6, 0x144, 3);
    MakeQuad(128, 96, 64, 48, 1.6, 128, 128, quad);
    SeamVertex corrected[4];
    Check(CorrectD3DSeamQuad(6, 0x144, quad, 4, {1024, 768, 128, 128}, corrected),
        "power of two square texture dimensions supported");
    TestCOM();
    printf("SeamFixTests: %d checks, %d failures, %u corrected tiles\n", checks, failures, changed);
    return failures ? 1 : 0;
}
