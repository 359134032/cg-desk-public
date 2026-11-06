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
$textExtensions = @('.cs', '.csproj', '.xaml', '.manifest', '.json', '.md', '.txt', '.url', '.ps1', '.yml', '.yaml', '.h', '.c', '.cpp', '.py', '.def', '.props', '.targets', '.slnx', '.gitignore', '.conf')
$findings = [System.Collections.Generic.List[object]]::new()

function Get-BinaryPathBytes([byte[]]$Data) {
    # Managed metadata table rows contain numeric indices, not path strings.
    # Preserve string/user-string/blob heaps, IL, resources and debug records.
    $masked = [byte[]]$Data.Clone()
    $stream = $null
    $pe = $null
    try {
        $stream = [IO.MemoryStream]::new($Data, $false)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        if (!$pe.HasMetadata) { return ,$Data }
        $rva = $pe.PEHeaders.CorHeader.MetadataDirectory.RelativeVirtualAddress
        $metadata = -1
        foreach ($section in $pe.PEHeaders.SectionHeaders) {
            if ($rva -ge $section.VirtualAddress -and $rva -lt $section.VirtualAddress + $section.SizeOfRawData) {
                $metadata = $section.PointerToRawData + $rva - $section.VirtualAddress
                break
            }
        }
        if ($metadata -lt 0 -or [BitConverter]::ToUInt32($Data, $metadata) -ne 0x424A5342) { return ,$Data }
        $cursor = $metadata + 16 + [BitConverter]::ToInt32($Data, $metadata + 12)
        $count = [BitConverter]::ToUInt16($Data, $cursor + 2)
        $cursor += 4
        for ($index = 0; $index -lt $count; $index++) {
            $start = $metadata + [BitConverter]::ToInt32($Data, $cursor)
            $size = [BitConverter]::ToInt32($Data, $cursor + 4)
            $nameStart = $cursor + 8
            $end = $nameStart
            while ($Data[$end] -ne 0) { $end++ }
            $name = [Text.Encoding]::ASCII.GetString($Data, $nameStart, $end - $nameStart)
            if ($name -in @('#~', '#-')) {
                if ($start -lt 0 -or $size -lt 0 -or $start + [long]$size -gt $Data.Length) { return ,$Data }
                [Array]::Clear($masked, $start, $size)
            }
            $cursor = $nameStart + [int]([Math]::Ceiling(($end - $nameStart + 1) / 4.0) * 4)
        }
        return ,$masked
    }
    catch { return ,$Data } # Unsupported/malformed files keep the full scan.
    finally { if ($pe) { $pe.Dispose() }; if ($stream) { $stream.Dispose() } }
}

function Test-Text([string]$Text, [string]$Location, [string]$Scope, [string]$PathText = $null) {
    foreach ($rule in $rules.GetEnumerator()) {
        $scanText = if ($rule.Key -eq 'personal-path' -and $PathText) { $PathText } else { $Text }
        foreach ($match in [regex]::Matches($scanText, $rule.Value)) {
            if ($rule.Key -eq 'personal-path' -and [IO.Path]::GetExtension($Location) -in @('.dll', '.exe', '.pdb')) {
                # Arbitrary PE/resource bytes can resemble a drive prefix. A
                # binary path finding must also contain a readable path component.
                # Text-source rules remain unchanged, including bare drive roots.
                $tail = $scanText.Substring($match.Index, [Math]::Min(512, $scanText.Length - $match.Index))
                if ($match.Value -match '^[A-Za-z]:[\\/]' -and $tail -notmatch '^[A-Za-z]:[\\/][\p{L}\p{N}_ .()@+\-\[\]]{2,}') { continue }
            }
            $line = 1 + ([regex]::Matches($scanText.Substring(0, $match.Index), "`n")).Count
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
                if ([IO.Path]::GetExtension($entry.FullName) -in $textExtensions -or $entry.Name -in @('CgDesk.dll', 'CgDesk.pdb', 'ddraw.dll')) {
                    $stream = $entry.Open()
                    $buffer = [IO.MemoryStream]::new()
                    try { $stream.CopyTo($buffer); $data = $buffer.ToArray() }
                    finally { $buffer.Dispose(); $stream.Dispose() }
                    $binary = $entry.Name -in @('CgDesk.dll', 'CgDesk.pdb', 'ddraw.dll')
                    $pathData = if ($binary) { Get-BinaryPathBytes $data } else { $data }
                    Test-Text ([Text.Encoding]::UTF8.GetString($data)) $entry.FullName 'package' ([Text.Encoding]::UTF8.GetString($pathData))
                    if ($binary) {
                        Test-Text ([Text.Encoding]::Unicode.GetString($data)) $entry.FullName 'package-utf16' ([Text.Encoding]::Unicode.GetString($pathData))
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
