using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Unidot;

internal sealed record BuildOptions(bool Dependencies = true, bool Deploy = true, bool Force = false, bool KeepGoing = false);
internal sealed record BuildState(string Fingerprint, string DllHash, string PdbHash);
internal sealed record BuildFailure(string Assembly, string Reason, bool Blocked);
internal sealed record BuildResult(int Compiled, int Cached, IReadOnlyDictionary<string, string> Outputs, IReadOnlyList<BuildFailure>? Failures = null);

internal sealed class BuildEngine(Configuration config, UnityToolchain toolchain, AssemblyGraph graph, PlayerLayout player)
{
    private readonly ReferenceResolver resolver = new(config, graph, player);

    public async Task<BuildResult> BuildAsync(IEnumerable<string> targets, BuildOptions options, CancellationToken cancellationToken)
    {
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
        var stage = Path.Combine(config.GeneratedDirectory, "staging", Guid.NewGuid().ToString("N"));
        var hashes = new ContentHashes();
        var failures = new List<BuildFailure>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var logDirectory = Path.Combine(config.GeneratedDirectory, "logs", "build-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        try
        {
            foreach (var node in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                var fingerprint = Fingerprint(node, references, arguments, hashes);
                BuildState? state = null;
                if (File.Exists(stateFile))
                    try { state = JsonSerializer.Deserialize<BuildState>(File.ReadAllText(stateFile), Configuration.JsonOptions); }
                    catch (JsonException) { }
                if (!options.Force && state?.Fingerprint == fingerprint && File.Exists(output) && File.Exists(pdb) &&
                    hashes.Get(output) == state.DllHash && hashes.Get(pdb) == state.PdbHash)
                {
                    Console.WriteLine($"[cached] {node.Name}");
                    cached++;
                    built[node.Name] = output;
                    continue;
                }
                var directory = Path.Combine(stage, node.Name);
                Directory.CreateDirectory(directory);
                var stagedDll = Path.Combine(directory, node.Name + ".dll");
                var stagedPdb = Path.ChangeExtension(stagedDll, ".pdb");
                var response = ResponseFile(node, references, stagedDll, stagedPdb, arguments);
                var responseFile = Path.Combine(config.GeneratedDirectory, "rsp", node.Name + ".rsp");
                ProjectGenerator.WriteIfChanged(responseFile, response);
                Console.WriteLine($"[compile] {node.Name} ({node.Sources.Count} sources)");
                var exit = await Processes.ExecuteAsync(toolchain.Dotnet, ["exec", toolchain.Compiler, "/noconfig", "@" + responseFile], config.UnityProject, cancellationToken,
                    Path.Combine(logDirectory, node.Name + ".log"));
                if (exit != 0) throw new UnidotException($"Compilation failed for {node.Name} (exit {exit}). Player files were not changed.");
                if (!File.Exists(stagedDll)) throw new UnidotException($"Compiler did not produce {stagedDll}.");
                if (config.UnityIlppPlugins.Length > 0)
                {
                    var host = Environment.ProcessPath ?? throw new UnidotException("Cannot locate the current CLI process for ILPP.");
                    var parameters = new List<string>();
                    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) parameters.Add(typeof(Program).Assembly.Location);
                    parameters.AddRange(["ilpp", "--config", config.FilePath ?? Path.Combine(config.Directory, "unidot.json"), "--dll", stagedDll, "--pdb", stagedPdb, "--rsp", responseFile]);
                    foreach (var plugin in config.UnityIlppPlugins) parameters.AddRange(["--processor", plugin]);
                    Console.WriteLine($"[unity ilpp] {node.Name}");
                    exit = await Processes.ExecuteAsync(host, parameters, config.Directory, cancellationToken, Path.Combine(logDirectory, node.Name + ".ilpp.log"));
                    if (exit != 0) throw new UnidotException($"Unity IL postprocessing failed for {node.Name} (exit {exit}). Player files were not changed.");
                }
                foreach (var processor in config.PostProcessors.GetValueOrDefault(node.Name) ?? [])
                {
                    if (string.IsNullOrWhiteSpace(processor.Executable)) throw new UnidotException($"Empty postprocessor executable for {node.Name}.");
                    string Expand(string value) => value.Replace("{dll}", stagedDll, StringComparison.Ordinal)
                        .Replace("{pdb}", stagedPdb, StringComparison.Ordinal).Replace("{assembly}", node.Name, StringComparison.Ordinal)
                        .Replace("{project}", config.UnityProject, StringComparison.Ordinal).Replace("{managed}", player.ManagedDirectory, StringComparison.Ordinal);
                    var executable = Expand(processor.Executable);
                    if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
                        executable = Path.GetFullPath(executable, config.Directory);
                    Console.WriteLine($"[postprocess] {node.Name}: {executable}");
                    exit = await Processes.ExecuteAsync(executable, processor.Arguments.Select(Expand), config.Directory, cancellationToken);
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
                }
                catch (Exception error) when (options.KeepGoing && error is UnidotException or IOException or UnauthorizedAccessException)
                {
                    failed.Add(node.Name);
                    failures.Add(new(node.Name, error.Message, false));
                    Console.Error.WriteLine($"[failed] {node.Name}: {error.Message}");
                }
            }
            if (options.Deploy) Deployment.Deploy(config, player, built);
            ProjectGenerator.WriteIfChanged(Path.Combine(config.GeneratedDirectory, "build-report.json"), JsonSerializer.Serialize(new
            {
                Timestamp = DateTime.UtcNow.ToString("O"), config.BuildRoots, config.ReferenceMode,
                Selected = order.Select(n => n.Name), Compiled = compiled, Cached = cached, Outputs = built, Failures = failures, LogDirectory = logDirectory,
                RawOnly = !options.Deploy
            }, Configuration.JsonOptions));
            Console.WriteLine($"Build {(failures.Count == 0 ? "succeeded" : "finished with failures")}: {compiled} compiled, {cached} cached, {failures.Count} failed/blocked{(options.Deploy ? ", deployed to Player" : ", no deployment")}.");
            return new(compiled, cached, built, failures);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private string[] ExtraArguments(AssemblyNode node)
    {
        var arguments = new List<string>(config.CompilerArguments);
        var files = new List<string> { Path.Combine(config.UnityProject, "Assets", "csc.rsp") };
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
        var values = new List<string> { "Unidot-build-v1", config.ReferenceMode, hashes.Get(typeof(BuildEngine).Assembly.Location), node.Name, config.LanguageVersion, node.Definition.AllowUnsafeCode.ToString(), string.Join(';', node.Defines),
            hashes.Get(toolchain.Compiler), toolchain.Version, config.UnityProject, JsonSerializer.Serialize(config.PostProcessors.GetValueOrDefault(node.Name)), string.Join('\n', extra) };
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
    public static async Task<int> ExecuteAsync(string executable, IEnumerable<string> arguments, string directory, CancellationToken cancellationToken, string? logPath = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new UnidotException($"Could not start {executable}.");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task CopyAsync(StreamReader source, TextWriter destination)
        {
            while (await source.ReadLineAsync(cancellationToken) is { } line)
            {
                if (logPath is not null) lines.Enqueue(line);
                await destination.WriteLineAsync(line);
            }
        }
        var streams = Task.WhenAll(CopyAsync(process.StandardOutput, Console.Out), CopyAsync(process.StandardError, Console.Error));
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
        return process.ExitCode;
    }
}
