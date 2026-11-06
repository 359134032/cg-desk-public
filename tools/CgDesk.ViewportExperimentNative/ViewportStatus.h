#pragma once
#include <windows.h>
#include "D3DSeamFix.h"
#include "MapCacheFix.h"

enum ViewportGuard : DWORD {
    GuardNone = 0, GuardWrongExecutable = 1, GuardWrongBase = 2,
    GuardWrongCaller = 3, GuardSignatureMismatch = 4, GuardNotWindowed = 5,
    GuardInvalidWindow = 6, GuardInvalidContext = 7, GuardWrongOutput = 8,
    GuardAlreadyInitialized = 9, GuardUnsupportedDimensions = 10,
    GuardUnwritableData = 11, GuardWindowGeometry = 12,
    GuardAlreadyApplied = 13, GuardPreviousFailure = 14, GuardException = 15,
    GuardMapCache = 17, GuardTransitionBuffers = 18
};

struct ViewportStatus {
    DWORD Size;
    DWORD Version;
    LONG WrapperCalls;
    LONG ProfileAttempts;
    LONG Applied;
    DWORD LastGuard;
    DWORD LastWin32Error;
    DWORD LastException;
    LONG RollbackAttempted;
    LONG RollbackSucceeded;
    LONG ForwardingFailures;
    HRESULT LastForwardResult;
    DWORD Context;
    DWORD Caller;
    DWORD OriginalWidth;
    DWORD OriginalHeight;
    DWORD RequestedWidth;
    DWORD RequestedHeight;
    D3DSeamFixStatus SeamFix;
    MapCacheDiagnostics MapCache;
};

using QueryViewportStatus = DWORD (WINAPI*)(ViewportStatus*, DWORD);
