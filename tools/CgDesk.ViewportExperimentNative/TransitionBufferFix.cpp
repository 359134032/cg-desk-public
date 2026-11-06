#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "TransitionBufferFix.h"
#include "TransitionBufferProfile.h"

static_assert(sizeof(void*)==4,"Verified x86 profile only");
namespace {
uintptr_t imageBase=0x400000;
void* regions[3]{};
float* arrays[3]{};
DWORD replacements[ARRAYSIZE(TransitionPatchSpecs)]{};
struct Page {uintptr_t address;DWORD original;bool changed;};
Page pages[3]{};
unsigned pageCount=0;
bool applied=false,healthy=true;
#ifdef TRANSITION_BUFFER_TESTING
int failAfter=-1;
#endif
uintptr_t Address(uintptr_t original) noexcept {return imageBase+original-0x400000;}
void Free() noexcept {
    for(unsigned i=0;i<3;i++){if(regions[i])VirtualFree(regions[i],0,MEM_RELEASE);regions[i]=nullptr;arrays[i]=nullptr;}
}
bool RestoreProtection() noexcept {
    bool ok=true;
    for(unsigned i=0;i<pageCount;i++)if(pages[i].changed){
        DWORD ignored=0;
        if(VirtualProtect(reinterpret_cast<void*>(pages[i].address),4096,pages[i].original,&ignored))pages[i].changed=false;
        else ok=false;
    }
    return ok;
}
bool Writable() noexcept {
    for(unsigned i=0;i<pageCount;i++){
        DWORD ignored=0;
        if(!VirtualProtect(reinterpret_cast<void*>(pages[i].address),4096,PAGE_EXECUTE_READWRITE,&ignored))return false;
        pages[i].changed=true;
    }
    return true;
}
bool RestoreCode() noexcept {
    if(!Writable()){RestoreProtection();return false;}
    bool ok=true;
    __try {
        for(const auto& p:TransitionPatchSpecs)
            memcpy(reinterpret_cast<void*>(Address(p.operand)),p.bytes+(p.operand-p.address),4);
    } __except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    if(!FlushInstructionCache(GetCurrentProcess(),nullptr,0))ok=false;
    if(!RestoreProtection())ok=false;
    return ok;
}
}
bool ValidateTransitionBufferFix() noexcept {
    if(!healthy)return false;
    __try {
        for(unsigned i=0;i<ARRAYSIZE(TransitionPatchSpecs);i++){
            const auto& p=TransitionPatchSpecs[i];
            MEMORY_BASIC_INFORMATION m{};
            if(VirtualQuery(reinterpret_cast<void*>(Address(p.address)),&m,sizeof(m))!=sizeof(m)||
                m.State!=MEM_COMMIT||m.Type!=MEM_IMAGE||reinterpret_cast<uintptr_t>(m.AllocationBase)!=imageBase||
                (m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
            BYTE expected[15];memcpy(expected,p.bytes,p.count);
            if(applied)memcpy(expected+p.operand-p.address,&replacements[i],4);
            if(memcmp(reinterpret_cast<void*>(Address(p.address)),expected,p.count))return false;
        }
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
bool ApplyTransitionBufferFix() noexcept {
    if(applied)return ValidateTransitionBufferFix();
    if(!ValidateTransitionBufferFix())return false;
    SYSTEM_INFO system{};GetSystemInfo(&system);if(system.dwPageSize!=4096)return false;
    // Each former 1024-float array receives its own two committed pages and a
    // no-access page. Front padding and >2000 rows include the original -2,
    // height, height+1 and vector-lane accesses at the maximum height of 1200.
    for(unsigned i=0;i<3;i++){
        regions[i]=VirtualAlloc(nullptr,12288,MEM_RESERVE,PAGE_NOACCESS);
        if(!regions[i]||!VirtualAlloc(regions[i],8192,MEM_COMMIT,PAGE_READWRITE)){Free();return false;}
        arrays[i]=reinterpret_cast<float*>(static_cast<BYTE*>(regions[i])+16);
    }
    pageCount=0;
    for(unsigned i=0;i<ARRAYSIZE(TransitionPatchSpecs);i++){
        const auto& p=TransitionPatchSpecs[i];
        replacements[i]=static_cast<DWORD>(reinterpret_cast<uintptr_t>(arrays[p.array])+p.offset);
        uintptr_t page=Address(p.address)&~static_cast<uintptr_t>(4095);
        bool found=false;for(unsigned n=0;n<pageCount;n++)if(pages[n].address==page)found=true;
        if(!found){
            if(pageCount==ARRAYSIZE(pages)){Free();return false;}
            MEMORY_BASIC_INFORMATION m{};VirtualQuery(reinterpret_cast<void*>(page),&m,sizeof(m));
            pages[pageCount++]={page,m.Protect,false};
        }
    }
    HMODULE self=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&ApplyTransitionBufferFix),&self)){Free();return false;}
    bool wrote=false,ok=false;
    if(Writable()){
        __try {
            for(unsigned i=0;i<ARRAYSIZE(TransitionPatchSpecs);i++){
                memcpy(reinterpret_cast<void*>(Address(TransitionPatchSpecs[i].operand)),&replacements[i],4);wrote=true;
#ifdef TRANSITION_BUFFER_TESTING
                if(failAfter>=0&&static_cast<int>(i+1)>=failAfter)__leave;
#endif
            }
            ok=FlushInstructionCache(GetCurrentProcess(),nullptr,0)!=FALSE;
        } __except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    }
    if(!RestoreProtection())ok=false;
    if(!ok){
        bool restored=wrote?RestoreCode():RestoreProtection();
        if(restored)Free();else healthy=false;
        return false;
    }
    applied=true;return true;
}
bool RollbackTransitionBufferFix() noexcept {
    if(!healthy)return false;
    if(!applied)return true;
    if(!ValidateTransitionBufferFix()||!RestoreCode()){healthy=false;return false;}
    applied=false;Free();return true;
}
bool TransitionBufferFixHealthy() noexcept {return healthy;}
bool TransitionBufferFixApplied() noexcept {return applied;}
#ifdef TRANSITION_BUFFER_TESTING
void SetTransitionTestBase(void* base) noexcept {imageBase=reinterpret_cast<uintptr_t>(base);}
void SetTransitionTestFailure(int count) noexcept {failAfter=count;}
float* GetTransitionTestBuffer(unsigned array) noexcept {return array<3?arrays[array]:nullptr;}
#endif
