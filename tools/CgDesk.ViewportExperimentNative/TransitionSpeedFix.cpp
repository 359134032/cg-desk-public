#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include "TransitionSpeedFix.h"
static_assert(sizeof(void*)==4,"Verified x86 profile only");
namespace {
uintptr_t imageBase=0x400000;
using Effect=int(__cdecl*)(int,int,float);
Effect originalExit=reinterpret_cast<Effect>(0x5588f0),originalEnter=reinterpret_cast<Effect>(0x559330);
int screenWidth=800,screenHeight=600;
float positiveCap=100,negativeCap=-100,heightThreshold=600;
volatile LONG calls=0;
bool applied=false,healthy=true;
struct Patch{uintptr_t address;unsigned count,offset,kind;BYTE original[8],replacement[8];};
Patch patches[]={
    {0x558312,5,1,0,{0xe8,0xd9,0x05,0,0}},
    {0x55832c,5,1,0,{0xe8,0xbf,0x05,0,0}},
    {0x558346,5,1,0,{0xe8,0xa5,0x05,0,0}},
    {0x5583fc,5,1,1,{0xe8,0x2f,0x0f,0,0}},
    {0x558416,5,1,1,{0xe8,0x15,0x0f,0,0}},
    {0x558430,5,1,1,{0xe8,0xfb,0x0e,0,0}},
    {0x558fcc,8,4,2,{0xf3,0x0f,0x10,0x25,0x5c,0xef,0x5a,0}},
    {0x559cbf,8,4,3,{0xf3,0x0f,0x10,0x25,0x98,0x1e,0x5b,0}},
    {0x559cca,8,4,2,{0xf3,0x0f,0x10,0x1d,0x5c,0xef,0x5a,0}},
    {0x55905c,7,3,4,{0x0f,0x2f,0x05,0x48,0xa8,0x5b,0}}
};
struct Page{uintptr_t address;DWORD original;bool changed;};Page pages[2]{};
#ifdef TRANSITION_SPEED_TESTING
int failAfter=-1;
bool failPageRecovery=false;
#endif
uintptr_t Address(uintptr_t value) noexcept{return imageBase+value-0x400000;}
int Invoke(Effect original,int mode,int step,float acceleration) noexcept {
    int adjusted=step;float scaled=acceleration;
    NormalizeTransitionStep(mode,step,acceleration,screenWidth,screenHeight,&adjusted,&scaled);
    InterlockedIncrement(&calls);
    return original(mode,adjusted,scaled);
}
int __cdecl ExitHook(int mode,int step,float acceleration) noexcept{return Invoke(originalExit,mode,step,acceleration);}
int __cdecl EnterHook(int mode,int step,float acceleration) noexcept{return Invoke(originalEnter,mode,step,acceleration);}
bool RestoreProtection() noexcept {
    bool ok=true;for(auto& p:pages)if(p.changed){DWORD ignored=0;
#ifdef TRANSITION_SPEED_TESTING
        if(failPageRecovery&&p.address==pages[0].address){ok=false;continue;}
#endif
        if(VirtualProtect(reinterpret_cast<void*>(p.address),4096,p.original,&ignored))p.changed=false;else ok=false;}
    return ok;
}
bool Writable() noexcept {
    for(auto& p:pages){DWORD ignored=0;
#ifdef TRANSITION_SPEED_TESTING
        if(failPageRecovery&&p.address==pages[1].address)return false;
#endif
        if(!VirtualProtect(reinterpret_cast<void*>(p.address),4096,PAGE_EXECUTE_READWRITE,&ignored))return false;p.changed=true;}
    return true;
}
bool RestoreCode() noexcept {
    if(!Writable()){if(!RestoreProtection())healthy=false;return false;}
    bool ok=true;
    __try{for(const auto& p:patches)memcpy(reinterpret_cast<void*>(Address(p.address)),p.original,p.count);}
    __except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    if(!FlushInstructionCache(GetCurrentProcess(),nullptr,0))ok=false;
    if(!RestoreProtection()){healthy=false;ok=false;}return ok;
}
}
bool NormalizeTransitionStep(int mode,int raw,float acceleration,int width,int height,int* step,float* scaled) noexcept {
    if(!step||!scaled||mode<6||mode>8||width<=800||width>1600||height<600||height>1200||3*width!=4*height||
        !isfinite(acceleration)||acceleration<0)return false;
    const int low=static_cast<signed char>(raw&255);if(low<0)return false;
    const float ratio=width/800.0f;
    const int original=low?low:(mode==8?16:4);
    int next=static_cast<int>(floorf(original*ratio+0.5f));if(next>127)next=127;if(next<1)next=1;
    // The native inverse strips use exact zero-crossing comparisons. Decimal
    // float increments such as 2*1.28 can leave a negative residual and reverse
    // again instead of completing. Quantize to an exact binary fraction.
    const double result=floor((acceleration==0?1.0:static_cast<double>(acceleration))*(width/800.0)*16.0+0.5)/16.0;
    const float value=static_cast<float>(result);if(!isfinite(value)||value<=0)return false;
    *step=(raw&~255)|next;*scaled=value;
    return true;
}
bool ValidateTransitionSpeedFix() noexcept {
    if(!healthy)return false;
    __try{for(const auto& p:patches){MEMORY_BASIC_INFORMATION m{};
        if(VirtualQuery(reinterpret_cast<void*>(Address(p.address)),&m,sizeof(m))!=sizeof(m)||m.State!=MEM_COMMIT||m.Type!=MEM_IMAGE||
            reinterpret_cast<uintptr_t>(m.AllocationBase)!=imageBase||(m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
        if(memcmp(reinterpret_cast<void*>(Address(p.address)),applied?p.replacement:p.original,p.count))return false;}
        return true;
    }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
bool ApplyTransitionSpeedFix() noexcept {
    if(applied)return ValidateTransitionSpeedFix();if(!ValidateTransitionSpeedFix())return false;
    __try{screenWidth=*reinterpret_cast<int*>(Address(0x5e0198));screenHeight=*reinterpret_cast<int*>(Address(0x5e019c));}
    __except(EXCEPTION_EXECUTE_HANDLER){return false;}
    if(screenWidth<=800||screenWidth>1600||screenHeight<600||screenHeight>1200||3*screenWidth!=4*screenHeight)return false;
    positiveCap=screenWidth/8.0f;negativeCap=-positiveCap;heightThreshold=static_cast<float>(screenHeight);
    for(unsigned i=0;i<2;i++){MEMORY_BASIC_INFORMATION m{};uintptr_t address=Address(0x558000+i*4096);
        if(VirtualQuery(reinterpret_cast<void*>(address),&m,sizeof(m))!=sizeof(m))return false;pages[i]={address,m.Protect,false};}
    for(auto& p:patches){memcpy(p.replacement,p.original,p.count);DWORD value=0;
        if(p.kind<=1){const auto target=p.kind?&EnterHook:&ExitHook;value=static_cast<DWORD>(reinterpret_cast<uintptr_t>(target)-(Address(p.address)+5));}
        else value=reinterpret_cast<DWORD>(p.kind==2?&positiveCap:(p.kind==3?&negativeCap:&heightThreshold));
        memcpy(p.replacement+p.offset,&value,4);
    }
    HMODULE self=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&ApplyTransitionSpeedFix),&self))return false;
    bool wrote=false,ok=false;
    if(Writable()){
        __try{
#ifdef TRANSITION_SPEED_TESTING
            unsigned i=0;
#endif
            for(const auto& p:patches){memcpy(reinterpret_cast<void*>(Address(p.address)),p.replacement,p.count);wrote=true;
#ifdef TRANSITION_SPEED_TESTING
                if(failAfter>=0&&++i>=static_cast<unsigned>(failAfter))__leave;
#endif
            }
            ok=FlushInstructionCache(GetCurrentProcess(),nullptr,0)!=FALSE;
        }__except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    }
    if(!RestoreProtection()){healthy=false;ok=false;}
    if(!ok){if(wrote&&!RestoreCode())healthy=false;return false;}
    applied=true;return true;
}
bool RollbackTransitionSpeedFix() noexcept {
    if(!healthy)return false;if(!applied)return true;if(calls)return false;
    if(!ValidateTransitionSpeedFix()||!RestoreCode()){healthy=false;return false;}applied=false;return true;
}
bool TransitionSpeedFixHealthy() noexcept{return healthy;}
LONG TransitionSpeedFixCalls() noexcept{return InterlockedCompareExchange(&calls,0,0);}
#ifdef TRANSITION_SPEED_TESTING
void SetTransitionSpeedTestBase(void* base) noexcept{imageBase=reinterpret_cast<uintptr_t>(base);}
void SetTransitionSpeedTestFailure(int value) noexcept{failAfter=value;}
void SetTransitionSpeedTestProtectionFailure(bool value) noexcept{failPageRecovery=value;}
void SetTransitionSpeedTestCallbacks(Effect exit,Effect enter) noexcept{originalExit=exit;originalEnter=enter;}
int InvokeTransitionSpeedTest(bool entering,int mode,int step,float acceleration) noexcept{return Invoke(entering?originalEnter:originalExit,mode,step,acceleration);}
#endif
