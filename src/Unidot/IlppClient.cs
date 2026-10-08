using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Unidot;

internal sealed class IlppClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderr;
    private readonly bool verbose;
    private readonly BuildDiagnostics? diagnostics;
    private int sequence;
    internal int ProcessId => process.Id;
    private IlppClient(Process process, bool verbose, BuildDiagnostics? diagnostics, string? stderrLog)
    {
        this.process = process;
        this.verbose = verbose;
        this.diagnostics = diagnostics;
        stderr = PumpErrorsAsync(stderrLog);
    }

    internal static (string Executable, List<string> Arguments) CliCommand()
    {
        var host = Environment.ProcessPath ?? "dotnet";
        var parameters = new List<string>();
        if (Assembly.GetEntryAssembly() != typeof(Program).Assembly) { host = "dotnet"; parameters.Add(typeof(Program).Assembly.Location); }
        else if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) parameters.Add(typeof(Program).Assembly.Location);
        return (host, parameters);
    }

    public static async Task<IlppClient> StartAsync(Configuration config, CancellationToken cancellationToken, BuildDiagnostics? diagnostics = null, string? stderrLog = null)
    {
        var (host, parameters) = CliCommand();
        parameters.AddRange(["ilpp-worker", "--config", config.FilePath ?? Path.Combine(config.Directory, "unidot.json")]);
        foreach (var plugin in config.UnityIlppPlugins) parameters.AddRange(["--processor", plugin]);
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = config.Directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var parameter in parameters) start.ArgumentList.Add(parameter);
        var process = Process.Start(start) ?? throw new UnidotException("Could not start the ILPP worker.");
        var client = new IlppClient(process, config.Verbose, diagnostics, stderrLog);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready != "{\"ready\":true}") throw new UnidotException("ILPP worker could not initialize; see its stderr diagnostics.");
            var message = $"[ilpp worker] Ready (PID {process.Id}); reused for this build.";
            if (diagnostics is not null) diagnostics.Detail(message);
            else if (config.Verbose) Console.WriteLine(message);
            return client;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<int> ProcessAsync(string dll, string pdb, string rsp, string log, CancellationToken cancellationToken)
    {
        var request = new IlppRequest(++sequence, dll, pdb, rsp);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, IlPostProcessing.WorkerJson).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null) throw new UnidotException("ILPP worker exited before completing the assembly.");
            var reply = JsonSerializer.Deserialize<IlppReply>(line, IlPostProcessing.WorkerJson) ?? throw new UnidotException("Invalid ILPP worker response.");
            if (reply.Id != request.Id) throw new UnidotException("ILPP worker response does not match its request.");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllLines(log, reply.Lines.Concat(reply.Error is null ? [] : new[] { reply.Error }), new UTF8Encoding(false));
            foreach (var message in reply.Lines)
            {
                if (diagnostics is not null) diagnostics.Ilpp(message);
                else if (verbose || BuildDiagnostics.IsError(message)) Console.WriteLine(message);
            }
            if (reply.Error is not null)
            {
                if (diagnostics is not null) diagnostics.Error(reply.Error);
                else Console.Error.WriteLine(verbose ? reply.Error : reply.Error.Split('\n')[0].TrimEnd('\r'));
            }
            return reply.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }

    private async Task PumpErrorsAsync(string? path)
    {
        if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = path is null ? null : new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (log is not null) await log.WriteLineAsync(line);
            if (diagnostics is not null) diagnostics.Ilpp(line, true);
            else Console.Error.WriteLine("[ilpp worker] " + line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await stderr;
        }
        finally { process.Dispose(); }
    }
}
