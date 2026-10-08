namespace Unidot;

internal static class PlayerSources
{
    public static Configuration Configure(string directory, string sourceDirectory, string? editor = null, string? playerPath = null)
    {
        var player = PlayerLayout.Discover(Path.GetFullPath(playerPath ?? directory));
        var source = Path.GetFullPath(sourceDirectory, directory);
        if (!Directory.Exists(source)) throw new UnidotException($"Source directory not found: {source}. Default is Src beside the Player; use --src to override.");
        var config = new Configuration
        {
            Player = player.Executable, SourceDirectory = sourceDirectory, UnityProject = InferUnityProject(source),
            UnityEditor = editor ?? "", ReferenceMode = "player"
        };
        config.Normalize(directory);
        var toolchain = UnityToolchain.Discover(config);
        config.UnityEditor = Path.GetDirectoryName(toolchain.DataDirectory)!;
        config.UnityIlppPlugins = new[] { "Unity.BITKit.Multiplayer.CodeGen.dll", "Unity.Collections.CodeGen.dll", "Unity.Burst.CodeGen.dll" }
            .Select(name => Path.Combine(config.UnityProject, "Library", "ScriptAssemblies", name)).Where(File.Exists).ToArray();
        return config;
    }

    public static string InferUnityProject(string source)
    {
        var visited = new HashSet<string>(PathComparer.Instance);
        var projects = new HashSet<string>(PathComparer.Instance);
        void Visit(string directory)
        {
            directory = PathComparer.PhysicalDirectory(directory);
            if (!visited.Add(directory)) return;
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (!File.Exists(Path.Combine(ancestor.FullName, "ProjectSettings", "ProjectVersion.txt"))) continue;
                projects.Add(ancestor.FullName);
                return;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" || name.EndsWith('~')) continue;
                Visit(child);
            }
        }
        Visit(source);
        if (projects.Count == 1) return projects.Single();
        if (projects.Count > 1) throw new UnidotException("Src maps to multiple Unity projects. Select one with init --project.");
        throw new UnidotException("Cannot infer Unity metadata from Src. Link it to your Unity Assets source tree, or use init --project <Unity project> --src <directory>.");
    }

    public static async Task LinkAsync(string source, string destination, CancellationToken cancellationToken, bool announce = true)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source)) throw new UnidotException($"Link source not found: {source}");
        if (Directory.Exists(destination))
        {
            if (PathComparer.Instance.Equals(PathComparer.PhysicalDirectory(destination), PathComparer.PhysicalDirectory(source)))
            { if (announce) Console.WriteLine($"Source link already exists: {destination} -> {source}"); return; }
            throw new UnidotException($"Link destination already exists and will not be overwritten: {destination}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { Directory.CreateSymbolicLink(destination, source); }
        catch (Exception error) when (OperatingSystem.IsWindows() && (error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xFFFF) == 1314))
        {
            // Directory junctions work without enabling Developer Mode or granting symbolic-link privilege.
            if ((source + destination).IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0) throw new UnidotException("Junction paths contain unsupported command characters.");
            var exit = await Processes.ExecuteAsync("cmd.exe", ["/c", $"mklink /J \"{destination}\" \"{source}\""], Environment.CurrentDirectory, cancellationToken);
            if (exit != 0) throw new UnidotException("Could not create source directory junction.");
        }
        if (announce) Console.WriteLine($"Mapped source: {destination} -> {source}");
    }
}
