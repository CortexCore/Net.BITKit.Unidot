using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Unidot;

internal sealed class ReferenceResolver(Configuration config, AssemblyGraph graph, PlayerLayout player)
{
    private readonly Dictionary<string, string> managed = Directory.EnumerateFiles(player.ManagedDirectory, "*.dll")
        .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Managed => managed;

    public Dictionary<string, string> Resolve(AssemblyNode node, IReadOnlyDictionary<string, string>? built = null, bool allowSourceOutputs = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name)
        {
            name = Path.GetFileNameWithoutExtension(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            if (name == node.Name) return;
            if (built is not null && built.TryGetValue(name, out var output)) result[name] = output;
            else if (managed.TryGetValue(name, out var runtime)) result[name] = runtime;
            else if (allowSourceOutputs && graph.All.TryGetValue(name, out var source) && source.Active && BuildScope.Contains(config, source)) result[name] = OutputPath(name);
            else throw new UnidotException($"{node.Name}: runtime reference {name}.dll is missing from Player Managed. Rebuild the base Player or fix the asmdef.");
        }
        if (config.ReferenceMode == "player")
        {
            foreach (var name in managed.Keys.Where(IsFramework)) Add(name);
            if (!node.Definition.NoEngineReferences)
                foreach (var name in managed.Keys.Where(IsEngine)) Add(name);
            foreach (var name in node.ResolvedReferences.Distinct(StringComparer.Ordinal))
            {
                if (graph.All.TryGetValue(name, out var definition) && !definition.Active) continue;
                if (built?.ContainsKey(name) == true || managed.ContainsKey(name) ||
                    (allowSourceOutputs && graph.All.TryGetValue(name, out var source) && source.Active && BuildScope.Contains(config, source))) Add(name);
            }
            if (node.Definition.OverrideReferences)
            {
                foreach (var name in node.Definition.PrecompiledReferences) Add(name);
            }
            else
            {
                foreach (var name in graph.AutoReferencedPlugins.Where(managed.ContainsKey))
                    if (!graph.All.ContainsKey(name) && !IsFramework(name) && !IsEngine(name)) Add(name);
            }
            // Unresolved GUIDs can describe historical references. Recover only the existing target's actual dependencies,
            // never every game DLL: unrelated namespaces and legacy contracts can shadow the intended types.
            if (node.Errors.Count > 0 && managed.TryGetValue(node.Name, out var previous))
                foreach (var name in AssemblyMetadata.References(previous).Where(managed.ContainsKey))
                    if ((!node.Definition.NoEngineReferences || !IsEngine(name)) &&
                        (!graph.All.TryGetValue(name, out var known) || known.Active)) Add(name);
            foreach (var name in node.ResolvedReferences.Distinct(StringComparer.Ordinal))
                if (built?.ContainsKey(name) == true || (allowSourceOutputs && graph.All.TryGetValue(name, out var source) && source.Active && BuildScope.Contains(config, source))) Add(name);
            return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }
        foreach (var name in managed.Keys.Where(IsFramework)) Add(name);
        if (!node.Definition.NoEngineReferences)
            foreach (var name in managed.Keys.Where(n => n == "UnityEngine" || n.StartsWith("UnityEngine.", StringComparison.Ordinal))) Add(name);
        if (node.Errors.Count > 0) throw new UnidotException($"{node.Name}: {string.Join("; ", node.Errors)}");
        foreach (var name in node.ResolvedReferences.Distinct(StringComparer.Ordinal))
        {
            if (graph.All.TryGetValue(name, out var dependency) && !dependency.Active)
                throw new UnidotException($"{node.Name} references {name}, which is excluded from the Player configuration.");
            Add(name);
        }
        if (node.Definition.OverrideReferences)
            foreach (var name in node.Definition.PrecompiledReferences) Add(name);
        else
            foreach (var name in managed.Keys.Where(n => !graph.All.ContainsKey(n) && !IsFramework(n) &&
                         n != "UnityEngine" && !n.StartsWith("UnityEngine.", StringComparison.Ordinal))) Add(name);
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public string OutputPath(string name) => Path.Combine(config.GeneratedDirectory, "bin", name, name + ".dll");
    public string[] Diagnostics(AssemblyNode node) => node.Errors.Concat(node.ResolvedReferences
        .Where(n => !managed.ContainsKey(n) && !(graph.All.TryGetValue(n, out var source) && source.Active && BuildScope.Contains(config, source)))
        .Select(n => $"Declared reference {n} has no Player DLL or in-scope source project.")).ToArray();
    private static bool IsEngine(string name) => name == "UnityEngine" || name.StartsWith("UnityEngine.", StringComparison.Ordinal);
    private static bool IsFramework(string name) => name is "mscorlib" or "netstandard" or "Microsoft.CSharp" or "Mono.Security" or "System" || name.StartsWith("System.", StringComparison.Ordinal);
}

internal static class AssemblyMetadata
{
    public static string Name(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetString(metadata.GetAssemblyDefinition().Name);
    }

    public static string Identity(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        return $"{metadata.GetString(assembly.Name)}|{assembly.Version}|{metadata.GetString(assembly.Culture)}|{Convert.ToHexString(metadata.GetBlobBytes(assembly.PublicKey))}";
    }

    public static string[] References(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
    }
}

internal static class PostProcessingPolicy
{
    public static string[] Requirements(AssemblyNode node)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (node.ResolvedReferences.Any(n => n is "Unity.Collections" or "Unity.Jobs" or "Unity.Burst")) reasons.Add("Unity Jobs/Burst/Collections");
        foreach (var path in node.Sources)
        {
            var source = File.ReadAllText(path);
            if (Regex.IsMatch(source, @"\[\s*(?:Unity\.Burst\.)?BurstCompile\b|\bIJob(?:ParallelFor|ParallelForTransform|For|Entity|Chunk)?\b")) reasons.Add("Unity Jobs/Burst");
            if (Regex.IsMatch(source, @"\[\s*(?:[\w.]+\.)?(?:NetRpc|ServerRpc|ClientRpc|HostRpc|ObserversRpc|Command|TargetRpc|SyncVar)\b")) reasons.Add("RPC/network weaving");
        }
        return reasons.Order(StringComparer.Ordinal).ToArray();
    }
}

internal static class ProjectGenerator
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/developer/msbuild/2003";
    private const string CSharpGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    public static int Generate(Configuration config, UnityToolchain toolchain, AssemblyGraph graph, ReferenceResolver resolver)
    {
        Directory.CreateDirectory(config.GeneratedDirectory);
        WriteIfChanged(Path.Combine(config.GeneratedDirectory, "Directory.Build.props"), "<Project />\n");
        WriteIfChanged(Path.Combine(config.GeneratedDirectory, "Unidot.UnityAdditionalFile.txt"), config.UnityProject + Environment.NewLine);
        var projects = new List<AssemblyNode>();
        var errors = new List<string>();
        foreach (var node in graph.Active.Values.Where(n => n.Sources.Count > 0 && BuildScope.Contains(config, n)).OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            try { resolver.Resolve(node, allowSourceOutputs: true); projects.Add(node); }
            catch (UnidotException error) { errors.Add(error.Message); }
        }
        var generated = projects.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var node in projects) WriteProject(config, toolchain, resolver, node, generated);
        LauncherGenerator.Generate(config);
        var solution = LauncherGenerator.SolutionPath(config);
        WriteSolution(solution, projects);
        LauncherGenerator.MigrateConflictingSolution(config);
        var snapshot = new
        {
            toolchain.Version,
            config.Platform,
            config.BuildRoots,
            config.ReferenceMode,
            Assemblies = graph.All.Values.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => new
            {
                n.Name, n.Guid, n.Active, InBuildScope = BuildScope.Contains(config, n), n.CachedPackage, n.DefinitionPath, Sources = n.Sources,
                References = n.ResolvedReferences, n.Defines, n.Errors, ReferenceDiagnostics = resolver.Diagnostics(n)
            }),
            Warnings = graph.Warnings,
            GenerationErrors = errors
        };
        WriteIfChanged(Path.Combine(config.GeneratedDirectory, "graph.json"), System.Text.Json.JsonSerializer.Serialize(snapshot, Configuration.JsonOptions));
        if (errors.Count > 0) Console.WriteLine($"Generated {projects.Count} projects; {errors.Count} assemblies have unresolved Player references (see .unidot/graph.json).");
        else Console.WriteLine($"Generated {projects.Count} Player projects + Launcher: {solution}");
        return projects.Count;
    }

    private static void WriteProject(Configuration config, UnityToolchain toolchain, ReferenceResolver resolver, AssemblyNode node, ISet<string> generated)
    {
        var references = resolver.Resolve(node, allowSourceOutputs: true);
        var path = Path.Combine(config.GeneratedDirectory, "projects", node.Name, node.Name + ".csproj");
        var output = Path.GetDirectoryName(resolver.OutputPath(node.Name))! + Path.DirectorySeparatorChar;
        var api = Path.Combine(toolchain.DataDirectory, "UnityReferenceAssemblies", "unity-4.8-api");
        var root = new XElement(Ns + "Project", new XAttribute("ToolsVersion", "Current"), new XAttribute("DefaultTargets", "Build"),
            new XComment("Generated by Unidot. Edit Unity sources and unidot.json; use unidot build for postprocessing and deployment."),
            new XElement(Ns + "PropertyGroup",
                Element("Configuration", "Debug"), Element("Platform", "AnyCPU"), Element("ProjectGuid", GuidFor(node.Name)),
                Element("AssemblyName", node.Name), Element("RootNamespace", node.Definition.RootNamespace), Element("OutputType", "Library"),
                Element("TargetFrameworkVersion", "v4.8"), Element("LangVersion", config.LanguageVersion),
                Element("OutputPath", output), Element("BaseIntermediateOutputPath", Path.Combine(config.GeneratedDirectory, "obj", node.Name) + Path.DirectorySeparatorChar),
                Element("DefineConstants", string.Join(';', node.Defines)), Element("AllowUnsafeBlocks", node.Definition.AllowUnsafeCode.ToString()),
                Element("DebugSymbols", "true"), Element("DebugType", "portable"), Element("Optimize", "true"), Element("Deterministic", "true"),
                Element("NoWarn", "0169;0649;0282;1701;1702"), Element("NoStdLib", "true"), Element("NoConfig", "true"),
                Element("ResolveNuGetPackages", "false"), Element("AddAdditionalExplicitAssemblyReferences", "false"),
                Element("ImplicitlyExpandNETStandardFacades", "false"), Element("ImplicitlyExpandDesignTimeFacades", "false"),
                Element("_TargetFrameworkDirectories", api), Element("_FullFrameworkReferenceAssemblyPaths", api),
                Element("FindDependenciesOfExternallyResolvedReferences", "false")),
            new XElement(Ns + "ItemGroup", node.Sources.Select(source => new XElement(Ns + "Compile", new XAttribute("Include", source),
                new XElement(Ns + "Link", Path.GetRelativePath(Path.GetDirectoryName(node.DefinitionPath) ?? config.UnityProject, source))))));
        var group = new XElement(Ns + "ItemGroup");
        foreach (var (name, hint) in references)
        {
            if (node.ResolvedReferences.Contains(name) && generated.Contains(name))
            {
                group.Add(new XElement(Ns + "ProjectReference", new XAttribute("Include", Path.Combine(config.GeneratedDirectory, "projects", name, name + ".csproj")),
                    new XAttribute("Condition", "'$(UnidotUsePlayerReferences)' != 'true'"),
                    Element("Project", GuidFor(name)), Element("Name", name), Element("Private", "false")));
                group.Add(new XElement(Ns + "Reference", new XAttribute("Include", name), new XAttribute("Condition", "'$(UnidotUsePlayerReferences)' == 'true'"),
                    Element("HintPath", hint), Element("Private", "false"), Element("ExternallyResolved", "true")));
            }
            else group.Add(new XElement(Ns + "Reference", new XAttribute("Include", name), Element("HintPath", hint), Element("Private", "false"), Element("ExternallyResolved", "true")));
        }
        root.Add(group);
        root.Add(new XElement(Ns + "ItemGroup", toolchain.Generators.Concat(config.Analyzers).Select(p => new XElement(Ns + "Analyzer", new XAttribute("Include", p))),
            new XElement(Ns + "AdditionalFiles", new XAttribute("Include", Path.Combine(config.GeneratedDirectory, "Unidot.UnityAdditionalFile.txt")))));
        root.Add(new XElement(Ns + "Target", new XAttribute("Name", "UnidotPostProcessingNotice"), new XAttribute("BeforeTargets", "CoreCompile"),
            new XElement(Ns + "Warning", new XAttribute("Text", "This is a raw compiler output. Use unidot build to apply configured IL postprocessors and deploy."),
                new XAttribute("Condition", PostProcessingPolicy.Requirements(node).Length > 0 || config.PostProcessors.ContainsKey(node.Name) ? "true" : "false"))));
        root.Add(new XElement(Ns + "Import", new XAttribute("Project", "$(MSBuildToolsPath)\\Microsoft.CSharp.targets")));
        // A custom CscToolPath treats csc.dll as a native executable. Invoke Unity's .NET host explicitly instead.
        var lines = string.Join(';', new[]
        {
            "/target:library", "/nostdlib+", "/nologo", "/utf8output", "/preferreduilang:en-US", "/deterministic+", "/optimize+", "/debug:portable",
            "/langversion:$(LangVersion)", node.Definition.AllowUnsafeCode ? "/unsafe+" : "/unsafe-", "/nowarn:0169,0649,0282,1701,1702",
            "@(IntermediateAssembly->'/out:\"%(Identity)\"')", "/pdb:\"$(IntermediateOutputPath)$(TargetName).pdb\"",
            "/define:$([System.String]::Copy('$(DefineConstants)').Replace(';', ','))",
            "@(ReferencePath->'/reference:\"%(FullPath)\"')", "@(Compile->'\"%(FullPath)\"')",
            "@(Analyzer->'/analyzer:\"%(FullPath)\"')", "@(AdditionalFiles->'/additionalfile:\"%(FullPath)\"')"
        });
        root.Add(new XElement(Ns + "Target", new XAttribute("Name", "CoreCompile"),
            new XAttribute("Inputs", "$(MSBuildProjectFullPath);@(Compile);@(ReferencePath);@(Analyzer);@(AdditionalFiles)"),
            new XAttribute("Outputs", "@(IntermediateAssembly);$(IntermediateOutputPath)$(TargetName).pdb"),
            new XElement(Ns + "MakeDir", new XAttribute("Directories", "$(IntermediateOutputPath)")),
            new XElement(Ns + "WriteLinesToFile", new XAttribute("File", "$(IntermediateOutputPath)unidot.rsp"),
                new XAttribute("Lines", lines), new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
            new XElement(Ns + "Exec", new XAttribute("WorkingDirectory", "$(MSBuildProjectDirectory)"),
                new XAttribute("Command", $"\"{toolchain.Dotnet}\" exec \"{toolchain.Compiler}\" /noconfig @\"$(IntermediateOutputPath)unidot.rsp\""))));
        WriteIfChanged(path, new XDocument(root).ToString());
    }

    private static XElement Element(string name, string value) => new(Ns + name, value);
    internal static string GuidFor(string name) => "{" + new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Unidot:" + name)).AsSpan(0, 16)).ToString().ToUpperInvariant() + "}";

    private static void WriteSolution(string path, IReadOnlyList<AssemblyNode> nodes)
    {
        if (File.Exists(path) && !File.ReadAllText(path).Contains("# Generated by Unidot", StringComparison.Ordinal))
            throw new UnidotException($"Existing user solution will not be overwritten: {path}. Set productName to another name.");
        var builder = new StringBuilder("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n# Generated by Unidot\n");
        var launcherGuid = GuidFor("Unidot.Generated.Launcher");
        var directory = Path.GetDirectoryName(path)!;
        var generatedDirectory = Path.Combine(directory, ".unidot");
        string RelativeProject(string project) => Path.GetRelativePath(directory, project).Replace('/', '\\');
        builder.AppendLine($"Project(\"{CSharpGuid}\") = \"Launcher\", \"{RelativeProject(Path.Combine(generatedDirectory, "Launcher", "Launcher.csproj"))}\", \"{launcherGuid}\"\nEndProject");
        foreach (var node in nodes)
            builder.AppendLine($"Project(\"{CSharpGuid}\") = \"{node.Name}\", \"{RelativeProject(Path.Combine(generatedDirectory, "projects", node.Name, node.Name + ".csproj"))}\", \"{GuidFor(node.Name)}\"\nEndProject");
        builder.AppendLine("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\t\tRelease|Any CPU = Release|Any CPU\n\tEndGlobalSection\n\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            builder.AppendLine($"\t\t{launcherGuid}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU");
            builder.AppendLine($"\t\t{launcherGuid}.{configuration}|Any CPU.Build.0 = {configuration}|Any CPU");
        }
        foreach (var node in nodes)
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                builder.AppendLine($"\t\t{GuidFor(node.Name)}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU");
                // IDE Play builds Launcher only. The launched CLI owns authoritative game compilation and ILPP.
            }
        builder.AppendLine("\tEndGlobalSection\nEndGlobal");
        WriteIfChanged(path, builder.ToString());
        var legacy = Path.Combine(generatedDirectory, Path.GetFileName(path));
        if (File.Exists(legacy))
        {
            var old = File.ReadAllText(legacy);
            if (old.Contains(launcherGuid, StringComparison.Ordinal) && old.Contains("\"Launcher\\Launcher.csproj\"", StringComparison.Ordinal)) File.Delete(legacy);
        }
    }

    internal static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
