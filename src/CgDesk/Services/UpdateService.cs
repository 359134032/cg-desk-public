using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CgDesk.Models;

namespace CgDesk.Services;

internal sealed record AvailableUpdate(UpdateManifest Manifest, UpdatePackage Package, string SourceHost, bool IsNewer);
internal sealed class UpdateException(string message) : Exception(message);

internal sealed class UpdateService : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int MaxManifestBytes = 256 * 1024;
    internal const long MaxPackageBytes = 1024L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string> _keys;
    private readonly TimeSpan _checkTimeout;
    public UpdateService(HttpMessageHandler? handler = null, IReadOnlyDictionary<string, string>? keys = null, TimeSpan? checkTimeout = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CgDesk/" + CurrentVersion);
        _keys = keys ?? LoadKeys();
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(10);
    }

    internal static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version!.ToString(3);
    private static Dictionary<string, string> LoadKeys()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("CgDesk.UpdateKeys.json");
        return stream == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json) ?? [];
    }

    internal static Uri PublicHttps(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.'))
            throw new UpdateException("更新地址必须是公网 HTTPS 地址，且不能包含账户信息或锚点。");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4 || bytes[0] is 0 or 10 or 127 or >= 224 || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                throw new UpdateException("更新地址不能使用本机或内网 IP。");
        }
        return uri;
    }

    public async Task<AvailableUpdate> CheckAsync(IEnumerable<string> sources, string channel, string? highestSeen, CancellationToken cancel)
    {
        var urls = sources.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(4).ToArray();
        if (urls.Length == 0) throw new UpdateException("尚未配置更新源，请先打开「更新源设置」。");
        if (channel is not ("stable" or "beta")) throw new UpdateException("更新通道无效。");
        var errors = new List<string>();
        foreach (var url in urls)
        {
            cancel.ThrowIfCancellationRequested();
            var host = "更新源";
            try
            {
                var uri = PublicHttps(url); host = uri.Host;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(_checkTimeout);
                if (GitHubApi(uri, channel) is Uri api)
                {
                    var metadata = await ReadBytesAsync(api, 1024 * 1024, timeout.Token).ConfigureAwait(false);
                    uri = GitHubManifest(metadata, channel);
                }
                var bytes = await ReadBytesAsync(uri, MaxManifestBytes, timeout.Token).ConfigureAwait(false);
                return Verify(bytes, channel, highestSeen, host);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { errors.Add(host + "：连接超时"); }
            catch (Exception error) when (error is UpdateException or HttpRequestException or IOException or JsonException or CryptographicException or FormatException or ArgumentException)
            { errors.Add(host + "：" + Reason(error)); }
        }
        throw new UpdateException(string.Join("；", errors));
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, int limit, CancellationToken cancel)
    {
        using var response = await GetAsync(uri, cancel).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > limit) throw new UpdateException("版本清单或发布信息过大。");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    internal static Uri? GitHubApi(Uri source, string channel)
    {
        var pattern = source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? @"^/([^/]+)/([^/]+)/releases(?:/latest)?/?$"
            : source.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? @"^/repos/([^/]+)/([^/]+)/releases(?:/latest)?/?$" : "";
        if (pattern.Length == 0) return null;
        var match = Regex.Match(source.AbsolutePath, pattern, RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var owner = match.Groups[1].Value; var repo = match.Groups[2].Value;
        if (!Regex.IsMatch(owner, @"\A[A-Za-z0-9_.-]+\z") || !Regex.IsMatch(repo, @"\A[A-Za-z0-9_.-]+\z")) throw new UpdateException("GitHub 仓库地址无效。");
        return new Uri($"https://api.github.com/repos/{owner}/{repo}/releases" + (channel == "beta" ? "?per_page=20" : "/latest"));
    }

    private static Uri GitHubManifest(byte[] metadata, string channel)
    {
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        var releases = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        var assetName = $"cgdesk-update-{channel}.json";
        foreach (var release in releases)
        {
            if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
            if (!release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            if (channel == "stable" && prerelease.GetBoolean()) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != assetName) continue;
                if (asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String)
                    return PublicHttps(url.GetString()!);
            }
        }
        throw new UpdateException($"没有已发布且包含 {assetName} 的 Release；草稿不会进入更新。");
    }

    internal AvailableUpdate Verify(byte[] bytes, string channel, string? highestSeen, string host)
    {
        if (bytes.Length > MaxManifestBytes) throw new UpdateException("版本清单过大。");
        var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(bytes, Json) ?? throw new UpdateException("清单格式无效。");
        if (envelope.KeyId == null || !_keys.TryGetValue(envelope.KeyId, out var publicKey)) throw new UpdateException("更新签名密钥不受信任。");
        var payload = Convert.FromBase64String(envelope.Payload ?? "");
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!verifier.VerifyData(payload, Convert.FromBase64String(envelope.Signature ?? ""), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new UpdateException("更新签名校验失败。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, Json) ?? throw new UpdateException("清单格式无效。");
        if (manifest.SchemaVersion != 1 || manifest.Product != "CgDesk" || manifest.Channel != channel)
            throw new UpdateException("清单版本、产品或通道不匹配。");
        if (manifest.PublishedAt == default || manifest.PublishedAt > DateTimeOffset.UtcNow.AddDays(1)) throw new UpdateException("发布时间无效。");
        if (manifest.Version == null || manifest.ReleaseNotes == null || manifest.ReleaseNotes.Length > 8000) throw new UpdateException("版本信息无效。");
        var version = ReleaseVersion.Parse(manifest.Version);
        if (channel == "stable" && version.IsPrerelease) throw new UpdateException("稳定通道不能发布预发布版本。");
        if (!string.IsNullOrEmpty(highestSeen) && version.CompareTo(ReleaseVersion.Parse(highestSeen)) < 0) throw new UpdateException("清单版本低于已接收版本，已拒绝回退。");
        var packages = manifest.Packages ?? [];
        var matches = packages.Where(item => item?.Rid == "win-x64").ToArray();
        if (matches.Length != 1 || !Environment.Is64BitProcess) throw new UpdateException("没有适用于当前架构的唯一安装包。");
        var package = matches[0];
        if (package.MinWindowsBuild < 19041 || package.MinWindowsBuild > Environment.OSVersion.Version.Build) throw new UpdateException("当前系统版本不满足更新要求。");
        ValidatePackage(package);
        return new AvailableUpdate(manifest, package, host, version.CompareTo(ReleaseVersion.Parse(CurrentVersion)) > 0);
    }

    internal static void ValidatePackage(UpdatePackage package)
    {
        if (package.Size is <= 0 or > MaxPackageBytes || package.Sha256 == null || !Regex.IsMatch(package.Sha256, @"\A[0-9a-fA-F]{64}\z")
            || package.Urls == null || package.Urls.Length is < 1 or > 4) throw new UpdateException("安装包信息无效。");
        foreach (var url in package.Urls) PublicHttps(url);
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, string directory, IProgress<double>? progress, CancellationToken cancel)
    {
        ValidatePackage(update.Package);
        _ = ReleaseVersion.Parse(update.Manifest.Version);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"CgDesk-{update.Manifest.Version}-win-x64.zip");
        if (File.Exists(destination) && await MatchesAsync(destination, update.Package, cancel).ConfigureAwait(false)) return destination;
        var errors = new List<string>();
        foreach (var url in update.Package.Urls)
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            cancel.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromMinutes(20));
                using var response = await GetAsync(PublicHttps(url), timeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is long length && length != update.Package.Size) throw new UpdateException("安装包大小不匹配。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long total = 0; var lastProgress = Environment.TickCount64;
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        total += count;
                        if (total > update.Package.Size) throw new UpdateException("安装包超出声明大小。");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                        if (Environment.TickCount64 - lastProgress >= 250) { progress?.Report(total * 100d / update.Package.Size); lastProgress = Environment.TickCount64; }
                    }
                    if (total != update.Package.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Package.Sha256)))
                        throw new UpdateException("安装包摘要或大小校验失败。");
                }
                File.Move(temp, destination, true);
                progress?.Report(100);
                return destination;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { errors.Add(new Uri(url).Host + "：下载超时"); }
            catch (Exception error) when (error is HttpRequestException or IOException or UpdateException or UnauthorizedAccessException)
            { errors.Add(new Uri(url).Host + "：" + Reason(error)); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        throw new UpdateException(string.Join("；", errors));
    }

    private static async Task<bool> MatchesAsync(string path, UpdatePackage package, CancellationToken cancel)
    {
        if (new FileInfo(path).Length != package.Size) return false;
        await using var file = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false), Convert.FromHexString(package.Sha256));
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancel)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null || redirect == 5) throw new UpdateException("下载重定向无效或过多。");
                if (!Uri.TryCreate(uri, location, out var next)) throw new UpdateException("下载重定向地址无效。");
                uri = PublicHttps(next.AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode; response.Dispose();
                throw new UpdateException(code is 401 or 403 or 404 ? $"HTTP {code}，请检查源是否公开可读" : $"HTTP {code}");
            }
            return response;
        }
        throw new UpdateException("重定向过多。");
    }

    private static string Reason(Exception error) => error is UpdateException ? error.Message
        : error is HttpRequestException ? "网络连接失败" : error is IOException or UnauthorizedAccessException ? "文件读写失败"
        : "清单格式或签名无效";
    public void Dispose() => _http.Dispose();
}
