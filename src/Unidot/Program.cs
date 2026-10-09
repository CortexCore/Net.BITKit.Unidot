using System.Text.Json;
using System.Threading.Channels;

namespace Unidot;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Task parent = Task.CompletedTask;
        try
        {
            if (Arguments.Parse(args).Value("parent-pid") is { } pid)
            {
                if (!int.TryParse(pid, out var parentId) || parentId <= 0 || parentId == Environment.ProcessId) throw new UnidotException("Invalid --parent-pid.");
                parent = ParentLifetime.WatchAsync(parentId, cancellation);
            }
            return await ExecuteAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception error) when (error is UnidotException or IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("unidot: " + error.Message);
            return 1;
        }
        finally { cancellation.Cancel(); await parent; }
    }

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var cli = Arguments.Parse(args);
        if (cli.Command is "help" or "--help" or "-h" || cli.Has("help")) { Help(); return 0; }
        if (cli.Command is "--version" or "version") { Console.WriteLine("Unidot " + ToolVersion); return 0; }
        if (cli.Command == "mcp") return await McpSession.RunAsync(cli, cancellationToken);
        if (cli.Command == "workspace") return await WorkspaceManager.ExecuteAsync(cli, cancellationToken);
        if (cli.Command == "link")
        {
            await PlayerSources.LinkAsync(cli.Value("source") ?? throw new UnidotException("link requires --source <directory>."), cli.Value("path") ?? "Src/Artists/Scripts", cancellationToken);
            return 0;
        }
        if (cli.Command == "init") return Initialize(cli);
        var config = Configuration.Load(cli.Value("config"), cli.Value("src"), cli.Value("unity-editor"));
        config.Verbose = cli.Has("verbose");
        WorkspaceManager.Validate(config);
        if (cli.Has("no-shared")) config.SharedCompilation = false;
        if (cli.Has("isolated-ilpp")) config.ReuseIlppHost = false;
        if (cli.Value("unity-editor") is { } editor) config.UnityEditor = Path.GetFullPath(editor);
        var backend = cli.Value("backend") ?? config.RunBackend;
        if (backend is not ("exe" or "native")) throw new UnidotException("--backend must be exe or native.");
        var player = PlayerLayout.Discover(config.Player, requireExecutable: cli.Command != "run" || backend == "exe", allowIl2cpp: cli.Command == "run" && cli.Has("no-build"));
        if (cli.Command is "generate" or "build" or "run" or "watch") PlayerAgentInstructions.Ensure(config, player);
        if (cli.Command == "ilpp") return IlPostProcessing.Execute(config, cli);
        if (cli.Command == "ilpp-worker") return await IlPostProcessing.WorkerAsync(config, cli, cancellationToken);
        if (cli.Command == "run" && cli.Has("no-build")) return await RunSessionsAsync(config, player, cli, null, cancellationToken);
        if (cli.Command == "run" && PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Player is already running. Close it before rebuilding and launching.");
        if (cli.Command == "restore") { Deployment.Restore(config, player, cli.Value("backup")); return 0; }
        if (cli.Command == "audit") { Deployment.Audit(config, player); return 0; }
        var toolchain = UnityToolchain.Discover(config);
        if (config.Verbose || cli.Command is "graph" or "generate") Console.WriteLine($"Unity {toolchain.Version} | {config.Platform} | {player.ManagedDirectory}");
        var graph = AssemblyGraph.Scan(config, toolchain);
        if (cli.Command is "graph" or "generate") foreach (var warning in graph.Warnings) Console.Error.WriteLine("Warning: " + warning);
        var resolver = new ReferenceResolver(config, graph, player);
        var options = new BuildOptions(!cli.Has("no-dependencies"), !cli.Has("no-deploy"), cli.Has("force"), cli.Has("keep-going"));
        var targets = SelectTargets(cli, config, graph, resolver);
        switch (cli.Command)
        {
            case "generate":
                ProjectGenerator.Generate(config, toolchain, graph, resolver);
                return 0;
            case "graph":
                var order = graph.BuildOrder(targets, options.Dependencies, n => BuildScope.Contains(config, n), config.ReferenceMode == "player");
                foreach (var node in order) Console.WriteLine($"{node.Name} ({node.Sources.Count} sources) -> {string.Join(", ", node.ResolvedReferences)}");
                Console.WriteLine($"{graph.Active.Count} active / {graph.All.Count} discovered assemblies; {order.Count} selected in dependency order.");
                return 0;
            case "build":
                var result = await new BuildEngine(config, toolchain, graph, player).BuildAsync(targets, options, cancellationToken);
                return result.Failures?.Count > 0 ? 1 : 0;
            case "run":
                var preparation = 0;
                async Task PrepareAsync(CancellationToken token)
                {
                    if (preparation++ > 0)
                    {
                        graph = AssemblyGraph.Scan(config, toolchain);
                        targets = SelectTargets(cli, config, graph, new(config, graph, player));
                    }
                    await new BuildEngine(config, toolchain, graph, player).BuildAsync(targets, new(!cli.Has("no-dependencies"), Force: cli.Has("force")), token);
                }
                return await RunSessionsAsync(config, player, cli, PrepareAsync, cancellationToken);
            case "watch":
                await WatchAsync(config, toolchain, player, cli, graph, cancellationToken);
                return 0;
            default: throw new UnidotException($"Unknown command '{cli.Command}'. Run unidot --help.");
        }
    }

    private static async Task<int> RunSessionsAsync(Configuration config, PlayerLayout player, Arguments cli, Func<CancellationToken, Task>? prepare, CancellationToken cancellationToken)
    {
        var options = new PlayerLaunchOptions(cli.Value("backend") ?? config.RunBackend, cli.Has("fallback-exe") || config.NativeFallbackToExe,
            cli.Value("native-host") is { } host ? Path.GetFullPath(host) : config.NativeHost);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prepare is not null) await prepare(cancellationToken);
            var result = await PlayerSession.RunAsync(config, player, cli.PlayerArguments, cli.Value("log-file"), cancellationToken, options);
            if (!cli.Has("keep-alive") || result == 130 || cancellationToken.IsCancellationRequested) return result;
            Console.WriteLine("Unidot session is alive. Enter 'restart' (or r) for a new Player process, 'quit' (or q) to exit. Ctrl+C exits.");
            while (true)
            {
                var command = await Console.In.ReadLineAsync(cancellationToken);
                if (command is null || command.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase) || command.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return result;
                if (command.Trim().Equals("restart", StringComparison.OrdinalIgnoreCase) || command.Trim().Equals("r", StringComparison.OrdinalIgnoreCase)) break;
                Console.WriteLine("Use restart/r or quit/q.");
            }
        }
    }

    private static int Initialize(Arguments cli)
    {
        var path = Path.GetFullPath(cli.Value("config") ?? Path.Combine(Environment.CurrentDirectory, "unidot.json"));
        if (File.Exists(path)) throw new UnidotException($"Configuration already exists: {path}. Edit it or choose another --config path.");
        if (cli.Value("project") is null)
        {
            var inferred = PlayerSources.Configure(Environment.CurrentDirectory, cli.Value("src") ?? "Src", cli.Value("unity-editor"), cli.Value("player"));
            inferred.Assemblies = cli.Values("assembly");
            inferred.Defines = cli.Values("define");
            inferred.SourceRoots = cli.Values("source-root").Select(Path.GetFullPath).ToArray();
            inferred.BuildRoots = cli.Values("build-root");
            inferred.ReferenceMode = cli.Value("reference-mode") ?? "player";
            inferred.ApiProfile = cli.Value("api-profile") ?? "unity-4.8";
            inferred.Development = cli.Has("development");
            if (cli.Values("ilpp-plugin").Length > 0) inferred.UnityIlppPlugins = cli.Values("ilpp-plugin");
            inferred.Normalize(Environment.CurrentDirectory);
            var inferredToolchain = UnityToolchain.Discover(inferred);
            var inferredGraph = AssemblyGraph.Scan(inferred, inferredToolchain);
            ProjectGenerator.Generate(inferred, inferredToolchain, inferredGraph, new(inferred, inferredGraph, PlayerLayout.Discover(inferred.Player)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Path.GetDirectoryName(path) != Environment.CurrentDirectory) inferred.SourceDirectory = inferred.SourcePath;
            inferred.Save(path);
            PlayerAgentInstructions.Ensure(inferred, PlayerLayout.Discover(inferred.Player));
            Console.WriteLine($"Initialized Player source configuration: {path}");
            return 0;
        }
        var config = new Configuration
        {
            UnityProject = cli.Value("project") ?? Environment.CurrentDirectory,
            Player = cli.Value("player") ?? throw new UnidotException("init requires --player <Player.exe or directory>."),
            UnityEditor = cli.Value("unity-editor") ?? "",
            SourceDirectory = cli.Value("src") is { } source ? Path.GetFullPath(source) : null,
            Assemblies = cli.Values("assembly"), Defines = cli.Values("define"), SourceRoots = cli.Values("source-root"),
            BuildRoots = cli.Values("build-root"), ReferenceMode = cli.Value("reference-mode") ?? "asmdef",
            ApiProfile = cli.Value("api-profile") ?? "unity-4.8", Development = cli.Has("development")
        };
        config.UnityIlppPlugins = cli.Values("ilpp-plugin");
        // CLI paths are relative to the invoking directory; saved configuration paths are absolute.
        config.UnityProject = Path.GetFullPath(config.UnityProject);
        config.Player = Path.GetFullPath(config.Player);
        if (config.UnityEditor.Length > 0) config.UnityEditor = Path.GetFullPath(config.UnityEditor);
        config.SourceRoots = config.SourceRoots.Select(Path.GetFullPath).ToArray();
        config.Normalize(Path.GetDirectoryName(path)!);
        var player = PlayerLayout.Discover(config.Player);
        var toolchain = UnityToolchain.Discover(config);
        config.Player = player.Executable;
        config.UnityEditor = Path.GetDirectoryName(toolchain.DataDirectory)!;
        var graph = AssemblyGraph.Scan(config, toolchain);
        var resolver = new ReferenceResolver(config, graph, player);
        foreach (var target in config.Assemblies) graph.BuildOrder([target], false, n => BuildScope.Contains(config, n), config.ReferenceMode == "player");
        ProjectGenerator.Generate(config, toolchain, graph, resolver);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        config.Save(path);
        PlayerAgentInstructions.Ensure(config, player);
        Console.WriteLine($"Initialized {path}");
        Console.WriteLine($"Compiler: {toolchain.Compiler}");
        return 0;
    }

    private static string[] SelectTargets(Arguments cli, Configuration config, AssemblyGraph graph, ReferenceResolver resolver)
    {
        if (cli.Positionals.Count > 0) return cli.Positionals.ToArray();
        if (config.Assemblies.Length > 0) return config.Assemblies;
        return graph.Active.Values.Where(n => n.Sources.Count > 0 && !n.CachedPackage && BuildScope.Contains(config, n) && (config.SourcePath is not null || config.BuildRoots.Length > 0 || resolver.Managed.ContainsKey(n.Name))).Select(n => n.Name).ToArray();
    }

    private static async Task WatchAsync(Configuration config, UnityToolchain toolchain, PlayerLayout player, Arguments cli, AssemblyGraph graph, CancellationToken cancellationToken)
    {
        var signals = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions { SingleReader = true });
        var watchers = new List<FileSystemWatcher>();
        void ResetWatchers(AssemblyGraph current)
        {
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
            var roots = current.Roots.Concat(current.All.Values.Where(n => !n.CachedPackage && n.DefinitionPath.Length > 0).Select(n => Path.GetDirectoryName(n.DefinitionPath)!))
                .Concat([Path.Combine(config.UnityProject, "ProjectSettings"), config.Directory]).Distinct(PathComparer.Instance).Where(Directory.Exists);
            foreach (var root in roots)
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                void Signal(string path)
                {
                    if (path.StartsWith(config.GeneratedDirectory + Path.DirectorySeparatorChar, PathComparer.Instance == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
                    var name = Path.GetFileName(path);
                    if (Directory.Exists(path) || name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".asmdef.meta", StringComparison.OrdinalIgnoreCase) ||
                        name is "csc.rsp" or "ProjectSettings.asset" or "ProjectVersion.txt" or "manifest.json" or "packages-lock.json" or "unidot.json") signals.Writer.TryWrite(0);
                }
                watcher.Changed += (_, e) => Signal(e.FullPath);
                watcher.Created += (_, e) => Signal(e.FullPath);
                watcher.Deleted += (_, e) => Signal(e.FullPath);
                watcher.Renamed += (_, e) => { Signal(e.OldFullPath); Signal(e.FullPath); };
                watcher.Error += (_, e) => { Console.Error.WriteLine("Watcher: " + e.GetException().Message); signals.Writer.TryWrite(0); };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }
        try
        {
            ResetWatchers(graph);
            signals.Writer.TryWrite(0);
            Console.WriteLine("Watching Unity sources. Ctrl+C to stop. Player replacement requires the Player to be closed.");
            while (await signals.Reader.WaitToReadAsync(cancellationToken))
            {
                await Task.Delay(400, cancellationToken);
                while (signals.Reader.TryRead(out _)) { }
                try
                {
                    config = Configuration.Load(cli.Value("config") ?? Path.Combine(config.Directory, "unidot.json"), cli.Value("src"), cli.Value("unity-editor"));
                    config.Verbose = cli.Has("verbose");
                    WorkspaceManager.Validate(config);
                    if (cli.Has("no-shared")) config.SharedCompilation = false;
                    if (cli.Has("isolated-ilpp")) config.ReuseIlppHost = false;
                    if (cli.Value("unity-editor") is { } editor) config.UnityEditor = Path.GetFullPath(editor);
                    toolchain = UnityToolchain.Discover(config);
                    player = PlayerLayout.Discover(config.Player);
                    PlayerAgentInstructions.Ensure(config, player);
                    graph = AssemblyGraph.Scan(config, toolchain);
                    ResetWatchers(graph);
                    var resolver = new ReferenceResolver(config, graph, player);
                    var targets = SelectTargets(cli, config, graph, resolver);
                    await new BuildEngine(config, toolchain, graph, player).BuildAsync(targets,
                        new(!cli.Has("no-dependencies"), !cli.Has("no-deploy"), cli.Has("force"), cli.Has("keep-going")), cancellationToken);
                }
                catch (Exception error) when (error is UnidotException or IOException or JsonException)
                { Console.Error.WriteLine("Watch build failed: " + error.Message); }
            }
        }
        finally { foreach (var watcher in watchers) watcher.Dispose(); }
    }

    private static string ToolVersion => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.3.0";
    private static void Help() => Console.WriteLine($"""
        Unidot {ToolVersion} - independent Mono Unity Player C# builds

        From a Player directory: map your sources to Src, then unidot run.
        unidot link --source <Unity Assets/Artists/Scripts> [--path Src/Artists/Scripts]
        unidot workspace create <name> --base <Player directory or exe> [--root Workspaces] [--src <source directory>]
        unidot workspace list [--root Workspaces]
        unidot workspace diff <name> [--root Workspaces]
        unidot workspace apply <name> [--dry-run] [--root Workspaces]
        unidot workspace resolve <name> <Src-relative-file> [--root Workspaces]
        unidot workspace remove <name> [--root Workspaces]
        unidot init [--src Src] [--unity-editor <installation>]
        unidot init --project <Unity project> --player <Player.exe or directory>
                    [--unity-editor <Unity.exe, Editor directory, or installation>]
                    [--assembly <name>] [--define <symbol>] [--source-root <directory>]
                    [--build-root <Unity-project-relative directory>] [--reference-mode asmdef|player]
                    [--api-profile unity-4.8|netstandard2.1] [--development]
        unidot generate
        unidot graph [assembly ...] [--no-dependencies]
        unidot build [assembly ...] [--no-dependencies] [--no-deploy] [--force] [--keep-going]
        unidot run [--src Src] [--no-build] [--force] [--log-file <path>] [-- <Player arguments ...>]
                   [--backend exe|native] [--fallback-exe] [--keep-alive]
        unidot mcp [--player <Mono Player.exe or directory>] [--unity-editor <matching installation>]
                   [--startup-timeout <1-300 seconds>] [--log-file <path>] [-- <Player arguments ...>]
        unidot watch [assembly ...] [--no-dependencies] [--no-deploy]
        unidot restore [--backup <manifest.json>]
        unidot audit
        unidot ilpp --dll <DLL> [--pdb <PDB>] --processor <compiled Unity ILPP DLL>

        Player commands accept --config <unidot.json>. workspace create can use --config for its base binding.
        Workspace management uses --root (default ./Workspaces), without Git or a current Player binding.
        Workspace creation copies Src/Managed and links resources; file-link privilege is required.
        Build commands accept --unity-editor.
        build/run/watch default to concise summaries; --verbose streams detailed compiler, ILPP and Player output.
        Full compiler/ILPP logs and Player stdout/stderr sidecars are retained under .unidot/logs.
        build/run/watch reuse Unity's compiler server and one ILPP worker per build by default.
        Use --no-shared and/or --isolated-ilpp to disable reuse; add --force to bypass cached outputs.
        Toolchain: config/--unity-editor > UNIDOT_UNITY_EDITOR > UNITY_EDITOR > Unity Hub.
        Default targets: configured assemblies, otherwise buildRoots (or local asmdefs present in Player).
        Cached registry/git packages remain binary dependencies unless explicitly targeted.
        Generated Player projects and outputs live in .unidot beside unidot.json.
        run builds, postprocesses and deploys, then follows the Player log until exit. Ctrl+C closes the Player.
        mcp starts the original Unity 2022 Mono Player EXE and injects its owned main thread; stdio is MCP-only.
        MCP tools: runtime_status, compile_code, execute_compiled, execute_code. stdin EOF closes the Player.
        <Product Name>.Unidot.sln and .run configurations live at the workspace root; Launcher is the IDE startup project.
        Workspace commands maintain only Unidot's managed section in Player AGENTS.md; help/version are read-only.
        """);
}

internal sealed class Arguments
{
    public string Command { get; private set; } = "help";
    public List<string> Positionals { get; } = [];
    public string[] PlayerArguments { get; private set; } = [];
    private readonly Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
    public bool Has(string name) => options.ContainsKey(name);
    public string? Value(string name) => options.GetValueOrDefault(name)?.LastOrDefault();
    public string[] Values(string name) => options.GetValueOrDefault(name)?.ToArray() ?? [];

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        if (args.Length == 0) return result;
        result.Command = args[0];
        if (result.Command is not ("help" or "--help" or "-h" or "--version" or "version" or "init" or "generate" or "graph" or "build" or "run" or "mcp" or "watch" or "restore" or "audit" or "ilpp" or "ilpp-worker" or "link" or "workspace"))
            throw new UnidotException($"Unknown command '{result.Command}'. Run unidot --help.");
        var flags = new HashSet<string>(["help", "no-dependencies", "no-deploy", "no-build", "force", "development", "keep-going", "fallback-exe", "keep-alive", "no-shared", "isolated-ilpp", "verbose", "dry-run"], StringComparer.Ordinal);
        var valued = new HashSet<string>(["config", "project", "player", "unity-editor", "assembly", "define", "source-root", "build-root", "reference-mode", "api-profile", "backup", "dll", "pdb", "processor", "rsp", "src", "source", "path", "ilpp-plugin", "log-file", "parent-pid", "backend", "native-host", "base", "root", "startup-timeout"], StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "-h") { result.options["help"] = ["true"]; continue; }
            if (args[i] == "--") { result.PlayerArguments = args[(i + 1)..]; break; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { result.Positionals.Add(args[i]); continue; }
            var parts = args[i][2..].Split('=', 2);
            var name = parts[0];
            if (!flags.Contains(name) && !valued.Contains(name)) throw new UnidotException($"Unknown option --{name}.");
            var value = "true";
            if (valued.Contains(name))
            {
                if (parts.Length == 2) value = parts[1];
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
                else throw new UnidotException($"--{name} requires a value.");
            }
            else if (parts.Length == 2) throw new UnidotException($"--{name} is a flag, not a value option.");
            if (!result.options.TryGetValue(name, out var list)) result.options[name] = list = [];
            list.Add(value);
        }
        if (result.PlayerArguments.Length > 0 && result.Command is not ("run" or "mcp")) throw new UnidotException("Arguments after -- are only supported by unidot run or mcp.");
        if (result.Command is "init" or "generate" or "run" or "mcp" or "restore" && result.Positionals.Count > 0)
            throw new UnidotException($"{result.Command} does not accept positional arguments.");
        var allowed = new HashSet<string>(["config", "help"], StringComparer.Ordinal);
        if (result.Command is "init" or "generate" or "graph" or "build" or "watch" or "run") allowed.UnionWith(["unity-editor", "src"]);
        if (result.Command == "init") allowed.UnionWith(["project", "player", "assembly", "define", "source-root", "build-root", "reference-mode", "api-profile", "development", "ilpp-plugin"]);
        if (result.Command is "build" or "watch") allowed.UnionWith(["no-dependencies", "no-deploy", "force", "keep-going", "no-shared", "isolated-ilpp", "verbose"]);
        if (result.Command == "graph") allowed.Add("no-dependencies");
        if (result.Command == "restore") allowed.Add("backup");
        if (result.Command == "ilpp") allowed.UnionWith(["dll", "pdb", "processor", "rsp"]);
        if (result.Command == "ilpp-worker") allowed.Add("processor");
        if (result.Command == "run") allowed.UnionWith(["no-build", "force", "no-dependencies", "log-file", "parent-pid", "backend", "native-host", "fallback-exe", "keep-alive", "no-shared", "isolated-ilpp", "verbose"]);
        if (result.Command == "mcp") allowed.UnionWith(["player", "unity-editor", "src", "startup-timeout", "log-file", "parent-pid", "no-shared", "verbose"]);
        if (result.Command == "link") allowed.UnionWith(["source", "path"]);
        if (result.Command == "workspace")
        {
            allowed.Add("root");
            if (result.Positionals.FirstOrDefault() == "create") allowed.UnionWith(["base", "src", "unity-editor"]);
            else allowed.Remove("config");
            if (result.Positionals.FirstOrDefault() == "apply") allowed.Add("dry-run");
        }
        foreach (var option in result.options.Keys)
            if (!allowed.Contains(option)) throw new UnidotException($"--{option} is not supported by {result.Command}.");
        return result;
    }
}
