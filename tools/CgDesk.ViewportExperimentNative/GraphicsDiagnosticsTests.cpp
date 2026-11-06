#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "GraphicsDiagnostics.h"

namespace {
void* methods[26]{};
struct Surface {void** methods;};
Surface surface{methods};
DWORD descriptor[31]{};
HRESULT expected=0;
int calls=0,failures=0,checks=0;
bool arguments=false;
HRESULT WINAPI FakeLock(void* self,void* rectangle,void* output,DWORD flags,HANDLE event){
    ++calls;
    arguments=self==&surface&&!rectangle&&output==descriptor&&flags==1&&!event;
    SetLastError(1234);
    return expected;
}
void Check(bool condition,const char* message){++checks;if(!condition){++failures;printf("FAIL: %s\n",message);}}
}
int main(){
    methods[25]=reinterpret_cast<void*>(&FakeLock);
    using Observer=HRESULT(WINAPI*)(void*,void*,void*,DWORD,HANDLE);
    auto observer=reinterpret_cast<Observer>(GraphicsLockObserverAddress());
    const HRESULT results[]={S_OK,S_FALSE,E_FAIL,static_cast<HRESULT>(0x887601c2),static_cast<HRESULT>(0x887601ae)};
    for(HRESULT result:results){
        expected=result;calls=0;arguments=false;
        DWORD before=0,after=0,flags=0;
        __asm mov before,esp
        HRESULT actual=observer(&surface,nullptr,descriptor,1,nullptr);
        __asm {
            pushfd
            pop eax
            mov flags,eax
            mov after,esp
        }
        DWORD error=GetLastError();
        Check(actual==result,"HRESULT preserved");
        Check(calls==1&&arguments,"original Lock called exactly once with all five original arguments");
        Check(before==after,"stdcall removes precisely 20 argument bytes");
        Check(((flags&0x40)!=0)==(result==0),"original JNZ receives matching TEST zero flag");
        Check(((flags&0x80)!=0)==(result<0),"original sign flag preserved by TEST");
        Check((flags&(0x800|1))==0,"TEST clears overflow and carry");
        Check(error==1234,"LastError preserved");
    }
    printf("Graphics diagnostic observer: %d checks, %d failures; no game code executed.\n",checks,failures);
    return failures?1:0;
}
