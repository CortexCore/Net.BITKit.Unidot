using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unidot;

internal sealed class AssemblyDefinition
{
    public string Name { get; set; } = "";
    public string RootNamespace { get; set; } = "";
    public string[] References { get; set; } = [];
    public string[] IncludePlatforms { get; set; } = [];
    public string[] ExcludePlatforms { get; set; } = [];
    public bool AllowUnsafeCode { get; set; }
    public bool OverrideReferences { get; set; }
    public string[] PrecompiledReferences { get; set; } = [];
    public bool AutoReferenced { get; set; } = true;
    public string[] DefineConstraints { get; set; } = [];
    public VersionDefine[] VersionDefines { get; set; } = [];
    public string[] OptionalUnityReferences { get; set; } = [];
    public bool NoEngineReferences { get; set; }
}

internal sealed class VersionDefine
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
    public string Define { get; set; } = "";
}

internal sealed class AssemblyNode(AssemblyDefinition definition, string definitionPath, string? guid, bool cached)
{
    public AssemblyDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string DefinitionPath { get; } = definitionPath;
    public string? Guid { get; } = guid;
    public bool CachedPackage { get; } = cached;
    public bool PlayerSource { get; set; }
    public bool Active { get; set; }
    public List<string> Sources { get; } = [];
    public string[] Defines { get; set; } = [];
    public List<string> ResolvedReferences { get; } = [];
    public List<string> Errors { get; } = [];
}

internal sealed class AssemblyGraph
{
    public Dictionary<string, AssemblyNode> All { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AssemblyNode> Active => All.Where(p => p.Value.Active).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    public List<string> Roots { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, string> PackageVersions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> AutoReferencedPlugins { get; } = new(StringComparer.Ordinal);
    public string[] GlobalDefines { get; private set; } = [];
    private readonly Dictionary<string, AssemblyNode> byGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> visited = new(PathComparer.Instance);
    private readonly List<DirectoryNode> directories = [];
    private readonly Dictionary<string, AssemblyNode> byDirectory = new(PathComparer.Instance);
    private sealed record DirectoryNode(string Path, DirectoryNode? Parent, bool Assets, bool Cached, bool PlayerSource, string[] Files);

    public static AssemblyGraph Scan(Configuration config, UnityToolchain toolchain)
    {
        var graph = new AssemblyGraph();
        graph.PackageVersions["Unity"] = toolchain.Version;
        if (config.SourcePath is not null) graph.AddRoot(config.SourcePath, true, false, true);
        var assets = Path.Combine(config.UnityProject, "Assets");
        if (Directory.Exists(assets)) graph.AddRoot(assets, true, false);
        var packages = Path.Combine(config.UnityProject, "Packages");
        var manifestPath = Path.Combine(packages, "manifest.json");
        if (Directory.Exists(packages))
            foreach (var directory in Directory.EnumerateDirectories(packages).Order(PathComparer.Instance))
                graph.AddRoot(directory, false, false);
        foreach (var root in config.SourceRoots) graph.AddRoot(root, false, false);
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { Path.Combine(packages, "packages-lock.json"), manifestPath })
        {
            if (!File.Exists(path)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(path), JsonDocumentOptions);
            if (!json.RootElement.TryGetProperty("dependencies", out var dependencies)) continue;
            foreach (var item in dependencies.EnumerateObject())
            {
                var version = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() :
                    item.Value.TryGetProperty("version", out var field) ? field.GetString() : null;
                if (version is not null) requested[item.Name] = version;
            }
        }
        var cache = Path.Combine(config.UnityProject, "Library", "PackageCache");
        foreach (var (name, version) in requested.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (version.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = Path.GetFullPath(version[5..], packages);
                if (!Directory.Exists(path))
                {
                    var projectRelative = Path.GetFullPath(version[5..], config.UnityProject);
                    if (Directory.Exists(projectRelative)) path = projectRelative;
                }
                if (Directory.Exists(path)) graph.AddRoot(path, false, false);
                else graph.Warnings.Add($"Local package {name} is missing: {path}");
                continue;
            }
            if (!Directory.Exists(cache)) continue;
            var matches = Directory.EnumerateDirectories(cache, name + "@*").Order(PathComparer.Instance).ToArray();
            var exact = matches.FirstOrDefault(p => Path.GetFileName(p).Equals(name + "@" + version, StringComparison.Ordinal));
            if (exact is not null) graph.AddRoot(exact, false, true);
            else if (matches.Length == 1) graph.AddRoot(matches[0], false, true);
            else if (matches.Length > 1) graph.Warnings.Add($"Multiple cached versions of {name}; use sourceRoots to select one.");
        }
        foreach (var directory in graph.directories)
        {
            foreach (var dll in directory.Files.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var meta = File.Exists(dll + ".meta") ? File.ReadAllText(dll + ".meta") : "";
                if (!meta.Contains("RoslynAnalyzer", StringComparison.Ordinal) && !Regex.IsMatch(meta, @"isExplicitlyReferenced:\s*1\b"))
                    graph.AutoReferencedPlugins.Add(Path.GetFileNameWithoutExtension(dll));
            }
            var definitions = directory.Files.Where(p => p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (definitions.Length > 1) throw new UnidotException($"Multiple asmdef files in {directory.Path}.");
            foreach (var path in definitions)
            {
                var definition = JsonSerializer.Deserialize<AssemblyDefinition>(File.ReadAllText(path), Configuration.JsonOptions)
                    ?? throw new UnidotException($"Invalid asmdef: {path}");
                if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.IndexOfAny(['/', '\\', ':', '"', '\n', '\r']) >= 0 || definition.Name is "." or "..")
                    throw new UnidotException($"Invalid assembly name in {path}.");
                string? guid = null;
                if (File.Exists(path + ".meta"))
                {
                    var match = Regex.Match(File.ReadAllText(path + ".meta"), @"(?m)^guid:\s*([a-fA-F0-9]{32})\s*$");
                    if (match.Success) guid = match.Groups[1].Value;
                }
                var node = new AssemblyNode(definition, path, guid, directory.Cached);
                node.PlayerSource = directory.PlayerSource;
                if (!graph.All.TryAdd(node.Name, node)) throw new UnidotException($"Duplicate assembly {node.Name}: {graph.All[node.Name].DefinitionPath} and {path}");
                if (guid is not null && !graph.byGuid.TryAdd(guid, node)) throw new UnidotException($"Duplicate asmdef GUID {guid}.");
                graph.byDirectory[directory.Path] = node;
            }
        }
        foreach (var directory in graph.directories)
        {
            var refs = directory.Files.Where(p => p.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (refs.Length > 1 || (refs.Length == 1 && graph.byDirectory.ContainsKey(directory.Path)))
                throw new UnidotException($"Conflicting assembly markers in {directory.Path}.");
            if (refs.Length != 1) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(refs[0]), JsonDocumentOptions);
            var reference = json.RootElement.GetProperty("reference").GetString() ?? "";
            var node = graph.Resolve(reference);
            if (node is null)
            {
                graph.Warnings.Add($"Unresolved asmref {reference} in {refs[0]}; this source subtree is excluded.");
                // An unresolved asmref is still an ownership boundary. Never leak its files into Assembly-CSharp.
                node = new(new AssemblyDefinition { Name = "UnresolvedAsmref" }, refs[0], null, directory.Cached);
            }
            graph.byDirectory[directory.Path] = node;
        }
        foreach (var directory in graph.directories)
        {
            AssemblyNode? owner = null;
            for (var current = directory; current is not null; current = current.Parent)
                if (graph.byDirectory.TryGetValue(current.Path, out owner)) break;
            foreach (var file in directory.Files.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                if (owner is not null) owner.Sources.Add(file);
                else if (directory.Assets && !Ancestors(directory).Any(p => Path.GetFileName(p.Path).Equals("Editor", StringComparison.OrdinalIgnoreCase)))
                {
                    var firstpass = Ancestors(directory).Any(p => Path.GetFileName(p.Path) is "Plugins" or "Standard Assets" or "Pro Standard Assets");
                    var name = firstpass ? "Assembly-CSharp-firstpass" : "Assembly-CSharp";
                    if (!graph.All.TryGetValue(name, out var predefined))
                    {
                        predefined = new(new AssemblyDefinition { Name = name }, "", null, false);
                        graph.All.Add(name, predefined);
                    }
                    predefined.Sources.Add(file);
                }
            }
        }
        graph.GlobalDefines = UnityDefines.Read(config, toolchain.Version);
        foreach (var node in graph.All.Values)
        {
            var symbols = graph.GlobalDefines.ToHashSet(StringComparer.Ordinal);
            foreach (var rule in node.Definition.VersionDefines)
                if (!string.IsNullOrWhiteSpace(rule.Define) && graph.PackageVersions.TryGetValue(rule.Name, out var version) && VersionRules.Matches(version, rule.Expression))
                    symbols.Add(rule.Define);
            node.Defines = symbols.Order(StringComparer.Ordinal).ToArray();
            node.Active = (node.Definition.IncludePlatforms.Length == 0 || node.Definition.IncludePlatforms.Contains(config.Platform)) &&
                !node.Definition.ExcludePlatforms.Contains(config.Platform) &&
                !node.Definition.OptionalUnityReferences.Contains("TestAssemblies") &&
                node.Definition.DefineConstraints.All(c => UnityDefines.Satisfies(c, symbols));
            node.Sources.Sort(PathComparer.Instance);
            foreach (var reference in node.Definition.References)
            {
                var target = graph.Resolve(reference);
                if (target is not null) node.ResolvedReferences.Add(target.Name);
                else if (reference.StartsWith("GUID:", StringComparison.Ordinal)) node.Errors.Add($"Unresolved reference {reference}");
                else node.ResolvedReferences.Add(reference);
            }
        }
        foreach (var node in graph.All.Values.Where(n => n.DefinitionPath.Length == 0))
        {
            node.ResolvedReferences.AddRange(graph.All.Values.Where(n => n.Active && n.DefinitionPath.Length > 0 && n.Definition.AutoReferenced).Select(n => n.Name));
            if (node.Name == "Assembly-CSharp" && graph.All.ContainsKey("Assembly-CSharp-firstpass")) node.ResolvedReferences.Add("Assembly-CSharp-firstpass");
        }
        return graph;
    }

    public IReadOnlyList<AssemblyNode> BuildOrder(IEnumerable<string> targets, bool dependencies, Func<AssemblyNode, bool>? scope = null, bool allowUnresolved = false)
    {
        var result = new List<AssemblyNode>();
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var chain = new List<string>();
        void Visit(string name)
        {
            if (!All.TryGetValue(name, out var node) || !node.Active) throw new UnidotException($"Assembly {name} is not available for this Player configuration.");
            if (scope is not null && !scope(node)) throw new UnidotException($"Assembly {name} is outside configured buildRoots; it remains a binary dependency.");
            if (states.TryGetValue(name, out var state))
            {
                if (state == 1) throw new UnidotException("Assembly dependency cycle: " + string.Join(" -> ", chain.Skip(chain.IndexOf(name)).Append(name)));
                return;
            }
            if (!allowUnresolved && node.Errors.Count > 0) throw new UnidotException($"{name}: {string.Join("; ", node.Errors)}");
            states[name] = 1;
            chain.Add(name);
            if (dependencies)
                foreach (var reference in node.ResolvedReferences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    if (All.TryGetValue(reference, out var dependency) && dependency.Active && !dependency.CachedPackage && (scope is null || scope(dependency))) Visit(reference);
            chain.RemoveAt(chain.Count - 1);
            states[name] = 2;
            result.Add(node);
        }
        foreach (var target in targets.Order(StringComparer.Ordinal)) Visit(target);
        return result;
    }

    private AssemblyNode? Resolve(string reference) => reference.StartsWith("GUID:", StringComparison.Ordinal)
        ? byGuid.GetValueOrDefault(reference[5..]) : All.GetValueOrDefault(reference);

    private void AddRoot(string path, bool assets, bool cached, bool playerSource = false)
    {
        if (!Directory.Exists(path)) throw new UnidotException($"Source root not found: {path}");
        Roots.Add(PathComparer.PhysicalDirectory(path));
        Walk(path, null, assets, cached, playerSource);
    }

    private void Walk(string path, DirectoryNode? parent, bool assets, bool cached, bool playerSource)
    {
        path = PathComparer.PhysicalDirectory(path);
        if (!visited.Add(path)) return;
        var files = Directory.GetFiles(path);
        var node = new DirectoryNode(path, parent, assets, cached, playerSource, files);
        directories.Add(node);
        var packageJson = Path.Combine(path, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(packageJson), JsonDocumentOptions);
                if (json.RootElement.TryGetProperty("name", out var name) && json.RootElement.TryGetProperty("version", out var version))
                    PackageVersions[name.GetString()!] = version.GetString()!;
            }
            catch (JsonException) { Warnings.Add($"Invalid package.json: {packageJson}"); }
        }
        foreach (var child in Directory.EnumerateDirectories(path).Order(PathComparer.Instance))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.EndsWith('~') || name is "node_modules" or "bin" or "obj") continue;
            Walk(child, node, assets, cached, playerSource);
        }
    }

    private static IEnumerable<DirectoryNode> Ancestors(DirectoryNode node)
    {
        for (DirectoryNode? current = node; current is not null; current = current.Parent) yield return current;
    }

    private static readonly JsonDocumentOptions JsonDocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}

internal static class UnityDefines
{
    public static string[] Read(Configuration config, string version)
    {
        var numeric = VersionRules.Parse(version);
        var symbols = new HashSet<string>(config.Defines, StringComparer.Ordinal)
        {
            $"UNITY_{numeric.Major}", $"UNITY_{numeric.Major}_{numeric.Minor}", $"UNITY_{numeric.Major}_{numeric.Minor}_{numeric.Build}",
            "UNITY_STANDALONE", "UNITY_STANDALONE_WIN", "UNITY_64", "PLATFORM_STANDALONE", "PLATFORM_STANDALONE_WIN",
            "PLATFORM_ARCH_64", "ENABLE_MONO", "PLATFORM_SUPPORTS_MONO", "CSHARP_7_OR_LATER", "CSHARP_7_3_OR_NEWER"
        };
        foreach (var minor in Enumerable.Range(3, 4)) symbols.Add($"UNITY_5_{minor}_OR_NEWER");
        for (var year = 2017; year <= Math.Min(numeric.Major, 6000); year++)
        {
            if (year > 2023 && year < 6000) continue;
            var lastMinor = year == numeric.Major ? numeric.Minor : year >= 2020 ? (year == 2023 ? 2 : 3) : 4;
            for (var minor = 1; minor <= lastMinor; minor++) symbols.Add($"UNITY_{year}_{minor}_OR_NEWER");
        }
        if (config.ApiProfile == "unity-4.8") symbols.UnionWith(["NET_4_6", "NET_UNITY_4_8"]);
        else symbols.UnionWith(["NET_STANDARD", "NET_STANDARD_2_0", "NET_STANDARD_2_1", "NETSTANDARD2_1"]);
        if (config.Development) symbols.UnionWith(["DEVELOPMENT_BUILD", "DEBUG", "TRACE", "UNITY_ASSERTIONS", "ENABLE_PROFILER"]);
        var settings = Path.Combine(config.UnityProject, "ProjectSettings", "ProjectSettings.asset");
        if (File.Exists(settings))
        {
            var text = File.ReadAllText(settings);
            var block = Regex.Match(text, @"(?m)^  scriptingDefineSymbols:\s*\r?\n((?:    [^\r\n]*\r?\n)*)");
            var standalone = Regex.Match(block.Groups[1].Value, @"(?m)^    Standalone:\s*(.*)$");
            foreach (var symbol in standalone.Groups[1].Value.Trim().Trim('"', '\'').Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) symbols.Add(symbol);
            var input = Regex.Match(text, @"(?m)^  activeInputHandler:\s*(\d+)");
            if (input.Success)
            {
                if (input.Groups[1].Value is "1" or "2") symbols.Add("ENABLE_INPUT_SYSTEM");
                if (input.Groups[1].Value is "0" or "2") symbols.Add("ENABLE_LEGACY_INPUT_MANAGER");
            }
        }
        foreach (var module in new[] { "PHYSICS", "AUDIO", "CLOTH", "VR", "WEBCAM", "MICROPHONE", "VIDEO", "UNITYWEBREQUEST" }) symbols.Add("ENABLE_" + module);
        if (symbols.Any(d => d.StartsWith("UNITY_EDITOR", StringComparison.Ordinal))) throw new UnidotException("Player defines unexpectedly contain UNITY_EDITOR.");
        return symbols.Order(StringComparer.Ordinal).ToArray();
    }

    public static bool Satisfies(string expression, ISet<string> defines) => expression.Split("||", StringSplitOptions.TrimEntries)
        .Any(term => term.StartsWith('!') ? !defines.Contains(term[1..].Trim()) : defines.Contains(term));
}

internal static class VersionRules
{
    public static Version Parse(string value)
    {
        var match = Regex.Match(value, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) throw new UnidotException($"Invalid version: {value}");
        return new(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0,
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
    }

    public static bool Matches(string version, string expression)
    {
        var value = Parse(version);
        expression = expression.Trim();
        if (expression.Length == 0) return true;
        if (expression.StartsWith('[') || expression.StartsWith('('))
        {
            var parts = expression[1..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 1) return value == Parse(parts[0]);
            return (parts[0].Length == 0 || (expression[0] == '[' ? value >= Parse(parts[0]) : value > Parse(parts[0]))) &&
                (parts[1].Length == 0 || (expression[^1] == ']' ? value <= Parse(parts[1]) : value < Parse(parts[1])));
        }
        return value >= Parse(expression);
    }
}
