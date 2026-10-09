using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Unidot;

internal static class PlayerSession
{
    public static async Task<int> RunAsync(Configuration config, PlayerLayout player, IEnumerable<string> arguments, string? requestedLog, CancellationToken cancellationToken, PlayerLaunchOptions? options = null)
    {
        if (PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Player is already running.");
        var forwarded = arguments.ToArray();
        if (forwarded.Any(a => a.Equals("-logFile", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-logFile=", StringComparison.OrdinalIgnoreCase)))
            throw new UnidotException("Use --log-file <path> so Unidot can follow the Unity log.");
        var log = requestedLog is null ? Path.Combine(config.GeneratedDirectory, "logs", "Player-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log") :
            Path.GetFullPath(requestedLog, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.Combine(player.RootDirectory, ".unidot"));
        FileStream sessionLock;
        try { sessionLock = new FileStream(Path.Combine(player.RootDirectory, ".unidot", "player-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UnidotException("Another Unidot Player session is already running in this directory."); }
        using (sessionLock)
        {
            options ??= new(config.RunBackend, config.NativeFallbackToExe, config.NativeHost);
            ProcessStartInfo Standard(string? targetLog = null)
            {
                var start = StandardStart(player, forwarded, targetLog ?? log);
                if (options.Environment is not null)
                    foreach (var (key, value) in options.Environment) start.Environment[key] = value;
                if (options.InjectOriginal) start.WindowStyle = ProcessWindowStyle.Minimized;
                return start;
            }
            if (options.Backend == "exe") return await RunProcessAsync(Standard(), log, cancellationToken, verbose: config.Verbose, injectOriginal: options.InjectOriginal);
            if (options.Backend != "native") throw new UnidotException("Run backend must be exe or native.");
            var diagnostics = log + ".native.json";
            ProcessStartInfo native;
            try { native = await NativePlayerBackend.CreateAsync(config, player, log, diagnostics, forwarded, options.NativeHost, cancellationToken); }
            catch (UnidotException error) when (options.FallbackToExe && player.HasExecutable)
            {
                Console.Error.WriteLine("Native preflight failed: " + error.Message + " Falling back to standard EXE.");
                return await RunProcessAsync(Standard(), log, cancellationToken, verbose: config.Verbose);
            }
            if (options.Environment is not null)
                foreach (var (key, value) in options.Environment) native.Environment[key] = value;
            int result;
            try { result = await RunProcessAsync(native, log, cancellationToken, verbose: config.Verbose); }
            catch (Win32Exception error) when (options.FallbackToExe && player.HasExecutable)
            {
                Console.Error.WriteLine("Native Host process could not start: " + error.Message + " Falling back to standard EXE.");
                return await RunProcessAsync(Standard(log + ".fallback.log"), log + ".fallback.log", cancellationToken, verbose: config.Verbose);
            }
            if (NativePlayerBackend.CanFallback(diagnostics, result))
            {
                Console.Error.WriteLine("Native startup failed: " + NativePlayerBackend.Describe(diagnostics));
                if (options.FallbackToExe && player.HasExecutable)
                {
                    Console.WriteLine("Falling back to standard EXE.");
                    return await RunProcessAsync(Standard(log + ".fallback.log"), log + ".fallback.log", cancellationToken, verbose: config.Verbose);
                }
            }
            return result;
        }
    }

    internal static ProcessStartInfo StandardStart(PlayerLayout player, IEnumerable<string> arguments, string log)
    {
        if (!player.HasExecutable) throw new UnidotException("Standard EXE fallback is unavailable: " + player.Executable);
        var start = new ProcessStartInfo(player.Executable) { WorkingDirectory = player.RootDirectory, UseShellExecute = false };
        start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(log);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<int> RunProcessAsync(ProcessStartInfo start, string logPath, CancellationToken cancellationToken, TextWriter? destination = null, bool verbose = false, bool injectOriginal = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logPath = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // Unity also truncates its log on launch. Start empty to avoid streaming an older session.
        using (new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        var output = TextWriter.Synchronized(destination ?? Console.Out);
        var errors = destination is null ? TextWriter.Synchronized(Console.Error) : output;
        using var unity = new RuntimeLogWriter(output, verbose, channel: "unity", errorOutput: errors);
        using var stdout = new RuntimeLogWriter(output, verbose, channel: "player stdout", errorOutput: errors);
        using var stderr = new RuntimeLogWriter(errors, verbose, stderr: true, channel: "player stderr");
        await using var stdoutLog = new StreamWriter(new FileStream(logPath + ".stdout.log", FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
        await using var stderrLog = new StreamWriter(new FileStream(logPath + ".stderr.log", FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var job = OperatingSystem.IsWindows() ? new OwnedProcessJob() : null;
        using var process = Process.Start(start) ?? throw new UnidotException($"Could not start {start.FileName}.");
        using var drain = new CancellationTokenSource();
        Task streams = Task.CompletedTask;
        Task follow = Task.CompletedTask;
        var cancelled = false;
        OriginalPlayerInjection? injection = null;
        try
        {
            job?.Assign(process);
            await output.WriteLineAsync($"Started Player (PID {process.Id}). Log: {logPath}");
            await output.WriteLineAsync($"Native logs: {logPath}.stdout.log / {logPath}.stderr.log");
            await output.WriteLineAsync(verbose ? "Following full Player logs. Ctrl+C closes this Player." : "Showing Player errors and session status. Use --verbose for full logs. Ctrl+C closes this Player.");
            follow = FollowLogAsync(logPath, unity, drain.Token);
            async Task PumpAsync(StreamReader reader, StreamWriter log, RuntimeLogWriter console)
            {
                try
                {
                    var chars = new char[4096];
                    int read;
                    while ((read = await reader.ReadAsync(chars.AsMemory(), drain.Token)) > 0)
                    {
                        await log.WriteAsync(chars.AsMemory(0, read), CancellationToken.None);
                        await console.WriteAsync(chars.AsMemory(0, read), CancellationToken.None);
                    }
                }
                finally { await console.CompleteAsync(); await log.FlushAsync(); }
            }
            streams = Task.WhenAll(PumpAsync(process.StandardOutput, stdoutLog, stdout), PumpAsync(process.StandardError, stderrLog, stderr));
            if (injectOriginal) injection = await OriginalPlayerInjection.AttachAsync(process, cancellationToken);
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
            injection?.Dispose();
            // Finish the file tail, including a final line without a newline. Descendant-held stdout pipes are bounded.
            try { await streams.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { } catch (TimeoutException) { }
            drain.Cancel();
            try { await follow; } catch (OperationCanceledException) { }
            try { await streams; } catch (OperationCanceledException) { }
            await unity.CompleteAsync();
        }
        await (process.ExitCode == 0 || cancelled ? output : errors).WriteLineAsync($"Player {(cancelled ? "stopped" : "exited")} (PID {process.Id}, code {process.ExitCode}). Log: {logPath}");
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
