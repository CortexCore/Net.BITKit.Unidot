using System.Text.Json;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Unidot;

if (args.Contains("--fail-postprocess")) return 9;
if (args.Contains("--fake-player"))
{
    var log = args[Array.IndexOf(args, "-logFile") + 1];
    var pid = args[Array.IndexOf(args, "--pid-file") + 1];
    File.WriteAllText(pid, Environment.ProcessId.ToString());
    var bytes = Encoding.UTF8.GetBytes("hello\n你好🙂\nfinal-without-newline");
    using var stream = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    await stream.WriteAsync(bytes.AsMemory(0, 10)); await stream.FlushAsync();
    await Task.Delay(250);
    await stream.WriteAsync(bytes.AsMemory(10)); await stream.FlushAsync();
    Console.WriteLine("native-stdout");
    if (args.Contains("--stay-open")) await Task.Delay(Timeout.Infinite);
    await Task.Delay(200);
    return 7;
}
if (args.Contains("--session-host"))
{
    var log = args[Array.IndexOf(args, "--log") + 1];
    var pid = args[Array.IndexOf(args, "--pid-file") + 1];
    return await PlayerSession.RunProcessAsync(SelfStart(["--fake-player", "--stay-open", "-logFile", log, "--pid-file", pid]), log, CancellationToken.None);
}

var tests = new List<(string Name, Action Test)>
{
    ("GUID/asmref ownership, nested boundaries, platform filters and dependency order", Scanner),
    ("Local packages, version defines and constraints", Packages),
    ("Cycles and unresolved references produce actionable errors", GraphErrors),
    ("Generated projects preserve sources, references, Player defines and stable IDs", Generation),
    ("Deployment verifies backups, restores DLL/PDB, and refuses changed destinations", DeployRestore),
    ("Deployment preflight leaves Player unchanged when a later target is missing", DeployPreflight),
    ("Unity toolchain environment aliases and configuration precedence", ToolchainEnvironment),
    ("Build roots keep plugins binary and Player mode separates declaration diagnostics", ScopeAndPlayerReferences),
    ("Player Src links infer Unity metadata and restrict source builds", LinkedPlayerSources),
    ("Foreground session streams UTF-8 logs and propagates normal exit", ForegroundExit),
    ("Cancellation closes only the owned Player process", ForegroundCancellation),
    ("Parent exit cancels the CLI lifetime", ParentExit),
    ("CLI rejects malformed options and Editor symbols", ArgumentsAndDefines)
};
if (OperatingSystem.IsWindows()) tests.Add(("Force-stopping a session host cleans up its owned Player job", JobLifetime));
var unityEditor = args.Length == 2 && args[0] == "--unity-editor" ? args[1] : null;
if (unityEditor is not null) tests.Add(("Unity compiler integration: dependency outputs, cache invalidation and failed postprocessing", () => CompilerIntegration(unityEditor)));
var failed = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed.");
return failed == 0 ? 0 : 1;

static void Scanner()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Assets/A/A.asmdef", """{"name":"A","references":["GUID:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"],"noEngineReferences":true}""");
    fixture.Write("Unity/Assets/A/A.cs", "class A {}");
    fixture.Write("Unity/Assets/A/Nested/Editor.asmdef", """{"name":"Nested.Editor","includePlatforms":["Editor"]}""");
    fixture.Write("Unity/Assets/A/Nested/Editor.cs", "class EditorOnly {}");
    fixture.Write("Unity/Assets/B/B.asmdef", """{"name":"B","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/B/B.asmdef.meta", "guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");
    fixture.Write("Unity/Assets/B/B.cs", "class B {}");
    fixture.Write("Unity/Assets/Shared/B.asmref", """{"reference":"GUID:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}""");
    fixture.Write("Unity/Assets/Shared/Extra.cs", "class Extra {}");
    fixture.Write("Unity/Assets/Unresolved/Lost.asmref", """{"reference":"GUID:ffffffffffffffffffffffffffffffff"}""");
    fixture.Write("Unity/Assets/Unresolved/Lost.cs", "class MustNotLeak {}");
    fixture.Write("Unity/Assets/Tests/Tests.asmdef", """{"name":"Tests","optionalUnityReferences":["TestAssemblies"]}""");
    var graph = fixture.Scan();
    Equal(1, graph.All["A"].Sources.Count);
    Equal(2, graph.All["B"].Sources.Count);
    Check(!graph.All.ContainsKey("Assembly-CSharp"), "Unresolved asmref sources leaked into the predefined assembly.");
    Check(graph.Warnings.Any(w => w.Contains("Unresolved asmref", StringComparison.Ordinal)), "Missing asmref diagnostic.");
    Check(!graph.All["Nested.Editor"].Active && !graph.All["Tests"].Active, "Editor/test assemblies leaked into Player.");
    Equal("B,A", string.Join(',', graph.BuildOrder(["A"], true).Select(n => n.Name)));
    Equal("A", string.Join(',', graph.BuildOrder(["A"], false).Select(n => n.Name)));
    Check(!graph.All["A"].Defines.Any(s => s.StartsWith("UNITY_EDITOR", StringComparison.Ordinal)), "Editor define leaked.");
}

static void Packages()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Packages/manifest.json", """{"dependencies":{"example.package":"file:../../External"}}""");
    fixture.Write("External/package.json", """{"name":"example.package","version":"2.1.0"}""");
    fixture.Write("External/Pkg.asmdef", """{"name":"Pkg","versionDefines":[{"name":"example.package","expression":"[2.0,3.0)","define":"PKG_V2"}],"defineConstraints":["PKG_V2","!UNITY_EDITOR || UNUSED"]}""");
    fixture.Write("External/Pkg.cs", "class Pkg {}");
    var graph = fixture.Scan();
    Check(graph.All["Pkg"].Active && graph.All["Pkg"].Defines.Contains("PKG_V2"), "Version define/constraint failed.");
    Equal(1, graph.All["Pkg"].Sources.Count);
    Check(VersionRules.Matches("2.0.0", "[2.0,3.0)") && !VersionRules.Matches("3.0.0", "[2.0,3.0)"), "Version bounds incorrect.");
    Check(!VersionRules.Matches("2.0.0", "(2.0,3.0]"), "Exclusive lower bound incorrect.");
}

static void GraphErrors()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Assets/A/A.asmdef", """{"name":"A","references":["B"]}""");
    fixture.Write("Unity/Assets/B/B.asmdef", """{"name":"B","references":["A"]}""");
    Throws(() => fixture.Scan().BuildOrder(["A"], true), "A -> B -> A");
    fixture.Write("Unity/Assets/B/B.asmdef", """{"name":"B","references":["GUID:ffffffffffffffffffffffffffffffff"]}""");
    Throws(() => fixture.Scan().BuildOrder(["B"], true), "Unresolved reference");
}

static void Generation()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Assets/A/A.asmdef", """{"name":"A","references":["B"],"noEngineReferences":true}""");
    fixture.Write("Unity/Assets/A/A.cs", "class A {}");
    fixture.Write("Unity/Assets/B/B.asmdef", """{"name":"B","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/B/B.cs", "class B {}");
    fixture.CopyManaged("mscorlib.dll"); fixture.CopyManaged("A.dll"); fixture.CopyManaged("B.dll"); fixture.CopyManaged("UnityEngine.CoreModule.dll");
    fixture.Write("Player/Test_Data/app.info", "Vendor\nTest Product\n");
    var graph = fixture.Scan();
    var resolver = new ReferenceResolver(fixture.Config, graph, fixture.Player);
    Equal(2, ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver));
    var path = Path.Combine(fixture.Config.GeneratedDirectory, "projects", "A", "A.csproj");
    var text = File.ReadAllText(path);
    var xml = XDocument.Load(path);
    XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
    Equal("true", xml.Descendants(ns + "NoStdLib").Single().Value);
    Check(xml.Descendants(ns + "ProjectReference").Single().Attribute("Include")!.Value.EndsWith("B.csproj"), "Missing project edge.");
    Check(!xml.Descendants(ns + "Reference").Any(e => e.Attribute("Include")!.Value.StartsWith("UnityEngine")), "noEngineReferences ignored.");
    Check(!xml.Descendants(ns + "DefineConstants").Single().Value.Contains("UNITY_EDITOR"), "Editor define in generated project.");
    Check(text.Contains(fixture.PathOf("Unity/Assets/A/A.cs")), "Source was copied or replaced instead of linked.");
    var solution = File.ReadAllText(Path.Combine(fixture.Config.GeneratedDirectory, "Test Product.sln"));
    Check(solution.IndexOf("\"Launcher\"", StringComparison.Ordinal) < solution.IndexOf("\"A\"", StringComparison.Ordinal), "Launcher is not the first startup project.");
    Equal(2, solution.Split('\n').Count(l => l.Contains(".Build.0", StringComparison.Ordinal)));
    var launcher = XDocument.Load(Path.Combine(fixture.Config.GeneratedDirectory, "Launcher", "Launcher.csproj"));
    Equal("Exe", launcher.Descendants("OutputType").Single().Value);
    Check(!launcher.Descendants("ProjectReference").Any(), "Launcher will rebuild raw game projects before unidot.");
    Check(File.Exists(Path.Combine(fixture.Config.GeneratedDirectory, ".run", "Launcher.run.xml")), "Rider launch profile missing.");
    var time = File.GetLastWriteTimeUtc(path);
    ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver);
    Equal(time, File.GetLastWriteTimeUtc(path));
}

static void DeployRestore()
{
    using var fixture = new Fixture();
    var target = fixture.CopyManaged("Unidot.Tests.dll");
    var original = File.ReadAllBytes(target);
    var source = fixture.PathOf("compiled/Unidot.Tests.dll");
    fixture.Write("compiled/.keep", "");
    File.WriteAllBytes(source, original.Concat(new byte[] { 1, 2, 3 }).ToArray());
    File.WriteAllText(Path.ChangeExtension(source, ".pdb"), "new-symbols");
    var outputs = new Dictionary<string, string> { ["Unidot.Tests"] = source };
    var manifestPath = Deployment.Deploy(fixture.Config, fixture.Player, outputs)!;
    Check(File.Exists(manifestPath), "Backup manifest missing.");
    var manifest = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(manifestPath), Configuration.JsonOptions)!;
    Check(File.ReadAllBytes(manifest.Files.First(e => e.Existed).Backup).SequenceEqual(original), "Backup is not the original DLL.");
    Equal("new-symbols", File.ReadAllText(Path.ChangeExtension(target, ".pdb")));
    File.AppendAllText(target, "external-change");
    Throws(() => Deployment.Restore(fixture.Config, fixture.Player, manifestPath), "changed since deployment");
    File.Copy(source, target, true);
    Deployment.Restore(fixture.Config, fixture.Player, manifestPath);
    Check(File.ReadAllBytes(target).SequenceEqual(original), "Original DLL not restored.");
    Check(!File.Exists(Path.ChangeExtension(target, ".pdb")), "New PDB not removed during restore.");
}

static void DeployPreflight()
{
    using var fixture = new Fixture();
    var target = fixture.CopyManaged("Unidot.Tests.dll");
    var original = File.ReadAllBytes(target);
    fixture.Write("compiled/.keep", "");
    var source = fixture.PathOf("compiled/Unidot.Tests.dll");
    File.WriteAllBytes(source, original.Concat(new byte[] { 9 }).ToArray());
    Throws(() => Deployment.Deploy(fixture.Config, fixture.Player, new Dictionary<string, string>
    {
        ["Unidot.Tests"] = source, ["NotRegistered"] = source
    }), "new assembly");
    Check(File.ReadAllBytes(target).SequenceEqual(original), "Preflight failure modified Player.");
}

static void ArgumentsAndDefines()
{
    Throws(() => Arguments.Parse(["build", "--mystery"]), "Unknown option");
    Throws(() => Arguments.Parse(["init", "--player"]), "requires a value");
    Throws(() => Arguments.Parse(["run", "--no-deploy"]), "not supported");
    Equal("My Assembly", Arguments.Parse(["build", "My Assembly", "--no-deploy"]).Positionals.Single());
    Equal("--mode=x", Arguments.Parse(["run", "--", "--mode=x"]).PlayerArguments.Single());
    Check(UnityDefines.Satisfies("!EDITOR || FEATURE", new HashSet<string>()), "OR/negation failed.");
    using var fixture = new Fixture();
    fixture.Config.Defines = ["UNITY_EDITOR_WIN"];
    Throws(() => fixture.Config.Normalize(fixture.Root), "UNITY_EDITOR");
}

static void ToolchainEnvironment()
{
    using var fixture = new Fixture();
    fixture.Write("Install/Editor/Data/DotNetSdkRoslyn/csc.dll", "");
    fixture.Write("Install/Editor/Data/NetCoreRuntime/dotnet.exe", "");
    fixture.Write("Other/Editor/Data/DotNetSdkRoslyn/csc.dll", "");
    fixture.Write("Other/Editor/Data/NetCoreRuntime/dotnet.exe", "");
    var originalPrimary = Environment.GetEnvironmentVariable("UNIDOT_UNITY_EDITOR");
    var originalAlias = Environment.GetEnvironmentVariable("UNITY_EDITOR");
    try
    {
        Environment.SetEnvironmentVariable("UNIDOT_UNITY_EDITOR", fixture.PathOf("Install"));
        Environment.SetEnvironmentVariable("UNITY_EDITOR", fixture.PathOf("Other/Editor"));
        Equal(fixture.PathOf("Install/Editor/Data"), UnityToolchain.Discover(fixture.Config).DataDirectory);
        fixture.Config.UnityEditor = fixture.PathOf("Other/Editor/Data");
        Equal(fixture.PathOf("Other/Editor/Data"), UnityToolchain.Discover(fixture.Config).DataDirectory);
        fixture.Config.UnityEditor = "";
        Environment.SetEnvironmentVariable("UNIDOT_UNITY_EDITOR", null);
        Equal(fixture.PathOf("Other/Editor/Data"), UnityToolchain.Discover(fixture.Config).DataDirectory);
    }
    finally
    {
        Environment.SetEnvironmentVariable("UNIDOT_UNITY_EDITOR", originalPrimary);
        Environment.SetEnvironmentVariable("UNITY_EDITOR", originalAlias);
    }
}

static void ScopeAndPlayerReferences()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Assets/Artists/Scripts/Game/Game.asmdef", """{"name":"Game","references":["Plugin","GUID:ffffffffffffffffffffffffffffffff","Optional.Missing"],"noEngineReferences":true}""");
    fixture.Write("Unity/Assets/Artists/Scripts/Game/Game.cs", "class Game {}");
    fixture.Write("Unity/Assets/Plugins/Plugin.asmdef", """{"name":"Plugin","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/Plugins/Plugin.cs", "class Plugin {}");
    fixture.CopyManaged("mscorlib.dll"); fixture.CopyManaged("Plugin.dll"); fixture.CopyManaged("UnityEngine.CoreModule.dll");
    fixture.CopyManaged("Unrelated.Game.dll"); fixture.CopyManaged("Auto.Plugin.dll"); fixture.CopyManaged("Explicit.Plugin.dll");
    fixture.Write("Unity/Assets/Binaries/Auto.Plugin.dll", "");
    fixture.Write("Unity/Assets/Binaries/Explicit.Plugin.dll", "");
    fixture.Write("Unity/Assets/Binaries/Explicit.Plugin.dll.meta", "PluginImporter:\n  isExplicitlyReferenced: 1\n");
    fixture.Config.BuildRoots = ["Assets/Artists/Scripts"];
    fixture.Config.ReferenceMode = "player";
    fixture.Config.Normalize(fixture.Root);
    var graph = fixture.Scan();
    var order = graph.BuildOrder(["Game"], true, n => BuildScope.Contains(fixture.Config, n), true);
    Equal("Game", string.Join(',', order.Select(n => n.Name)));
    var resolver = new ReferenceResolver(fixture.Config, graph, fixture.Player);
    var references = resolver.Resolve(graph.All["Game"]);
    Check(references.ContainsKey("Plugin"), "Plugin binary dependency missing.");
    Check(!references.ContainsKey("UnityEngine.CoreModule"), "Player mode ignored noEngineReferences.");
    Check(!references.ContainsKey("Unrelated.Game"), "Unrelated Player assembly can shadow declared types.");
    Check(references.ContainsKey("Auto.Plugin") && !references.ContainsKey("Explicit.Plugin"), "Plugin auto-reference policy ignored.");
    Check(resolver.Diagnostics(graph.All["Game"]).Length == 2, "Optional/GUID declaration diagnostics lost.");
    Equal(1, ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver));
    Check(!File.Exists(Path.Combine(fixture.Config.GeneratedDirectory, "projects", "Plugin", "Plugin.csproj")), "Plugin project escaped the build scope.");
    Throws(() => graph.BuildOrder(["Plugin"], true, n => BuildScope.Contains(fixture.Config, n), true), "outside configured buildRoots");
    fixture.Config.ReferenceMode = "asmdef";
    Throws(() => resolver.Resolve(graph.All["Game"]), "Unresolved reference");
}

static void LinkedPlayerSources()
{
    using var fixture = new Fixture();
    fixture.Write("Unity/Assets/Artists/Scripts/Game/Game.asmdef", """{"name":"Game","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/Artists/Scripts/Game/Game.cs", "public class Game {}");
    fixture.Write("Unity/Assets/Plugins/Plugin.asmdef", """{"name":"Plugin"}""");
    fixture.Write("Unity/Assets/Plugins/Plugin.cs", "public class Plugin {}");
    var destination = fixture.PathOf("Player/Src/Artists/Scripts");
    PlayerSources.LinkAsync(fixture.PathOf("Unity/Assets/Artists/Scripts"), destination, CancellationToken.None).GetAwaiter().GetResult();
    Equal(fixture.PathOf("Unity"), PlayerSources.InferUnityProject(fixture.PathOf("Player/Src")));
    fixture.Config.SourceDirectory = "Src";
    fixture.Config.ReferenceMode = "player";
    fixture.Config.Normalize(fixture.PathOf("Player"));
    var graph = fixture.Scan();
    Check(graph.All["Game"].PlayerSource && !graph.All["Plugin"].PlayerSource, "Src ownership did not survive physical path deduplication.");
    Check(BuildScope.Contains(fixture.Config, graph.All["Game"]) && !BuildScope.Contains(fixture.Config, graph.All["Plugin"]), "Plugin source escaped Src scope.");
    Equal(1, graph.All["Game"].Sources.Count);
    PlayerSources.LinkAsync(fixture.PathOf("Unity/Assets/Artists/Scripts"), destination, CancellationToken.None).GetAwaiter().GetResult();
    Throws(() => PlayerSources.LinkAsync(fixture.PathOf("Unity/Assets/Plugins"), destination, CancellationToken.None).GetAwaiter().GetResult(), "will not be overwritten");
}

static ProcessStartInfo SelfStart(string[] arguments)
{
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

static void ForegroundExit()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("session.log"); var pid = fixture.PathOf("player.pid");
    using var output = new StringWriter();
    var exit = PlayerSession.RunProcessAsync(SelfStart(["--fake-player", "-logFile", log, "--pid-file", pid]), log, CancellationToken.None, output).GetAwaiter().GetResult();
    Equal(7, exit);
    Check(output.ToString().Contains("你好🙂", StringComparison.Ordinal), "UTF-8 split at EOF was corrupted.");
    Check(output.ToString().Contains("final-without-newline", StringComparison.Ordinal) && output.ToString().Contains("native-stdout", StringComparison.Ordinal), "Final file/native stdout data was lost.");
    Check(File.ReadAllText(log).Contains("你好🙂", StringComparison.Ordinal), "Persistent log missing.");
}

static void ForegroundCancellation()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("session.log"); var pid = fixture.PathOf("player.pid");
    using var output = new StringWriter();
    using var cancellation = new CancellationTokenSource();
    var session = PlayerSession.RunProcessAsync(SelfStart(["--fake-player", "--stay-open", "-logFile", log, "--pid-file", pid]), log, cancellation.Token, output);
    WaitFor(() => File.Exists(pid), "Fake Player did not start.");
    var playerId = int.Parse(File.ReadAllText(pid));
    cancellation.Cancel();
    Equal(130, session.GetAwaiter().GetResult());
    Check(!Alive(playerId), "Player survived session cancellation.");
    Check(Alive(Environment.ProcessId), "Session cancellation killed an unrelated process.");
}

static void ParentExit()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("parent.log"); var pid = fixture.PathOf("parent.pid");
    using var parent = Process.Start(SelfStart(["--fake-player", "--stay-open", "-logFile", log, "--pid-file", pid]))!;
    using var cancellation = new CancellationTokenSource();
    try
    {
        var monitor = ParentLifetime.WatchAsync(parent.Id, cancellation);
        parent.Kill(); parent.WaitForExit();
        monitor.GetAwaiter().GetResult();
        Check(cancellation.IsCancellationRequested, "Launcher/parent exit did not cancel the session lifetime.");
    }
    finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } }
}

static void JobLifetime()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("job.log"); var pid = fixture.PathOf("player.pid");
    var start = SelfStart(["--session-host", "--log", log, "--pid-file", pid]);
    start.RedirectStandardOutput = true; start.RedirectStandardError = true;
    using var host = Process.Start(start)!;
    var playerId = 0;
    try
    {
        WaitFor(() => File.Exists(pid), "Session child did not start.");
        playerId = int.Parse(File.ReadAllText(pid));
        host.Kill(entireProcessTree: false); host.WaitForExit();
        WaitFor(() => !Alive(playerId), "IDE force-stop left the Player orphaned.");
    }
    finally
    {
        if (!host.HasExited) { host.Kill(true); host.WaitForExit(); }
        if (playerId > 0 && Alive(playerId)) { using var child = Process.GetProcessById(playerId); child.Kill(true); child.WaitForExit(); }
    }
}

static bool Alive(int id)
{
    try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; }
}

static void WaitFor(Func<bool> condition, string failure)
{
    for (var i = 0; i < 100; i++) { if (condition()) return; Thread.Sleep(50); }
    throw new Exception(failure);
}

static void CompilerIntegration(string editor)
{
    using var fixture = new Fixture();
    fixture.Config.UnityEditor = editor;
    var toolchain = UnityToolchain.Discover(fixture.Config);
    var api = Path.Combine(toolchain.DataDirectory, "UnityReferenceAssemblies", "unity-4.8-api");
    foreach (var path in Directory.EnumerateFiles(api, "*.dll")) File.Copy(path, Path.Combine(fixture.Player.ManagedDirectory, Path.GetFileName(path)), true);
    fixture.Write("Unity/Assets/B/B.asmdef", """{"name":"B","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/B/B.cs", "public static class BLib { public static int Value => 41; }");
    fixture.Write("Unity/Assets/A/A.asmdef", """{"name":"A","references":["B"],"noEngineReferences":true}""");
    fixture.Write("Unity/Assets/A/A.cs", "public static class ALib { public static int Value => BLib.Value + 1; }");
    var graph = fixture.Scan();
    var engine = new BuildEngine(fixture.Config, toolchain, graph, fixture.Player);
    var first = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(2, first.Compiled);
    Equal("A", AssemblyMetadata.Name(first.Outputs["A"]));
    var hash = new ContentHashes().Get(first.Outputs["A"]);
    var second = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(0, second.Compiled); Equal(2, second.Cached);
    fixture.Write("Unity/Assets/B/B.cs", "public static class BLib { public static int Value => 43; }");
    var third = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(2, third.Compiled);
    var beforeFailure = new ContentHashes().Get(third.Outputs["A"]);
    fixture.Config.PostProcessors["A"] = [new PostProcessor { Executable = Environment.ProcessPath!, Arguments = ["--fail-postprocess"] }];
    Throws(() => engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult(), "Postprocessing failed");
    Equal(beforeFailure, new ContentHashes().Get(third.Outputs["A"]));
    Check(!Directory.Exists(Path.Combine(fixture.Config.GeneratedDirectory, "backups")), "Raw build unexpectedly deployed files.");
    Check(hash.Length > 0, "Initial output hash missing.");

    var configPath = fixture.PathOf("unidot.json");
    fixture.Config.Save(configPath);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var watch = Unidot.Program.ExecuteAsync(["watch", "B", "--no-deploy", "--config", configPath], cancellation.Token);
    try
    {
        Task.Delay(1500).GetAwaiter().GetResult();
        var beforeChange = new ContentHashes().Get(third.Outputs["B"]);
        fixture.Write("Unity/Assets/B/B.cs", "public static class BLib { public static int Value => 45; }");
        var changed = false;
        for (var i = 0; i < 80; i++)
        {
            Task.Delay(100).GetAwaiter().GetResult();
            if (new ContentHashes().Get(third.Outputs["B"]) != beforeChange) { changed = true; break; }
        }
        Check(changed, "watch did not rebuild after a source change.");
    }
    finally
    {
        cancellation.Cancel();
        try { watch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
    }

    fixture.Write("Unity/Assets/C/C.asmdef", """{"name":"C","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/C/C.cs", "public class Independent {}");
    fixture.Write("Unity/Assets/B/B.cs", "public static class BLib { this is invalid code }");
    graph = fixture.Scan();
    var partial = new BuildEngine(fixture.Config, toolchain, graph, fixture.Player)
        .BuildAsync(["A", "C"], new(Deploy: false, KeepGoing: true), CancellationToken.None).GetAwaiter().GetResult();
    Equal(1, partial.Compiled);
    Equal(2, partial.Failures!.Count);
    Check(partial.Failures.Any(f => f.Assembly == "A" && f.Blocked), "Failed source dependency fell back to a stale DLL.");
    Check(File.Exists(Path.Combine(fixture.Config.GeneratedDirectory, "build-report.json")), "Partial build report missing.");
    Check(partial.Outputs.ContainsKey("C") && !partial.Outputs.ContainsKey("A"), "Independent compilation did not continue.");

    File.Copy(third.Outputs["B"], Path.Combine(fixture.Player.ManagedDirectory, "B.dll"), true);
    fixture.Config.Assemblies = ["B"];
    fixture.Config.Save(configPath);
    var beforeRun = new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "B.dll"));
    Throws(() => Unidot.Program.ExecuteAsync(["run", "--config", configPath], CancellationToken.None).GetAwaiter().GetResult(), "Compilation failed");
    Equal(beforeRun, new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "B.dll")));
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
static void Throws(Action action, string message)
{
    try { action(); } catch (UnidotException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
    throw new Exception("Expected error containing " + message);
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "opencode", "unidot-tests-" + Guid.NewGuid().ToString("N"));
    public Configuration Config { get; }
    public UnityToolchain Toolchain { get; }
    public PlayerLayout Player { get; }
    public Fixture()
    {
        Write("Unity/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.62f3\n");
        Write("Unity/ProjectSettings/ProjectSettings.asset", "  scriptingDefineSymbols:\n    Standalone: GAME_FEATURE\n  activeInputHandler: 2\n");
        Write("Unity/Assets/.keep", "");
        Write("Player/Test.exe", "");
        Write("Player/Test_Data/Managed/.keep", "");
        Write("Player/MonoBleedingEdge/etc/.keep", "");
        Config = new Configuration { UnityProject = PathOf("Unity"), Player = PathOf("Player/Test.exe") };
        Config.Normalize(Root);
        Toolchain = new(PathOf("Toolchain/Data"), "dotnet", PathOf("Toolchain/Data/csc.dll"), "2022.3.62f3");
        Player = new(Config.Player, PathOf("Player/Test_Data"), PathOf("Player/Test_Data/Managed"));
    }
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public void Write(string relative, string text)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    public string CopyManaged(string name)
    {
        var path = Path.Combine(Player.ManagedDirectory, name);
        File.Copy(typeof(Fixture).Assembly.Location, path, true);
        return path;
    }
    public AssemblyGraph Scan() => AssemblyGraph.Scan(Config, Toolchain);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
