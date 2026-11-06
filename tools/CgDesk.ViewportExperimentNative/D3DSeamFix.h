#pragma once
#include <stdint.h>

// FVF 0x144: XYZRHW, diffuse color, one texture coordinate pair.
struct SeamVertex {
    float x, y, z, rhw;
    uint32_t color;
    float u, v;
};
static_assert(sizeof(SeamVertex) == 28, "FVF 0x144 has a 28-byte stride");

struct SeamGeometryContext {
    int screenWidth, screenHeight;
    int textureWidth, textureHeight;
};

// Leaves output untouched unless a validated quad needs a 1-pixel correction.
bool CorrectD3DSeamQuad(unsigned primitive, uint32_t fvf,
    const SeamVertex* input, unsigned count, const SeamGeometryContext& context,
    SeamVertex* output) noexcept;

struct D3DSeamFixStatus {
    int32_t installedDraw, installedD3D, installedDevice, installFailures;
    int32_t drawCalls, callerMatches, correctedQuads, correctedX, correctedY;
    int32_t retiredRecords, retainedRecords;
};

// Caller must first establish the hash-bound game profile and 1024x768 setup.
// Replaces the caller-owned interface pointer with a forwarding facade, leaving
// every system COM object's vptr untouched. Facades and module remain resident.
// No game code pages or global sampler state are modified. Maximum 128 records.
bool InstallD3DSeamFix(void** ddraw7) noexcept;
D3DSeamFixStatus GetD3DSeamFixStatus() noexcept;
