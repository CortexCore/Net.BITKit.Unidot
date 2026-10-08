using System.Text;
using System.Text.Json;

namespace Unidot;

internal sealed record WorkspaceSource(string Path, string Hash, long Bytes);
internal sealed record WorkspaceLink(string Path, string Target, bool Directory);
internal sealed record WorkspaceResource(string Path, long Bytes, long ModifiedTicks);
internal sealed record WorkspaceSourceRoot(string Path, string Target);
internal sealed record WorkspaceManifest(int Version, string Id, string Name, string Root, string BasePlayer, string SourceOrigin,
    string Created, long CopiedBytes, long SharedBytes, WorkspaceSource[] Sources, WorkspaceLink[] Links, WorkspaceResource[] Resources, WorkspaceSourceRoot[]? SourceRoots = null);
internal sealed record WorkspaceChange(string Path, string Status);

internal static class WorkspaceManager
{
    private const string ManifestRelative = ".unidot/workspace.json";
    private static readonly string[] ExcludedNames = [".unidot", ".git", ".run", "Src", "Workspaces", "AGENTS.md", "unidot.json"];

    internal static bool Within(string path, string root)
    {
        path = Path.GetFullPath(path); root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    public static async Task<int> ExecuteAsync(Arguments cli, CancellationToken token)
    {
        var action = cli.Positionals.FirstOrDefault() ?? throw new UnidotException("workspace requires create, list, diff, apply, resolve, or remove.");
        var expected = action == "list" ? 1 : action == "resolve" ? 3 : 2;
        if (cli.Positionals.Count != expected) throw new UnidotException($"workspace {action} {(action == "list" ? "does not accept a name" : action == "resolve" ? "requires a name and one Src-relative file path" : "requires one name")}.");
        var parent = Path.GetFullPath(cli.Value("root") ?? "Workspaces");
        if (action == "list")
        {
            foreach (var item in List(parent)) Console.WriteLine($"{item.Name}: {item.Root} | private now {Size(PrivateBytes(item.Root))}, initially copied {Size(item.CopiedBytes)}, shared {Size(item.SharedBytes)} | base {item.BasePlayer}");
            return 0;
        }
        var name = cli.Positionals[1];
        ValidateName(name);
        if (action == "create")
        {
            var basePath = Path.GetFullPath(cli.Value("base") ?? throw new UnidotException("workspace create requires --base <Player directory or exe>."));
            var player = PlayerLayout.Discover(basePath);
            var configPath = cli.Value("config") ?? Path.Combine(player.RootDirectory, "unidot.json");
            Configuration config;
            if (File.Exists(configPath) || cli.Value("config") is not null)
                config = Configuration.Load(configPath, cli.Value("src"), cli.Value("unity-editor"));
            else
                config = PlayerSources.Configure(player.RootDirectory, cli.Value("src") ?? "Src", cli.Value("unity-editor"), player.Executable);
            config.Player = player.Executable;
            if (cli.Value("unity-editor") is { } editor) config.UnityEditor = Path.GetFullPath(editor);
            Validate(config);
            var manifest = await CreateAsync(name, parent, config, token);
            Console.WriteLine($"Created workspace {name}: {manifest.Root}");
            Console.WriteLine($"Copied {Size(manifest.CopiedBytes)}; shared {Size(manifest.SharedBytes)} through {manifest.Links.Length} links. Resources were not copied.");
            Console.WriteLine($"Run unidot build/run from \"{manifest.Root}\".");
            return 0;
        }
        var root = Path.Combine(parent, name);
        if (action == "apply") return WorkspaceMerge.Apply(root, cli.Has("dry-run"), token).Conflicts > 0 ? 1 : 0;
        if (action == "resolve") { WorkspaceMerge.Resolve(root, cli.Positionals[2], token); return 0; }
        if (action == "diff")
        {
            var changes = Diff(root);
            foreach (var change in changes) Console.WriteLine($"{change.Status} {change.Path}");
            Console.WriteLine($"{changes.Count} source changes compared with the creation snapshot.");
            return 0;
        }
        if (action == "remove") { Remove(root); Console.WriteLine($"Removed workspace {name}."); return 0; }
        throw new UnidotException($"Unknown workspace action '{action}'.");
    }

    internal static async Task<WorkspaceManifest> CreateAsync(string name, string parent, Configuration original, CancellationToken token,
        Func<string, string, bool, CancellationToken, Task>? link = null)
    {
        ValidateName(name);
        var root = Path.GetFullPath(Path.Combine(parent, name));
        var player = PlayerLayout.Discover(original.Player);
        var source = original.SourcePath ?? (Directory.Exists(Path.Combine(player.RootDirectory, "Src")) ? Path.Combine(player.RootDirectory, "Src") : null);
        if (source is null) throw new UnidotException("Workspace creation requires a base Src mapping. Configure sourceDirectory or pass --src.");
        RequireRealAncestors(root);
        if (File.Exists(root) || Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new UnidotException($"Workspace destination already exists: {root}");
        if (Within(root, player.RootDirectory) || Within(player.RootDirectory, root) || Within(root, PathComparer.PhysicalDirectory(source)))
            throw new UnidotException("Workspace destination must be outside the base Player and source tree.");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        using var workspaceLock = AcquireNameLock(root);
        if (File.Exists(root) || Directory.Exists(root)) throw new UnidotException($"Workspace destination already exists: {root}");
        link ??= LinkAsync;
        // Check file and directory linking before copying any source or Managed files.
        var probe = Path.Combine(Path.GetDirectoryName(root)!, ".unidot-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var file = Path.Combine(probe, "file"); File.WriteAllText(file, "probe");
            await link(file, Path.Combine(probe, "file-link"), false, token);
            var directory = Path.Combine(probe, "directory"); Directory.CreateDirectory(directory);
            await link(directory, Path.Combine(probe, "directory-link"), true, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UnidotException)
        { throw new UnidotException("Workspace resource links are unavailable. Enable Windows Developer Mode or symbolic-link privilege; large resources will not be copied. " + error.Message); }
        finally { DeleteTree(probe); }

        Directory.CreateDirectory(root);
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(original.GeneratedDirectory);
            using var baseBuildLock = BuildEngine.AcquireLock(original.GeneratedDirectory);
            var managedSnapshot = new List<WorkspaceResource>();
            CollectResources(player.ManagedDirectory, managedSnapshot, new HashSet<string>(PathComparer.Instance), token);
            var mappings = new Dictionary<string, string>(PathComparer.Instance);
            var copied = CopyTree(source, Path.Combine(root, "Src"), mappings, token);
            copied += CopyTree(player.ManagedDirectory, Path.Combine(root, Path.GetFileName(player.DataDirectory), "Managed"), null, token);
            var links = new List<WorkspaceLink>();
            var resources = new List<WorkspaceResource>();
            long sharedBytes = 0;
            var resourceVisited = new HashSet<string>(PathComparer.Instance);
            async Task ShareAsync(FileSystemInfo entry, string destination)
            {
                var directory = (entry.Attributes & FileAttributes.Directory) != 0;
                var target = entry.ResolveLinkTarget(true)?.FullName ?? entry.FullName;
                var previous = resources.Count;
                CollectResources(target, resources, resourceVisited, token);
                sharedBytes += resources.Skip(previous).Where(r => r.Bytes >= 0).Sum(r => r.Bytes);
                await link(target, destination, directory, token);
                links.Add(new(Path.GetRelativePath(root, destination), target, directory));
            }
            foreach (var entry in new DirectoryInfo(player.RootDirectory).EnumerateFileSystemInfos().OrderBy(e => e.Name, PathComparer.Instance))
            {
                token.ThrowIfCancellationRequested();
                if (ExcludedNames.Contains(entry.Name, PathComparer.Instance) || PathComparer.Instance.Equals(entry.FullName, Path.GetFullPath(source)) || entry.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(root, entry.Name);
                if (PathComparer.Instance.Equals(entry.FullName, player.DataDirectory))
                {
                    foreach (var child in new DirectoryInfo(player.DataDirectory).EnumerateFileSystemInfos())
                    {
                        if (child.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase)) continue;
                        var childDestination = Path.Combine(destination, child.Name);
                        if (child is FileInfo && child.Name is "app.info" or "ScriptingAssemblies.json" or "RuntimeInitializeOnLoads.json")
                        { copied += CopyFile(child.FullName, childDestination); CollectResources(child.FullName, resources, resourceVisited, token); }
                        else await ShareAsync(child, childDestination);
                    }
                }
                else if (PathComparer.Instance.Equals(entry.FullName, player.Executable))
                { copied += CopyFile(entry.FullName, destination); CollectResources(entry.FullName, resources, resourceVisited, token); }
                else await ShareAsync(entry, destination);
            }

            var config = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(original, Configuration.JsonOptions), Configuration.JsonOptions)!;
            config.Player = Path.Combine(root, Path.GetFileName(player.Executable));
            foreach (var processor in config.PostProcessors.Values.SelectMany(p => p))
                if (!Path.IsPathRooted(processor.Executable) && (processor.Executable.Contains(Path.DirectorySeparatorChar) || processor.Executable.Contains(Path.AltDirectorySeparatorChar)))
                    processor.Executable = Path.GetFullPath(processor.Executable, original.Directory);
            config.SourceDirectory = "Src";
            config.WorkspaceExcludedSources = original.WorkspaceExcludedSources.Concat(mappings.Keys)
                .OrderBy(p => p.Length).Aggregate(new List<string>(), (roots, path) => { if (!roots.Any(r => Within(path, r))) roots.Add(path); return roots; }).ToArray();
            config.BuildRoots = original.BuildRoots.SelectMany(buildRoot => mappings.Where(m => Within(m.Key, PathComparer.PhysicalDirectory(buildRoot))).Select(m => m.Value))
                .Distinct(PathComparer.Instance).OrderBy(p => p.Length).Aggregate(new List<string>(), (roots, path) => { if (!roots.Any(r => Within(path, r))) roots.Add(path); return roots; }).ToArray();
            if (original.BuildRoots.Length > 0 && config.BuildRoots.Length == 0) throw new UnidotException("Base buildRoots do not intersect the copied Src tree.");
            config.WorkspaceManifest = ManifestRelative;
            config.GlobalCompilerResponse = ".unidot/base-csc.rsp";
            config.Normalize(root);
            Directory.CreateDirectory(config.GeneratedDirectory);
            var globalResponse = original.GlobalCompilerResponse ?? Path.Combine(original.UnityProject, "Assets", "csc.rsp");
            if (File.Exists(globalResponse)) copied += CopyFile(globalResponse, config.GlobalCompilerResponse!);
            config.Save(Path.Combine(root, "unidot.json"));
            // Fail during creation rather than discovering a duplicate or escaped source at first build.
            var graph = AssemblyGraph.Scan(config, new UnityToolchain("", "", "", ReadUnityVersion(config)));
            if (config.Assemblies.Length > 0) graph.BuildOrder(config.Assemblies, false, n => BuildScope.Contains(config, n), config.ReferenceMode == "player");
            var sources = Snapshot(Path.Combine(root, "Src"), token);
            foreach (var entry in managedSnapshot)
                if (ResourceChanged(entry)) throw new UnidotException($"Managed snapshot input changed during creation: {entry.Path}. Retry after the base files are stable.");
            var manifest = new WorkspaceManifest(1, Guid.NewGuid().ToString("N"), name, root, player.Executable, Path.GetFullPath(source),
                DateTime.UtcNow.ToString("O"), copied, sharedBytes,
                sources, links.ToArray(), resources.ToArray(), SourceRoutes(root, mappings));
            File.WriteAllText(Path.Combine(root, ManifestRelative), JsonSerializer.Serialize(manifest, Configuration.JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            PlayerAgentInstructions.Ensure(config, PlayerLayout.Discover(config.Player));
            return manifest;
        }
        catch { DeleteTree(root); throw; }
    }

    private static string ReadUnityVersion(Configuration config)
    {
        var text = File.ReadAllText(Path.Combine(config.UnityProject, "ProjectSettings", "ProjectVersion.txt"));
        return System.Text.RegularExpressions.Regex.Match(text, @"m_EditorVersion:\s*(\S+)").Groups[1].Value;
    }

    private static long CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var before = new FileInfo(source); var size = before.Length; var modified = before.LastWriteTimeUtc;
        try { File.Copy(source, destination, false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new UnidotException($"Cannot copy snapshot input '{source}': {error.Message}"); }
        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        before.Refresh();
        if (size != before.Length || modified != before.LastWriteTimeUtc) throw new UnidotException($"Snapshot input changed while copying: {source}");
        File.SetLastWriteTimeUtc(destination, modified);
        return size;
    }

    private static long CopyTree(string source, string destination, Dictionary<string, string>? mappings, CancellationToken token)
    {
        var visited = new HashSet<string>(PathComparer.Instance);
        long bytes = 0;
        void Visit(string path, string target)
        {
            token.ThrowIfCancellationRequested();
            path = PathComparer.PhysicalDirectory(path);
            if (Within(destination, path)) throw new UnidotException("Workspace destination overlaps a linked snapshot input.");
            if (!visited.Add(path)) return; // Same ownership de-duplication as source discovery, including link cycles.
            mappings?.TryAdd(path, target);
            Directory.CreateDirectory(target);
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(e => e.Name, PathComparer.Instance))
            {
                token.ThrowIfCancellationRequested();
                if (mappings is not null && entry.Name is (".git" or ".unidot" or "bin" or "obj")) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0) Visit(entry.FullName, Path.Combine(target, entry.Name));
                else bytes += CopyFile(entry.FullName, Path.Combine(target, entry.Name));
            }
        }
        Visit(source, destination);
        return bytes;
    }

    private static void CollectResources(string path, List<WorkspaceResource> resources, HashSet<string> visited, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        path = entry.ResolveLinkTarget(true)?.FullName ?? entry.FullName;
        if (!visited.Add(path)) return;
        if (Directory.Exists(path))
        {
            resources.Add(new(path, -1, Directory.GetLastWriteTimeUtc(path).Ticks));
            foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos()) CollectResources(child.FullName, resources, visited, token);
        }
        else
        {
            var file = new FileInfo(path);
            resources.Add(new(path, file.Length, file.LastWriteTimeUtc.Ticks));
        }
    }

    private static async Task LinkAsync(string source, string destination, bool directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (directory) await PlayerSources.LinkAsync(source, destination, token, announce: false);
        else File.CreateSymbolicLink(destination, source);
    }

    internal static WorkspaceSource[] Snapshot(string source, CancellationToken token)
    {
        RequireRealAncestors(source);
        if (!Directory.Exists(source)) return [];
        var rows = new List<WorkspaceSource>(); var hashes = new ContentHashes();
        void Visit(string path)
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(e => e.Name, PathComparer.Instance))
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnidotException($"Workspace Src must contain real files, not links: {entry.FullName}");
                if (entry is DirectoryInfo) Visit(entry.FullName);
                else rows.Add(new(Path.GetRelativePath(source, entry.FullName), hashes.Get(entry.FullName), ((FileInfo)entry).Length));
            }
        }
        Visit(source); return rows.ToArray();
    }

    internal static WorkspaceManifest Read(string root)
    {
        root = Path.GetFullPath(root);
        RequireRealAncestors(Path.Combine(root, ManifestRelative));
        var manifest = JsonSerializer.Deserialize<WorkspaceManifest>(File.ReadAllText(Path.Combine(root, ManifestRelative)), Configuration.JsonOptions)
            ?? throw new UnidotException("Invalid workspace manifest.");
        if (manifest.Version != 1 || !Guid.TryParseExact(manifest.Id, "N", out _) || !PathComparer.Instance.Equals(manifest.Root, root) || manifest.Name != Path.GetFileName(root) ||
            manifest.Sources is null || manifest.Links is null || manifest.Resources is null)
            throw new UnidotException("Workspace manifest identity does not match this directory. Refusing to manage it.");
        return manifest;
    }

    internal static IReadOnlyList<WorkspaceManifest> List(string parent)
    {
        if (!Directory.Exists(parent)) return [];
        var manifests = new List<WorkspaceManifest>();
        foreach (var directory in Directory.EnumerateDirectories(parent).Order(PathComparer.Instance))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !File.Exists(Path.Combine(directory, ManifestRelative))) continue;
            try { manifests.Add(Read(directory)); }
            catch (Exception error) when (error is IOException or UnidotException or JsonException) { Console.Error.WriteLine($"Warning: {directory}: {error.Message}"); }
        }
        return manifests;
    }

    internal static IReadOnlyList<WorkspaceChange> Diff(string root)
    {
        var manifest = Read(root);
        var before = manifest.Sources.ToDictionary(s => s.Path, s => s.Hash, PathComparer.Instance);
        var after = Snapshot(Path.Combine(root, "Src"), CancellationToken.None).ToDictionary(s => s.Path, s => s.Hash, PathComparer.Instance);
        return before.Keys.Union(after.Keys, PathComparer.Instance).Order(PathComparer.Instance)
            .Where(p => before.GetValueOrDefault(p) != after.GetValueOrDefault(p))
            .Select(p => new WorkspaceChange(p, !before.ContainsKey(p) ? "added" : !after.ContainsKey(p) ? "deleted" : "modified")).ToArray();
    }

    internal static long PrivateBytes(string root)
    {
        RequireRealAncestors(root);
        long total = 0;
        void Visit(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo) Visit(entry.FullName);
                else total += ((FileInfo)entry).Length;
            }
        }
        Visit(root); return total;
    }

    internal static void Validate(Configuration config)
    {
        if (config.WorkspaceManifest is null) return;
        if (!PathComparer.Instance.Equals(config.WorkspaceManifest, Path.GetFullPath(Path.Combine(config.Directory, ManifestRelative)))) throw new UnidotException("Workspace manifest must be owned by this configuration directory.");
        var manifest = Read(config.Directory);
        if (!Within(config.Player, config.Directory) || !PathComparer.Instance.Equals(config.SourcePath, Path.Combine(config.Directory, "Src")))
            throw new UnidotException("Workspace Player and Src must stay inside their own directory.");
        RequireRealAncestors(config.Player);
        RequireRealAncestors(Path.Combine(Path.GetDirectoryName(config.Player)!, Path.GetFileNameWithoutExtension(config.Player) + "_Data", "Managed"));
        RequireRealAncestors(config.SourcePath!);
        VerifyManaged(Path.Combine(Path.GetDirectoryName(config.Player)!, Path.GetFileNameWithoutExtension(config.Player) + "_Data", "Managed"));
        foreach (var shared in manifest.Links)
        {
            var path = Path.GetFullPath(shared.Path, config.Directory);
            if (!Within(path, config.Directory)) throw new UnidotException("Workspace manifest contains an external link destination.");
            var info = shared.Directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists || !PathComparer.Instance.Equals(info.ResolveLinkTarget(true)?.FullName, shared.Target))
                throw new UnidotException($"Workspace resource link is missing or changed: {shared.Path}. Recreate the workspace.");
        }
        foreach (var resource in manifest.Resources)
        {
            if (ResourceChanged(resource))
                throw new UnidotException($"Workspace base resource changed or disappeared: {resource.Path}. Recreate the workspace against the updated Build.");
        }
    }

    private static bool ResourceChanged(WorkspaceResource resource) => resource.Bytes < 0 ?
        !Directory.Exists(resource.Path) || Directory.GetLastWriteTimeUtc(resource.Path).Ticks != resource.ModifiedTicks :
        !File.Exists(resource.Path) || new FileInfo(resource.Path).Length != resource.Bytes || File.GetLastWriteTimeUtc(resource.Path).Ticks != resource.ModifiedTicks;

    internal static void Remove(string root)
    {
        root = Path.GetFullPath(root);
        using var workspaceLock = AcquireNameLock(root);
        Read(root);
        var configPath = Path.Combine(root, "unidot.json");
        RequireRealAncestors(configPath);
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configPath), Configuration.JsonOptions) ?? throw new UnidotException("Invalid workspace configuration.");
        var player = Path.GetFullPath(config.Player, root);
        if (!Within(player, root)) throw new UnidotException("Workspace configuration points to an external Player.");
        if (PlayerRuntime.IsRunning(player)) throw new UnidotException("Close this workspace Player before removing it.");
        var buildLockPath = Path.Combine(root, ".unidot", "build.lock");
        using (BuildEngine.AcquireLock(Path.GetDirectoryName(buildLockPath)!)) DeleteTree(root, buildLockPath);
        File.Delete(buildLockPath);
        Directory.Delete(Path.GetDirectoryName(buildLockPath)!);
        Directory.Delete(root);
    }

    private static void VerifyManaged(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnidotException($"Workspace Managed must remain private, not linked: {entry.FullName}");
            if (entry is DirectoryInfo) VerifyManaged(entry.FullName);
        }
    }

    internal static void DeleteTree(string path, string? preserve = null)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path); return; }
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (PathComparer.Instance.Equals(entry.FullName, preserve)) continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0) DeleteTree(entry.FullName, preserve);
            else
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0 && entry.IsReadOnly()) entry.Attributes &= ~FileAttributes.ReadOnly;
                File.Delete(entry.FullName);
            }
        }
        if (preserve is null || !Within(preserve, path)) Directory.Delete(path);
    }

    private static bool IsReadOnly(this FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReadOnly) != 0;

    internal static FileStream AcquireNameLock(string root)
    {
        RequireRealAncestors(root);
        try { return new FileStream(Path.Combine(Path.GetDirectoryName(root)!, ".unidot-workspace-" + Path.GetFileName(root) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UnidotException("Another workspace management command is using this name."); }
    }

    internal static void RequireRealAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current) || new DirectoryInfo(current).LinkTarget is not null) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnidotException($"Workspace-owned path must not traverse a link: {current}");
    }

    private static WorkspaceSourceRoot[] SourceRoutes(string root, Dictionary<string, string> mappings)
    {
        var src = Path.Combine(root, "Src");
        var routes = new List<WorkspaceSourceRoot>();
        foreach (var mapping in mappings.OrderBy(m => m.Value.Length))
        {
            var relative = Path.GetRelativePath(src, mapping.Value).Replace('\\', '/');
            if (relative == ".") relative = "";
            var parent = routes.Where(r => r.Path.Length == 0 || relative.StartsWith(r.Path + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
            if (parent is not null && PathComparer.Instance.Equals(mapping.Key, Path.GetFullPath(relative[(parent.Path.Length == 0 ? 0 : parent.Path.Length + 1)..], parent.Target))) continue;
            routes.Add(new(relative, mapping.Key));
        }
        return routes.ToArray();
    }

    private static void ValidateName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") || name.EndsWith('.') ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new UnidotException("Workspace name must be a single portable directory name (letters, numbers, dot, dash, underscore), not a path or reserved device name.");
    }

    private static string Size(long bytes) => $"{bytes / 1048576d:F2} MiB";
}
