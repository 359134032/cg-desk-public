param([string]$FixturePath = (Join-Path $PSScriptRoot '..\..\artifacts\viewport-research\runtime-72316-20261106-042053-1f8731d2\partial-image.exe'))
$ErrorActionPreference='Stop'
$fixture=[IO.Path]::GetFullPath($FixturePath)
if((Get-FileHash -LiteralPath $fixture).Hash.ToLowerInvariant() -ne 'b95fdfaa61520908ed1a7b04fe75c74decb53a96860d17f25b8aef8a6a2b2636'){throw 'Wrong offline fixture'}
$output=Join-Path $PSScriptRoot 'bin/battle-layout-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$audit=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../artifacts/viewport-research/battle-geometry-independent-audit.json') -Raw | ConvertFrom-Json
$lines=@('#pragma once','static const int sourceRows[22][4]={')
foreach($row in $audit.source_rows){$lines+='    {'+($row -join ',')+'},'}
$lines+='};';$lines+='static const int native800[22][4]={'
foreach($row in $audit.original_800x600){$lines+='    {'+($row.points -join ',')+'},'}
$lines+='};';[IO.File]::WriteAllLines((Join-Path $output 'BattleFixture.h'),$lines,[Text.Encoding]::ASCII)
$vswhere=[Environment]::ExpandEnvironmentVariables('%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe')
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'Existing MSVC x86 tools required'}
$vcvars=Join-Path $installation 'VC/Auxiliary/Build/vcvarsall.bat'
$command='@echo off'+"`r`n"+'call "'+$vcvars+'" x86 >nul'+"`r`n"+'if errorlevel 1 exit /b %errorlevel%'+"`r`n"+
 'cl /nologo /std:c++17 /W4 /WX /EHsc /O2 /DBATTLE_LAYOUT_TESTING /DTRANSITION_BUFFER_TESTING /DTRANSITION_SPEED_TESTING /DMAP_CACHE_TESTING /I"'+$PSScriptRoot+'" /I"'+$output+'" "'+(Join-Path $PSScriptRoot 'BattleLayoutFix.cpp')+'" "'+(Join-Path $PSScriptRoot 'TransitionBufferFix.cpp')+'" "'+(Join-Path $PSScriptRoot 'TransitionSpeedFix.cpp')+'" "'+(Join-Path $PSScriptRoot 'MapCacheFix.cpp')+'" "'+(Join-Path $PSScriptRoot 'BattleLayoutTests.cpp')+'" /Fe:"'+(Join-Path $output 'BattleLayoutTests.exe')+'" /Fo:"'+$output.Replace('\','/')+'/" /link /MACHINE:X86'+"`r`n"+'exit /b %errorlevel%'+"`r`n"
$build=Join-Path $output 'build.cmd';[IO.File]::WriteAllText($build,$command,[Text.Encoding]::ASCII)
& $env:ComSpec /d /c $build
if($LASTEXITCODE){throw 'Battle fixture build failed'}
& (Join-Path $output 'BattleLayoutTests.exe') $fixture
if($LASTEXITCODE){throw 'Battle fixture failed'}
