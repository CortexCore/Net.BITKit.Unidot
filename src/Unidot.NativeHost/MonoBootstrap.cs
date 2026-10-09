using System.Runtime.InteropServices;

namespace Unidot.NativeHost;

// Unity's own Mono runtime is already loaded. Invoke only on its native main thread.
internal sealed class MonoBootstrap : IDisposable
{
    private const uint Message = 0x8000 + 0x51;
    private readonly string assemblyPath;
    private readonly HookProc callback;
    private readonly CancellationTokenSource stop = new();
    private readonly Task sender;
    private readonly IntPtr hook;
    private bool attempted;

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg
    { public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Position; public uint Private; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookProc(int code, IntPtr wparam, IntPtr lparam);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DomainGet();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AssemblyOpen(IntPtr domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AssemblyImage(IntPtr assembly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ClassFind(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string space, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MethodFind(IntPtr type, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Invoke(IntPtr method, IntPtr instance, IntPtr parameters, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ObjectString(IntPtr instance, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr StringUtf8(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MonoFree(IntPtr instance);

    public MonoBootstrap(string path)
    {
        assemblyPath = Path.GetFullPath(path);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Runtime bootstrap not found.", assemblyPath);
        var thread = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        callback = OnMessage;
        hook = SetWindowsHookExW(3, callback, IntPtr.Zero, thread);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not install runtime bootstrap message hook.");
        sender = Task.Run(async () =>
        {
            try
            {
                // Retain the tested startup grace period; CLI connection readiness is bounded separately.
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    PostThreadMessageW(thread, Message, UIntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(100, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    private IntPtr OnMessage(int code, IntPtr wparam, IntPtr lparam)
    {
        if (code >= 0)
        {
            var message = Marshal.PtrToStructure<Msg>(lparam);
            if (message.Message == Message)
            {
                message.Message = 0;
                Marshal.StructureToPtr(message, lparam, false);
                if (!attempted)
                {
                    try
                    {
                        var mono = GetModuleHandleW("mono-2.0-bdwgc.dll");
                        if (mono != IntPtr.Zero && Export<DomainGet>(mono, "mono_domain_get")() is { } domain && domain != IntPtr.Zero)
                        {
                            attempted = true;
                            var assembly = Export<AssemblyOpen>(mono, "mono_domain_assembly_open")(domain, assemblyPath);
                            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono could not load the runtime bootstrap.");
                            var image = Export<AssemblyImage>(mono, "mono_assembly_get_image")(assembly);
                            var type = Export<ClassFind>(mono, "mono_class_from_name")(image, "Unidot.Runtime", "Bootstrap");
                            if (type == IntPtr.Zero) throw new TypeLoadException("Unidot.Runtime.Bootstrap not found.");
                            var method = Export<MethodFind>(mono, "mono_class_get_method_from_name")(type, "Install", 0);
                            if (method == IntPtr.Zero) throw new MissingMethodException("Bootstrap.Install not found.");
                            var result = Export<Invoke>(mono, "mono_runtime_invoke")(method, IntPtr.Zero, IntPtr.Zero, out var error);
                            if (error != IntPtr.Zero)
                            {
                                result = Export<ObjectString>(mono, "mono_object_to_string")(error, out _);
                                throw new InvalidOperationException(ReadString(mono, result));
                            }
                            var installed = ReadString(mono, result);
                            if (installed == "UNIDOT_NOT_READY") attempted = false;
                            else { Console.Error.WriteLine("[runtime] " + installed); stop.Cancel(); }
                        }
                    }
                    catch (Exception error)
                    {
                        attempted = true;
                        stop.Cancel();
                        Console.Error.WriteLine("[runtime bootstrap error] " + error);
                        var directory = Environment.GetEnvironmentVariable("UNIDOT_RUNTIME_DIRECTORY");
                        if (directory is not null)
                        {
                            try { File.WriteAllText(Path.Combine(directory, "runtime-error.txt"), error.ToString()); }
                            catch (IOException) { }
                        }
                    }
                }
            }
        }
        return CallNextHookEx(hook, code, wparam, lparam);
    }

    private static T Export<T>(IntPtr module, string name) where T : Delegate
    {
        var address = GetProcAddress(module, name);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string ReadString(IntPtr mono, IntPtr value)
    {
        if (value == IntPtr.Zero) return "<null>";
        var pointer = Export<StringUtf8>(mono, "mono_string_to_utf8")(value);
        try { return Marshal.PtrToStringUTF8(pointer) ?? "<null>"; }
        finally { Export<MonoFree>(mono, "mono_free")(pointer); }
    }

    public void Dispose()
    {
        stop.Cancel();
        sender.GetAwaiter().GetResult();
        UnhookWindowsHookEx(hook);
        stop.Dispose();
        GC.KeepAlive(callback);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessageW(out Msg message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
}
