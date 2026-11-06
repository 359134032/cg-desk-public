param(
    [string]$CapturePath = (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\runtime-72316-20261106-042053-1f8731d2\code-section-00-text.bin'),
    [string]$CaptureSha256 = '1e7f13e748c792586c7db2eb21d26d19f710bafecea02df6c2d6e44ad136d427'
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
$output = Join-Path $source 'bin'
[IO.Directory]::CreateDirectory($output) | Out-Null
$vswhere = [Environment]::ExpandEnvironmentVariables('%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe')
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio discovery tool is unavailable. No tools are installed by this script.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC x86/x64 tools are unavailable. No tools are installed by this script.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw 'vcvarsall.bat is unavailable.' }
$systemDll = Join-Path $env:WINDIR 'SysWOW64\ddraw.dll'
if (-not (Test-Path -LiteralPath $systemDll)) { throw 'The local Windows x86 DirectDraw DLL is unavailable.' }

# Generate signatures only from the explicitly hash-bound local code capture.
$capture = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($CapturePath))
$actualCaptureHash = (Get-FileHash -LiteralPath $CapturePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualCaptureHash -ne $CaptureSha256.ToLowerInvariant()) { throw 'The .text capture SHA-256 does not match this profile.' }
$signatureLines = @('#pragma once', '#include <stdint.h>', 'struct ProfileSignature { uintptr_t address; unsigned char bytes[24]; };', 'static const ProfileSignature ProfileSignatures[] = {')
foreach ($address in @(0x481100, 0x5080e0, 0x4978c0, 0x49be00, 0x480438)) {
    $offset = $address - 0x401000
    if ($offset -lt 0 -or $offset + 24 -gt $capture.Length) { throw 'Signature range lies outside the captured code.' }
    $bytes = for ($i = 0; $i -lt 24; $i++) { '0x{0:x2}' -f $capture[$offset + $i] }
    $signatureLines += ('    {{ 0x{0:x}, {{ {1} }} }},' -f $address, ($bytes -join ', '))
}
$signatureLines += '};'
[IO.File]::WriteAllLines((Join-Path $output 'ProfileSignatures.h'), $signatureLines, [Text.Encoding]::ASCII)

# Enumerate the real local system DLL's PE export table. No external binary is fetched.
$systemBytes = [IO.File]::ReadAllBytes($systemDll)
function Read-U16([int]$offset) { [BitConverter]::ToUInt16($systemBytes, $offset) }
function Read-U32([int]$offset) { [BitConverter]::ToUInt32($systemBytes, $offset) }
$pe = [int](Read-U32 0x3c)
if ((Read-U16 0) -ne 0x5a4d -or (Read-U32 $pe) -ne 0x4550 -or (Read-U16 ($pe + 4)) -ne 0x14c) { throw 'Expected the local x86 PE DirectDraw DLL.' }
$optional = $pe + 24
if ((Read-U16 $optional) -ne 0x10b) { throw 'Expected PE32 optional header.' }
$sectionTable = $optional + (Read-U16 ($pe + 20))
$sectionCount = Read-U16 ($pe + 6)
function Rva-ToRaw([uint32]$rva) {
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $section = $sectionTable + $index * 40
        $start = Read-U32 ($section + 12)
        $length = [Math]::Max((Read-U32 ($section + 8)), (Read-U32 ($section + 16)))
        if ($rva -ge $start -and $rva -lt [uint64]$start + $length) { return [int]((Read-U32 ($section + 20)) + $rva - $start) }
    }
    throw 'Export RVA was outside the validated section table.'
}
function Read-Ascii([int]$offset) {
    $end = $offset
    while ($end -lt $systemBytes.Length -and $systemBytes[$end] -ne 0) { $end++ }
    if ($end -ge $systemBytes.Length) { throw 'Unterminated export name.' }
    [Text.Encoding]::ASCII.GetString($systemBytes, $offset, $end - $offset)
}
$export = Rva-ToRaw (Read-U32 ($optional + 96))
$ordinalBase = Read-U32 ($export + 16)
$functionCount = Read-U32 ($export + 20)
$nameCount = Read-U32 ($export + 24)
$functions = Rva-ToRaw (Read-U32 ($export + 28))
$names = Rva-ToRaw (Read-U32 ($export + 32))
$ordinals = Rva-ToRaw (Read-U32 ($export + 36))
if ($functionCount -gt 4096 -or $nameCount -gt 4096) { throw 'Unexpected export table size.' }
$named = @{}
for ($i = 0; $i -lt $nameCount; $i++) {
    $name = Read-Ascii (Rva-ToRaw (Read-U32 ($names + $i * 4)))
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw ('Unexpected export name: ' + $name) }
    $slot = [int](Read-U16 ($ordinals + $i * 2))
    $named[$slot] = $name
}
if ($named.Values -notcontains 'DirectDrawCreateEx') { throw 'System DirectDrawCreateEx export is missing.' }
$definition = @('LIBRARY ddraw', 'EXPORTS')
$exportInventory = @()
for ($slot = 0; $slot -lt $functionCount; $slot++) {
    if ((Read-U32 ($functions + $slot * 4)) -eq 0) { continue }
    $ordinal = $ordinalBase + $slot
    if ($named.ContainsKey($slot)) {
        $name = $named[$slot]
        if ($name -eq 'DirectDrawCreateEx') { $definition += ('    DirectDrawCreateEx=_CgDeskDirectDrawCreateEx@16 @{0}' -f $ordinal) }
        else { $definition += ('    {0}=cgdesk_viewport_sys.{0} @{1} PRIVATE' -f $name, $ordinal) }
        $exportInventory += [pscustomobject]@{ Name=$name; Ordinal=$ordinal; LocalWrapper=($name -eq 'DirectDrawCreateEx') }
    } else {
        $definition += ('    CgDeskSystemOrdinal{0}=cgdesk_viewport_sys.#{0} @{0} NONAME' -f $ordinal)
        $exportInventory += [pscustomobject]@{ Name=$null; Ordinal=$ordinal; LocalWrapper=$false }
    }
}
$definition += '    CgDeskViewportStatus=_CgDeskViewportStatus@8'
[IO.File]::WriteAllLines((Join-Path $output 'ddraw.def'), $definition, [Text.Encoding]::ASCII)
[IO.File]::WriteAllLines((Join-Path $output 'ddraw.local.def'), @('LIBRARY "ddraw.local.dll"', 'EXPORTS', '    DirectDrawCreateEx=_CgDeskDirectDrawCreateEx@16 @10', '    CgDeskViewportStatus=_CgDeskViewportStatus@8 @23'), [Text.Encoding]::ASCII)

$batch = @(
    '@echo off',
    ('call "{0}" x64_x86 >nul' -f $vcvars),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /c /EHsc /DUNICODE /D_UNICODE /I"{0}" /Fo"{0}\Proxy.obj" "{1}\Proxy.cpp"' -f $output, $source),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /c /EHsc /DUNICODE /D_UNICODE /Fo"{0}\D3DSeamFix.obj" "{1}\D3DSeamFix.cpp"' -f $output, $source),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /c /EHsc /DUNICODE /D_UNICODE /Fo"{0}\MapCacheFix.obj" "{1}\MapCacheFix.cpp"' -f $output, $source),
    'if errorlevel 1 exit /b %errorlevel%',
    ('link /nologo /DLL "{0}\Proxy.obj" "{0}\D3DSeamFix.obj" "{0}\MapCacheFix.obj" dxguid.lib user32.lib kernel32.lib /DEF:"{0}\ddraw.local.def" /OUT:"{0}\ddraw.local.dll" /IMPLIB:"{0}\ddraw.local.lib" /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO' -f $output),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /EHsc /DUNICODE /D_UNICODE /Fo"{0}\SmokeHost.obj" /Fe"{0}\ViewportSmokeHost.exe" "{1}\SmokeHost.cpp" /link dxguid.lib /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO' -f $output, $source),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /EHsc /DUNICODE /D_UNICODE /Fo"{0}\SeamFixTests.obj" /Fe"{0}\SeamFixTests.exe" "{1}\SeamFixTests.cpp" "{0}\D3DSeamFix.obj" /link dxguid.lib /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO' -f $output, $source),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /W4 /WX /O2 /MT /EHsc /DUNICODE /D_UNICODE /Fo"{0}\RealD3DSmoke.obj" /Fe"{0}\RealD3DSmoke.exe" "{1}\RealD3DSmoke.cpp" "{0}\D3DSeamFix.obj" /link dxguid.lib user32.lib /MACHINE:X86 /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO' -f $output, $source),
    'exit /b %errorlevel%'
)
$batchPath = Join-Path $output 'build-native.cmd'
[IO.File]::WriteAllLines($batchPath, $batch, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $batchPath
if ($LASTEXITCODE -ne 0) { throw ('Native compilation failed: ' + $LASTEXITCODE) }
& py -3.12 (Join-Path $source 'FinalizeExports.py') (Join-Path $output 'ddraw.local.dll') (Join-Path $output 'ddraw.dll') $systemDll
if ($LASTEXITCODE -ne 0) { throw ('PE export finalization failed: ' + $LASTEXITCODE) }
$metadata = [ordered]@{
    Profile='CgDeskViewportLaunch-v1'
    Architecture='x86'
    OriginalExecutableSha256='9baa0d2660f96c6699ccf42ad46bd918717b98ccb228f4edde2d92afb89d3126'
    CaptureSha256=$actualCaptureHash
    SystemDirectDrawSha256=(Get-FileHash -LiteralPath $systemDll -Algorithm SHA256).Hash.ToLowerInvariant()
    ProxySha256=(Get-FileHash -LiteralPath (Join-Path $output 'ddraw.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    SystemExports=$exportInventory
    RealGameTested=$false
}
[IO.File]::WriteAllText((Join-Path $output 'build-profile.json'), ($metadata | ConvertTo-Json -Depth 6))
$bundled = Join-Path $source '..\..\src\CgDesk\Native\Viewport'
[IO.Directory]::CreateDirectory($bundled) | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'ddraw.dll') -Destination (Join-Path $bundled 'ddraw.dll') -Force
Copy-Item -LiteralPath (Join-Path $output 'build-profile.json') -Destination (Join-Path $bundled 'build-profile.json') -Force
Write-Output ('Built configurable native viewport component: ' + (Join-Path $output 'ddraw.dll'))
Write-Output ('Built independent smoke host: ' + (Join-Path $output 'ViewportSmokeHost.exe'))
