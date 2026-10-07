using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Unidot;

// Runs compiled Unity ILPP plug-ins in a separate CLI process. No Unity Editor process is required.
internal static class IlPostProcessing
{
    public static int Execute(Configuration config, Arguments cli)
    {
        var dll = Path.GetFullPath(cli.Value("dll") ?? throw new UnidotException("ilpp requires --dll."));
        var pdb = Path.GetFullPath(cli.Value("pdb") ?? Path.ChangeExtension(dll, ".pdb"));
        var plugins = cli.Values("processor").Select(Path.GetFullPath).ToArray();
        if (plugins.Length == 0) throw new UnidotException("ilpp requires at least one --processor <DLL>.");
        var toolchain = UnityToolchain.Discover(config);
        var commonPath = Path.Combine(toolchain.DataDirectory, "Tools", "ilpp", "Unity.CompilationPipeline.Common", "Unity.CompilationPipeline.Common.dll");
        var common = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "Unity.CompilationPipeline.Common")
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(commonPath);
        var contract = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.ICompiledAssembly", true)!;
        var baseType = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.ILPostProcessor", true)!;
        var memoryType = common.GetType("Unity.CompilationPipeline.Common.ILPostProcessing.InMemoryAssembly", true)!;
        var player = PlayerLayout.Discover(config.Player);
        var name = AssemblyMetadata.Name(dll);
        var rsp = cli.Value("rsp") ?? Path.Combine(config.GeneratedDirectory, "rsp", name + ".rsp");
        var lines = File.ReadAllLines(rsp);
        var references = lines.Select(l => Regex.Match(l, "^/reference:\"([^\"]+)\"$")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
        var defines = lines.FirstOrDefault(l => l.StartsWith("/define:", StringComparison.Ordinal))?[8..].Split([';', ','], StringSplitOptions.RemoveEmptyEntries) ?? [];
        var dependencyDirectories = new List<string> { player.ManagedDirectory, Path.Combine(config.UnityProject, "Library", "ScriptAssemblies") };
        var cache = Path.Combine(config.UnityProject, "Library", "PackageCache");
        if (Directory.Exists(cache))
        {
            dependencyDirectories.AddRange(Directory.EnumerateDirectories(cache, "com.unity.nuget.mono-cecil@*"));
            dependencyDirectories.AddRange(Directory.EnumerateDirectories(cache, "com.unity.burst@*").Select(p => Path.Combine(p, "Unity.Burst.CodeGen")).Where(Directory.Exists));
        }
        var identity = AssemblyMetadata.Identity(dll);
        var changed = 0;
        foreach (var plugin in plugins)
        {
            var context = new PluginContext(common, dependencyDirectories.Prepend(Path.GetDirectoryName(plugin)!).ToArray());
            try
            {
                var assembly = context.LoadFromAssemblyPath(plugin);
                Type[] availableTypes;
                try { availableTypes = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error)
                { throw new UnidotException($"Cannot load ILPP dependencies for {plugin}: {string.Join("; ", error.LoaderExceptions.Select(e => e?.Message))}"); }
                var types = availableTypes.Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
                if (types.Length == 0) throw new UnidotException($"No Unity ILPostProcessor found in {plugin}.");
                foreach (var type in types)
                {
                    var processor = baseType.GetMethod("GetInstance")!.Invoke(Activator.CreateInstance(type), null)!;
                    var candidate = CreateCandidate(contract, memoryType, name, references, defines, File.ReadAllBytes(dll), File.ReadAllBytes(pdb));
                    if (!(bool)baseType.GetMethod("WillProcess")!.Invoke(processor, [candidate])!)
                    {
                        Console.WriteLine($"[ilpp skip] {name}: {type.FullName}");
                        continue;
                    }
                    var result = baseType.GetMethod("Process")!.Invoke(processor, [candidate]);
                    if (result is null) continue;
                    var hasErrors = false;
                    if (Member(result, "Diagnostics") is System.Collections.IEnumerable diagnostics)
                        foreach (var diagnostic in diagnostics)
                        {
                            var severity = Member(diagnostic!, "DiagnosticType")?.ToString();
                            Console.WriteLine($"[ilpp {severity}] {name}: {Member(diagnostic!, "MessageData")}");
                            hasErrors |= severity == "Error";
                        }
                    if (hasErrors) throw new UnidotException($"IL postprocessor {type.FullName} reported errors for {name}.");
                    var memory = Member(result, "InMemoryAssembly");
                    if (memory is null) { Console.WriteLine($"[ilpp unchanged] {name}: {type.FullName}"); continue; }
                    var bytes = (byte[])Member(memory, "PeData")!;
                    var symbols = (byte[])Member(memory, "PdbData")!;
                    if (bytes.Length == 0 || symbols.Length == 0) throw new UnidotException($"{type.FullName} did not preserve DLL/PDB output.");
                    File.WriteAllBytes(dll, bytes);
                    File.WriteAllBytes(pdb, symbols);
                    if (AssemblyMetadata.Identity(dll) != identity) throw new UnidotException($"{type.FullName} changed assembly identity.");
                    Console.WriteLine($"[ilpp processed] {name}: {type.FullName}");
                    changed++;
                }
            }
            catch (TargetInvocationException error) { throw new UnidotException($"ILPP failed for {name}: {error.InnerException}"); }
            finally { context.Unload(); }
        }
        Console.WriteLine($"ILPP completed for {name}: {changed} processing results.");
        return 0;
    }

    private static object? Member(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value) ?? value.GetType().GetField(name)?.GetValue(value);

    private static object CreateCandidate(Type contract, Type memoryType, string name, string[] references, string[] defines, byte[] dll, byte[] pdb)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new("Unidot.IlppCandidate." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("Candidate").DefineType("Candidate", TypeAttributes.Public | TypeAttributes.Sealed);
        builder.AddInterfaceImplementation(contract);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        foreach (var property in contract.GetProperties())
        {
            var field = builder.DefineField(property.Name, property.PropertyType, FieldAttributes.Public);
            var getter = builder.DefineMethod("get_" + property.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Final | MethodAttributes.NewSlot,
                property.PropertyType, Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(getter, property.GetMethod!);
        }
        var type = builder.CreateType()!;
        var result = Activator.CreateInstance(type)!;
        type.GetField("Name")!.SetValue(result, name);
        type.GetField("References")!.SetValue(result, references);
        type.GetField("Defines")!.SetValue(result, defines);
        type.GetField("InMemoryAssembly")!.SetValue(result, Activator.CreateInstance(memoryType, dll, pdb));
        return result;
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
