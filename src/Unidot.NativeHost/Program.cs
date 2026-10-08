using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Unidot.NativeHost;

internal static class Program
{
    private const int HostFailure = 110;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnityMain2(IntPtr instance, IntPtr dataFolder, IntPtr commandLine, int showCommand);

    [STAThread]
    public static int Main(string[] args)
    {
        string? diagnostics = null;
        var entered = false;
        var phase = "arguments";
        try
        {
            string? library = null;
            string? data = null;
            string[] forwarded = [];
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--") { forwarded = args[(i + 1)..]; break; }
                if (i + 1 >= args.Length) throw new ArgumentException("Native host options require a value.");
                var option = args[i++];
                switch (option)
                {
                    case "--unity-player": library = args[i]; break;
                    case "--data-dir": data = args[i]; break;
                    case "--diagnostics": diagnostics = args[i]; break;
                    default: throw new ArgumentException("Unknown native host option: " + option);
                }
            }
            phase = "preflight";
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Native Host requires Windows x64.");
            if (library is null || data is null) throw new ArgumentException("--unity-player and --data-dir are required.");
            library = Path.GetFullPath(library); data = Path.GetFullPath(data);
            if (!File.Exists(library)) throw new FileNotFoundException("UnityPlayer.dll not found.", library);
            if (!Directory.Exists(data) || !File.Exists(Path.Combine(data, "globalgamemanagers"))) throw new DirectoryNotFoundException("Unity data directory is missing or incomplete: " + data);
            var root = Path.GetDirectoryName(library)!;
            Directory.SetCurrentDirectory(root);
            State(diagnostics, phase, false, null, library, data);
            phase = "load";
            if (!SetDefaultDllDirectories(0x00001000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var search = AddDllDirectory(root);
            if (search == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var module = LoadLibraryExW(library, IntPtr.Zero, 0x00000100 | 0x00001000);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load UnityPlayer.dll or one of its dependencies.");
            phase = "entry";
            var address = GetProcAddress(module, "UnityMain2");
            if (address == IntPtr.Zero)
                throw new EntryPointNotFoundException("UnityMain2 is unavailable; this host requires the Unity 2022.3 custom-data-folder entry. Standard EXE fallback may be used.");
            var main = Marshal.GetDelegateForFunctionPointer<UnityMain2>(address);
            var dataPointer = Marshal.StringToHGlobalUni(data);
            var commandPointer = Marshal.StringToHGlobalUni(string.Join(' ', forwarded.Select(QuoteWindowsArgument)));
            try
            {
                // This is the same native entry used by Unity 2022.3's official WindowsPlayer Main.cpp.
                // Unity owns engine, graphics, window, scripting and scene initialization.
                phase = "entering-unity";
                State(diagnostics, phase, true, null, library, data);
                Console.WriteLine($"[native host] PID {Environment.ProcessId}; UnityMain2; data={data}");
                Console.Out.Flush();
                entered = true;
                var result = main(GetModuleHandleW(null), dataPointer, commandPointer, 1);
                State(diagnostics, "returned", true, null, library, data, result);
                return result;
            }
            finally { Marshal.FreeHGlobal(commandPointer); Marshal.FreeHGlobal(dataPointer); }
            // Unity may exit the host itself. Do not attempt to unload/reinitialize the engine in this process.
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[native host error] phase={phase}; entered={entered}; {error}");
            State(diagnostics, phase, entered, error.Message, null, null, HostFailure);
            return HostFailure;
        }
    }

    private static void State(string? path, string phase, bool entered, string? error, string? library, string? data, int? exit = null)
    {
        if (path is null) return;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { protocol = 1, phase, unityEntered = entered, error, library, data, exitCode = exit, pid = Environment.ProcessId }));
        File.Move(temporary, path, true);
    }

    internal static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr AddDllDirectory(string directory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
