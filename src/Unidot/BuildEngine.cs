using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Unidot;

internal sealed record BuildOptions(bool Dependencies = true, bool Deploy = true, bool Force = false, bool KeepGoing = false);
internal sealed record BuildState(string Fingerprint, string DllHash, string PdbHash);
internal sealed record BuildFailure(string Assembly, string Reason, bool Blocked);
internal sealed record AssemblyTiming(string Assembly, bool Cached, double FingerprintMs, double CompileMs, double IlppMs, double TotalMs);
internal sealed record BuildResult(int Compiled, int Cached, IReadOnlyDictionary<string, string> Outputs, IReadOnlyList<BuildFailure>? Failures = null);

internal sealed class BuildEngine(Configuration config, UnityToolchain toolchain, AssemblyGraph graph, PlayerLayout player)
{
    private readonly ReferenceResolver resolver = new(config, graph, player);

    public async Task<BuildResult> BuildAsync(IEnumerable<string> targets, BuildOptions options, CancellationToken cancellationToken)
    {
        var buildTimer = Stopwatch.StartNew();
        Directory.CreateDirectory(config.GeneratedDirectory);
        using var buildLock = AcquireLock(config.GeneratedDirectory);
        if (options.KeepGoing && options.Deploy) throw new UnidotException("--keep-going requires --no-deploy; partial builds are never deployed.");
        var order = graph.BuildOrder(targets, options.Dependencies, n => BuildScope.Contains(config, n), config.ReferenceMode == "player");
        if (order.Count == 0) throw new UnidotException("No assemblies selected.");
        foreach (var node in order)
        {
            if (node.Sources.Count == 0) throw new UnidotException($"{node.Name} has no source files.");
            if (options.Deploy && !resolver.Managed.ContainsKey(node.Name))
                throw new UnidotException($"{node.Name} is not registered in the base Player. Use --no-deploy to compile, or rebuild the base Player first.");
            var requirements = PostProcessingPolicy.Requirements(node);
            if (requirements.Length > 0 && config.UnityIlppPlugins.Length == 0 && (config.PostProcessors.GetValueOrDefault(node.Name)?.Length ?? 0) == 0)
            {
                var message = $"{node.Name} may require {string.Join(", ", requirements)} IL postprocessing.";
                if (options.Deploy) throw new UnidotException(message + " Configure postProcessors before deployment; --no-deploy can produce a raw DLL for inspection.");
                Console.WriteLine("Warning: " + message + " Producing raw compiler output only.");
            }
        }
        var built = new Dictionary<string, string>(StringComparer.Ordinal);
        var compiled = 0;
        var cached = 0;
        // The build lock serializes this workspace. Stable paths keep deterministic DLL/PDB hashes stable between rebuilds.
        var stage = Path.Combine(config.GeneratedDirectory, "staging", "current");
        var hashes = new ContentHashes();
        var failures = new List<BuildFailure>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var logDirectory = Path.Combine(config.GeneratedDirectory, "logs", "build-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        using var diagnostics = new BuildDiagnostics(logDirectory, config.Verbose);
        diagnostics.Discovery(graph.Warnings);
        Console.WriteLine($"Building {order.Count} assemblies...");
        diagnostics.Record("build", $"Unity {toolchain.Version} | {config.Platform} | {player.ManagedDirectory}");
        var progressTime = TimeSpan.Zero;
        var ilppAssemblies = 0;
        var timings = new List<AssemblyTiming>();
        IlppClient? ilppWorker = null;
        var currentAssembly = "build";
        void SaveReport(string status) => ProjectGenerator.WriteIfChanged(Path.Combine(config.GeneratedDirectory, "build-report.json"), JsonSerializer.Serialize(new
        {
            Timestamp = DateTime.UtcNow.ToString("O"), Status = status, config.BuildRoots, config.ReferenceMode,
            Selected = order.Select(n => n.Name), Compiled = compiled, Cached = cached, Outputs = built, Failures = failures, LogDirectory = logDirectory,
            RawOnly = !options.Deploy, ElapsedMs = buildTimer.Elapsed.TotalMilliseconds, Timings = timings,
            Warnings = diagnostics.Warnings, diagnostics.CompilerWarnings, diagnostics.IlppWarnings, diagnostics.DiscoveryWarnings, IlppAssemblies = ilppAssemblies, IlppProcessed = diagnostics.Processed
        }, Configuration.JsonOptions));
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            foreach (var node in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentAssembly = node.Name;
                if (!config.Verbose && buildTimer.Elapsed - progressTime > TimeSpan.FromSeconds(3))
                {
                    Console.WriteLine($"Building {compiled + cached + failures.Count}/{order.Count}: {node.Name}...");
                    progressTime = buildTimer.Elapsed;
                }
                var assemblyTimer = Stopwatch.StartNew();
                var fingerprintMs = 0d; var compileMs = 0d; var ilppMs = 0d;
                try
                {
                var failedDependencies = node.ResolvedReferences.Where(failed.Contains).ToArray();
                if (options.Dependencies && failedDependencies.Length > 0)
                {
                    failed.Add(node.Name);
                    failures.Add(new(node.Name, "Dependency failed: " + string.Join(", ", failedDependencies), true));
                    Console.Error.WriteLine($"[blocked] {node.Name}: {string.Join(", ", failedDependencies)}");
                    continue;
                }
                var references = resolver.Resolve(node, options.Dependencies || config.ReferenceMode == "player" ? built : null);
                var output = resolver.OutputPath(node.Name);
                var pdb = Path.ChangeExtension(output, ".pdb");
                var stateFile = Path.Combine(config.GeneratedDirectory, "state", node.Name + ".json");
                var arguments = ExtraArguments(node);
                var phaseTimer = Stopwatch.StartNew();
                var fingerprint = Fingerprint(node, references, arguments, hashes);
                fingerprintMs = phaseTimer.Elapsed.TotalMilliseconds;
                BuildState? state = null;
                if (File.Exists(stateFile))
                    try { state = JsonSerializer.Deserialize<BuildState>(File.ReadAllText(stateFile), Configuration.JsonOptions); }
                    catch (JsonException) { }
                if (!options.Force && state?.Fingerprint == fingerprint && File.Exists(output) && File.Exists(pdb) &&
                    hashes.Get(output) == state.DllHash && hashes.Get(pdb) == state.PdbHash)
                {
                    diagnostics.Detail($"[cached] {node.Name}");
                    cached++;
                    built[node.Name] = output;
                    timings.Add(new(node.Name, true, fingerprintMs, 0, 0, assemblyTimer.Elapsed.TotalMilliseconds));
                    continue;
                }
                var directory = Path.Combine(stage, node.Name);
                Directory.CreateDirectory(directory);
                var stagedDll = Path.Combine(directory, node.Name + ".dll");
                var stagedPdb = Path.ChangeExtension(stagedDll, ".pdb");
                var response = ResponseFile(node, references, stagedDll, stagedPdb, arguments);
                var responseFile = Path.Combine(config.GeneratedDirectory, "rsp", node.Name + ".rsp");
                ProjectGenerator.WriteIfChanged(responseFile, response);
                diagnostics.Detail($"[compile] {node.Name} ({node.Sources.Count} sources)");
                var compilerArguments = new List<string> { "exec", toolchain.Compiler, "/noconfig" };
                if (config.SharedCompilation) compilerArguments.Add("/shared");
                compilerArguments.Add("@" + responseFile);
                phaseTimer.Restart();
                var exit = await Processes.ExecuteAsync(toolchain.Dotnet, compilerArguments, config.UnityProject, cancellationToken,
                    Path.Combine(logDirectory, node.Name + ".log"), diagnostics.Compiler);
                compileMs = phaseTimer.Elapsed.TotalMilliseconds;
                if (exit != 0) throw new UnidotException($"Compilation failed for {node.Name} (exit {exit}). Player files were not changed.");
                if (!File.Exists(stagedDll)) throw new UnidotException($"Compiler did not produce {stagedDll}.");
                if (config.UnityIlppPlugins.Length > 0)
                {
                    diagnostics.Detail($"[unity ilpp] {node.Name}");
                    ilppAssemblies++;
                    phaseTimer.Restart();
                    if (config.ReuseIlppHost)
                    {
                        ilppWorker ??= await IlppClient.StartAsync(config, cancellationToken, diagnostics, Path.Combine(logDirectory, "ilpp-worker.stderr.log"));
                        exit = await ilppWorker.ProcessAsync(stagedDll, stagedPdb, responseFile, Path.Combine(logDirectory, node.Name + ".ilpp.log"), cancellationToken);
                    }
                    else
                    {
                        var (host, parameters) = IlppClient.CliCommand();
                        parameters.AddRange(["ilpp", "--config", config.FilePath ?? Path.Combine(config.Directory, "unidot.json"), "--dll", stagedDll, "--pdb", stagedPdb, "--rsp", responseFile]);
                        foreach (var plugin in config.UnityIlppPlugins) parameters.AddRange(["--processor", plugin]);
                        exit = await Processes.ExecuteAsync(host, parameters, config.Directory, cancellationToken, Path.Combine(logDirectory, node.Name + ".ilpp.log"), diagnostics.Ilpp);
                    }
                    ilppMs = phaseTimer.Elapsed.TotalMilliseconds;
                    if (exit != 0) throw new UnidotException($"Unity IL postprocessing failed for {node.Name} (exit {exit}). Player files were not changed.");
                }
                var processorIndex = 0;
                foreach (var processor in config.PostProcessors.GetValueOrDefault(node.Name) ?? [])
                {
                    if (string.IsNullOrWhiteSpace(processor.Executable)) throw new UnidotException($"Empty postprocessor executable for {node.Name}.");
                    string Expand(string value) => value.Replace("{dll}", stagedDll, StringComparison.Ordinal)
                        .Replace("{pdb}", stagedPdb, StringComparison.Ordinal).Replace("{assembly}", node.Name, StringComparison.Ordinal)
                        .Replace("{project}", config.UnityProject, StringComparison.Ordinal).Replace("{managed}", player.ManagedDirectory, StringComparison.Ordinal);
                    var executable = Expand(processor.Executable);
                    if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
                        executable = Path.GetFullPath(executable, config.Directory);
                    diagnostics.Detail($"[postprocess] {node.Name}: {executable}");
                    exit = await Processes.ExecuteAsync(executable, processor.Arguments.Select(Expand), config.Directory, cancellationToken,
                        Path.Combine(logDirectory, $"{node.Name}.postprocess-{++processorIndex}.log"), diagnostics.Compiler);
                    if (exit != 0) throw new UnidotException($"Postprocessing failed for {node.Name} (exit {exit}). Player files were not changed.");
                }
                if (!File.Exists(stagedDll) || !File.Exists(stagedPdb))
                    throw new UnidotException($"{node.Name}: compilation/postprocessing must produce both DLL and portable PDB.");
                if (AssemblyMetadata.Name(stagedDll) != node.Name) throw new UnidotException($"Postprocessing changed assembly identity for {node.Name}.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(stagedDll, output, true);
                File.Copy(stagedPdb, pdb, true);
                built[node.Name] = output;
                ProjectGenerator.WriteIfChanged(stateFile, JsonSerializer.Serialize(new BuildState(fingerprint, hashes.Get(output), hashes.Get(pdb)), Configuration.JsonOptions));
                compiled++;
                timings.Add(new(node.Name, false, fingerprintMs, compileMs, ilppMs, assemblyTimer.Elapsed.TotalMilliseconds));
                diagnostics.Detail($"[time] {node.Name}: csc {compileMs / 1000:F2}s, ilpp {ilppMs / 1000:F2}s");
                }
                catch (Exception error) when (options.KeepGoing && error is UnidotException or IOException or UnauthorizedAccessException)
                {
                    diagnostics.Record("failure", error.ToString());
                    failed.Add(node.Name);
                    failures.Add(new(node.Name, error.Message, false));
                    timings.Add(new(node.Name, false, fingerprintMs, compileMs, ilppMs, assemblyTimer.Elapsed.TotalMilliseconds));
                    Console.Error.WriteLine($"[failed] {node.Name}: {error.Message}");
                }
            }
            currentAssembly = "deployment";
            if (options.Deploy) Deployment.Deploy(config, player, built);
            SaveReport(failures.Count == 0 ? "succeeded" : "failed");
            if (ilppAssemblies > 0) Console.WriteLine($"ILPP: {ilppAssemblies} assemblies checked, {diagnostics.Processed} processing results, {diagnostics.IlppWarnings} warnings.");
            var summary = $"Build {(failures.Count == 0 ? "succeeded" : "failed")}: {compiled} compiled, {cached} cached, {failures.Count} failed/blocked, {diagnostics.Warnings} warnings{(options.Deploy ? ", deployed to Player" : ", no deployment")}, {buildTimer.Elapsed.TotalSeconds:F2}s.";
            diagnostics.Record("result", summary);
            (failures.Count == 0 ? Console.Out : Console.Error).WriteLine(summary);
            return new(compiled, cached, built, failures);
        }
        catch (Exception error)
        {
            diagnostics.Record("failure", error.ToString());
            failures.Add(new(currentAssembly, error.Message, false));
            SaveReport(error is OperationCanceledException ? "cancelled" : "failed");
            Console.Error.WriteLine($"Build {(error is OperationCanceledException ? "cancelled" : "failed")}: {compiled} compiled, {cached} cached, {diagnostics.Warnings} warnings, {buildTimer.Elapsed.TotalSeconds:F2}s.");
            throw;
        }
        finally
        {
            try { if (ilppWorker is not null) await ilppWorker.DisposeAsync(); }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                finally { Console.WriteLine("Build logs: " + logDirectory); }
            }
        }
    }

    private string[] ExtraArguments(AssemblyNode node)
    {
        var arguments = new List<string>(config.CompilerArguments);
        var files = new List<string> { config.GlobalCompilerResponse ?? Path.Combine(config.UnityProject, "Assets", "csc.rsp") };
        if (node.DefinitionPath.Length > 0) files.Add(Path.Combine(Path.GetDirectoryName(node.DefinitionPath)!, "csc.rsp"));
        foreach (var file in files.Distinct(PathComparer.Instance).Where(File.Exists))
            arguments.AddRange(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        foreach (var argument in arguments)
        {
            var flag = argument.TrimStart('-', '/').Split(':', '=')[0].ToLowerInvariant();
            if (flag is "out" or "pdb" or "target" or "r" or "reference" || argument.StartsWith('@') ||
                argument.Contains("UNITY_EDITOR", StringComparison.Ordinal))
                throw new UnidotException($"Unsupported compiler argument '{argument}'. Output paths, references, and Player defines are managed by Unidot.");
        }
        return arguments.Where(a => !a.TrimStart('-', '/').Split(':')[0].Equals("noconfig", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private string Fingerprint(AssemblyNode node, IReadOnlyDictionary<string, string> references, string[] extra, ContentHashes hashes)
    {
        var values = new List<string> { "Unidot-build-v2-stable-paths", config.ReferenceMode, hashes.Get(typeof(BuildEngine).Assembly.Location), node.Name, config.LanguageVersion, node.Definition.AllowUnsafeCode.ToString(), string.Join(';', node.Defines),
            hashes.Get(toolchain.Compiler), toolchain.Version, config.UnityProject, JsonSerializer.Serialize(config.PostProcessors.GetValueOrDefault(node.Name)), string.Join('\n', extra),
            config.SharedCompilation.ToString(), config.ReuseIlppHost.ToString() };
        foreach (var path in node.Sources.Concat(references.Values).Concat(toolchain.Generators).Concat(config.Analyzers).Concat(config.UnityIlppPlugins).Order(PathComparer.Instance))
            values.Add(path + "=" + hashes.Get(path));
        foreach (var processor in config.PostProcessors.GetValueOrDefault(node.Name) ?? [])
        {
            var executable = Path.GetFullPath(processor.Executable, config.Directory);
            if (File.Exists(executable)) values.Add(executable + "=" + hashes.Get(executable));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
    }

    private string ResponseFile(AssemblyNode node, IReadOnlyDictionary<string, string> references, string dll, string pdb, string[] extra)
    {
        var lines = new List<string>
        {
            "/target:library", "/out:" + Quote(dll), "/pdb:" + Quote(pdb), "/nostdlib+", "/langversion:" + config.LanguageVersion,
            "/deterministic+", "/optimize+", "/debug:portable", node.Definition.AllowUnsafeCode ? "/unsafe+" : "/unsafe-",
            "/nologo", "/utf8output", "/preferreduilang:en-US", "/nowarn:0169,0649,0282,1701,1702", "/define:" + string.Join(';', node.Defines)
        };
        lines.AddRange(references.Values.Select(p => "/reference:" + Quote(p)));
        lines.AddRange(toolchain.Generators.Concat(config.Analyzers).Select(p => "/analyzer:" + Quote(p)));
        var additionalFile = Path.Combine(config.GeneratedDirectory, "Unidot.UnityAdditionalFile.txt");
        ProjectGenerator.WriteIfChanged(additionalFile, config.UnityProject + Environment.NewLine);
        lines.Add("/additionalfile:" + Quote(additionalFile));
        lines.AddRange(extra);
        lines.AddRange(node.Sources.Select(Quote));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";

    internal static FileStream AcquireLock(string directory)
    {
        try { return new FileStream(Path.Combine(directory, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UnidotException("Another Unidot build or deployment is using this workspace."); }
    }
}

internal sealed class ContentHashes
{
    private readonly Dictionary<string, (long Size, long Time, string Hash)> cache = new(PathComparer.Instance);
    public string Get(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new UnidotException($"Build input missing: {path}");
        if (cache.TryGetValue(path, out var entry) && entry.Size == info.Length && entry.Time == info.LastWriteTimeUtc.Ticks) return entry.Hash;
        using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        cache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }
}

internal static class Processes
{
    public static async Task<int> ExecuteAsync(string executable, IEnumerable<string> arguments, string directory, CancellationToken cancellationToken, string? logPath = null, Action<string, bool>? onOutput = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new UnidotException($"Could not start {executable}.");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task CopyAsync(StreamReader source, TextWriter destination, bool stderr)
        {
            while (await source.ReadLineAsync(cancellationToken) is { } line)
            {
                if (logPath is not null || onOutput is not null) lines.Enqueue(line);
                if (onOutput is not null) onOutput(line, stderr);
                else await destination.WriteLineAsync(line);
            }
        }
        var streams = Task.WhenAll(CopyAsync(process.StandardOutput, Console.Out, false), CopyAsync(process.StandardError, Console.Error, true));
        try { await process.WaitForExitAsync(cancellationToken); await streams; }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            try { await streams; } catch (OperationCanceledException) { }
            throw;
        }
        finally
        {
            if (logPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllLines(logPath, lines, new UTF8Encoding(false));
            }
        }
        if (process.ExitCode != 0 && onOutput is not null && !lines.Any(BuildDiagnostics.IsError))
        {
            Console.Error.WriteLine($"Tool exited with code {process.ExitCode}. Last output:");
            foreach (var line in lines.TakeLast(8)) Console.Error.WriteLine(line);
        }
        return process.ExitCode;
    }
}
