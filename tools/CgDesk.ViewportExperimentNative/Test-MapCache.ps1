param([string]$FixturePath = (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\runtime-72316-20261106-042053-1f8731d2\partial-image.exe'))
$ErrorActionPreference = 'Stop'
$fixture = [IO.Path]::GetFullPath($FixturePath)
if ((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'b95fdfaa61520908ed1a7b04fe75c74decb53a96860d17f25b8aef8a6a2b2636') { throw 'Wrong offline fixture.' }
& py -3.12 (Join-Path $PSScriptRoot 'AuditMapCache.py')
if ($LASTEXITCODE) { throw 'Map cache capture audit failed.' }
$vswhere = [Environment]::ExpandEnvironmentVariables('%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe')
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC x86 tools unavailable; nothing installed.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$output = Join-Path $PSScriptRoot 'bin\map-cache-tests'
[IO.Directory]::CreateDirectory($output) | Out-Null
$command = '@echo off' + "`r`n" + 'call "' + $vcvars + '" x86 >nul' + "`r`n" + 'if errorlevel 1 exit /b %errorlevel%' + "`r`n" +
    'cl /nologo /std:c++17 /W4 /WX /EHsc /O2 /DMAP_CACHE_TESTING /I"' + $PSScriptRoot + '" "' + (Join-Path $PSScriptRoot 'MapCacheFix.cpp') + '" "' + (Join-Path $PSScriptRoot 'MapCacheTests.cpp') + '" /Fe:"' + (Join-Path $output 'MapCacheTests.exe') + '" /Fo:"' + $output.Replace('\', '/') + '/" /link /MACHINE:X86' + "`r`n" + 'exit /b %errorlevel%' + "`r`n"
$build = Join-Path $output 'build.cmd'
[IO.File]::WriteAllText($build, $command, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $build
if ($LASTEXITCODE) { throw 'Map cache test host build failed.' }
$productionCommand = '@echo off' + "`r`n" + 'call "' + $vcvars + '" x86 >nul' + "`r`n" + 'if errorlevel 1 exit /b %errorlevel%' + "`r`n" +
    'cl /nologo /std:c++17 /W4 /WX /EHsc /O2 /c /I"' + $PSScriptRoot + '" "' + (Join-Path $PSScriptRoot 'MapCacheFix.cpp') + '" /Fo:"' + (Join-Path $output 'MapCacheFix-production.obj') + '"' + "`r`n" + 'exit /b %errorlevel%' + "`r`n"
$productionBuild = Join-Path $output 'build-production-object.cmd'
[IO.File]::WriteAllText($productionBuild, $productionCommand, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $productionBuild
if ($LASTEXITCODE) { throw 'Map cache production object compile failed.' }
# Only our host executes. It SEC_IMAGE-maps the read-only fixture and invokes
# its own synthetic callbacks. The supplied game fixture never executes.
$result = & (Join-Path $output 'MapCacheTests.exe') $fixture
$exitCode = $LASTEXITCODE
$result | Tee-Object -FilePath (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\map-cache-fixture-tests.txt')
if ($exitCode) { throw "Map cache offline test failed: $exitCode" }
