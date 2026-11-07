#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include "BattleLayoutFix.h"
static_assert(sizeof(void*)==4,"Verified x86 profile only");
namespace {
constexpr DWORD logicalWidth=800,logicalHeight=600;
uintptr_t imageBase=0x400000;
using Tail=void(__cdecl*)();
Tail originalTail=reinterpret_cast<Tail>(0x416b20);
using Update=int(__cdecl*)();
Update originalUpdate=reinterpret_cast<Update>(0x439310);
struct Patch{uintptr_t address;unsigned count;BYTE original[8],replacement[8];};
Patch patches[]={
    {0x464277,8,{0x66,0x0f,0x6e,0x15,0x98,0x01,0x5e,0}},
    {0x464282,8,{0x66,0x0f,0x6e,0x1d,0x9c,0x01,0x5e,0}},
    {0x4643d0,5,{0xe9,0x4b,0x27,0xfb,0xff}},
    {0x46a3bc,5,{0xe8,0x4f,0xef,0xfc,0xff}}
};
bool applied=false,healthy=true;
volatile LONG calls=0,entryCalls=0;
struct Page{uintptr_t address;DWORD original;bool writable;};Page pages[2]{};
#ifdef BATTLE_LAYOUT_TESTING
int failAfter=-1;
#endif
uintptr_t Address(uintptr_t value) noexcept{return imageBase+value-0x400000;}
int __cdecl InitialEntryUpdate() noexcept {
    const DWORD error=GetLastError();
    __try {
        const int width=*reinterpret_cast<volatile int*>(Address(0x5e0198));
        const int height=*reinterpret_cast<volatile int*>(Address(0x5e019c));
        for(unsigned i=0;i<20;i++) {
            const uintptr_t actor=*reinterpret_cast<volatile uintptr_t*>(Address(0x68dee0+i*4));
            if(!actor||*reinterpret_cast<volatile uintptr_t*>(actor+8)!=Address(0x439410)||
                *reinterpret_cast<volatile int*>(actor+0x2c)!=2)continue;
            const uintptr_t aux=*reinterpret_cast<volatile uintptr_t*>(actor+0xc);
            if(!aux||(*reinterpret_cast<volatile BYTE*>(aux+0xfdc)&1))continue;
            float x=*reinterpret_cast<volatile float*>(actor+0x40),y=*reinterpret_cast<volatile float*>(actor+0x44);
            if(TrimBattleInitialPosition(&x,&y,*reinterpret_cast<volatile int*>(aux),
                *reinterpret_cast<volatile int*>(aux+4),width,height)) {
                *reinterpret_cast<volatile float*>(actor+0x40)=x;
                *reinterpret_cast<volatile float*>(actor+0x44)=y;
            }
        }
        InterlockedIncrement(&entryCalls);
    } __except(EXCEPTION_EXECUTE_HANDLER){healthy=false;}
    SetLastError(error);
    return originalUpdate();
}
void __cdecl LayoutTail() noexcept {
    // This replaces only the original final tail-jump. The native initializer
    // still runs once, using separate dimensions and its original rounding.
    // Recenter all four absolute endpoints before any consumer creates actors.
    __try{
        const int width=*reinterpret_cast<volatile int*>(Address(0x5e0198));
        const int height=*reinterpret_cast<volatile int*>(Address(0x5e019c));
        CenterBattleLayout(reinterpret_cast<int*>(Address(0x68e340)),22,width,height);
        InterlockedIncrement(&calls);
    }__except(EXCEPTION_EXECUTE_HANDLER){healthy=false;}
    originalTail();
}
bool RestoreProtection() noexcept {
    bool ok=true;
    for(auto& p:pages)if(p.writable){DWORD ignored=0;
        if(VirtualProtect(reinterpret_cast<void*>(p.address),4096,p.original,&ignored))p.writable=false;else ok=false;}
    return ok;
}
bool MakeWritable() noexcept {
    for(auto& p:pages){DWORD old=0;
        if(!VirtualProtect(reinterpret_cast<void*>(p.address),4096,PAGE_EXECUTE_READWRITE,&old))return false;p.writable=true;}
    return true;
}
bool RestoreCode() noexcept {
    if(!MakeWritable()){RestoreProtection();return false;}
    bool ok=true;
    __try{for(const auto& p:patches)memcpy(reinterpret_cast<void*>(Address(p.address)),p.original,p.count);}
    __except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    if(!FlushInstructionCache(GetCurrentProcess(),nullptr,0))ok=false;
    if(!RestoreProtection()){healthy=false;ok=false;}
    return ok;
}
}
void CenterBattleLayout(int* data,unsigned rows,int width,int height) noexcept {
    if(!data||rows!=22||width<800||height<600||width>1600||height>1200||3*width!=4*height)return;
    const int x=(width-800)/2,y=(height-600)/2;
    for(unsigned i=0;i<rows;i++){data[i*4]+=x;data[i*4+1]+=y;data[i*4+2]+=x;data[i*4+3]+=y;}
}
bool TrimBattleInitialPosition(float* x,float* y,int tx,int ty,int width,int height) noexcept {
    if(!x||!y||!isfinite(*x)||!isfinite(*y)||width<=800||width>1600||height<600||height>1200||3*width!=4*height)return false;
    const int left=(width-800)/2,top=(height-600)/2,right=left+800,bottom=top+600;
    if(tx<left||tx>right||ty<top||ty>bottom||(*x>=left&&*x<=right&&*y>=top&&*y<=bottom))return false;
    const double dx=static_cast<double>(*x)-tx,dy=static_cast<double>(*y)-ty;
    double visible=1.0;
    const double vx=dx>0?(right-tx)/dx:(dx<0?(left-tx)/dx:1.0);
    const double vy=dy>0?(bottom-ty)/dy:(dy<0?(top-ty)/dy:1.0);
    if(vx<visible)visible=vx;if(vy<visible)visible=vy;
    if(visible<0||visible>=1)return false;
    *x=static_cast<float>(tx+dx*visible);*y=static_cast<float>(ty+dy*visible);return true;
}
bool ValidateBattleLayoutFix() noexcept {
    if(!healthy)return false;
    __try{
        for(const auto& p:patches){
            MEMORY_BASIC_INFORMATION m{};
            if(VirtualQuery(reinterpret_cast<void*>(Address(p.address)),&m,sizeof(m))!=sizeof(m)||m.State!=MEM_COMMIT||m.Type!=MEM_IMAGE||
                reinterpret_cast<uintptr_t>(m.AllocationBase)!=imageBase||(m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
            if(memcmp(reinterpret_cast<void*>(Address(p.address)),applied?p.replacement:p.original,p.count))return false;
        }
        // The original table loop has 22 records and four absolute int fields.
        const BYTE loopEnd[]={0x83,0xc1,0x10,0x81,0xf9,0x60,0x01,0,0,0x7c,0x81};
        if(memcmp(reinterpret_cast<void*>(Address(0x4643c5)),loopEnd,sizeof(loopEnd)))return false;
        const BYTE initialPhase[]={0xc7,0x42,0x2c,2,0,0,0,0xbe,1,0,0,0};
        if(memcmp(reinterpret_cast<void*>(Address(0x46a402)),initialPhase,sizeof(initialPhase)))return false;
        return true;
    }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
bool ApplyBattleLayoutFix() noexcept {
    if(applied)return ValidateBattleLayoutFix();
    if(!ValidateBattleLayoutFix())return false;
    for(unsigned i=0;i<2;i++){MEMORY_BASIC_INFORMATION m{};const uintptr_t address=Address(i?0x46a000:0x464000);
        if(VirtualQuery(reinterpret_cast<void*>(address),&m,sizeof(m))!=sizeof(m))return false;pages[i]={address,m.Protect,false};}
    for(auto& p:patches)memcpy(p.replacement,p.original,p.count);
    DWORD value=reinterpret_cast<DWORD>(&logicalWidth);memcpy(patches[0].replacement+4,&value,4);
    value=reinterpret_cast<DWORD>(&logicalHeight);memcpy(patches[1].replacement+4,&value,4);
    value=static_cast<DWORD>(reinterpret_cast<uintptr_t>(&LayoutTail)-(Address(0x4643d0)+5));
    memcpy(patches[2].replacement+1,&value,4);
    value=static_cast<DWORD>(reinterpret_cast<uintptr_t>(&InitialEntryUpdate)-(Address(0x46a3bc)+5));
    memcpy(patches[3].replacement+1,&value,4);
    HMODULE self=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&ApplyBattleLayoutFix),&self))return false;
    bool wrote=false,ok=false;
    if(MakeWritable()){
        __try{
            #ifdef BATTLE_LAYOUT_TESTING
            unsigned i=0;
            #endif
            for(const auto& p:patches){memcpy(reinterpret_cast<void*>(Address(p.address)),p.replacement,p.count);wrote=true;
#ifdef BATTLE_LAYOUT_TESTING
                ++i;
                if(failAfter>=0&&i>=static_cast<unsigned>(failAfter))__leave;
#endif
            }
            ok=FlushInstructionCache(GetCurrentProcess(),nullptr,0)!=FALSE;
        }__except(EXCEPTION_EXECUTE_HANDLER){ok=false;}
    }
    if(!RestoreProtection()){healthy=false;ok=false;}
    if(!ok){if(wrote&&!RestoreCode())healthy=false;return false;}
    applied=true;return true;
}
bool RollbackBattleLayoutFix() noexcept {
    if(!healthy)return false;if(!applied)return true;
    if(calls||entryCalls)return false;
    if(!ValidateBattleLayoutFix()||!RestoreCode()){healthy=false;return false;}
    applied=false;return true;
}
bool BattleLayoutFixHealthy() noexcept{return healthy;}
LONG BattleLayoutFixCalls() noexcept{return InterlockedCompareExchange(&calls,0,0);}
#ifdef BATTLE_LAYOUT_TESTING
void SetBattleLayoutTestBase(void* base) noexcept{imageBase=reinterpret_cast<uintptr_t>(base);}
void SetBattleLayoutTestFailure(int value) noexcept{failAfter=value;}
void SetBattleLayoutTestTail(Tail value) noexcept{originalTail=value;}
void InvokeBattleLayoutTestTail() noexcept{LayoutTail();}
void SetBattleEntryTestUpdate(Update value) noexcept{originalUpdate=value;}
int InvokeBattleEntryTestUpdate() noexcept{return InitialEntryUpdate();}
#endif
