using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

internal static class Program
{
    const uint CreateSuspended = 0x00000004;

    static int Main()
    {
        var command = SkipFirst(Marshal.PtrToStringUni(GetCommandLineW()) ?? "");
        var exeName = ExeName(command);
        var once = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "enhance.once");
        var inject = File.Exists(once);
        if (inject)
        {
            try
            {
                File.Delete(once);
            }
            catch (IOException)
            {
                inject = false;
            }
        }

        RemoveDebugger(exeName);
        if (OurDebuggerStillSet(exeName))
        {
            Log("debugger key still set");
            return 4;
        }

        if (command.Length == 0)
        {
            Log("empty command");
            return 1;
        }

        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        var buffer = new StringBuilder(command, command.Length + 16);
        var flags = inject ? CreateSuspended : 0u;
        ProcessInformation process;
        if (!CreateProcess(null, buffer, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref startup, out process))
        {
            Log("create " + Marshal.GetLastWin32Error());
            return 2;
        }

        var started = true;
        if (inject)
        {
            var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ddraw.dll");
            started = Inject(process.hProcess, dll);
            ResumeThread(process.hThread);
            Log(started ? "inject ok " + process.dwProcessId : "inject failed " + Marshal.GetLastWin32Error());
        }
        else
        {
            Log("passthrough " + process.dwProcessId);
        }

        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return started ? 0 : 3;
    }

    static string SkipFirst(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return "";
        }

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? "" : command.Substring(end + 1).TrimStart();
        }

        var space = command.IndexOf(' ');
        return space < 0 ? "" : command.Substring(space + 1).TrimStart();
    }

    static string ExeName(string command)
    {
        var token = command.Trim();
        if (token.Length == 0)
        {
            return "";
        }

        if (token[0] == '"')
        {
            var end = token.IndexOf('"', 1);
            token = end < 0 ? token.Substring(1) : token.Substring(1, end - 1);
        }
        else
        {
            var space = token.IndexOf(' ');
            if (space >= 0)
            {
                token = token.Substring(0, space);
            }
        }

        try
        {
            return Path.GetFileName(token);
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    static void RemoveDebugger(string exeName)
    {
        if (exeName.Length == 0)
        {
            return;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hive.OpenSubKey(KeyPath(exeName), true))
                {
                    if (key == null)
                    {
                        continue;
                    }

                    var value = key.GetValue("Debugger") as string;
                    if (value != null && value.IndexOf("enhance-inject.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        key.DeleteValue("Debugger", false);
                    }
                }
            }
            catch (SystemException)
            {
            }
        }
    }

    static bool OurDebuggerStillSet(string exeName)
    {
        if (exeName.Length == 0)
        {
            return false;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hive.OpenSubKey(KeyPath(exeName), false))
                {
                    var value = key == null ? null : key.GetValue("Debugger") as string;
                    if (value != null && value.IndexOf("enhance-inject.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (SystemException)
            {
            }
        }

        return false;
    }

    static string KeyPath(string exeName)
    {
        return @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + exeName;
    }

    static bool Inject(IntPtr process, string dll)
    {
        var bytes = Encoding.Unicode.GetBytes(dll + "\0");
        var remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length, 0x3000, 0x04);
        if (remote == IntPtr.Zero)
        {
            return false;
        }

        int written;
        if (!WriteProcessMemory(process, remote, bytes, (uint)bytes.Length, out written))
        {
            return false;
        }

        var kernel = GetModuleHandle("kernel32.dll");
        var load = GetProcAddress(kernel, "LoadLibraryW");
        if (load == IntPtr.Zero)
        {
            return false;
        }

        var thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, load, remote, 0, IntPtr.Zero);
        if (thread == IntPtr.Zero)
        {
            return false;
        }

        WaitForSingleObject(thread, 8000);
        uint code;
        GetExitCodeThread(thread, out code);
        CloseHandle(thread);
        return code != 0;
    }

    static void Log(string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "enhance-inject.log"), text);
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetCommandLineW();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, uint size, out int written);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr parameter, uint flags, IntPtr threadId);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeThread(IntPtr thread, out uint code);

    [DllImport("kernel32.dll")]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string reserved;
        public string desktop;
        public string title;
        public int x;
        public int y;
        public int xSize;
        public int ySize;
        public int xCountChars;
        public int yCountChars;
        public int fillAttribute;
        public int flags;
        public short showWindow;
        public short reserved2;
        public IntPtr reserved2Ptr;
        public IntPtr stdInput;
        public IntPtr stdOutput;
        public IntPtr stdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
