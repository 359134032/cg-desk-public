# Run with PowerShell 7: pwsh -File scripts/Test-UpdatePublisher.ps1
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tools/CgDesk.UpdatePublisher'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publisher build failed.' }
$publisher = Join-Path $project 'bin/Release/net10.0-windows/CgDesk.UpdatePublisher.dll'
$scratch = Join-Path $repoRoot ('artifacts/signing-tests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$script:checks = 0

function Invoke-Publisher([string[]]$Arguments, [string]$InputLine = '', [bool]$Success = $true) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add($publisher)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine($InputLine)
        $process.StandardInput.Close()
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Publisher timed out.' }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if (($process.ExitCode -eq 0) -ne $Success) { throw ('Unexpected publisher result: ' + $output) }
        if ($InputLine.Length -gt 0 -and $output.Contains($InputLine)) { throw 'Publisher echoed input.' }
        $script:checks++
    }
    finally { $process.Dispose() }
}

# All keys and passwords below are disposable test fixtures, never the publisher's real key.
$key = Join-Path $scratch 'original.dpapi'
$public = Join-Path $scratch 'public.json'
$backup = Join-Path $scratch 'backup.p8'
$restored = Join-Path $scratch 'restored.dpapi'
$passphrase = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
Invoke-Publisher @('keygen', $key, $public)
Invoke-Publisher @('check-key', $key, $public)
Invoke-Publisher @('export-key', $key, $backup, $public, '--password-stdin') $passphrase
Invoke-Publisher @('import-key', $backup, $restored, $public, '--password-stdin') $passphrase
Invoke-Publisher @('check-key', $restored, $public)
$before = [IO.File]::ReadAllText($restored)
Invoke-Publisher @('import-key', $backup, $restored, $public, '--password-stdin') $passphrase $false
if ([IO.File]::ReadAllText($restored) -ne $before) { throw 'Import overwrote the existing key.' }
Invoke-Publisher @('export-key', $key, $backup, $public, '--password-stdin') $passphrase $false
Invoke-Publisher @('keygen', $key, $public) '' $false
$rejected = Join-Path $scratch 'rejected.dpapi'
Invoke-Publisher @('import-key', $backup, $rejected, $public, '--password-stdin') ($passphrase + 'wrong') $false
if (Test-Path -LiteralPath $rejected) { throw 'Wrong password created an output.' }
$otherKey = Join-Path $scratch 'other.dpapi'
$otherPublic = Join-Path $scratch 'other-public.json'
Invoke-Publisher @('keygen', $otherKey, $otherPublic)
Invoke-Publisher @('check-key', $key, $otherPublic) '' $false
Invoke-Publisher @('import-key', $backup, $rejected, $otherPublic, '--password-stdin') $passphrase $false
if (Test-Path -LiteralPath $rejected) { throw 'Untrusted key created an output.' }
$shortBackup = Join-Path $scratch 'short.p8'
Invoke-Publisher @('export-key', $key, $shortBackup, $public, '--password-stdin') 'short' $false
if (Test-Path -LiteralPath $shortBackup) { throw 'Weak password created a backup.' }
Invoke-Publisher @('export-key', $key, $shortBackup, $public) $passphrase $false
Invoke-Publisher @('check-key', (Join-Path $scratch 'missing.dpapi'), $public) '' $false
$damaged = [IO.File]::ReadAllBytes($backup)
$damaged[0] = $damaged[0] -bxor 1
$damagedPath = Join-Path $scratch 'damaged.p8'
[IO.File]::WriteAllBytes($damagedPath, $damaged)
Invoke-Publisher @('import-key', $damagedPath, $rejected, $public, '--password-stdin') $passphrase $false
if (Test-Path -LiteralPath $rejected) { throw 'Damaged backup created an output.' }

$package = Join-Path $scratch 'fixture.zip'
[IO.File]::WriteAllBytes($package, [Text.Encoding]::UTF8.GetBytes('Disposable update package fixture'))
foreach ($channel in @('stable', 'beta')) {
    $manifest = Join-Path $scratch ($channel + '.json')
    Invoke-Publisher @('manifest', $restored, $package, '99.0.0', $channel, $manifest, 'https://example.com/fixture.zip')
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
public static class SigningMigrationVerifier
{
    public static void Verify(string publicFile, string manifestFile)
    {
        using var keys = JsonDocument.Parse(File.ReadAllText(publicFile));
        using var envelope = JsonDocument.Parse(File.ReadAllText(manifestFile));
        var root = envelope.RootElement;
        var publicBytes = Convert.FromBase64String(keys.RootElement.GetProperty(root.GetProperty("keyId").GetString()).GetString());
        using var signer = ECDsa.Create();
        signer.ImportSubjectPublicKeyInfo(publicBytes, out _);
        if (!signer.VerifyData(Convert.FromBase64String(root.GetProperty("payload").GetString()),
            Convert.FromBase64String(root.GetProperty("signature").GetString()), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new Exception("Restored key signature was not trusted by the original public key.");
    }
}
'@
foreach ($channel in @('stable', 'beta')) {
    [SigningMigrationVerifier]::Verify($public, (Join-Path $scratch ($channel + '.json')))
    $script:checks++
}
Write-Output "PASS: $script:checks signing migration checks, including original-public-key verification of restored-key manifests."
