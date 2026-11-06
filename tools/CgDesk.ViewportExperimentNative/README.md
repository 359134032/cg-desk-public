# Native viewport component

CgDesk ships an x86 DLL and its hash-bound build profile under `Native/Viewport`, copied into app output as `viewport`. Since 0.1.13, the launcher defaults to the largest 4:3 mode fitting the current monitor, capped at 1600×1200. The account/detection risk warning and launch confirmation remain. Users can select native settings to disable the experiment. The original EXE and resources are never opened for writing.

At the first verified DirectDraw factory call, the component reads adjacent `cgdesk_viewport.ini`. Missing or unsupported dimensions forward without changing game geometry. Guards validate the client code, initial context, original 640×480 or 800×600 settings, window ownership and initialization thread. Rollback restores snapshotted geometry; an unconfirmed restoration fails initialization.

Modes above 1152×864 require four 64×64 tile caches. Pointer and call operand changes have exact byte guards, page protections are restored, and the module is pinned. A legacy scratch facade retains the original scan, protected-coordinate getter counts and request rectangles. Queue limits, protocol and server behavior are unchanged. Runtime code changes may be detected; no account-safety guarantee is made. See [the cache audit](../../docs/reports/map-cache-64-audit-2026-11-06.md) for evidence and static-analysis limits.

`D3DSeamFix.cpp` uses COM forwarding facades, preserving original system vptrs and interface identity. Only the exact background-tile draw shape and caller are corrected. World sprites, UVs and global sampling are unchanged. The removed resource-radius trial, old injector, fixed-profile deployment scripts and one-off process probe are no longer built or shipped.

Build with an existing MSVC x86 toolchain:

```powershell
./tools/CgDesk.ViewportExperimentNative/Build.ps1
./tools/CgDesk.ViewportExperimentNative/Test-Smoke.ps1
./tools/CgDesk.ViewportExperimentNative/Test-MapCache.ps1
```

The build requires the exact local runtime capture declared by its hash; captures are ignored research artifacts and are not redistributed. A normal .NET build uses the checked-in DLL and manifest. These scripts do not install to a game directory or execute captured game code. `Test-MapCache` invokes synthetic callbacks in an independent host. The smoke host checks ordinary Windows export forwarding and rejects the foreign target.

The 0.1.12 user test accepted this machine's 1600×1200 map/UI and FSR presentation. This does not certify every map, long gameplay, another client version or account safety. Historical trials remain documented under `docs/reports`.
