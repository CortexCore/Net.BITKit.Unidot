using System.Text.Json;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Unidot;

if (args.Contains("--fail-postprocess")) return 9;
if (args.Contains("--fake-tool"))
{
    Console.WriteLine("tool-detail");
    Console.WriteLine("Sample.cs(1,1): warning CS9999: quiet-warning");
    if (args.Contains("--tool-error")) { Console.WriteLine("Sample.cs(2,1): error CS8888: visible-error"); return 8; }
    if (args.Contains("--tool-failure")) { Console.WriteLine("cannot open assembly"); return 9; }
    return 0;
}
if (args.Contains("--fake-player"))
{
    var log = args[Array.IndexOf(args, "-logFile") + 1];
    var pid = args[Array.IndexOf(args, "--pid-file") + 1];
    File.WriteAllText(pid, Environment.ProcessId.ToString());
    var bytes = Encoding.UTF8.GetBytes("hello\n你好🙂\n" + (args.Contains("--error-log") ? "NullReferenceException: runtime-failure\n  at Fake.Run()\n\nordinary-after-error\n" : "") + "final-without-newline");
    using var stream = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    await stream.WriteAsync(bytes.AsMemory(0, 10)); await stream.FlushAsync();
    await Task.Delay(250);
    await stream.WriteAsync(bytes.AsMemory(10)); await stream.FlushAsync();
    Console.WriteLine("native-stdout🙂");
    Console.Error.Write("native-stderr-without-newline");
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
if (args.Length == 2 && args[0] == "--installed-cli-smoke")
{
    InstalledCliSmoke(Path.GetFullPath(args[1]));
    Console.WriteLine("PASS Installed CLI workspace create/conflict diff/resolve/apply/remove.");
    return 0;
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
    ("Player AGENTS uses the Player root and idempotent managed instructions", AgentCreation),
    ("AGENTS updates preserve user sections, BOM, encoding and CRLF", AgentPreservation),
    ("Malformed or unsupported AGENTS files remain unchanged", AgentMalformed),
    ("Help/version stay read-only and generation binds instructions to Player", AgentCommands),
    ("Workspaces copy source/Managed, link resources, isolate edits and remove safely", WorkspaceLifecycle),
    ("Workspace link failure/cancellation cleans up without copying resources", WorkspaceFailure),
    ("Workspace ownership, base changes and active builds prevent unsafe management", WorkspaceProtection),
    ("Workspace source boundaries and compiler response stay isolated", WorkspaceSourceBoundaries),
    ("Workspace snapshots allow active base sessions/read-shared DLLs and detect input changes", WorkspaceRunningBase),
    ("Workspace apply handles add/modify/delete, dry runs, bytes and repeated baselines", WorkspaceApplyLifecycle),
    ("Workspace conflicts emit diffs, resolve hashes expire, and matched results advance", WorkspaceApplyConflicts),
    ("Workspace add/delete conflicts and unresolved markers require explicit resolution", WorkspaceApplyFileConflicts),
    ("Workspace apply rolls back failures/cancellation and preserves concurrent edits", WorkspaceApplyRollback),
    ("Workspace origin routes, legacy manifests, links and source locks stay bounded", WorkspaceApplyBoundaries),
    ("Concise tool output retains full logs, counts warnings and surfaces failures", ToolOutput),
    ("Native fallback requires pre-entry status and forwards the correct log path", NativeFallback),
    ("Foreground session streams UTF-8 logs and propagates normal exit", ForegroundExit),
    ("Concise Player output preserves errors, stack traces and all native/file logs", ForegroundConcise),
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
    var solutionPath = Path.Combine(fixture.Root, "Test Product.Unidot.sln");
    var solution = File.ReadAllText(solutionPath);
    Check(solution.IndexOf("\"Launcher\"", StringComparison.Ordinal) < solution.IndexOf("\"A\"", StringComparison.Ordinal), "Launcher is not the first startup project.");
    Equal(2, solution.Split('\n').Count(l => l.Contains(".Build.0", StringComparison.Ordinal)));
    var launcher = XDocument.Load(Path.Combine(fixture.Config.GeneratedDirectory, "Launcher", "Launcher.csproj"));
    Equal("Exe", launcher.Descendants("OutputType").Single().Value);
    Check(!launcher.Descendants("ProjectReference").Any(), "Launcher will rebuild raw game projects before unidot.");
    Check(solution.Contains(".unidot\\Launcher\\Launcher.csproj", StringComparison.Ordinal), "Top-level solution paths are not relative to the workspace root.");
    Check(File.Exists(Path.Combine(fixture.Root, ".run", "Launcher.run.xml")), "Root Rider launch profile missing.");
    var time = File.GetLastWriteTimeUtc(path);
    var conflicting = Path.Combine(fixture.Root, "Test Product.sln");
    File.WriteAllText(conflicting, solution);
    ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver);
    Equal(time, File.GetLastWriteTimeUtc(path));
    Check(!File.Exists(conflicting), "Generated solution still occupies Unity's native build marker path.");
    Equal(solution, File.ReadAllText(Path.Combine(fixture.Config.GeneratedDirectory, "legacy-solutions", "Test Product.sln")));
    File.WriteAllText(conflicting, "User/native Visual Studio solution");
    ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver);
    Equal("User/native Visual Studio solution", File.ReadAllText(conflicting));
    File.WriteAllText(solutionPath, "User-owned solution");
    Throws(() => ProjectGenerator.Generate(fixture.Config, fixture.Toolchain, graph, resolver), "will not be overwritten");
    Equal("User-owned solution", File.ReadAllText(solutionPath));
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

static void AgentCreation()
{
    using var fixture = new Fixture();
    Check(PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "New instructions were not created.");
    var path = fixture.PathOf("Player/AGENTS.md");
    var text = File.ReadAllText(path);
    Check(!File.Exists(fixture.PathOf("AGENTS.md")), "Instructions were written to the arbitrary configuration directory.");
    Check(text.Contains("Do not scaffold an ad-hoc", StringComparison.Ordinal), "Player testing policy is missing.");
    Check(text.Contains("--config", StringComparison.Ordinal) && text.Contains(fixture.Config.Directory, StringComparison.Ordinal), "External configuration instructions have invalid commands.");
    Check(!text.Contains("{{", StringComparison.Ordinal), "Template variables were not expanded.");
    var bytes = File.ReadAllBytes(path); var modified = File.GetLastWriteTimeUtc(path);
    Check(!PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "Unchanged instructions were rewritten.");
    Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "Idempotent update changed content.");
    Equal(modified, File.GetLastWriteTimeUtc(path));
}

static void AgentPreservation()
{
    using var fixture = new Fixture();
    var path = fixture.PathOf("Player/AGENTS.md");
    var prefix = "# User rules 用户内容\r\nKeep this section.\r\n\r\n";
    var suffix = "\r\n\r\n# User appendix\r\nKeep trailing content.\r\n";
    var text = prefix + PlayerAgentInstructions.StartMarker + "\r\nOld generated rules\r\n" + PlayerAgentInstructions.EndMarker + suffix;
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
    {
        File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        Check(PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "Managed instructions were not updated.");
        var result = File.ReadAllBytes(path); var bom = encoding.GetPreamble();
        Check(result.Take(bom.Length).SequenceEqual(bom), "Original BOM changed.");
        var updated = encoding.GetString(result, bom.Length, result.Length - bom.Length);
        Check(updated.StartsWith(prefix, StringComparison.Ordinal) && updated.EndsWith(suffix, StringComparison.Ordinal), "User-owned sections changed.");
        Check(!updated.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), "File line endings changed.");
        Check(!updated.Contains("Old generated rules", StringComparison.Ordinal), "Old managed rules survived.");
    }
}

static void AgentMalformed()
{
    using var fixture = new Fixture();
    var path = fixture.PathOf("Player/AGENTS.md");
    foreach (var text in new[] { "user\n" + PlayerAgentInstructions.StartMarker, PlayerAgentInstructions.EndMarker + "\n" + PlayerAgentInstructions.StartMarker,
                 PlayerAgentInstructions.StartMarker + "\n" + PlayerAgentInstructions.StartMarker + "\n" + PlayerAgentInstructions.EndMarker })
    {
        File.WriteAllText(path, text); var original = File.ReadAllBytes(path);
        Check(!PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "Malformed managed markers were overwritten.");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Malformed file changed.");
    }
    File.WriteAllBytes(path, [0xE9, 0x20, 0x41]);
    var unsupported = File.ReadAllBytes(path);
    Check(!PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "Unsupported encoding was overwritten.");
    Check(unsupported.SequenceEqual(File.ReadAllBytes(path)), "Unsupported text bytes changed.");
    File.WriteAllText(path, "# Existing user notes\n");
    Check(PlayerAgentInstructions.Ensure(fixture.Config, fixture.Player), "Unmanaged instructions were not appended.");
    Check(File.ReadAllText(path).StartsWith("# Existing user notes\n", StringComparison.Ordinal), "Existing unmanaged content was lost.");
}

static void AgentCommands()
{
    using var fixture = new Fixture();
    var original = Environment.CurrentDirectory;
    fixture.Write("Player/Subdirectory/.keep", "");
    try
    {
        Environment.CurrentDirectory = fixture.PathOf("Player/Subdirectory");
        foreach (var arguments in new[] { new[] { "--help" }, new[] { "--version" } })
            Equal(0, Unidot.Program.ExecuteAsync(arguments, CancellationToken.None).GetAwaiter().GetResult());
        Check(!File.Exists(fixture.PathOf("Player/AGENTS.md")) && !Directory.Exists(fixture.Config.GeneratedDirectory), "Help/version created workspace files.");
        fixture.Write("Install/Editor/Data/DotNetSdkRoslyn/csc.dll", "");
        fixture.Write("Install/Editor/Data/NetCoreRuntime/dotnet.exe", "");
        fixture.Config.UnityEditor = fixture.PathOf("Install/Editor");
        fixture.CopyManaged("mscorlib.dll");
        var config = fixture.PathOf("custom.json"); fixture.Config.Save(config);
        Equal(0, Unidot.Program.ExecuteAsync(["generate", "--config", config], CancellationToken.None).GetAwaiter().GetResult());
        Check(File.Exists(fixture.PathOf("Player/AGENTS.md")), "Workspace command did not create Player instructions.");
        Check(!File.Exists(fixture.PathOf("Player/Subdirectory/AGENTS.md")), "Instructions were created in an arbitrary subdirectory.");
    }
    finally { Environment.CurrentDirectory = original; }
}

static ProcessStartInfo SelfStart(string[] arguments)
{
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

static void PrepareWorkspace(Fixture fixture)
{
    fixture.CopyManaged("mscorlib.dll");
    fixture.CopyManaged("Game.dll");
    fixture.Write("Unity/Assets/Artists/Scripts/Game.asmdef", """{"name":"Game","noEngineReferences":true}""");
    fixture.Write("Unity/Assets/Artists/Scripts/Game.asmdef.meta", "guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
    fixture.Write("Unity/Assets/Artists/Scripts/Game.cs", "public static class GameCode { public const int Value = 1; }");
    fixture.Write("Unity/Assets/Artists/Scripts/Game.cs.meta", "guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");
    fixture.Write("Unity/Assets/csc.rsp", "/define:BASE_RESPONSE\n");
    fixture.Write("Player/Test_Data/app.info", "Company\nTest Product\n");
    fixture.Write("Player/Test_Data/ScriptingAssemblies.json", """{"names":["Game.dll"]}""");
    fixture.Write("Player/Test_Data/StreamingAssets/resource.txt", "shared resource");
    fixture.Write("Player/UnityPlayer.dll", "shared runtime");
    File.WriteAllBytes(fixture.PathOf("Player/Test_Data/sharedassets0.assets"), new byte[4 * 1024 * 1024]);
    PlayerSources.LinkAsync(fixture.PathOf("Unity/Assets/Artists/Scripts"), fixture.PathOf("Player/Src/Artists/Scripts"), CancellationToken.None).GetAwaiter().GetResult();
    fixture.Config.SourceDirectory = fixture.PathOf("Player/Src");
    fixture.Config.BuildRoots = [fixture.PathOf("Unity/Assets/Artists/Scripts")];
    fixture.Config.Normalize(fixture.Root);
    fixture.Config.Save(fixture.PathOf("Player/unidot.json"));
}

static void WorkspaceLifecycle()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var container = fixture.PathOf("Workspaces");
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "create", "agent-a", "--base", fixture.PathOf("Player"), "--root", container], CancellationToken.None).GetAwaiter().GetResult());
    var root = Path.Combine(container, "agent-a");
    var manifest = WorkspaceManager.Read(root);
    Check(manifest.SharedBytes >= 4 * 1024 * 1024 && manifest.CopiedBytes < manifest.SharedBytes, "Resources consumed private workspace space.");
    var src = Path.Combine(root, "Src", "Artists", "Scripts", "Game.cs");
    Check(new DirectoryInfo(Path.GetDirectoryName(src)!).LinkTarget is null && new FileInfo(src).LinkTarget is null, "Source snapshot still points to original code.");
    var managed = Path.Combine(root, "Test_Data", "Managed", "Game.dll");
    Check(new FileInfo(managed).LinkTarget is null, "Managed DLL was linked instead of copied.");
    Check(new FileInfo(Path.Combine(root, "Test_Data", "sharedassets0.assets")).LinkTarget is not null, "Large resource was copied instead of linked.");
    Check(new DirectoryInfo(Path.Combine(root, "Test_Data", "StreamingAssets")).LinkTarget is not null, "Resource directory was copied instead of linked.");
    Check(File.Exists(src + ".meta") && File.Exists(Path.Combine(root, "Src", "Artists", "Scripts", "Game.asmdef.meta")), "Source metadata was lost.");
    Check(!File.Exists(fixture.PathOf("Player/AGENTS.md")), "Creation modified base instructions.");
    var config = Configuration.Load(Path.Combine(root, "unidot.json"));
    WorkspaceManager.Validate(config);
    var graph = AssemblyGraph.Scan(config, fixture.Toolchain);
    Equal(1, graph.All["Game"].Sources.Count);
    Equal(src, graph.All["Game"].Sources.Single());
    Check(BuildScope.Contains(config, graph.All["Game"]), "Copied buildRoots no longer select the source assembly.");
    File.WriteAllText(src, "public static class GameCode { public const int Value = 2; }");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(src)!, "New.cs"), "class NewCode {}");
    File.Delete(src + ".meta");
    File.WriteAllText(managed, "workspace private DLL");
    Check(File.ReadAllText(fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs")).Contains("Value = 1", StringComparison.Ordinal), "Workspace edit changed original source.");
    Check(new FileInfo(fixture.PathOf("Player/Test_Data/Managed/Game.dll")).Length > 100, "Workspace DLL write changed base DLL.");
    var changes = WorkspaceManager.Diff(root);
    Equal(3, changes.Count);
    Check(changes.Any(c => c.Status == "modified") && changes.Any(c => c.Status == "added") && changes.Any(c => c.Status == "deleted"), "Diff did not classify source changes.");
    Equal(1, WorkspaceManager.List(container).Count);
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "diff", "agent-a", "--root", container], CancellationToken.None).GetAwaiter().GetResult());
    fixture.Write("Unity/Assets/Artists/Scripts/Game.cs", "original source changed independently");
    WorkspaceManager.Validate(config);
    Check(AssemblyGraph.Scan(config, fixture.Toolchain).All["Game"].Sources.All(p => WorkspaceManager.Within(p, Path.Combine(root, "Src"))), "Discovery escaped to changed original source.");
    fixture.Write("Outside/keep.txt", "keep");
    PlayerSources.LinkAsync(fixture.PathOf("Outside"), Path.Combine(root, "extra-link"), CancellationToken.None).GetAwaiter().GetResult();
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "remove", "agent-a", "--root", container], CancellationToken.None).GetAwaiter().GetResult());
    Check(!Directory.Exists(root), "Workspace removal left its root behind.");
    Equal("keep", File.ReadAllText(fixture.PathOf("Outside/keep.txt")));
    Equal("shared resource", File.ReadAllText(fixture.PathOf("Player/Test_Data/StreamingAssets/resource.txt")));
    Equal(4L * 1024 * 1024, new FileInfo(fixture.PathOf("Player/Test_Data/sharedassets0.assets")).Length);
}

static void WorkspaceFailure()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    Throws(() => WorkspaceManager.CreateAsync("../escape", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "single portable");
    Throws(() => WorkspaceManager.CreateAsync("CON", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "single portable");
    Throws(() => WorkspaceManager.CreateAsync("nested", fixture.PathOf("Player"), fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "outside the base");
    Throws(() => WorkspaceManager.CreateAsync("no-links", parent, fixture.Config, CancellationToken.None,
        (_, _, _, _) => throw new UnauthorizedAccessException("test link privilege failure")).GetAwaiter().GetResult(), "large resources will not be copied");
    Check(!Directory.Exists(Path.Combine(parent, "no-links")), "Failed preflight created a populated workspace.");
    Check(!Directory.EnumerateDirectories(parent).Any(), "Failed link probe left a temporary directory.");
    async Task FailLate(string source, string destination, bool directory, CancellationToken token)
    {
        if (source.EndsWith("sharedassets0.assets", StringComparison.Ordinal)) throw new IOException("late link failure");
        if (directory) await PlayerSources.LinkAsync(source, destination, token);
        else File.CreateSymbolicLink(destination, source);
    }
    var failed = false;
    try { WorkspaceManager.CreateAsync("late", parent, fixture.Config, CancellationToken.None, FailLate).GetAwaiter().GetResult(); }
    catch (IOException error) when (error.Message == "late link failure") { failed = true; }
    Check(failed && !Directory.Exists(Path.Combine(parent, "late")), "Partial creation was not cleaned up.");
    using var cancel = new CancellationTokenSource();
    async Task CancelLate(string source, string destination, bool directory, CancellationToken token)
    {
        if (directory) await PlayerSources.LinkAsync(source, destination, token);
        else File.CreateSymbolicLink(destination, source);
        if (source.EndsWith("UnityPlayer.dll", StringComparison.Ordinal)) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
    }
    // Cancellation after real source/Managed copies and resource links must remove only those links.
    failed = false;
    try { WorkspaceManager.CreateAsync("cancelled", parent, fixture.Config, cancel.Token, CancelLate).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { failed = true; }
    Check(failed && !Directory.Exists(Path.Combine(parent, "cancelled")), "Cancelled creation left a workspace behind or ignored cancellation.");
    Check(File.Exists(fixture.PathOf("Player/Test_Data/sharedassets0.assets")), "Cleanup deleted a shared target.");
    Check(File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs")), "Cleanup deleted original source.");
}

static void WorkspaceProtection()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    var manifest = WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var root = manifest.Root;
    Throws(() => WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "already exists");
    var config = Configuration.Load(Path.Combine(root, "unidot.json"));
    using (BuildEngine.AcquireLock(Path.Combine(root, ".unidot"))) Throws(() => WorkspaceManager.Remove(root), "Another Unidot build");
    using (new FileStream(Path.Combine(root, ".unidot", "player-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Throws(() => WorkspaceManager.Remove(root), "Close this workspace Player");
    Check(File.Exists(Path.Combine(root, "unidot.json")), "Busy workspace was partially removed.");
    using (BuildEngine.AcquireLock(fixture.Config.GeneratedDirectory))
        Throws(() => WorkspaceManager.CreateAsync("base-busy", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "Another Unidot build");
    Check(!Directory.Exists(Path.Combine(parent, "base-busy")), "Busy base left a partial workspace.");
    var managedDll = Path.Combine(root, "Test_Data", "Managed", "Game.dll");
    File.Delete(managedDll);
    File.CreateSymbolicLink(managedDll, fixture.PathOf("Player/Test_Data/Managed/Game.dll"));
    Throws(() => WorkspaceManager.Validate(config), "Managed must remain private");
    File.Delete(managedDll);
    File.Copy(fixture.PathOf("Player/Test_Data/Managed/Game.dll"), managedDll);
    fixture.Write("Player/Test_Data/StreamingAssets/resource.txt", "resource changed");
    Throws(() => WorkspaceManager.Validate(config), "base resource changed");
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "list", "--root", parent], CancellationToken.None).GetAwaiter().GetResult());
    var manifestPath = Path.Combine(root, ".unidot", "workspace.json");
    var original = File.ReadAllText(manifestPath);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Root = fixture.PathOf("Outside") }, Configuration.JsonOptions));
    Throws(() => WorkspaceManager.Remove(root), "identity does not match");
    File.WriteAllText(manifestPath, original);
    // Broken shared resources must not prevent explicit cleanup of an owned workspace.
    File.Delete(fixture.PathOf("Player/UnityPlayer.dll"));
    WorkspaceManager.Remove(root);
    Check(Directory.Exists(fixture.PathOf("Player/Test_Data/StreamingAssets")), "Removal followed a broken/shared resource link.");
    fixture.Write("Workspaces/user-owned/keep.txt", "user content");
    var refused = false;
    try { WorkspaceManager.Remove(fixture.PathOf("Workspaces/user-owned")); }
    catch (IOException) { refused = true; }
    Check(refused && File.Exists(fixture.PathOf("Workspaces/user-owned/keep.txt")), "An unmanaged directory was removed.");
}

static void WorkspaceSourceBoundaries()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    fixture.Write("Unity/Assets/Extra/Extra.asmref", """{"reference":"GUID:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
    fixture.Write("Unity/Assets/Extra/Extra.cs", "class OutsideSnapshot {}");
    Throws(() => WorkspaceManager.CreateAsync("escaped", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "outside workspace Src");
    Check(!Directory.Exists(Path.Combine(parent, "escaped")), "Escaped-source failure left a workspace.");
    WorkspaceManager.DeleteTree(fixture.PathOf("Unity/Assets/Extra"));
    var manifest = WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var config = Configuration.Load(Path.Combine(manifest.Root, "unidot.json"));
    var originalResponse = File.ReadAllText(config.GlobalCompilerResponse!);
    fixture.Write("Unity/Assets/csc.rsp", "/define:CHANGED_OUTSIDE\n");
    Equal(originalResponse, File.ReadAllText(config.GlobalCompilerResponse!));
    var before = WorkspaceManager.PrivateBytes(manifest.Root);
    Directory.CreateDirectory(Path.Combine(manifest.Root, ".unidot", "backups"));
    File.WriteAllBytes(Path.Combine(manifest.Root, ".unidot", "backups", "size-probe"), new byte[1024]);
    Equal(before + 1024, WorkspaceManager.PrivateBytes(manifest.Root));
    var src = Path.Combine(manifest.Root, "Src", "Artists", "Scripts", "Game.cs");
    File.Delete(src); File.CreateSymbolicLink(src, fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs"));
    Throws(() => AssemblyGraph.Scan(config, fixture.Toolchain), "real files, not links");
    Throws(() => WorkspaceManager.Diff(manifest.Root), "real files, not links");
    WorkspaceManager.Remove(manifest.Root);
    Check(File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs")), "Removal deleted a source link's original target.");
}

static string MergeSource(string root, string file = "Game.cs") => Path.Combine(root, "Src", "Artists", "Scripts", file);

static void InstalledCliSmoke(string executable)
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces"); var root = Path.Combine(parent, "agent-a");
    int Invoke(params string[] parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return Processes.ExecuteAsync(executable, parameters, fixture.Root, timeout.Token).GetAwaiter().GetResult();
    }
    Equal(0, Invoke("workspace", "create", "agent-a", "--base", fixture.PathOf("Player"), "--root", parent));
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    File.WriteAllText(main, "main change\n"); File.WriteAllText(MergeSource(root), "agent change\n");
    Equal(1, Invoke("workspace", "apply", "agent-a", "--dry-run", "--root", parent));
    Check(File.ReadAllText(Path.Combine(root, ".unidot", "merge-conflicts.diff")).Contains("-main change", StringComparison.Ordinal), "Installed package did not load/render DiffPlex conflict output.");
    File.WriteAllText(MergeSource(root), "combined result\n");
    Equal(0, Invoke("workspace", "resolve", "agent-a", "Artists/Scripts/Game.cs", "--root", parent));
    Equal("main change\n", File.ReadAllText(main));
    Equal(0, Invoke("workspace", "apply", "agent-a", "--root", parent));
    Equal("combined result\n", File.ReadAllText(main));
    Equal(0, Invoke("workspace", "remove", "agent-a", "--root", parent));
    Check(File.Exists(fixture.PathOf("Player/Test_Data/sharedassets0.assets")), "Installed CLI removal deleted base resources.");
}

static void WorkspaceApplyLifecycle()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    fixture.Write("Unity/Assets/Artists/Scripts/MainOnly.cs", "initial main-only file");
    var parent = fixture.PathOf("Workspaces");
    var manifest = WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    var initial = File.ReadAllBytes(main);
    var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("// 中文\r\npublic class Merged {}\r\n")).ToArray();
    File.WriteAllBytes(MergeSource(manifest.Root), bytes);
    File.WriteAllText(MergeSource(manifest.Root, "Added.cs"), "class Added {}\n");
    File.WriteAllText(MergeSource(manifest.Root, "Added.cs.meta"), "guid: cccccccccccccccccccccccccccccccc\n");
    File.Delete(MergeSource(manifest.Root, "Game.cs.meta"));
    File.WriteAllText(MergeSource(manifest.Root, "ignored.dll"), "not source");
    fixture.Write("Unity/Assets/Artists/Scripts/MainOnly.cs", "main independently changed");
    var mainDll = fixture.PathOf("Player/Test_Data/Managed/Game.dll"); var dllHash = new ContentHashes().Get(mainDll);
    File.WriteAllText(Path.Combine(manifest.Root, "Test_Data", "Managed", "Game.dll"), "private compiled output");
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "apply", "agent-a", "--dry-run", "--root", parent], CancellationToken.None).GetAwaiter().GetResult());
    Check(initial.SequenceEqual(File.ReadAllBytes(main)), "Dry run wrote main source.");
    Check(!File.Exists(Path.Combine(manifest.Root, ".unidot", "workspace-apply.json")), "Dry run changed merge baseline.");
    var applied = WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None);
    Equal(4, applied.Applied); Equal(0, applied.Conflicts);
    Check(bytes.SequenceEqual(File.ReadAllBytes(main)), "Apply changed BOM, encoding or line endings.");
    Check(File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/Added.cs.meta")) && !File.Exists(main + ".meta"), "Metadata additions/deletions were not applied.");
    Equal("main independently changed", File.ReadAllText(fixture.PathOf("Unity/Assets/Artists/Scripts/MainOnly.cs")));
    Check(!File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/ignored.dll")), "Compiled binary was copied into source origin.");
    Equal(dllHash, new ContentHashes().Get(mainDll));
    Check(File.Exists(Path.Combine(applied.Backup!, "transaction.json")), "Apply backups/journal missing.");
    Equal(0, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    File.WriteAllText(MergeSource(manifest.Root), "second modification\n");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    Equal("second modification\n", File.ReadAllText(main));
    File.WriteAllText(MergeSource(manifest.Root, "Game.cs.meta"), "restored metadata");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    Equal("restored metadata", File.ReadAllText(main + ".meta"));
    // Creation diff stays independent of the advancing apply baseline.
    Check(WorkspaceManager.Diff(manifest.Root).Any(c => c.Path.EndsWith("Game.cs", StringComparison.Ordinal)), "Apply overwrote the initial source snapshot.");
    WorkspaceManager.Remove(manifest.Root);
}

static void WorkspaceApplyConflicts()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    var manifest = WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    File.WriteAllText(main, "main change\n"); File.WriteAllText(MergeSource(manifest.Root), "agent change\n");
    File.WriteAllText(MergeSource(manifest.Root, "Safe.cs"), "safe addition");
    Equal(1, Unidot.Program.ExecuteAsync(["workspace", "apply", "agent-a", "--root", parent], CancellationToken.None).GetAwaiter().GetResult());
    Equal("main change\n", File.ReadAllText(main));
    Check(!File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/Safe.cs")), "Conflicted batch partially wrote a safe file.");
    var diff = File.ReadAllText(Path.Combine(manifest.Root, ".unidot", "merge-conflicts.diff"));
    Check(diff.Contains("--- main/", StringComparison.Ordinal) && diff.Contains("+++ workspace/", StringComparison.Ordinal) && diff.Contains("-main change", StringComparison.Ordinal) && diff.Contains("+agent change", StringComparison.Ordinal), "Conflict diff omitted line-level content.");
    File.WriteAllText(MergeSource(manifest.Root), "combined result\n");
    Equal(0, Unidot.Program.ExecuteAsync(["workspace", "resolve", "agent-a", "Artists/Scripts/Game.cs", "--root", parent], CancellationToken.None).GetAwaiter().GetResult());
    Equal("main change\n", File.ReadAllText(main));
    File.WriteAllText(main, "new main edit\n");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Conflicts);
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    File.WriteAllText(MergeSource(manifest.Root), "new merged result\n");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Conflicts);
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(2, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    Equal("new merged result\n", File.ReadAllText(main));
    using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifest.Root, ".unidot", "workspace-apply.json"))))
        Equal(0, state.RootElement.GetProperty("resolutions").EnumerateObject().Count());
    File.WriteAllText(main, "externally matched\n"); File.WriteAllText(MergeSource(manifest.Root), "externally matched\n");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).AlreadyMatched);
    File.WriteAllText(MergeSource(manifest.Root), "after matched baseline\n");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    WorkspaceManager.Remove(manifest.Root);
}

static void WorkspaceApplyFileConflicts()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var manifest = WorkspaceManager.CreateAsync("agent-a", fixture.PathOf("Workspaces"), fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    File.WriteAllText(main, "modified before delete"); File.Delete(MergeSource(manifest.Root));
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Conflicts);
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    Check(!File.Exists(main), "Resolved deletion was not applied.");
    File.WriteAllText(main, "main added"); File.WriteAllText(MergeSource(manifest.Root), "workspace added");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Conflicts);
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    File.Delete(main); File.WriteAllText(MergeSource(manifest.Root), "restored from workspace");
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Conflicts);
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    File.WriteAllText(main, "conflicting main");
    File.WriteAllText(MergeSource(manifest.Root), "<<<<<<< main\r\nleft\r\n=======\r\nright\r\n>>>>>>> agent\r\n");
    Throws(() => WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None), "Conflict markers remain");
    File.WriteAllText(MergeSource(manifest.Root), "=======\r\n");
    Throws(() => WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None), "Conflict markers remain");
    // Explicit resolution may choose the previous baseline as its final result.
    File.WriteAllText(MergeSource(manifest.Root), "restored from workspace");
    WorkspaceMerge.Resolve(manifest.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None).Applied);
    Equal("restored from workspace", File.ReadAllText(main));
    Throws(() => WorkspaceMerge.Resolve(manifest.Root, "../escape.cs", CancellationToken.None), "relative to Src");
    Throws(() => Arguments.Parse(["workspace", "apply", "agent-a", "--force"]), "not supported");
    WorkspaceManager.Remove(manifest.Root);
}

static void WorkspaceApplyRollback()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var manifest = WorkspaceManager.CreateAsync("agent-a", fixture.PathOf("Workspaces"), fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    var original = File.ReadAllBytes(main);
    File.WriteAllText(MergeSource(manifest.Root), "modified main");
    File.WriteAllText(MergeSource(manifest.Root, "ZZ.cs"), "new file");
    var failed = false;
    try { WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None, index => { if (index == 1) throw new IOException("injected second-write failure"); }); }
    catch (IOException error) when (error.Message.Contains("injected", StringComparison.Ordinal)) { failed = true; }
    Check(failed && original.SequenceEqual(File.ReadAllBytes(main)), "Failed batch did not restore source bytes.");
    Check(!File.Exists(fixture.PathOf("Unity/Assets/Artists/Scripts/ZZ.cs")), "Failed batch left an addition.");
    Check(!File.Exists(Path.Combine(manifest.Root, ".unidot", "workspace-apply.json")), "Failed batch advanced merge baseline.");
    using var cancellation = new CancellationTokenSource();
    failed = false;
    try { WorkspaceMerge.Apply(manifest.Root, false, cancellation.Token, index => { if (index == 1) cancellation.Cancel(); }); }
    catch (OperationCanceledException) { failed = true; }
    Check(failed && original.SequenceEqual(File.ReadAllBytes(main)), "Cancelled batch did not roll back.");
    Throws(() => WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None, index =>
    {
        if (index == 0) File.WriteAllText(main, "concurrent preflight edit");
    }), "changed since merge preflight");
    Equal("concurrent preflight edit", File.ReadAllText(main));
    File.WriteAllBytes(main, original);
    Throws(() => WorkspaceMerge.Apply(manifest.Root, false, CancellationToken.None, index =>
    {
        if (index == 1) { File.WriteAllText(main, "concurrent edit after first write"); throw new IOException("injected failure"); }
    }), "Concurrent changes were preserved");
    Equal("concurrent edit after first write", File.ReadAllText(main));
    Check(!File.Exists(Path.Combine(manifest.Root, ".unidot", "workspace-apply.json")), "Incomplete rollback advanced the baseline.");
    WorkspaceManager.Remove(manifest.Root);
}

static void WorkspaceApplyBoundaries()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    var a = WorkspaceManager.CreateAsync("agent-a", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var b = WorkspaceManager.CreateAsync("agent-b", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    File.WriteAllText(MergeSource(a.Root), "agent a"); File.WriteAllText(MergeSource(b.Root), "agent b");
    WorkspaceMerge.Apply(a.Root, false, CancellationToken.None, _ => Throws(() => WorkspaceMerge.Apply(b.Root, false, CancellationToken.None), "same source directory"));
    Equal("agent a", File.ReadAllText(fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs")));
    var manifestPath = Path.Combine(b.Root, ".unidot", "workspace.json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(b with { SourceRoots = null }, Configuration.JsonOptions));
    WorkspaceMerge.Resolve(b.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(b.Root, false, CancellationToken.None).Applied);
    // New manifests freeze physical source roots even if the old Player-side Src link is retargeted.
    fixture.Write("OtherSource/Game.cs", "other source untouched");
    WorkspaceManager.DeleteTree(fixture.PathOf("Player/Src/Artists/Scripts"));
    PlayerSources.LinkAsync(fixture.PathOf("OtherSource"), fixture.PathOf("Player/Src/Artists/Scripts"), CancellationToken.None).GetAwaiter().GetResult();
    File.WriteAllText(MergeSource(a.Root), "frozen origin update");
    WorkspaceMerge.Resolve(a.Root, "Artists/Scripts/Game.cs", CancellationToken.None);
    Equal(1, WorkspaceMerge.Apply(a.Root, false, CancellationToken.None).Applied);
    Equal("other source untouched", File.ReadAllText(fixture.PathOf("OtherSource/Game.cs")));
    File.WriteAllText(MergeSource(b.Root), "legacy route changed");
    Throws(() => WorkspaceMerge.Apply(b.Root, false, CancellationToken.None), "Legacy Src link target changed");
    var main = fixture.PathOf("Unity/Assets/Artists/Scripts/Game.cs");
    File.Delete(main); File.CreateSymbolicLink(main, fixture.PathOf("OtherSource/Game.cs"));
    File.WriteAllText(MergeSource(a.Root), "linked target must be refused");
    Throws(() => WorkspaceMerge.Apply(a.Root, false, CancellationToken.None), "must not traverse a link");
    WorkspaceManager.Remove(a.Root); WorkspaceManager.Remove(b.Root);
}

static void NativeFallback()
{
    using var fixture = new Fixture();
    var status = fixture.PathOf("native-status.json");
    File.WriteAllText(status, """{"protocol":1,"unityEntered":false,"phase":"load","error":"missing dependency"}""");
    Check(NativePlayerBackend.CanFallback(status, 110), "Safe pre-entry failure did not allow fallback.");
    Check(!NativePlayerBackend.CanFallback(status, 0), "Successful return permitted fallback.");
    File.WriteAllText(status, """{"protocol":1,"unityEntered":true,"phase":"returned"}""");
    Check(!NativePlayerBackend.CanFallback(status, 110), "Engine-entered failure permitted a second automatic launch.");
    File.WriteAllText(status, "not json");
    Check(!NativePlayerBackend.CanFallback(status, 110), "Invalid startup status permitted fallback.");
    var fallbackLog = fixture.PathOf("session.log.fallback.log");
    var start = PlayerSession.StandardStart(fixture.Player, ["--mode=sandbox"], fallbackLog);
    Equal(fallbackLog, start.ArgumentList[1]);
    Equal("--mode=sandbox", start.ArgumentList[2]);
    Equal(fixture.Player.RootDirectory, start.WorkingDirectory);
}

static void ForegroundExit()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("session.log"); var pid = fixture.PathOf("player.pid");
    using var output = new StringWriter();
    var exit = PlayerSession.RunProcessAsync(SelfStart(["--fake-player", "-logFile", log, "--pid-file", pid]), log, CancellationToken.None, output, verbose: true).GetAwaiter().GetResult();
    Equal(7, exit);
    Check(output.ToString().Contains("你好🙂", StringComparison.Ordinal), "UTF-8 split at EOF was corrupted.");
    Check(output.ToString().Contains("final-without-newline", StringComparison.Ordinal) && output.ToString().Contains("native-stdout", StringComparison.Ordinal), "Final file/native stdout data was lost.");
    Check(File.ReadAllText(log).Contains("你好🙂", StringComparison.Ordinal), "Persistent log missing.");
    Check(File.ReadAllText(log + ".stdout.log").Contains("native-stdout🙂", StringComparison.Ordinal), "Native stdout sidecar missing or corrupted.");
    Equal("native-stderr-without-newline", File.ReadAllText(log + ".stderr.log"));
}

static void ForegroundConcise()
{
    using var fixture = new Fixture();
    var log = fixture.PathOf("concise.log"); var pid = fixture.PathOf("player.pid");
    using var output = new StringWriter();
    Equal(7, PlayerSession.RunProcessAsync(SelfStart(["--fake-player", "--error-log", "-logFile", log, "--pid-file", pid]), log, CancellationToken.None, output).GetAwaiter().GetResult());
    var text = output.ToString();
    Check(!text.Contains("你好🙂", StringComparison.Ordinal) && !text.Contains("native-stdout🙂", StringComparison.Ordinal) && !text.Contains("ordinary-after-error", StringComparison.Ordinal), "Concise session still streamed ordinary logs.");
    Check(text.Contains("runtime-failure", StringComparison.Ordinal) && text.Contains("at Fake.Run()", StringComparison.Ordinal), "Runtime error or stack trace was suppressed.");
    Check(text.Contains("native-stderr-without-newline", StringComparison.Ordinal) && text.Contains("code 7", StringComparison.Ordinal), "Native stderr or exit status was suppressed.");
    Check(File.ReadAllText(log).Contains("final-without-newline", StringComparison.Ordinal), "Full Unity log lost its final line.");
    Check(File.ReadAllText(log + ".stdout.log").Contains("native-stdout🙂", StringComparison.Ordinal), "Suppressed stdout was not retained.");
    Equal("native-stderr-without-newline", File.ReadAllText(log + ".stderr.log"));
    using var splitOutput = new StringWriter();
    using var writer = new RuntimeLogWriter(splitOutput, false);
    writer.WriteAsync("NullReferenceEx").GetAwaiter().GetResult();
    writer.WriteAsync("ception: split-error\n  at Split.Run()\nnormal").GetAwaiter().GetResult();
    writer.CompleteAsync().GetAwaiter().GetResult();
    Check(splitOutput.ToString().Contains("split-error", StringComparison.Ordinal) && splitOutput.ToString().Contains("at Split.Run()", StringComparison.Ordinal) && !splitOutput.ToString().Contains("normal", StringComparison.Ordinal), "Chunk-split error filtering lost its context.");
    using var liveError = new StringWriter();
    using var nativeError = new RuntimeLogWriter(liveError, false, stderr: true);
    nativeError.WriteAsync("error-without-newline").GetAwaiter().GetResult();
    Check(liveError.ToString().Contains("error-without-newline", StringComparison.Ordinal), "Native stderr waited for exit/newline before appearing.");
    nativeError.CompleteAsync().GetAwaiter().GetResult();
}

static void ToolOutput()
{
    using var fixture = new Fixture();
    foreach (var verbose in new[] { false, true })
    {
        var oldOut = Console.Out; var oldError = Console.Error;
        using var output = new StringWriter(); using var errors = new StringWriter();
        var directory = fixture.PathOf(verbose ? "verbose" : "concise");
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            using var diagnostics = new BuildDiagnostics(directory, verbose);
            var start = SelfStart(["--fake-tool"]);
            Equal(0, Processes.ExecuteAsync(start.FileName, start.ArgumentList, fixture.Root, CancellationToken.None, Path.Combine(directory, "compiler.log"), diagnostics.Compiler).GetAwaiter().GetResult());
            diagnostics.Ilpp("[ilpp Warning] Sample: quiet-ilpp-warning");
            diagnostics.Ilpp("[ilpp skip] Sample: Probe");
            diagnostics.Ilpp("[ilpp processed] Sample: Probe");
            Equal(1, diagnostics.CompilerWarnings); Equal(1, diagnostics.IlppWarnings); Equal(1, diagnostics.Processed);
            var text = output.ToString() + errors;
            Equal(verbose, text.Contains("tool-detail", StringComparison.Ordinal));
            Equal(verbose, text.Contains("quiet-warning", StringComparison.Ordinal));
            Equal(verbose, text.Contains("[ilpp skip]", StringComparison.Ordinal));
            Equal(verbose, text.Contains("quiet-ilpp-warning", StringComparison.Ordinal));
            Check(File.ReadAllText(Path.Combine(directory, "compiler.log")).Contains("quiet-warning", StringComparison.Ordinal), "Suppressed compiler output was not logged.");
            start = SelfStart(["--fake-tool", "--tool-error"]);
            Equal(8, Processes.ExecuteAsync(start.FileName, start.ArgumentList, fixture.Root, CancellationToken.None, Path.Combine(directory, "error.log"), diagnostics.Compiler).GetAwaiter().GetResult());
            Check(errors.ToString().Contains("visible-error", StringComparison.Ordinal), "Key compiler error was hidden.");
            start = SelfStart(["--fake-tool", "--tool-failure"]);
            Equal(9, Processes.ExecuteAsync(start.FileName, start.ArgumentList, fixture.Root, CancellationToken.None, Path.Combine(directory, "failure.log"), diagnostics.Compiler).GetAwaiter().GetResult());
            Check(errors.ToString().Contains("cannot open assembly", StringComparison.Ordinal), "Unstructured tool failure was hidden.");
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Check(File.ReadAllText(Path.Combine(directory, "build.log")).Contains("quiet-ilpp-warning", StringComparison.Ordinal), "ILPP details were not journaled.");
    }
    Check(Arguments.Parse(["build", "--verbose"]).Has("verbose") && Arguments.Parse(["run", "--verbose"]).Has("verbose") && Arguments.Parse(["watch", "--verbose"]).Has("verbose"), "Verbose option not accepted across build/run/watch.");
}

static void WorkspaceRunningBase()
{
    using var fixture = new Fixture(); PrepareWorkspace(fixture);
    var parent = fixture.PathOf("Workspaces");
    Directory.CreateDirectory(fixture.PathOf("Player/.unidot"));
    var dll = fixture.PathOf("Player/Test_Data/Managed/Game.dll");
    using (var session = new FileStream(fixture.PathOf("Player/.unidot/player-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    using (var loadedDll = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(PlayerRuntime.IsRunning(fixture.Player.Executable), "Fixture did not model an active base session.");
        var manifest = WorkspaceManager.CreateAsync("while-running", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
        Equal(new ContentHashes().Get(dll), new ContentHashes().Get(Path.Combine(manifest.Root, "Test_Data", "Managed", "Game.dll")));
        Check(PlayerRuntime.IsRunning(fixture.Player.Executable), "Snapshot interfered with the base session lock.");
        WorkspaceManager.Remove(manifest.Root);
    }
    using (var exclusiveDll = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        Throws(() => WorkspaceManager.CreateAsync("unreadable", parent, fixture.Config, CancellationToken.None).GetAwaiter().GetResult(), "Cannot copy snapshot input");
    Check(!Directory.Exists(Path.Combine(parent, "unreadable")), "Unreadable source DLL left a partial workspace.");
    async Task ChangeManaged(string source, string destination, bool directory, CancellationToken token)
    {
        if (directory) await PlayerSources.LinkAsync(source, destination, token, announce: false);
        else File.CreateSymbolicLink(destination, source);
        if (source.EndsWith("UnityPlayer.dll", StringComparison.Ordinal)) File.WriteAllText(dll, "Changed after Managed copy");
    }
    Throws(() => WorkspaceManager.CreateAsync("changed-input", parent, fixture.Config, CancellationToken.None, ChangeManaged).GetAwaiter().GetResult(), "Managed snapshot input changed");
    Check(!Directory.Exists(Path.Combine(parent, "changed-input")), "Changing Managed inputs left an inconsistent workspace.");
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
    fixture.Write("Unity/Assets/B/B.cs", "#warning integration-warning\npublic static class BLib { public static int Value => 41; }");
    fixture.Write("Unity/Assets/A/A.asmdef", """{"name":"A","references":["B"],"noEngineReferences":true}""");
    fixture.Write("Unity/Assets/A/A.cs", "public static class ALib { public static int Value => BLib.Value + 1; }");
    var graph = fixture.Scan();
    var engine = new BuildEngine(fixture.Config, toolchain, graph, fixture.Player);
    BuildResult first;
    using (var console = new StringWriter())
    {
        var old = Console.Out; Console.SetOut(console);
        try { first = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetOut(old); }
        Check(!console.ToString().Contains("integration-warning", StringComparison.Ordinal) && !console.ToString().Contains("[compile]", StringComparison.Ordinal), "Default compiler output was not concise.");
        Check(console.ToString().Contains("Build succeeded", StringComparison.Ordinal) && console.ToString().Contains("Build logs:", StringComparison.Ordinal), "Concise build omitted result or log path.");
    }
    using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Config.GeneratedDirectory, "build-report.json"))))
    {
        Equal(1, report.RootElement.GetProperty("compilerWarnings").GetInt32());
        Check(File.ReadAllText(Path.Combine(report.RootElement.GetProperty("logDirectory").GetString()!, "B.log")).Contains("integration-warning", StringComparison.Ordinal), "Real compiler warning was lost from the log.");
    }
    Equal(2, first.Compiled);
    Equal("A", AssemblyMetadata.Name(first.Outputs["A"]));
    var hash = new ContentHashes().Get(first.Outputs["A"]);
    var pdbHash = new ContentHashes().Get(Path.ChangeExtension(first.Outputs["A"], ".pdb"));
    var second = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(0, second.Compiled); Equal(2, second.Cached);
    fixture.Config.Verbose = true;
    BuildResult forced;
    using (var console = new StringWriter())
    {
        var old = Console.Out; Console.SetOut(console);
        try { forced = engine.BuildAsync(["A"], new(Deploy: false, Force: true), CancellationToken.None).GetAwaiter().GetResult(); }
        finally { Console.SetOut(old); }
        Check(console.ToString().Contains("integration-warning", StringComparison.Ordinal) && console.ToString().Contains("[compile]", StringComparison.Ordinal), "Verbose build omitted real compiler details.");
    }
    fixture.Config.Verbose = false;
    Equal(2, forced.Compiled);
    Equal(hash, new ContentHashes().Get(forced.Outputs["A"]));
    Equal(pdbHash, new ContentHashes().Get(Path.ChangeExtension(forced.Outputs["A"], ".pdb")));
    fixture.Config.SharedCompilation = false;
    var isolated = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(2, isolated.Compiled);
    // Compiler hosts may use different runtime patch versions, recorded in the PDB. Verify determinism within each mode.
    var isolatedHash = new ContentHashes().Get(isolated.Outputs["A"]);
    var isolatedPdbHash = new ContentHashes().Get(Path.ChangeExtension(isolated.Outputs["A"], ".pdb"));
    engine.BuildAsync(["A"], new(Deploy: false, Force: true), CancellationToken.None).GetAwaiter().GetResult();
    Equal(isolatedHash, new ContentHashes().Get(isolated.Outputs["A"]));
    Equal(isolatedPdbHash, new ContentHashes().Get(Path.ChangeExtension(isolated.Outputs["A"], ".pdb")));
    fixture.Config.SharedCompilation = true;
    fixture.Write("Unity/Assets/B/B.cs", "public static class BLib { public static int Value => 43; }");
    var third = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(2, third.Compiled);
    var beforeFailure = new ContentHashes().Get(third.Outputs["A"]);
    File.Copy(third.Outputs["A"], Path.Combine(fixture.Player.ManagedDirectory, "A.dll"));
    File.Copy(third.Outputs["B"], Path.Combine(fixture.Player.ManagedDirectory, "B.dll"));
    fixture.Write("Player/Test_Data/ScriptingAssemblies.json", """{"names":["A.dll","B.dll"]}""");
    fixture.Config.SourceDirectory = fixture.PathOf("Unity/Assets");
    var workspace = WorkspaceManager.CreateAsync("compiler-agent", fixture.PathOf("Workspaces"), fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    fixture.Config.SourceDirectory = null;
    var workspaceConfig = Configuration.Load(Path.Combine(workspace.Root, "unidot.json"));
    WorkspaceManager.Validate(workspaceConfig);
    File.WriteAllText(Path.Combine(workspace.Root, "Src", "A", "A.cs"), "public static class ALib { public static int Value => BLib.Value + 3; }");
    var workspaceGraph = AssemblyGraph.Scan(workspaceConfig, toolchain);
    var workspaceBuild = new BuildEngine(workspaceConfig, toolchain, workspaceGraph, PlayerLayout.Discover(workspaceConfig.Player))
        .BuildAsync(["A"], new(), CancellationToken.None).GetAwaiter().GetResult();
    Equal(2, workspaceBuild.Compiled);
    Equal(beforeFailure, new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "A.dll")));
    Check(new ContentHashes().Get(Path.Combine(workspace.Root, "Test_Data", "Managed", "A.dll")) != beforeFailure, "Workspace deployment did not replace its private DLL.");
    Check(File.ReadAllText(fixture.PathOf("Unity/Assets/A/A.cs")).Contains("+ 1", StringComparison.Ordinal), "Workspace compilation edited original source.");
    Equal(1, WorkspaceMerge.Apply(workspace.Root, false, CancellationToken.None).Applied);
    Check(File.ReadAllText(fixture.PathOf("Unity/Assets/A/A.cs")).Contains("+ 3", StringComparison.Ordinal), "Apply did not return the validated workspace source.");
    var returned = engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult();
    Equal(1, returned.Compiled);
    beforeFailure = new ContentHashes().Get(returned.Outputs["A"]);
    WorkspaceManager.Remove(workspace.Root);
    fixture.Config.PostProcessors["A"] = [new PostProcessor { Executable = Environment.ProcessPath!, Arguments = ["--fail-postprocess"] }];
    Throws(() => engine.BuildAsync(["A"], new(Deploy: false), CancellationToken.None).GetAwaiter().GetResult(), "Postprocessing failed");
    using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Config.GeneratedDirectory, "build-report.json"))))
        Equal("failed", report.RootElement.GetProperty("status").GetString());
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

    var plugin = fixture.PathOf("ProbeIlpp.dll");
    var pluginSource = fixture.PathOf("ProbeIlpp.cs");
    File.WriteAllText(pluginSource, """
        using System.Collections.Generic;
        using Unity.CompilationPipeline.Common.Diagnostics;
        using Unity.CompilationPipeline.Common.ILPostProcessing;
        public sealed class ProbeProcessor : ILPostProcessor {
            private static int calls;
            private static System.IO.FileStream retainedReference;
            private int instanceCalls;
            public override ILPostProcessor GetInstance() => this;
            public override bool WillProcess(ICompiledAssembly assembly) => true;
            public override ILPostProcessResult Process(ICompiledAssembly assembly) {
                foreach (var define in assembly.Defines)
                    if (define == "UNIDOT_TEST_CANCEL") System.Threading.Thread.Sleep(30000);
                    else if (define == "UNIDOT_TEST_HOLD_REFERENCE") {
                        retainedReference = new System.IO.FileStream(@"PLAYER_REFERENCE_PATH", System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                        System.Console.WriteLine("Held-Player-reference; PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                    }
                calls++; instanceCalls++;
                return new ILPostProcessResult(null, new List<DiagnosticMessage> {
                    new DiagnosticMessage { DiagnosticType = DiagnosticType.Warning, MessageData = "calls=" + calls + "; instance=" + instanceCalls }
                });
            }
        }
        """.Replace("PLAYER_REFERENCE_PATH", fixture.PathOf("Player/Test_Data/Managed/C.dll").Replace("\"", "\"\"", StringComparison.Ordinal), StringComparison.Ordinal));
    var compile = new List<string> { "exec", toolchain.Compiler, "/noconfig", "/target:library", "/nostdlib+", "/nologo", "/out:" + plugin,
        "/reference:" + Path.Combine(toolchain.DataDirectory, "Tools", "ilpp", "Unity.CompilationPipeline.Common", "Unity.CompilationPipeline.Common.dll"), pluginSource };
    compile.AddRange(Directory.EnumerateFiles(api, "*.dll").Select(p => "/reference:" + p));
    compile.Add("/reference:" + Path.Combine(api, "Facades", "netstandard.dll"));
    Equal(0, Processes.ExecuteAsync(toolchain.Dotnet, compile, fixture.Root, CancellationToken.None).GetAwaiter().GetResult());
    fixture.Config.UnityIlppPlugins = [plugin]; fixture.Config.Save(configPath);
    var client = IlppClient.StartAsync(fixture.Config, CancellationToken.None).GetAwaiter().GetResult();
    var workerPid = client.ProcessId;
    try
    {
        var logA = fixture.PathOf("worker-a.log");
        var rspA = Path.Combine(fixture.Config.GeneratedDirectory, "rsp", "A.rsp");
        Equal(0, client.ProcessAsync(first.Outputs["A"], Path.ChangeExtension(first.Outputs["A"], ".pdb"), rspA, logA, CancellationToken.None).GetAwaiter().GetResult());
        Check(File.ReadAllText(logA).Contains("calls=1; instance=1", StringComparison.Ordinal), "First worker request did not execute the processor.");
        var broken = fixture.PathOf("Broken.dll"); File.WriteAllText(broken, "not a DLL");
        Equal(1, client.ProcessAsync(broken, Path.ChangeExtension(first.Outputs["A"], ".pdb"), rspA, fixture.PathOf("worker-error.log"), CancellationToken.None).GetAwaiter().GetResult());
        Equal(0, client.ProcessAsync(first.Outputs["A"], Path.ChangeExtension(first.Outputs["A"], ".pdb"), rspA, logA, CancellationToken.None).GetAwaiter().GetResult());
        Check(File.ReadAllText(logA).Contains("calls=2; instance=1", StringComparison.Ordinal), "Worker did not retain its assembly while creating a fresh processor instance.");
        var cancelRsp = fixture.PathOf("cancel.rsp");
        File.WriteAllLines(cancelRsp, File.ReadAllLines(rspA).Select(line => line.StartsWith("/define:", StringComparison.Ordinal) ? line + ";UNIDOT_TEST_CANCEL" : line));
        using var cancelWorker = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var cancelled = false;
        try { client.ProcessAsync(first.Outputs["A"], Path.ChangeExtension(first.Outputs["A"], ".pdb"), cancelRsp, fixture.PathOf("worker-cancel.log"), cancelWorker.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "A running ILPP request ignored cancellation.");
    }
    finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    Check(!Alive(workerPid), "ILPP worker survived build disposal.");
    // A real processor can retain Player reference streams in static Cecil caches until its host exits.
    File.Copy(partial.Outputs["C"], Path.Combine(fixture.Player.ManagedDirectory, "C.dll"), true);
    var oldC = new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "C.dll"));
    fixture.Write("Unity/Assets/C/C.cs", "public class Independent { public const int Changed = 1; }");
    var originalDefines = fixture.Config.Defines;
    fixture.Config.Defines = originalDefines.Concat(["UNIDOT_TEST_HOLD_REFERENCE"]).ToArray();
    fixture.Config.Save(configPath);
    var retainedBuild = new BuildEngine(fixture.Config, toolchain, fixture.Scan(), fixture.Player)
        .BuildAsync(["C"], new(Force: true), CancellationToken.None).GetAwaiter().GetResult();
    Equal(1, retainedBuild.Compiled);
    Check(new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "C.dll")) != oldC, "Retained ILPP reference blocked private deployment.");
    using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Config.GeneratedDirectory, "build-report.json"))))
    {
        var logDirectory = report.RootElement.GetProperty("logDirectory").GetString()!;
        var log = File.ReadAllText(Path.Combine(logDirectory, "C.ilpp.log"));
        var match = System.Text.RegularExpressions.Regex.Match(log, @"Held-Player-reference; PID=(\d+)");
        Check(match.Success && !Alive(int.Parse(match.Groups[1].Value)), "Retaining processor host was not closed before deployment completed.");
        Check(File.ReadAllText(Path.Combine(logDirectory, "build.log")).Contains("released before deployment", StringComparison.Ordinal), "Worker release stage was not recorded.");
    }
    fixture.Config.Defines = originalDefines; fixture.Config.Save(configPath);
    fixture.Write("Unity/Assets/C/C.cs", "public class Independent { public const int Changed = 2; }");
    var beforeBlockedDeployment = new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "C.dll"));
    using (new FileStream(Path.Combine(fixture.Player.ManagedDirectory, "C.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
    using (var console = new StringWriter())
    using (var errors = new StringWriter())
    {
        var oldOutput = Console.Out; var oldErrors = Console.Error; var blocked = false;
        Console.SetOut(console); Console.SetError(errors);
        try { new BuildEngine(fixture.Config, toolchain, fixture.Scan(), fixture.Player).BuildAsync(["C"], new(Force: true), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (IOException) { blocked = true; }
        finally { Console.SetOut(oldOutput); Console.SetError(oldErrors); }
        Check(blocked && console.ToString().Contains("Compilation succeeded", StringComparison.Ordinal) && errors.ToString().Contains("Deployment failed", StringComparison.Ordinal), "External lock was not reported as a deployment-only failure.");
    }
    Equal(beforeBlockedDeployment, new ContentHashes().Get(Path.Combine(fixture.Player.ManagedDirectory, "C.dll")));
    fixture.Config.UnityIlppPlugins = []; fixture.Config.Save(configPath);

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
