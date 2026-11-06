#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ddraw.h>
#include <d3d.h>
#include <intrin.h>
#include <math.h>
#include <string.h>
#include <stddef.h>
#include "D3DSeamFix.h"

#pragma intrinsic(_ReturnAddress)
static_assert(sizeof(void*) == 4, "Only the verified x86 profile is supported");

namespace {
bool Near(double a, double b, double tolerance = 0.001) noexcept {
    return isfinite(a) && isfinite(b) && fabs(a - b) <= tolerance;
}

bool RecoverExtent(float low, float high, int texture, int maximum, int* extent) noexcept {
    if (!isfinite(low) || !isfinite(high) || low < 0 || high > 1 || high <= low) return false;
    double start = static_cast<double>(low) * texture;
    double end = static_cast<double>(high) * texture;
    double left = floor(start), right = floor(end + 0.5);
    if (!Near(start, left + 0.05, 0.002) || !Near(end, right, 0.002)) return false;
    int size = static_cast<int>(right - left);
    if (size < 1 || size > maximum) return false;
    *extent = size;
    return true;
}

bool CorrectEdge(float start, float end, int sourceSize, double scale,
    int screenSize, float* corrected) noexcept {
    if (!Near(start, floor(start + 0.5)) || !Near(end, floor(end + 0.5)) ||
        start < 0 || end <= start || end > screenSize) return false;
    int scaledSize = static_cast<int>(sourceSize * scale);
    if (!Near(end - start, scaledSize)) return false;
    int origin = static_cast<int>(ceil(start / scale - 0.0000001));
    if (origin < 0 || origin > 32767 || static_cast<int>(origin * scale) != static_cast<int>(start)) return false;
    int sharedEdge = static_cast<int>((origin + sourceSize) * scale);
    int increase = sharedEdge - static_cast<int>(end);
    if (increase < 0 || increase > 1 || sharedEdge > screenSize) return false;
    *corrected = static_cast<float>(sharedEdge);
    return true;
}
}

bool CorrectD3DSeamQuad(unsigned primitive, uint32_t fvf, const SeamVertex* input,
    unsigned count, const SeamGeometryContext& context, SeamVertex* output) noexcept {
    if (!input || !output || primitive != 6 || fvf != 0x144 || count != 4 ||
        context.screenWidth < 640 || context.screenWidth > 1600 ||
        context.screenHeight < 480 || context.screenHeight > 1200 ||
        3 * context.screenWidth != 4 * context.screenHeight ||
        context.textureWidth < 1 || context.textureWidth > 4096 ||
        context.textureHeight < 1 || context.textureHeight > 4096) return false;
    const SeamVertex& a = input[0]; const SeamVertex& b = input[1];
    const SeamVertex& c = input[2]; const SeamVertex& d = input[3];
    if (!Near(a.x, d.x) || !Near(b.x, c.x) || !Near(a.y, b.y) || !Near(c.y, d.y) ||
        !Near(a.u, d.u) || !Near(b.u, c.u) || !Near(a.v, b.v) || !Near(c.v, d.v)) return false;
    for (unsigned i = 0; i < 4; ++i) {
        if (!Near(input[i].z, 0) || !Near(input[i].rhw, 1) || input[i].color != a.color) return false;
    }
    int sourceWidth = 0, sourceHeight = 0;
    if (!RecoverExtent(a.u, b.u, context.textureWidth, 64, &sourceWidth) ||
        !RecoverExtent(a.v, d.v, context.textureHeight, 48, &sourceHeight)) return false;
    // A 1:1 world sprite can otherwise resemble a tiny scaled background tile.
    if (Near(b.x - a.x, sourceWidth) && Near(d.y - a.y, sourceHeight)) return false;
    float right = 0, bottom = 0;
    if (!CorrectEdge(a.x, b.x, sourceWidth, context.screenWidth / 640.0, context.screenWidth, &right) ||
        !CorrectEdge(a.y, d.y, sourceHeight, context.screenHeight / 480.0, context.screenHeight, &bottom)) return false;
    if (Near(right, b.x) && Near(bottom, d.y)) return false;
    memcpy(output, input, sizeof(SeamVertex) * 4);
    output[1].x = output[2].x = right;
    output[2].y = output[3].y = bottom;
    return true;
}

namespace {
enum class Kind { Draw, D3D, Device, Unknown };
struct Record {
    void** table;
    void* originalObject;
    Record* next;
    Kind kind;
    bool retired;
    void* entries[49];
};
static_assert(offsetof(Record, originalObject) == 4, "x86 facade forwarding layout");
// Forward the original stdcall stack unchanged except for its interface pointer.
// The system object retains its original vptr, including during internal calls.
#define FORWARD_SLOT(n) __declspec(naked) void Forward##n() { \
    __asm mov eax, [esp + 4] \
    __asm mov eax, [eax + 4] \
    __asm mov [esp + 4], eax \
    __asm mov eax, [eax] \
    __asm jmp dword ptr [eax + n * 4] \
}
FORWARD_SLOT(0) FORWARD_SLOT(1) FORWARD_SLOT(2) FORWARD_SLOT(3) FORWARD_SLOT(4)
FORWARD_SLOT(5) FORWARD_SLOT(6) FORWARD_SLOT(7) FORWARD_SLOT(8) FORWARD_SLOT(9)
FORWARD_SLOT(10) FORWARD_SLOT(11) FORWARD_SLOT(12) FORWARD_SLOT(13) FORWARD_SLOT(14)
FORWARD_SLOT(15) FORWARD_SLOT(16) FORWARD_SLOT(17) FORWARD_SLOT(18) FORWARD_SLOT(19)
FORWARD_SLOT(20) FORWARD_SLOT(21) FORWARD_SLOT(22) FORWARD_SLOT(23) FORWARD_SLOT(24)
FORWARD_SLOT(25) FORWARD_SLOT(26) FORWARD_SLOT(27) FORWARD_SLOT(28) FORWARD_SLOT(29)
FORWARD_SLOT(30) FORWARD_SLOT(31) FORWARD_SLOT(32) FORWARD_SLOT(33) FORWARD_SLOT(34)
FORWARD_SLOT(35) FORWARD_SLOT(36) FORWARD_SLOT(37) FORWARD_SLOT(38) FORWARD_SLOT(39)
FORWARD_SLOT(40) FORWARD_SLOT(41) FORWARD_SLOT(42) FORWARD_SLOT(43) FORWARD_SLOT(44)
FORWARD_SLOT(45) FORWARD_SLOT(46) FORWARD_SLOT(47) FORWARD_SLOT(48)
#undef FORWARD_SLOT
#define SLOT(n) reinterpret_cast<void*>(&Forward##n)
void* const forwarding[] = {
    SLOT(0), SLOT(1), SLOT(2), SLOT(3), SLOT(4), SLOT(5), SLOT(6), SLOT(7), SLOT(8), SLOT(9),
    SLOT(10), SLOT(11), SLOT(12), SLOT(13), SLOT(14), SLOT(15), SLOT(16), SLOT(17), SLOT(18), SLOT(19),
    SLOT(20), SLOT(21), SLOT(22), SLOT(23), SLOT(24), SLOT(25), SLOT(26), SLOT(27), SLOT(28), SLOT(29),
    SLOT(30), SLOT(31), SLOT(32), SLOT(33), SLOT(34), SLOT(35), SLOT(36), SLOT(37), SLOT(38), SLOT(39),
    SLOT(40), SLOT(41), SLOT(42), SLOT(43), SLOT(44), SLOT(45), SLOT(46), SLOT(47), SLOT(48)
};
#undef SLOT
SRWLOCK recordsLock = SRWLOCK_INIT;
Record* records = nullptr;
constexpr unsigned RecordLimit = 128;
volatile LONG installedDraw = 0, installedD3D = 0, installedDevice = 0, installFailures = 0;
volatile LONG drawCalls = 0, callerMatches = 0, correctedQuads = 0, correctedX = 0, correctedY = 0;
volatile LONG retiredRecords = 0, retainedRecords = 0;
PVOID volatile drawObserver = nullptr;
HRESULT ObservedDraw(HRESULT result) noexcept {
    auto observer=reinterpret_cast<void (*)(int32_t)>(InterlockedCompareExchangePointer(&drawObserver,nullptr,nullptr));
    if(observer)observer(result);
    return result;
}
using QueryFn = HRESULT (WINAPI*)(void*, REFIID, void**);
using ReleaseFn = ULONG (WINAPI*)(void*);
using CreateDeviceFn = HRESULT (WINAPI*)(void*, REFCLSID, IDirectDrawSurface7*, IDirect3DDevice7**);
using GetDirect3DFn = HRESULT (WINAPI*)(void*, IDirect3D7**);
using DrawFn = HRESULT (WINAPI*)(void*, D3DPRIMITIVETYPE, DWORD, void*, DWORD, DWORD);

bool Accessible(const void* pointer, SIZE_T length, bool writable) noexcept {
    uintptr_t address = reinterpret_cast<uintptr_t>(pointer);
    if (!address || !length || address > UINTPTR_MAX - length) return false;
    uintptr_t end = address + length;
    while (address < end) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<void*>(address), &info, sizeof(info)) != sizeof(info) ||
            info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        DWORD p = info.Protect & 0xff;
        if (writable) {
            if (p != PAGE_READWRITE && p != PAGE_WRITECOPY && p != PAGE_EXECUTE_READWRITE && p != PAGE_EXECUTE_WRITECOPY) return false;
        } else if (p != PAGE_READONLY && p != PAGE_READWRITE && p != PAGE_WRITECOPY &&
            p != PAGE_EXECUTE_READ && p != PAGE_EXECUTE_READWRITE && p != PAGE_EXECUTE_WRITECOPY) return false;
        uintptr_t next = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        if (next <= address) return false;
        address = next;
    }
    return true;
}

bool SafeCopy(void* destination, const void* source, SIZE_T size) noexcept {
    if (!Accessible(source, size, false)) return false;
    __try { memcpy(destination, source, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

Record* Find(void* object) noexcept {
    AcquireSRWLockShared(&recordsLock);
    Record* found = nullptr;
    for (Record* r = records; r; r = r->next) {
        if (r == object) { found = r; break; }
    }
    ReleaseSRWLockShared(&recordsLock);
    return found;
}

HRESULT WINAPI QueryHook(void*, REFIID, void**);
ULONG WINAPI ReleaseHook(void*);
HRESULT WINAPI CreateDeviceHook(void*, REFCLSID, IDirectDrawSurface7*, IDirect3DDevice7**);
HRESULT WINAPI GetDirect3DHook(void*, IDirect3D7**);
HRESULT WINAPI DrawHook(void*, D3DPRIMITIVETYPE, DWORD, void*, DWORD, DWORD);

bool Install(void** output, Kind kind) noexcept {
    if (!Accessible(output, sizeof(void*), true)) return false;
    void* object = *output;
    if (auto existing = Find(object)) return existing->kind == kind;
    unsigned size = kind == Kind::Draw ? 30 : (kind == Kind::D3D ? 8 : (kind == Kind::Device ? 49 : 3));
    void** original = nullptr;
    if (!SafeCopy(&original, object, sizeof(original)) || !Accessible(original, size * sizeof(void*), false)) return false;
    AcquireSRWLockExclusive(&recordsLock);
    for (Record* r = records; r; r = r->next) {
        if (r->originalObject == object && r->kind == kind) {
            *output = r;
            ReleaseSRWLockExclusive(&recordsLock);
            return true;
        }
    }
    if (retainedRecords >= static_cast<LONG>(RecordLimit)) {
        ReleaseSRWLockExclusive(&recordsLock);
        return false;
    }
    Record* r = static_cast<Record*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Record)));
    if (!r) {
        if (r) HeapFree(GetProcessHeap(), 0, r);
        ReleaseSRWLockExclusive(&recordsLock);
        return false;
    }
    r->originalObject = object; r->kind = kind; r->table = r->entries;
    memcpy(r->entries, forwarding, size * sizeof(void*));
    r->entries[0] = reinterpret_cast<void*>(&QueryHook);
    r->entries[2] = reinterpret_cast<void*>(&ReleaseHook);
    if (kind == Kind::D3D) r->entries[4] = reinterpret_cast<void*>(&CreateDeviceHook);
    if (kind == Kind::Device) {
        r->entries[7] = reinterpret_cast<void*>(&GetDirect3DHook);
        r->entries[25] = reinterpret_cast<void*>(&DrawHook);
    }
    r->next = records; records = r;
    InterlockedIncrement(&retainedRecords);
    if (kind != Kind::Unknown) InterlockedIncrement(kind == Kind::Draw ? &installedDraw : (kind == Kind::D3D ? &installedD3D : &installedDevice));
    *output = r;
    ReleaseSRWLockExclusive(&recordsLock);
    return true;
}

HRESULT WINAPI QueryHook(void* object, REFIID iid, void** output) {
    Record* r = Find(object);
    if (!r) return E_UNEXPECTED;
    void** original = *static_cast<void***>(r->originalObject);
    HRESULT result = reinterpret_cast<QueryFn>(original[0])(r->originalObject, iid, output);
    void* target = nullptr;
    if (SUCCEEDED(result) && output && SafeCopy(&target, output, sizeof(target)) && target) {
        bool known = true; Kind kind = Kind::Draw;
        if (IsEqualGUID(iid, IID_IDirectDraw7)) kind = Kind::Draw;
        else if (IsEqualGUID(iid, IID_IDirect3D7)) kind = Kind::D3D;
        else if (IsEqualGUID(iid, IID_IDirect3DDevice7)) kind = Kind::Device;
        else if (IsEqualGUID(iid, IID_IUnknown)) kind = Kind::Unknown;
        else known = false;
        if (known && !Install(output, kind)) InterlockedIncrement(&installFailures);
    }
    return result;
}

ULONG WINAPI ReleaseHook(void* object) {
    Record* r = Find(object);
    if (!r) return 0;
    void** original = *static_cast<void***>(r->originalObject);
    ULONG result = reinterpret_cast<ReleaseFn>(original[2])(r->originalObject);
    if (!result) {
        // Facades contain no owned system reference and remain allocated. A
        // subsequent QI for the same original address can reuse the same facade.
        AcquireSRWLockExclusive(&recordsLock);
        if (!r->retired) { r->retired = true; InterlockedIncrement(&retiredRecords); }
        ReleaseSRWLockExclusive(&recordsLock);
    }
    return result;
}

HRESULT WINAPI CreateDeviceHook(void* object, REFCLSID clsid, IDirectDrawSurface7* surface, IDirect3DDevice7** output) {
    Record* r = Find(object);
    if (!r) return E_UNEXPECTED;
    void** original = *static_cast<void***>(r->originalObject);
    HRESULT result = reinterpret_cast<CreateDeviceFn>(original[4])(r->originalObject, clsid, surface, output);
    void* target = nullptr;
    if (SUCCEEDED(result) && output && SafeCopy(&target, output, sizeof(target)) && target &&
        !Install(reinterpret_cast<void**>(output), Kind::Device)) InterlockedIncrement(&installFailures);
    return result;
}

HRESULT WINAPI GetDirect3DHook(void* object, IDirect3D7** output) {
    Record* r = Find(object);
    if (!r) return E_UNEXPECTED;
    void** original = *static_cast<void***>(r->originalObject);
    HRESULT result = reinterpret_cast<GetDirect3DFn>(original[7])(r->originalObject, output);
    void* target = nullptr;
    if (SUCCEEDED(result) && output && SafeCopy(&target, output, sizeof(target)) && target &&
        !Install(reinterpret_cast<void**>(output), Kind::D3D)) InterlockedIncrement(&installFailures);
    return result;
}

bool ReadGeometryContext(SeamGeometryContext* context) noexcept {
    uintptr_t pointer = 0;
    short textureWidth = 0, textureHeight = 0;
    if (!SafeCopy(&context->screenWidth, reinterpret_cast<void*>(0x5e0198), sizeof(int)) ||
        !SafeCopy(&context->screenHeight, reinterpret_cast<void*>(0x5e019c), sizeof(int)) ||
        context->screenWidth < 640 || context->screenWidth > 1600 ||
        context->screenHeight < 480 || context->screenHeight > 1200 ||
        3 * context->screenWidth != 4 * context->screenHeight ||
        !SafeCopy(&pointer, reinterpret_cast<void*>(0x73d4f4), sizeof(pointer)) || !pointer ||
        pointer > UINTPTR_MAX - 0xbc ||
        !SafeCopy(&textureWidth, reinterpret_cast<void*>(pointer + 0xb8), sizeof(textureWidth)) ||
        !SafeCopy(&textureHeight, reinterpret_cast<void*>(pointer + 0xba), sizeof(textureHeight))) return false;
    context->textureWidth = textureWidth; context->textureHeight = textureHeight;
    return true;
}

// _ReturnAddress must be evaluated in this COM entry wrapper itself.
__declspec(noinline) HRESULT WINAPI DrawHook(void* object, D3DPRIMITIVETYPE primitive, DWORD fvf,
    void* vertices, DWORD count, DWORD flags) {
    void* caller = _ReturnAddress();
    Record* r = Find(object);
    if (!r) return E_UNEXPECTED;
    InterlockedIncrement(&drawCalls);
    SeamVertex copied[4], corrected[4];
    SeamGeometryContext context{};
    if (reinterpret_cast<uintptr_t>(caller) == 0x48044a && primitive == D3DPT_TRIANGLEFAN && fvf == 0x144 && count == 4) {
        InterlockedIncrement(&callerMatches);
        if (ReadGeometryContext(&context) && SafeCopy(copied, vertices, sizeof(copied)) &&
            CorrectD3DSeamQuad(primitive, fvf, copied, count, context, corrected)) {
            InterlockedIncrement(&correctedQuads);
            if (corrected[1].x > copied[1].x) InterlockedIncrement(&correctedX);
            if (corrected[3].y > copied[3].y) InterlockedIncrement(&correctedY);
            void** original = *static_cast<void***>(r->originalObject);
            return ObservedDraw(reinterpret_cast<DrawFn>(original[25])(r->originalObject, primitive, fvf, corrected, count, flags));
        }
    }
    void** original = *static_cast<void***>(r->originalObject);
    return ObservedDraw(reinterpret_cast<DrawFn>(original[25])(r->originalObject, primitive, fvf, vertices, count, flags));
}
}

bool InstallD3DSeamFix(void** ddraw7) noexcept {
    // COM objects can outlive a proxy's ordinary module reference. Pin before
    // exposing any facade address, matching the facades' process lifetime.
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&InstallD3DSeamFix), &module) || !Install(ddraw7, Kind::Draw)) {
        InterlockedIncrement(&installFailures);
        return false;
    }
    return true;
}

void SetD3DDrawObserver(void (*observer)(int32_t)) noexcept {
    InterlockedExchangePointer(&drawObserver,reinterpret_cast<void*>(observer));
}

D3DSeamFixStatus GetD3DSeamFixStatus() noexcept {
    return { InterlockedCompareExchange(&installedDraw, 0, 0), InterlockedCompareExchange(&installedD3D, 0, 0),
        InterlockedCompareExchange(&installedDevice, 0, 0), InterlockedCompareExchange(&installFailures, 0, 0),
        InterlockedCompareExchange(&drawCalls, 0, 0), InterlockedCompareExchange(&callerMatches, 0, 0),
        InterlockedCompareExchange(&correctedQuads, 0, 0), InterlockedCompareExchange(&correctedX, 0, 0),
        InterlockedCompareExchange(&correctedY, 0, 0), InterlockedCompareExchange(&retiredRecords, 0, 0),
        InterlockedCompareExchange(&retainedRecords, 0, 0) };
}
