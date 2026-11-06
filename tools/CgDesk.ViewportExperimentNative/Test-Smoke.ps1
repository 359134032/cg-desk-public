$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'bin'
$proxy = Join-Path $output 'ddraw.dll'
$hostExe = Join-Path $output 'ViewportSmokeHost.exe'
$alias = Join-Path $output 'cgdesk_viewport_sys.dll'
$localSystem = Join-Path $env:WINDIR 'SysWOW64\ddraw.dll'
if (-not (Test-Path -LiteralPath $proxy) -or -not (Test-Path -LiteralPath $hostExe)) { throw 'Build the native tool first.' }
if (Test-Path -LiteralPath $alias) {
    if ((Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $localSystem -Algorithm SHA256).Hash) {
        throw 'Existing test alias differs from the local Windows system DLL; it was not overwritten.'
    }
} else {
    Copy-Item -LiteralPath $localSystem -Destination $alias
}
$lines = @(& $hostExe $proxy $alias)
$code = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'smoke-result.txt'), [string[]]$lines)
$lines | Write-Output
if ($code -ne 0) { throw ('Isolated native smoke host failed: ' + $code) }
[IO.File]::WriteAllText((Join-Path $output 'smoke-result.json'), ([ordered]@{
    Success=$true
    TestedUtc=[DateTime]::UtcNow.ToString('O')
    HostExitCode=$code
    ProxySha256=(Get-FileHash -LiteralPath $proxy -Algorithm SHA256).Hash.ToLowerInvariant()
    SystemAliasSha256=(Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash.ToLowerInvariant()
    RealGameTested=$false
    Output=$lines
} | ConvertTo-Json -Depth 4))
