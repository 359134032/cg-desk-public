#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <initializer_list>
#include "TransitionSpeedFix.h"
namespace {
uintptr_t base=0;int checks=0,failures=0,exitCalls=0,enterCalls=0,lastMode=0,lastStep=0;float lastAcceleration=0;
uintptr_t Address(uintptr_t value){return base+value-0x400000;}
void Check(bool value,const char* message){++checks;if(!value){++failures;if(failures<10)printf("FAIL: %s\n",message);}}
int __cdecl FakeExit(int mode,int step,float acceleration){++exitCalls;lastMode=mode;lastStep=step;lastAcceleration=acceleration;SetLastError(1234);return 0x12345678;}
int __cdecl FakeEnter(int mode,int step,float acceleration){++enterCalls;lastMode=mode;lastStep=step;lastAcceleration=acceleration;SetLastError(5678);return 1;}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2&&argc!=3)return 2;
    HANDLE file=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return 3;
    HANDLE mapping=CreateFileMappingW(file,nullptr,PAGE_READONLY|SEC_IMAGE,0,0,nullptr);if(!mapping){CloseHandle(file);return 4;}
    void* view=MapViewOfFile(mapping,FILE_MAP_READ,0,0,0);if(!view){CloseHandle(mapping);CloseHandle(file);return 5;}
    base=reinterpret_cast<uintptr_t>(view);SetTransitionSpeedTestBase(view);SetTransitionSpeedTestCallbacks(&FakeExit,&FakeEnter);
    DWORD old=0,ignored=0;VirtualProtect(reinterpret_cast<void*>(Address(0x5e0198)),8,PAGE_READWRITE,&old);
    *reinterpret_cast<int*>(Address(0x5e0198))=1600;*reinterpret_cast<int*>(Address(0x5e019c))=1200;
    VirtualProtect(reinterpret_cast<void*>(Address(0x5e0198)),8,old,&ignored);
    Check(ValidateTransitionSpeedFix(),"all original call and constant guards validate");
    MEMORY_BASIC_INFORMATION first{},second{};VirtualQuery(reinterpret_cast<void*>(Address(0x558000)),&first,sizeof(first));VirtualQuery(reinterpret_cast<void*>(Address(0x559000)),&second,sizeof(second));
    if(argc==3){
        if(wcscmp(argv[2],L"--protection-failure"))return 2;
        SetTransitionSpeedTestProtectionFailure(true);
        Check(!ApplyTransitionSpeedFix(),"second page permission change fails before instruction writes");
        Check(!TransitionSpeedFixHealthy(),"failed permission recovery marks component unhealthy even without code writes");
        Check(!RollbackTransitionSpeedFix(),"rollback cannot report success when page permissions are unrecovered");
        const BYTE instruction[]={0xe8,0xd9,5,0,0};
        Check(!memcmp(reinterpret_cast<void*>(Address(0x558312)),instruction,sizeof(instruction)),"no instruction was written in the permission double-failure scenario");
        SetTransitionSpeedTestProtectionFailure(false);
        VirtualProtect(reinterpret_cast<void*>(Address(0x558000)),4096,first.Protect,&ignored);
        UnmapViewOfFile(view);CloseHandle(mapping);CloseHandle(file);
        printf("Transition permission fixture: %d checks, %d failures; no game code executed.\n",checks,failures);return failures?1:0;
    }
    for(int n=1;n<=10;n++){
        SetTransitionSpeedTestFailure(n);Check(!ApplyTransitionSpeedFix(),"partial-write failure injected");
        Check(ValidateTransitionSpeedFix()&&TransitionSpeedFixHealthy(),"all original instructions restored transactionally");
        MEMORY_BASIC_INFORMATION a{},b{};VirtualQuery(reinterpret_cast<void*>(Address(0x558000)),&a,sizeof(a));VirtualQuery(reinterpret_cast<void*>(Address(0x559000)),&b,sizeof(b));
        Check(a.Protect==first.Protect&&b.Protect==second.Protect,"both original page protections restored");
    }
    SetTransitionSpeedTestFailure(-1);Check(ApplyTransitionSpeedFix()&&ApplyTransitionSpeedFix()&&ValidateTransitionSpeedFix(),"install is successful and idempotent");
    Check(**reinterpret_cast<float**>(Address(0x558fd0))==200,"positive speed cap scales to canvas");
    Check(**reinterpret_cast<float**>(Address(0x559cc3))==-200,"negative speed cap scales to canvas");
    Check(**reinterpret_cast<float**>(Address(0x559cce))==200,"return speed cap scales to canvas");
    Check(**reinterpret_cast<float**>(Address(0x55905f))==1200,"completion threshold follows actual screen height");
    Check(RollbackTransitionSpeedFix()&&ValidateTransitionSpeedFix(),"initialization rollback restores the original effect");
    Check(ApplyTransitionSpeedFix(),"reinstall after rollback succeeds");
    const int widths[]={800,1024,1152,1280,1400,1440,1600};
    for(int width:widths)for(int mode=6;mode<=8;mode++)for(int step0:{0,8,32})for(float acceleration:{0.0f,0.5f,2.0f}){
        int step=step0;float scaled=acceleration;bool changed=NormalizeTransitionStep(mode,step0,acceleration,width,width*3/4,&step,&scaled);
        Check(changed==(width>800),"native size forwards original parameters unchanged");
        if(width==800){Check(step==step0&&scaled==acceleration,"800x600 behavior unchanged");continue;}
        int expected=static_cast<int>(floor((step0?step0:(mode==8?16:4))*(width/800.0)+0.5));
        Check(step==expected&&step>0&&step<=127,"row activation stride follows actual extent within signed byte");
        Check(scaled>0&&scaled*16==floorf(scaled*16),"acceleration is exactly representable in binary to avoid inverse zero-crossing stalls");
    }
    int actual=InvokeTransitionSpeedTest(false,6,8,2);
    Check(actual==0x12345678&&exitCalls==1&&enterCalls==0,"exit helper called once and EAX result preserved");
    Check(lastMode==6&&lastStep==16&&lastAcceleration==4&&GetLastError()==1234,"exit mode and scaled arguments forwarded with LastError intact");
    actual=InvokeTransitionSpeedTest(true,8,32,0.5f);
    Check(actual==1&&enterCalls==1&&exitCalls==1,"entry helper called once with completion result unchanged");
    Check(lastMode==8&&lastStep==64&&lastAcceleration==1&&GetLastError()==5678,"entry arguments scale without calling game phase functions");
    int step=8;float acceleration=2;
    Check(!NormalizeTransitionStep(5,8,2,1600,1200,&step,&acceleration)&&step==8&&acceleration==2,"unreviewed effect types untouched");
    Check(!NormalizeTransitionStep(6,8,2,1920,1080,&step,&acceleration),"unsupported aspect ratio rejected");
    Check(!NormalizeTransitionStep(6,255,2,1600,1200,&step,&acceleration),"negative byte stride preserves native behavior");
    Check(!RollbackTransitionSpeedFix()&&TransitionSpeedFixHealthy(),"live animation cannot be unpatched mid-effect");
    UnmapViewOfFile(view);CloseHandle(mapping);CloseHandle(file);
    printf("Transition speed fixture: %d checks, %d failures; no captured game code executed.\n",checks,failures);return failures?1:0;
}
