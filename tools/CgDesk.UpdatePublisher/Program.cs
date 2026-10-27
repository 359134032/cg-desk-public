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
        SigningKeys.Save(signer, keyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        File.WriteAllText(publicPath, JsonSerializer.Serialize(new Dictionary<string, string> { [keyId] = Convert.ToBase64String(publicBytes) }, json));
        Console.WriteLine($"Created protected signing key; key ID: {keyId}. Only the public key belongs in source control.");
        return 0;
    }
    if (args.Length == 3 && args[0] == "check-key")
    {
        using var signer = SigningKeys.Open(args[1]);
        SigningKeys.RequireTrusted(signer, args[2]);
        Console.WriteLine($"Signing key is usable and matches the client's public key; key ID: {SigningKeys.Id(signer)}.");
        return 0;
    }
    if ((args.Length == 3 || args.Length == 4 && args[3] == "--password-stdin") && args[0] == "check-portable-key")
    {
        using var signer = SigningKeys.OpenPortable(args[1], args[2], args.Length == 4);
        Console.WriteLine($"Portable key is usable and matches the client's public key; key ID: {SigningKeys.Id(signer)}. No machine-bound key file was created.");
        return 0;
    }
    if ((args.Length == 4 || args.Length == 5 && args[4] == "--password-stdin") && args[0] is "export-key" or "import-key")
    {
        SigningKeys.EnsureNew(args[2]);
        var fromStdin = args.Length == 5;
        if (args[0] == "export-key")
        {
            using var signer = SigningKeys.Open(args[1]);
            SigningKeys.RequireTrusted(signer, args[3]);
            var password = SigningKeys.ReadPassword(fromStdin, confirm: true);
            try
            {
                var encrypted = signer.ExportEncryptedPkcs8PrivateKey(password,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
                SigningKeys.WriteNew(args[2], encrypted);
            }
            finally { Array.Clear(password); }
            Console.WriteLine($"Created password-encrypted portable backup; key ID: {SigningKeys.Id(signer)}. Keep the password separately and test restoration.");
        }
        else
        {
            using var signer = SigningKeys.OpenPortable(args[1], args[3], fromStdin);
            SigningKeys.Save(signer, args[2]);
            Console.WriteLine($"Imported signing key protected for this Windows user; key ID: {SigningKeys.Id(signer)}. Client public keys were preserved.");
        }
        return 0;
    }
    string? portablePublicPath = null;
    var passwordFromStdin = false;
    if (args.Length >= 8 && args[0] == "manifest-portable")
    {
        portablePublicPath = args[2];
        passwordFromStdin = args[^1] == "--password-stdin";
        if (passwordFromStdin) args = args[..^1];
        args = ["manifest", args[1], .. args.Skip(3)];
    }
    if (args.Length >= 7 && args[0] == "manifest")
    {
        using var signer = portablePublicPath == null ? SigningKeys.Open(args[1])
            : SigningKeys.OpenPortable(args[1], portablePublicPath, passwordFromStdin);
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
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var outputPath = Path.GetFullPath(args[5]);
        if (string.Equals(outputPath, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase)
            || portablePublicPath != null && string.Equals(outputPath, Path.GetFullPath(portablePublicPath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, package.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Manifest output cannot overwrite the signing key or package.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new UpdateEnvelope(SigningKeys.Id(signer), Convert.ToBase64String(payload), Convert.ToBase64String(signature)), json));
        Console.WriteLine($"Created signed {channel} manifest for {args[3]}; SHA256 {digest}; {urls.Length} mirrors.");
        return 0;
    }
    Console.Error.WriteLine("Usage:\n  keygen <protected-key-file> <public-keys-json>\n  check-key <protected-key-file> <public-keys-json>\n  check-portable-key <encrypted-key.p8> <public-keys-json> [--password-stdin]\n  export-key <protected-key-file> <encrypted-backup.p8> <public-keys-json> [--password-stdin]\n  import-key <encrypted-backup.p8> <new-protected-key-file> <public-keys-json> [--password-stdin]\n  manifest <protected-key-file> <zip> <version> <stable|beta> <output-json> <https-url> [backup-url...]\n  manifest-portable <encrypted-key.p8> <public-keys-json> <zip> <version> <stable|beta> <output-json> <https-url> [backup-url...] [--password-stdin]\nPasswords are prompted without echo; never pass a password as a command-line argument.");
    return 1;
}
catch (Exception error)
{
    // Do not echo keys or signed URL parameters in errors.
    Console.Error.WriteLine(error switch
    {
        CryptographicException => "Cannot decrypt signing key or backup. A DPAPI key requires its original Windows user and machine; a portable backup requires the correct password.",
        FileNotFoundException => "Signing input file is missing. Restore the existing key from an encrypted backup, or sign on the original machine. Do not replace the client's public key.",
        IOException => "Cannot read or write a release file; check paths and whether the output already exists.",
        InvalidOperationException or InvalidDataException => error.Message,
        _ => "Invalid publishing arguments or signing configuration."
    });
    return 1;
}
