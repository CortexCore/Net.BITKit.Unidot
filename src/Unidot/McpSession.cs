using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Unidot;

internal static class McpSession
{
    public static async Task<int> RunAsync(Arguments cli, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new UnidotException("MCP Player sessions currently require Windows x64 Mono.");
        var console = Console.Out;
        // MCP owns stdout. Compiler, bootstrap, Player and workspace messages belong on stderr.
        Console.SetOut(Console.Error);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int>? playerTask = null;
        McpClient? client = null;
        try
        {
            var startupSeconds = TimeoutSeconds(cli);
            var config = LoadConfiguration(cli);
            WorkspaceManager.Validate(config);
            var player = PlayerLayout.Discover(config.Player);
            if (PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Player is already running. MCP starts and owns a new session.");
            UnityEngineVersion engine;
            try { engine = UnityPlayerValidation.ReadVersion(player.UnityPlayerLibrary); }
            catch (Exception error) when (error is ArgumentException or BadImageFormatException or PlatformNotSupportedException)
            { throw new UnidotException(error.Message); }
            if (!engine.SupportsMcp)
                throw new UnidotException("Runtime MCP supports Unity 2022 Mono; actual Player engine: " + engine.Name);
            var toolchain = UnityToolchain.DiscoverForPlayer(config);
            UnityEngineVersion compilerVersion;
            try { compilerVersion = UnityPlayerValidation.ParseVersion(toolchain.Version); }
            catch (ArgumentException error) { throw new UnidotException(error.Message); }
            if (compilerVersion.Major != engine.Major || compilerVersion.Minor != engine.Minor)
                throw new UnidotException($"Unity compiler family {compilerVersion.Name} does not match Player engine {engine.Name}.");
            var directory = Path.Combine(config.GeneratedDirectory, "mcp", "session-" + Guid.NewGuid().ToString("N"));
            var runtime = Path.Combine(directory, "runtime");
            var log = cli.Value("log-file") is { } requested ? Path.GetFullPath(requested) : Path.Combine(directory, "Player.log");
            var compiler = new RuntimeCodeCompiler(toolchain, player, directory, runtime, !cli.Has("no-shared"));
            var bootstrap = await compiler.PrepareBootstrapAsync(lifetime.Token);
            var pipeName = "unidot-" + Guid.NewGuid().ToString("N");
            var options = new PlayerLaunchOptions("exe", false, null, new Dictionary<string, string>
            {
                ["UNIDOT_RUNTIME_BOOTSTRAP"] = bootstrap,
                ["UNIDOT_RUNTIME_PIPE"] = pipeName,
                ["UNIDOT_RUNTIME_DIRECTORY"] = directory
            }, InjectOriginal: true);
            playerTask = PlayerSession.RunAsync(config, player, cli.PlayerArguments, log, lifetime.Token, options);
            // A crashed/closed Player ends the Agent session rather than leaving an orphan server.
            _ = playerTask.ContinueWith(_ => lifetime.Cancel(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(startupSeconds));
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await ConnectAsync(pipe, directory, playerTask, startup.Token);
                client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: startup.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();
                throw new UnidotException($"Runtime MCP startup timed out or Player exited. Inspect {log} and {directory}/runtime-error.txt.");
            }
            catch { pipe.Dispose(); throw; }
            var tools = new McpTools(client, compiler, directory, log);
            var serverOptions = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "Unidot", Version = typeof(McpSession).Assembly.GetName().Version!.ToString(3) },
                ToolCollection = tools.CreateTools()
            };
            Console.Error.WriteLine("MCP ready. Player log: " + log);
            await using var server = McpServer.Create(new StdioServerTransport(serverOptions), serverOptions);
            try { await server.RunAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            return playerTask.IsCompletedSuccessfully && playerTask.Result != 130 ? playerTask.Result : 0;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (playerTask is not null)
                {
                    try { await playerTask; }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                try { if (client is not null) await client.DisposeAsync(); }
                finally { Console.SetOut(console); }
            }
        }
    }

    internal static Configuration LoadConfiguration(Arguments cli)
    {
        Configuration config;
        if (cli.Value("player") is { } target && cli.Value("config") is null)
        {
            var player = PlayerLayout.Discover(Path.GetFullPath(target), requireExecutable: false);
            config = new Configuration { Player = player.RootDirectory, UnityProject = player.RootDirectory,
                UnityEditor = cli.Value("unity-editor") ?? "" };
            config.Normalize(player.RootDirectory);
        }
        else
        {
            config = Configuration.Load(cli.Value("config"), cli.Value("src"), cli.Value("unity-editor"));
            if (cli.Value("player") is { } player) config.Player = Path.GetFullPath(player);
            if (cli.Value("unity-editor") is { } editor) config.UnityEditor = Path.GetFullPath(editor);
        }
        config.Verbose = cli.Has("verbose");
        return config;
    }

    internal static int TimeoutSeconds(Arguments cli)
    {
        var value = cli.Value("startup-timeout") ?? "60";
        if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 300)
            throw new UnidotException("--startup-timeout must be 1-300 seconds.");
        return seconds;
    }

    private static async Task ConnectAsync(NamedPipeClientStream pipe, string directory, Task<int> player, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = Path.Combine(directory, "runtime-error.txt");
            if (File.Exists(error)) throw new UnidotException("Runtime MCP bootstrap failed:\n" + await File.ReadAllTextAsync(error, cancellationToken));
            if (player.IsCompleted) { await player; throw new UnidotException("Player exited before runtime MCP became ready."); }
            try { await pipe.ConnectAsync(100, cancellationToken); return; }
            catch (TimeoutException) { }
            await Task.Delay(50, cancellationToken);
        }
    }
}

internal sealed class McpTools(McpClient client, RuntimeCodeCompiler compiler, string directory, string log)
{
    private readonly Dictionary<string, CodeCompilation> compilations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    public McpServerPrimitiveCollection<McpServerTool> CreateTools() => new()
    {
        McpServerTool.Create((Func<CancellationToken, Task<CallToolResult>>)StatusAsync,
            new() { Name = "runtime_status", Description = "Read live Unity version, scene and main-thread status for this owned Player." }),
        McpServerTool.Create((Func<string, string, string, int, CancellationToken, Task<CallToolResult>>)CompileAsync,
            new() { Name = "compile_code", Description = "Compile a complete C# type against the running Player using its matching Unity compiler. Returns diagnostics and a compilationId; does not execute." }),
        McpServerTool.Create((Func<string, int, CancellationToken, Task<CallToolResult>>)ExecuteCompiledAsync,
            new() { Name = "execute_compiled", Description = "Execute a successful compilationId in the Unity main thread. The entry point must be public static and parameterless, returning a value or Task." }),
        McpServerTool.Create((Func<string, string, string, int, CancellationToken, Task<CallToolResult>>)ExecuteCodeAsync,
            new() { Name = "execute_code", Description = "Compile and execute a complete C# type in the Unity main thread. Default entry: public static Script.Run(). Returns compiler diagnostics or execution result/exception." })
    };

    private static CallToolResult Text(object value, bool error = false) => new()
    { IsError = error, Content = [new TextContentBlock { Text = value is string text ? text : JsonSerializer.Serialize(value, Configuration.JsonOptions) }] };

    public async Task<CallToolResult> StatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync("runtime_status", cancellationToken: cancellationToken);
        result.Content.Add(new TextContentBlock { Text = JsonSerializer.Serialize(new { logFile = log, sessionDirectory = directory }) });
        return result;
    }

    public async Task<CallToolResult> CompileAsync([Description("Complete C# source, not a fragment.")] string code,
        string typeName = "Script", string methodName = "Run", int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        using var timeout = Timeout(timeoutMs, cancellationToken);
        try
        {
            await gate.WaitAsync(timeout.Token);
            try
            {
                var compilation = await compiler.CompileAsync(code, typeName, methodName, timeout.Token);
                if (compilation.Success) compilations.Add(compilation.Id, compilation);
                return Text(new { compilationId = compilation.Id, compilation.Success, compilation.TypeName, compilation.MethodName,
                    compilation.Diagnostics, compilation.SourcePath, compilation.AssemblyPath }, !compilation.Success);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Text("Compilation timed out; compiler stopped.", true); }
        catch (UnidotException error) { return Text(error.Message, true); }
    }

    public async Task<CallToolResult> ExecuteCompiledAsync(string compilationId, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        using var timeout = Timeout(timeoutMs, cancellationToken);
        try
        {
            CodeCompilation compilation;
            await gate.WaitAsync(timeout.Token);
            try
            {
                if (string.IsNullOrWhiteSpace(compilationId) || !compilations.TryGetValue(compilationId, out compilation!))
                    return Text("Unknown successful compilationId for this session.", true);
            }
            finally { gate.Release(); }
            return await client.CallToolAsync("invoke_assembly", new Dictionary<string, object?>
            {
                ["assemblyPath"] = compilation.AssemblyPath, ["typeName"] = compilation.TypeName, ["methodName"] = compilation.MethodName
            }, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Text("Execution response timed out. Already-running user code cannot be forcibly interrupted and may continue; close this MCP session to stop the Player.", true); }
    }

    public async Task<CallToolResult> ExecuteCodeAsync(string code, string typeName = "Script", string methodName = "Run",
        int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        using var timeout = Timeout(timeoutMs, cancellationToken);
        try
        {
            await gate.WaitAsync(timeout.Token);
            CodeCompilation compilation;
            try
            {
                compilation = await compiler.CompileAsync(code, typeName, methodName, timeout.Token);
                if (!compilation.Success) return Text(new { compilation.Success, compilation.Diagnostics, compilation.SourcePath }, true);
                compilations.Add(compilation.Id, compilation);
            }
            finally { gate.Release(); }
            return await ExecuteCompiledAsync(compilation.Id, timeoutMs, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Text("Compile/execute response timed out. Already-running user code may continue; close this MCP session to stop the Player.", true); }
        catch (UnidotException error) { return Text(error.Message, true); }
    }

    private static CancellationTokenSource Timeout(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds is < 1 or > 120000) throw new ModelContextProtocol.McpException("timeoutMs must be 1-120000.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(milliseconds);
        return timeout;
    }
}
