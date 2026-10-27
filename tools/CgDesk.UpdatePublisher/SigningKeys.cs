using System.Security.Cryptography;
using System.Text.Json;

internal static class SigningKeys
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Id(ECDsa signer) => Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    public static ECDsa Open(string path)
    {
        var key = JsonSerializer.Deserialize<ProtectedKey>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid protected key file.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(key.ProtectedPkcs8), null, DataProtectionScope.CurrentUser);
        var signer = ECDsa.Create();
        try
        {
            signer.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length || Id(signer) != key.KeyId) throw new InvalidDataException("Signing key ID mismatch.");
            return signer;
        }
        catch { signer.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void RequireTrusted(ECDsa signer, string publicPath)
    {
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(publicPath), Json);
        if (keys == null || !keys.TryGetValue(Id(signer), out var value)
            || !Convert.FromBase64String(value).AsSpan().SequenceEqual(signer.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("This key does not match the client's trusted public keys. Do not generate a replacement for an existing release.");
    }

    public static void Save(ECDsa signer, string path)
    {
        var bytes = signer.ExportPkcs8PrivateKey();
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(new ProtectedKey(Id(signer), Convert.ToBase64String(protectedBytes)), Json));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void EnsureNew(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("Output already exists; choose a new path to avoid overwriting a key or backup.");
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
    }

    public static char[] ReadPassword(bool fromStdin, bool confirm)
    {
        if (Console.IsInputRedirected && !fromStdin) throw new InvalidOperationException("Use an interactive terminal, or explicitly use --password-stdin with a secure input pipe.");
        var first = ReadOne(fromStdin, "Backup password: ");
        try
        {
            if (first.Length == 0 || confirm && first.Length < 12) throw new InvalidOperationException("Use a backup password of at least 12 characters.");
            if (confirm && !fromStdin)
            {
                var second = ReadOne(false, "Confirm password: ");
                try { if (!first.AsSpan().SequenceEqual(second)) throw new InvalidOperationException("Passwords do not match."); }
                finally { Array.Clear(second); }
            }
            return first;
        }
        catch { Array.Clear(first); throw; }
    }

    private static char[] ReadOne(bool fromStdin, string prompt)
    {
        var buffer = new char[1024];
        try
        {
            if (!fromStdin) Console.Error.Write(prompt);
            var length = 0;
            while (true)
            {
                char value;
                if (fromStdin)
                {
                    var next = Console.In.Read();
                    if (next == -1 || next == '\n') break;
                    value = (char)next;
                    if (value == '\r') continue;
                }
                else
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); break; }
                    if (key.Key == ConsoleKey.Escape) throw new InvalidOperationException("Password entry cancelled.");
                    if (key.Key == ConsoleKey.Backspace) { if (length > 0) buffer[--length] = '\0'; continue; }
                    value = key.KeyChar;
                    if (char.IsControl(value)) continue;
                }
                if (length == buffer.Length) throw new InvalidOperationException("Password is too long.");
                buffer[length++] = value;
            }
            return buffer[..length];
        }
        finally { Array.Clear(buffer); }
    }

    private sealed record ProtectedKey(string KeyId, string ProtectedPkcs8);
}
