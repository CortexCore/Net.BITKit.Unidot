using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Unidot;

// A temporary hook only on the process this CLI just created; never attach to arbitrary existing games.
internal sealed class OriginalPlayerInjection : IDisposable
{
    private readonly IntPtr module;
    private readonly IntPtr hook;
    private readonly CancellationTokenSource stop;
    private readonly Task sender;

    private OriginalPlayerInjection(IntPtr module, IntPtr hook, uint thread, string? runtimeDirectory, CancellationToken cancellationToken)
    {
        this.module = module; this.hook = hook;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var message = RegisterWindowMessageW("Unidot.RuntimeBootstrap.4F524947494E414C.v1");
        sender = Task.Run(async () =>
        {
            try
            {
                // Allow Mono's scene/domain setup to settle before invoking external managed code.
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    if (runtimeDirectory is not null && File.Exists(Path.Combine(runtimeDirectory, "runtime-ready.txt"))) break;
                    PostThreadMessageW(thread, message, UIntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(100, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    public static async Task<OriginalPlayerInjection> AttachAsync(Process ownedProcess, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new UnidotException("Original Player injection requires Windows x64.");
        var library = Path.Combine(AppContext.BaseDirectory, "native", "Unidot.RuntimeHook.dll");
        if (!File.Exists(library)) throw new UnidotException("Original-EXE runtime hook is missing from the distribution: " + library);
        var module = NativeLibrary.Load(library);
        try
        {
            var callback = NativeLibrary.GetExport(module, "UnidotRuntimeHook");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ownedProcess.HasExited) throw new UnidotException("Original Player exited before its Unity window became available.");
                ownedProcess.Refresh();
                var window = ownedProcess.MainWindowHandle;
                if (window != IntPtr.Zero)
                {
                    var thread = GetWindowThreadProcessId(window, out var pid);
                    if (pid != ownedProcess.Id) throw new UnidotException("Unity window is not owned by this Player process.");
                    var hook = SetWindowsHookExW(3, callback, module, thread);
                    if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not install the owned Player main-thread hook.");
                    ownedProcess.StartInfo.Environment.TryGetValue("UNIDOT_RUNTIME_DIRECTORY", out var runtimeDirectory);
                    return new(module, hook, thread, runtimeDirectory, cancellationToken);
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        catch { NativeLibrary.Free(module); throw; }
    }

    public void Dispose()
    {
        stop.Cancel();
        sender.GetAwaiter().GetResult();
        UnhookWindowsHookEx(hook);
        NativeLibrary.Free(module);
        stop.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, IntPtr callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
}
