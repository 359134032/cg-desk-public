using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CgDesk.Services;

internal sealed class ViewportLaunchFiles
{
    internal const string ClientHash = "9baa0d2660f96c6699ccf42ad46bd918717b98ccb228f4edde2d92afb89d3126";
    private const string PreviousProxyHash = "353c3924a718132c3f2aabb59436929bb29414d9faece58912f7edac88e406c6";
    private const string PreviousAliasHash = "cc8687345e7ac20c2dcd21321634c191a035d145f442d0a403584fd77e72b1a1";
    private const string StateName = "cgdesk_viewport.install.json";
    private static readonly string[] Names = ["ddraw.dll", "cgdesk_viewport_sys.dll", "cgdesk_viewport.ini"];
    private readonly string _bundle;
    private readonly string _system;
    private readonly string _clientHash;
    private readonly Func<bool> _running;

    internal ViewportLaunchFiles(string? bundle = null, string? system = null, string? clientHash = null, Func<bool>? running = null)
    {
        _bundle = bundle ?? Path.Combine(AppContext.BaseDirectory, "viewport");
        _system = system ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "ddraw.dll");
        _clientHash = clientHash ?? ClientHash;
        _running = running ?? (() =>
        {
            var processes = Process.GetProcessesByName("cg_se_3000");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        });
    }

    internal string Prepare(string client, LaunchResolution resolution, string? diagnosticPath = null)
    {
        if (diagnosticPath != null)
        {
            if (!Path.IsPathFullyQualified(diagnosticPath) || diagnosticPath.IndexOfAny(['\r','\n']) >= 0
                || Path.GetFileName(diagnosticPath) != "cgdesk-graphics-state.jsonl")
                throw new InvalidOperationException("诊断日志必须是本地绝对路径，文件名为 cgdesk-graphics-state.jsonl。");
            diagnosticPath = Path.GetFullPath(diagnosticPath);
        }
        if (string.IsNullOrWhiteSpace(client) || !File.Exists(client))
        {
            if (resolution.Native) return "使用游戏原生设置。";
            throw new InvalidOperationException("未能定位客户端，请先在官方登录器中选择并安装游戏。");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(client))!;
        var statePath = Path.Combine(directory, StateName);
        var owned = ReadOwned(directory, statePath);
        if (resolution.Native && owned == null)
            return File.Exists(Path.Combine(directory, "ddraw.dll")) ? "跟随现有客户端设置；目录内已有其他渲染组件，未改动。" : "使用游戏原生设置。";
        if (!resolution.Native && (!LaunchResolutions.Supported(resolution.Width, resolution.Height)
            || !Path.GetFileName(client).Equals("cg_se_3000.exe", StringComparison.OrdinalIgnoreCase) || Hash(client) != _clientHash))
            throw new InvalidOperationException("实验分辨率仅支持已核对的客户端版本和已适配档位，未改动任何文件。");

        var desired = new Dictionary<string, byte[]>();
        if (!resolution.Native)
        {
            var proxy = File.ReadAllBytes(Path.Combine(_bundle, "ddraw.dll"));
            using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(_bundle, "build-profile.json")));
            if (!Encoding.ASCII.GetString(proxy).Contains("CgDeskViewportLaunch-v1", StringComparison.Ordinal)
                || Hash(proxy) != profile.RootElement.GetProperty("ProxySha256").GetString())
                throw new InvalidOperationException("实验组件校验失败，请重新解压完整的窗口台程序包。");
            desired[Names[0]] = proxy;
            desired[Names[1]] = File.ReadAllBytes(_system);
            var diagnostic = diagnosticPath == null ? "" : $"DiagnosticPath={diagnosticPath}\r\n";
            var ini = $"; CgDeskViewportLaunch-v1\r\n[Viewport]\r\nWidth={resolution.Width}\r\nHeight={resolution.Height}\r\n{diagnostic}";
            desired[Names[2]] = diagnosticPath == null ? Encoding.ASCII.GetBytes(ini)
                : Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(ini)).ToArray();
            foreach (var name in Names)
                if (File.Exists(Path.Combine(directory, name)) && (owned == null || !owned.ContainsKey(name)))
                    throw new InvalidOperationException($"客户端目录已有其他 {name}，窗口台不会覆盖。");
        }
        var same = owned != null && desired.Count == owned.Count && desired.All(pair => owned.TryGetValue(pair.Key, out var hash) && hash == Hash(pair.Value));
        if (same) return $"启动分辨率 {resolution.Width}×{resolution.Height}（实验）。";
        if (_running()) throw new InvalidOperationException("请先正常退出所有游戏客户端，再更改启动分辨率。原有设置未改动。");

        // Only prevalidated owned additions participate. Never open the EXE or resources for writing.
        var before = (owned ?? []).Keys.Append(StateName).Distinct().Where(name => File.Exists(Path.Combine(directory, name)))
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
        var changed = new HashSet<string>();
        var written = new Dictionary<string, string>();
        void WriteNew(string name, byte[] bytes)
        {
            var path = Path.Combine(directory, name);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                changed.Add(name);
                try { stream.Write(bytes); }
                finally
                {
                    stream.Flush();
                    // Record only bytes created by this operation, including a partial write.
                    using var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    written[name] = Convert.ToHexString(SHA256.HashData(verify)).ToLowerInvariant();
                }
            }
        }
        try
        {
            foreach (var name in (owned ?? []).Keys)
            {
                if (Hash(Path.Combine(directory, name)) != owned![name]) throw new IOException("实验文件在操作前发生变化，已停止。");
                File.Delete(Path.Combine(directory, name));
                changed.Add(name);
            }
            foreach (var (name, bytes) in desired)
            {
                WriteNew(name, bytes);
            }
            if (File.Exists(statePath)) { File.Delete(statePath); changed.Add(StateName); }
            if (desired.Count > 0)
            {
                WriteNew(StateName, JsonSerializer.SerializeToUtf8Bytes(new Ownership("CgDeskViewportLaunch-v1", desired.ToDictionary(pair => pair.Key, pair => Hash(pair.Value)))));
            }
        }
        catch
        {
            foreach (var name in changed)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    if (!written.TryGetValue(name, out var ourHash) || Hash(path) != ourHash) continue;
                    File.Delete(path);
                }
                if (before.TryGetValue(name, out var bytes))
                {
                    using var restored = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    restored.Write(bytes);
                }
            }
            throw;
        }
        return resolution.Native ? "已停用实验组件，下次启动使用游戏原生设置。" : $"启动分辨率 {resolution.Width}×{resolution.Height}（实验）。";
    }

    private static Dictionary<string, string>? ReadOwned(string directory, string statePath)
    {
        if (File.Exists(statePath))
        {
            var state = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(statePath));
            if (state == null || state.Schema != "CgDeskViewportLaunch-v1" || state.Files.Count != Names.Length
                || state.Files.Keys.Any(name => !Names.Contains(name))) throw new InvalidOperationException("实验文件记录异常，未改动客户端目录。");
            foreach (var (name, hash) in state.Files)
                if (!File.Exists(Path.Combine(directory, name)) || Hash(Path.Combine(directory, name)) != hash)
                    throw new InvalidOperationException($"实验文件 {name} 已改变，未自动覆盖或删除。");
            return state.Files;
        }
        // Adopt only the exact UI-only build already accepted by the user in this experiment.
        var proxy = Path.Combine(directory, Names[0]);
        var alias = Path.Combine(directory, Names[1]);
        if (File.Exists(proxy) && File.Exists(alias) && Hash(proxy) == PreviousProxyHash && Hash(alias) == PreviousAliasHash)
            return new() { [Names[0]] = PreviousProxyHash, [Names[1]] = PreviousAliasHash };
        return null;
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Ownership(string Schema, Dictionary<string, string> Files);
}
