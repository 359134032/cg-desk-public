#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "MapCacheFix.h"

// Execution-free fixture: callbacks here model the captured map setter and
// scanner contracts; no mapped game instruction or entry point is invoked.
namespace {
uintptr_t image = 0;
constexpr uintptr_t fields[] = {0xaa87ac,0xaa87b0,0xaa87b4,0xaa87b8,0xaa87bc,0xaa87c0};
struct Bounds { short x0,y0,x1,y1,w,h; };
int mapId=1, mapW=200, mapH=200, px=100, py=100, reads=0, scans=0, failures=0, checks=0;
int scanMode=0;
bool staleScratch=false;
uintptr_t A(uintptr_t p) { return image+p-0x400000; }
void Check(bool ok,const char* message) { ++checks; if(!ok){++failures; printf("FAIL %s\n",message);} }
int Min(int a,int b){return a<b?a:b;} int Max(int a,int b){return a>b?a:b;}
Bounds Expected(bool expanded) {
    Bounds b{};
    b.x0=static_cast<short>(Max(0,px-(expanded?36:30)));
    b.y0=static_cast<short>(Max(0,py-(expanded?28:20)));
    b.x1=static_cast<short>(Min(mapW,px+(expanded?28:21)));
    b.y1=static_cast<short>(Min(mapH,py+(expanded?36:31)));
    b.w=b.x1-b.x0; b.h=b.y1-b.y0; return b;
}
Bounds Read(){Bounds b{};for(unsigned i=0;i<6;++i)(&b.x0)[i]=*reinterpret_cast<short*>(A(fields[i]));return b;}
void Write(const Bounds& b){for(unsigned i=0;i<6;++i)*reinterpret_cast<short*>(A(fields[i]))=(&b.x0)[i];}
bool Same(const Bounds& a,const Bounds& b){return memcmp(&a,&b,sizeof(a))==0;}
void SetMap(int id,int w,int h,int x,int y) {
    mapId=id;mapW=w;mapH=h;px=x;py=y;
    *reinterpret_cast<int*>(A(0xaa8790))=0;
    *reinterpret_cast<int*>(A(0xaa8794))=id;
    *reinterpret_cast<int*>(A(0xaa87a4))=w;
    *reinterpret_cast<int*>(A(0xaa87a8))=h;
}
unsigned short Cell(int id,int x,int y){return static_cast<unsigned short>((id*997+x*31+y*131)&0xffff);}
void Fill(int id) {
    const Bounds b=Read();auto* p=static_cast<unsigned short*>(GetMapCacheTestBuffer(2));
    for(int y=0;y<b.h;++y)for(int x=0;x<b.w;++x)p[y*b.w+x]=Cell(id,b.x0+x,b.y0+y);
}
void __cdecl OriginalBounds(){reads+=4;Write(Expected(false));}
int __cdecl OriginalScan(int) {
    ++scans;const Bounds b=Read();
    if(b.w<51||b.h<51)return 0; // Captured original's first two guards.
    reads+=10; // A full interior cardinal scan uses ten protected-coordinate operations.
    const auto* p=static_cast<const unsigned short*>(GetMapCacheTestScratch());
    staleScratch=false;
    for(int y=0;y<b.h;++y)for(int x=0;x<b.w;++x)
        if(p[y*b.w+x]!=Cell(mapId,b.x0+x,b.y0+y))staleScratch=true;
    if(scanMode==1){SetMap(90,80,80,40,40);Write(Expected(true));}
    return 1;
}
void* __cdecl OriginalClear(void* p,int c,size_t n){return memset(p,c,n);}
DWORD WINAPI WorkerBounds(void*){InvokeMapCacheTestBounds();return 0;}
}

int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;
    HANDLE file=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return 3;
    HANDLE mapping=CreateFileMappingW(file,nullptr,PAGE_READONLY|SEC_IMAGE,0,0,nullptr);
    if(!mapping){CloseHandle(file);return 4;}
    void* view=MapViewOfFile(mapping,FILE_MAP_READ,0,0,0);
    if(!view){CloseHandle(mapping);CloseHandle(file);return 5;}
    image=reinterpret_cast<uintptr_t>(view);SetMapCacheTestBase(view);
    SetMapCacheTestCallbacks(&OriginalBounds,&OriginalScan,&OriginalClear);
    Check(ApplyMapCacheFix(),"profile applies");
    // Normal captured transition: 545560 -> 49feb0 -> coordinate setter ->
    // 49fe60 -> original4a0540. The facade observes the new map and coordinates.
    const int cases[][5]={{1,200,200,100,100},{2,300,240,60,90},{3,20,20,10,10},
                          {4,64,80,30,40},{5,2000,2000,1000,1000}};
    for(const auto& c:cases) {
        SetMap(c[0],c[1],c[2],c[3],c[4]);const int prior=reads;
        InvokeMapCacheTestBounds();Check(reads==prior+4,"normal map change keeps four bounds reads");
        Check(Same(Read(),Expected(true)),"normal map change updates expanded snapshot");
        Fill(mapId);const Bounds b=Read();
        const int result=InvokeMapCacheTestScan(0);
        const Bounds legacy=Expected(false);
        Check(result==((legacy.w>=51&&legacy.h>=51)?1:0),"original scan guard/return preserved");
        Check(!staleScratch,"normal transition scratch uses current map data");
        Check(Same(Read(),b),"normal transition restores current expanded bounds");
    }
    // Captured reset49d800: dimensions/position zero before bounds.
    SetMap(0,0,0,0,0);InvokeMapCacheTestBounds();
    const int resetReads=reads;
    Check(InvokeMapCacheTestScan(0)==0,"zero map returns original early zero");
    Check(reads==resetReads,"zero map scan performs no protected reads");
    // Negative lifecycle witness: map ID change without its normal bounds/
    // cache reload. Current facade has no generation check, so stale data passes.
    SetMap(10,200,200,100,100);InvokeMapCacheTestBounds();Fill(mapId);
    SetMap(11,200,200,100,100);const int staleScans=scans;
    InvokeMapCacheTestScan(0);
    const bool unguardedEpoch=scans==staleScans+1&&staleScratch;
    Check(unguardedEpoch,"negative witness: changed map ID is not rejected");
    // Negative thread witness: a real bounds call on a different thread is
    // dropped before the original function. This is new wrapper behavior.
    const int threadReads=reads;
    SetMap(12,300,300,200,200);
    HANDLE thread=CreateThread(nullptr,0,&WorkerBounds,nullptr,0,nullptr);
    Check(thread!=nullptr,"secondary thread starts");
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    const bool rejectedBounds=reads==threadReads&&!Same(Read(),Expected(true));
    Check(rejectedBounds,"negative witness: wrong-thread update keeps stale bounds");
    Check(GetMapCacheDiagnostics().ThreadOrReentryRejected>0,"wrong-thread diagnostic exposes witness");
    // Negative interleaving witness: if an unobserved concurrent consumer changes
    // the map while original scan runs, finally restores the previous map bounds.
    SetMap(20,200,200,100,100);InvokeMapCacheTestBounds();Fill(mapId);
    const Bounds old=Read();scanMode=1;InvokeMapCacheTestScan(0);scanMode=0;
    const bool clobberedEpoch=mapId==90&&Same(Read(),old)&&!Same(Read(),Expected(true));
    Check(clobberedEpoch,"negative witness: finally is not map-generation-aware");
    ResetMapCacheTestRuntimeUse();Check(RollbackMapCacheFix(),"offline fixture rollback");
    UnmapViewOfFile(view);CloseHandle(mapping);CloseHandle(file);
    printf("Map-cache lifecycle fixture checks=%d failures=%d normal_maps=5 reset_map=1 stale_epoch_witness=%d wrong_thread_witness=%d concurrent_epoch_witness=%d; no game instruction executed.\n",
        checks,failures,unguardedEpoch?1:0,rejectedBounds?1:0,clobberedEpoch?1:0);
    return failures?1:0;
}
