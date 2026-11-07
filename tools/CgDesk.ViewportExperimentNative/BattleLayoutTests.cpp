#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include "BattleLayoutFix.h"
#include "TransitionBufferFix.h"
#include "TransitionSpeedFix.h"
#include "MapCacheFix.h"
#include "BattleFixture.h"
namespace {
uintptr_t base=0;int checks=0,failures=0,tailCalls=0,updateCalls=0;DWORD updateInputError=0;
void Check(bool value,const char* message){++checks;if(!value){++failures;if(failures<10)printf("FAIL: %s\n",message);}}
uintptr_t Address(uintptr_t value){return base+value-0x400000;}
void __cdecl FakeTail(){++tailCalls;}
int __cdecl FakeUpdate(){++updateCalls;updateInputError=GetLastError();SetLastError(2468);return 0x12345678;}
void Store(uintptr_t address,const void* data,unsigned size){DWORD old=0,ignored=0;VirtualProtect(reinterpret_cast<void*>(Address(address)),size,PAGE_READWRITE,&old);memcpy(reinterpret_cast<void*>(Address(address)),data,size);VirtualProtect(reinterpret_cast<void*>(Address(address)),size,old,&ignored);}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;
    HANDLE file=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return 3;
    HANDLE map=CreateFileMappingW(file,nullptr,PAGE_READONLY|SEC_IMAGE,0,0,nullptr);
    if(!map){CloseHandle(file);return 4;}
    void* view=MapViewOfFile(map,FILE_MAP_READ,0,0,0);
    if(!view){CloseHandle(map);CloseHandle(file);return 5;}
    base=reinterpret_cast<uintptr_t>(view);SetBattleLayoutTestBase(view);SetBattleLayoutTestTail(&FakeTail);
    SetTransitionTestBase(view);SetTransitionSpeedTestBase(view);SetMapCacheTestBase(view);
    const int initialSize[]={1600,1200};Store(0x5e0198,initialSize,sizeof(initialSize));
    Check(ApplyTransitionBufferFix()&&ApplyBattleLayoutFix()&&ApplyTransitionSpeedFix()&&ApplyMapCacheFix(),"all four viewport components compose in production install order");
    Check(ValidateTransitionBufferFix()&&ValidateBattleLayoutFix()&&ValidateTransitionSpeedFix()&&ValidateMapCacheFix(),"all instruction guards remain valid after combined installation");
    Check(RollbackMapCacheFix()&&RollbackTransitionSpeedFix()&&RollbackBattleLayoutFix()&&RollbackTransitionBufferFix(),"combined initialization rollback restores every component");
    Check(ValidateBattleLayoutFix(),"all three original instructions and table loop validate");
    MEMORY_BASIC_INFORMATION m{};VirtualQuery(reinterpret_cast<void*>(Address(0x464000)),&m,sizeof(m));DWORD original=m.Protect;
    VirtualQuery(reinterpret_cast<void*>(Address(0x46a000)),&m,sizeof(m));const DWORD entryProtection=m.Protect;
    for(int i=1;i<=4;i++){
        SetBattleLayoutTestFailure(i);Check(!ApplyBattleLayoutFix(),"partial-write failure injected");
        Check(ValidateBattleLayoutFix()&&BattleLayoutFixHealthy(),"all original instructions restored on failure");
        VirtualQuery(reinterpret_cast<void*>(Address(0x464000)),&m,sizeof(m));Check(m.Protect==original,"code protection restored after failure");
        VirtualQuery(reinterpret_cast<void*>(Address(0x46a000)),&m,sizeof(m));Check(m.Protect==entryProtection,"initial entry code page protection restored after failure");
    }
    SetBattleLayoutTestFailure(-1);
    Check(ApplyBattleLayoutFix()&&ApplyBattleLayoutFix()&&ValidateBattleLayoutFix(),"apply succeeds and is idempotent");
    Check(**reinterpret_cast<DWORD**>(Address(0x46427b))==800,"only battle width source becomes 800");
    Check(**reinterpret_cast<DWORD**>(Address(0x464286))==600,"only battle height source becomes 600");
    Check(RollbackBattleLayoutFix()&&ValidateBattleLayoutFix(),"rollback before first use restores original code");
    Check(ApplyBattleLayoutFix(),"reapply after initialization rollback succeeds");
    const int dimensions[][2]={{800,600},{1024,768},{1152,864},{1280,960},{1400,1050},{1440,1080},{1600,1200}};
    for(const auto& size:dimensions){
        Store(0x5e0198,size,sizeof(size));
        Store(0x68e340,native800,sizeof(native800));
        Store(0x68e4a0,sourceRows,sizeof(sourceRows));
        DWORD marker=0x7a7b7c7d;Store(0x68e33c,&marker,4);Store(0x68e4a0+sizeof(sourceRows),&marker,4);
        const int before=tailCalls;SetLastError(1234);InvokeBattleLayoutTestTail();
        Check(GetLastError()==1234,"tail wrapper leaves LastError intact");
        Check(tailCalls==before+1,"original tail called exactly once with no added game getters");
        const auto* table=reinterpret_cast<const int*>(Address(0x68e340));
        const int dx=(size[0]-800)/2,dy=(size[1]-600)/2;
        for(unsigned row=0;row<22;row++){
            for(unsigned col=0;col<4;col++)Check(table[row*4+col]==native800[row][col]+((col&1)?dy:dx),"all shared formation targets retain original native rounding");
            float px=static_cast<float>(table[row*4]),py=static_cast<float>(table[row*4+1]);
            const bool trimmed=TrimBattleInitialPosition(&px,&py,table[row*4+2],table[row*4+3],size[0],size[1]);
            Check(trimmed==(size[0]>800),"initial position clipping applies only above native800");
            if(size[0]>800){
                const double ex=px-dx,ey=py-dy;
                const double vx=native800[row][0]-native800[row][2],vy=native800[row][1]-native800[row][3];
                const double nx=ex-native800[row][2],ny=ey-native800[row][3];
                Check(ex>=0&&ex<=800&&ey>=0&&ey<=600,"entry is within logical native viewport");
                Check(fabs(ex)<.001||fabs(ex-800)<.001||fabs(ey)<.001||fabs(ey-600)<.001,"offscreen entry begins at native visibility boundary");
                Check(nx*nx+ny*ny<vx*vx+vy*vy,"only formerly hidden entry distance is removed");
                Check(fabs(nx*vy-ny*vx)<fabs(vx)+fabs(vy)&&nx*vx+ny*vy>=0,"entry retains original direction within integer rounding");
            }
        }
        // All cross-slot movement vectors remain identical to native as well.
        for(unsigned a=0;a<22;a++)for(unsigned b=0;b<22;b++)
            Check(table[a*4+2]-table[b*4+2]==native800[a][2]-native800[b][2]&&table[a*4+3]-table[b*4+3]==native800[a][3]-native800[b][3],"cross-slot approach and return vectors stay native");
        Check(*reinterpret_cast<int*>(Address(0x5e0198))==size[0]&&*reinterpret_cast<int*>(Address(0x5e019c))==size[1],"global drawing dimensions never change during battle adjustment");
        Check(!memcmp(reinterpret_cast<void*>(Address(0x68e4a0)),sourceRows,sizeof(sourceRows)),"source formation table never modified");
        Check(*reinterpret_cast<DWORD*>(Address(0x68e33c))==marker&&*reinterpret_cast<DWORD*>(Address(0x68e4a0)+sizeof(sourceRows))==marker,"only 22 destination records are modified");
    }
    BYTE actor[0x400]{},aux[0x1400]{};
    const DWORD actorPointer=reinterpret_cast<DWORD>(actor),auxPointer=reinterpret_cast<DWORD>(aux);
    DWORD empty[20]{};Store(0x68dee0,empty,sizeof(empty));Store(0x68dee0,&actorPointer,4);
    *reinterpret_cast<DWORD*>(actor+8)=static_cast<DWORD>(Address(0x439410));
    *reinterpret_cast<DWORD*>(actor+0xc)=auxPointer;*reinterpret_cast<int*>(actor+0x2c)=2;
    *reinterpret_cast<float*>(actor+0x40)=1375;*reinterpret_cast<float*>(actor+0x44)=1125;
    *reinterpret_cast<float*>(actor+0x54)=4;
    const int auxiliary[]={1000,750,1375,1125};memcpy(aux,auxiliary,sizeof(auxiliary));
    SetBattleEntryTestUpdate(&FakeUpdate);SetLastError(1234);
    Check(InvokeBattleEntryTestUpdate()==0x12345678&&updateCalls==1&&updateInputError==1234&&GetLastError()==2468,"initial entry wrapper invokes original update exactly once and preserves ABI/errors");
    Check(*reinterpret_cast<float*>(actor+0x40)==1150&&*reinterpret_cast<float*>(actor+0x44)==900,"only initial hero position is clipped to visible boundary");
    Check(!memcmp(aux,auxiliary,sizeof(auxiliary))&&*reinterpret_cast<float*>(actor+0x54)==4,"shared auxiliary entry/return targets and actor speed stay intact");
    InvokeBattleEntryTestUpdate();Check(*reinterpret_cast<float*>(actor+0x40)==1150&&*reinterpret_cast<float*>(actor+0x44)==900,"repeated frame is idempotent after clipping");
    *reinterpret_cast<float*>(actor+0x40)=1375;*reinterpret_cast<float*>(actor+0x44)=1125;*reinterpret_cast<int*>(actor+0x2c)=3;
    InvokeBattleEntryTestUpdate();Check(*reinterpret_cast<float*>(actor+0x40)==1375,"attack and later phases are not clipped");
    *reinterpret_cast<int*>(actor+0x2c)=2;aux[0xfdc]=1;
    InvokeBattleEntryTestUpdate();Check(*reinterpret_cast<float*>(actor+0x40)==1375,"completed entry actors are not clipped");
    float enemyX=225,enemyY=150;
    Check(TrimBattleInitialPosition(&enemyX,&enemyY,598,523,1600,1200)&&enemyX==400&&enemyY==325,"measured enemy visible path retains 280px without touching its shared entry");
    int invalid[22][4];memcpy(invalid,native800,sizeof(invalid));CenterBattleLayout(&invalid[0][0],22,1920,1080);
    Check(!memcmp(invalid,native800,sizeof(invalid)),"unsupported aspect ratio leaves table untouched");
    Check(!RollbackBattleLayoutFix()&&BattleLayoutFixHealthy(),"live layout cannot be unpatched mid-battle");
    UnmapViewOfFile(view);CloseHandle(map);CloseHandle(file);
    printf("Battle layout fixture: %d checks, %d failures; no game instructions executed.\n",checks,failures);
    return failures?1:0;
}
