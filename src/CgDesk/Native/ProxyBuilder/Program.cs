using System.Diagnostics;
using System.Text;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ProxyBuilder <output-dll-path>");
    return 1;
}
var output = Path.GetFullPath(args[0]);

var imports = new[]
{
    "LoadLibraryW",
    "LoadLibraryExW",
    "GetProcAddress",
    "GetModuleFileNameW",
    "GetSystemDirectoryW",
    "GetFileAttributesW",
    "GetCommandLineW",
    "InterlockedCompareExchange",
    "Sleep"
};

var functions = new[]
{
    "AcquireDDThreadLock",
    "CompleteCreateSysmemSurface",
    "D3DParseUnknownCommand",
    "DDGetAttachedSurfaceLcl",
    "DDInternalLock",
    "DDInternalUnlock",
    "DSoundHelp",
    "DirectDrawCreate",
    "DirectDrawCreateClipper",
    "DirectDrawCreateEx",
    "DirectDrawEnumerateA",
    "DirectDrawEnumerateExA",
    "DirectDrawEnumerateExW",
    "DirectDrawEnumerateW",
    "DllCanUnloadNow",
    "DllGetClassObject",
    "GetDDSurfaceLocal",
    "GetOLEThunkData",
    "GetSurfaceFromDC",
    "RegisterSpecialCase",
    "ReleaseDDThreadLock",
    "SetAppCompatData",
    "CgDeskDDrawProbe"
};

var data = new Blob();
var iat = data.Dwords(imports.Length + 1);
var ilt = data.Dwords(imports.Length + 1);
var importDir = data.Reserve(40);
var exportDir = data.Reserve(40);
var eat = data.Dwords(functions.Length);
var nameRvas = data.Dwords(functions.Length);
var ordinals = data.Reserve(functions.Length * 2);
data.Align(4);
var dllName = data.Ascii("ddraw.dll");
var kernelName = data.Ascii("KERNEL32.dll");
var hintAt = new int[imports.Length];
for (var i = 0; i < imports.Length; i++)
{
    hintAt[i] = data.Reserve(2);
    data.Ascii(imports[i]);
    data.Align(2);
}

var exportNameAt = new int[functions.Length];
for (var i = 0; i < functions.Length; i++)
{
    exportNameAt[i] = data.Ascii(functions[i]);
}

var marker = data.Ascii("CgDeskDDrawProxy");
var hSelf = data.Dword(0);
var ready = data.Dword(0);
var useWrap = data.Dword(0);
var hDgv = data.Dword(0);
var hSys = data.Dword(0);
var mode = data.Dword(0);
var path = data.Reserve(520 * 2);
var flagSuffix = data.Utf16("\\enhance.on");
var dgvSuffix = data.Utf16("\\dgv\\ddraw_dgv.dll");
var sysCopySuffix = data.Utf16("\\ddraw_sys.dll");
var sysDllName = data.Utf16("\\ddraw.dll");
var needle = data.Utf16("cgdeskenhance");
var nameTable = data.Dwords(functions.Length);
for (var i = 0; i < functions.Length; i++)
{
    data.Patch(nameTable + (i * 4), (uint)exportNameAt[i]);
}

data.Dwords(functions.Length);

_ = marker;
var code = new Code();
EmitProxy(code, new Offsets(iat, hSelf, ready, useWrap, hDgv, hSys, mode, path, flagSuffix, dgvSuffix, sysCopySuffix, sysDllName, needle, nameTable, functions.Length));
var entry = code.Labels["dllmain"];
var thunkAt = functions.Select(name => code.Labels["thunk_" + name]).ToArray();

var image = LinkDll(code, data, entry, thunkAt, exportNameAt, functions, iat, ilt, importDir, exportDir, eat, nameRvas, ordinals, hintAt, dllName, kernelName);
var folder = Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(folder))
{
    Directory.CreateDirectory(folder);
}
File.WriteAllBytes(output, image);
Console.WriteLine($"proxy {output} {image.Length}");

var temp = Path.Combine(Path.GetTempPath(), "cgdesk-ddraw-test-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(temp, "dgv"));
    File.Copy(output, Path.Combine(temp, "ddraw.dll"), true);
    File.WriteAllBytes(Path.Combine(temp, "dgv", "ddraw_dgv.dll"), DummyDll());
    var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "ddraw.dll");
    File.Copy(sys, Path.Combine(temp, "ddraw_sys.dll"), true);
    File.WriteAllBytes(Path.Combine(temp, "probe.exe"), ProbeExe());

    var plain = RunProbe(temp);
    Console.WriteLine($"plain hr=0x{plain.Hr:X8} probe={plain.Probe} ptr=0x{plain.Ptr:X}");
    File.WriteAllText(Path.Combine(temp, "enhance.on"), "1");
    var wrapped = RunProbe(temp);
    Console.WriteLine($"wrapped hr=0x{wrapped.Hr:X8} probe={wrapped.Probe} ptr=0x{wrapped.Ptr:X}");

    if (plain.Probe != 1 || plain.Hr != 0 || plain.Ptr == 0)
    {
        throw new InvalidOperationException("system forward failed");
    }

    if (wrapped.Probe != 2 || wrapped.Hr != 0x0DD00001)
    {
        throw new InvalidOperationException("option gate failed");
    }

    Console.WriteLine("proxy gate ok");
}
finally
{
    var resolved = Path.GetFullPath(temp);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected probe directory.");
    Directory.Delete(resolved, true);
}
return 0;

static ProbeResult RunProbe(string dir)
{
    var result = Path.Combine(dir, "result.bin");
    if (File.Exists(result))
    {
        File.Delete(result);
    }

    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(dir, "probe.exe"),
        WorkingDirectory = dir,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException("probe did not start");
    if (!process.WaitForExit(8000))
    {
        process.Kill(true);
        throw new InvalidOperationException("probe timed out");
    }

    var bytes = File.ReadAllBytes(result);
    return new ProbeResult(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4), BitConverter.ToUInt32(bytes, 8));
}

static void EmitProxy(Code code, Offsets o)
{
    code.Label("dllmain");
    code.Bytes(0x55, 0x89, 0xE5, 0x53);
    code.Bytes(0x83, 0x7D, 0x0C, 0x01);
    code.Jcc(0x85, "dll_ret");
    code.Pic("dll_pic");
    code.Bytes(0x8B, 0x45, 0x08);
    code.Bytes(0x89, 0x83);
    code.Disp(o.HSelf);
    code.Label("dll_ret");
    code.Bytes(0xB8, 0x01, 0x00, 0x00, 0x00, 0x5B, 0x5D, 0xC2, 0x0C, 0x00);

    code.Label("ensure");
    code.Bytes(0x53, 0x56, 0x57);
    code.Pic("ensure_pic");
    code.Bytes(0x83, 0xBB);
    code.Disp(o.Ready);
    code.Bytes(0x01);
    code.Jcc(0x84, "ensure_ok");
    code.Bytes(0x31, 0xC0, 0xB9, 0x02, 0x00, 0x00, 0x00);
    code.Bytes(0xF0, 0x0F, 0xB1, 0x8B);
    code.Disp(o.Ready);
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "ensure_load");
    code.Bytes(0x83, 0xF8, 0x01);
    code.Jcc(0x84, "ensure_ok");
    code.Label("ensure_spin");
    code.Bytes(0x6A, 0x00);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (8 * 4));
    code.Bytes(0x83, 0xBB);
    code.Disp(o.Ready);
    code.Bytes(0x01);
    code.Jcc(0x85, "ensure_spin");
    code.Jmp("ensure_ok");
    code.Label("ensure_load");
    code.Call("load_impl");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Ready);
    code.Bytes(0x01, 0x00, 0x00, 0x00);
    code.Label("ensure_ok");
    code.Bytes(0x5F, 0x5E, 0x5B, 0xC3);

    code.Label("load_impl");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Use);
    code.Bytes(0x00, 0x00, 0x00, 0x00);
    code.Bytes(0xB8);
    code.Imm(o.FlagSuffix);
    code.Call("build_path");
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (5 * 4));
    code.Bytes(0x83, 0xF8, 0xFF);
    code.Jcc(0x84, "try_cmd");
    code.Bytes(0xA9, 0x10, 0x00, 0x00, 0x00);
    code.Jcc(0x85, "try_cmd");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Use);
    code.Bytes(0x01, 0x00, 0x00, 0x00);
    code.Jmp("choose");
    code.Label("try_cmd");
    code.Call("scan_cmd");
    code.Label("choose");
    code.Bytes(0x83, 0xBB);
    code.Disp(o.Use);
    code.Bytes(0x01);
    code.Jcc(0x85, "only_sys");
    code.Bytes(0xB8);
    code.Imm(o.DgvSuffix);
    code.Call("build_path");
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x6A, 0x08, 0x6A, 0x00, 0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (1 * 4));
    code.Bytes(0x3B, 0x83);
    code.Disp(o.HSelf);
    code.Jcc(0x84, "only_sys");
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "only_sys");
    code.Bytes(0x89, 0x83);
    code.Disp(o.HDgv);
    code.Label("only_sys");
    code.Call("load_sys");
    code.Call("resolve");
    code.Bytes(0xC3);

    code.Label("build_path");
    code.Bytes(0x50);
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x68, 0x04, 0x01, 0x00, 0x00, 0x50);
    code.Bytes(0xFF, 0xB3);
    code.Disp(o.HSelf);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (3 * 4));
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "build_fail");
    code.Bytes(0x8D, 0xBB);
    code.Disp(o.Path);
    code.Bytes(0x89, 0xFE, 0x89, 0xC1, 0xD1, 0xE1, 0x01, 0xCE);
    code.Label("scan_slash");
    code.Bytes(0x39, 0xFE);
    code.Jcc(0x86, "build_fail");
    code.Bytes(0x83, 0xEE, 0x02);
    code.Bytes(0x66, 0x83, 0x3E, 0x5C);
    code.Jcc(0x85, "scan_slash");
    code.Bytes(0x89, 0xF7, 0x58, 0x8D, 0x34, 0x03, 0xFC);
    code.Label("copy_suffix");
    code.Bytes(0x66, 0xAD, 0x66, 0xAB, 0x66, 0x85, 0xC0);
    code.Jcc(0x85, "copy_suffix");
    code.Bytes(0xC3);
    code.Label("build_fail");
    code.Bytes(0x58, 0xC3);

    code.Label("scan_cmd");
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (6 * 4));
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "scan_ret");
    code.Bytes(0x89, 0xC6);
    code.Label("scan_outer");
    code.Bytes(0x66, 0x83, 0x3E, 0x00);
    code.Jcc(0x84, "scan_ret");
    code.Bytes(0x89, 0xDF);
    code.Bytes(0x81, 0xC7);
    code.Imm(o.Needle);
    code.Bytes(0x89, 0xF2);
    code.Label("scan_inner");
    code.Bytes(0x66, 0x8B, 0x0F, 0x66, 0x85, 0xC9);
    code.Jcc(0x84, "scan_found");
    code.Bytes(0x66, 0x3B, 0x0A);
    code.Jcc(0x85, "scan_next");
    code.Bytes(0x83, 0xC7, 0x02, 0x83, 0xC2, 0x02);
    code.Jmp("scan_inner");
    code.Label("scan_next");
    code.Bytes(0x83, 0xC6, 0x02);
    code.Jmp("scan_outer");
    code.Label("scan_found");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Use);
    code.Bytes(0x01, 0x00, 0x00, 0x00);
    code.Label("scan_ret");
    code.Bytes(0xC3);

    code.Label("load_sys");
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x68, 0x04, 0x01, 0x00, 0x00, 0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (4 * 4));
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "sys_copy");
    code.Bytes(0x8D, 0xBB);
    code.Disp(o.Path);
    code.Bytes(0x89, 0xC1, 0xD1, 0xE1, 0x01, 0xCF);
    code.Bytes(0x8D, 0xB3);
    code.Disp(o.SysDllName);
    code.Bytes(0xFC);
    code.Label("sys_copy_name");
    code.Bytes(0x66, 0xAD, 0x66, 0xAB, 0x66, 0x85, 0xC0);
    code.Jcc(0x85, "sys_copy_name");
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat);
    code.Bytes(0x3B, 0x83);
    code.Disp(o.HSelf);
    code.Jcc(0x84, "sys_copy");
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "sys_copy");
    code.Bytes(0x89, 0x83);
    code.Disp(o.HSys);
    code.Bytes(0xC3);
    code.Label("sys_copy");
    code.Bytes(0xB8);
    code.Imm(o.SysCopySuffix);
    code.Call("build_path");
    code.Bytes(0x8D, 0x83);
    code.Disp(o.Path);
    code.Bytes(0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat);
    code.Bytes(0x3B, 0x83);
    code.Disp(o.HSelf);
    code.Jcc(0x84, "sys_done");
    code.Bytes(0x89, 0x83);
    code.Disp(o.HSys);
    code.Label("sys_done");
    code.Bytes(0xC3);

    code.Label("resolve");
    code.Bytes(0x31, 0xF6);
    code.Label("resolve_loop");
    code.Bytes(0x8B, 0x84, 0xB3);
    code.Disp(o.NameTable);
    code.Bytes(0x01, 0xD8);
    code.Bytes(0x83, 0xBB);
    code.Disp(o.HDgv);
    code.Bytes(0x00);
    code.Jcc(0x84, "resolve_sys");
    code.Bytes(0x50);
    code.Bytes(0xFF, 0xB3);
    code.Disp(o.HDgv);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (2 * 4));
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x85, "resolve_store");
    code.Bytes(0x8B, 0x84, 0xB3);
    code.Disp(o.NameTable);
    code.Bytes(0x01, 0xD8);
    code.Label("resolve_sys");
    code.Bytes(0x83, 0xBB);
    code.Disp(o.HSys);
    code.Bytes(0x00);
    code.Jcc(0x84, "resolve_stub");
    code.Bytes(0x50);
    code.Bytes(0xFF, 0xB3);
    code.Disp(o.HSys);
    code.Bytes(0xFF, 0x93);
    code.Disp(o.Iat + (2 * 4));
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x85, "resolve_store");
    code.Label("resolve_stub");
    code.Bytes(0xE8, 0x00, 0x00, 0x00, 0x00);
    code.Label("stub_pic");
    code.Bytes(0x58);
    code.CodeDelta("stub_pic", "stub");
    code.Label("resolve_store");
    code.Bytes(0x89, 0x84, 0xB3);
    code.Disp(o.Fps);
    code.Bytes(0x46, 0x83, 0xFE, (byte)o.Count);
    code.Jcc(0x82, "resolve_loop");
    code.Bytes(0x83, 0xBB);
    code.Disp(o.HDgv);
    code.Bytes(0x00);
    code.Jcc(0x84, "mode_sys");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Mode);
    code.Bytes(0x02, 0x00, 0x00, 0x00, 0xC3);
    code.Label("mode_sys");
    code.Bytes(0x83, 0xBB);
    code.Disp(o.HSys);
    code.Bytes(0x00);
    code.Jcc(0x84, "mode_none");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Mode);
    code.Bytes(0x01, 0x00, 0x00, 0x00, 0xC3);
    code.Label("mode_none");
    code.Bytes(0xC7, 0x83);
    code.Disp(o.Mode);
    code.Bytes(0x00, 0x00, 0x00, 0x00, 0xC3);
    code.Label("stub");
    code.Bytes(0xB8, 0x05, 0x40, 0x00, 0x80, 0xC3);

    for (var i = 0; i < o.Count - 1; i++)
    {
        code.Label("thunk_" + FunctionName(i));
        code.Call("ensure");
        code.Bytes(0xE8, 0x00, 0x00, 0x00, 0x00);
        var pop = "tpic_" + i;
        code.Label(pop);
        code.Bytes(0x58);
        code.PicAddEax(pop, "thunk_" + FunctionName(i));
        code.Bytes(0xFF, 0xA0);
        code.Disp(o.Fps + (i * 4));
    }

    code.Label("thunk_CgDeskDDrawProbe");
    code.Call("ensure");
    code.Bytes(0xE8, 0x00, 0x00, 0x00, 0x00);
    code.Label("probe_pic");
    code.Bytes(0x58);
    code.PicAddEax("probe_pic", "probe_pic");
    code.Bytes(0x8B, 0x80);
    code.Disp(o.Mode);
    code.Bytes(0xC3);
}

static string FunctionName(int index) => index switch
{
    0 => "AcquireDDThreadLock",
    1 => "CompleteCreateSysmemSurface",
    2 => "D3DParseUnknownCommand",
    3 => "DDGetAttachedSurfaceLcl",
    4 => "DDInternalLock",
    5 => "DDInternalUnlock",
    6 => "DSoundHelp",
    7 => "DirectDrawCreate",
    8 => "DirectDrawCreateClipper",
    9 => "DirectDrawCreateEx",
    10 => "DirectDrawEnumerateA",
    11 => "DirectDrawEnumerateExA",
    12 => "DirectDrawEnumerateExW",
    13 => "DirectDrawEnumerateW",
    14 => "DllCanUnloadNow",
    15 => "DllGetClassObject",
    16 => "GetDDSurfaceLocal",
    17 => "GetOLEThunkData",
    18 => "GetSurfaceFromDC",
    19 => "RegisterSpecialCase",
    20 => "ReleaseDDThreadLock",
    21 => "SetAppCompatData",
    _ => "CgDeskDDrawProbe"
};

static byte[] LinkDll(Code code, Blob data, int entry, int[] thunkAt, int[] exportNameAt, string[] functions, int iat, int ilt, int importDir, int exportDir, int eat, int nameRvas, int ordinals, int[] hintAt, int dllName, int kernelName)
{
    code.PatchCode();
    var codeRva = 0x1000;
    var dataRva = 0x1000 + Align(code.B.Count, 0x1000);
    code.PatchPic(codeRva, dataRva);

    for (var i = 0; i < hintAt.Length; i++)
    {
        var rva = (uint)(dataRva + hintAt[i]);
        data.Patch(iat + (i * 4), rva);
        data.Patch(ilt + (i * 4), rva);
    }

    for (var i = 0; i < functions.Length; i++)
    {
        data.Patch(eat + (i * 4), (uint)(codeRva + thunkAt[i]));
    }

    var order = Enumerable.Range(0, functions.Length).OrderBy(i => functions[i], StringComparer.Ordinal).ToArray();
    for (var slot = 0; slot < order.Length; slot++)
    {
        var index = order[slot];
        data.Patch(nameRvas + (slot * 4), (uint)(dataRva + exportNameAt[index]));
        data.PatchUshort(ordinals + (slot * 2), (ushort)index);
    }

    void Dword(int at, uint value) => data.Patch(at, value);
    Dword(importDir + 0, (uint)(dataRva + ilt));
    Dword(importDir + 12, (uint)(dataRva + kernelName));
    Dword(importDir + 16, (uint)(dataRva + iat));
    Dword(exportDir + 12, (uint)(dataRva + dllName));
    Dword(exportDir + 16, 1);
    Dword(exportDir + 20, (uint)functions.Length);
    Dword(exportDir + 24, (uint)functions.Length);
    Dword(exportDir + 28, (uint)(dataRva + eat));
    Dword(exportDir + 32, (uint)(dataRva + nameRvas));
    Dword(exportDir + 36, (uint)(dataRva + ordinals));

    return WritePe(code.B.ToArray(), data.Bytes.ToArray(), codeRva + entry, dataRva, dll: true,
        (dataRva + exportDir, 40), (dataRva + importDir, 40), (dataRva + iat, (hintAt.Length + 1) * 4));
}

static byte[] DummyDll()
{
    var code = new Code();
    code.Label("dllmain");
    code.Bytes(0xB8, 0x01, 0x00, 0x00, 0x00, 0xC2, 0x0C, 0x00);
    code.Label("create");
    code.Bytes(0xB8, 0x01, 0x00, 0xD0, 0x0D, 0xC2, 0x0C, 0x00);
    var data = new Blob();
    var exportDir = data.Reserve(40);
    var eat = data.Dword(0);
    var name = data.Dword(0);
    var ord = data.Reserve(2);
    data.Align(4);
    var dllName = data.Ascii("ddraw_dgv.dll");
    var exportName = data.Ascii("DirectDrawCreate");
    var codeRva = 0x1000;
    var dataRva = 0x2000;
    data.Patch(eat, (uint)(codeRva + code.Labels["create"]));
    data.Patch(name, (uint)(dataRva + exportName));
    data.Patch(exportDir + 12, (uint)(dataRva + dllName));
    data.Patch(exportDir + 16, 1);
    data.Patch(exportDir + 20, 1);
    data.Patch(exportDir + 24, 1);
    data.Patch(exportDir + 28, (uint)(dataRva + eat));
    data.Patch(exportDir + 32, (uint)(dataRva + name));
    data.Patch(exportDir + 36, (uint)(dataRva + ord));
    return WritePe(code.B.ToArray(), data.Bytes.ToArray(), codeRva + code.Labels["dllmain"], dataRva, true,
        (dataRva + exportDir, 40), (0, 0), (0, 0));
}

static byte[] ProbeExe()
{
    var imports = new[] { "LoadLibraryW", "GetProcAddress", "CreateFileA", "WriteFile", "ExitProcess" };
    var data = new Blob();
    var iat = data.Dwords(imports.Length + 1);
    var ilt = data.Dwords(imports.Length + 1);
    var importDir = data.Reserve(40);
    var kernelName = data.Ascii("KERNEL32.dll");
    var hintAt = new int[imports.Length];
    for (var i = 0; i < imports.Length; i++)
    {
        hintAt[i] = data.Reserve(2);
        data.Ascii(imports[i]);
        data.Align(2);
    }

    var dllName = data.Utf16("ddraw.dll");
    var createName = data.Ascii("DirectDrawCreate");
    var probeName = data.Ascii("CgDeskDDrawProbe");
    var resultName = data.Ascii("result.bin");
    var buffer = data.Reserve(12);

    var code = new Code();
    code.Label("start");
    code.Pic("start_pic");
    code.Bytes(0x8D, 0x83);
    code.Disp(dllName);
    code.Bytes(0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat);
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "fail");
    code.Bytes(0x89, 0xC6);
    code.Bytes(0x8D, 0x83);
    code.Disp(createName);
    code.Bytes(0x50, 0x56);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat + 4);
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "fail");
    code.Bytes(0x89, 0xC7);
    code.Bytes(0x83, 0xEC, 0x10);
    code.Bytes(0x8D, 0x44, 0x24, 0x04);
    code.Bytes(0x6A, 0x00, 0x50, 0x6A, 0x00);
    code.Bytes(0xFF, 0xD7);
    code.Bytes(0x89, 0x83);
    code.Disp(buffer);
    code.Bytes(0x8B, 0x44, 0x24, 0x04);
    code.Bytes(0x89, 0x83);
    code.Disp(buffer + 8);
    code.Bytes(0x83, 0xC4, 0x10);
    code.Bytes(0x8D, 0x83);
    code.Disp(probeName);
    code.Bytes(0x50, 0x56);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat + 4);
    code.Bytes(0x85, 0xC0);
    code.Jcc(0x84, "fail");
    code.Bytes(0xFF, 0xD0);
    code.Bytes(0x89, 0x83);
    code.Disp(buffer + 4);
    code.Jmp("write");
    code.Label("fail");
    code.Bytes(0xC7, 0x83);
    code.Disp(buffer);
    code.Bytes(0xFF, 0xFF, 0xFF, 0xFF);
    code.Label("write");
    code.Bytes(0x6A, 0x00, 0x68, 0x80, 0x00, 0x00, 0x00, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x00, 0x00, 0x00, 0x40);
    code.Bytes(0x8D, 0x83);
    code.Disp(resultName);
    code.Bytes(0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat + 8);
    code.Bytes(0x83, 0xEC, 0x04);
    code.Bytes(0x6A, 0x00);
    code.Bytes(0x8D, 0x4C, 0x24, 0x04);
    code.Bytes(0x51);
    code.Bytes(0x6A, 0x0C);
    code.Bytes(0x8D, 0x93);
    code.Disp(buffer);
    code.Bytes(0x52, 0x50);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat + 12);
    code.Bytes(0x83, 0xC4, 0x04);
    code.Bytes(0x6A, 0x00);
    code.Bytes(0xFF, 0x93);
    code.Disp(iat + 16);

    code.PatchCode();
    var codeRva = 0x1000;
    var dataRva = 0x1000 + Align(code.B.Count, 0x1000);
    code.PatchPic(codeRva, dataRva);
    for (var i = 0; i < hintAt.Length; i++)
    {
        var rva = (uint)(dataRva + hintAt[i]);
        data.Patch(iat + (i * 4), rva);
        data.Patch(ilt + (i * 4), rva);
    }

    data.Patch(importDir + 0, (uint)(dataRva + ilt));
    data.Patch(importDir + 12, (uint)(dataRva + kernelName));
    data.Patch(importDir + 16, (uint)(dataRva + iat));
    return WritePe(code.B.ToArray(), data.Bytes.ToArray(), codeRva + code.Labels["start"], dataRva, false,
        (0, 0), (dataRva + importDir, 40), (dataRva + iat, (hintAt.Length + 1) * 4));
}

static byte[] WritePe(byte[] code, byte[] data, int entryRva, int dataRva, bool dll, (int Rva, int Size) exportDir, (int Rva, int Size) importDir, (int Rva, int Size) iat)
{
    var textRaw = Align(code.Length, 0x200);
    var dataRaw = Align(data.Length, 0x200);
    var headers = new byte[0x200];
    headers[0] = 0x4D;
    headers[1] = 0x5A;
    BitConverter.GetBytes(0x80).CopyTo(headers, 0x3C);
    var pe = 0x80;
    headers[pe] = (byte)'P';
    headers[pe + 1] = (byte)'E';
    Put16(headers, pe + 4, 0x14C);
    Put16(headers, pe + 6, 2);
    Put16(headers, pe + 20, 0xE0);
    Put16(headers, pe + 22, dll ? 0x2102 : 0x0102);
    var opt = pe + 24;
    Put16(headers, opt, 0x10B);
    headers[opt + 2] = 14;
    Put32(headers, opt + 4, code.Length);
    Put32(headers, opt + 8, data.Length);
    Put32(headers, opt + 16, entryRva);
    Put32(headers, opt + 20, 0x1000);
    Put32(headers, opt + 24, dataRva);
    Put32(headers, opt + 28, 0x10000000);
    Put32(headers, opt + 32, 0x1000);
    Put32(headers, opt + 36, 0x200);
    Put16(headers, opt + 40, 6);
    Put16(headers, opt + 48, 6);
    var image = dataRva + Align(data.Length, 0x1000);
    Put32(headers, opt + 56, image);
    Put32(headers, opt + 60, 0x200);
    Put16(headers, opt + 68, dll ? 2 : 3);
    Put16(headers, opt + 70, 0x0140);
    Put32(headers, opt + 72, 0x100000);
    Put32(headers, opt + 76, 0x1000);
    Put32(headers, opt + 80, 0x100000);
    Put32(headers, opt + 84, 0x1000);
    Put32(headers, opt + 92, 16);
    Put32(headers, opt + 96, exportDir.Rva);
    Put32(headers, opt + 100, exportDir.Size);
    Put32(headers, opt + 104, importDir.Rva);
    Put32(headers, opt + 108, importDir.Size);
    Put32(headers, opt + 192, iat.Rva);
    Put32(headers, opt + 196, iat.Size);
    var section = opt + 0xE0;
    WriteSection(headers, section, ".text", code.Length, 0x1000, textRaw, 0x200, 0x60000020);
    WriteSection(headers, section + 40, ".data", data.Length, dataRva, dataRaw, 0x200 + textRaw, 0xC0000040);
    var file = new byte[0x200 + textRaw + dataRaw];
    headers.CopyTo(file, 0);
    code.CopyTo(file, 0x200);
    data.CopyTo(file, 0x200 + textRaw);
    return file;
}

static void WriteSection(byte[] buffer, int at, string name, int virtualSize, int virtualAddress, int rawSize, int rawPointer, uint flags)
{
    Encoding.ASCII.GetBytes(name).CopyTo(buffer, at);
    Put32(buffer, at + 8, virtualSize);
    Put32(buffer, at + 12, virtualAddress);
    Put32(buffer, at + 16, rawSize);
    Put32(buffer, at + 20, rawPointer);
    Put32(buffer, at + 36, unchecked((int)flags));
}

static void Put16(byte[] buffer, int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(buffer, at);
static void Put32(byte[] buffer, int at, int value) => BitConverter.GetBytes(value).CopyTo(buffer, at);
static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

sealed class Blob
{
    public readonly List<byte> Bytes = [];
    public int Reserve(int size)
    {
        var at = Bytes.Count;
        Bytes.AddRange(new byte[size]);
        return at;
    }

    public int Dword(uint value)
    {
        var at = Bytes.Count;
        Bytes.AddRange(BitConverter.GetBytes(value));
        return at;
    }

    public int Dwords(int count) => Reserve(count * 4);
    public void Align(int n)
    {
        while (Bytes.Count % n != 0)
        {
            Bytes.Add(0);
        }
    }

    public int Ascii(string text)
    {
        var at = Bytes.Count;
        Bytes.AddRange(Encoding.ASCII.GetBytes(text));
        Bytes.Add(0);
        return at;
    }

    public int Utf16(string text)
    {
        var at = Bytes.Count;
        Bytes.AddRange(Encoding.Unicode.GetBytes(text + "\0"));
        return at;
    }

    public void Patch(int at, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i < 4; i++)
        {
            Bytes[at + i] = bytes[i];
        }
    }

    public void PatchUshort(int at, ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        Bytes[at] = bytes[0];
        Bytes[at + 1] = bytes[1];
    }
}

sealed class Code
{
    public readonly List<byte> B = [];
    public readonly Dictionary<string, int> Labels = [];
    readonly List<(int At, string Target)> _rel = [];
    readonly List<(int At, string Pop, string Base)> _pic = [];
    readonly List<(int At, string Pop, string Target)> _codeDelta = [];
    public void Label(string name) => Labels[name] = B.Count;
    public void Bytes(params byte[] bytes) => B.AddRange(bytes);
    public void Disp(int offset) => B.AddRange(BitConverter.GetBytes(offset));
    public void Imm(int value) => B.AddRange(BitConverter.GetBytes(value));
    public void Rel(string target)
    {
        _rel.Add((B.Count, target));
        B.AddRange(new byte[4]);
    }

    public void Call(string target) { Bytes(0xE8); Rel(target); }
    public void Jmp(string target) { Bytes(0xE9); Rel(target); }
    public void Jcc(byte op, string target) { Bytes(0x0F, op); Rel(target); }
    public void Pic(string pop)
    {
        Bytes(0xE8);
        Rel(pop);
        Label(pop);
        Bytes(0x5B);
        _pic.Add((B.Count + 2, pop, "data"));
        Bytes(0x81, 0xC3, 0, 0, 0, 0);
    }

    public void PicAddEax(string pop, string unused)
    {
        _ = unused;
        _pic.Add((B.Count + 2, pop, "data"));
        Bytes(0x81, 0xC0, 0, 0, 0, 0);
    }

    public void CodeDelta(string pop, string target)
    {
        _codeDelta.Add((B.Count + 2, pop, target));
        Bytes(0x81, 0xC0, 0, 0, 0, 0);
    }

    public void PatchCode()
    {
        foreach (var (at, target) in _rel)
        {
            WriteAt(at, Labels[target] - (at + 4));
        }

        foreach (var (at, pop, target) in _codeDelta)
        {
            WriteAt(at, Labels[target] - Labels[pop]);
        }
    }

    public void PatchPic(int codeRva, int dataRva)
    {
        foreach (var (at, pop, _) in _pic)
        {
            WriteAt(at, dataRva - (codeRva + Labels[pop]));
        }
    }

    void WriteAt(int at, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i < 4; i++)
        {
            B[at + i] = bytes[i];
        }
    }
}

readonly record struct Offsets(int Iat, int HSelf, int Ready, int Use, int HDgv, int HSys, int Mode, int Path, int FlagSuffix, int DgvSuffix, int SysCopySuffix, int SysDllName, int Needle, int NameTable, int Count)
{
    public int Fps => NameTable + (Count * 4);
}

readonly record struct ProbeResult(int Hr, int Probe, uint Ptr);
