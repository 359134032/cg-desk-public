param(
    [switch]$IncludeHistory,
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$rules = [ordered]@{
    'github-token' = '\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'
    'private-key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
    'aws-access-key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'literal-credential' = '(?i)\b(?:password|passwd|api[_-]?key|client[_-]?secret|access[_-]?token)["'']?\s*[=:]\s*["''][^"''\r\n]{8,}["'']'
    'bearer-credential' = '(?i)\bBearer\s+[A-Za-z0-9_./+-]{20,}'
    'personal-path' = '(?i)\b[A-Z]:[\\/]|/(?:Users|home)/[A-Za-z0-9._-]+/'
}
$textExtensions = @('.cs', '.csproj', '.xaml', '.manifest', '.json', '.md', '.txt', '.url', '.ps1', '.yml', '.yaml', '.h', '.props', '.targets', '.slnx', '.gitignore', '.conf')
$findings = [System.Collections.Generic.List[object]]::new()

function Test-Text([string]$Text, [string]$Location, [string]$Scope) {
    foreach ($rule in $rules.GetEnumerator()) {
        foreach ($match in [regex]::Matches($Text, $rule.Value)) {
            $line = 1 + ([regex]::Matches($Text.Substring(0, $match.Index), "`n")).Count
            # Never emit the matching value, including when a real credential is found.
            $findings.Add([pscustomobject]@{ Scope = $Scope; Location = $Location; Line = $line; Rule = $rule.Key })
        }
    }
}

Push-Location -LiteralPath $repoRoot
try {
    $paths = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository files.' }
    $currentCount = 0
    foreach ($path in $paths) {
        if ($path -match '(?i)(^|/)(\.env(?:\.[^/]+)?|config\.json|credentials(?:\.[^/]+)?|id_rsa|id_ed25519)(/|$)') {
            $findings.Add([pscustomobject]@{ Scope = 'current'; Location = $path; Line = 0; Rule = 'private-file' })
        }
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or [IO.Path]::GetExtension($path) -notin $textExtensions) { continue }
        Test-Text ([IO.File]::ReadAllText((Join-Path $repoRoot $path))) $path 'current'
        $currentCount++
    }
    Write-Output "Current text files scanned: $currentCount"

    if ($IncludeHistory) {
        $objects = @(git -c core.quotepath=false rev-list --objects --all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate Git history.' }
        $historyCount = 0
        foreach ($object in $objects) {
            if ($object -notmatch '^([a-f0-9]{40,64}) (.+)$') { continue }
            $id = $Matches[1]
            $path = $Matches[2]
            if ([IO.Path]::GetExtension($path) -notin $textExtensions) { continue }
            $content = (git cat-file blob $id) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'Cannot read a historical blob.' }
            Test-Text $content ($id.Substring(0, 12) + ':' + $path) 'history'
            $historyCount++
        }
        Write-Output "Reachable historical text blobs scanned: $historyCount (binary archives are not inspected)"
    }

    if ($PackagePath) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName -match '(?i)(^|/)(\.git|\.env(?:\.[^/]+)?|config\.json|credentials(?:\.[^/]+)?|id_rsa|id_ed25519)(/|$)') {
                    $findings.Add([pscustomobject]@{ Scope = 'package'; Location = $entry.FullName; Line = 0; Rule = 'private-file' })
                }
                # Inspect app-owned binaries and text; third-party runtime binaries are outside this source audit.
                if ([IO.Path]::GetExtension($entry.FullName) -in $textExtensions -or $entry.Name -in @('CgDesk.dll', 'CgDesk.pdb', 'ddraw-proxy.dll', 'enhance-inject.exe')) {
                    $stream = $entry.Open()
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
                    try { Test-Text ($reader.ReadToEnd()) $entry.FullName 'package' }
                    finally { $reader.Dispose(); $stream.Dispose() }
                    if ($entry.Name -in @('CgDesk.dll', 'CgDesk.pdb', 'ddraw-proxy.dll', 'enhance-inject.exe')) {
                        $stream = $entry.Open()
                        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::Unicode, $false)
                        try { Test-Text ($reader.ReadToEnd()) $entry.FullName 'package-utf16' }
                        finally { $reader.Dispose(); $stream.Dispose() }
                    }
                }
            }
            Write-Output "Package entries checked: $($zip.Entries.Count)"
        }
        finally { $zip.Dispose() }
    }

    if ($findings.Count -gt 0) {
        $findings | Format-Table -AutoSize | Out-String -Width 240 | Write-Output
        Write-Output "Privacy findings: $($findings.Count). Review required; values are redacted."
        exit 1
    }
    Write-Output 'PASS: No configured privacy rules matched. This is a bounded pattern scan, not a guarantee that every secret is detectable.'
}
finally { Pop-Location }
