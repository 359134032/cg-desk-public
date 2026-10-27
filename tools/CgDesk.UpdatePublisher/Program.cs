using System.Security.Cryptography;
using System.Text.Json;
using CgDesk.Models;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    if (args.Length == 3 && args[0] == "keygen")
    {
        var keyPath = Path.GetFullPath(args[1]);
        var publicPath = Path.GetFullPath(args[2]);
        if (string.Equals(keyPath, publicPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Private and public outputs must be different files.");
        if (File.Exists(keyPath)) throw new InvalidOperationException("Signing key already exists; it must not be replaced accidentally.");
        if (File.Exists(publicPath) && File.ReadAllText(publicPath).Trim() != "{}") throw new InvalidOperationException("Public trust keys already exist; review key rotation first.");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicBytes = signer.ExportSubjectPublicKeyInfo();
        var keyId = Convert.ToHexString(SHA256.HashData(publicBytes))[..16].ToLowerInvariant();
        var privateBytes = signer.ExportPkcs8PrivateKey();
        try
        {
            var protectedBytes = ProtectedData.Protect(privateBytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, JsonSerializer.Serialize(new ProtectedKey(keyId, Convert.ToBase64String(protectedBytes)), json));
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        File.WriteAllText(publicPath, JsonSerializer.Serialize(new Dictionary<string, string> { [keyId] = Convert.ToBase64String(publicBytes) }, json));
        Console.WriteLine($"Created protected signing key; key ID: {keyId}. Only the public key belongs in source control.");
        return 0;
    }
    if (args.Length >= 7 && args[0] == "manifest")
    {
        var key = JsonSerializer.Deserialize<ProtectedKey>(File.ReadAllText(args[1]), json) ?? throw new InvalidDataException("Invalid protected key file.");
        var package = new FileInfo(args[2]);
        var version = ReleaseVersion.Parse(args[3]);
        var channel = args[4];
        if (channel is not ("stable" or "beta") || channel == "stable" && version.IsPrerelease) throw new InvalidDataException("Invalid channel or stable prerelease.");
        var urls = args.Skip(6).ToArray();
        if (urls.Length is < 1 or > 4 || package.Length is <= 0 or > 1024L * 1024 * 1024) throw new InvalidDataException("Invalid package size or mirror count.");
        foreach (var url in urls)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Package mirrors must be HTTPS URLs without credentials or fragments.");
        using var input = package.OpenRead();
        var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var notesPath = Path.ChangeExtension(args[5], ".notes.txt");
        var notes = File.Exists(notesPath) ? File.ReadAllText(notesPath) : $"CgDesk {args[3]}";
        if (notes.Length > 8000) throw new InvalidDataException("Release notes are too long.");
        var manifest = new UpdateManifest(1, "CgDesk", channel, args[3], DateTimeOffset.UtcNow, notes,
            [new UpdatePackage("win-x64", 19041, package.Length, digest, urls)]);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, json);
        var privateBytes = ProtectedData.Unprotect(Convert.FromBase64String(key.ProtectedPkcs8), null, DataProtectionScope.CurrentUser);
        byte[] signature;
        try
        {
            using var signer = ECDsa.Create(); signer.ImportPkcs8PrivateKey(privateBytes, out _);
            var actualId = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
            if (actualId != key.KeyId) throw new InvalidDataException("Signing key ID mismatch.");
            signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        var outputPath = Path.GetFullPath(args[5]);
        if (string.Equals(outputPath, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, package.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Manifest output cannot overwrite the signing key or package.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new UpdateEnvelope(key.KeyId, Convert.ToBase64String(payload), Convert.ToBase64String(signature)), json));
        Console.WriteLine($"Created signed {channel} manifest for {args[3]}; SHA256 {digest}; {urls.Length} mirrors.");
        return 0;
    }
    Console.Error.WriteLine("Usage:\n  keygen <protected-key-file> <public-keys-json>\n  manifest <protected-key-file> <zip> <version> <stable|beta> <output-json> <https-url> [backup-url...]");
    return 1;
}
catch (Exception error)
{
    // Do not echo keys or signed URL parameters in errors.
    Console.Error.WriteLine(error is CryptographicException ? "Signing key cannot be opened. Use the Windows user and machine that created the protected key." : error is IOException ? "Cannot read or write a release file." : "Invalid publishing arguments or signing configuration.");
    return 1;
}

sealed record ProtectedKey(string KeyId, string ProtectedPkcs8);
