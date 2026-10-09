using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unidot;

internal sealed class UnidotException(string message) : Exception(message);

internal sealed class Configuration
{
    public int Version { get; set; } = 1;
    public string UnityProject { get; set; } = "";
    public string Player { get; set; } = "";
    public string UnityEditor { get; set; } = "";
    public string? SourceDirectory { get; set; }
    public string? ProductName { get; set; }
    public string RunBackend { get; set; } = "exe";
    public string? NativeHost { get; set; }
    public bool NativeFallbackToExe { get; set; }
    public bool SharedCompilation { get; set; } = true;
    public bool ReuseIlppHost { get; set; } = true;
    public string[] WorkspaceExcludedSources { get; set; } = [];
    public string? WorkspaceManifest { get; set; }
    public string? GlobalCompilerResponse { get; set; }
    public string Platform { get; set; } = "WindowsStandalone64";
    public string ApiProfile { get; set; } = "unity-4.8";
    public string LanguageVersion { get; set; } = "9.0";
    public bool Development { get; set; }
    public string[] Assemblies { get; set; } = [];
    public string[] Defines { get; set; } = [];
    public string[] SourceRoots { get; set; } = [];
    public string[] BuildRoots { get; set; } = [];
    public string ReferenceMode { get; set; } = "asmdef";
    public string[] CompilerArguments { get; set; } = [];
    public string[] Analyzers { get; set; } = [];
    public string[] UnityIlppPlugins { get; set; } = [];
    public Dictionary<string, PostProcessor[]> PostProcessors { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public string Directory { get; private set; } = "";
    [JsonIgnore] public bool Verbose { get; set; }
    [JsonIgnore] public string? FilePath { get; private set; }
    [JsonIgnore] public string GeneratedDirectory => Path.Combine(Directory, ".unidot");
    [JsonIgnore] public string? SourcePath => SourceDirectory is null ? null : Path.GetFullPath(SourceDirectory, Directory);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Configuration Load(string? path, string? sourceDirectory = null, string? unityEditor = null)
    {
        var automatic = path is null;
        path = path is null ? FindFile(Environment.CurrentDirectory) : Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            if (!automatic) throw new UnidotException($"Configuration not found: {path}. Run unidot init first.");
            var created = PlayerSources.Configure(Environment.CurrentDirectory, sourceDirectory ?? "Src", unityEditor);
            created.Save(path);
            PlayerAgentInstructions.Ensure(created, PlayerLayout.Discover(created.Player));
            Console.WriteLine($"Bound Player sources: {created.SourcePath}. Configuration: {path}");
            return created;
        }
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), JsonOptions)
            ?? throw new UnidotException($"Invalid configuration: {path}");
        if (config.Version != 1) throw new UnidotException($"Unsupported configuration version {config.Version}.");
        if (sourceDirectory is not null) { config.SourceDirectory = Path.GetFullPath(sourceDirectory); config.BuildRoots = []; }
        config.Normalize(Path.GetDirectoryName(path)!);
        config.FilePath = path;
        return config;
    }

    public void Normalize(string directory)
    {
        Directory = Path.GetFullPath(directory);
        if (SourcePath is not null && !System.IO.Directory.Exists(SourcePath)) throw new UnidotException($"Source directory not found: {SourcePath}. Map your source tree here or pass --src <directory>.");
        if (string.IsNullOrWhiteSpace(UnityProject) && SourcePath is not null) UnityProject = PlayerSources.InferUnityProject(SourcePath);
        UnityProject = Absolute(UnityProject);
        Player = Absolute(Player);
        if (!string.IsNullOrWhiteSpace(UnityEditor)) UnityEditor = Absolute(UnityEditor);
        SourceRoots = SourceRoots.Select(Absolute).ToArray();
        WorkspaceExcludedSources = WorkspaceExcludedSources.Select(Absolute).ToArray();
        if (WorkspaceManifest is not null) WorkspaceManifest = Absolute(WorkspaceManifest);
        if (GlobalCompilerResponse is not null) GlobalCompilerResponse = Absolute(GlobalCompilerResponse);
        BuildRoots = BuildRoots.Select(p => Path.GetFullPath(p, UnityProject)).ToArray();
        if (ReferenceMode is not ("asmdef" or "player")) throw new UnidotException("referenceMode must be asmdef or player.");
        foreach (var root in BuildRoots)
            if (!System.IO.Directory.Exists(root)) throw new UnidotException($"Build root not found: {root}");
        Analyzers = Analyzers.Select(Absolute).ToArray();
        UnityIlppPlugins = UnityIlppPlugins.Select(p => Path.GetFullPath(p, UnityProject)).ToArray();
        if (RunBackend is not ("exe" or "native")) throw new UnidotException("runBackend must be exe or native.");
        if (NativeHost is not null) NativeHost = Absolute(NativeHost);
        if (Platform != "WindowsStandalone64")
            throw new UnidotException("v1 currently supports WindowsStandalone64 Mono Players only.");
        if (ApiProfile is not ("unity-4.8" or "netstandard2.1"))
            throw new UnidotException("apiProfile must be unity-4.8 or netstandard2.1.");
        if (Defines.Any(d => d.StartsWith("UNITY_EDITOR", StringComparison.Ordinal)))
            throw new UnidotException("UNITY_EDITOR defines cannot be used in a Player build.");
    }

    public void Save(string path)
    {
        FilePath = Path.GetFullPath(path);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
    }

    private string Absolute(string path) => Path.GetFullPath(path, Directory);

    private static string FindFile(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "unidot.json");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, "unidot.json");
    }
}

internal sealed class PostProcessor
{
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
}

internal sealed record PlayerLayout(string Executable, string DataDirectory, string ManagedDirectory)
{
    public string RootDirectory => Path.GetDirectoryName(DataDirectory)!;
    public string UnityPlayerLibrary => Path.Combine(RootDirectory, "UnityPlayer.dll");
    public bool HasExecutable => File.Exists(Executable);

    public static PlayerLayout Discover(string path, bool requireExecutable = true, bool allowIl2cpp = false)
    {
        string executable;
        if (System.IO.Directory.Exists(path))
        {
            var candidates = System.IO.Directory.EnumerateFiles(path, "*.exe")
                .Where(p => System.IO.Directory.Exists(Path.Combine(path, Path.GetFileNameWithoutExtension(p) + "_Data")))
                .ToArray();
            if (candidates.Length == 1) executable = candidates[0];
            else if (!requireExecutable)
            {
                var dataCandidates = System.IO.Directory.EnumerateDirectories(path, "*_Data")
                    .Where(p => UnityPlayerValidation.HasMonoLayout(path, p) ||
                        (allowIl2cpp && File.Exists(Path.Combine(path, "GameAssembly.dll")))).ToArray();
                if (dataCandidates.Length != 1) throw new UnidotException($"Expected one Unity data directory in {path}; specify its original Player .exe path to disambiguate.");
                executable = Path.Combine(path, Path.GetFileName(dataCandidates[0])[..^5] + ".exe");
            }
            else throw new UnidotException($"Expected one Unity Player in {path}; specify the .exe explicitly.");
        }
        else executable = path;
        if (requireExecutable && !File.Exists(executable)) throw new UnidotException($"Player executable not found: {executable}");
        var root = Path.GetDirectoryName(executable)!;
        var data = Path.Combine(root, Path.GetFileNameWithoutExtension(executable) + "_Data");
        var managed = Path.Combine(data, "Managed");
        if (!System.IO.Directory.Exists(data)) throw new UnidotException($"Unity data directory not found: {data}");
        var il2cpp = File.Exists(Path.Combine(root, "GameAssembly.dll"));
        if (!(allowIl2cpp && il2cpp) && !UnityPlayerValidation.HasMonoLayout(root, data))
            throw new UnidotException("Code compilation/deployment requires Mono with an intact Managed directory. IL2CPP can only use a no-build runtime backend.");
        return new(Path.GetFullPath(executable), data, managed);
    }
}
