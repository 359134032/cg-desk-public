#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>
#include "GraphicsDiagnostics.h"
#include "MapCacheFix.h"
#include "D3DSeamFix.h"
#include "TransitionBufferFix.h"

namespace {
wchar_t outputPath[32768]{};
volatile LONG started=0,lastDraw=0;
volatile LONG lockCalls=0,lockFailures=0,lastLock=0;
bool healthy=true,lockHooks=false;
void __cdecl RecordLock(LONG result) noexcept {
    InterlockedIncrement(&lockCalls);
    InterlockedExchange(&lastLock,result);
    if(result<0)InterlockedIncrement(&lockFailures);
}
__declspec(naked) HRESULT WINAPI ObserveLock(void*,void*,void*,DWORD,HANDLE) {
    __asm {
        push ebp
        mov ebp,esp
        push dword ptr [ebp+24]
        push dword ptr [ebp+20]
        push dword ptr [ebp+16]
        push dword ptr [ebp+12]
        push dword ptr [ebp+8]
        mov eax,[ebp+8]
        mov eax,[eax]
        call dword ptr [eax+64h]
        pushfd
        pushad
        push eax
        call RecordLock
        add esp,4
        popad
        popfd
        mov esp,ebp
        pop ebp
        test eax,eax
        ret 20
    }
}
bool InstallLockObservers() noexcept {
    // The hash-bound callers have CALL [EAX+64h]; TEST EAX,EAX. The observer
    // performs the same stdcall once and reproduces TEST flags before RET.
    const uintptr_t sites[]={0x47bb30,0x47cd33};
    const BYTE original[]={0xff,0x50,0x64,0x85,0xc0};
    DWORD protections[2]{};unsigned changed=0;bool wrote=false,success=false;
    __try {
        const BYTE guards[2][11]={{0xff,0x50,0x64,0x85,0xc0,0x0f,0x85,0x4d,0x11,0,0},
            {0xff,0x50,0x64,0x85,0xc0,0x0f,0x85,0x83,0x0d,0,0}};
        for(unsigned i=0;i<2;i++)if(memcmp(reinterpret_cast<void*>(sites[i]),guards[i],11))__leave;
        for(unsigned i=0;i<2;i++){
            if(!VirtualProtect(reinterpret_cast<void*>(sites[i]),5,PAGE_EXECUTE_READWRITE,&protections[i]))__leave;
            ++changed;
        }
        if(changed!=2)__leave;
        wrote=true;
        for(uintptr_t site:sites){
            BYTE call[5]={0xe8};DWORD relative=static_cast<DWORD>(reinterpret_cast<uintptr_t>(&ObserveLock)-(site+5));
            memcpy(call+1,&relative,4);memcpy(reinterpret_cast<void*>(site),call,5);
        }
        success=FlushInstructionCache(GetCurrentProcess(),nullptr,0)!=FALSE;
    } __except(EXCEPTION_EXECUTE_HANDLER){success=false;}
    if(!success&&wrote){
        __try {for(uintptr_t site:sites)memcpy(reinterpret_cast<void*>(site),original,5);}
        __except(EXCEPTION_EXECUTE_HANDLER){healthy=false;}
        if(!FlushInstructionCache(GetCurrentProcess(),nullptr,0))healthy=false;
    }
    for(unsigned i=0;i<changed;i++){
        DWORD ignored=0;if(!VirtualProtect(reinterpret_cast<void*>(sites[i]),5,protections[i],&ignored))healthy=false;
    }
    return success&&healthy;
}
struct Snapshot {
    DWORD width,height,windowWidth,windowHeight,map0,map1,mapWidth,mapHeight;
    DWORD waitScan,waitMap,restorePending,inputReset,context,contextWidth,contextHeight,workSurface,screenshotSurface;
    short bounds[6],sheetWidth,sheetHeight;
    LONG windowLeft,windowTop;
    DWORD childWindow;
    DWORD transition[9];
    MapCacheDiagnostics cache;
    D3DSeamFixStatus drawing;
};
bool Capture(Snapshot* value) noexcept {
    __try {
        value->width=*reinterpret_cast<volatile DWORD*>(0x5e0198);
        value->height=*reinterpret_cast<volatile DWORD*>(0x5e019c);
        value->windowWidth=*reinterpret_cast<volatile DWORD*>(0xa527e0);
        value->windowHeight=*reinterpret_cast<volatile DWORD*>(0xa527e4);
        value->map0=*reinterpret_cast<volatile DWORD*>(0xaa8790);
        value->map1=*reinterpret_cast<volatile DWORD*>(0xaa8794);
        value->mapWidth=*reinterpret_cast<volatile DWORD*>(0xaa87a4);
        value->mapHeight=*reinterpret_cast<volatile DWORD*>(0xaa87a8);
        value->waitScan=*reinterpret_cast<volatile DWORD*>(0xaa8810);
        value->waitMap=*reinterpret_cast<volatile DWORD*>(0xaa8834);
        value->restorePending=*reinterpret_cast<volatile DWORD*>(0x73ed64);
        value->inputReset=*reinterpret_cast<volatile DWORD*>(0x10cf18c);
        value->context=*reinterpret_cast<volatile DWORD*>(0x73d4f4);
        value->workSurface=*reinterpret_cast<volatile DWORD*>(0x73ed58);
        value->screenshotSurface=*reinterpret_cast<volatile DWORD*>(0x11131b8);
        value->transition[0]=TransitionBufferFixApplied()?1:0;
        const uintptr_t transitionFields[]={0x621554,0x621558,0x10c9860,0x854a28,0x10c9854,0x10c9858};
        for(unsigned i=0;i<6;i++)value->transition[i+1]=*reinterpret_cast<volatile DWORD*>(transitionFields[i]);
        value->transition[7]=*reinterpret_cast<volatile BYTE*>(0xa55c4a);
        value->transition[8]=*reinterpret_cast<volatile short*>(0x1113190);
        HWND window=*reinterpret_cast<volatile HWND*>(0xa526ac);
        RECT rectangle{};
        if(IsWindow(window)&&GetWindowRect(window,&rectangle)){
            value->windowLeft=rectangle.left;value->windowTop=rectangle.top;
            value->childWindow=(GetWindowLongPtrW(window,GWL_STYLE)&WS_CHILD)?1:0;
        }
        const uintptr_t fields[]={0xaa87ac,0xaa87b0,0xaa87b4,0xaa87b8,0xaa87bc,0xaa87c0};
        for(unsigned i=0;i<6;i++)value->bounds[i]=*reinterpret_cast<volatile short*>(fields[i]);
        if(value->context){
            value->contextWidth=*reinterpret_cast<volatile DWORD*>(value->context+0x94);
            value->contextHeight=*reinterpret_cast<volatile DWORD*>(value->context+0x98);
            value->sheetWidth=*reinterpret_cast<volatile short*>(value->context+0xb8);
            value->sheetHeight=*reinterpret_cast<volatile short*>(value->context+0xba);
        }
        value->cache=GetMapCacheDiagnostics();value->drawing=GetD3DSeamFixStatus();
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
DWORD WINAPI Run(void*) {
    // Observe fixed graphics state only. Do not call game functions/getters or
    // COM methods from this thread, and do not read account or network buffers.
    for(;;){
        Snapshot s{};
        if(Capture(&s)){
            char line[2048];
            int count=sprintf_s(line,
                "{\"tick\":%llu,\"pid\":%lu,\"screen\":[%lu,%lu],\"window\":[%lu,%lu],"
                "\"map\":[%lu,%lu,%lu,%lu],\"bounds\":[%d,%d,%d,%d,%d,%d],"
                "\"wait\":[%lu,%lu],\"restore\":[%lu,%lu],\"context\":%lu,\"viewport\":[%lu,%lu],"
                "\"sheet\":[%d,%d],\"surfaces\":[%lu,%lu],\"cache\":[%ld,%lu,%ld,%ld,%ld,%lu],"
                "\"draw\":[%ld,%ld,%ld,%ld,%ld],\"lock\":[%d,%ld,%ld,%ld],\"placement\":[%ld,%ld,%lu],"
                "\"transition\":[%lu,%lu,%lu,%lu,%lu,%lu,%lu,%lu,%lu]}\n",
                GetTickCount64(),GetCurrentProcessId(),s.width,s.height,s.windowWidth,s.windowHeight,
                s.map0,s.map1,s.mapWidth,s.mapHeight,s.bounds[0],s.bounds[1],s.bounds[2],s.bounds[3],s.bounds[4],s.bounds[5],
                s.waitScan,s.waitMap,s.restorePending,s.inputReset,s.context,s.contextWidth,s.contextHeight,s.sheetWidth,s.sheetHeight,
                s.workSurface,s.screenshotSurface,s.cache.Applied,s.cache.LastError,s.cache.BoundsCalls,s.cache.LegacyScanCalls,
                s.cache.ThreadOrReentryRejected,s.cache.OwnerThread,s.drawing.installedDevice,s.drawing.drawCalls,
                s.drawing.installFailures,s.drawing.retainedRecords,InterlockedCompareExchange(&lastDraw,0,0),lockHooks?1:0,
                InterlockedCompareExchange(&lockCalls,0,0),InterlockedCompareExchange(&lockFailures,0,0),InterlockedCompareExchange(&lastLock,0,0),
                s.windowLeft,s.windowTop,s.childWindow,
                s.transition[0],s.transition[1],s.transition[2],s.transition[3],s.transition[4],
                s.transition[5],s.transition[6],s.transition[7],s.transition[8]);
            if(count>0){
                HANDLE file=CreateFileW(outputPath,FILE_APPEND_DATA,FILE_SHARE_READ,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
                if(file!=INVALID_HANDLE_VALUE){
                    LARGE_INTEGER size{};
                    if(GetFileSizeEx(file,&size)&&size.QuadPart<=1024*1024){DWORD written=0;WriteFile(file,line,static_cast<DWORD>(count),&written,nullptr);}
                    CloseHandle(file);
                }
            }
        }
        Sleep(1000);
    }
}
}
void ObserveGraphicsDraw(int32_t result) noexcept {InterlockedExchange(&lastDraw,result);}
void StartGraphicsDiagnostics(const wchar_t* path) noexcept {
    if(!path||wcslen(path)<4||path[1]!=L':'||path[2]!=L'\\')return;
    const wchar_t* leaf=wcsrchr(path,L'\\');
    if(!leaf||_wcsicmp(leaf+1,L"cgdesk-graphics-state.jsonl"))return;
    if(InterlockedCompareExchange(&started,1,0))return;
    if(wcscpy_s(outputPath,path)){InterlockedExchange(&started,0);return;}
    HMODULE self=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&StartGraphicsDiagnostics),&self)){InterlockedExchange(&started,0);return;}
    lockHooks=InstallLockObservers();
    if(!healthy)return;
    HANDLE thread=CreateThread(nullptr,0,&Run,nullptr,0,nullptr);
    if(!thread){InterlockedExchange(&started,0);return;}
    SetD3DDrawObserver(&ObserveGraphicsDraw);
    CloseHandle(thread);
}
bool GraphicsDiagnosticsHealthy() noexcept {return healthy;}
#ifdef GRAPHICS_DIAGNOSTICS_TESTING
void* GraphicsLockObserverAddress() noexcept {return reinterpret_cast<void*>(&ObserveLock);}
#endif
