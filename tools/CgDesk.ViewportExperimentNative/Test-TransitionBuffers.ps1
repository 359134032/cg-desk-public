param([string]$FixturePath = (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\runtime-72316-20261106-042053-1f8731d2\partial-image.exe'))
$ErrorActionPreference = 'Stop'
$fixture = [IO.Path]::GetFullPath($FixturePath)
if ((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'b95fdfaa61520908ed1a7b04fe75c74decb53a96860d17f25b8aef8a6a2b2636') { throw 'Wrong offline fixture.' }
& py -3.12 (Join-Path $PSScriptRoot 'AuditTransitionBuffers.py')
if ($LASTEXITCODE) { throw 'Transition buffer capture audit failed.' }
$vswhere = [Environment]::ExpandEnvironmentVariables('%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe')
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC x86 tools unavailable; nothing installed.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$output = Join-Path $PSScriptRoot 'bin\transition-buffer-tests'
[IO.Directory]::CreateDirectory($output) | Out-Null
$command = '@echo off' + "`r`n" + 'call "' + $vcvars + '" x86 >nul' + "`r`n" + 'if errorlevel 1 exit /b %errorlevel%' + "`r`n" +
    'cl /nologo /std:c++17 /W4 /WX /EHsc /O2 /DTRANSITION_BUFFER_TESTING /I"' + $PSScriptRoot + '" "' + (Join-Path $PSScriptRoot 'TransitionBufferFix.cpp') + '" "' + (Join-Path $PSScriptRoot 'TransitionBufferTests.cpp') + '" /Fe:"' + (Join-Path $output 'TransitionBufferTests.exe') + '" /Fo:"' + $output.Replace('\', '/') + '/" /link /MACHINE:X86' + "`r`n" + 'exit /b %errorlevel%' + "`r`n"
$build = Join-Path $output 'build.cmd'
[IO.File]::WriteAllText($build, $command, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $build
if ($LASTEXITCODE) { throw 'Transition buffer test build failed.' }
$result = & (Join-Path $output 'TransitionBufferTests.exe') $fixture
$exitCode = $LASTEXITCODE
$result | Tee-Object -FilePath (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\transition-buffer-fixture-tests.txt')
if ($exitCode) { throw "Transition buffer offline test failed: $exitCode" }
