using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unidot;

internal sealed record IlppRequest(int Id, string Dll, string Pdb, string Rsp);
internal sealed record IlppReply(int Id, int ExitCode, string[] Lines, string? Error, double ProcessingMs);

// One process per build may retain processor assemblies; every request gets a fresh processor instance.
internal static class IlPostProcessing
{
    internal static readonly JsonSerializerOptions WorkerJson = new(Configuration.JsonOptions) { WriteIndented = false };

    public static int Execute(Configuration config, Arguments cli)
    {
        var dll = Path.GetFullPath(cli.Value("dll") ?? throw new UnidotException("ilpp requires --dll."));
        using var host = new ProcessorHost(config, cli.Values("processor"));
        return host.Process(dll, Path.GetFullPath(cli.Value("pdb") ?? Path.ChangeExtension(dll, ".pdb")),
            cli.Value("rsp") ?? Path.Combine(config.GeneratedDirectory, "rsp", AssemblyMetadata.Name(dll) + ".rsp"), Console.Out);
    }

    public static async Task<int> WorkerAsync(Configuration config, Arguments cli, CancellationToken cancellationToken)
    {
        using var host = new ProcessorHost(config, cli.Values("processor"));
        var protocol = Console.Out;
        await protocol.WriteLineAsync("{\"ready\":true}");
        await protocol.FlushAsync();
        while (await Console.In.ReadLineAsync(cancellationToken) is { } line)
        {
            var request = JsonSerializer.Deserialize<IlppRequest>(line, WorkerJson) ?? throw new UnidotException("Invalid ILPP worker request.");
            using var logs = new StringWriter();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            IlppReply reply;
            Console.SetOut(logs);
            try
            {
                var exit = host.Process(request.Dll, request.Pdb, request.Rsp, logs);
                reply = new(request.Id, exit, logs.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray(), null, timer.Elapsed.TotalMilliseconds);
            }
            catch (Exception error)
            {
                reply = new(request.Id, 1, logs.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray(), error.ToString(), timer.Elapsed.TotalMilliseconds);
            }
            finally { Console.SetOut(protocol); }
            await protocol.WriteLineAsync(JsonSerializer.Serialize(reply, WorkerJson));
            await protocol.FlushAsync();
        }
        return 0;
    }

    private sealed class ProcessorHost : IDisposable
    {
        private readonly Type baseType;
        private readonly Type memoryType;
        private readonly Type candidateType;
        private readonly List<(PluginContext Context, Type[] Types)> processors = [];

        public ProcessorHost(Configuration config, string[] plugins)
        {
            if (plugins.Length == 0) throw new UnidotException("ILPP requires at least one --processor DLL.");
            var toolchain = UnityToolchain.Discover(config);
            var commonPath = Path.Combine(toolchain.DataDirectory, "Tools", "ilpp", "Unity.CompilationPipeline.Common", "Unity.CompilationPipeline.Common.dll");
            var common = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "Unity.CompilationPipeline.Common")
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(commonPath);
            var contract = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.ICompiledAssembly", true)!;
            baseType = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.ILPostProcessor", true)!;
            memoryType = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.InMemoryAssembly", true)!;
            candidateType = CreateCandidateType(contract);
            var player = PlayerLayout.Discover(config.Player);
            var directories = new List<string> { player.ManagedDirectory, Path.Combine(config.UnityProject, "Library", "ScriptAssemblies") };
            var cache = Path.Combine(config.UnityProject, "Library", "PackageCache");
            if (Directory.Exists(cache))
            {
                directories.AddRange(Directory.EnumerateDirectories(cache, "com.unity.nuget.mono-cecil@*"));
                directories.AddRange(Directory.EnumerateDirectories(cache, "com.unity.burst@*").Select(p => Path.Combine(p, "Unity.Burst.CodeGen")).Where(Directory.Exists));
            }
            try
            {
                foreach (var pluginPath in plugins)
                {
                    var plugin = Path.GetFullPath(pluginPath);
                    var context = new PluginContext(common, directories.Prepend(Path.GetDirectoryName(plugin)!).ToArray());
                    try
                    {
                        var assembly = context.LoadFromAssemblyPath(plugin);
                        Type[] available;
                        try { available = assembly.GetTypes(); }
                        catch (ReflectionTypeLoadException error)
                        { throw new UnidotException($"Cannot load ILPP dependencies for {plugin}: {string.Join("; ", error.LoaderExceptions.Select(e => e?.Message))}"); }
                        var types = available.Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
                        if (types.Length == 0) throw new UnidotException($"No Unity ILPostProcessor found in {plugin}.");
                        processors.Add((context, types));
                    }
                    catch { context.Unload(); throw; }
                }
            }
            catch { Dispose(); throw; }
        }

        public int Process(string dll, string pdb, string rsp, TextWriter log)
        {
            var name = AssemblyMetadata.Name(dll);
            var lines = File.ReadAllLines(rsp);
            var references = lines.Select(l => Regex.Match(l, "^/reference:\"([^\"]+)\"$")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
            var defines = lines.FirstOrDefault(l => l.StartsWith("/define:", StringComparison.Ordinal))?[8..].Split([';', ','], StringSplitOptions.RemoveEmptyEntries) ?? [];
            var identity = AssemblyMetadata.Identity(dll);
            var changed = 0;
            foreach (var (_, types) in processors)
                foreach (var type in types)
                {
                    try
                    {
                        var processor = baseType.GetMethod("GetInstance")!.Invoke(Activator.CreateInstance(type), null)!;
                        var candidate = Activator.CreateInstance(candidateType)!;
                        candidateType.GetField("Name")!.SetValue(candidate, name);
                        candidateType.GetField("References")!.SetValue(candidate, references);
                        candidateType.GetField("Defines")!.SetValue(candidate, defines);
                        candidateType.GetField("InMemoryAssembly")!.SetValue(candidate, Activator.CreateInstance(memoryType, File.ReadAllBytes(dll), File.ReadAllBytes(pdb)));
                        if (!(bool)baseType.GetMethod("WillProcess")!.Invoke(processor, [candidate])!)
                        { log.WriteLine($"[ilpp skip] {name}: {type.FullName}"); continue; }
                        var result = baseType.GetMethod("Process")!.Invoke(processor, [candidate]);
                        if (result is null) continue;
                        var hasErrors = false;
                        if (Member(result, "Diagnostics") is System.Collections.IEnumerable diagnostics)
                            foreach (var diagnostic in diagnostics)
                            {
                                var severity = Member(diagnostic!, "DiagnosticType")?.ToString();
                                log.WriteLine($"[ilpp {severity}] {name}: {Member(diagnostic!, "MessageData")}");
                                hasErrors |= severity == "Error";
                            }
                        if (hasErrors) throw new UnidotException($"IL postprocessor {type.FullName} reported errors for {name}.");
                        var memory = Member(result, "InMemoryAssembly");
                        if (memory is null) { log.WriteLine($"[ilpp unchanged] {name}: {type.FullName}"); continue; }
                        var bytes = (byte[])Member(memory, "PeData")!;
                        var symbols = (byte[])Member(memory, "PdbData")!;
                        if (bytes.Length == 0 || symbols.Length == 0) throw new UnidotException($"{type.FullName} did not preserve DLL/PDB output.");
                        File.WriteAllBytes(dll, bytes); File.WriteAllBytes(pdb, symbols);
                        if (AssemblyMetadata.Identity(dll) != identity) throw new UnidotException($"{type.FullName} changed assembly identity.");
                        log.WriteLine($"[ilpp processed] {name}: {type.FullName}"); changed++;
                    }
                    catch (TargetInvocationException error) { throw new UnidotException($"ILPP failed for {name}: {error.InnerException}"); }
                }
            log.WriteLine($"ILPP completed for {name}: {changed} processing results.");
            return 0;
        }

        public void Dispose() { foreach (var (context, _) in processors) context.Unload(); processors.Clear(); }
    }

    private static object? Member(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value) ?? value.GetType().GetField(name)?.GetValue(value);

    private static Type CreateCandidateType(Type contract)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new("Unidot.IlppCandidate." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("Candidate").DefineType("Candidate", TypeAttributes.Public | TypeAttributes.Sealed);
        builder.AddInterfaceImplementation(contract); builder.DefineDefaultConstructor(MethodAttributes.Public);
        foreach (var property in contract.GetProperties())
        {
            var field = builder.DefineField(property.Name, property.PropertyType, FieldAttributes.Public);
            var getter = builder.DefineMethod("get_" + property.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Final | MethodAttributes.NewSlot,
                property.PropertyType, Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(getter, property.GetMethod!);
        }
        return builder.CreateType()!;
    }

    private sealed class PluginContext(Assembly common, string[] directories) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == common.GetName().Name) return common;
            if (name.Name is "mscorlib" or "netstandard" or "System" or "System.Core" || name.Name!.StartsWith("System.", StringComparison.Ordinal)) return null;
            foreach (var directory in directories)
            {
                var file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return LoadFromAssemblyPath(file);
            }
            return null;
        }
    }
}
