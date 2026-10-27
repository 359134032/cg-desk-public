using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CgDesk;
using CgDesk.Models;
using CgDesk.Services;

internal static partial class Program
{
    private static void TestUpdates(App app)
    {
        var firstRun = new AppConfig(); UpdateSourceDefaults.Apply(firstRun);
        Require(firstRun.UpdateManifestUrl == UpdateSourceDefaults.Official, "A new installation selects the public official release source.");
        var legacySource = new AppConfig { UpdateManifestUrl = "https://github.com/359134032/cg-desk/releases", UpdateChecksEnabled = false, NextUpdateCheckUtc = DateTimeOffset.MaxValue };
        UpdateSourceDefaults.Apply(legacySource);
        Require(legacySource.UpdateManifestUrl == UpdateSourceDefaults.Official && !legacySource.UpdateChecksEnabled && legacySource.NextUpdateCheckUtc == default,
            "Legacy private-source settings migrate without re-enabling disabled checks.");
        var customSource = new AppConfig { UpdateManifestUrl = "https://custom.example.com/stable.json", UpdateBackupManifestUrl = "https://backup.example.com/stable.json" };
        UpdateSourceDefaults.Apply(customSource);
        Require(customSource.UpdateManifestUrl == "https://custom.example.com/stable.json" && customSource.UpdateBackupManifestUrl == "https://backup.example.com/stable.json", "User-configured mirrors survive migration.");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test-key"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) };
        var packageBytes = Encoding.UTF8.GetBytes("CgDesk update integrity test fixture");
        var package = new UpdatePackage("win-x64", 19041, packageBytes.Length, Convert.ToHexString(SHA256.HashData(packageBytes)),
            ["https://first.example.com/update.zip", "https://second.example.com/update.zip"]);
        var manifest = new UpdateManifest(1, "CgDesk", "stable", "99.0.0", DateTimeOffset.UtcNow, "回归测试更新说明", [package]);
        byte[] Signed(UpdateManifest value, string keyId = "test-key")
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, UpdateService.Json);
            return JsonSerializer.SerializeToUtf8Bytes(new UpdateEnvelope(keyId, Convert.ToBase64String(payload),
                Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))), UpdateService.Json);
        }
        static HttpResponseMessage Response(byte[] bytes, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new ByteArrayContent(bytes) };
        using var verifier = new UpdateService(new UpdateHandler((_, _) => Task.FromResult(Response([]))), keys);
        var signed = Signed(manifest);
        Require(verifier.Verify(signed, "stable", null, "fixture").IsNewer, "A valid publisher signature offers a newer compatible version.");
        Require(!verifier.Verify(Signed(manifest with { Version = UpdateService.CurrentVersion }), "stable", null, "fixture").IsNewer, "An equal version produces no update notification.");
        Require(ReleaseVersion.Parse("1.10.0").CompareTo(ReleaseVersion.Parse("1.9.9")) > 0
            && ReleaseVersion.Parse("2.0.0-beta.10").CompareTo(ReleaseVersion.Parse("2.0.0-beta.2")) > 0
            && ReleaseVersion.Parse("2.0.0").CompareTo(ReleaseVersion.Parse("2.0.0-rc.1")) > 0, "Version comparison follows numeric and prerelease precedence.");
        RejectUpdate(() => ReleaseVersion.Parse("1.0.0-beta.01"), "Invalid numeric prerelease identifiers are rejected.");
        var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(signed, UpdateService.Json)!;
        var changed = Convert.FromBase64String(envelope.Payload); changed[0] ^= 1;
        RejectUpdate(() => verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(changed) }, UpdateService.Json), "stable", null, "fixture"), "Tampered manifest payloads fail signature verification.");
        RejectUpdate(() => verifier.Verify(Signed(manifest, "unknown"), "stable", null, "fixture"), "Custom sources cannot replace publisher trust keys.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Product = "Other" }), "stable", null, "fixture"), "A correctly signed manifest for another product is rejected.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { SchemaVersion = 2 }), "stable", null, "fixture"), "Unknown manifest schemas are rejected.");
        RejectUpdate(() => verifier.Verify(signed, "beta", null, "fixture"), "Stable and beta channel manifests cannot be mixed.");
        RejectUpdate(() => verifier.Verify(signed, "stable", "100.0.0", "fixture"), "A mirror cannot replay a version below the accepted high-water mark.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Version = "99.0.0-beta.1" }), "stable", null, "fixture"), "Prereleases cannot be offered to stable users.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Packages = [package with { Rid = "win-arm64" }] }), "stable", null, "fixture"), "Wrong-architecture packages are rejected.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Packages = [package, package] }), "stable", null, "fixture"), "Ambiguous architecture packages are rejected.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Packages = [package with { MinWindowsBuild = int.MaxValue }] }), "stable", null, "fixture"), "Incompatible Windows requirements are rejected.");
        RejectUpdate(() => verifier.Verify(Signed(manifest with { Packages = [package with { Size = UpdateService.MaxPackageBytes + 1 }] }), "stable", null, "fixture"), "Oversized declared packages are rejected.");
        RejectUpdate(() => verifier.Verify(new byte[UpdateService.MaxManifestBytes + 1], "stable", null, "fixture"), "Oversized manifests are rejected before parsing.");
        foreach (var url in new[] { "http://updates.example.com/a", "https://user:pass@updates.example.com/a", "https://127.0.0.1/a", "https://192.168.1.2/a", "https://localhost/a", "https://updates.local/a", "file:///update.json" })
            RejectUpdate(() => UpdateService.PublicHttps(url), "Unsafe update address is rejected: " + (url.StartsWith("http:") ? "HTTP" : "local or credentialed URL"));

        var primary = "https://primary.example.com/stable.json?credential=redacted-fixture";
        var backup = "https://backup.example.com/stable.json";
        var githubPage = "https://github.com/359134032/cg-desk-public/releases";
        var githubAsset = "https://github.com/359134032/cg-desk-public/releases/download/v99.0.0/cgdesk-update-stable.json";
        var releaseMetadata = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, assets = new[] { new { name = "cgdesk-update-stable.json", browser_download_url = githubAsset } } });
        var githubHandler = new UpdateHandler((request, _) => Task.FromResult(Response(request.RequestUri!.Host == "api.github.com" ? releaseMetadata : signed)));
        using (var service = new UpdateService(githubHandler, keys))
        {
            Require(service.CheckAsync([githubPage], "stable", null, default).GetAwaiter().GetResult().IsNewer && githubHandler.Requests.Count == 2, "A GitHub Releases source discovers and verifies its signed manifest asset.");
            Require(githubHandler.Requests[0].AbsolutePath.EndsWith("/releases/latest"), "Stable GitHub detection uses the latest published release API.");
        }
        var draftMetadata = JsonSerializer.SerializeToUtf8Bytes(new { draft = true, prerelease = false, assets = new[] { new { name = "cgdesk-update-stable.json", browser_download_url = githubAsset } } });
        using (var service = new UpdateService(new UpdateHandler((_, _) => Task.FromResult(Response(draftMetadata))), keys))
            RejectUpdate(() => service.CheckAsync([githubPage], "stable", null, default).GetAwaiter().GetResult(), "GitHub draft releases never enter client updates.");
        Require(UpdateService.GitHubApi(new Uri(githubPage), "beta")!.Query == "?per_page=20", "Beta GitHub detection searches published prereleases separately.");
        var fallbackHandler = new UpdateHandler((request, _) => Task.FromResult(request.RequestUri!.Host.StartsWith("primary")
            ? Response([], HttpStatusCode.Forbidden) : Response(signed)));
        using (var service = new UpdateService(fallbackHandler, keys))
        {
            var result = service.CheckAsync([primary, backup], "stable", null, default).GetAwaiter().GetResult();
            Require(result.SourceHost == "backup.example.com" && fallbackHandler.Requests.Count == 2, "Unavailable private or blocked primary sources fall back to the backup.");
            try { service.CheckAsync([primary], "stable", null, default).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure."); }
            catch (UpdateException error) { Require(error.Message.Contains("403") && !error.Message.Contains("redacted-fixture"), "Network errors never expose URL query parameters."); }
        }
        var timeoutHandler = new UpdateHandler(async (request, cancel) =>
        {
            if (request.RequestUri!.Host.StartsWith("primary")) await Task.Delay(Timeout.Infinite, cancel);
            return Response(signed);
        });
        using (var service = new UpdateService(timeoutHandler, keys, TimeSpan.FromMilliseconds(50)))
            Require(service.CheckAsync([primary, backup], "stable", null, default).GetAwaiter().GetResult().SourceHost == "backup.example.com", "A stalled manifest source times out and falls back.");
        var redirectHandler = new UpdateHandler((_, _) => { var response = Response([], HttpStatusCode.Redirect); response.Headers.Location = new Uri("http://downgrade.example.com/a"); return Task.FromResult(response); });
        using (var service = new UpdateService(redirectHandler, keys))
            RejectUpdate(() => service.CheckAsync([primary], "stable", null, default).GetAwaiter().GetResult(), "HTTPS redirects cannot downgrade to HTTP.");
        var oversizedHandler = new UpdateHandler((_, _) => Task.FromResult(Response(new byte[UpdateService.MaxManifestBytes + 1])));
        using (var service = new UpdateService(oversizedHandler, keys))
            RejectUpdate(() => service.CheckAsync([primary], "stable", null, default).GetAwaiter().GetResult(), "Streaming manifest reads enforce the size limit.");

        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qa", "updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var downloadHandler = new UpdateHandler((request, _) => Task.FromResult(Response(request.RequestUri!.Host.StartsWith("first") ? new byte[packageBytes.Length] : packageBytes)));
            using (var service = new UpdateService(downloadHandler, keys))
            {
                var update = verifier.Verify(signed, "stable", null, "fixture");
                var path = service.DownloadAsync(update, folder, null, default).GetAwaiter().GetResult();
                Require(File.ReadAllBytes(path).SequenceEqual(packageBytes) && downloadHandler.Requests.Count == 2, "A corrupt download is discarded and recovered from a verified mirror.");
                Require(!Directory.EnumerateFiles(folder, "*.partial").Any(), "Failed download attempts leave no partial files.");
                service.DownloadAsync(update, folder, null, default).GetAwaiter().GetResult();
                Require(downloadHandler.Requests.Count == 2, "A cached package is reused only after its size and hash pass again.");
                File.Delete(path);
            }
            var badLengthHandler = new UpdateHandler((_, _) => Task.FromResult(Response([1, 2, 3])));
            using (var service = new UpdateService(badLengthHandler, keys))
                RejectUpdate(() => service.DownloadAsync(verifier.Verify(signed, "stable", null, "fixture"), folder, null, default).GetAwaiter().GetResult(), "Truncated packages never become installable files.");
            var cancelledHandler = new UpdateHandler(async (_, cancel) => { await Task.Delay(Timeout.Infinite, cancel); return Response([]); });
            using (var service = new UpdateService(cancelledHandler, keys))
            using (var cancellation = new CancellationTokenSource(50))
            {
                try { service.DownloadAsync(verifier.Verify(signed, "stable", null, "fixture"), folder, null, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected cancellation."); }
                catch (OperationCanceledException) { Require(!Directory.EnumerateFiles(folder).Any(), "Cancelling a download removes partial files and never publishes a ZIP."); }
            }
            var blockedStream = new BlockingUpdateStream();
            using (var service = new UpdateService(new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blockedStream) })), keys))
            using (var cancellation = new CancellationTokenSource(100))
            {
                try { service.DownloadAsync(verifier.Verify(signed, "stable", null, "fixture"), folder, null, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected cancellation."); }
                catch (OperationCanceledException) { Require(blockedStream.Emitted && !Directory.EnumerateFiles(folder).Any(), "Cancelling a partially streamed ZIP deletes its temporary file."); }
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(folder);
            var qaRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qa")) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(qaRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected update fixture directory.");
            Directory.Delete(absolute, true);
        }

        var uiHandler = new UpdateHandler((_, _) => Task.FromResult(Response(signed)));
        var main = new MainWindow(new AppConfig { HotkeysEnabled = false, UpdateManifestUrl = primary }, initializeEnhancement: false,
            windowFilter: _ => false, persistConfiguration: false, updateService: new UpdateService(uiHandler, keys));
        app.MainWindow = main; Field(main, "_guideStarted", true);
        try
        {
            main.Show(); Pump();
            Require(uiHandler.Requests.Count == 0, "Regression desks never contact update sources automatically.");
            var check = (Task)Call(main, "CheckForUpdatesAsync", true)!;
            Wait(() => check.IsCompleted, "Manual update check completes without blocking the UI.");
            check.GetAwaiter().GetResult();
            Require(((Border)main.FindName("UpdateBar")).IsVisible && ((TextBlock)main.FindName("UpdateNoticeText")).Text.Contains("99.0.0"), "New versions appear in a persistent update banner.");
            Require(main.Config.HighestUpdateVersions["stable"] == "99.0.0" && main.Config.NextUpdateCheckUtc > DateTimeOffset.UtcNow, "The checked version and next-check deadline are retained in configuration.");
            var automatic = (Task)Call(main, "CheckForUpdatesAsync", false)!; automatic.GetAwaiter().GetResult();
            Require(uiHandler.Requests.Count == 1, "Automatic checks respect the saved schedule.");
            ((CheckBox)main.FindName("UpdateChecksBox")).IsChecked = false; main.Config.NextUpdateCheckUtc = default;
            ((Task)Call(main, "CheckForUpdatesAsync", false)!).GetAwaiter().GetResult();
            Require(uiHandler.Requests.Count == 1, "Disabling automatic updates suppresses automatic network checks.");
        }
        finally { CloseDesk(main); }

        var closingMain = new MainWindow(new AppConfig { HotkeysEnabled = false, UpdateManifestUrl = primary }, initializeEnhancement: false,
            windowFilter: _ => false, persistConfiguration: false, updateService: new UpdateService(new UpdateHandler(async (_, cancel) =>
            { await Task.Delay(Timeout.Infinite, cancel); return Response([]); }), keys));
        app.MainWindow = closingMain; Field(closingMain, "_guideStarted", true);
        closingMain.Show(); Pump();
        var pending = (Task)Call(closingMain, "CheckForUpdatesAsync", true)!;
        CloseDesk(closingMain);
        Wait(() => pending.IsCompleted, "Closing the desk cancels a pending update check.");
        pending.GetAwaiter().GetResult();
    }

    private static void RejectUpdate(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is UpdateException or FormatException or JsonException or CryptographicException)
        { Require(true, message); return; }
        throw new InvalidOperationException(message);
    }
    private sealed class UpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); return send(request, cancellationToken); }
    }
    private sealed class BlockingUpdateStream : Stream
    {
        public bool Emitted { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!Emitted) { Emitted = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
