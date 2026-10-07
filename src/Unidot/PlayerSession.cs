using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Unidot;

internal static class PlayerSession
{
    public static Task<int> RunAsync(Configuration config, PlayerLayout player, IEnumerable<string> arguments, string? requestedLog, CancellationToken cancellationToken)
    {
        if (PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Player is already running.");
        var forwarded = arguments.ToArray();
        if (forwarded.Any(a => a.Equals("-logFile", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-logFile=", StringComparison.OrdinalIgnoreCase)))
            throw new UnidotException("Use --log-file <path> so Unidot can follow the Unity log.");
        var log = requestedLog is null ? Path.Combine(config.GeneratedDirectory, "logs", "Player-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log") :
            Path.GetFullPath(requestedLog, Environment.CurrentDirectory);
        var start = new ProcessStartInfo(player.Executable) { WorkingDirectory = Path.GetDirectoryName(player.Executable)!, UseShellExecute = false };
        start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(log);
        foreach (var argument in forwarded) start.ArgumentList.Add(argument);
        return RunProcessAsync(start, log, cancellationToken);
    }

    internal static async Task<int> RunProcessAsync(ProcessStartInfo start, string logPath, CancellationToken cancellationToken, TextWriter? destination = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logPath = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // Unity also truncates its log on launch. Start empty to avoid streaming an older session.
        using (new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        var output = TextWriter.Synchronized(destination ?? Console.Out);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var job = OperatingSystem.IsWindows() ? new OwnedProcessJob() : null;
        using var process = Process.Start(start) ?? throw new UnidotException($"Could not start {start.FileName}.");
        using var drain = new CancellationTokenSource();
        Task streams = Task.CompletedTask;
        Task follow = Task.CompletedTask;
        var cancelled = false;
        try
        {
            job?.Assign(process);
            await output.WriteLineAsync($"Started Player (PID {process.Id}). Log: {logPath}");
            await output.WriteLineAsync("Following Unity log. Ctrl+C closes this Player and ends the session.");
            follow = FollowLogAsync(logPath, output, drain.Token);
            async Task PumpAsync(StreamReader reader)
            {
                var chars = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(chars.AsMemory(), drain.Token)) > 0)
                    await output.WriteAsync(chars.AsMemory(0, read), drain.Token);
            }
            streams = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                cancelled = true;
                await output.WriteLineAsync("Stopping Player...");
                await StopAsync(process);
            }
        }
        finally
        {
            if (!process.HasExited) await StopAsync(process);
            // Finish the file tail, including a final line without a newline. Descendant-held stdout pipes are bounded.
            try { await streams.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { } catch (TimeoutException) { }
            drain.Cancel();
            try { await follow; } catch (OperationCanceledException) { }
            try { await streams; } catch (OperationCanceledException) { }
        }
        await output.WriteLineAsync($"Player exited (PID {process.Id}, code {process.ExitCode}).");
        return cancelled ? 130 : process.ExitCode;
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        var requested = false;
        try { requested = process.CloseMainWindow(); } catch (InvalidOperationException) { return; }
        if (requested)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); return; } catch (OperationCanceledException) { }
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync();
    }

    internal static async Task FollowLogAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var pending = new StringBuilder();
        async Task EmitAsync(int count, bool final = false)
        {
            pending.Append(chars, 0, count);
            var text = pending.ToString();
            var length = final ? text.Length : text.LastIndexOf('\n') + 1;
            if (length == 0 && pending.Length >= 65536) length = char.IsHighSurrogate(text[^1]) ? text.Length - 1 : text.Length;
            if (length == 0) return;
            await output.WriteAsync(text.AsMemory(0, length), CancellationToken.None);
            pending.Remove(0, length);
        }
        async Task ReadAvailableAsync()
        {
            if (stream is null)
            {
                if (!File.Exists(path)) return;
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous);
            }
            if (stream.Length < stream.Position) { stream.Position = 0; decoder.Reset(); pending.Clear(); }
            int read;
            while ((read = await stream.ReadAsync(bytes.AsMemory(), CancellationToken.None)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                await EmitAsync(count);
            }
            await output.FlushAsync();
        }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadAvailableAsync();
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReadAvailableAsync();
            var count = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            await EmitAsync(count, final: true);
            await output.FlushAsync();
        }
        finally { if (stream is not null) await stream.DisposeAsync(); }
    }
}

internal static class ParentLifetime
{
    public static async Task WatchAsync(int parentId, CancellationTokenSource lifetime)
    {
        if (parentId <= 0 || parentId == Environment.ProcessId) throw new UnidotException("--parent-pid must identify another process.");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            await parent.WaitForExitAsync(lifetime.Token);
            lifetime.Cancel();
        }
        catch (ArgumentException) { lifetime.Cancel(); }
        catch (InvalidOperationException) { lifetime.Cancel(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}

// Closing this CLI (including an IDE force-stop) closes the job handle and kills only its owned Player tree.
internal sealed class OwnedProcessJob : IDisposable
{
    private readonly JobHandle handle;
    public OwnedProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(JobHandle job, int type, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
