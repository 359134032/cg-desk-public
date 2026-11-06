#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "TransitionBufferFix.h"
#include "TransitionBufferProfile.h"
namespace {
int checks=0,failures=0;
uintptr_t base=0;
uintptr_t Address(uintptr_t address){return base+address-0x400000;}
void Check(bool value,const char* message){++checks;if(!value){++failures;if(failures<10)printf("FAIL: %s\n",message);}}
DWORD Protection(uintptr_t address){MEMORY_BASIC_INFORMATION m{};VirtualQuery(reinterpret_cast<void*>(address),&m,sizeof(m));return m.Protect;}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;
    HANDLE file=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return 3;
    HANDLE mapping=CreateFileMappingW(file,nullptr,PAGE_READONLY|SEC_IMAGE,0,0,nullptr);
    if(!mapping){CloseHandle(file);return 4;}
    void* view=MapViewOfFile(mapping,FILE_MAP_READ,0,0,0);
    if(!view){CloseHandle(mapping);CloseHandle(file);return 5;}
    base=reinterpret_cast<uintptr_t>(view);SetTransitionTestBase(view);
    DWORD protections[ARRAYSIZE(TransitionPatchSpecs)]{};
    for(unsigned i=0;i<ARRAYSIZE(TransitionPatchSpecs);i++)protections[i]=Protection(Address(TransitionPatchSpecs[i].address));
    Check(ValidateTransitionBufferFix(),"all original instruction guards valid");
    // Inject an interruption after every write; code, allocations and protection
    // must all return to the initial state. No fixture instruction executes.
    for(int fail=1;fail<=static_cast<int>(ARRAYSIZE(TransitionPatchSpecs));fail++){
        SetTransitionTestFailure(fail);
        Check(!ApplyTransitionBufferFix(),"injected partial transaction fails");
        Check(ValidateTransitionBufferFix(),"every original instruction restored");
        Check(TransitionBufferFixHealthy()&&!TransitionBufferFixApplied(),"rollback leaves healthy inactive state");
        Check(!GetTransitionTestBuffer(0)&&!GetTransitionTestBuffer(1)&&!GetTransitionTestBuffer(2),"all unexposed buffers released");
        for(unsigned i=0;i<ARRAYSIZE(TransitionPatchSpecs);i++)Check(Protection(Address(TransitionPatchSpecs[i].address))==protections[i],"original page protection restored");
    }
    SetTransitionTestFailure(-1);
    Check(ApplyTransitionBufferFix()&&ApplyTransitionBufferFix(),"apply is successful and idempotent");
    Check(ValidateTransitionBufferFix(),"full relocated instructions validate");
    for(const auto& p:TransitionPatchSpecs){
        DWORD expected=static_cast<DWORD>(reinterpret_cast<uintptr_t>(GetTransitionTestBuffer(p.array))+p.offset);
        Check(*reinterpret_cast<DWORD*>(Address(p.operand))==expected,"operand preserves array and signed offset");
    }
    for(unsigned n=0;n<3;n++){
        auto* a=GetTransitionTestBuffer(n);
        MEMORY_BASIC_INFORMATION guard{};
        VirtualQuery(reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(a)-16+8192),&guard,sizeof(guard));
        Check(guard.State==MEM_RESERVE&&guard.AllocationProtect==PAGE_NOACCESS,"allocation ends with inaccessible reserved guard page");
        for(int i=-2;i<=1201;i++)a[i]=static_cast<float>(10000*n+i);
    }
    for(unsigned n=0;n<3;n++)for(int i=-2;i<=1201;i++)
        Check(GetTransitionTestBuffer(n)[i]==static_cast<float>(10000*n+i),"max-height rows and original boundary offsets never alias another array");
    // Reproduce the real initialization order for vertical strips. The legacy
    // 1024-row layout overwrites the next array at height=1200; the new storage
    // keeps positions intact while clearing every velocity row.
    float legacy[3*1024+1204]{};
    float* oldPosition=legacy+1024;float* oldSpeed=legacy+2048;
    for(int i=0;i<1200;i++){oldSpeed[i]=0;oldPosition[i]=static_cast<float>(i);}
    Check(oldSpeed[0]==1024.0f,"legacy initialization demonstrably corrupts velocity array at row 1024");
    auto* position=GetTransitionTestBuffer(1);auto* speed=GetTransitionTestBuffer(2);
    for(int i=0;i<1200;i++){speed[i]=0;position[i]=static_cast<float>(i);}
    for(int i=0;i<1200;i++)Check(speed[i]==0&&position[i]==i,"1200-row transition initialization preserves both arrays");
    Check(RollbackTransitionBufferFix()&&ValidateTransitionBufferFix(),"initialization rollback restores complete original code");
    Check(!GetTransitionTestBuffer(0),"rollback releases unexposed buffers");
    // Corrupt an opcode (not the operand): validation must reject it before any write.
    uintptr_t first=Address(TransitionPatchSpecs[0].address);DWORD old=0,ignored=0;
    VirtualProtect(reinterpret_cast<void*>(first),1,PAGE_EXECUTE_READWRITE,&old);
    BYTE original=*reinterpret_cast<BYTE*>(first);*reinterpret_cast<BYTE*>(first)^=1;
    VirtualProtect(reinterpret_cast<void*>(first),1,old,&ignored);
    Check(!ApplyTransitionBufferFix()&&!GetTransitionTestBuffer(0),"instruction mismatch fails closed before allocation or mutation");
    VirtualProtect(reinterpret_cast<void*>(first),1,PAGE_EXECUTE_READWRITE,&ignored);
    *reinterpret_cast<BYTE*>(first)=original;VirtualProtect(reinterpret_cast<void*>(first),1,old,&ignored);
    UnmapViewOfFile(view);CloseHandle(mapping);CloseHandle(file);
    printf("Transition buffer fixture: %d checks, %d failures; captured game instructions never executed.\n",checks,failures);
    return failures?1:0;
}
